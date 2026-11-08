using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Backend
{
    internal enum OwnedComponentKind { TextProcesses, BinaryProcesses, DeviceClient, Media, Library, Other }
    internal enum ShutdownOutcome { Clean, Pending, Cancelled }
    internal sealed class ShutdownResult
    {
        public readonly ShutdownOutcome Outcome;
        public readonly Guid[] PendingIds;
        public bool CleanupComplete { get { return Outcome == ShutdownOutcome.Clean; } }
        internal ShutdownResult(ShutdownOutcome outcome, Guid[] pending) { Outcome = outcome; PendingIds = pending; }
    }
    // Registration never auto-releases. Stop returning true is a cleanup acknowledgment contract.
    internal sealed class ShutdownCoordinator
    {
        private sealed class Component
        {
            internal readonly Guid Id = Guid.NewGuid();
            internal OwnedComponentKind Kind;
            internal Func<CancellationToken, Task<bool>> Stop;
            internal CancellationTokenSource Cancellation;
            internal Task<bool> Work;
            internal Task CancelWork;
            internal bool Clean;
            internal bool FailureRecorded;
        }
        private readonly object gate = new object();
        private readonly List<Component> components = new List<Component>();
        private bool closing;
        private readonly SemaphoreSlim serialize = new SemaphoreSlim(1, 1);
        public bool IsClosing { get { lock (gate) return closing; } }
        public int PendingCount { get { lock (gate) { int count = 0; foreach (Component c in components) if (!c.Clean) count++; return count; } } }
        public Guid Register(OwnedComponentKind kind, Func<CancellationToken, Task<bool>> stop)
        {
            if (stop == null || !Enum.IsDefined(typeof(OwnedComponentKind), kind)) throw new ArgumentException("Invalid owned component");
            lock (gate)
            {
                if (closing) throw new InvalidOperationException("Shutdown has begun");
                Component component = new Component { Kind = kind, Stop = stop }; components.Add(component); return component.Id;
            }
        }
        public async Task<ShutdownResult> StopAsync(TimeSpan deadline, CancellationToken token)
        {
            if (deadline.TotalMilliseconds < 1 || deadline.TotalMilliseconds > 300000) throw new ArgumentOutOfRangeException("deadline");
            Stopwatch clock = Stopwatch.StartNew();
            // Concurrent callers get their own deadline, never an unbounded wait on the first caller.
            while (!await serialize.WaitAsync(20).ConfigureAwait(false))
                if (token.IsCancellationRequested || clock.Elapsed >= deadline) return Snapshot(token.IsCancellationRequested ? ShutdownOutcome.Cancelled : ShutdownOutcome.Pending);
            try
            {
                Component[] all;
                HashSet<Guid> attempted = new HashSet<Guid>();
                lock (gate) { closing = true; all = components.ToArray(); }
                foreach (Component component in all)
                {
                    lock (gate)
                    {
                        StartIfReady(component, attempted);
                    }
                }
                while (true)
                {
                    bool clean = true;
                    lock (gate)
                    {
                        foreach (Component component in all)
                        {
                            ObserveFailure(component);
                            if (!component.Clean && component.Work != null && component.Work.Status == TaskStatus.RanToCompletion && component.Work.Result && (component.CancelWork == null || component.CancelWork.IsCompleted)) MarkClean(component);
                            if (clock.Elapsed < deadline && !token.IsCancellationRequested) StartIfReady(component, attempted);
                            if (!component.Clean) clean = false;
                        }
                    }
                    if (clean) return Snapshot(ShutdownOutcome.Clean);
                    if (token.IsCancellationRequested || clock.Elapsed >= deadline)
                    {
                        foreach (Component component in all)
                            if (!component.Clean && component.Cancellation != null && (component.CancelWork == null || component.CancelWork.IsCompleted))
                                component.CancelWork = Task.Run(delegate { try { component.Cancellation.Cancel(); } catch (AggregateException) { } });
                        return Snapshot(token.IsCancellationRequested ? ShutdownOutcome.Cancelled : ShutdownOutcome.Pending);
                    }
                    await Task.Delay(10).ConfigureAwait(false);
                }
            }
            finally { serialize.Release(); }
        }
        private static void StartIfReady(Component component, HashSet<Guid> attempted)
        {
            if (component.Clean || attempted.Contains(component.Id) || component.Work != null && !component.Work.IsCompleted) return;
            if (component.CancelWork != null && !component.CancelWork.IsCompleted) return;
            if (component.Work != null && component.Work.Status == TaskStatus.RanToCompletion && component.Work.Result) { MarkClean(component); return; }
            ObserveFailure(component);
            if (component.Cancellation != null) component.Cancellation.Dispose();
            component.Cancellation = new CancellationTokenSource(); component.CancelWork = null;
            component.FailureRecorded = false; attempted.Add(component.Id);
            CancellationToken attempt = component.Cancellation.Token;
            component.Work = Task.Run(async delegate { return await component.Stop(attempt).ConfigureAwait(false); });
        }
        private static void MarkClean(Component component) { component.Clean = true; if (component.Cancellation != null) { component.Cancellation.Dispose(); component.Cancellation = null; } }
        private static void ObserveFailure(Component component)
        {
            if (component.Work != null && component.Work.IsFaulted && component.Work.Exception != null && !component.FailureRecorded)
            {
                component.FailureRecorded = true;
                Diagnostics.Shared.Record(component.Id, DiagnosticArea.Shutdown, DiagnosticCode.CleanupPending, DiagnosticState.Failed, TimeSpan.Zero, -1, false);
            }
        }
        private ShutdownResult Snapshot(ShutdownOutcome requested)
        {
            List<Guid> pending = new List<Guid>();
            lock (gate) foreach (Component component in components) if (!component.Clean) pending.Add(component.Id);
            ShutdownOutcome outcome = pending.Count == 0 ? ShutdownOutcome.Clean : requested;
            Diagnostics.Shared.Record(Guid.NewGuid(), DiagnosticArea.Shutdown, outcome == ShutdownOutcome.Clean ? DiagnosticCode.Closed : DiagnosticCode.CleanupPending,
                outcome == ShutdownOutcome.Clean ? DiagnosticState.Success : outcome == ShutdownOutcome.Cancelled ? DiagnosticState.Cancelled : DiagnosticState.Pending, TimeSpan.Zero, -1, pending.Count == 0);
            return new ShutdownResult(outcome, pending.ToArray());
        }
    }
}
