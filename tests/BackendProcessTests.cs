using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture
{
    internal static class BackendProcessTests
    {
        private static ProcessRequest Request(string command, string label = "private_serial_and_token")
        { return new ProcessRequest(typeof(Tests).Assembly.Location, label, new[] { command, "secret-user-path-token" }); }
        internal static void Run()
        {
            Task.Run(() => Binary()).GetAwaiter().GetResult();
            Task.Run(() => Shutdown()).GetAwaiter().GetResult();
            Task.Run(() => DiagnosticChecks()).GetAwaiter().GetResult();
        }
        private static async Task Binary()
        {
            using (BinaryProcessRunner runner = new BinaryProcessRunner(1000))
            {
                BinaryProcessResult complete = await runner.RunBytesAsync(Request("bp-bytes"), 1024, TimeSpan.FromSeconds(3), CancellationToken.None);
                bool same = complete.Output.Length == 1024;
                for (int i = 0; i < complete.Output.Length; i++) same &= complete.Output[i] == (byte)i;
                Tests.Check(same && complete.Outcome == BinaryProcessOutcome.Success && complete.CleanupComplete && runner.ActiveCount == 0, "binary byte mode preserves all byte values and cleanup acknowledgment");
                Tests.Reject(() => runner.RunBytesAsync(Request("bp-bytes"), 64 * 1024 * 1024 + 1, TimeSpan.FromSeconds(3), CancellationToken.None).GetAwaiter().GetResult(), "binary byte arrays reject budgets above 64 MiB before start");
                BinaryProcessResult limited = await runner.RunBytesAsync(Request("bp-flood"), 32768, TimeSpan.FromSeconds(3), CancellationToken.None);
                Tests.Check(limited.Outcome == BinaryProcessOutcome.OutputLimit && limited.Output.Length == 32768 && limited.Truncated && limited.CleanupComplete, "binary output flood fails explicitly at exact caller byte budget");
                using (CountingStream sink = new CountingStream())
                {
                    BinaryProcessResult flood = await runner.RunAsync(Request("bp-flood"), sink, 4 * 1024 * 1024, TimeSpan.FromSeconds(5), CancellationToken.None);
                    Tests.Check(flood.Outcome == BinaryProcessOutcome.Success && flood.BytesWritten == 2 * 1024 * 1024 && sink.Count == flood.BytesWritten && sink.MaxChunk <= 65536 && flood.Error.Length == 1000 && flood.Truncated,
                        "stream mode drains binary and stderr floods using bounded chunks and retains no body array");
                    Tests.Check(flood.Output.Length == 0 && sink.CanWrite, "stream target remains caller owned and open");
                }
                BinaryProcessResult fail = await runner.RunBytesAsync(Request("bp-fail"), 4096, TimeSpan.FromSeconds(3), CancellationToken.None);
                Tests.Check(fail.Outcome == BinaryProcessOutcome.Failed && fail.ExitCode == 19 && fail.CleanupComplete, "nonzero binary producer cannot masquerade as successful bytes");
                BinaryProcessResult timeout = await runner.RunBytesAsync(Request("bp-hang"), 4096, TimeSpan.FromMilliseconds(120), CancellationToken.None);
                Tests.Check(timeout.Outcome == BinaryProcessOutcome.TimedOut && timeout.CleanupComplete && timeout.Duration.TotalSeconds < 3, "binary timeout returns bounded owned cleanup result");
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    cancel.CancelAfter(100);
                    BinaryProcessResult cancelled = await runner.RunBytesAsync(Request("bp-hang"), 4096, TimeSpan.FromSeconds(5), cancel.Token);
                    Tests.Check(cancelled.Outcome == BinaryProcessOutcome.Cancelled && cancelled.CleanupComplete, "binary inflight cancellation stops only the owned producer");
                }
                using (FailingStream failedSink = new FailingStream())
                {
                    BinaryProcessResult failed = await runner.RunAsync(Request("bp-flood"), failedSink, 4 * 1024 * 1024, TimeSpan.FromSeconds(3), CancellationToken.None);
                    Tests.Check(failed.Outcome == BinaryProcessOutcome.SinkFailed && failed.CleanupComplete, "failing consumer produces typed outcome without raw exception diagnostics");
                }
                BinaryProcessResult missing = await runner.RunBytesAsync(new ProcessRequest(Path.Combine(Tests.Root, "absent-process.exe"), "missing", new string[0]), 1024, TimeSpan.FromSeconds(1), CancellationToken.None);
                Tests.Check(missing.Outcome == BinaryProcessOutcome.NotFound && missing.CleanupComplete, "binary missing executable releases owned startup reservation");
            }
            using (BinaryProcessRunner runner = new BinaryProcessRunner())
            using (BlockedStream sink = new BlockedStream())
            using (Process foreign = new Process { StartInfo = Request("bp-hang").StartInfo() })
            {
                foreign.Start();
                try
                {
                    OwnedBinaryJob job = runner.Start(Request("bp-flood"), sink, 4 * 1024 * 1024, TimeSpan.FromMilliseconds(120), CancellationToken.None);
                    Tests.Check(sink.Entered.WaitOne(2000), "blocking consumer is reached by actual owned binary child");
                    BinaryProcessResult pending = await job.Completion;
                    Tests.Check(!pending.CleanupComplete && runner.ActiveCount == 1 && !job.Cleanup.IsCompleted && !foreign.HasExited,
                        "unfinished sink worker retains ownership and does not kill a foreign process");
                    Stopwatch watch = Stopwatch.StartNew();
                    Tests.Check(!await runner.StopAllAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None) && watch.ElapsedMilliseconds < 600,
                        "binary shutdown deadline reports blocked cleanup instead of releasing its job");
                    Tests.Check(!await runner.AwaitCleanupAsync(job.Id, TimeSpan.FromMilliseconds(80), CancellationToken.None), "cleanup acknowledgment stays pending while consumer is blocked");
                    sink.Release.Set();
                    Tests.Check(await runner.AwaitCleanupAsync(job.Id, TimeSpan.FromSeconds(3), CancellationToken.None) && runner.ActiveCount == 0 && await job.Cleanup,
                        "released consumer eventually permits cleanup acknowledgment and resource release");
                    Tests.Reject(() => runner.Start(Request("bp-bytes"), sink, 1024, TimeSpan.FromSeconds(1), CancellationToken.None), "closed binary runner cannot restart after shutdown");
                }
                finally { sink.Release.Set(); if (!foreign.HasExited) foreign.Kill(); foreign.WaitForExit(2000); }
            }
            using (ProcessRunner text = new ProcessRunner())
            {
                await text.RunAsync(Request("bp-fail"), TimeSpan.FromSeconds(3), CancellationToken.None);
                Tests.Check(!File.ReadAllText(Log.CurrentFile).Contains("private_serial_and_token") && !File.ReadAllText(Log.CurrentFile).Contains("secret-user-path-token"),
                    "existing text runner no longer logs arbitrary request labels or arguments");
            }
        }
        private static async Task Shutdown()
        {
            ShutdownCoordinator coordinator = new ShutdownCoordinator();
            TaskCompletionSource<bool> stuck = new TaskCompletionSource<bool>();
            coordinator.Register(OwnedComponentKind.Media, token => stuck.Task);
            coordinator.Register(OwnedComponentKind.Library, token => Task.FromResult(true));
            Stopwatch watch = Stopwatch.StartNew();
            ShutdownResult first = await coordinator.StopAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
            Tests.Check(first.Outcome == ShutdownOutcome.Pending && first.PendingIds.Length == 1 && coordinator.IsClosing && watch.ElapsedMilliseconds < 600,
                "global shutdown deadline leaves hung owned component registered and blocked");
            Tests.Reject(() => coordinator.Register(OwnedComponentKind.Other, token => Task.FromResult(true)), "shutdown seals new owned component registration");
            stuck.SetResult(true);
            Tests.Check((await coordinator.StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).CleanupComplete && coordinator.PendingCount == 0,
                "late cleanup acknowledgment can finish repeated shutdown without duplicate stop");
            ShutdownCoordinator retry = new ShutdownCoordinator(); int attempts = 0;
            retry.Register(OwnedComponentKind.DeviceClient, token => Task.FromResult(Interlocked.Increment(ref attempts) > 1));
            Tests.Check(!(await retry.StopAsync(TimeSpan.FromMilliseconds(60), CancellationToken.None)).CleanupComplete &&
                (await retry.StopAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None)).CleanupComplete && attempts == 2,
                "negative cleanup acknowledgment remains pending and can retry explicitly");
            ShutdownCoordinator blockingCancel = new ShutdownCoordinator();
            using (ManualResetEvent cancelEntered = new ManualResetEvent(false))
            using (ManualResetEvent cancelRelease = new ManualResetEvent(false))
            {
                ShutdownCoordinator delayedRetry = new ShutdownCoordinator(); int retryAttempts = 0;
                delayedRetry.Register(OwnedComponentKind.Other, token => {
                    int number = Interlocked.Increment(ref retryAttempts);
                    if (number == 1) token.Register(() => { cancelEntered.Set(); cancelRelease.WaitOne(); });
                    return Task.FromResult(number > 1);
                });
                Tests.Check(!(await delayedRetry.StopAsync(TimeSpan.FromMilliseconds(80),CancellationToken.None)).CleanupComplete && retryAttempts == 1 && cancelEntered.WaitOne(1000),
                    "one shutdown call does not repeatedly retry a negative acknowledgement");
                Task<ShutdownResult> second = delayedRetry.StopAsync(TimeSpan.FromSeconds(1),CancellationToken.None);
                await Task.Delay(40); cancelRelease.Set();
                Tests.Check((await second).CleanupComplete && retryAttempts == 2,
                    "explicit retry resumes inside its deadline after the previous cancellation callback drains");
            }
            using (ManualResetEvent entered = new ManualResetEvent(false))
            using (ManualResetEvent release = new ManualResetEvent(false))
            {
                blockingCancel.Register(OwnedComponentKind.Other, token =>
                {
                    token.Register(() => { entered.Set(); release.WaitOne(); });
                    return Task.FromResult(false);
                });
                watch.Restart();
                ShutdownResult result = await blockingCancel.StopAsync(TimeSpan.FromMilliseconds(70), CancellationToken.None);
                Tests.Check(!result.CleanupComplete && watch.ElapsedMilliseconds < 600 && entered.WaitOne(1000), "blocking cancellation callback cannot defeat the global shutdown deadline");
                release.Set();
            }
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                ShutdownCoordinator cancelled = new ShutdownCoordinator();
                TaskCompletionSource<bool> noAck = new TaskCompletionSource<bool>();
                cancelled.Register(OwnedComponentKind.Other, token => noAck.Task);
                cancel.CancelAfter(50);
                Tests.Check((await cancelled.StopAsync(TimeSpan.FromSeconds(1), cancel.Token)).Outcome == ShutdownOutcome.Cancelled && cancelled.PendingCount == 1,
                    "cancelled shutdown preserves unacknowledged component ownership");
                noAck.SetResult(true); await cancelled.StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            }
            ShutdownCoordinator faults = new ShutdownCoordinator();
            faults.Register(OwnedComponentKind.Other, token => { throw new IOException("private path token"); });
            Tests.Check(!(await faults.StopAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None)).CleanupComplete && faults.PendingCount == 1,
                "throwing stop adapter is observed and cannot falsely acknowledge clean shutdown");
            using (ManualResetEvent released = new ManualResetEvent(false))
            {
                ShutdownCoordinator synchronous = new ShutdownCoordinator();
                synchronous.Register(OwnedComponentKind.Media, token => { released.WaitOne(); return Task.FromResult(true); });
                watch.Restart();
                ShutdownResult pending = await synchronous.StopAsync(TimeSpan.FromMilliseconds(60), CancellationToken.None);
                Tests.Check(!pending.CleanupComplete && watch.ElapsedMilliseconds < 600, "synchronously hung cleanup delegate cannot block global deadline");
                released.Set(); await synchronous.StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            }
        }
        private static async Task DiagnosticChecks()
        {
            Diagnostics ring = new Diagnostics(3); Guid id = Guid.NewGuid();
            for (int i = 0; i < 10; i++) ring.Record(id, DiagnosticArea.BinaryProcess, DiagnosticCode.Finished, DiagnosticState.Success, TimeSpan.FromMilliseconds(i), i, true);
            DiagnosticEntry[] snapshot = ring.Snapshot(3);
            Tests.Check(snapshot.Length == 3 && snapshot[0].Sequence == 8 && snapshot[2].Sequence == 10, "structured diagnostic ring retains only bounded newest entries");
            Tests.Check(Array.TrueForAll(typeof(DiagnosticEntry).GetFields(), field => field.FieldType != typeof(string)), "diagnostic entry schema exposes no free-text field to hold secrets");
            Tests.Reject(() => ring.Record(id, (DiagnosticArea)999, DiagnosticCode.Finished, DiagnosticState.Success, TimeSpan.Zero, 0, true), "unknown diagnostic labels cannot be passed through as raw messages");
            string file = Path.Combine(Tests.Root, "safe-diagnostic-" + Guid.NewGuid().ToString("N") + ".tsv");
            Tests.Check(await ring.ExportAsync(file, 2, CancellationToken.None) == DiagnosticExportOutcome.Success && File.ReadAllLines(file).Length == 3 && new FileInfo(file).Length < 1048576,
                "diagnostic export has bounded schema and selected entry count");
            Tests.Check(await ring.ExportAsync(file, 2, CancellationToken.None) == DiagnosticExportOutcome.AlreadyExists, "diagnostic export never replaces an existing file");
            string body = File.ReadAllText(file);
            Tests.Check(!body.Contains("secret-user-path-token") && !body.Contains("private_serial_and_token") && !body.Contains(Tests.Root), "exported diagnostics contain no paths labels argv or producer output");
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); string cancelled = file + ".cancelled";
                Tests.Check(await ring.ExportAsync(cancelled, 3, cancel.Token) == DiagnosticExportOutcome.Cancelled && !File.Exists(cancelled), "cancelled diagnostic export publishes no partial file");
            }
        }
        internal static int FakeAdb(string command, string mode, string dir)
        {
            if (command.StartsWith("bp-hang", StringComparison.Ordinal)) { Thread.Sleep(10000); return 0; }
            if (command.StartsWith("bp-fail", StringComparison.Ordinal)) { Console.Error.Write("secret-user-path-token"); return 19; }
            if (command.StartsWith("bp-bytes", StringComparison.Ordinal))
            { byte[] bytes = new byte[1024]; for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)i; Console.OpenStandardOutput().Write(bytes, 0, bytes.Length); return 0; }
            if (command.StartsWith("bp-flood", StringComparison.Ordinal))
            {
                Task error = Task.Run(() => Console.Error.Write(new string('e', 2 * 1024 * 1024)));
                byte[] bytes = new byte[65536]; for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;
                Stream output = Console.OpenStandardOutput(); for (int i = 0; i < 32; i++) output.Write(bytes, 0, bytes.Length);
                error.Wait(); return 0;
            }
            return Tests.NotHandled;
        }
        private class CountingStream : Stream
        {
            public long Count; public int MaxChunk;
            public override bool CanRead { get { return false; } } public override bool CanSeek { get { return false; } } public override bool CanWrite { get { return true; } }
            public override long Length { get { return Count; } } public override long Position { get { return Count; } set { throw new NotSupportedException(); } }
            public override void Flush() { } public override int Read(byte[] b, int o, int c) { throw new NotSupportedException(); }
            public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); } public override void SetLength(long n) { throw new NotSupportedException(); }
            public override void Write(byte[] b, int o, int c) { Count += c; MaxChunk = Math.Max(MaxChunk, c); }
        }
        private sealed class FailingStream : CountingStream { public override void Write(byte[] b, int o, int c) { throw new IOException("private path token user serial"); } }
        private sealed class BlockedStream : CountingStream
        {
            public readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false);
            public override void Write(byte[] b, int o, int c) { Entered.Set(); Release.WaitOne(); base.Write(b, o, c); }
            protected override void Dispose(bool disposing) { Release.Set(); base.Dispose(disposing); }
        }
    }
}
