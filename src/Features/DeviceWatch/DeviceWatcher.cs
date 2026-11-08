using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture.Features.DeviceWatch
{
    internal enum DeviceLinkState { Device, Unauthorized, Offline, NoPermission, Missing, Unknown }
    internal enum WatchMode { Stopped, TrackDevices, Polling, CleanupPending }
    internal enum WatchFailure { None, ProviderFailed, InvalidSnapshot, CleanupPending }
    internal sealed class DeviceEntry
    {
        public readonly string Serial;
        public readonly DeviceLinkState State;
        public readonly bool Wireless;
        public DeviceEntry(string serial, DeviceLinkState state)
        { DeviceSessionRegistry.ValidateSerial(serial); if (!Enum.IsDefined(typeof(DeviceLinkState), state)) throw new ArgumentException("state"); Serial = serial; State = state; string host; int port; Wireless = WirelessAdb.TryParseEndpoint(serial, out host, out port) || serial.EndsWith("._adb-tls-connect._tcp", StringComparison.Ordinal); }
    }
    internal sealed class DeviceListChangedEventArgs : EventArgs
    {
        public readonly IList<DeviceEntry> Current, Added, Removed, Changed;
        internal DeviceListChangedEventArgs(IList<DeviceEntry> current, IList<DeviceEntry> added, IList<DeviceEntry> removed, IList<DeviceEntry> changed)
        { Current = current; Added = added; Removed = removed; Changed = changed; }
    }
    internal sealed class WatchReply
    {
        public readonly IList<DeviceEntry> Devices;
        public readonly bool Success, CleanupComplete;
        public readonly WatchFailure Failure;
        public WatchReply(bool success, bool cleanup, IList<DeviceEntry> devices = null, WatchFailure failure = WatchFailure.ProviderFailed)
        { Success = success; CleanupComplete = cleanup; Failure = success ? WatchFailure.None : failure; Devices = DeviceListParser.Freeze(devices ?? new DeviceEntry[0]); }
    }
    internal static class DeviceListParser
    {
        internal static IList<DeviceEntry> Freeze(IEnumerable<DeviceEntry> values)
        { DeviceEntry[] copy = values.Take(257).ToArray(); if (copy.Length > 256 || copy.Any(e => e == null) || copy.Select(e => e.Serial).Distinct(StringComparer.Ordinal).Count() != copy.Length) throw new ArgumentException("snapshot"); return new ReadOnlyCollection<DeviceEntry>(copy); }
        public static bool TryParse(string text, bool requireHeader, out IList<DeviceEntry> devices)
        {
            devices = Freeze(new DeviceEntry[0]);
            if (text == null || text.Length > 65535 || text.IndexOf('\0') >= 0) return false;
            string[] lines = text.Replace("\r\n", "\n").Split('\n'); bool header = !requireHeader;
            Dictionary<string, DeviceEntry> parsed = new Dictionary<string, DeviceEntry>(StringComparer.Ordinal);
            foreach (string raw in lines)
            {
                string line = raw.Trim(); if (line.Length == 0) continue;
                if (line == "List of devices attached") { if (parsed.Count != 0) return false; header = true; continue; }
                if (!header) return false;
                Match row = Regex.Match(line, @"\A([A-Za-z0-9_.:\-\[\]]{1,256})\s+(.+)\z");
                if (!row.Success || parsed.Count >= 256) return false;
                string serial = row.Groups[1].Value, detail = row.Groups[2].Value;
                DeviceLinkState state;
                string word = detail.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
                if (word == "device") state = DeviceLinkState.Device;
                else if (word == "offline") state = DeviceLinkState.Offline;
                else if (word == "unauthorized") state = DeviceLinkState.Unauthorized;
                else if (detail.StartsWith("no permissions", StringComparison.Ordinal)) state = DeviceLinkState.NoPermission;
                else state = DeviceLinkState.Unknown;
                if (parsed.ContainsKey(serial)) return false;
                parsed.Add(serial, new DeviceEntry(serial, state));
            }
            if (!header) return false;
            devices = Freeze(parsed.Values.OrderBy(d => d.Serial, StringComparer.Ordinal)); return true;
        }
    }
    // CLI track-devices forwards the server's four ASCII hex length bytes, then UTF-8 text.
    internal sealed class TrackTextSink : Stream
    {
        private readonly Action<IList<DeviceEntry>> publish;
        private readonly byte[] prefix = new byte[4];
        private int prefixCount, size = -1, used;
        private byte[] payload;
        public TrackTextSink(Action<IList<DeviceEntry>> publish) { if (publish == null) throw new ArgumentNullException("publish"); this.publish = publish; }
        public bool Complete { get { return prefixCount == 0 && size < 0; } }
        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                if (size < 0)
                {
                    prefix[prefixCount++] = buffer[offset++]; count--;
                    if (prefixCount < 4) continue;
                    string length = Encoding.ASCII.GetString(prefix);
                    if (!Regex.IsMatch(length, @"\A[0-9a-fA-F]{4}\z")) throw new InvalidDataException("TRACK_PREFIX");
                    size = int.Parse(length, NumberStyles.HexNumber, CultureInfo.InvariantCulture); payload = new byte[size]; used = 0;
                }
                int take = Math.Min(count, size - used);
                Buffer.BlockCopy(buffer, offset, payload, used, take); offset += take; count -= take; used += take;
                if (used == size)
                {
                    string body = new UTF8Encoding(false, true).GetString(payload); IList<DeviceEntry> entries;
                    if (!DeviceListParser.TryParse(body, false, out entries)) throw new InvalidDataException("TRACK_SNAPSHOT");
                    prefixCount = 0; size = -1; payload = null; publish(entries);
                }
            }
        }
        public override bool CanRead { get { return false; } } public override bool CanWrite { get { return true; } } public override bool CanSeek { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } } public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { } public override int Read(byte[] b, int o, int c) { throw new NotSupportedException(); }
        public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); } public override void SetLength(long v) { throw new NotSupportedException(); }
    }
    internal interface IDeviceWatchProvider
    {
        bool TrackSupported { get; }
        Task<WatchReply> PollAsync(CancellationToken token);
        Task<WatchReply> TrackAsync(Action<IList<DeviceEntry>> publish, CancellationToken token);
        Task<bool> StopAsync(TimeSpan deadline, CancellationToken token);
    }
    internal enum TrackFormat { Unknown, HexUtf8Text }
    internal sealed class OwnedAdbWatchProvider : IDeviceWatchProvider
    {
        private readonly string executable;
        private readonly ProcessRunner text = new ProcessRunner(65536);
        private readonly BinaryProcessRunner binary = new BinaryProcessRunner(4096);
        private readonly TrackFormat format;
        private Task textStop;
        private readonly object gate = new object();
        public OwnedAdbWatchProvider(string executable, TrackFormat verifiedFormat = TrackFormat.Unknown)
        { if (string.IsNullOrWhiteSpace(executable)) throw new ArgumentException("executable"); this.executable = executable; format = verifiedFormat; }
        public bool TrackSupported { get { return format == TrackFormat.HexUtf8Text; } }
        public async Task<WatchReply> PollAsync(CancellationToken token)
        {
            ProcessResult result = await text.RunAsync(new ProcessRequest(executable, "devices", new[] { "devices", "-l" }), TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            IList<DeviceEntry> entries = new DeviceEntry[0];
            bool ok = result.Outcome == ProcessOutcome.Success && !result.Truncated && DeviceListParser.TryParse(result.Output, true, out entries);
            if (!ok) entries = new DeviceEntry[0];
            return new WatchReply(ok, result.CleanupComplete, entries, result.Outcome == ProcessOutcome.Success ? WatchFailure.InvalidSnapshot : WatchFailure.ProviderFailed);
        }
        public async Task<WatchReply> TrackAsync(Action<IList<DeviceEntry>> publish, CancellationToken token)
        {
            if (!TrackSupported) return new WatchReply(false, true);
            TrackTextSink sink = new TrackTextSink(publish);
            OwnedBinaryJob job = binary.Start(new ProcessRequest(executable, "track-devices", new[] { "track-devices", "-l" }), sink, 16 * 1024 * 1024, TimeSpan.FromMinutes(30), token);
            BinaryProcessResult result = await job.Completion.ConfigureAwait(false);
            if (result.CleanupComplete) sink.Dispose(); else DisposeAfterCleanup(job, sink);
            // Even clean EOF requires restart; partial frames never become empty device snapshots.
            return new WatchReply(result.Outcome == BinaryProcessOutcome.TimedOut && sink.Complete, result.CleanupComplete, null, result.Outcome == BinaryProcessOutcome.SinkFailed ? WatchFailure.InvalidSnapshot : WatchFailure.ProviderFailed);
        }
        private static void DisposeAfterCleanup(OwnedBinaryJob job, Stream sink) { job.Cleanup.ContinueWith(done => sink.Dispose()); }
        public async Task<bool> StopAsync(TimeSpan deadline, CancellationToken token)
        {
            if (deadline.TotalMilliseconds < 1 || deadline.TotalMinutes > 5) throw new ArgumentOutOfRangeException("deadline");
            Task stopped; lock (gate) { if (textStop == null) textStop = Task.Run(() => text.StopAllAsync()); stopped = textStop; }
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            await Task.WhenAny(stopped, Task.Delay(deadline, token)).ConfigureAwait(false);
            TimeSpan left = deadline - clock.Elapsed;
            bool binaryClean = left.TotalMilliseconds > 0 && await binary.StopAllAsync(left, token).ConfigureAwait(false);
            return stopped.Status == TaskStatus.RanToCompletion && text.ActiveCount == 0 && binaryClean && binary.ActiveCount == 0;
        }
    }
    internal sealed class DeviceWatcher : IDisposable
    {
        private readonly object gate = new object();
        private readonly IDeviceWatchProvider provider;
        private readonly int intervalMs, debounceMs;
        private CancellationTokenSource cancellation;
        private Task loop, dispatch, stop, cancellationWork;
        private IList<DeviceEntry> current = DeviceListParser.Freeze(new DeviceEntry[0]), pending;
        private long pendingVersion;
        private bool closed;
        private int cancellationDisposed;
        private WatchMode mode;
        private WatchFailure failure;
        public event EventHandler<DeviceListChangedEventArgs> Changed;
        public DeviceWatcher(IDeviceWatchProvider provider, int intervalMs = 1000, int debounceMs = 150)
        { if (provider == null) throw new ArgumentNullException("provider"); if (intervalMs < 20 || intervalMs > 30000 || debounceMs < 1 || debounceMs > 2000) throw new ArgumentOutOfRangeException("timing"); this.provider = provider; this.intervalMs = intervalMs; this.debounceMs = debounceMs; }
        public IList<DeviceEntry> Current { get { lock (gate) return current; } }
        public WatchMode Mode { get { lock (gate) return mode; } }
        public WatchFailure Failure { get { lock (gate) return failure; } }
        public void Start()
        {
            lock (gate)
            {
                if (closed) throw new ObjectDisposedException("DeviceWatcher");
                if (loop != null) return;
                cancellation = new CancellationTokenSource();
                dispatch = Task.Run(() => Dispatch(cancellation.Token)); loop = Task.Run(() => Run(cancellation.Token));
            }
        }
        private void Publish(IList<DeviceEntry> entries)
        { lock (gate) { if (closed) return; pending = DeviceListParser.Freeze(entries); pendingVersion++; failure = WatchFailure.None; } }
        private async Task Dispatch(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(debounceMs, token).ConfigureAwait(false);
                    long version; lock (gate) version = pendingVersion;
                    await Task.Delay(debounceMs, token).ConfigureAwait(false);
                    DeviceListChangedEventArgs changes = null;
                    lock (gate)
                    {
                        if (closed || version != pendingVersion || pending == null) continue;
                        Dictionary<string, DeviceEntry> old = current.ToDictionary(e => e.Serial, StringComparer.Ordinal), next = pending.ToDictionary(e => e.Serial, StringComparer.Ordinal);
                        IList<DeviceEntry> added = DeviceListParser.Freeze(pending.Where(e => !old.ContainsKey(e.Serial)));
                        IList<DeviceEntry> removed = DeviceListParser.Freeze(current.Where(e => !next.ContainsKey(e.Serial)).Select(e => new DeviceEntry(e.Serial, DeviceLinkState.Missing)));
                        IList<DeviceEntry> changed = DeviceListParser.Freeze(pending.Where(e => old.ContainsKey(e.Serial) && old[e.Serial].State != e.State));
                        current = pending; pending = null;
                        if (added.Count + removed.Count + changed.Count > 0) changes = new DeviceListChangedEventArgs(current, added, removed, changed);
                    }
                    if (changes != null)
                    {
                        EventHandler<DeviceListChangedEventArgs> handlers = Changed;
                        if (handlers != null) foreach (EventHandler<DeviceListChangedEventArgs> handler in handlers.GetInvocationList()) try { handler(this, changes); } catch { /* Subscribers own their failures; no raw diagnostics. */ }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
        private async Task Run(CancellationToken token)
        {
            int failures = 0; bool track = provider.TrackSupported;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    lock (gate) mode = track ? WatchMode.TrackDevices : WatchMode.Polling;
                    WatchReply reply;
                    try { reply = track ? await provider.TrackAsync(Publish, token).ConfigureAwait(false) : await provider.PollAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { if (token.IsCancellationRequested) break; reply = new WatchReply(false, true); }
                    catch { reply = new WatchReply(false, false); }
                    if (token.IsCancellationRequested) break;
                    if (reply == null) reply = new WatchReply(false, false);
                    if (!reply.CleanupComplete) { lock (gate) { mode = WatchMode.CleanupPending; failure = WatchFailure.CleanupPending; } break; }
                    if (reply.Success) { failures = 0; if (!track) Publish(reply.Devices); }
                    else { failures = Math.Min(6, failures + 1); lock (gate) failure = reply.Failure; }
                    if (track && failures >= 3) { track = false; failures = 0; }
                    int delay = reply.Success ? intervalMs : Math.Min(30000, intervalMs * (1 << failures));
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }
        public async Task<bool> StopAsync(TimeSpan deadline, CancellationToken token)
        {
            if (deadline.TotalMilliseconds < 1 || deadline.TotalMinutes > 5) throw new ArgumentOutOfRangeException("deadline");
            Task owned;
            lock (gate)
            {
                closed = true; pending = null;
                if (cancellation != null && cancellationWork == null) cancellationWork = Task.Run(() => { try { cancellation.Cancel(); } catch { } });
                if (stop == null || stop.IsFaulted || (stop.Status == TaskStatus.RanToCompletion && !((Task<bool>)stop).Result)) stop = Task.Run(() => StopOwned()); owned = stop;
            }
            await Task.WhenAny(owned, Task.Delay(deadline, token)).ConfigureAwait(false);
            bool clean = owned.Status == TaskStatus.RanToCompletion && ((Task<bool>)owned).Result;
            lock (gate) { mode = clean ? WatchMode.Stopped : WatchMode.CleanupPending; failure = clean ? WatchFailure.None : WatchFailure.CleanupPending; }
            return clean;
        }
        private async Task<bool> StopOwned()
        {
            Task work, events; lock (gate) { work = loop; events = dispatch; }
            bool clean; try { clean = await provider.StopAsync(TimeSpan.FromMinutes(5), CancellationToken.None).ConfigureAwait(false); } catch { clean = false; }
            if (work != null) await work.ConfigureAwait(false); if (events != null) await events.ConfigureAwait(false);
            if (cancellationWork != null) await cancellationWork.ConfigureAwait(false);
            if (clean && cancellation != null && Interlocked.Exchange(ref cancellationDisposed, 1) == 0) cancellation.Dispose();
            return clean;
        }
        public void Dispose() { RequestStop(); }
        private void RequestStop() { Task<bool> requested = StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None); GC.KeepAlive(requested); }
    }
}
