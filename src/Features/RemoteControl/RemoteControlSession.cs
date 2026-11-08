using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture.Features.RemoteControl
{
    internal sealed class RemoteControlSession : IDisposable
    {
        private readonly object sync = new object();
        private readonly OperationLease lease;
        private readonly IControlTransport transport;
        private readonly int capacity;
        private readonly ControlSupport support;
        private readonly List<ControlCommand> queue = new List<ControlCommand>();
        private readonly Dictionary<int, DisplayTarget> touches = new Dictionary<int, DisplayTarget>();
        private readonly HashSet<int> keys = new HashSet<int>();
        private readonly SemaphoreSlim signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly CancellationTokenRegistration registration;
        private readonly Task worker;
        private long sequence;
        private bool enabled, stopping, cleanup, releaseConfirmed;
        private Task<bool> closeRetry;
        public readonly ControlBinding Binding;
        public bool Enabled { get { lock (sync) return enabled && !stopping && lease.IsCurrent; } }
        public bool CleanupComplete { get { lock (sync) return cleanup; } }
        public bool ReleasesConfirmed { get { lock (sync) return releaseConfirmed; } }
        public int QueueDepth { get { lock (sync) return queue.Count; } }
        private RemoteControlSession(OperationLease owner, IControlTransport channel, long revision, int maxQueued)
        {
            lease = owner; transport = channel; support = channel.Support; capacity = maxQueued; Binding = new ControlBinding(owner.Session, revision);
            registration = lease.Token.Register(RequestStop);
            worker = Task.Run((Func<Task>)RunAsync);
        }
        public static ControlStartOutcome TryStart(DeviceSession device, OperationGate gate, IControlTransport channel,
            bool explicitEnable, long mappingRevision, int maxQueued, out RemoteControlSession session)
        {
            session = null;
            if (!explicitEnable) return ControlStartOutcome.Disabled;
            if (device == null || gate == null || channel == null) throw new ArgumentNullException("control dependency");
            if (mappingRevision < 0 || maxQueued < 4 || maxQueued > 1024) throw new ArgumentOutOfRangeException("control limits");
            if (channel.Protocol != ControlBinding.Protocol || channel.Support == ControlSupport.Unknown || ((int)channel.Support & ~7) != 0 || device.Displays.Length == 0)
                return ControlStartOutcome.Unsupported;
            OperationLease owner;
            LeaseOutcome acquired = gate.TryAcquire(device, OperationKind.RemoteControl, out owner);
            if (acquired != LeaseOutcome.Acquired) return (ControlStartOutcome)Enum.Parse(typeof(ControlStartOutcome), acquired.ToString());
            session = new RemoteControlSession(owner, channel, mappingRevision, maxQueued); return ControlStartOutcome.Starting;
        }
        public ControlEnqueueOutcome Touch(ControlAction action, int pointer, string role, int x, int y, long revision)
        {
            lock (sync)
            {
                if (!CanInput(revision)) return ControlEnqueueOutcome.Disabled;
                DisplayTarget display = Binding.Display(role), held;
                if ((support & ControlSupport.Touch) == 0 || display == null || pointer < 0 || pointer > 9 || x < 0 || y < 0 || x >= display.Width || y >= display.Height
                    || (action != ControlAction.TouchDown && action != ControlAction.TouchMove && action != ControlAction.TouchUp)) return ControlEnqueueOutcome.Invalid;
                bool active = touches.TryGetValue(pointer, out held);
                if ((action == ControlAction.TouchDown && active) || (action != ControlAction.TouchDown && (!active || !ReferenceEquals(display, held)))) return ControlEnqueueOutcome.Invalid;
                ControlEnqueueOutcome result = Append(action, display, pointer, x, y, 0);
                if (result == ControlEnqueueOutcome.Queued || result == ControlEnqueueOutcome.Coalesced)
                { if (action == ControlAction.TouchDown) touches.Add(pointer, display); if (action == ControlAction.TouchUp) touches.Remove(pointer); }
                return result;
            }
        }
        public ControlEnqueueOutcome Key(bool down, int androidKey, bool navigation, long revision)
        {
            lock (sync)
            {
                if (!CanInput(revision)) return ControlEnqueueOutcome.Disabled;
                bool nav = androidKey == 3 || androidKey == 4 || androidKey == 187;
                if (androidKey < 1 || androidKey > 288 || (androidKey >= 96 && androidKey <= 110) || (androidKey >= 188 && androidKey <= 203) || (navigation && !nav) || (support & (navigation ? ControlSupport.Navigation : ControlSupport.Keyboard)) == 0
                    || (down ? keys.Contains(androidKey) : !keys.Contains(androidKey))) return ControlEnqueueOutcome.Invalid;
                ControlEnqueueOutcome result = Append(down ? ControlAction.KeyDown : ControlAction.KeyUp, null, 0, 0, 0, androidKey);
                if (result == ControlEnqueueOutcome.Queued) { if (down) keys.Add(androidKey); else keys.Remove(androidKey); }
                return result;
            }
        }
        private bool CanInput(long revision)
        {
            if (revision != Binding.MappingRevision || !lease.IsCurrent) { RequestStop(); return false; }
            return enabled && !stopping;
        }
        private ControlEnqueueOutcome Append(ControlAction action, DisplayTarget display, int pointer, int x, int y, int key)
        {
            ControlCommand command = new ControlCommand(Binding, ++sequence, action, display, pointer, x, y, key);
            if (action == ControlAction.TouchMove)
            {
                // Never cross down/up barriers for this pointer.
                for (int i = queue.Count - 1; i >= 0; i--)
                    if (queue[i].Display == display && queue[i].PointerId == pointer)
                    { if (queue[i].Action == ControlAction.TouchMove) { queue[i] = new ControlCommand(Binding, queue[i].Sequence, action, display, pointer, x, y, key); return ControlEnqueueOutcome.Coalesced; } break; }
            }
            // Releases evict obsolete moves, but do not jump ahead of their own down.
            if (action == ControlAction.TouchUp || action == ControlAction.KeyUp)
                queue.RemoveAll(c => c.Action == ControlAction.TouchMove);
            if (queue.Count >= capacity)
            {
                if (action == ControlAction.TouchMove) return ControlEnqueueOutcome.Congested;
                RequestStop(); return ControlEnqueueOutcome.Congested;
            }
            queue.Add(command); signal.Release(); return ControlEnqueueOutcome.Queued;
        }
        // Focus/capture loss, gallery, mapping changes and explicit disable all end this session.
        // A fresh explicit TryStart is required; reconnect cannot re-enable it.
        public void RequestStop()
        {
            lock (sync)
            {
                if (stopping) return;
                stopping = true; enabled = false; queue.Clear(); touches.Clear(); keys.Clear(); signal.Release();
            }
            // Avoid running transport cancellation callbacks on the UI/session thread.
            Task.Run(() => { try { cancellation.Cancel(); } catch (AggregateException) { } });
        }
        private async Task RunAsync()
        {
            bool opened = false;
            try
            {
                using (CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
                using (startup.Token.Register(RequestStop))
                {
                    startup.CancelAfter(5000);
                    opened = await transport.OpenAsync(Binding, startup.Token).ConfigureAwait(false);
                }
                lock (sync) enabled = opened && !stopping && lease.IsCurrent;
                while (Enabled)
                {
                    await signal.WaitAsync(cancellation.Token).ConfigureAwait(false);
                    ControlCommand command = null;
                    lock (sync) { if (stopping || !lease.IsCurrent) break; if (queue.Count > 0) { command = queue[0]; queue.RemoveAt(0); } }
                    if (command == null) continue;
                    ControlAcknowledgment ack;
                    using (CancellationTokenSource send = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
                    using (send.Token.Register(RequestStop))
                    {
                        send.CancelAfter(500);
                        ack = await transport.SendAsync(command, send.Token).ConfigureAwait(false);
                    }
                    if (ack == null || !ack.Matches(command) || !lease.IsCurrent) break;
                }
            }
            catch (Exception) { /* No raw transport error enters diagnostics. */ }
            {
                RequestStop();
                if (opened)
                {
                    ControlCommand release = new ControlCommand(Binding, Interlocked.Increment(ref sequence), ControlAction.ReleaseAll, null, 0, 0, 0, 0);
                    try
                    {
                        // Release attempts have their own bounded cancellation window.
                        using (CancellationTokenSource deadline = new CancellationTokenSource(500))
                        {
                            Task<ControlAcknowledgment> attempt = transport.SendAsync(release, deadline.Token);
                            // Still await transport ownership even if cancellation is ignored: StopAsync reports pending.
                            ControlAcknowledgment ack = await attempt.ConfigureAwait(false);
                            lock (sync) releaseConfirmed = lease.IsCurrent && ack != null && ack.Matches(release);
                        }
                    }
                    catch (Exception) { }
                }
                await CloseAsync().ConfigureAwait(false);
            }
        }
        private async Task<bool> CloseAsync()
        {
            bool closed = false;
            try { closed = await transport.CloseAsync().ConfigureAwait(false); } catch (Exception) { }
            if (closed)
            {
                lock (sync) cleanup = true;
                registration.Dispose(); lease.Dispose();
            }
            return closed;
        }
        public async Task<bool> StopAsync(TimeSpan deadline, CancellationToken token)
        {
            if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException("deadline");
            RequestStop(); Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed < deadline && !token.IsCancellationRequested)
            {
                if (CleanupComplete) return true;
                if (worker.IsCompleted)
                {
                    lock (sync) if (closeRetry == null || closeRetry.IsCompleted) closeRetry = Task.Run((Func<Task<bool>>)CloseAsync);
                }
                await Task.Delay(10).ConfigureAwait(false);
            }
            return CleanupComplete;
        }
        public void Dispose() { RequestStop(); }
    }
}
