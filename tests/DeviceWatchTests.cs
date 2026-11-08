using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;
using ThorCapture.Features.DeviceWatch;

namespace ThorCapture
{
    internal static class DeviceWatchTests
    {
        internal static void Run() { ParserChecks(); Task.Run((Func<Task>)AsyncChecks).GetAwaiter().GetResult(); }
        private static IList<DeviceEntry> Parse(string value) { IList<DeviceEntry> parsed; if (!DeviceListParser.TryParse(value, false, out parsed)) throw new Exception("fixture"); return parsed; }
        private static void ParserChecks()
        {
            IList<DeviceEntry> entries;
            Tests.Check(DeviceListParser.TryParse("List of devices attached\r\nusb1\tdevice product:p model:Thor transport_id:1\r\n192.168.1.4:4567 offline\nusb2 unauthorized\nusb3 no permissions (user in plugdev group; are your udev rules wrong?)\nusb4 recovery", true, out entries) && entries.Count == 5 && entries.Count(e => e.State == DeviceLinkState.NoPermission) == 1 && entries.Count(e => e.Wireless) == 1, "watch parser handles long CLI rows permission and unknown states");
            Tests.Check(!DeviceListParser.TryParse("garbage", true, out entries) && !DeviceListParser.TryParse("x device\nx offline", false, out entries), "watch invalid and duplicate snapshots fail closed");
            Tests.Check(DeviceListParser.TryParse("List of devices attached\n", true, out entries) && entries.Count == 0 && !DeviceListParser.TryParse("", true, out entries), "watch genuine empty CLI snapshot differs from missing header");
            Tests.Check(!DeviceListParser.TryParse(new string('x', 65536), false, out entries) && !DeviceListParser.TryParse("x\0 device", false, out entries), "watch snapshot bounds and nul rejection");
            List<IList<DeviceEntry>> blocks = new List<IList<DeviceEntry>>(); TrackTextSink sink = new TrackTextSink(blocks.Add);
            byte[] stream = Encoding.UTF8.GetBytes("000aux\tdevice\n0000");
            foreach (byte b in stream) sink.Write(new[] { b }, 0, 1);
            Tests.Check(blocks.Count == 2 && blocks[0].Single().Serial == "ux" && blocks[1].Count == 0 && sink.Complete, "track split byte prefixes and zero length block parse exactly");
            sink.Write(Encoding.ASCII.GetBytes("0005xx"), 0, 6);
            Tests.Check(!sink.Complete && blocks.Count == 2, "track truncated block never publishes partial or missing device snapshot");
            Tests.Reject(() => new TrackTextSink(blocks.Add).Write(Encoding.ASCII.GetBytes("zzzz"), 0, 4), "track rejects invalid hexadecimal prefix");
            Tests.Reject(() => new TrackTextSink(blocks.Add).Write(new byte[] {48,48,48,49,255}, 0, 5), "track rejects malformed UTF8 before publication");
            Tests.Reject(() => ((IList<DeviceEntry>)blocks[0]).Clear(), "watch snapshots immutable to consumers");
            string host; int port;
            Tests.Check(WirelessAdb.TryParseEndpoint("192.168.1.4:45678", out host, out port) && host == "192.168.1.4" && port == 45678, "wireless explicit IPv4 and port");
            Tests.Check(WirelessAdb.TryParseEndpoint("[2001:db8::1]:4567", out host, out port) && port == 4567, "wireless bracket IPv6 and port");
            foreach (string bad in new[] {"host:5555", "127.0.0.1:5555", "192.168.1.4", "192.168.1.4:0", "192.168.1.4:65536", "192.168.01.4:5555", "192.168.1.4:5555;tcpip", "http://192.168.1.4:5555", "[fe80::1]:5555", "0.0.0.0:5555", "255.255.255.255:5555", " 192.168.1.4:5555"})
                Tests.Check(!WirelessAdb.TryParseEndpoint(bad, out host, out port), "wireless rejects unsafe or ambiguous address");
            Tests.Check(OwnedWirelessTransport.Parse(WirelessOperation.Connect, new ProcessResult { Outcome = ProcessOutcome.Success, Output = "failed to connect to private endpoint: token 654321" }).Outcome == WirelessOutcome.Rejected, "wireless exit zero failure text is rejected");
            Tests.Check(OwnedWirelessTransport.Parse(WirelessOperation.Pair, new ProcessResult { Outcome = ProcessOutcome.Failed, Output = "unknown command pair" }).Outcome == WirelessOutcome.Unsupported, "wireless unsupported command typed even nonzero exit");
            Tests.Check(OwnedWirelessTransport.Parse(WirelessOperation.Pair, new ProcessResult { Outcome = ProcessOutcome.TimedOut, Output = "654321" }).Hint.IndexOf("654321") < 0, "wireless error hints never incorporate raw output or pairing code");
        }
        private sealed class FakeWatch : IDeviceWatchProvider
        {
            internal int Polls, Tracks, Stops;
            internal bool SupportsTrack, Hang, Clean = true;
            internal string Snapshot = "a device";
            internal Action<CancellationToken> OnPoll;
            internal TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>();
            public bool TrackSupported { get { return SupportsTrack; } }
            public async Task<WatchReply> PollAsync(CancellationToken token)
            { Interlocked.Increment(ref Polls); if (OnPoll != null) OnPoll(token); if (Hang) await Release.Task; return new WatchReply(Polls != 2, true, Parse(Snapshot)); }
            public Task<WatchReply> TrackAsync(Action<IList<DeviceEntry>> publish, CancellationToken token)
            { Interlocked.Increment(ref Tracks); for (int i = 0; i < 40; i++) publish(Parse(i == 39 ? "b device" : "a offline")); return Task.FromResult(new WatchReply(false, true)); }
            public Task<bool> StopAsync(TimeSpan deadline, CancellationToken token) { Stops++; return Task.FromResult(Clean); }
        }
        private sealed class FakeWireless : IWirelessTransport
        {
            internal int Calls; internal WirelessResult Next = new WirelessResult(WirelessOutcome.Success);
            internal Action OnExecute;
            public Task<WirelessResult> ExecuteAsync(WirelessOperation op, string endpoint, string code, CancellationToken token) { Calls++; if (OnExecute != null) OnExecute(); return Task.FromResult(Next); }
            public Task<bool> StopAsync(TimeSpan deadline, CancellationToken token) { return Task.FromResult(true); }
        }
        private sealed class Guard : IWirelessOperationGuard
        {
            internal int Held;
            private sealed class Lease : IDisposable { private Guard owner; internal Lease(Guard owner) { this.owner = owner; } public void Dispose() { if (owner != null) { owner.Held--; owner = null; } } }
            public Task<IDisposable> AcquireAsync(string endpoint, CancellationToken token) { Held++; return Task.FromResult<IDisposable>(new Lease(this)); }
        }
        private static async Task Wait(Func<bool> ready) { Stopwatch clock = Stopwatch.StartNew(); while (!ready() && clock.ElapsedMilliseconds < 4000) await Task.Delay(10); if (!ready()) throw new Exception("watch test deadline"); }
        private static async Task AsyncChecks()
        {
            FakeWatch fake = new FakeWatch { SupportsTrack = true }; DeviceWatcher watcher = new DeviceWatcher(fake, 50, 5); int events = 0;
            watcher.Changed += delegate { Interlocked.Increment(ref events); }; watcher.Changed += delegate { throw new Exception("subscriber raw secret"); };
            watcher.Start(); await Wait(() => fake.Polls >= 1); await Wait(() => watcher.Current.Any(e => e.Serial == "a"));
            Tests.Check(fake.Tracks == 3 && watcher.Mode == WatchMode.Polling, "watch bounded retry backoff then downgrade to polling");
            Tests.Check(events < 8, "watch bursts coalesce without one task per snapshot and subscriber failures isolated");
            await Wait(() => fake.Polls >= 2);
            Tests.Check(watcher.Current.Count == 1, "watch transient poll failure preserves last device snapshot");
                        DeviceLinkState removedState = DeviceLinkState.Unknown; watcher.Changed += delegate(object sender, DeviceListChangedEventArgs args) { if (args.Removed.Count > 0) removedState = args.Removed[0].State; };
            fake.Snapshot = "a unauthorized"; await Wait(() => watcher.Current.Single().State == DeviceLinkState.Unauthorized);
            Tests.Check(watcher.Current.Single().State == DeviceLinkState.Unauthorized, "watch authorization state changes are observable without selecting or capturing");
            fake.Snapshot = ""; await Wait(() => watcher.Current.Count == 0 && removedState == DeviceLinkState.Missing);
            Tests.Check(removedState == DeviceLinkState.Missing, "watch complete empty snapshot emits Missing removal only after debounce");
            Tests.Check(await watcher.StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None) && watcher.Mode == WatchMode.Stopped, "watch StopAsync waits owned loop events provider cleanup");
            int stopped = fake.Polls; await Task.Delay(70); Tests.Check(fake.Polls == stopped, "watch no polling after acknowledged stop");
            Tests.Reject(watcher.Start, "watch disposed lifecycle cannot resume capture automatically");
            FakeWatch hanging = new FakeWatch { Hang = true }; DeviceWatcher blocked = new DeviceWatcher(hanging, 20, 10); blocked.Start(); await Wait(() => hanging.Polls == 1);
            Tests.Check(!await blocked.StopAsync(TimeSpan.FromMilliseconds(40), CancellationToken.None) && blocked.Mode == WatchMode.CleanupPending, "watch ignored cancellation retains pending ownership within caller deadline");
            hanging.Release.SetResult(true); Tests.Check(await blocked.StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None), "watch late cleanup ack can be observed on repeated stop");
                        using (ManualResetEvent enteredCancel = new ManualResetEvent(false))
            using (ManualResetEvent releaseCancel = new ManualResetEvent(false))
            {
                FakeWatch callback = new FakeWatch(); callback.OnPoll = token => { if (callback.Polls == 1) token.Register(() => { enteredCancel.Set(); releaseCancel.WaitOne(); }); };
                DeviceWatcher callbackWatcher = new DeviceWatcher(callback, 50, 5); callbackWatcher.Start(); await Wait(() => callback.Polls == 1);
                Tests.Check(!await callbackWatcher.StopAsync(TimeSpan.FromMilliseconds(40), CancellationToken.None), "watch blocked cancellation callback remains owned within shutdown deadline");
                Tests.Check(enteredCancel.WaitOne(1000), "watch cancellation callbacks run without blocking caller thread");
                releaseCancel.Set(); Tests.Check(await callbackWatcher.StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None), "watch late cancellation callback acknowledgment completes shutdown");
            }
            FakeWatch falseAck = new FakeWatch { Clean = false }; DeviceWatcher retry = new DeviceWatcher(falseAck, 20, 10); retry.Start();
            Tests.Check(!await retry.StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None), "watch false provider cleanup acknowledgment never reports stopped");
            falseAck.Clean = true; Tests.Check(await retry.StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None), "watch explicit repeated stop retries failed cleanup acknowledgment");
            FakeWireless wireless = new FakeWireless(); WirelessAdb unknown = new WirelessAdb(wireless);
            Tests.Check((await unknown.PairAsync("192.168.1.4:4567", "123456", true, CancellationToken.None)).Outcome == WirelessOutcome.UnknownCapability && wireless.Calls == 0, "wireless capability unknown never invokes transport");
            bool current = true; WirelessAdb enabled = new WirelessAdb(wireless, new WirelessCapability(WirelessSupport.Supported, WirelessSupport.Supported, () => current));
            Tests.Check((await enabled.PairAsync("192.168.1.4:4567", "badcode", true, CancellationToken.None)).Outcome == WirelessOutcome.BadCode && wireless.Calls == 0, "wireless invalid code refuses before transport");
            Tests.Check((await enabled.ConnectAsync("192.168.1.4:4567", false, CancellationToken.None)).Outcome == WirelessOutcome.UnknownCapability && wireless.Calls == 0, "wireless requires explicit request even verified capability");
            Tests.Check((await enabled.PairAsync("192.168.1.4:4567", "123456", true, CancellationToken.None)).Outcome == WirelessOutcome.Success && enabled.Recent().Count == 0, "wireless pairing address never silently becomes connection address");
            Tests.Check((await enabled.ConnectAsync("192.168.1.4:4568", true, CancellationToken.None)).Outcome == WirelessOutcome.Success && enabled.Recent().Single().EndsWith(":4568"), "wireless successful explicit connection remembers only connection endpoint");
            for (int i = 1; i <= 8; i++) enabled.Remember("192.168.1.4:" + (4600 + i));
            Tests.Check(enabled.Recent().Count == 5 && enabled.Recent()[0].EndsWith("4608"), "wireless recents bounded five newest endpoints"); enabled.Forget(enabled.Recent()[0]); Tests.Check(enabled.Recent().Count == 4, "wireless forget recent endpoint");
            wireless.OnExecute = () => current = false;
            Tests.Check((await enabled.ConnectAsync("192.168.1.4:4568", true, CancellationToken.None)).Outcome == WirelessOutcome.UnknownCapability, "wireless rejects response after capability binding changes");
            await enabled.StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            Tests.Check((await enabled.ConnectAsync("192.168.1.4:4568", true, CancellationToken.None)).Outcome == WirelessOutcome.Closed, "wireless closed facade never reconnects automatically");
            Tests.FakeMode("watch-provider");
            OwnedAdbWatchProvider owned = new OwnedAdbWatchProvider(typeof(Tests).Assembly.Location);
            WatchReply poll = await owned.PollAsync(CancellationToken.None);
            Tests.Check(!owned.TrackSupported && poll.Success && poll.Devices.Count == 2 && poll.CleanupComplete, "owned provider defaults bounded CLI polling via actual fake process");
            Tests.Check(await owned.StopAsync(TimeSpan.FromSeconds(2), CancellationToken.None), "owned polling cleanup acknowledgment");
            OwnedAdbWatchProvider tracked = new OwnedAdbWatchProvider(typeof(Tests).Assembly.Location, TrackFormat.HexUtf8Text); int snapshots = 0;
            WatchReply track = await tracked.TrackAsync(entries => snapshots++, CancellationToken.None);
            Tests.Check(snapshots == 2 && !track.Success && track.CleanupComplete, "owned track producer EOF triggers retry with exactly complete frames");
            Tests.Check(await tracked.StopAsync(TimeSpan.FromSeconds(2), CancellationToken.None), "owned track cleanup acknowledgment");
            Guard guard = new Guard(); OwnedWirelessTransport transport = new OwnedWirelessTransport(typeof(Tests).Assembly.Location, guard, () => true);
            WirelessResult paired = await transport.ExecuteAsync(WirelessOperation.Pair, "192.168.1.4:4567", "654321", CancellationToken.None);
            Tests.Check(paired.Outcome == WirelessOutcome.Success && guard.Held == 0 && paired.Hint.IndexOf("654321") < 0, "owned fixed pair fake process sanitized result and guard release");
            Tests.Check((await transport.ExecuteAsync(WirelessOperation.Connect, "192.168.1.4:4568", null, CancellationToken.None)).Outcome == WirelessOutcome.Success, "owned connect fixed command output parsed");
            Tests.Check((await transport.ExecuteAsync(WirelessOperation.Disconnect, "192.168.1.4:4568", null, CancellationToken.None)).Outcome == WirelessOutcome.Success, "owned disconnect always targets one address");
            Tests.Check(await transport.StopAsync(TimeSpan.FromSeconds(2), CancellationToken.None), "owned wireless shutdown confirms child cleanup and operation lease release");
                        Tests.Check(OwnedWirelessTransport.Parse(WirelessOperation.Connect, new ProcessResult { Outcome = ProcessOutcome.Success, Output = "connected to 192.168.1.9:4568" }, "192.168.1.4:4568").Outcome == WirelessOutcome.Rejected, "wireless success output must match requested endpoint");
            Guard deniedGuard = new Guard(); OwnedWirelessTransport deniedTransport = new OwnedWirelessTransport(typeof(Tests).Assembly.Location, deniedGuard, () => false);
            Tests.Check((await deniedTransport.ExecuteAsync(WirelessOperation.Connect, "192.168.1.4:4568", null, CancellationToken.None)).Outcome == WirelessOutcome.UnknownCapability && deniedGuard.Held == 0, "wireless rechecks authorization after acquiring shared operation guard");
            Tests.Check(await deniedTransport.StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None), "wireless denied pre-start operation releases guard");
            Tests.FakeMode("watch-hang"); OwnedWirelessTransport slowTransport = new OwnedWirelessTransport(typeof(Tests).Assembly.Location, guard, () => true);
            using (CancellationTokenSource cancelWireless = new CancellationTokenSource())
            { cancelWireless.CancelAfter(100); WirelessResult cancelledWireless = await slowTransport.ExecuteAsync(WirelessOperation.Pair, "192.168.1.4:4567", "654321", cancelWireless.Token); Tests.Check(cancelledWireless.Outcome == WirelessOutcome.Cancelled && cancelledWireless.CleanupComplete && guard.Held == 0, "wireless inflight cancellation stops only owned child and releases operation lease"); }
            Tests.Check(await slowTransport.StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None), "wireless cancellation acknowledges drained process cleanup");
            Tests.FakeMode("watch-hang"); OwnedAdbWatchProvider hung = new OwnedAdbWatchProvider(typeof(Tests).Assembly.Location);
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            { cancel.CancelAfter(100); WatchReply cancelled = await hung.PollAsync(cancel.Token); Tests.Check(!cancelled.Success && cancelled.CleanupComplete, "owned poll cancellation stops fake owned child"); }
            Tests.Check(await hung.StopAsync(TimeSpan.FromSeconds(2), CancellationToken.None), "owned cancelled provider drains both pipes before stop acknowledgment");
            string log = Log.CurrentFile;
            Tests.Check(File.Exists(log) && File.ReadAllText(log).IndexOf("654321", StringComparison.Ordinal) < 0, "wireless pairing code absent from durable log");
            Tests.FakeMode("normal");
        }
        internal static int FakeAdb(string command, string mode, string dir)
        {
            if (mode != "watch-provider" && mode != "watch-hang") return Tests.NotHandled;
            if (mode == "watch-hang") { Thread.Sleep(30000); return 0; }
            if (command == "devices -l") { Console.Write("List of devices attached\nusb device model:Thor\n192.168.1.4:4568 unauthorized\n"); return 0; }
            if (command == "track-devices -l") { Console.Write("000busb device\n0000"); return 0; }
            if (command.StartsWith("pair ")) { Console.Write("Successfully paired to 192.168.1.4:4567 [guid=test]\n"); return 0; }
            if (command.StartsWith("connect ")) { Console.Write("connected to 192.168.1.4:4568\n"); return 0; }
            if (command.StartsWith("disconnect ")) { Console.Write("disconnected 192.168.1.4:4568\n"); return 0; }
            return Tests.NotHandled;
        }
    }
}
