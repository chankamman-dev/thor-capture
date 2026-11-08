# 7 号包：设备监视、安全重连与无线 ADB

- **ROADMAP 任务**：P2-6 设备监视与安全重连、P2-7 无线 ADB 配对与连接（C 级，先确认固件支持）。
- **批次**：第二批。**等 Track A 的 A1（`IAdbClient` / 进程生命周期）和 A3（`OperationGate`）合并后再写实现**；在此之前可以先研究 `adb track-devices` 的输出格式和 Thor 的无线调试流程，并写好设计。
- **短名**：`DeviceWatch`；代码 `src/Features/DeviceWatch/`，测试 `tests/DeviceWatchTests.cs`，说明 `docs/features/device-watch.md`。

## P2-6 设备监视

```csharp
internal enum DeviceLinkState { Device, Unauthorized, Offline, NoPermission, Missing, Unknown }
internal sealed class DeviceEntry { public string Serial; public DeviceLinkState State; public bool Wireless; }
internal sealed class DeviceWatcher : IDisposable
{
    public DeviceWatcher(IAdbClient adb);                  // A1 提供
    public IList<DeviceEntry> Current { get; }
    public event EventHandler<DeviceListChangedEventArgs> Changed;   // 在后台线程触发；带新增 / 移除 / 状态变化列表
    public void Start(); public void Stop();
    public WatchMode Mode { get; }                          // TrackDevices / Polling / Stopped，出错时降级为有界轮询
}
```

要点：
- 优先用 `adb track-devices`（输出是 4 位十六进制长度前缀加一段文本块）；进程退出后退避重启，失败则退回有界轮询，并把模式报告给界面。
- **不自动执行 `kill-server`**，不结束其他程序的 adb。
- 设备消失时只发出事件，由 Track A 停止相关的流和输入并留下恢复记录；本包**不去停录制、不切换设备**。多台设备时不强行切换正在采集的 serial。
- 快速反复插拔不能造成事件风暴：要合并事件、做去抖。

## P2-7 无线 ADB

```csharp
internal static class WirelessAdb
{
    public static bool TryParseEndpoint(string text, out string host, out int port);   // 配对地址和连接地址分开
    public static Task<WirelessResult> PairAsync(IAdbClient adb, string pairEndpoint, string code, CancellationToken token);
    public static Task<WirelessResult> ConnectAsync(IAdbClient adb, string endpoint, CancellationToken token);
    public static Task<WirelessResult> DisconnectAsync(IAdbClient adb, string endpoint, CancellationToken token);
    public static IList<string> Recent(); public static void Remember(string endpoint); public static void Forget(string endpoint);  // 设置键 wireless.recent，最多 5 个
}
```

要点：
- **配对码绝不写进日志、设置或错误信息**。
- 不默认把 USB 调试切换为网络调试（不执行 `adb tcpip`），不做任何降低设备网络安全的操作。
- 结果要区分：地址错误、配对码错误、超时、设备拒绝、固件不支持，每种都有中文说明和下一步提示。
- 先在实机上确认固件是否支持官方的无线调试流程（Android 11+ 配对码）；不支持就只提供「连接已配置的地址」，或者向用户建议延期。

## 测试
- 固定样本解析 `track-devices` 流（多台设备、未授权 → 已授权、离线、截断的数据块）。
- 假 ADB 模拟：进程退出后重启、退避、降级为轮询、快速插拔时事件被合并。
- 无线：地址解析、各类失败输出，以及配对码不出现在日志中（测试时读取日志文件检查）。
- 实机（经同意）：插拔、授权变化、adb server 重启、无线配对 / 连接 / 断线重连；无线连接下截图可用（性能单独记录）。

## 集成说明需要写
- 设备菜单需要的状态文案（未授权、离线、无权限各显示什么，下一步怎么做）。
- 无线对话框的字段、步骤、错误提示，以及可信网络的提醒文案。
- 设备消失或恢复时，界面和 `OperationGate` 应采取的动作建议。
