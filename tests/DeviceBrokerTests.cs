using System;
using System.IO;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture
{
    internal static class DeviceBrokerTests
    {
        private sealed class Reader : IBrokerFileReader
        {
            internal int Calls, Active, Maximum;
            internal byte[] Bytes = Encoding.UTF8.GetBytes("catalog");
            internal bool Pending, Clean = true, Hang;
            internal readonly ManualResetEvent Entered = new ManualResetEvent(false);
            public async Task<BrokerFileResult> ReadAsync(DeviceSession session, string file, CancellationToken token)
            {
                Interlocked.Increment(ref Calls); int count = Interlocked.Increment(ref Active);
                Maximum = Math.Max(Maximum, count); Entered.Set();
                try { if (Hang) { while (!token.IsCancellationRequested) await Task.Delay(10); }
                    else await Task.Delay(60);
                    return new BrokerFileResult { Code = file == "catalog-wal" ? "DEVICE_FILE_MISSING" : "OK", Bytes = file == "catalog-wal" ? new byte[0] : Bytes, CleanupComplete = !Pending };
                } finally { Interlocked.Decrement(ref Active); }
            }
            public Task<bool> StopAsync() { return Task.FromResult(Clean); }
        }
        private static string Request(DeviceBrokerAddress address, DeviceSession session, string token = null, string file = "catalog", string instance = null)
        {
            return "{\"contract\":\"thor.device-broker/1\",\"instanceId\":" + Json.Quote(instance ?? address.InstanceId) + ",\"token\":" + Json.Quote(token ?? address.Token)
                + ",\"requestId\":" + Json.Quote(Guid.NewGuid().ToString("D")) + ",\"serial\":" + Json.Quote(session.Serial) + ",\"sessionId\":" + Json.Quote(session.Id.ToString("D")) + ",\"file\":" + Json.Quote(file) + "}\n";
        }
        private static object Call(DeviceBrokerAddress address, string request)
        {
            using (TcpClient client = new TcpClient()) {
                client.Connect("127.0.0.1", address.Port); client.ReceiveTimeout = 10000; NetworkStream stream = client.GetStream();
                byte[] encoded = Encoding.UTF8.GetBytes(request); stream.Write(encoded, 0, encoded.Length);
                byte[] prefix = Exact(stream, 4); int count = (prefix[0] << 24) | (prefix[1] << 16) | (prefix[2] << 8) | prefix[3];
                object header = Json.Parse(Encoding.UTF8.GetString(Exact(stream, count))); Exact(stream, (int)Json.Number(header, "bytes", -1)); return header;
            }
        }
        private static byte[] Exact(Stream input, int size)
        {
            byte[] bytes = new byte[size]; int offset = 0;
            while (offset < size) { int count = input.Read(bytes, offset, size - offset); if (count == 0) throw new IOException("Incomplete broker response"); offset += count; } return bytes;
        }
        internal static void Run() { Task.Run(() => Checks()).GetAwaiter().GetResult(); }
        private static async Task Checks()
        {
            Tests.Reject(() => DeviceTransportGate.SerialOf("shell reboot"), "unbound shell is rejected before transport");
            Tests.Reject(() => DeviceTransportGate.SerialOf("-d shell cat fixed"), "implicit USB selection cannot bypass device coordination");
            Tests.Reject(() => DeviceTransportGate.SerialOf("-s first -s second shell cat fixed"), "second serial selector cannot silently change a captured target");
            Tests.Check(DeviceTransportGate.SerialOf("devices -l") == null && DeviceTransportGate.SerialOf("-s offline-device exec-out cat fixed") == "offline-device", "host discovery and explicit device target are distinct");
            using (DeviceSessionRegistry sessions = new DeviceSessionRegistry())
            using (OperationGate operations = new OperationGate(sessions)) {
                DeviceSession session = sessions.Open("offline-device", DeviceCapabilities.CocoonRead | DeviceCapabilities.CocoonWrite), unsupported = sessions.Open("no-read", DeviceCapabilities.None);
                Reader reader = new Reader(); using (DeviceCommandBroker broker = new DeviceCommandBroker(sessions, operations, reader)) {
                    Tests.Check(Json.Text(Call(broker.Address, Request(broker.Address, session, "wrong")), "code") == "BROKER_AUTH_REQUIRED"
                        && Json.Text(Call(broker.Address, Request(broker.Address, session, null, "catalog", Guid.NewGuid().ToString())), "code") == "BROKER_AUTH_REQUIRED" && reader.Calls == 0,
                        "wrong broker token and instance launch zero readers");
                    Tests.Check(Json.Text(Call(broker.Address, Request(broker.Address, unsupported)), "code") == "DEVICE_READ_UNSUPPORTED" && reader.Calls == 0, "unknown device capability fails closed");
                    Tests.Check(Json.Text(Call(broker.Address, Request(broker.Address, session, null, "arbitrary")), "code") == "BROKER_REQUEST_INVALID" && reader.Calls == 0, "broker has no arbitrary path or shell command");
                    Task<object> first = Task.Run(() => Call(broker.Address, Request(broker.Address, session))), second = Task.Run(() => Call(broker.Address, Request(broker.Address, session)));
                    await Task.WhenAll(first, second);
                    Tests.Check(reader.Calls == 2 && reader.Maximum == 1 && Json.Text(first.Result, "code") == "OK" && Json.Text(second.Result, "code") == "OK", "two broker requests queue under one named device transport mutex");
                    OperationLease writer; operations.TryAcquire(session, OperationKind.CocoonWrite, out writer);
                    Task<object> queued = Task.Run(() => Call(broker.Address, Request(broker.Address, session))); await Task.Delay(80);
                    Tests.Check(reader.Calls == 2, "waiting device lease permits zero calls"); writer.Dispose(); await queued;
                    sessions.Disconnect(session.Serial);
                    Tests.Check(Json.Text(Call(broker.Address, Request(broker.Address, session)), "code") == "DEVICE_DISCONNECTED", "old device identity is refused after disconnect");
                    session = sessions.Open("offline-device", DeviceCapabilities.CocoonRead);
                    await NodeInterop(broker, session);
                    Tests.Check(await broker.StopAsync() && operations.ActiveCount == 0, "broker stop acknowledges reader cleanup and releases owned leases");
                }
            }
            using (DeviceSessionRegistry sessions = new DeviceSessionRegistry())
            using (OperationGate operations = new OperationGate(sessions)) {
                DeviceSession session = sessions.Open("pending-device", DeviceCapabilities.CocoonRead); Reader reader = new Reader { Pending = true, Clean = false };
                using (DeviceCommandBroker broker = new DeviceCommandBroker(sessions, operations, reader)) {
                    Tests.Check(Json.Text(Call(broker.Address, Request(broker.Address, session)), "code") == "DEVICE_CLEANUP_REQUIRED" && operations.ActiveCount == 1, "unfinished reader retains operation ownership");
                    Tests.Reject(() => DeviceTransportGate.Execute(session.Serial, TimeSpan.FromMilliseconds(50), CancellationToken.None, () => true), "pending device cleanup blocks subsequent native transport");
                    Tests.Check(!await broker.StopAsync() && operations.ActiveCount == 1, "failed cleanup acknowledgment does not release the lease");
                    reader.Clean = true; Tests.Check(await broker.StopAsync() && operations.ActiveCount == 0, "later acknowledged cleanup releases retained lease");
                }
            }
            using (DeviceSessionRegistry sessions = new DeviceSessionRegistry())
            using (OperationGate operations = new OperationGate(sessions)) {
                DeviceSession session = sessions.Open("cancel-device", DeviceCapabilities.CocoonRead); Reader reader = new Reader { Hang = true };
                using (DeviceCommandBroker broker = new DeviceCommandBroker(sessions, operations, reader)) {
                    using (TcpClient client = new TcpClient()) { client.Connect("127.0.0.1", broker.Address.Port); byte[] bytes = Encoding.UTF8.GetBytes(Request(broker.Address, session)); client.GetStream().Write(bytes, 0, bytes.Length);
                        Tests.Check(reader.Entered.WaitOne(2000), "read entered before peer disconnect"); }
                    Stopwatch wait = Stopwatch.StartNew(); while (operations.ActiveCount > 0 && wait.ElapsedMilliseconds < 2000) await Task.Delay(10);
                    Tests.Check(reader.Active == 0 && operations.ActiveCount == 0, "closed Node peer cancels and drains reader before releasing lease"); await broker.StopAsync();
                }
            }
            await OwnedLibraryRead();
            await CrossProcessGate();
            await CrossProcessPending();
        }
        private static async Task CrossProcessGate()
        {
            string marker = Path.Combine(Tests.Root, "mutex-proof-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(marker);
            string old = Environment.GetEnvironmentVariable("THOR_MUTEX_PROOF"); Environment.SetEnvironmentVariable("THOR_MUTEX_PROOF", marker);
            try { using (Process first = new Process { StartInfo = new ProcessRequest(typeof(Tests).Assembly.Location, "mutex-proof", new[] { "broker-mutex-owner" }).StartInfo() }) {
                first.Start(); Stopwatch wait = Stopwatch.StartNew(); while (!File.Exists(Path.Combine(marker, "entered")) && wait.ElapsedMilliseconds < 2000) await Task.Delay(10);
                Tests.Check(File.Exists(Path.Combine(marker, "entered")), "separate owned process acquired the named device mutex");
                bool called = false; Tests.Reject(() => DeviceTransportGate.Execute("cross-process", TimeSpan.FromMilliseconds(60), CancellationToken.None, () => called = true), "second process cannot enter while named transport is owned");
                Tests.Check(!called, "failed cross-process reservation sends zero device calls");
                File.WriteAllText(Path.Combine(marker, "release"), "1"); first.WaitForExit(3000);
                Tests.Check(first.HasExited && DeviceTransportGate.Execute("cross-process", TimeSpan.FromSeconds(1), CancellationToken.None, () => true), "acknowledged first process exit lets the next process enter");
            } } finally { Environment.SetEnvironmentVariable("THOR_MUTEX_PROOF", old); }
        }
        internal static int FakeAdb(string command, string mode, string dir)
        {
            if (command == "broker-pending-owner") {
                string pending = Environment.GetEnvironmentVariable("THOR_MUTEX_PROOF");
                DeviceTransportGate.Execute("cross-pending", TimeSpan.FromSeconds(2), CancellationToken.None, () => { DeviceTransportGate.Block("cross-pending"); return true; });
                File.WriteAllText(Path.Combine(pending,"entered"),"1"); Stopwatch clock = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(pending,"release")) && clock.ElapsedMilliseconds < 4000) Thread.Sleep(10);
                DeviceTransportGate.Unblock("cross-pending"); return 0;
            }
            if (command != "broker-mutex-owner") return Tests.NotHandled;
            string marker = Environment.GetEnvironmentVariable("THOR_MUTEX_PROOF");
            return DeviceTransportGate.Execute("cross-process", TimeSpan.FromSeconds(2), CancellationToken.None, () => {
                File.WriteAllText(Path.Combine(marker, "entered"), "1"); Stopwatch wait = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(marker, "release")) && wait.ElapsedMilliseconds < 4000) Thread.Sleep(10); return 0;
            });
        }
        private static async Task CrossProcessPending()
        {
            string marker = Path.Combine(Tests.Root,"pending-proof-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(marker);
            string old = Environment.GetEnvironmentVariable("THOR_MUTEX_PROOF"); Environment.SetEnvironmentVariable("THOR_MUTEX_PROOF",marker);
            try { using (Process child = new Process { StartInfo = new ProcessRequest(typeof(Tests).Assembly.Location,"pending-proof",new[]{"broker-pending-owner"}).StartInfo() }) {
                child.Start(); try {
                    Stopwatch clock = Stopwatch.StartNew(); while (!File.Exists(Path.Combine(marker,"entered")) && clock.ElapsedMilliseconds < 2000) await Task.Delay(10);
                    Tests.Check(File.Exists(Path.Combine(marker,"entered")),"separate process returned pending cleanup while retaining cross-process reservation");
                    bool called = false; Tests.Reject(() => DeviceTransportGate.Execute("cross-pending",TimeSpan.FromMilliseconds(80),CancellationToken.None,()=>called=true),"pending cleanup still prevents a different process from acquiring device");
                    Tests.Check(!called,"cross-process pending cleanup permits zero device calls");
                } finally { File.WriteAllText(Path.Combine(marker,"release"),"1"); child.WaitForExit(3000); }
                Tests.Check(child.HasExited && DeviceTransportGate.Execute("cross-pending",TimeSpan.FromSeconds(1),CancellationToken.None,()=>true),"explicit cleanup acknowledgement releases cross-process file reservation");
            } } finally { Environment.SetEnvironmentVariable("THOR_MUTEX_PROOF",old); }
        }
        private static async Task OwnedLibraryRead()
        {
            string backendDir = Path.GetFullPath(Path.Combine(Adb.AppDir(), "..", "..", "cocoon-art-backend"));
            string demoScript = Path.GetFullPath(Path.Combine(Adb.AppDir(), "..", "tools", "cocoon-demo.cjs"));
            string source = Path.Combine(Tests.Root, "pull-source-" + Guid.NewGuid().ToString("N")), destination = Path.Combine(Tests.Root, "pull-target-" + Guid.NewGuid().ToString("N"));
            using (ProcessRunner maker = new ProcessRunner()) {
                ProcessResult built = await maker.RunAsync(new ProcessRequest("node", "offline-catalog-fixture", new[] { demoScript, source }), TimeSpan.FromSeconds(10), CancellationToken.None);
                if (built.Outcome != ProcessOutcome.Success) throw new IOException("Fixture creation failed");
            }
            byte[] original = File.ReadAllBytes(Path.Combine(source, "_data", "cocoon_db"));
            using (DeviceSessionRegistry sessions = new DeviceSessionRegistry())
            using (OperationGate operations = new OperationGate(sessions)) {
                DeviceSession session = sessions.Open("offline-library", DeviceCapabilities.CocoonRead); Reader reader = new Reader { Bytes = original };
                using (DeviceCommandBroker broker = new DeviceCommandBroker(sessions, operations, reader))
                using (CocoonBackend backend = new CocoonBackend(null) { BackendDir = backendDir, DataRoot = destination }) {
                    try {
                        backend.Start(); Stopwatch wait = Stopwatch.StartNew(); while (backend.State == BackendState.Starting && wait.ElapsedMilliseconds < 15000) await Task.Delay(20);
                        Tests.Check(backend.State == BackendState.Ready && await backend.BindDeviceBrokerAsync(broker.Address), "owned Node backend accepts broker capability only through parent stdin");
                        DevicePullClient pull = new DevicePullClient(backend);
                        DevicePullJob job = await pull.StartPlanAsync(session, CancellationToken.None);
                        Tests.Check(job.State == "reading" && !File.Exists(Path.Combine(destination, "_data", "cocoon_db")), "native read starts asynchronously and cannot overwrite an empty local catalog");
                        wait.Restart(); do { await Task.Delay(30); job = await pull.StatusAsync(job.JobId, CancellationToken.None); } while ((job.State == "reading" || job.State == "checking") && wait.ElapsedMilliseconds < 15000);
                        Tests.Check(job.State == "planned" && job.Plan.TotalGames == 24 && reader.Calls == 4, "actual C# broker to Node pull creates a validated catalog review plan");
                        LibraryImportClient imports = new LibraryImportClient(backend);
                        LibraryImportJob imported = await imports.ExecuteAsync(job.Plan.PlanId, true); wait.Restart();
                        while (imported.State != "completed" && imported.State != "failed" && wait.ElapsedMilliseconds < 15000) { await Task.Delay(30); imported = await imports.StatusAsync(imported.JobId); }
                        Tests.Check(imported.State == "completed" && ((System.Collections.Generic.List<object>)backend.GetJson("/api/games")).Count == 24,
                            "explicit native confirmation publishes the paired broker/import result");
                        Tests.Check(Convert.ToBase64String(original) == Convert.ToBase64String(File.ReadAllBytes(Path.Combine(source, "_data", "cocoon_db"))), "read fixture database remains byte identical after publication");
                    } finally { Tests.Check(backend.StopAsync(TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult(), "owned library backend drains read/import scratch on shutdown"); broker.StopAsync().GetAwaiter().GetResult(); }
                }
            }
        }
        private static async Task NodeInterop(DeviceCommandBroker broker, DeviceSession session)
        {
            string module = Path.GetFullPath(Path.Combine(Adb.AppDir(), "..", "..", "cocoon-art-backend", "lib", "runtime", "deviceBrokerClient.js"));
            if (!File.Exists(module)) throw new IOException("Required offline Node broker component absent");
            string root = Path.Combine(Tests.Root, "broker-node-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string config = Path.Combine(root, "session.json"), script = Path.Combine(root, "check.cjs");
            File.WriteAllText(config, "{\"contract\":\"thor.device-broker/1\",\"host\":\"127.0.0.1\",\"port\":" + broker.Address.Port + ",\"instanceId\":" + Json.Quote(broker.Address.InstanceId)
                + ",\"token\":" + Json.Quote(broker.Address.Token) + ",\"serial\":" + Json.Quote(session.Serial) + ",\"sessionId\":" + Json.Quote(session.Id.ToString("D")) + "}");
            File.WriteAllText(script, "const fs=require('node:fs'),{DeviceBrokerClient}=require(" + Json.Quote(module) + ");const c=JSON.parse(fs.readFileSync(process.argv[2],'utf8'));const b=new DeviceBrokerClient();b.bind(c);b.read({...c,file:'catalog'}).then(x=>{if(x.toString()!=='catalog')throw Error();b.stop();console.log('verified')}).catch(()=>process.exit(3));");
            try { using (ProcessRunner runner = new ProcessRunner()) {
                ProcessResult result = await runner.RunAsync(new ProcessRequest("node", "broker-interoperability", new[] { script, config }), TimeSpan.FromSeconds(5), CancellationToken.None);
                Tests.Check(result.Outcome == ProcessOutcome.Success && result.Output.Trim() == "verified", "actual Node client authenticates and verifies C# broker framing and binary payload");
            } } finally { Directory.Delete(root, true); }
        }
    }
}
