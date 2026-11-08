# 1 号包：实机验收与设备能力档案

- **ROADMAP 任务**：P0-2、P0-3、P0-4（实机验收）、P0-5（能力档案）。验收标准以 ROADMAP 正文为准。
- **批次**：第一批，现在可以开始。P0-5 的解析部分可以离线做；P0-2/3/4 需要连 Thor，**先问用户**。
- **短名**：`Capability`；代码放 `src/Features/Capability/`，测试 `tests/CapabilityTests.cs`，说明 `docs/features/capability.md`，证据 `evidence/<日期>-pkg1/`。

## 目标

1. 用当前版本在真机上把截图、录屏、预览、输入、前台应用走一遍，**用数据说话**：哪些能用，哪些不能，限制是什么。
2. 做一份「设备能力档案」，让界面能按实际能力启用或禁用功能，并说明原因。

## P0-5 能力档案（先做，离线为主）

建议接口：

```csharp
internal enum Support { Supported, Unsupported, Unknown }
internal sealed class CapabilityItem { public string Key; public Support State; public string Detail; }   // Detail 是给用户看的中文原因
internal sealed class DeviceCapabilities
{
    public string SerialHash;              // 脱敏：只保存哈希，不保存完整 serial
    public string Model, AndroidRelease, Fingerprint;
    public List<DeviceDisplay> Displays;
    public Dictionary<string, CapabilityItem> Items;   // 例如 "screencap.display", "screenrecord.displayId", "screenrecord.dual", "input.getevent", "audio.playback"
    public DateTime ProbedUtc;
}
internal static class CapabilityProbe
{
    public static Task<DeviceCapabilities> ProbeAsync(string serial, CancellationToken token);   // 只读探测，可取消
    public static DeviceCapabilities Parse(ProbeOutputs outputs);                            // 纯解析，便于用固定样本测试
    public static HostCapabilities ProbeHost();                                              // adb / ffmpeg 版本、ffmpeg 解码器
}
```

要点：
- 只读：`getprop`、`dumpsys display`、`screenrecord --help`、`getevent -lp`（权限不够就记为 Unsupported 并写原因），不启动长时间录制。
- 结果缓存到 `evidence` 之外的位置（交给 Track A 的设置 / 数据目录，在集成说明里写需求）；设备的 fingerprint 或屏幕变化时缓存失效。
- 「未知」不能当作「支持」。
- 固定样本放 `tests/fixtures/capability/`，要覆盖：多种 dumpsys 格式、权限拒绝、空输出、屏幕数量变化。

## P0-2 / P0-3 / P0-4 实机验收

按 ROADMAP 各条的「范围」和「验收」逐项执行，每项记录：设备型号与系统版本、连接方式、命令、结果、原始数据位置。

建议的产出结构（`evidence/<日期>-pkg1/`）：
- `acceptance.md`：总表，每项是通过、失败还是有限制，外加原因和截图。
- `p0-2-capture/`：两块屏的截图、录屏片段、FFmpeg 解码日志、启动时间差。
- `p0-3-input/`：按键映射表、斜向和极值测试、断线表现、前台包名样本、同步误差的测量方法和数据。
- `p0-4-preview/`：各档位的接收帧率和绘制帧率、首帧时间、延迟测量方法（同镜头拍实机和预览，或可重复的时间标记）、中位数 / P95 / 样本数、30 分钟资源曲线。

要点：
- **测试发现的缺陷不要在本包里改 Track A 的文件**：写进 `acceptance.md`，并附复现步骤，由 Claude 修复。
- 脱敏：公开摘要里不出现完整 serial、用户名路径，也不出现可识别的私人画面。
- 如果需要更好的测量工具（例如延迟计时画面），可以在 `tools/` 新增脚本或小程序。

## 测试
- `CapabilityTests.Run()`：用固定样本测试解析；用假 ADB 测试「拒绝权限、空输出、超时、取消」都能得到明确结果。
- `CapabilityTests.RunLive()`：真机探测一次，打印档案摘要（已脱敏）。

## 集成说明需要写
- 每个能力键影响哪个界面功能、不支持时显示什么中文原因。
- 探测的触发时机（连接时、手动刷新）和大致耗时。
- 验收结论建议如何更新 P0-2/3/4/5、P3-2、P3-3 的状态。
