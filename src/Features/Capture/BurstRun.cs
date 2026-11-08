using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Features.Capture
{
    internal sealed class BurstPlan
    {
        public readonly TimeSpan Countdown, Interval;
        public readonly int Count;
        public readonly long MaxBytes, EstimatedBatchBytes;
        public BurstPlan(TimeSpan countdown, TimeSpan interval, int count, long maxBytes, long estimatedBatchBytes)
        {
            if (count < 1 || count > 500 || countdown < TimeSpan.Zero || countdown.TotalHours > 1 || interval.TotalMilliseconds < 50 || interval.TotalHours > 1
                || maxBytes < 1 || estimatedBatchBytes < 1 || estimatedBatchBytes > maxBytes / count) throw new ArgumentOutOfRangeException("burst limits");
            Countdown = countdown; Interval = interval; Count = count; MaxBytes = maxBytes; EstimatedBatchBytes = estimatedBatchBytes;
        }
    }
    internal sealed class BurstProgressEventArgs : EventArgs
    {
        public readonly string BurstId; public readonly int Completed; public readonly TimeSpan NextIn;
        public BurstProgressEventArgs(string id, int completed, TimeSpan next) { BurstId = id; Completed = completed; NextIn = next; }
    }
    internal sealed class BurstSummary
    {
        public readonly string BurstId, Code; public readonly long Bytes;
        public readonly ReadOnlyCollection<CaptureBatchResult> Batches;
        internal BurstSummary(string id, string code, long bytes, IList<CaptureBatchResult> values) { BurstId = id; Code = code; Bytes = bytes; Batches = new ReadOnlyCollection<CaptureBatchResult>(new List<CaptureBatchResult>(values)); }
    }
    internal sealed class BurstRun : IDisposable
    {
        readonly Func<string, long, CancellationToken, Task<CaptureBatchResult>> shoot; readonly BurstPlan plan;
        readonly CancellationTokenSource stop = new CancellationTokenSource(); int started;
        public readonly string BurstId = Guid.NewGuid().ToString("D");
        public event EventHandler<BurstProgressEventArgs> Progress;
        public BurstRun(Func<string, long, CancellationToken, Task<CaptureBatchResult>> capture, BurstPlan options)
        { if (capture == null || options == null) throw new ArgumentNullException("burst dependencies"); shoot = capture; plan = options; }
        public Task<BurstSummary> RunAsync(CancellationToken token)
        { if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Burst already started"); return Task.Run(() => Execute(token)); }
        async Task Wait(TimeSpan delay, int done, CancellationToken token)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed < delay) {
                token.ThrowIfCancellationRequested(); TimeSpan remaining = delay - clock.Elapsed;
                EventHandler<BurstProgressEventArgs> handler = Progress;
                if (handler != null) { try { handler(this, new BurstProgressEventArgs(BurstId, done, remaining)); } catch { } }
                await Task.Delay((int)Math.Max(1, Math.Min(100, remaining.TotalMilliseconds)), token).ConfigureAwait(false);
            }
        }
        async Task<BurstSummary> Execute(CancellationToken token)
        {
            List<CaptureBatchResult> batches = new List<CaptureBatchResult>(); long bytes = 0; string code = "OK";
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token)) {
                try {
                    await Wait(plan.Countdown, 0, linked.Token).ConfigureAwait(false);
                    for (int i = 0; i < plan.Count; i++) {
                        linked.Token.ThrowIfCancellationRequested();
                        if (plan.MaxBytes - bytes < plan.EstimatedBatchBytes) { code = "BURST_BYTE_LIMIT"; break; }
                        Stopwatch shot = Stopwatch.StartNew();
                        CaptureBatchResult batch = await shoot(BurstId, plan.MaxBytes - bytes, linked.Token).ConfigureAwait(false);
                        batches.Add(batch); bytes = checked(bytes + batch.Bytes);
                        if (!batch.CleanupComplete || batch.Code != "OK" || bytes > plan.MaxBytes) { code = bytes > plan.MaxBytes ? "BURST_BYTE_LIMIT" : batch.Code; break; }
                        // Delay the next slot when a shot is slow; never enqueue missed intervals.
                        if (i + 1 < plan.Count) await Wait(shot.Elapsed < plan.Interval ? plan.Interval - shot.Elapsed : plan.Interval, i + 1, linked.Token).ConfigureAwait(false);
                    }
                } catch (OperationCanceledException) { code = "BURST_CANCELLED"; }
                catch { code = "BURST_FAILED"; }
            }
            return new BurstSummary(BurstId, code, bytes, batches);
        }
        public void Dispose() { stop.Cancel(); }
    }
}
