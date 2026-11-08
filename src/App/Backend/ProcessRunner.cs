using System;
using System.Collections;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Backend
{
    internal enum ProcessOutcome { Success, Failed, Cancelled, TimedOut, NotFound, StartDenied }

    internal sealed class ProcessRequest
    {
        private readonly string[] arguments;
        private readonly Dictionary<string, string> environment;
        public readonly string Executable, Label;
        private readonly string startSerial;
        public ProcessRequest(string executable, string label, string[] args, IDictionary<string, string> env = null, string startupDevice = null)
        {
            if (string.IsNullOrWhiteSpace(executable) || executable.IndexOf('\0') >= 0) throw new ArgumentException("Invalid executable");
            if (!Regex.IsMatch(label ?? "", @"\A[A-Za-z0-9_.-]{1,64}\z")) throw new ArgumentException("Invalid operation label");
            if (args == null) throw new ArgumentNullException("args");
            foreach (string arg in args) if (arg == null || arg.IndexOf('\0') >= 0) throw new ArgumentException("Invalid argument");
            Executable = executable; Label = label; arguments = (string[])args.Clone();
            if (startupDevice != null) { DeviceSessionRegistry.ValidateSerial(startupDevice); if (args.Length < 2 || args[0] != "-s" || args[1] != startupDevice) throw new ArgumentException("Process target mismatch"); }
            startSerial = startupDevice;
            environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (env != null)
                foreach (KeyValuePair<string, string> entry in env)
                {
                    if (string.IsNullOrEmpty(entry.Key) || entry.Key.IndexOfAny(new[] { '=', '\0' }) >= 0
                        || (entry.Value != null && entry.Value.IndexOf('\0') >= 0)) throw new ArgumentException("Invalid environment override");
                    environment[entry.Key] = entry.Value;
                }
        }
        internal bool Start(Process process, TimeSpan timeout, CancellationToken token)
        { return DeviceTransportGate.Execute(startSerial, timeout, token, () => process.Start()); }
        internal ProcessStartInfo StartInfo()
        {
            StringBuilder argv = new StringBuilder();
            foreach (string argument in arguments) { if (argv.Length > 0) argv.Append(' '); argv.Append(Quote(argument)); }
            ProcessStartInfo info = new ProcessStartInfo(Executable, argv.ToString()) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            if (environment.Count > 0)
            {
                StringDictionary child;
                try { child = info.EnvironmentVariables; }
                catch (ArgumentException)
                {
                    // Framework initializes its case-insensitive dictionary with Add(). An inherited
                    // block containing both Path and PATH throws after the dictionary has been allocated.
                    child = info.EnvironmentVariables; child.Clear();
                    foreach (DictionaryEntry inherited in Environment.GetEnvironmentVariables())
                        child[(string)inherited.Key] = Environment.GetEnvironmentVariable((string)inherited.Key);
                }
                foreach (KeyValuePair<string, string> entry in environment)
                    if (entry.Value == null) child.Remove(entry.Key); else child[entry.Key] = entry.Value;
            }
            return info;
        }
        internal static string Quote(string value)
        {
            StringBuilder quoted = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') quoted.Append('\\', slashes * 2 + 1);
                else quoted.Append('\\', slashes);
                quoted.Append(c); slashes = 0;
            }
            quoted.Append('\\', slashes * 2); quoted.Append('"'); return quoted.ToString();
        }
    }

    internal sealed class ProcessResult
    {
        public Guid JobId;
        public ProcessOutcome Outcome;
        public int ExitCode = -1;
        public string Output = "", Error = "";
        public string FailureType = "";
        public bool Truncated, CleanupComplete = true;
        public TimeSpan Duration;
    }

    // Short text jobs only. Binary streams and remote PIDs stay with their specialized owners.
    internal sealed class ProcessRunner : IDisposable
    {
        private sealed class Job
        {
            internal readonly Guid Id = Guid.NewGuid();
            internal readonly object Sync = new object();
            internal CancellationTokenSource Cancellation;
            internal Process Process;
            internal Task<ProcessResult> Task;
            internal Task Stdout, Stderr;
            internal int Released;
        }
        private sealed class BoundedText
        {
            private readonly int limit;
            private readonly StringBuilder text = new StringBuilder();
            private readonly object gate = new object();
            internal bool Truncated, ReadFailed;
            internal BoundedText(int max) { limit = max; }
            internal string Text { get { lock (gate) return text.ToString(); } }
            internal void Drain(StreamReader reader)
            {
                char[] buffer = new char[4096];
                try
                {
                    int count;
                    while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                        lock (gate)
                        {
                            int keep = Math.Min(count, limit - text.Length);
                            text.Append(buffer, 0, keep); if (keep < count) Truncated = true;
                        }
                }
                catch (IOException) { lock (gate) { ReadFailed = true; } }
                catch (ObjectDisposedException) { lock (gate) { ReadFailed = true; } }
            }
        }

        private readonly object gate = new object();
        private readonly List<Job> jobs = new List<Job>();
        private readonly int outputLimit;
        private bool closed;
        public int ActiveCount { get { lock (gate) return jobs.Count; } }
        public ProcessRunner(int maxOutputCharacters = 262144)
        {
            if (maxOutputCharacters < 1 || maxOutputCharacters > 1048576) throw new ArgumentOutOfRangeException("maxOutputCharacters");
            outputLimit = maxOutputCharacters;
        }
        public Task<ProcessResult> RunAsync(ProcessRequest request, TimeSpan timeout, CancellationToken token)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (timeout.TotalMilliseconds < 1 || timeout.TotalMilliseconds > 300000) throw new ArgumentOutOfRangeException("timeout");
            lock (gate)
            {
                if (closed) throw new ObjectDisposedException("ProcessRunner");
                Job job = new Job { Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token) };
                jobs.Add(job);
                job.Task = Task.Run(() => Execute(job, request, timeout));
                return job.Task;
            }
        }
        private static bool Exited(Process process)
        {
            try { return process.HasExited; } catch (InvalidOperationException) { return true; }
        }
        private static bool StopChild(Job job)
        {
            lock (job.Sync)
            {
                if (job.Process == null) return true;
                try
                {
                    if (!Exited(job.Process)) job.Process.Kill();
                    return job.Process.WaitForExit(2000);
                }
                catch (InvalidOperationException) { return true; }
                catch (Win32Exception) { return Exited(job.Process); }
            }
        }
        private void ReleaseJob(Job job)
        {
            if ((job.Stdout != null && !job.Stdout.IsCompleted) || (job.Stderr != null && !job.Stderr.IsCompleted)) return;
            if (Interlocked.Exchange(ref job.Released, 1) != 0) return;
            lock (job.Sync)
            {
                if (job.Process != null) { job.Process.Dispose(); job.Process = null; }
            }
            lock (gate) jobs.Remove(job);
            job.Cancellation.Dispose();
        }
        private ProcessResult Execute(Job job, ProcessRequest request, TimeSpan timeout)
        {
            Stopwatch clock = Stopwatch.StartNew();
            ProcessResult result = new ProcessResult { JobId = job.Id, Outcome = ProcessOutcome.Failed };
            Process process = null;
            try
            {
                if (job.Cancellation.IsCancellationRequested) { result.Outcome = ProcessOutcome.Cancelled; return result; }
                process = new Process { StartInfo = request.StartInfo() };
                lock (job.Sync) { request.Start(process, timeout, job.Cancellation.Token); job.Process = process; }
                BoundedText output = new BoundedText(outputLimit), error = new BoundedText(outputLimit);
                Task stdout = job.Stdout = Task.Run(() => output.Drain(process.StandardOutput));
                Task stderr = job.Stderr = Task.Run(() => error.Drain(process.StandardError));
                bool aborted = false;
                while (!process.WaitForExit(20))
                {
                    if (job.Cancellation.IsCancellationRequested || clock.Elapsed >= timeout)
                    {
                        aborted = true;
                        result.Outcome = job.Cancellation.IsCancellationRequested ? ProcessOutcome.Cancelled : ProcessOutcome.TimedOut;
                        result.CleanupComplete = StopChild(job); break;
                    }
                }
                bool drained = Task.WaitAll(new[] { stdout, stderr }, 1500);
                result.CleanupComplete = result.CleanupComplete && drained;
                result.Output = output.Text; result.Error = error.Text;
                result.Truncated = !drained || output.Truncated || error.Truncated || output.ReadFailed || error.ReadFailed;
                if (!aborted)
                {
                    result.ExitCode = process.ExitCode;
                    result.Outcome = job.Cancellation.IsCancellationRequested ? ProcessOutcome.Cancelled
                        : (result.ExitCode == 0 && !output.ReadFailed && !error.ReadFailed) ? ProcessOutcome.Success : ProcessOutcome.Failed;
                }
                if (!drained) { process.StandardOutput.Close(); process.StandardError.Close(); }
            }
            catch (Win32Exception error) { result.FailureType = error.GetType().Name; result.Outcome = error.NativeErrorCode == 2 || error.NativeErrorCode == 3 ? ProcessOutcome.NotFound : ProcessOutcome.StartDenied; }
            catch (Exception error) { result.FailureType = error.GetType().Name; result.Outcome = job.Cancellation.IsCancellationRequested ? ProcessOutcome.Cancelled : ProcessOutcome.Failed; }
            finally
            {
                if (process != null && job.Process == null) process.Dispose();
                if (job.Process != null && !Exited(job.Process)) result.CleanupComplete = StopChild(job);
                result.CleanupComplete = result.CleanupComplete && (job.Stdout == null || job.Stdout.IsCompleted) && (job.Stderr == null || job.Stderr.IsCompleted);
                if (result.CleanupComplete) ReleaseJob(job);
                else Task.WhenAll(job.Stdout ?? System.Threading.Tasks.Task.FromResult(0), job.Stderr ?? System.Threading.Tasks.Task.FromResult(0)).ContinueWith(done => { if (StopChild(job)) ReleaseJob(job); });
                result.Duration = clock.Elapsed;
                DiagnosticState state; if (!Enum.TryParse<DiagnosticState>(result.Outcome.ToString(), out state)) state = DiagnosticState.Failed;
                Diagnostics.Shared.Record(job.Id, DiagnosticArea.TextProcess, result.CleanupComplete ? DiagnosticCode.Finished : DiagnosticCode.CleanupPending, state, result.Duration, result.ExitCode, result.CleanupComplete);
                Log.Info("Backend job=" + job.Id.ToString("N") + " operation=TextProcess outcome=" + result.Outcome
                    + " exit=" + result.ExitCode + " cleanup=" + result.CleanupComplete + " elapsedMs=" + clock.ElapsedMilliseconds);
            }
            return result;
        }
        public Task StopAllAsync()
        {
            Job[] active;
            lock (gate) { closed = true; active = jobs.ToArray(); }
            foreach (Job job in active)
                try { job.Cancellation.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { Log.Warn("Process cancellation callback failed"); }
            return Task.Run(async delegate
            {
                foreach (Job job in active)
                {
                    await job.Task.ConfigureAwait(false);
                    bool retained;
                    lock (gate) retained = jobs.Contains(job);
                    if (retained && StopChild(job)) ReleaseJob(job);
                }
            });
        }
        // Nonblocking. Call and await StopAllAsync before claiming shutdown has completed.
        public void Dispose() { StopAllAsync(); }
    }
}
