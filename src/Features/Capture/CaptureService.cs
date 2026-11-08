using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;
using ThorCapture.Media;

namespace ThorCapture.Features.Capture
{
    internal sealed class ScreenBytes
    {
        public readonly byte[] Bytes;
        public readonly string Code;
        public readonly bool CleanupComplete;
        public ScreenBytes(byte[] bytes, string code, bool clean) { Bytes = bytes; Code = code; CleanupComplete = clean; }
    }
    internal interface IScreenCaptureSource
    {
        bool SupportsParallel { get; }
        Task<ScreenBytes> ReadAsync(DeviceSession session, DisplayTarget display, TimeSpan timeout, CancellationToken token);
        Task<bool> StopAsync();
    }
    internal sealed class CaptureRequest
    {
        public readonly DeviceSession Session;
        public readonly ReadOnlyCollection<DisplayTarget> Displays;
        public readonly string Directory, BurstId;
        public readonly bool Parallel, ReadForeground;
        public readonly TimeSpan Timeout;
        public readonly long MaxBytes;
        public CaptureRequest(DeviceSession session, IEnumerable<DisplayTarget> displays, string directory, bool parallel,
            TimeSpan timeout, long maxBytes, string burstId = null, bool readForeground = false)
        {
            if (session == null) throw new ArgumentNullException("session");
            DisplayTarget[] values = displays.ToArray();
            if (values.Length < 1 || values.Length > 2 || values.Select(x => x.Role).Distinct().Count() != values.Length
                || values.Any(x => !Object.ReferenceEquals(session.Display(x.Role), x))) throw new ArgumentException("Display session mismatch");
            if (timeout.TotalMilliseconds < 100 || timeout.TotalSeconds > 60 || maxBytes < 1 || maxBytes > 128L * 1024 * 1024) throw new ArgumentOutOfRangeException("capture limits");
            Guid id; if (burstId != null && !Guid.TryParse(burstId, out id)) throw new ArgumentException("burstId");
            Session = session; Displays = new ReadOnlyCollection<DisplayTarget>(values); Directory = Path.GetFullPath(directory);
            Parallel = parallel; Timeout = timeout; MaxBytes = maxBytes; BurstId = burstId; ReadForeground = readForeground;
        }
    }
    internal sealed class TrackShot
    {
        public readonly string Role, Path, Code;
        public readonly TimeSpan Issued, Completed;
        public readonly long Bytes;
        internal TrackShot(string role, string path, string code, TimeSpan issued, TimeSpan completed, long bytes)
        { Role = role; Path = path; Code = code; Issued = issued; Completed = completed; Bytes = bytes; }
    }
    internal sealed class CaptureBatchResult
    {
        public readonly string BatchId, GroupId, Mode, Code;
        public readonly ReadOnlyCollection<TrackShot> Tracks;
        public readonly bool CleanupComplete;
        public long Bytes { get { return Tracks.Sum(x => x.Bytes); } }
        internal CaptureBatchResult(string batch, string group, string mode, string code, IEnumerable<TrackShot> tracks, bool clean)
        { BatchId = batch; GroupId = group; Mode = mode; Code = code; Tracks = new ReadOnlyCollection<TrackShot>(tracks.ToArray()); CleanupComplete = clean; }
    }
    internal sealed class CaptureService
    {
        readonly IScreenCaptureSource source; readonly OperationGate operations; readonly MediaRepository repository;
        readonly IAdbClient observer; readonly Func<Task<bool>> stopObserver;
        readonly object gate = new object(); readonly List<OperationLease> retained = new List<OperationLease>();
        readonly CancellationTokenSource closing = new CancellationTokenSource();
        readonly List<Task<CaptureBatchResult>> active = new List<Task<CaptureBatchResult>>(); bool closed;
        Task cancelWork; Task<bool> stopSourceWork, stopForegroundWork;
        readonly List<Task> trackCancellations = new List<Task>();
        public CaptureService(IScreenCaptureSource provider, OperationGate operationGate, IMediaRepository media, IAdbClient foreground = null, Func<Task<bool>> foregroundStop = null)
        { if (provider == null || operationGate == null || media == null) throw new ArgumentNullException("capture dependencies");
            if ((foreground == null) != (foregroundStop == null)) throw new ArgumentException("Foreground owner must provide cleanup acknowledgement");
            repository = media as MediaRepository; if (repository == null) throw new ArgumentException("Owned atomic publication repository required");
            source = provider; operations = operationGate; observer = foreground; stopObserver = foregroundStop; }
        public Task<CaptureBatchResult> RunAsync(CaptureRequest request, CancellationToken token)
        {
            lock (gate) {
                if (closed) throw new ObjectDisposedException("CaptureService");
                Task<CaptureBatchResult> work = Execute(request, token); active.Add(work);
                Task observation = work.ContinueWith(done => { lock (gate) active.Remove(done); }, TaskContinuationOptions.ExecuteSynchronously);
                GC.KeepAlive(observation); return work;
            }
        }
        static void SafeDirectory(string directory)
        {
            for (string current = directory; current != null; current = Path.GetDirectoryName(current))
                if (System.IO.Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked capture directory");
            System.IO.Directory.CreateDirectory(directory);
        }
        static void SaveNew(string file, byte[] bytes)
        {
            string temp = file + ".partial"; bool owned = false;
            try { using (FileStream output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { owned = true; output.Write(bytes, 0, bytes.Length); output.Flush(true); } File.Move(temp, file); }
            finally { if (owned && File.Exists(temp)) File.Delete(temp); }
        }
        async Task<CaptureBatchResult> Execute(CaptureRequest request, CancellationToken token)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (!String.Equals(Path.GetFullPath(request.Directory + Path.DirectorySeparatorChar), Path.GetFullPath(repository.MediaRoot + Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Capture directory must match repository");
            string batch = Guid.NewGuid().ToString("D"), mode = request.Parallel && source.SupportsParallel ? "parallel" : request.Parallel ? "sequential-fallback" : "sequential";
            OperationLease lease; LeaseOutcome acquisition = operations.TryAcquire(request.Session, OperationKind.Capture, out lease);
            if (acquisition != LeaseOutcome.Acquired) return new CaptureBatchResult(batch, null, mode, acquisition.ToString().ToUpperInvariant(), new TrackShot[0], true);
            bool clean = true; List<TrackShot> shots = new List<TrackShot>(); Stopwatch clock = Stopwatch.StartNew();
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, lease.Token, closing.Token))
            {
                try {
                    SafeDirectory(request.Directory); linked.Token.ThrowIfCancellationRequested();
                    ForegroundSnapshot foreground = new ForegroundSnapshot();
                    if (request.ReadForeground) {
                        foreground.QueryFailed = true;
                        if (observer != null) {
                            clean = false;
                            DeviceCommandResult observation = await observer.RunAsync(request.Session, AdbQuery.Activities, TimeSpan.FromSeconds(8), linked.Token).ConfigureAwait(false);
                            clean = observation.CleanupComplete;
                            if (!observation.CleanupComplete) { clean = false; return new CaptureBatchResult(batch, null, mode, "CAPTURE_CLEANUP_REQUIRED", shots, false); }
                            if (observation.Outcome == CommandOutcome.Success && observation.SessionId == request.Session.Id && !observation.Truncated) foreground = ForegroundSnapshot.Parse(observation.Output);
                        }
                    }
                    string suffix = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    long total = 0; object sizeGate = new object();
                    Dictionary<string, PublishedPngInput> publications = new Dictionary<string, PublishedPngInput>();
                    Func<DisplayTarget, Task<TrackShot>> shoot = async display => {
                        TimeSpan issued = clock.Elapsed; ScreenBytes result = null; bool providerAcknowledged = false;
                        try {
                            using (CancellationTokenSource track = CancellationTokenSource.CreateLinkedTokenSource(linked.Token)) {
                                Task<ScreenBytes> read = Task.Run(() => source.ReadAsync(request.Session, display, request.Timeout, track.Token));
                                if (await Task.WhenAny(read, Task.Delay(request.Timeout)).ConfigureAwait(false) != read) {
                                    Task cancelTrack = Task.Run(delegate { try { track.Cancel(); } catch (AggregateException) { } });
                                    lock (gate) trackCancellations.Add(cancelTrack);
                                    Task trackDrained = Task.WhenAll(read, cancelTrack);
                                    Task observation = trackDrained.ContinueWith(done => { if (done.IsFaulted) GC.KeepAlive(done.Exception); }); GC.KeepAlive(observation);
                                    if (await Task.WhenAny(trackDrained, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != trackDrained) {
                                        lock (sizeGate) clean = false;
                                        return new TrackShot(display.Role, null, "CAPTURE_CLEANUP_REQUIRED", issued, clock.Elapsed, 0);
                                    }
                                    await cancelTrack.ConfigureAwait(false);
                                }
                                result = await read.ConfigureAwait(false);
                                providerAcknowledged = result != null && result.CleanupComplete;
                                if (!result.CleanupComplete) { lock (sizeGate) clean = false; return new TrackShot(display.Role, null, "CAPTURE_CLEANUP_REQUIRED", issued, clock.Elapsed, 0); }
                                track.Token.ThrowIfCancellationRequested();
                            }
                            if (result.Code != "OK") return new TrackShot(display.Role, null, result.Code, issued, clock.Elapsed, 0);
                            if (result.Bytes == null || result.Bytes.Length < 8 || result.Bytes.Length > 32 * 1024 * 1024) throw new IOException("Invalid capture bytes");
                            byte[] b = result.Bytes; if (b.Length < 24 || b[0] != 137 || b[1] != 80 || b[2] != 78 || b[3] != 71) throw new IOException("PNG required");
                            uint width = ((uint)b[16] << 24) | ((uint)b[17] << 16) | ((uint)b[18] << 8) | b[19];
                            uint height = ((uint)b[20] << 24) | ((uint)b[21] << 16) | ((uint)b[22] << 8) | b[23];
                            if (width != display.Width || height != display.Height || (long)width * height > 16 * 1024 * 1024) throw new IOException("Capture dimensions mismatch");
                            using (MemoryStream memory = new MemoryStream(b)) using (Image image = Image.FromStream(memory, false, true))
                                if (image.Width != display.Width || image.Height != display.Height || (long)image.Width * image.Height > 16 * 1024 * 1024) throw new IOException("Capture dimensions mismatch");
                            lock (sizeGate) { if (total + b.Length > request.MaxBytes) return new TrackShot(display.Role, null, "CAPTURE_BYTE_LIMIT", issued, clock.Elapsed, 0); total += b.Length; }
                            linked.Token.ThrowIfCancellationRequested();
                            string file = System.IO.Path.Combine(request.Directory, "thor-" + display.Role + "-" + suffix + ".png");
                            string package;
                            foreground.Packages.TryGetValue(display.LogicalId, out package);
                            if ((package ?? "").Length > 512) package = "";
                            string status = !request.ReadForeground ? "not-requested" : foreground.QueryFailed ? "query-failed" : String.IsNullOrEmpty(package) ? "unknown" : "identified";
                            string metadata = String.Join("\n",new[]{"version=1", "package="+(package??""), "status="+status, "serial="+Uri.EscapeDataString(request.Session.Serial),
                                "displayId="+display.LogicalId.ToString(CultureInfo.InvariantCulture), "physicalId="+display.PhysicalId,
                                "capturedUtc="+DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture), "foregroundObservedUtc="+foreground.ObservedUtc.ToString("o",CultureInfo.InvariantCulture)})+"\n";
                            lock(sizeGate) publications.Add(display.Role,new PublishedPngInput(System.IO.Path.GetFileName(file),display.Role=="upper"?ScreenRole.Upper:ScreenRole.Lower,b,Encoding.UTF8.GetBytes(metadata)));
                            return new TrackShot(display.Role, file, "OK", issued, clock.Elapsed, b.Length);
                        }
                        catch (OperationCanceledException) { if (!providerAcknowledged) lock (sizeGate) clean = false; return new TrackShot(display.Role, null, linked.IsCancellationRequested ? "CAPTURE_CANCELLED" : "CAPTURE_TIMEOUT", issued, clock.Elapsed, 0); }
                        catch { if (!providerAcknowledged) lock (sizeGate) clean = false; return new TrackShot(display.Role, null, "CAPTURE_FAILED", issued, clock.Elapsed, 0); }
                    };
                    if (mode == "parallel") shots.AddRange(await Task.WhenAll(request.Displays.Select(shoot)).ConfigureAwait(false));
                    else foreach (DisplayTarget display in request.Displays) { if (linked.IsCancellationRequested) break; shots.Add(await shoot(display).ConfigureAwait(false)); }
                    string group = null, code = linked.IsCancellationRequested ? "CAPTURE_CANCELLED" : "OK";
                    TrackShot[] saved = shots.Where(x => x.Path != null).ToArray();
                    if (code == "OK" && saved.Length > 0 && saved.Length != request.Displays.Count) code = "CAPTURE_PARTIAL";
                    if (code == "OK" && saved.Any(x => x.Code != "OK")) code = "CAPTURE_METADATA_PENDING";
                    if (saved.Length > 0) {
                        MediaRepositoryResult registered = await repository.PublishGroupAsync(repository.GetSnapshot().RepositoryId, saved.Select(x=>publications[x.Role]), linked.Token).ConfigureAwait(false);
                        if (registered.Group != null) group = registered.Group.GroupId;
                        if (registered.Outcome != MediaOutcome.Success) code = registered.Outcome == MediaOutcome.Cancelled ? "CAPTURE_CANCELLED" : "CAPTURE_PUBLICATION_PENDING";
                        shots = shots.Select(x => x.Path == null || registered.Group != null && registered.Group.Tracks.Any(t=>t.State==TrackState.Present && t.RelativePath.Equals(System.IO.Path.GetFileName(x.Path),StringComparison.OrdinalIgnoreCase))
                            ? x : new TrackShot(x.Role,null,registered.Code,x.Issued,x.Completed,0)).ToList();
                        saved = shots.Where(x=>x.Path!=null).ToArray();
                        string json = "{\"contract\":\"thor.capture-batch/1\",\"batchId\":" + Json.Quote(batch) + ",\"groupId\":" + Json.Quote(group) + ",\"burstId\":" + Json.Quote(request.BurstId) + ",\"mode\":" + Json.Quote(mode) + ",\"tracks\":[" + String.Join(",", shots.Select(x => "{\"role\":" + Json.Quote(x.Role) + ",\"file\":" + Json.Quote(x.Path == null ? null : System.IO.Path.GetFileName(x.Path)) + ",\"code\":" + Json.Quote(x.Code) + ",\"issuedMs\":" + ((long)x.Issued.TotalMilliseconds) + ",\"completedMs\":" + ((long)x.Completed.TotalMilliseconds) + "}")) + "]}";
                        try { if (saved.Length > 0) SaveNew(System.IO.Path.Combine(request.Directory, "thor-batch-" + batch + ".json"), Encoding.UTF8.GetBytes(json)); }
                        catch { code = "CAPTURE_METADATA_PENDING"; }
                    } else if (code == "OK") code = "CAPTURE_FAILED";
                    return new CaptureBatchResult(batch, group, mode, clean ? code : "CAPTURE_CLEANUP_REQUIRED", shots, clean);
                }
                catch (OperationCanceledException) { return new CaptureBatchResult(batch, null, mode, "CAPTURE_CANCELLED", shots, clean); }
                catch { return new CaptureBatchResult(batch, null, mode, "CAPTURE_FAILED", shots, clean); }
                finally { if (clean) lease.Dispose(); else lock (gate) retained.Add(lease); }
            }
        }
        public async Task<bool> StopAsync(TimeSpan timeout)
        {
            if (timeout.TotalMilliseconds < 1 || timeout.TotalSeconds > 60) throw new ArgumentOutOfRangeException("timeout");
            Task timer = Task.Delay(timeout); Task<CaptureBatchResult>[] work; Task cancellation; Task<bool> stopping, observing;
            lock (gate) {
                closed = true; work = active.ToArray();
                if (cancelWork == null) cancelWork = Task.Run(delegate { try { closing.Cancel(); } catch (AggregateException) { } });
                if (stopSourceWork == null || stopSourceWork.IsCompleted && (stopSourceWork.Status != TaskStatus.RanToCompletion || !stopSourceWork.Result)) stopSourceWork = Task.Run(() => source.StopAsync());
                if (stopForegroundWork == null || stopForegroundWork.IsCompleted && (stopForegroundWork.Status != TaskStatus.RanToCompletion || !stopForegroundWork.Result)) stopForegroundWork = stopObserver == null ? Task.FromResult(true) : Task.Run(() => stopObserver());
                cancellation = cancelWork; stopping = stopSourceWork; observing = stopForegroundWork;
            }
            Task all = Task.WhenAll(work); Task drained = Task.WhenAll(all, cancellation, stopping, observing);
            Task observation = drained.ContinueWith(done => { if (done.IsFaulted) GC.KeepAlive(done.Exception); }, TaskContinuationOptions.ExecuteSynchronously); GC.KeepAlive(observation);
            if (await Task.WhenAny(drained, timer).ConfigureAwait(false) != drained) return false;
            try {
                await drained.ConfigureAwait(false); if (!await stopping.ConfigureAwait(false) || !await observing.ConfigureAwait(false)) return false;
                Task[] tracks; lock (gate) tracks = trackCancellations.ToArray(); Task tracksDrained = Task.WhenAll(tracks);
                if (await Task.WhenAny(tracksDrained, timer).ConfigureAwait(false) != tracksDrained) return false;
                await tracksDrained.ConfigureAwait(false);
                lock (gate) { foreach (OperationLease lease in retained) lease.Dispose(); retained.Clear(); } return true;
            } catch { return false; }
        }
    }
}
