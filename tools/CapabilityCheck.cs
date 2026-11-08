using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ThorCapture.Features.Capability;

namespace ThorCapture
{
    internal static class CapabilityCheck
    {
        private static int Main(string[] args)
        {
            if (args.Length != 5 || (args[0] != "--offline" && args[0] != "--host" && args[0] != "--live")) return 2;
            bool live = args[0] == "--live";
            if (live && Environment.GetEnvironmentVariable("THOR_CAPABILITY_LIVE_AUTHORIZED") != "1")
            { Console.Error.WriteLine("Live probe requires renewed explicit user authorization."); return 3; }
            string output = args[4];
            if (File.Exists(output)) { Console.Error.WriteLine("Refusing to overwrite an existing acceptance record."); return 4; }
            string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; cancel.Cancel(); };
                try
                {
                    Log.Directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)), "logs");
                    CapabilityProcessRunner runner = new CapabilityProcessRunner(args[1], args[2]);
                    StringBuilder report = new StringBuilder();
                    report.AppendLine("# Thor 设备验收记录").AppendLine().AppendLine("生成时间 UTC：" + DateTime.UtcNow.ToString("o"));
                    report.AppendLine("证据边界：" + (live ? "已授权的短时只读探测，不是录屏/声音/延迟实机验收。" : args[0] == "--host" ? "仅本机依赖探测，未连接设备。" : "离线记录模板，未运行 ADB 或 FFmpeg。"));
                    if (args[0] != "--offline")
                    {
                        HostCapabilities host = CapabilityProbe.ProbeHostAsync(runner, cancel.Token).GetAwaiter().GetResult();
                        report.AppendLine().AppendLine("## 本机环境").AppendLine().AppendLine("ADB：" + host.AdbVersion + "；FFmpeg：" + host.FfmpegVersion + "；结果：" + host.Outcome);
                        foreach (CapabilityItem item in host.Items.Values.OrderBy(i => i.Key)) report.AppendLine("- " + item.Key + "：" + item.State + "；" + item.Detail);
                    }
                    if (live)
                    {
                        DeviceCapabilities device = CapabilityProbe.ProbeAsync(args[3], runner, cancel.Token).GetAwaiter().GetResult();
                        report.AppendLine().AppendLine("## 设备档案").AppendLine().AppendLine("Serial SHA256：" + device.SerialHash);
                        // Public reports retain only a fingerprint hash, never raw device properties or child logs.
                        report.AppendLine("Fingerprint SHA256：" + CapabilityProbe.HashSerial(device.Fingerprint));
                        report.AppendLine("探测结果：" + device.Outcome);
                        foreach (DeviceDisplay display in device.Displays) report.AppendLine("- " + display.Slug + "：逻辑 " + display.LogicalId + " → 物理 " + display.PhysicalId + "；" + display.Width + "×" + display.Height);
                        foreach (CapabilityItem item in device.Items.Values.OrderBy(i => i.Key)) report.AppendLine("- " + item.Key + "：" + item.State + "；" + item.Detail);
                    }
                    report.AppendLine().AppendLine("## 手工验收（所有项初始未测试）").AppendLine();
                    report.AppendLine("| 任务 | 项目 | 状态 | 方法/数据/脱敏证据相对位置 | 限制或失败原因 |");
                    report.AppendLine("|---|---|---|---|---|");
                    foreach (string row in new[] { "P0-2|上/下/双屏截图尺寸及内容", "P0-2|单/双屏录屏停止及完整解码", "P0-2|单侧失败保留另一侧", "P0-2|带壳双屏导出及声音", "P0-3|所有键、斜向、极值、松开", "P0-3|断线清零及前台应用未知", "P0-3|CSV音画启动偏移和长期漂移", "P0-4|动态双屏档位帧率、首帧、丢帧", "P0-4|同镜头延迟中位数/P95/样本数", "P0-4|30分钟CPU/内存/180秒续流", "P3-4|系统声音权限、游戏声音、外放" })
                        report.AppendLine("| " + row.Replace("|", " | ") + " | 未测试 | 待填写 | 待填写 |");
                    report.AppendLine().AppendLine("记录设备型号/系统、USB或无线、主机/DPI、候选SHA256、尺寸/码率、场景与原始数据位置；公开前人工审查私人画面和路径。");
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
                    File.WriteAllText(temporary, report.ToString(), new UTF8Encoding(true));
                    if (File.ReadAllText(temporary).Length != report.Length) throw new IOException("Report validation failed");
                    File.Move(temporary, output);
                    Console.WriteLine("Acceptance record created; manual gates remain untested.");
                    return cancel.IsCancellationRequested ? 5 : 0;
                }
                catch { Console.Error.WriteLine("Acceptance tool failed; original record was preserved."); return 1; }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }
}
