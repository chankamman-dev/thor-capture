using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Backend
{
    internal enum BinaryProcessOutcome { Success, Failed, Cancelled, TimedOut, NotFound, StartDenied, OutputLimit, SinkFailed }
    internal sealed class BinaryProcessResult
    {
        public Guid JobId;
        public BinaryProcessOutcome Outcome;
        public int ExitCode = -1;
        public byte[] Output = new byte[0];
        public string Error = "";
        public bool Truncated, CleanupComplete;
        public long BytesWritten;
        public TimeSpan Duration;
    }
    internal sealed class OwnedBinaryJob
    {
        internal readonly Guid JobId = Guid.NewGuid();
        internal readonly object Gate = new object();
        internal readonly TaskCompletionSource<bool> Clean = new TaskCompletionSource<bool>();
        internal CancellationTokenSource Cancellation;
        internal Process Process;
        internal Task Stdout, Stderr, CancelWork;
        internal readonly object CancelGate = new object();
        internal readonly TaskCompletionSource<bool> Initialized = new TaskCompletionSource<bool>();
        internal CancellationTokenRegistration ParentCancellation;
        internal Action AfterCancellation;
        internal int StopRequested;
        internal int Released, SinkFailed, Limited, ErrorFailed;
        internal long Written;
        internal Task<BinaryProcessResult> Work;
        public Guid Id { get { return JobId; } }
        public Task<BinaryProcessResult> Completion { get { return Work; } }
        // Only completes true after the direct owned process and both pipe/sink workers finish.
        public Task<bool> Cleanup { get { return Clean.Task; } }
        internal bool IsStopping { get { return Interlocked.CompareExchange(ref StopRequested, 0, 0) != 0 || Cancellation.IsCancellationRequested; } }
        public void RequestStop()
        {
            lock (CancelGate)
            {
                if (Released != 0) return;
                Interlocked.Exchange(ref StopRequested, 1);
                if (CancelWork != null) return;
                CancelWork = Task.Run(delegate { try { Cancellation.Cancel(); } catch (AggregateException) { } catch (ObjectDisposedException) { } });
                CancelWork.ContinueWith(done => Initialized.Task.ContinueWith(ready => AfterCancellation()));
            }
        }
    }
    internal sealed class BinaryProcessRunner : IDisposable
    {
        private readonly object gate = new object();
        private readonly Dictionary<Guid, OwnedBinaryJob> jobs = new Dictionary<Guid, OwnedBinaryJob>();
        private readonly int errorLimit;
        private bool closed;
        public int ActiveCount { get { lock (gate) return jobs.Count; } }
        public BinaryProcessRunner(int maxErrorCharacters = 262144)
        { if (maxErrorCharacters < 1 || maxErrorCharacters > 1048576) throw new ArgumentOutOfRangeException("maxErrorCharacters"); errorLimit = maxErrorCharacters; }
        public OwnedBinaryJob Start(ProcessRequest request, Stream output, long maxBytes, TimeSpan timeout, CancellationToken token)
        {
            if (request == null || output == null) throw new ArgumentNullException("binary dependency");
            if (!output.CanWrite) throw new ArgumentException("Writable sink is required");
            if (maxBytes < 1) throw new ArgumentOutOfRangeException("maxBytes");
            if (timeout.TotalMilliseconds < 1 || timeout.TotalDays > 1) throw new ArgumentOutOfRangeException("timeout");
            lock (gate)
            {
                if (closed) throw new ObjectDisposedException("BinaryProcessRunner");
                OwnedBinaryJob job = new OwnedBinaryJob { Cancellation = new CancellationTokenSource() };
                job.AfterCancellation = () => job.Work.ContinueWith(done => TryRelease(job));
                job.ParentCancellation = token.Register(job.RequestStop);
                jobs.Add(job.Id, job);
                job.Work = Task.Run(() => { job.Initialized.Task.GetAwaiter().GetResult(); return Execute(job, request, output, maxBytes, timeout); });
                job.Initialized.TrySetResult(true);
                return job;
            }
        }
        public Task<BinaryProcessResult> RunAsync(ProcessRequest request, Stream output, long maxBytes, TimeSpan timeout, CancellationToken token)
        { return Start(request, output, maxBytes, timeout, token).Completion; }
        public async Task<BinaryProcessResult> RunBytesAsync(ProcessRequest request, int maxBytes, TimeSpan timeout, CancellationToken token)
        {
            if (maxBytes < 1 || maxBytes > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException("maxBytes");
            // Byte mode's internally owned sink cannot block or be closed by another consumer.
            MemoryStream buffer = new MemoryStream(Math.Min(maxBytes, 65536));
            OwnedBinaryJob job = null;
            try
            {
                job = Start(request, buffer, maxBytes, timeout, token);
                BinaryProcessResult result = await job.Completion.ConfigureAwait(false);
                if (result.CleanupComplete) result.Output = buffer.ToArray();
                else DisposeBufferAfterCleanup(job, buffer);
                return result;
            }
            finally { if (job == null || job.Cleanup.IsCompleted) buffer.Dispose(); }
        }
        private static void DisposeBufferAfterCleanup(OwnedBinaryJob job, MemoryStream buffer) { job.Cleanup.ContinueWith(done => buffer.Dispose()); }
        private static bool Exited(Process process)
        { try { return process == null || process.HasExited; } catch (InvalidOperationException) { return true; } }
        private static bool StopChild(OwnedBinaryJob job, int waitMilliseconds = 200)
        {
            lock (job.Gate)
            {
                if (job.Process == null) return true;
                try { if (!Exited(job.Process)) job.Process.Kill(); return job.Process.WaitForExit(waitMilliseconds); }
                catch (InvalidOperationException) { return true; }
                catch (Win32Exception) { return Exited(job.Process); }
            }
        }
        private bool TryRelease(OwnedBinaryJob job)
        {
            lock (job.Gate)
            {
                if (job.Released != 0) return true;
                if (!Exited(job.Process) || (job.Stdout != null && !job.Stdout.IsCompleted) || (job.Stderr != null && !job.Stderr.IsCompleted)) return false;
                lock (job.CancelGate)
                {
                    if (job.CancelWork != null && !job.CancelWork.IsCompleted) return false;
                    job.Released = 1;
                }
                job.ParentCancellation.Dispose();
                if (job.Process != null) { job.Process.Dispose(); job.Process = null; }
                job.Cancellation.Dispose();
            }
            lock (gate) jobs.Remove(job.Id);
            job.Clean.TrySetResult(true);
            return true;
        }
        private static void DrainOutput(OwnedBinaryJob job, Stream pipe, Stream sink, long maxBytes)
        {
            byte[] buffer = new byte[65536];
            try
            {
                int count;
                while (!job.IsStopping && (count = pipe.Read(buffer, 0, buffer.Length)) > 0)
                {
                    int keep = (int)Math.Min(count, maxBytes - Interlocked.Read(ref job.Written));
                    if (keep > 0) { sink.Write(buffer, 0, keep); Interlocked.Add(ref job.Written, keep); }
                    if (keep < count) { Interlocked.Exchange(ref job.Limited, 1); job.RequestStop(); break; }
                }
            }
            catch { if (!job.IsStopping) Interlocked.Exchange(ref job.SinkFailed, 1); job.RequestStop(); }
        }
        private BinaryProcessResult Execute(OwnedBinaryJob job, ProcessRequest request, Stream sink, long maxBytes, TimeSpan timeout)
        {
            Stopwatch clock = Stopwatch.StartNew();
            BinaryProcessResult result = new BinaryProcessResult { JobId = job.Id, Outcome = BinaryProcessOutcome.Failed };
            StringBuilder errors = new StringBuilder(); object errorGate = new object(); bool errorTruncated = false;
            Diagnostics.Shared.Record(job.Id, DiagnosticArea.BinaryProcess, DiagnosticCode.Started, DiagnosticState.Pending, TimeSpan.Zero, -1, false);
            try
            {
                if (job.IsStopping) { result.Outcome = BinaryProcessOutcome.Cancelled; return result; }
                lock (job.Gate)
                {
                    job.Process = new Process { StartInfo = request.StartInfo() };
                    request.Start(job.Process, timeout, job.Cancellation.Token);
                }
                Process process = job.Process;
                job.Stdout = Task.Run(() => DrainOutput(job, process.StandardOutput.BaseStream, sink, maxBytes));
                job.Stderr = Task.Run(delegate
                {
                    char[] chars = new char[4096];
                    try
                    {
                        int count;
                        while ((count = process.StandardError.Read(chars, 0, chars.Length)) > 0)
                            lock (errorGate)
                            {
                                int keep = Math.Min(count, errorLimit - errors.Length);
                                errors.Append(chars, 0, keep); if (keep < count) errorTruncated = true;
                            }
                    }
                    catch { Interlocked.Exchange(ref job.ErrorFailed, 1); }
                });
                while (!process.WaitForExit(20))
                {
                    if (job.IsStopping || clock.Elapsed >= timeout)
                    {
                        result.Outcome = job.Limited != 0 ? BinaryProcessOutcome.OutputLimit : job.SinkFailed != 0 ? BinaryProcessOutcome.SinkFailed :
                            job.IsStopping ? BinaryProcessOutcome.Cancelled : BinaryProcessOutcome.TimedOut;
                        job.RequestStop(); StopChild(job); break;
                    }
                }
                if (Exited(process))
                {
                    result.ExitCode = process.ExitCode;
                    if (result.Outcome == BinaryProcessOutcome.Failed && result.ExitCode == 0)
                        result.Outcome = BinaryProcessOutcome.Success;
                }
                Task.WhenAll(job.Stdout, job.Stderr).Wait(1000);
                if (job.Limited != 0) result.Outcome = BinaryProcessOutcome.OutputLimit;
                else if (job.SinkFailed != 0) result.Outcome = BinaryProcessOutcome.SinkFailed;
                else if (job.IsStopping && result.Outcome != BinaryProcessOutcome.TimedOut) result.Outcome = BinaryProcessOutcome.Cancelled;
                else if (job.ErrorFailed != 0 && result.Outcome == BinaryProcessOutcome.Success) result.Outcome = BinaryProcessOutcome.Failed;
                lock (errorGate) { result.Error = errors.ToString(); result.Truncated = errorTruncated || job.Limited != 0; }
                result.BytesWritten = Interlocked.Read(ref job.Written);
            }
            catch (Win32Exception error) { result.Outcome = error.NativeErrorCode == 2 || error.NativeErrorCode == 3 ? BinaryProcessOutcome.NotFound : BinaryProcessOutcome.StartDenied; }
            catch { result.Outcome = job.IsStopping ? BinaryProcessOutcome.Cancelled : BinaryProcessOutcome.Failed; }
            finally
            {
                if (!Exited(job.Process)) StopChild(job);
                result.CleanupComplete = TryRelease(job);
                result.Duration = clock.Elapsed;
                if (!result.CleanupComplete)
                {
                    // Retain ownership while an arbitrary consumer is blocked. Never dispose its sink.
                    Task.WhenAll(job.Stdout ?? Task.FromResult(0), job.Stderr ?? Task.FromResult(0)).ContinueWith(done => { if (!Exited(job.Process)) StopChild(job); TryRelease(job); });
                    if (result.Outcome == BinaryProcessOutcome.Success) result.Outcome = BinaryProcessOutcome.Failed;
                    result.Truncated = true;
                }
                DiagnosticState state; if (!Enum.TryParse<DiagnosticState>(result.Outcome.ToString(), out state)) state = result.Outcome == BinaryProcessOutcome.OutputLimit ? DiagnosticState.LimitExceeded : DiagnosticState.Failed;
                Diagnostics.Shared.Record(job.Id, DiagnosticArea.BinaryProcess, result.CleanupComplete ? DiagnosticCode.Finished : DiagnosticCode.CleanupPending, state, result.Duration, result.ExitCode, result.CleanupComplete);
            }
            return result;
        }
        public async Task<bool> AwaitCleanupAsync(Guid id, TimeSpan deadline, CancellationToken token)
        {
            if (deadline.TotalMilliseconds < 1 || deadline.TotalMilliseconds > 300000) throw new ArgumentOutOfRangeException("deadline");
            OwnedBinaryJob job; lock (gate) if (!jobs.TryGetValue(id, out job)) return true;
            Stopwatch clock = Stopwatch.StartNew();
            while (!job.Cleanup.IsCompleted && clock.Elapsed < deadline && !token.IsCancellationRequested)
            {
                if (job.Completion.IsCompleted) TryRelease(job);
                if (!job.Cleanup.IsCompleted) await Task.Delay(10).ConfigureAwait(false);
            }
            return job.Cleanup.Status == TaskStatus.RanToCompletion && job.Cleanup.Result;
        }
        public async Task<bool> StopAllAsync(TimeSpan deadline, CancellationToken token)
        {
            if (deadline.TotalMilliseconds < 1 || deadline.TotalMilliseconds > 300000) throw new ArgumentOutOfRangeException("deadline");
            Stopwatch clock = Stopwatch.StartNew();
            OwnedBinaryJob[] active;
            lock (gate) { closed = true; active = new List<OwnedBinaryJob>(jobs.Values).ToArray(); }
            foreach (OwnedBinaryJob job in active) job.RequestStop();
            while (ActiveCount > 0 && clock.Elapsed < deadline && !token.IsCancellationRequested)
            {
                foreach (OwnedBinaryJob job in active) if (job.Completion.IsCompleted) { StopChild(job, 0); TryRelease(job); }
                await Task.Delay(10).ConfigureAwait(false);
            }
            return ActiveCount == 0;
        }
        public void Dispose() { Observe(StopAllAsync(TimeSpan.FromSeconds(2), CancellationToken.None)); }
        private static void Observe(Task work)
        {
            work.ContinueWith(done =>
            {
                if (done.IsFaulted && done.Exception != null) Diagnostics.Shared.Record(Guid.NewGuid(), DiagnosticArea.BinaryProcess,
                    DiagnosticCode.CleanupPending, DiagnosticState.Failed, TimeSpan.Zero, -1, false);
            });
        }
    }
}
