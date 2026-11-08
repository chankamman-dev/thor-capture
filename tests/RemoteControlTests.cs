using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;
using ThorCapture.Features.RemoteControl;

namespace ThorCapture
{
    internal static class RemoteControlTests
    {
        private sealed class Fake : IControlTransport
        {
            public string Protocol { get; set; }
            public ControlSupport Support { get; set; }
            public readonly List<ControlCommand> Commands = new List<ControlCommand>();
            public readonly TaskCompletionSource<bool> Block = new TaskCompletionSource<bool>();
            public bool BlockFirst, WrongAck, ThrowSend, Closed = true, Opened = true;
            public int Opens, Closes, Sends;
            public ControlBinding Binding;
            public Fake() { Protocol = ControlBinding.Protocol; Support = (ControlSupport)7; }
            public Task<bool> OpenAsync(ControlBinding binding, CancellationToken token) { Binding = binding; Interlocked.Increment(ref Opens); return Task.FromResult(Opened); }
            public async Task<ControlAcknowledgment> SendAsync(ControlCommand c, CancellationToken token)
            {
                int number = Interlocked.Increment(ref Sends);
                lock (Commands) Commands.Add(c);
                if (BlockFirst && number == 1) await Block.Task.ConfigureAwait(false);
                if (ThrowSend && c.Action != ControlAction.ReleaseAll) throw new InvalidOperationException("not logged fixture secret");
                if (WrongAck && c.Action != ControlAction.ReleaseAll) return new ControlAcknowledgment(Guid.NewGuid(), c.Generation, c.MappingRevision, c.Sequence, true);
                return ControlAcknowledgment.For(c);
            }
            public Task<bool> CloseAsync() { Interlocked.Increment(ref Closes); return Task.FromResult(Closed); }
            public ControlCommand[] Snapshot() { lock (Commands) return Commands.ToArray(); }
        }
        private static DeviceSession Device(DeviceSessionRegistry r, DeviceCapabilities cap = DeviceCapabilities.RemoteControl)
        { return r.Open("offline", cap, new DisplayTarget(0, "111", "upper", 1920, 1080), new DisplayTarget(6, "222", "lower", 1240, 1080)); }
        private static async Task Wait(Func<bool> condition)
        { for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(5).ConfigureAwait(false); if (!condition()) throw new Exception("remote fixture deadline"); }
        private static async Task<RemoteControlSession> Start(DeviceSession d, OperationGate gate, Fake fake, int size = 8)
        {
            RemoteControlSession s;
            Tests.Check(RemoteControlSession.TryStart(d, gate, fake, true, 10, size, out s) == ControlStartOutcome.Starting, "remote explicitly enabled acquires existing operation lease");
            await Wait(() => s.Enabled); return s;
        }
        internal static void Run() { Task.Run((Func<Task>)Checks).GetAwaiter().GetResult(); }
        private static async Task Checks()
        {
            using (DeviceSessionRegistry r = new DeviceSessionRegistry())
            using (OperationGate gate = new OperationGate(r))
            {
                DeviceSession d = Device(r); Fake f = new Fake(); RemoteControlSession s;
                Tests.Check(RemoteControlSession.TryStart(d, gate, f, false, 10, 8, out s) == ControlStartOutcome.Disabled && f.Opens == 0, "view/demo/default disabled performs zero transport operations");
                f.Support = ControlSupport.Unknown;
                Tests.Check(RemoteControlSession.TryStart(d, gate, f, true, 10, 8, out s) == ControlStartOutcome.Unsupported && gate.ActiveCount == 0, "unknown control capability fails closed without lease");
                f.Support = (ControlSupport)7; f.Protocol = "unverified";
                Tests.Check(RemoteControlSession.TryStart(d, gate, f, true, 10, 8, out s) == ControlStartOutcome.Unsupported && f.Opens == 0, "unknown protocol cannot start sender");
                f.Protocol = ControlBinding.Protocol;
                DeviceSession unsupported = r.Open("other", DeviceCapabilities.None, new DisplayTarget(0,"333","upper",10,10));
                Tests.Check(RemoteControlSession.TryStart(unsupported, gate, f, true, 10, 8, out s) == ControlStartOutcome.Unsupported && f.Opens == 0, "device capability must independently authorize remote control");
                f.BlockFirst = true; s = await Start(d, gate, f);
                Tests.Check(f.Binding.SessionId == d.Id && f.Binding.Generation == d.Generation && f.Binding.Serial == "offline" && f.Binding.Display("lower").LogicalId == 6 && f.Binding.Display("lower").PhysicalId == "222", "control handshake binds exact session and separate physical/logical screens");
                DisplayTarget[] exposed = s.Binding.Displays; exposed[0] = null;
                Tests.Check(s.Binding.Display("upper") != null, "control binding display array is immutable snapshot");
                ControlViewport viewport = new ControlViewport(d.Display("upper"),10,100,50,960,540,0,0,0,1920,1080); int px, py;
                Tests.Check(viewport.TryMap(580,320,out px,out py) && px == 960 && py == 540 && !viewport.TryMap(99,320,out px,out py), "DPI-content viewport maps screen pixels and excludes shell/black bars");
                viewport = new ControlViewport(d.Display("upper"),10,0,0,100,200,90,100,200,400,600);
                Tests.Check(viewport.TryMap(0,0,out px,out py) && px == 100 && py == 799 && viewport.TryMap(50,100,out px,out py) && px == 300 && py == 500, "rotated cropped viewport maps corners and center within native pixel bounds");
                Tests.Check(!viewport.TryMap(double.NaN,1,out px,out py) && !viewport.TryMap(100,1,out px,out py), "viewport rejects nonfinite values and exclusive right edge");
                RemoteControlSession second;
                Tests.Check(RemoteControlSession.TryStart(d, gate, new Fake(), true, 10, 8, out second) == ControlStartOutcome.Busy, "second controller cannot bypass operation gate");
                Tests.Check(s.Touch(ControlAction.TouchDown, 1,"upper",100,100,10) == ControlEnqueueOutcome.Queued, "screen touch down enters bounded sender");
                await Wait(() => f.Sends == 1);
                Tests.Check(s.Touch(ControlAction.TouchMove,1,"lower",101,101,10) == ControlEnqueueOutcome.Invalid, "drag cannot jump to other screen");
                Tests.Check(s.Touch(ControlAction.TouchMove,1,"upper",1920,101,10) == ControlEnqueueOutcome.Invalid, "touch rejects coordinates outside screen");
                Tests.Check(s.Touch(ControlAction.TouchDown,1,"upper",101,101,10) == ControlEnqueueOutcome.Invalid, "duplicate down is rejected");
                s.Touch(ControlAction.TouchMove,1,"upper",120,120,10);
                bool merged = true;
                for (int i=0;i<1000;i++) merged &= s.Touch(ControlAction.TouchMove,1,"upper",200+i,200,10) == ControlEnqueueOutcome.Coalesced;
                Tests.Check(merged && s.QueueDepth == 1, "1000 touch moves coalesce into one bounded newest move");
                Tests.Check(s.Key(true,29,false,10) == ControlEnqueueOutcome.Queued && s.Key(true,29,false,10) == ControlEnqueueOutcome.Invalid, "keyboard repeat cannot create duplicate held key");
                Tests.Check(s.Key(true,96,false,10) == ControlEnqueueOutcome.Invalid, "SDK gamepad buttons are not offered as keyboard/UHID support");
                Tests.Check(s.Key(false,29,false,10) == ControlEnqueueOutcome.Queued && s.QueueDepth == 2, "key release evicts stale moves while retaining down ordering");
                s.Touch(ControlAction.TouchUp,1,"upper",140,140,10);
                f.Block.SetResult(true); await Wait(() => f.Sends >= 4);
                Tests.Check(f.Snapshot()[1].Action == ControlAction.KeyDown && f.Snapshot()[2].Action == ControlAction.KeyUp && f.Snapshot()[3].Action == ControlAction.TouchUp, "critical down/up survive move congestion in lifecycle order");
                Tests.Check(await s.StopAsync(TimeSpan.FromSeconds(1),CancellationToken.None) && s.ReleasesConfirmed && gate.ActiveCount == 0, "focus/disable stop sends all-release then confirms channel cleanup before lease release");
                Tests.Check(s.Key(true,4,true,10) == ControlEnqueueOutcome.Disabled, "stopped session cannot auto-resume input");
            }
            using (DeviceSessionRegistry r = new DeviceSessionRegistry())
            using (OperationGate gate = new OperationGate(r))
            {
                DeviceSession d = Device(r); Fake f = new Fake { BlockFirst = true }; RemoteControlSession s = await Start(d,gate,f,4);
                s.Key(true,29,false,10); await Wait(() => f.Sends == 1);
                for (int k=30;k<34;k++) s.Key(true,k,false,10);
                Tests.Check(s.QueueDepth == 4 && s.Key(true,34,false,10) == ControlEnqueueOutcome.Congested && !s.Enabled, "critical queue overflow ends input rather than silently dropping releases");
                Tests.Check(!await s.StopAsync(TimeSpan.FromMilliseconds(60),CancellationToken.None) && gate.ActiveCount == 1 && !s.CleanupComplete, "ignored send cancellation returns pending and retains operation lease");
                using (CancellationTokenSource cancelled = new CancellationTokenSource())
                { cancelled.Cancel(); Tests.Check(!await s.StopAsync(TimeSpan.FromSeconds(1),cancelled.Token) && gate.ActiveCount == 1, "cancelled stop caller cannot falsely return held ownership"); }
                f.Block.SetResult(true);
                Tests.Check(await s.StopAsync(TimeSpan.FromSeconds(1),CancellationToken.None) && f.Snapshot().Length == 2 && f.Snapshot()[1].Action == ControlAction.ReleaseAll, "late send cleanup releases all and discards unsent obsolete downs");
            }
            using (DeviceSessionRegistry r = new DeviceSessionRegistry())
            using (OperationGate gate = new OperationGate(r))
            {
                DeviceSession d = Device(r); Fake f = new Fake { WrongAck = true }; RemoteControlSession s = await Start(d,gate,f);
                s.Key(true,4,true,10); await Wait(() => s.CleanupComplete);
                Tests.Check(!s.Enabled && f.Snapshot().Length == 2, "wrong-session acknowledgment terminates channel and attempts all-release");
                f = new Fake { Closed = false }; s = await Start(d,gate,f);
                Tests.Check(!await s.StopAsync(TimeSpan.FromMilliseconds(50),CancellationToken.None) && gate.ActiveCount == 1, "unconfirmed channel close remains blocked and retryable");
                f.Closed = true;
                Tests.Check(await s.StopAsync(TimeSpan.FromSeconds(1),CancellationToken.None) && gate.ActiveCount == 0, "confirmed cleanup retry returns retained lease");
                f = new Fake(); s = await Start(d,gate,f);
                Tests.Check(s.Touch(ControlAction.TouchDown,0,"upper",0,0,11) == ControlEnqueueOutcome.Disabled && !s.Enabled, "mapping revision change ends gesture generation");
                await s.StopAsync(TimeSpan.FromSeconds(1),CancellationToken.None);
                f = new Fake { BlockFirst = true }; s = await Start(d,gate,f);
                s.Key(true,29,false,10); await Wait(() => f.Sends == 1);
                Device(r); f.Block.SetResult(true);
                Tests.Check(await s.StopAsync(TimeSpan.FromSeconds(1),CancellationToken.None) && !s.ReleasesConfirmed && !s.Enabled, "reconnect rejects old response and does not claim disconnected releases applied");
                f = new Fake { Opened = false }; RemoteControlSession.TryStart(Device(r),gate,f,true,10,8,out s);
                await Wait(() => s.CleanupComplete);
                Tests.Check(f.Sends == 0 && gate.ActiveCount == 0, "failed protocol open performs no injection and closes owned channel");
                f = new Fake { Support = ControlSupport.Navigation }; s = await Start(Device(r),gate,f);
                Tests.Check(s.Touch(ControlAction.TouchDown,0,"upper",1,1,10) == ControlEnqueueOutcome.Invalid && s.Key(true,29,false,10) == ControlEnqueueOutcome.Invalid && f.Sends == 0, "navigation support alone does not enable unknown touch or keyboard injection");
                s.Key(true,3,true,10); await Wait(() => f.Sends == 1); await Task.Delay(30);
                Tests.Check(f.Sends == 1, "held navigation does not synthesize repeat or extra writes");
                await s.StopAsync(TimeSpan.FromSeconds(1),CancellationToken.None);
                f = new Fake { ThrowSend = true }; s = await Start(Device(r),gate,f);
                s.Key(true,4,true,10); await Wait(() => s.CleanupComplete);
                Tests.Check(f.Snapshot().Length == 2 && gate.ActiveCount == 0, "send fault still attempts all-release and confirms owned cleanup");
                f = new Fake { BlockFirst = true }; s = await Start(Device(r),gate,f);
                s.Touch(ControlAction.TouchDown,0,"upper",1,1,10); await Wait(() => f.Sends == 1);
                await Wait(() => !s.Enabled);
                Tests.Check(gate.ActiveCount == 1 && !s.CleanupComplete, "500ms send deadline disables stalled sender without releasing unconfirmed resources");
                f.Block.SetResult(true); await s.StopAsync(TimeSpan.FromSeconds(1),CancellationToken.None);
            }
        }
    }
}
