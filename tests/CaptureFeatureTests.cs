using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;
using ThorCapture.Media;
using ThorCapture.Features.Capture;

namespace ThorCapture
{
    internal static class CaptureFeatureTests
    {
        sealed class Observer : IAdbClient
        {
            internal bool Denied;
            public Task<DeviceCommandResult> RunAsync(DeviceSession session, AdbQuery query, TimeSpan timeout, CancellationToken token)
            { return Task.FromResult(new DeviceCommandResult { SessionId = session.Id, Outcome = Denied ? CommandOutcome.Failed : CommandOutcome.Success,
                Output = "Display #0\n Resumed: ActivityRecord{fixture.upper/.Main}\nDisplay #1\n Resumed: ActivityRecord{fixture.lower/.Main}", CleanupComplete = true }); }
        }
        sealed class Source : IScreenCaptureSource
        {
            internal bool Parallel = true, DenyLower, Late, Pending, Stopped, ThrowBeforeAck;
            internal int Calls, Active, Maximum;
            public bool SupportsParallel { get { return Parallel; } }
            public async Task<ScreenBytes> ReadAsync(DeviceSession session, DisplayTarget display, TimeSpan timeout, CancellationToken token)
            {
                Interlocked.Increment(ref Calls); int now = Interlocked.Increment(ref Active); Maximum = Math.Max(Maximum, now);
                try { await Task.Delay(Late ? 180 : 40, Late ? CancellationToken.None : token);
                    if (ThrowBeforeAck) throw new IOException("isolated provider failure");
                    if (DenyLower && display.Role == "lower") return new ScreenBytes(null, "CAPTURE_READ_DENIED", true);
                    using (Bitmap image = new Bitmap(display.Width, display.Height)) using (MemoryStream memory = new MemoryStream()) {
                        image.Save(memory, ImageFormat.Png); return new ScreenBytes(memory.ToArray(), "OK", !Pending);
                    }
                } finally { Interlocked.Decrement(ref Active); }
            }
            public Task<bool> StopAsync() { Stopped = true; return Task.FromResult(!Pending); }
        }
        internal static void Run() { Task.Run((Func<Task>)Checks).GetAwaiter().GetResult(); }
        static string Root() { string path = Path.Combine(Tests.Root, "capture-core-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        static async Task Checks()
        {
            string root = Root();
            using (DeviceSessionRegistry sessions = new DeviceSessionRegistry()) using (OperationGate operations = new OperationGate(sessions))
            using (MediaRepository repository = new MediaRepository(root, Root())) {
                await repository.RefreshAsync(CancellationToken.None);
                DeviceSession session = sessions.Open("capture-offline", DeviceCapabilities.Capture, new[] { new DisplayTarget(0,"1","upper",12,8), new DisplayTarget(1,"2","lower",12,8) });
                Source source = new Source(); CaptureService service = new CaptureService(source, operations, repository);
                CaptureRequest request = new CaptureRequest(session, session.Displays, root, true, TimeSpan.FromSeconds(1), 1024 * 1024);
                CaptureBatchResult result = await service.RunAsync(request, CancellationToken.None);
                Tests.Check(result.Code == "OK" && result.Tracks.Count == 2 && source.Maximum == 2, "capture launches supported independent screen reads concurrently");
                Tests.Check(result.GroupId != null && repository.GetSnapshot().Groups.Single().Tracks.Count == 2, "capture registers both tracks as one stable media group");
                string first = Path.GetFileName(result.Tracks[0].Path);
                Tests.Check(File.Exists(Path.Combine(root, first.Replace("-upper-", "-lower-"))), "capture preserves legacy paired filename convention");
                Tests.Check(result.Tracks.All(x => x.Issued <= x.Completed && x.Bytes > 0), "capture records command issue and completion times per track");
                Tests.Check(File.Exists(Path.Combine(root, "thor-batch-" + result.BatchId + ".json")), "capture writes durable batch identity and timing metadata");
                MediaLibraryService watched = new MediaLibraryService(30,1000);
                await watched.SwitchAsync(root,repository.DataRoot,CancellationToken.None);
                CaptureBatchResult atomic = await service.RunAsync(request,CancellationToken.None);
                System.Diagnostics.Stopwatch seen = System.Diagnostics.Stopwatch.StartNew();
                while (watched.Snapshot.Media.FindGroup(atomic.GroupId)==null && seen.ElapsedMilliseconds<4000) await Task.Delay(20);
                Tests.Check(watched.Snapshot.Media.FindGroup(atomic.GroupId)!=null && watched.Snapshot.Media.FindGroup(atomic.GroupId).Tracks.Count==2,
                    "live filesystem watcher sees one preassigned dual-screen group after atomic publication");
                Tests.Check(await watched.StopAsync(TimeSpan.FromSeconds(2))==MediaLibraryOutcome.Success,"watcher drains alongside capture publication without holding the device lease");
                source.Parallel = false; result = await service.RunAsync(request, CancellationToken.None);
                Tests.Check(result.Mode == "sequential-fallback" && result.Tracks.Count == 2, "unverified parallel capability uses explicit sequential fallback");
                source.DenyLower = true; result = await service.RunAsync(request, CancellationToken.None);
                Tests.Check(result.Tracks.Count(x => x.Path != null) == 1 && result.Tracks.Any(x => x.Code == "CAPTURE_READ_DENIED"), "one screen rejection preserves the successful original");
                Tests.Check(repository.GetSnapshot().FindGroup(result.GroupId).Tracks.Count == 1, "partial capture forms a valid one-track group");
                int before = source.Calls;
                sessions.Open("other", DeviceCapabilities.Capture);
                // A Capture lease blocks another capture even when its source is idle.
                OperationLease held; operations.TryAcquire(session, OperationKind.Capture, out held);
                result = await service.RunAsync(request, CancellationToken.None); held.Dispose();
                Tests.Check(result.Code == "BUSY" && source.Calls == before, "busy operation gate starts zero screen reads");
                source.DenyLower = false;
                result = await service.RunAsync(new CaptureRequest(session, session.Displays, root, false, TimeSpan.FromSeconds(1), 1), CancellationToken.None);
                Tests.Check(result.Tracks.All(x => x.Code == "CAPTURE_BYTE_LIMIT" && x.Path == null), "byte budget rejects images before filesystem publication");
                source.DenyLower = false; source.Late = true;
                result = await service.RunAsync(new CaptureRequest(session, session.Displays, root, true, TimeSpan.FromMilliseconds(100), 1024 * 1024), CancellationToken.None);
                Tests.Check(result.Tracks.All(x => x.Path == null && x.Code == "CAPTURE_TIMEOUT"), "late provider bytes after the deadline never publish");
                using (CancellationTokenSource cancel = new CancellationTokenSource()) { cancel.Cancel(); before = source.Calls;
                    result = await service.RunAsync(request, cancel.Token); Tests.Check(result.Code == "CAPTURE_CANCELLED" && source.Calls == before, "pre-cancelled capture starts zero provider calls"); }
                source.Late = false; source.Pending = true;
                result = await service.RunAsync(request, CancellationToken.None);
                Tests.Check(!result.CleanupComplete && operations.ActiveCount == 1 && result.Tracks.All(x => x.Path == null), "unacknowledged child cleanup retains device lease and rejects bytes");
                Tests.Check(!await service.StopAsync(TimeSpan.FromSeconds(1)) && operations.ActiveCount == 1, "failed stop cannot release pending capture ownership");
                source.Pending = false; Tests.Check(await service.StopAsync(TimeSpan.FromSeconds(1)) && operations.ActiveCount == 0, "later cleanup acknowledgement releases retained ownership");
                Source failedSource = new Source { ThrowBeforeAck = true };
                CaptureService failedService = new CaptureService(failedSource, operations, repository);
                result = await failedService.RunAsync(request, CancellationToken.None);
                Tests.Check(!result.CleanupComplete && operations.ActiveCount == 1 && result.Tracks.All(x => x.Path == null), "provider exception without cleanup acknowledgement retains capture lease");
                Tests.Check(await failedService.StopAsync(TimeSpan.FromSeconds(1)) && operations.ActiveCount == 0, "explicit provider stop acknowledgement releases a failed capture lease");
                Observer observer = new Observer(); CaptureService observed = new CaptureService(new Source(), operations, repository, observer, () => Task.FromResult(true));
                CaptureRequest identified = new CaptureRequest(session, session.Displays, root, false, TimeSpan.FromSeconds(1), 1024*1024, null, true);
                result = await observed.RunAsync(identified,CancellationToken.None);
                Tests.Check(repository.GetSnapshot().FindGroup(result.GroupId).Tracks.All(x => x.Package == "fixture." + (x.Role == ScreenRole.Upper ? "upper" : "lower")), "optional foreground query preserves each display package in metadata and media model");
                observer.Denied = true; result = await observed.RunAsync(identified,CancellationToken.None);
                Tests.Check(result.Code == "OK" && result.Tracks.All(x => CaptureMetadata.Load(x.Path).Status == "query-failed"), "denied foreground observation still saves capture with explicit query failure");
                Tests.Check(await observed.StopAsync(TimeSpan.FromSeconds(1)),"capture drains injected foreground ownership during close");
            }
            Tests.Reject(() => new BurstPlan(TimeSpan.Zero,TimeSpan.FromMilliseconds(50),501,1000,1), "burst rejects an unbounded count");
            Tests.Reject(() => new BurstPlan(TimeSpan.Zero,TimeSpan.FromMilliseconds(50),10,100,11), "burst rejects an estimate beyond its byte budget");
            int shots = 0; Guid burst = Guid.Empty;
            using (BurstRun run = new BurstRun(async (id,budget,token) => { shots++; burst = Guid.Parse(id); await Task.Delay(70,token);
                return new CaptureBatchResult(Guid.NewGuid().ToString("D"),null,"fixture","OK",new TrackShot[0],true); },new BurstPlan(TimeSpan.Zero,TimeSpan.FromMilliseconds(50),3,1000,1))) {
                BurstSummary summary = await run.RunAsync(CancellationToken.None);
                Tests.Check(summary.Code == "OK" && shots == 3 && burst != Guid.Empty, "slow finite burst completes sequentially under one burst identity");
                Tests.Reject(() => run.RunAsync(CancellationToken.None), "burst cannot accidentally run twice");
            }
            shots = 0;
            using (BurstRun run = new BurstRun((id,budget,token) => { shots++; return Task.FromResult(new CaptureBatchResult(id,null,"fixture","DISCONNECTED",new TrackShot[0],true)); },new BurstPlan(TimeSpan.Zero,TimeSpan.FromMilliseconds(50),3,1000,1))) {
                BurstSummary summary = await run.RunAsync(CancellationToken.None);
                Tests.Check(shots == 1 && summary.Code == "DISCONNECTED", "burst stops on disconnected or failed capture without scheduling another");
            }
            using (BurstRun run = new BurstRun((id,budget,token) => { shots++; return Task.FromResult(new CaptureBatchResult(id,null,"fixture","OK",new TrackShot[0],true)); },new BurstPlan(TimeSpan.FromSeconds(1),TimeSpan.FromMilliseconds(50),3,1000,1))) {
                using (CancellationTokenSource cancel = new CancellationTokenSource()) { cancel.Cancel(); int before = shots; BurstSummary summary = await run.RunAsync(cancel.Token);
                    Tests.Check(summary.Code == "BURST_CANCELLED" && shots == before, "cancelled countdown cannot start a capture"); }
            }
        }
    }
}
