// Thor screen grabber: pick a screen, capture it, browse what you captured.
//
// The one thing this tool exists to get right is the display id. screencap -d
// wants the *physical* display id, not the logical one; passing the logical id
// silently writes a zero-byte file. So the screens are always enumerated from
// dumpsys rather than typed in by hand.
//
// Built with the in-box .NET Framework compiler, so the language level is C# 5:
// no string interpolation, no expression-bodied members, no null-conditionals.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThorCapture
{
    internal class DeviceDisplay
    {
        public int LogicalId;
        public string PhysicalId;
        public int Width;
        public int Height;
        public bool IsLower;

        public string Label
        {
            get
            {
                if (LogicalId == 0) return "上屏";
                return IsLower ? "下屏" : "屏幕 " + LogicalId;
            }
        }

        public string Slug
        {
            get
            {
                if (LogicalId == 0) return "upper";
                return IsLower ? "lower" : "display-" + LogicalId;
            }
        }

        public override string ToString()
        {
            return string.Format("{0}  {1}×{2}", Label, Width, Height);
        }
    }

    internal class AdbResult
    {
        public int Code;
        public string Out = "";
        public string Err = "";
    }

    internal static class Adb
    {
        public static string ExePath = Locate();

        private static string Locate()
        {
            List<string> candidates = new List<string>();
            candidates.Add(Environment.GetEnvironmentVariable("THOR_ADB"));
            candidates.Add(Path.Combine(AppDir(), "adb.exe"));
            candidates.Add(Path.Combine(AppDir(), @"platform-tools\adb.exe"));
            string sdk = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? Environment.GetEnvironmentVariable("ANDROID_HOME");
            if (!string.IsNullOrEmpty(sdk)) candidates.Add(Path.Combine(sdk, @"platform-tools\adb.exe"));
            try
            {
                string beside = Path.Combine(AppDir(), "adb-path.txt");
                if (File.Exists(beside)) candidates.Add(File.ReadAllText(beside).Trim());
            }
            catch { }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Android\Sdk\platform-tools\adb.exe"));
            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;
            }
            return "adb"; // last resort: whatever is on PATH
        }

        public static string AppDir()
        {
            return Path.GetDirectoryName(Application.ExecutablePath);
        }

        public static AdbResult Run(string arguments, int timeoutMs)
        { return Run(arguments, timeoutMs, CancellationToken.None); }
        internal static Task<AdbResult> RunAsync(string arguments, int timeoutMs, CancellationToken token)
        { return Task.Run(() => Run(arguments, timeoutMs, token), token); }
        internal static AdbResult Run(string arguments, int timeoutMs, CancellationToken token)
        {
            try {
                string serial = Backend.DeviceTransportGate.SerialOf(arguments);
                // A long encoder owns its own lifecycle; serialize startup, not its entire lifetime.
                if (arguments.Contains("exec screenrecord")) return RunCore(arguments, timeoutMs, serial, token);
                return Backend.DeviceTransportGate.Execute(serial, TimeSpan.FromMilliseconds(timeoutMs), token, () => RunCore(arguments, timeoutMs, null, token));
            }
            catch (Exception error) { return new AdbResult { Code = -1, Err = error is TimeoutException ? "设备操作排队超时" : "设备目标或协调状态无效" }; }
        }

        private static readonly Backend.ProcessRunner textJobs = new Backend.ProcessRunner();
        private static readonly Backend.BinaryProcessRunner binaryJobs = new Backend.BinaryProcessRunner();
        private static readonly object cleanupGate = new object();
        private static readonly HashSet<string> cleanupSerials = new HashSet<string>();
        private static void RetainCleanup(string serial)
        { if (serial != null) { lock (cleanupGate) cleanupSerials.Add(serial); Backend.DeviceTransportGate.Block(serial); } }
        // Final shutdown only: these shared runners cannot be reopened after stopping.
        internal static async Task<bool> StopOwnedAsync(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException("timeout");
            Task timer = Task.Delay(timeout);
            Task text = Task.Run(() => textJobs.StopAllAsync());
            Task<bool> binary = Task.Run(() => binaryJobs.StopAllAsync(timeout, CancellationToken.None));
            Task all = Task.WhenAll(text, binary);
            Task observation = all.ContinueWith(done => { if (done.IsFaulted) GC.KeepAlive(done.Exception); }); GC.KeepAlive(observation);
            if (await Task.WhenAny(all, timer).ConfigureAwait(false) != all) return false;
            try { await all.ConfigureAwait(false); if (!await binary.ConfigureAwait(false) || textJobs.ActiveCount != 0) return false; }
            catch { return false; }
            lock (cleanupGate) { foreach (string serial in cleanupSerials) Backend.DeviceTransportGate.Unblock(serial); cleanupSerials.Clear(); }
            return true;
        }
        private static AdbResult RunCore(string arguments, int timeoutMs, string startupSerial, CancellationToken token)
        {
            string serial = Backend.DeviceTransportGate.SerialOf(arguments);
            Backend.ProcessResult result = textJobs.RunAsync(new Backend.ProcessRequest(ExePath, "adb.legacy",
                Backend.LegacyArguments.Parse(arguments), null, startupSerial), TimeSpan.FromMilliseconds(timeoutMs), token).GetAwaiter().GetResult();
            if (!result.CleanupComplete) { RetainCleanup(serial); return new AdbResult { Code = -1, Err = "设备操作清理尚未完成" }; }
            if (result.Outcome != Backend.ProcessOutcome.Success) return new AdbResult { Code = result.ExitCode == 0 ? -1 : result.ExitCode,
                Out = result.Output, Err = result.Outcome == Backend.ProcessOutcome.TimedOut ? "adb 超时" : result.Error };
            if (result.Truncated) return new AdbResult { Code = -1, Err = "adb 输出超过限制" };
            return new AdbResult { Code = result.ExitCode, Out = result.Output, Err = result.Error };
        }

        internal static byte[] RunBinary(string arguments, int timeoutMs, out string error)
        {
            string detail = ""; byte[] result = null;
            try { result = Backend.DeviceTransportGate.Execute(Backend.DeviceTransportGate.SerialOf(arguments), TimeSpan.FromMilliseconds(timeoutMs), CancellationToken.None, () => RunBinaryCore(arguments, timeoutMs, out detail)); }
            catch (Exception failure) { detail = failure is TimeoutException ? "设备操作排队超时" : "设备目标或协调状态无效"; }
            error = detail; return result;
        }

        internal static void StartBoundProcess(Process process, string serial, int timeoutMs)
        {
            Backend.DeviceSessionRegistry.ValidateSerial(serial);
            if (Backend.DeviceTransportGate.SerialOf(process.StartInfo.Arguments) != serial) throw new ArgumentException("Process target mismatch");
            Backend.DeviceTransportGate.Execute(serial, TimeSpan.FromMilliseconds(timeoutMs), CancellationToken.None, () => process.Start());
        }

        private static byte[] RunBinaryCore(string arguments, int timeoutMs, out string error)
        {
            Backend.BinaryProcessResult result = binaryJobs.RunBytesAsync(new Backend.ProcessRequest(ExePath, "adb.binary",
                Backend.LegacyArguments.Parse(arguments)), 64 * 1024 * 1024, TimeSpan.FromMilliseconds(timeoutMs), CancellationToken.None).GetAwaiter().GetResult();
            error = result.Error;
            if (!result.CleanupComplete) { RetainCleanup(Backend.DeviceTransportGate.SerialOf(arguments)); error = "设备操作清理尚未完成"; return null; }
            if (result.Outcome != Backend.BinaryProcessOutcome.Success) { if (string.IsNullOrWhiteSpace(error)) error = result.Outcome == Backend.BinaryProcessOutcome.TimedOut ? "adb 超时" : "设备读取未完成"; return null; }
            return result.Output;
        }

        public static List<string> Devices()
        {
            List<string> list = new List<string>();
            AdbResult result = Run("devices", 10000);
            if (result.Code != 0) throw new IOException(Describe(result));
            List<string> unavailable = new List<string>();
            foreach (string line in result.Out.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("List of devices")) continue;
                string[] parts = trimmed.Split('\t');
                if (parts.Length >= 2 && parts[1].Trim() == "device") list.Add(parts[0].Trim());
                else if (parts.Length >= 2) unavailable.Add(parts[0] + "：" + parts[1].Trim());
            }
            if (list.Count == 0 && unavailable.Count > 0)
                throw new IOException(string.Join("；", unavailable) + "。请解锁掌机允许 USB 调试；offline 请重连 USB。");
            return list;
        }

        // Physical ids and sizes both come out of the viewport list, so the two
        // can never drift apart.
        public static List<DeviceDisplay> Displays(string serial)
        {
            List<DeviceDisplay> found = new List<DeviceDisplay>();
            AdbResult result = Run("-s " + serial + " shell dumpsys display", 20000);
            if (result.Code != 0) throw new IOException(Describe(result));
            return ParseDisplays(result.Out);
        }

        internal static List<DeviceDisplay> ParseDisplays(string output)
        {
            List<DeviceDisplay> found = new List<DeviceDisplay>();
            HashSet<string> seen = new HashSet<string>();
            foreach (Match viewport in Regex.Matches(output, @"DisplayViewport\{([^}]*)\}"))
            {
                string body = viewport.Groups[1].Value;
                Match logical = Regex.Match(body, @"displayId=(\d+)");
                Match physical = Regex.Match(body, @"uniqueId='local:(\d+)'");
                Match width = Regex.Match(body, @"deviceWidth=(\d+)");
                Match height = Regex.Match(body, @"deviceHeight=(\d+)");
                if (!logical.Success || !physical.Success) continue;
                if (!seen.Add(physical.Groups[1].Value)) continue;
                DeviceDisplay display = new DeviceDisplay();
                display.LogicalId = int.Parse(logical.Groups[1].Value);
                display.PhysicalId = physical.Groups[1].Value;
                if (width.Success) display.Width = int.Parse(width.Groups[1].Value);
                if (height.Success) display.Height = int.Parse(height.Groups[1].Value);
                found.Add(display);
            }
            found.Sort(delegate(DeviceDisplay a, DeviceDisplay b) { return a.LogicalId.CompareTo(b.LogicalId); });
            // Android can allocate a new logical id whenever the lower panel reconnects.
            if (found.Count == 2 && found[0].LogicalId == 0) found[1].IsLower = true;
            return found;
        }

        private static bool IsPng(byte[] data)
        {
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (data == null || data.Length < 33) return false;
            for (int i = 0; i < signature.Length; i++) if (data[i] != signature[i]) return false;
            byte[] end = { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 };
            for (int i = 0; i < end.Length; i++) if (data[data.Length - end.Length + i] != end[i]) return false;
            try
            {
                using (MemoryStream stream = new MemoryStream(data))
                using (Image decoded = Image.FromStream(stream, true, true))
                using (Bitmap bitmap = new Bitmap(decoded)) return bitmap.Width > 0 && bitmap.Height > 0;
            }
            catch { return false; }
        }

        public static byte[] Capture(string serial, string physicalId, out string error)
        {
            // exec-out streams the PNG straight back, no temp file on the device.
            byte[] streamed = RunBinary("-s " + serial + " exec-out screencap -p -d " + physicalId, 40000, out error);
            if (IsPng(streamed)) return streamed;

            // Older adb builds mangle binary stdout; fall back to write-then-pull.
            string remote = "/data/local/tmp/thor-capture-" + Guid.NewGuid().ToString("N") + ".png";
            AdbResult shot = Run("-s " + serial + " shell screencap -p -d " + physicalId + " " + remote, 40000);
            if (shot.Code != 0)
            {
                error = Describe(shot);
                Run("-s " + serial + " shell rm -f " + remote, 5000);
                return null;
            }
            string temp = Path.Combine(Path.GetTempPath(), "thor-capture-" + Guid.NewGuid().ToString("N") + ".png");
            AdbResult pull = Run("-s " + serial + " pull " + remote + " \"" + temp + "\"", 40000);
            try
            {
                if (pull.Code == 0 && File.Exists(temp))
                {
                    byte[] pulled = File.ReadAllBytes(temp);
                    if (IsPng(pulled)) return pulled;
                    error = "截图为空。物理显示 ID 可能不对——逻辑 ID 不能直接用于 screencap -d。";
                    return null;
                }
                error = Describe(pull);
                return null;
            }
            catch (Exception failure)
            {
                error = failure.Message;
                return null;
            }
            finally
            {
                Run("-s " + serial + " shell rm -f " + remote, 5000);
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static string Describe(AdbResult result)
        {
            string text = (result.Err == null ? "" : result.Err.Trim());
            if (text.Length == 0) text = (result.Out == null ? "" : result.Out.Trim());
            if (text.Length == 0) text = "adb 退出码 " + result.Code;
            return text;
        }
    }


    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
