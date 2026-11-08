using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Backend
{
    internal sealed class BrokerFileResult
    {
        public string Code = "OK";
        public byte[] Bytes = new byte[0];
        public bool CleanupComplete = true;
    }
    internal interface IBrokerFileReader
    {
        Task<BrokerFileResult> ReadAsync(DeviceSession session, string file, CancellationToken cancellation);
        Task<bool> StopAsync();
    }
    internal sealed class DeviceBrokerAddress
    {
        public const string Contract = "thor.device-broker/1";
        public readonly string InstanceId = Guid.NewGuid().ToString("D");
        public readonly int Port;
        internal readonly string Token;
        internal DeviceBrokerAddress(int port)
        {
            Port = port; byte[] bytes = new byte[32]; using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            Token = Convert.ToBase64String(bytes);
        }
    }
    // A request executes one fixed read under a session lease and shared transport mutex.
    // No lease is handed to Node; disconnect cancels the broker's owned command instead.
    internal sealed class DeviceCommandBroker : IDisposable
    {
        private readonly object sync = new object();
        private readonly DeviceSessionRegistry registry;
        private readonly OperationGate operations;
        private readonly IBrokerFileReader reader;
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private readonly Dictionary<TcpClient, Task> jobs = new Dictionary<TcpClient, Task>();
        private readonly List<OperationLease> retained = new List<OperationLease>();
        private readonly HashSet<string> blocked = new HashSet<string>();
        private readonly Task accepting;
        private bool closed;
        public readonly DeviceBrokerAddress Address;
        public DeviceCommandBroker(DeviceSessionRegistry sessions, OperationGate gate, IBrokerFileReader ownedReader)
        {
            if (sessions == null || gate == null || ownedReader == null) throw new ArgumentNullException("broker dependency");
            registry = sessions; operations = gate; reader = ownedReader;
            listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(16);
            Address = new DeviceBrokerAddress(((IPEndPoint)listener.LocalEndpoint).Port);
            accepting = AcceptLoop();
        }
        public int ActiveCount { get { lock (sync) return jobs.Count; } }
        private async Task AcceptLoop()
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { if (stopping.IsCancellationRequested) break; throw; }
                lock (sync)
                {
                    if (closed || jobs.Count >= 64) { client.Close(); continue; }
                    Task job = Task.Run(() => Serve(client)); jobs[client] = job;
                    Task cleanup = job.ContinueWith(done => { lock (sync) jobs.Remove(client); client.Close(); }, TaskContinuationOptions.ExecuteSynchronously);
                    GC.KeepAlive(cleanup);
                }
            }
        }
        private static bool EqualSecret(string expected, string actual)
        {
            byte[] a = Encoding.UTF8.GetBytes(expected), b = Encoding.UTF8.GetBytes(actual ?? "");
            int mismatch = a.Length ^ b.Length; for (int i = 0; i < a.Length; i++) mismatch |= a[i] ^ (i < b.Length ? b[i] : 0); return mismatch == 0;
        }
        private void Serve(TcpClient client)
        {
            OperationLease lease = null;
            using (CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
            {
                cancelled.CancelAfter(30000); string requestId = Guid.NewGuid().ToString("D"), code = "BROKER_REQUEST_INVALID";
                byte[] bytes = new byte[0]; DeviceSession session = null;
                try
                {
                    client.ReceiveTimeout = client.SendTimeout = 5000; NetworkStream stream = client.GetStream();
                    byte[] input = new byte[4096]; int length = 0;
                    for (;;) { int value = stream.ReadByte(); if (value < 0) throw new IOException(); if (value == 10) break; if (length == input.Length) throw new InvalidDataException(); input[length++] = (byte)value; }
                    object message = Json.Parse(new UTF8Encoding(false, true).GetString(input, 0, length));
                    if (Json.Text(message, "contract") != DeviceBrokerAddress.Contract || Json.Text(message, "instanceId") != Address.InstanceId
                        || !EqualSecret(Address.Token, Json.Text(message, "token"))) code = "BROKER_AUTH_REQUIRED";
                    else
                    {
                        string serial = Json.Text(message, "serial"), file = Json.Text(message, "file"); Guid sessionId, parsedId;
                        if (!Guid.TryParse(Json.Text(message, "requestId"), out parsedId) || !Guid.TryParse(Json.Text(message, "sessionId"), out sessionId)
                            || (file != "catalog" && file != "catalog-wal")) throw new InvalidDataException();
                        requestId = parsedId.ToString("D"); session = registry.Find(serial, sessionId);
                        if (session == null) code = "DEVICE_DISCONNECTED";
                        else
                        {
                            // Peer closure is cancellation, never permission to release a running child early.
                            Task monitor = Task.Run(async delegate {
                                try { while (!cancelled.IsCancellationRequested) {
                                    if (client.Client.Poll(0, SelectMode.SelectRead) && client.Available == 0) { cancelled.Cancel(); break; }
                                    await Task.Delay(25).ConfigureAwait(false);
                                } } catch (Exception) { try { cancelled.Cancel(); } catch (ObjectDisposedException) { } }
                            });
                            LeaseOutcome outcome;
                            do { outcome = operations.TryAcquire(session, OperationKind.CocoonRead, out lease); if (outcome == LeaseOutcome.Busy) cancelled.Token.WaitHandle.WaitOne(25); cancelled.Token.ThrowIfCancellationRequested(); }
                            while (outcome == LeaseOutcome.Busy);
                            if (outcome != LeaseOutcome.Acquired) code = outcome == LeaseOutcome.Unsupported ? "DEVICE_READ_UNSUPPORTED" : "DEVICE_DISCONNECTED";
                            else
                            {
                                lock (sync) if (blocked.Contains(serial)) throw new InvalidOperationException();
                                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancelled.Token, lease.Token))
                                {
                                    BrokerFileResult result = DeviceTransportGate.Execute(serial, TimeSpan.FromSeconds(10), linked.Token,
                                        () => { BrokerFileResult value = reader.ReadAsync(session, file, linked.Token).GetAwaiter().GetResult(); if (!value.CleanupComplete) DeviceTransportGate.Block(serial); return value; });
                                    code = result.Code; bytes = result.Bytes ?? new byte[0];
                                    if (!result.CleanupComplete) { lock (sync) { retained.Add(lease); blocked.Add(serial); } lease = null; code = "DEVICE_CLEANUP_REQUIRED"; bytes = new byte[0]; }
                                    else if (!registry.IsCurrent(session)) { code = "DEVICE_DISCONNECTED"; bytes = new byte[0]; }
                                    if (bytes.Length > 64 * 1024 * 1024) { code = "DEVICE_FILE_TOO_LARGE"; bytes = new byte[0]; }
                                }
                            }
                            cancelled.Cancel(); monitor.GetAwaiter().GetResult();
                        }
                    }
                }
                catch (OperationCanceledException) { code = "DEVICE_CANCELLED"; bytes = new byte[0]; }
                catch (TimeoutException) { code = "DEVICE_QUEUE_TIMEOUT"; bytes = new byte[0]; }
                catch (InvalidOperationException) { code = "DEVICE_CLEANUP_REQUIRED"; bytes = new byte[0]; }
                catch (Exception) { if (code == "OK") code = "DEVICE_READ_FAILED"; bytes = new byte[0]; }
                finally { if (lease != null) lease.Dispose(); cancelled.Cancel(); }
                try
                {
                    string digest; using (SHA256 hash = SHA256.Create()) digest = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                    string header = "{\"contract\":\"" + DeviceBrokerAddress.Contract + "\",\"instanceId\":" + Json.Quote(Address.InstanceId)
                        + ",\"requestId\":" + Json.Quote(requestId) + ",\"code\":" + Json.Quote(code) + ",\"bytes\":" + bytes.Length + ",\"sha256\":" + Json.Quote(digest) + "}";
                    byte[] encoded = Encoding.UTF8.GetBytes(header), size = { (byte)(encoded.Length >> 24), (byte)(encoded.Length >> 16), (byte)(encoded.Length >> 8), (byte)encoded.Length };
                    NetworkStream output = client.GetStream(); output.Write(size, 0, size.Length); output.Write(encoded, 0, encoded.Length); output.Write(bytes, 0, bytes.Length); output.Flush();
                }
                catch (Exception) { }
            }
        }
        public async Task<bool> StopAsync()
        {
            Task[] active;
            lock (sync) { closed = true; active = new List<Task>(jobs.Values).ToArray(); }
            stopping.Cancel(); listener.Stop();
            await accepting.ConfigureAwait(false);
            bool cleaned = await reader.StopAsync().ConfigureAwait(false);
            await Task.WhenAll(active).ConfigureAwait(false);
            if (cleaned) { lock (sync) { foreach (OperationLease lease in retained) { DeviceTransportGate.Unblock(lease.Session.Serial); lease.Dispose(); } retained.Clear(); blocked.Clear(); } }
            return cleaned;
        }
        public void Dispose() { lock (sync) closed = true; stopping.Cancel(); listener.Stop(); }
    }
}
