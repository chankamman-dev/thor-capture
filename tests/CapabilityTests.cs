using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Features.Capability;

namespace ThorCapture
{
    internal static class CapabilityTests
    {
        private static string Fixture(string name)
        {
            string project = Path.GetDirectoryName(Path.GetDirectoryName(typeof(Tests).Assembly.Location));
            return File.ReadAllText(Path.Combine(project, "tests/fixtures/capability/" + name));
        }
        private static ProbeOutputs Sample()
        {
            ProbeOutputs outputs = new ProbeOutputs { SerialHash = CapabilityProbe.HashSerial("private-device"), Outcome = ProbeOutcome.Success };
            Set(outputs, ProbeCommand.Properties, "[ro.product.model]: [Thor]\n[ro.build.version.release]: [13]\n[ro.build.version.sdk]: [33]\n[ro.build.fingerprint]: [vendor/thor/build:13/test]");
            Set(outputs, ProbeCommand.Displays, Fixture("viewport.txt"));
            Set(outputs, ProbeCommand.ScreenCaptureHelp, "usage: screencap [-d display-id] [FILENAME]");
            Set(outputs, ProbeCommand.ScreenRecordHelp, "Usage: screenrecord [options] <filename>\n--display-id ID\n--size WIDTHxHEIGHT\n--bit-rate RATE\n--output-format {mp4,h264}");
            Set(outputs, ProbeCommand.InputNodes, "add device 1: /dev/input/event3\nname: gamepad\nKEY (0001): 0130 0131\nABS (0003): 0000");
            Set(outputs, ProbeCommand.DeviceCodecs, "c2.qti.avc.encoder");
            return outputs;
        }
        private static void Set(ProbeOutputs outputs, ProbeCommand command, string text)
        {
            outputs.Replies[command] = new ProbeReply { Outcome = ProbeOutcome.Success, ExitCode = 0, Output = text };
        }
        internal static void Run()
        {
            DeviceCapabilities good = CapabilityProbe.Parse(Sample());
            Tests.Check(good.Model == "Thor" && good.AndroidRelease == "13" && good.Fingerprint.Length > 0, "capability parses system identity without retaining serial");
            Tests.Check(good.SerialHash.Length == 64 && !good.SerialHash.Contains("private"), "capability uses complete SHA256 identity");
            Tests.Check(good.Displays.Count == 2 && good.Displays[1].IsLower && good.Displays[1].PhysicalId == "4619827259835644673", "viewport maps dynamic logical lower id to physical id");
            Tests.Check(CapabilityProbe.ParseDisplays(Fixture("info.txt")).Count == 2 && CapabilityProbe.ParseDisplays(Fixture("info.txt"))[1].LogicalId == 7, "DisplayInfo alternative syntax and duplicates parse");
            Tests.Check(CapabilityProbe.ParseDisplays("DisplayViewport{displayId=0, uniqueId='virtual:1', deviceWidth=20, deviceHeight=10}").Count == 0, "virtual id never substitutes physical id");
            Tests.Check(CapabilityProbe.ParseDisplays(Fixture("viewport.txt") + "\nDisplayViewport{displayId=0, uniqueId='local:99', deviceWidth=1920, deviceHeight=1080}").Count == 0, "conflicting mappings invalidate snapshot");
            Tests.Check(good.CanUse("screencap.display") && good.CanUse("screenrecord.displayId") && good.CanUse("screenrecord.h264Stream") && good.CanUse("input.getevent"), "recognisable help options and node inventory produce narrow support");
            Tests.Check(!good.CanUse("screenrecord.dual") && !good.CanUse("audio.playback") && !good.CanUse("preview.lowLatency") && !good.CanUse("input.mapping"), "help never proves dual recording audio latency or input mapping");
            Tests.Check(good.CanUse("encoder.avcDeclared") && good.Items["screenrecord.frameRate"].State == Support.Unsupported, "codec declaration does not invent screenrecord source fps option");
            ProbeOutputs denied = Sample(); Set(denied, ProbeCommand.InputNodes, Fixture("denied.txt"));
            Tests.Check(CapabilityProbe.Parse(denied).Items["input.getevent"].State == Support.Unsupported, "permission denial disables only input read capability");
            ProbeOutputs empty = new ProbeOutputs();
            Tests.Check(CapabilityProbe.Parse(empty).Items.Values.All(i => i.State == Support.Unknown), "empty output never grants support");
            ProbeOutputs old = Sample(); Set(old, ProbeCommand.Properties, "[ro.build.version.sdk]: [32]");
            Tests.Check(CapabilityProbe.Parse(old).Items["audio.playback"].State == Support.Unsupported, "old Android explicitly lacks current playback audio backend");
            ProbeOutputs noOption = Sample(); Set(noOption, ProbeCommand.ScreenRecordHelp, "Usage: screenrecord [options] <filename>\n--size WIDTHxHEIGHT");
            Tests.Check(CapabilityProbe.Parse(noOption).Items["screenrecord.displayId"].State == Support.Unsupported, "recognisable complete help can report absent option");
            ProbeOutputs truncated = Sample(); truncated.Replies[ProbeCommand.Displays].Truncated = true;
            Tests.Check(CapabilityProbe.Parse(truncated).Displays.Count == 0 && !CapabilityProbe.CacheMatches(good, CapabilityProbe.Parse(truncated)), "truncated display dump cannot refresh cache");
            ProbeOutputs statusOne = Sample(); statusOne.Replies[ProbeCommand.ScreenCaptureHelp].Outcome = ProbeOutcome.Failed; statusOne.Replies[ProbeCommand.ScreenCaptureHelp].ExitCode = 1;
            Tests.Check(CapabilityProbe.Parse(statusOne).CanUse("screencap.display"), "usage help with intentional exit one remains identifiable");
            Tests.Check(CapabilityProbe.CacheMatches(good, CapabilityProbe.Parse(Sample())), "unchanged fingerprint and display signature match cache");
            ProbeOutputs changed = Sample(); Set(changed, ProbeCommand.Properties, "[ro.build.fingerprint]: [new-build]");
            Tests.Check(!CapabilityProbe.CacheMatches(good, CapabilityProbe.Parse(changed)), "system fingerprint change invalidates cache");
            changed = Sample(); Set(changed, ProbeCommand.Displays, Fixture("info.txt"));
            Tests.Check(!CapabilityProbe.CacheMatches(good, CapabilityProbe.Parse(changed)), "logical display reconnect invalidates cache");
            changed = Sample(); Set(changed, ProbeCommand.Displays, Fixture("viewport.txt").Replace("1920", "1280"));
            Tests.Check(!CapabilityProbe.CacheMatches(good, CapabilityProbe.Parse(changed)), "screen resolution change invalidates cache");
            changed = Sample(); Set(changed, ProbeCommand.Displays, Fixture("viewport.txt").Split('\n')[0]);
            Tests.Check(!CapabilityProbe.CacheMatches(good, CapabilityProbe.Parse(changed)) && !CapabilityProbe.Parse(changed).CanUse("display.dualMapping"), "screen count change invalidates cache and dual role");
            changed = Sample(); changed.SerialHash = CapabilityProbe.HashSerial("other-device");
            Tests.Check(!CapabilityProbe.CacheMatches(good, CapabilityProbe.Parse(changed)), "device switch invalidates cache");
            ProbeOutputs host = new ProbeOutputs();
            Set(host, ProbeCommand.AdbVersion, "Android Debug Bridge version 1.0.41\nInstalled as C:\\Users\\private\\adb.exe");
            Set(host, ProbeCommand.FfmpegVersion, "ffmpeg version 6.0 Copyright");
            Set(host, ProbeCommand.FfmpegDecoders, " VFS..D h264 H264\n A....D aac AAC");
            Set(host, ProbeCommand.FfmpegEncoders, " V....D libx264 H264\n A..... aac AAC");
            HostCapabilities h = CapabilityProbe.ParseHost(host);
            Tests.Check(h.AdbVersion == "1.0.41" && h.FfmpegVersion == "6.0" && !h.AdbVersion.Contains("Users"), "host summary extracts versions without executable path");
            Tests.Check(h.Items.Values.All(i => i.State == Support.Supported), "host codecs parsed from typed table rows");
            Set(host, ProbeCommand.FfmpegEncoders, " V..... rawvideo raw");
            Tests.Check(CapabilityProbe.ParseHost(host).Items["host.encode.h264"].State == Support.Unsupported, "complete codec table reports missing encoder");
            host.Replies[ProbeCommand.FfmpegEncoders].Truncated = true;
            Tests.Check(CapabilityProbe.ParseHost(host).Items["host.encode.h264"].State == Support.Unknown, "truncated codec table does not prove absence");
            Tests.Reject(() => CapabilityProcessRunner.Arguments("x\" & injected", ProbeCommand.Displays), "runner rejects argument injection");
            Tests.Check(CapabilityProcessRunner.Arguments("127.0.0.1:5555", ProbeCommand.Displays).Contains("dumpsys display"), "runner supports network serial without shell injection");
            CapabilityProcessRunner runner = new CapabilityProcessRunner(typeof(Tests).Assembly.Location, typeof(Tests).Assembly.Location);
            Tests.FakeMode("capability-good");
            DeviceCapabilities probed = CapabilityProbe.ProbeAsync("private-device", runner, CancellationToken.None).GetAwaiter().GetResult();
            Tests.Check(probed.Outcome == ProbeOutcome.Success && probed.CanUse("display.mapping"), "actual child fake adb read-only probe succeeds");
            Tests.FakeMode("capability-help-one");
            Tests.Check(CapabilityProbe.ProbeAsync("private-device", runner, CancellationToken.None).Result.Outcome == ProbeOutcome.Success, "expected help exit one does not invalidate successful probe cache");
            Tests.FakeMode("capability-denied");
            Tests.Check(CapabilityProbe.ProbeAsync("private-device", runner, CancellationToken.None).Result.Items["input.getevent"].State == Support.Unsupported, "fake adb stderr denial typed unsupported");
            Tests.FakeMode("capability-empty");
            Tests.Check(CapabilityProbe.ProbeAsync("private-device", runner, CancellationToken.None).Result.Items.Values.All(i => i.State == Support.Unknown), "fake adb empty command output stays unknown");
            Tests.FakeMode("capability-offline");
            Tests.Check(CapabilityProbe.ProbeAsync("private-device", runner, CancellationToken.None).Result.Outcome == ProbeOutcome.Disconnected, "fake adb disconnected result is explicit");
            Tests.FakeMode("capability-hang");
            ProbeReply timed = runner.RunAsync("private-device", ProbeCommand.Displays, TimeSpan.FromMilliseconds(100), CancellationToken.None).Result;
            Tests.Check(timed.Outcome == ProbeOutcome.TimedOut && timed.Duration.TotalSeconds < 4, "timeout kills only owned fake process promptly");
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                cancel.CancelAfter(100);
                DeviceCapabilities cancelled = CapabilityProbe.ProbeAsync("private-device", runner, cancel.Token).Result;
                Tests.Check(cancelled.Outcome == ProbeOutcome.Cancelled && !cancelled.CanUse("display.mapping"), "inflight cancellation returns partial typed result without support");
            }
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                Tests.Check(CapabilityProbe.ProbeHostAsync(runner, cancel.Token).Result.Outcome == ProbeOutcome.Cancelled, "host probe supports cancellation before launch");
            }
            Tests.FakeMode("capability-flood");
            ProbeReply flood = runner.RunAsync("private-device", ProbeCommand.Displays, TimeSpan.FromSeconds(4), CancellationToken.None).Result;
            Tests.Check(flood.Output.Length == 262144 && flood.Truncated, "child output is bounded while pipe continues draining");
            string log = File.ReadAllText(Log.CurrentFile);
            Tests.Check(!log.Contains("private-device") && !log.Contains("sensitive-username"), "probe logs omit serial paths and child errors");
            Tests.FakeMode("normal");
        }
        internal static int FakeAdb(string command, string mode, string dir)
        {
            if (mode == null || !mode.StartsWith("capability-", StringComparison.Ordinal)) return Tests.NotHandled;
            if (mode == "capability-hang") { Thread.Sleep(10000); return 0; }
            if (mode == "capability-empty") return 0;
            if (mode == "capability-offline") { Console.Error.WriteLine("error: device offline private-device"); return 1; }
            if (mode == "capability-flood") { Console.Write(new string('x', 300000)); return 0; }
            if (command.Contains("getevent") && mode == "capability-denied") { Console.Error.WriteLine("Permission denied C:\\Users\\sensitive-username"); return 1; }
            if (command.Contains("getprop")) Console.Write(Sample().Replies[ProbeCommand.Properties].Output);
            else if (command.Contains("dumpsys display")) Console.Write(Fixture("viewport.txt"));
            else if (command.Contains("screencap --help")) Console.Write(Sample().Replies[ProbeCommand.ScreenCaptureHelp].Output);
            else if (command.Contains("screenrecord --help")) Console.Write(Sample().Replies[ProbeCommand.ScreenRecordHelp].Output);
            else if (command.Contains("getevent")) Console.Write(Sample().Replies[ProbeCommand.InputNodes].Output);
            else if (command.Contains("dumpsys media.codec")) Console.Write(Sample().Replies[ProbeCommand.DeviceCodecs].Output);
            if (mode == "capability-help-one" && command.Contains("--help")) return 1;
            return 0;
        }
    }
}
