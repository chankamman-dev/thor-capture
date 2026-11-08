using System;
using System.Collections.Generic;
using System.Threading;

namespace ThorCapture.Backend
{
    internal enum OperationKind { Inspect, Capture, Preview, Recording, InputObservation, RemoteControl, CocoonRead, CocoonWrite }
    internal enum LeaseOutcome { Acquired, Disconnected, Unsupported, Busy, Closed }

    internal sealed class OperationLease : IDisposable
    {
        private readonly OperationGate gate;
        private readonly CancellationTokenSource cancellation;
        private int released;
        public readonly Guid JobId = Guid.NewGuid();
        public readonly DeviceSession Session;
        public readonly OperationKind Kind;
        public readonly CancellationToken Token;
        public bool IsCurrent { get { return released == 0 && gate.IsCurrent(this); } }
        internal OperationLease(OperationGate owner, DeviceSession session, OperationKind kind)
        {
            gate = owner; Session = session; Kind = kind;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Token); Token = cancellation.Token;
        }
        internal void Cancel()
        {
            try { cancellation.Cancel(); }
            catch (AggregateException) { Log.Warn("Operation cancellation callback failed"); }
            catch (ObjectDisposedException) { }
        }
        // Dispose only after the operation's owned resources have actually stopped.
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            Cancel(); gate.Release(this); cancellation.Dispose(); Session.Release();
        }
    }

    internal sealed class OperationGate : IDisposable
    {
        private readonly object gate = new object();
        private readonly DeviceSessionRegistry registry;
        private readonly List<OperationLease> leases = new List<OperationLease>();
        private bool closed;
        public OperationGate(DeviceSessionRegistry sessions) { if (sessions == null) throw new ArgumentNullException("sessions"); registry = sessions; }
        public int ActiveCount { get { lock (gate) return leases.Count; } }

        internal static DeviceCapabilities Required(OperationKind kind)
        {
            switch (kind)
            {
                case OperationKind.Inspect: return DeviceCapabilities.None;
                case OperationKind.Capture: return DeviceCapabilities.Capture;
                case OperationKind.Preview: return DeviceCapabilities.VideoPreview;
                case OperationKind.Recording: return DeviceCapabilities.Recording;
                case OperationKind.InputObservation: return DeviceCapabilities.InputObservation;
                case OperationKind.RemoteControl: return DeviceCapabilities.RemoteControl;
                case OperationKind.CocoonRead: return DeviceCapabilities.CocoonRead;
                case OperationKind.CocoonWrite: return DeviceCapabilities.CocoonWrite;
                default: throw new ArgumentOutOfRangeException("kind");
            }
        }
        internal static bool Conflicts(OperationKind a, OperationKind b)
        {
            if (a == OperationKind.CocoonWrite || b == OperationKind.CocoonWrite) return true;
            if ((a == OperationKind.Preview || a == OperationKind.Recording) && (b == OperationKind.Preview || b == OperationKind.Recording)) return true;
            if (a == b && (a == OperationKind.Capture || a == OperationKind.InputObservation || a == OperationKind.RemoteControl)) return true;
            return false;
        }
        public LeaseOutcome TryAcquire(DeviceSession session, OperationKind kind, out OperationLease lease)
        {
            DeviceCapabilities required = Required(kind); lease = null;
            lock (gate)
            {
                if (closed) return LeaseOutcome.Closed;
                if (!registry.IsCurrent(session)) return LeaseOutcome.Disconnected;
                if ((session.Capabilities & required) != required) return LeaseOutcome.Unsupported;
                foreach (OperationLease active in leases)
                    if (active.Session.Serial == session.Serial && Conflicts(active.Kind, kind)) return LeaseOutcome.Busy;
                if (!session.Hold()) return LeaseOutcome.Disconnected;
                try { lease = new OperationLease(this, session, kind); leases.Add(lease); }
                catch { session.Release(); throw; }
                return LeaseOutcome.Acquired;
            }
        }
        internal bool IsCurrent(OperationLease lease)
        {
            lock (gate) return !closed && leases.Contains(lease) && registry.IsCurrent(lease.Session) && !lease.Token.IsCancellationRequested;
        }
        internal void Release(OperationLease lease) { lock (gate) leases.Remove(lease); }
        public void Dispose()
        {
            OperationLease[] active;
            lock (gate) { if (closed) return; closed = true; active = leases.ToArray(); }
            // Do not return resources until each owner completes its own cleanup and disposes its lease.
            foreach (OperationLease lease in active) lease.Cancel();
        }
    }
}
