using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Features.Capability
{
    internal enum Support { Supported, Unsupported, Unknown }
    internal enum ProbeOutcome { Success, Cancelled, TimedOut, Disconnected, Failed, NotFound }
    internal enum ProbeCommand { Properties, Displays, ScreenCaptureHelp, ScreenRecordHelp, InputNodes, DeviceCodecs, AdbVersion, FfmpegVersion, FfmpegDecoders, FfmpegEncoders }
    internal sealed class ProbeReply
    {
        public ProbeOutcome Outcome;
        public int ExitCode;
        public string Output = "";
        public string Error = "";
        public TimeSpan Duration;
        public bool Truncated;
    }
    // Implement with A1's process runner when integrated. Commands are a read-only allowlist.
    internal interface ICapabilityRunner
    {
        Task<ProbeReply> RunAsync(string serial, ProbeCommand command, TimeSpan timeout, CancellationToken token);
    }
    internal sealed class CapabilityItem
    {
        public string Key;
        public Support State;
        public string Detail;
    }
    internal sealed class ProbeOutputs
    {
        public readonly Dictionary<ProbeCommand, ProbeReply> Replies = new Dictionary<ProbeCommand, ProbeReply>();
        public string SerialHash = "";
        public DateTime ProbedUtc;
        public ProbeOutcome Outcome;
    }
    internal sealed class DeviceCapabilities
    {
        public string SerialHash, Model, AndroidRelease, Fingerprint;
        public DateTime ProbedUtc;
        public ProbeOutcome Outcome;
        public readonly List<DeviceDisplay> Displays = new List<DeviceDisplay>();
        public readonly Dictionary<string, CapabilityItem> Items = new Dictionary<string, CapabilityItem>();
        public readonly Dictionary<ProbeCommand, ProbeOutcome> Commands = new Dictionary<ProbeCommand, ProbeOutcome>();
        public bool CanUse(string key) { return Items.ContainsKey(key) && Items[key].State == Support.Supported; }
    }
    internal sealed class HostCapabilities
    {
        public string AdbVersion = "", FfmpegVersion = "";
        public ProbeOutcome Outcome;
        public readonly Dictionary<string, CapabilityItem> Items = new Dictionary<string, CapabilityItem>();
    }
    internal static class CapabilityProbe
    {
        private static readonly ProbeCommand[] deviceCommands = { ProbeCommand.Properties, ProbeCommand.Displays,
            ProbeCommand.ScreenCaptureHelp, ProbeCommand.ScreenRecordHelp, ProbeCommand.InputNodes, ProbeCommand.DeviceCodecs };
        public static string HashSerial(string serial)
        {
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(serial ?? ""))).Replace("-", "").ToLowerInvariant();
        }
        public static async Task<DeviceCapabilities> ProbeAsync(string serial, ICapabilityRunner runner, CancellationToken token)
        {
            if (runner == null) throw new ArgumentNullException("runner");
            if (string.IsNullOrWhiteSpace(serial)) throw new ArgumentException("Device identity is required", "serial");
            ProbeOutputs outputs = new ProbeOutputs { SerialHash = HashSerial(serial), ProbedUtc = DateTime.UtcNow, Outcome = ProbeOutcome.Success };
            foreach (ProbeCommand command in deviceCommands)
            {
                ProbeReply reply = token.IsCancellationRequested ? new ProbeReply { Outcome = ProbeOutcome.Cancelled } :
                    await RunSafe(runner, serial, command, token).ConfigureAwait(false);
                outputs.Replies[command] = reply;
                bool expectedHelpExit = (command == ProbeCommand.ScreenCaptureHelp || command == ProbeCommand.ScreenRecordHelp) &&
                    reply.Outcome == ProbeOutcome.Failed && reply.ExitCode == 1 && !reply.Truncated &&
                    Regex.IsMatch(reply.Output + reply.Error, @"(?i)\busage:\s*(?:screenrecord|screencap)");
                if (reply.Outcome != ProbeOutcome.Success && !expectedHelpExit && outputs.Outcome == ProbeOutcome.Success) outputs.Outcome = reply.Outcome;
                if (reply.Outcome == ProbeOutcome.Cancelled) { outputs.Outcome = ProbeOutcome.Cancelled; break; }
                if (reply.Outcome == ProbeOutcome.Disconnected) break;
            }
            return Parse(outputs);
        }
        private static async Task<ProbeReply> RunSafe(ICapabilityRunner runner, string serial, ProbeCommand command, CancellationToken token)
        {
            try { return await runner.RunAsync(serial, command, TimeSpan.FromSeconds(5), token).ConfigureAwait(false) ?? new ProbeReply { Outcome = ProbeOutcome.Failed }; }
            catch (OperationCanceledException) { return new ProbeReply { Outcome = ProbeOutcome.Cancelled }; }
            catch { return new ProbeReply { Outcome = ProbeOutcome.Failed }; }
        }
        public static DeviceCapabilities Parse(ProbeOutputs outputs)
        {
            if (outputs == null) throw new ArgumentNullException("outputs");
            DeviceCapabilities value = new DeviceCapabilities { SerialHash = outputs.SerialHash, ProbedUtc = outputs.ProbedUtc, Outcome = outputs.Outcome,
                Model = "未知", AndroidRelease = "未知", Fingerprint = "" };
            foreach (KeyValuePair<ProbeCommand, ProbeReply> entry in outputs.Replies) value.Commands[entry.Key] = entry.Value.Outcome;
            string props = GoodText(outputs, ProbeCommand.Properties);
            value.Model = Property(props, "ro.product.model", "未知");
            value.AndroidRelease = Property(props, "ro.build.version.release", "未知");
            value.Fingerprint = Property(props, "ro.build.fingerprint", "");
            value.Displays.AddRange(ParseDisplays(GoodText(outputs, ProbeCommand.Displays)));
            Add(value.Items, "display.mapping", value.Displays.Count > 0 ? Support.Supported : Support.Unknown,
                value.Displays.Count > 0 ? "已解析物理显示 ID；实际画面仍需验证" : Reason(outputs, ProbeCommand.Displays));
            Add(value.Items, "display.dualMapping", value.Displays.Count == 2 && value.Displays.Any(d => d.LogicalId == 0) ? Support.Supported : Support.Unknown,
                value.Displays.Count == 2 && value.Displays.Any(d => d.LogicalId == 0) ? "两块屏的上下映射已解析" : "未获得明确的上下双屏映射");
            Option(value.Items, outputs, ProbeCommand.ScreenCaptureHelp, "screencap.display", @"(?<!\w)-d\b", "截图物理屏选项");
            Option(value.Items, outputs, ProbeCommand.ScreenRecordHelp, "screenrecord.displayId", @"--display-id\b", "录屏物理屏选项");
            Option(value.Items, outputs, ProbeCommand.ScreenRecordHelp, "screenrecord.size", @"--size\b", "录屏尺寸选项");
            Option(value.Items, outputs, ProbeCommand.ScreenRecordHelp, "screenrecord.bitRate", @"--bit-rate\b", "录屏码率选项");
            Option(value.Items, outputs, ProbeCommand.ScreenRecordHelp, "screenrecord.frameRate", @"--(?:frame-rate|fps)\b", "源端帧率选项");
            Option(value.Items, outputs, ProbeCommand.ScreenRecordHelp, "screenrecord.h264Stream", @"--output-format\b[\s\S]*?h264", "H.264 输出选项");
            string codecs = GoodText(outputs, ProbeCommand.DeviceCodecs);
            bool encoder = Regex.IsMatch(codecs, @"(?i)(?:OMX|c2)\.[A-Za-z0-9_.-]+(?:avc|h264)[A-Za-z0-9_.-]*(?:encoder|enc)\b");
            Add(value.Items, "encoder.avcDeclared", encoder ? Support.Supported : Support.Unknown,
                encoder ? "媒体服务列出 AVC 编码实现；并发数量和可用性尚未实测" : Reason(outputs, ProbeCommand.DeviceCodecs));
            string input = GoodText(outputs, ProbeCommand.InputNodes);
            ProbeReply inputReply = Reply(outputs, ProbeCommand.InputNodes);
            bool denied = Regex.IsMatch(inputReply.Output + "\n" + inputReply.Error, "permission denied|operation not permitted", RegexOptions.IgnoreCase);
            bool node = Regex.IsMatch(input, @"add device\s+\d+:\s*/dev/input/event\d+") && Regex.IsMatch(input, @"(?:KEY|ABS)\s*\(", RegexOptions.IgnoreCase);
            Add(value.Items, "input.getevent", denied ? Support.Unsupported : node ? Support.Supported : Support.Unknown,
                denied ? "系统拒绝读取输入节点；不提权，画面功能仍可用" : node ? "输入节点与能力可读；按键映射和事件流尚未实测" : Reason(outputs, ProbeCommand.InputNodes));
            Add(value.Items, "input.mapping", Support.Unknown, "需要人工按键、斜向、极值与松开验收");
            Add(value.Items, "screenrecord.dual", Support.Unknown, "帮助选项不能证明双编码器并发；需要双屏短录制验收");
            int sdk;
            bool oldSystem = int.TryParse(Property(props, "ro.build.version.sdk", ""), out sdk) && sdk < 33;
            Add(value.Items, "audio.playback", oldSystem ? Support.Unsupported : Support.Unknown,
                oldSystem ? "当前保留外放的系统声音后端要求 Android 13 或更新版本" : "系统版本不证明游戏声音可捕获；权限、外放和音画同步待实测");
            Add(value.Items, "preview.lowLatency", Support.Unknown, "接收帧率不是端到端延迟；需同镜头测量与30分钟观察");
            return value;
        }
        public static List<DeviceDisplay> ParseDisplays(string text)
        {
            List<DeviceDisplay> result = new List<DeviceDisplay>();
            // DisplayInfo can be duplicated in dumpsys; only accept an unambiguous local physical ID.
            foreach (Match block in Regex.Matches(text ?? "", @"(?:DisplayInfo|DisplayViewport)\{([^}]+)\}"))
            {
                string body = block.Groups[1].Value;
                Match logical = Regex.Match(body, @"\bdisplayId\s*[= ]\s*(\d+)");
                Match physical = Regex.Match(body, @"uniqueId\s*[= ]\s*['"" ]*local:(\d+)");
                Match size = Regex.Match(body, @"\breal\s+(\d+)\s*x\s*(\d+)");
                if (!size.Success) size = Regex.Match(body, @"deviceWidth=(\d+),\s*deviceHeight=(\d+)");
                int id, width, height;
                if (!logical.Success || !physical.Success || !size.Success || !int.TryParse(logical.Groups[1].Value, out id) ||
                    !int.TryParse(size.Groups[1].Value, out width) || !int.TryParse(size.Groups[2].Value, out height) || width <= 0 || height <= 0) continue;
                DeviceDisplay found = result.FirstOrDefault(d => d.LogicalId == id);
                if (found != null) { if (found.PhysicalId != physical.Groups[1].Value || found.Width != width || found.Height != height) return new List<DeviceDisplay>(); continue; }
                if (result.Any(d => d.PhysicalId == physical.Groups[1].Value)) return new List<DeviceDisplay>();
                result.Add(new DeviceDisplay { LogicalId = id, PhysicalId = physical.Groups[1].Value, Width = width, Height = height });
            }
            result.Sort((a, b) => a.LogicalId.CompareTo(b.LogicalId));
            if (result.Count == 2 && result[0].LogicalId == 0) result[1].IsLower = true;
            return result;
        }
        public static bool CacheMatches(DeviceCapabilities cached, DeviceCapabilities fresh)
        {
            if (cached == null || fresh == null || fresh.Outcome != ProbeOutcome.Success || cached.Outcome != ProbeOutcome.Success ||
                string.IsNullOrEmpty(fresh.Fingerprint) || string.IsNullOrEmpty(fresh.SerialHash) || fresh.Displays.Count == 0) return false;
            return cached.SerialHash == fresh.SerialHash && cached.Fingerprint == fresh.Fingerprint && DisplaySignature(cached) == DisplaySignature(fresh);
        }
        private static string DisplaySignature(DeviceCapabilities value)
        {
            return string.Join(";", value.Displays.OrderBy(d => d.LogicalId).Select(d => d.LogicalId.ToString(CultureInfo.InvariantCulture) + ":" + d.PhysicalId + ":" + d.Width + "x" + d.Height + ":" + d.IsLower));
        }
        public static async Task<HostCapabilities> ProbeHostAsync(ICapabilityRunner runner, CancellationToken token)
        {
            if (runner == null) throw new ArgumentNullException("runner");
            ProbeOutputs outputs = new ProbeOutputs { Outcome = ProbeOutcome.Success };
            foreach (ProbeCommand command in new[] { ProbeCommand.AdbVersion, ProbeCommand.FfmpegVersion, ProbeCommand.FfmpegDecoders, ProbeCommand.FfmpegEncoders })
            {
                ProbeReply reply = token.IsCancellationRequested ? new ProbeReply { Outcome = ProbeOutcome.Cancelled } : await RunSafe(runner, null, command, token).ConfigureAwait(false);
                outputs.Replies[command] = reply;
                if (reply.Outcome != ProbeOutcome.Success && outputs.Outcome == ProbeOutcome.Success) outputs.Outcome = reply.Outcome;
                if (reply.Outcome == ProbeOutcome.Cancelled) { outputs.Outcome = ProbeOutcome.Cancelled; break; }
            }
            return ParseHost(outputs);
        }
        public static HostCapabilities ParseHost(ProbeOutputs outputs)
        {
            HostCapabilities host = new HostCapabilities { Outcome = outputs.Outcome };
            host.AdbVersion = Version(GoodText(outputs, ProbeCommand.AdbVersion), @"Android Debug Bridge version\s+([\w.\-]+)");
            host.FfmpegVersion = Version(GoodText(outputs, ProbeCommand.FfmpegVersion), @"ffmpeg version\s+([\w.\-]+)");
            Add(host.Items, "host.adb", host.AdbVersion.Length > 0 ? Support.Supported : Reply(outputs, ProbeCommand.AdbVersion).Outcome == ProbeOutcome.NotFound ? Support.Unsupported : Support.Unknown,
                host.AdbVersion.Length > 0 ? "ADB 可执行文件已识别；未连接设备" : Reason(outputs, ProbeCommand.AdbVersion));
            Add(host.Items, "host.ffmpeg", host.FfmpegVersion.Length > 0 ? Support.Supported : Reply(outputs, ProbeCommand.FfmpegVersion).Outcome == ProbeOutcome.NotFound ? Support.Unsupported : Support.Unknown,
                host.FfmpegVersion.Length > 0 ? "FFmpeg 可执行文件已识别" : Reason(outputs, ProbeCommand.FfmpegVersion));
            Codec(host, outputs, ProbeCommand.FfmpegDecoders, "host.decode.h264", "h264");
            Codec(host, outputs, ProbeCommand.FfmpegDecoders, "host.decode.aac", "aac");
            Codec(host, outputs, ProbeCommand.FfmpegEncoders, "host.encode.h264", "libx264");
            Codec(host, outputs, ProbeCommand.FfmpegEncoders, "host.encode.aac", "aac");
            return host;
        }
        private static void Codec(HostCapabilities host, ProbeOutputs outputs, ProbeCommand command, string key, string codec)
        {
            string text = GoodText(outputs, command);
            bool present = Regex.IsMatch(text, @"(?m)^\s*[A-Z.]{6}\s+" + codec + @"\s");
            bool table = Regex.IsMatch(text, @"(?m)^\s*[A-Z.]{6}\s+\w+\s");
            Support state = present ? Support.Supported : table && !Reply(outputs, command).Truncated ? Support.Unsupported : Support.Unknown;
            Add(host.Items, key, state, present ? "已列出编解码器；实际文件仍需验证" : state == Support.Unsupported ? "完整编解码器列表未提供所需实现" : Reason(outputs, command));
        }
        private static string Version(string text, string pattern) { Match m = Regex.Match(text, pattern); return m.Success ? m.Groups[1].Value : ""; }
        private static string Property(string text, string key, string fallback)
        {
            Match match = Regex.Match(text, @"(?m)^\[" + Regex.Escape(key) + @"\]:\s*\[([^\r\n]*)\]\s*$");
            return match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : fallback;
        }
        private static ProbeReply Reply(ProbeOutputs outputs, ProbeCommand command) { ProbeReply reply; return outputs.Replies.TryGetValue(command, out reply) ? reply : new ProbeReply { Outcome = ProbeOutcome.Failed }; }
        private static string GoodText(ProbeOutputs outputs, ProbeCommand command)
        {
            ProbeReply reply = Reply(outputs, command);
            return reply.Outcome == ProbeOutcome.Success && reply.ExitCode == 0 && !reply.Truncated ? reply.Output : "";
        }
        private static void Option(Dictionary<string, CapabilityItem> items, ProbeOutputs outputs, ProbeCommand command, string key, string pattern, string title)
        {
            string text = GoodText(outputs, command);
            // Several Android help commands intentionally exit with status 1. Accept recognisable usage only.
            ProbeReply reply = Reply(outputs, command);
            if (reply.Outcome == ProbeOutcome.Failed && reply.ExitCode == 1 && !reply.Truncated && Regex.IsMatch(reply.Output + reply.Error, @"(?i)\busage:\s*(?:screenrecord|screencap)")) text = reply.Output + reply.Error;
            bool completeHelp = Regex.IsMatch(text, @"(?i)\busage:\s*(?:screenrecord|screencap)");
            bool present = completeHelp && Regex.IsMatch(text, pattern);
            Add(items, key, present ? Support.Supported : completeHelp ? Support.Unsupported : Support.Unknown,
                present ? title + "已声明；不代表实际采集成功" : completeHelp ? "帮助文本未提供" + title : Reason(outputs, command));
        }
        private static void Add(Dictionary<string, CapabilityItem> items, string key, Support state, string detail) { items[key] = new CapabilityItem { Key = key, State = state, Detail = detail }; }
        private static string Reason(ProbeOutputs outputs, ProbeCommand command)
        {
            ProbeReply reply = Reply(outputs, command);
            if (reply.Truncated) return "探测输出超限，结果未知";
            switch (reply.Outcome)
            {
                case ProbeOutcome.Cancelled: return "探测已取消，未确认能力";
                case ProbeOutcome.TimedOut: return "只读探测超时，未确认能力";
                case ProbeOutcome.Disconnected: return "设备未连接或未授权，未确认能力";
                case ProbeOutcome.NotFound: return "缺少本机依赖";
                default: return "输出为空、格式未识别或命令失败，结果未知";
            }
        }
    }
}
