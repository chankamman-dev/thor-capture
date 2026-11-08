using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Backend
{
    internal enum AdbQuery { Properties, Displays, InputNodes, Activities, RecordHelp }
    internal enum CommandOutcome { Success, Failed, Cancelled, TimedOut, NotFound, StartDenied, Disconnected, Unsupported, Busy, Closed, CleanupRequired }
    internal sealed class DeviceCommandResult
    {
        public Guid SessionId, JobId;
        public CommandOutcome Outcome;
        public string Output = "", Error = "";
        public string FailureType = "";
        public int ExitCode = -1;
        public bool Truncated, CleanupComplete = true;
        public TimeSpan Duration;
    }
    internal interface IAdbClient
    {
        Task<DeviceCommandResult> RunAsync(DeviceSession session, AdbQuery query, TimeSpan timeout, CancellationToken token);
    }

    // No root, arbitrary shell, file write or injected input is exposed by this initial adapter.
    internal sealed class ReadOnlyAdbClient : IAdbClient
    {
        private readonly object gate = new object();
        private readonly ProcessRunner runner;
        private readonly OperationGate operations;
        private readonly string executable;
        private readonly List<Task<DeviceCommandResult>> running = new List<Task<DeviceCommandResult>>();
        private readonly List<OperationLease> retained = new List<OperationLease>();
        private bool closed;
        public ReadOnlyAdbClient(string adbPath, ProcessRunner processRunner, OperationGate operationGate)
        {
            if (string.IsNullOrWhiteSpace(adbPath)) throw new ArgumentException("Invalid adb path");
            if (processRunner == null || operationGate == null) throw new ArgumentNullException("backend dependency");
            executable = adbPath; runner = processRunner; operations = operationGate;
        }
        internal static string[] Arguments(DeviceSession session, AdbQuery query)
        {
            if (session == null) throw new ArgumentNullException("session");
            DeviceSessionRegistry.ValidateSerial(session.Serial);
            string[] command;
            switch (query)
            {
                case AdbQuery.Properties: command = new[] { "getprop" }; break;
                case AdbQuery.Displays: command = new[] { "dumpsys", "display" }; break;
                case AdbQuery.InputNodes: command = new[] { "getevent", "-lp" }; break;
                case AdbQuery.Activities: command = new[] { "dumpsys", "activity", "activities" }; break;
                case AdbQuery.RecordHelp: command = new[] { "screenrecord", "--help" }; break;
                default: throw new ArgumentOutOfRangeException("query");
            }
            List<string> args = new List<string> { "-s", session.Serial, "shell" }; args.AddRange(command); return args.ToArray();
        }
        private static CommandOutcome Outcome(ProcessOutcome outcome)
        {
            switch (outcome)
            {
                case ProcessOutcome.Success: return CommandOutcome.Success;
                case ProcessOutcome.Cancelled: return CommandOutcome.Cancelled;
                case ProcessOutcome.TimedOut: return CommandOutcome.TimedOut;
                case ProcessOutcome.NotFound: return CommandOutcome.NotFound;
                case ProcessOutcome.StartDenied: return CommandOutcome.StartDenied;
                default: return CommandOutcome.Failed;
            }
        }
        public Task<DeviceCommandResult> RunAsync(DeviceSession session, AdbQuery query, TimeSpan timeout, CancellationToken token)
        {
            string[] args = Arguments(session, query);
            if (timeout.TotalMilliseconds < 1 || timeout.TotalMilliseconds > 300000) throw new ArgumentOutOfRangeException("timeout");
            lock (gate)
            {
                if (closed) throw new ObjectDisposedException("ReadOnlyAdbClient");
                Task<DeviceCommandResult> work = Execute(session, query, args, timeout, token); running.Add(work);
                work.ContinueWith(done => { lock (gate) running.Remove(done); }, TaskContinuationOptions.ExecuteSynchronously);
                return work;
            }
        }
        private async Task<DeviceCommandResult> Execute(DeviceSession session, AdbQuery query, string[] args, TimeSpan timeout, CancellationToken token)
        {
            OperationLease lease;
            LeaseOutcome acquire = operations.TryAcquire(session, OperationKind.Inspect, out lease);
            DeviceCommandResult reply = new DeviceCommandResult { SessionId = session.Id };
            if (acquire != LeaseOutcome.Acquired)
            {
                reply.Outcome = acquire == LeaseOutcome.Busy ? CommandOutcome.Busy : acquire == LeaseOutcome.Closed ? CommandOutcome.Closed
                    : acquire == LeaseOutcome.Unsupported ? CommandOutcome.Unsupported : CommandOutcome.Disconnected;
                return reply;
            }
            bool release = true;
            try
            {
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, lease.Token))
                {
                    ProcessResult process = await Task.Run(() => DeviceTransportGate.Execute(session.Serial, timeout, linked.Token,
                        () => { ProcessResult result = runner.RunAsync(new ProcessRequest(executable, "adb." + query, args), timeout, linked.Token).GetAwaiter().GetResult();
                            if (!result.CleanupComplete) DeviceTransportGate.Block(session.Serial); return result; })).ConfigureAwait(false);
                    reply.JobId = process.JobId; reply.ExitCode = process.ExitCode; reply.Truncated = process.Truncated;
                    reply.Duration = process.Duration; reply.CleanupComplete = process.CleanupComplete;
                    reply.FailureType = process.FailureType;
                    if (!process.CleanupComplete)
                    {
                        release = false; lock (gate) retained.Add(lease); reply.Outcome = CommandOutcome.CleanupRequired;
                    }
                    else if (!lease.IsCurrent) reply.Outcome = CommandOutcome.Disconnected;
                    else
                    {
                        reply.Outcome = Outcome(process.Outcome); reply.Output = process.Output; reply.Error = process.Error;
                    }
                }
            }
            catch (OperationCanceledException) { reply.Outcome = lease.IsCurrent ? CommandOutcome.Cancelled : CommandOutcome.Disconnected; }
            catch (TimeoutException) { reply.Outcome = CommandOutcome.TimedOut; }
            catch (InvalidOperationException) { reply.Outcome = CommandOutcome.CleanupRequired; }
            finally { if (release) lease.Dispose(); }
            return reply;
        }
        public async Task<bool> StopAsync()
        {
            Task<DeviceCommandResult>[] replies;
            lock (gate) { closed = true; replies = running.ToArray(); }
            Task cleanup = runner.StopAllAsync();
            Exception failure = null;
            try { await Task.WhenAll(replies).ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            await cleanup.ConfigureAwait(false);
            if (runner.ActiveCount != 0) return false;
            OperationLease[] leases;
            lock (gate) { leases = retained.ToArray(); retained.Clear(); }
            foreach (OperationLease lease in leases) { DeviceTransportGate.Unblock(lease.Session.Serial); lease.Dispose(); }
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return true;
        }
    }
}
