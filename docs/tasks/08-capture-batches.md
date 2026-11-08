# 8 号包：双屏并行截图、定时与连拍

- **ROADMAP 任务**：P2-1 双屏并行截图与批次时序、P2-4 定时截图与有限连拍。
- **批次**：第二批。**等 Track A 的 A1（`IAdbClient`）、A2（`CaptureBatch` / `MediaRepository`）、A3（`OperationGate`）合并后再写实现。** 实机对比需要 1 号包 P0-2 测出的依次截图基线。
- **短名**：`Capture`；代码 `src/Features/Capture/`，测试 `tests/CaptureFeatureTests.cs`，说明 `docs/features/capture.md`。

## 背景
现在的截图流程在 `MainForm.CaptureNow` 里：先读前台应用，再依次截每块屏，写 PNG 和 `.thor-meta.txt`。Track A 在 P1-3 会把它提取成 `CaptureService`；本包在这个基础上实现并行截图和连拍，文件命名和元数据必须与现有格式兼容（见 AGENTS.md「文件命名」）。

## P2-1 并行截图

```csharp
internal sealed class CaptureRequest { public string Serial; public IList<DeviceDisplay> Displays; public string Directory; public bool Parallel; public bool ReadForeground; }  // 开始时固定，不读全局状态
internal sealed class TrackShot { public DeviceDisplay Display; public string Path; public TimeSpan Issued, Completed; public CaptureOutcome Outcome; public string Reason; }
internal static class ParallelCapture
{
    public static Task<CaptureBatchResult> RunAsync(IAdbClient adb, CaptureRequest request, CancellationToken token);  // 每块屏各自超时；部分成功也保存
}
```

要点：
- 两块屏同时发起，每块屏有自己的超时；一块失败不影响另一块，结果按屏幕分别记录。
- 记录每块屏的「发起 / 完成」时间（单调时钟），写进批次元数据。**电脑端的命令时间差不等于画面采样时间差**，文案和说明里不能写「同步」。
- 设备不支持并发，或并发不稳定时，退回依次截图，并在结果里记录实际使用的模式。

## P2-4 定时与连拍

```csharp
internal sealed class BurstPlan { public TimeSpan Countdown; public TimeSpan Interval; public int Count; public long MaxBytes; }   // 必须有数量上限
internal sealed class BurstRun : IDisposable
{
    public BurstRun(Func<CancellationToken, Task<CaptureBatchResult>> shoot, BurstPlan plan);
    public event EventHandler<BurstProgressEventArgs> Progress;   // 在后台线程触发：第几张、下一张倒计时、跳过次数
    public Task<BurstSummary> RunAsync(CancellationToken token);  // 取消后不再开始下一张；已完成的保留
}
```

要点：
- 用单调时钟计时。上一张太慢时跳过或顺延（二选一，写明规则），**不排无限队列**。
- 每张照片都是一个独立的截图批次，文件名唯一；连拍用同一个连拍编号，方便图库折叠显示。
- 设备断开、磁盘写满、窗口关闭时停止，并保留已经完成的部分。
- 开始前根据数量和单张大小估计占用空间，超过上限时拒绝。

## 测试
- 假 ADB 加上可控延迟：一块屏延迟、一块拒绝、超时、晚到的结果（取消后才返回）不被写入。
- 连拍：取消时机、太慢时跳过或顺延、数量上限、空间估计、断线中止。
- 与现有文件命名和配对的兼容性：新生成的文件在旧图库里仍然能配对。
- 实机（经同意）：动态画面下比较并行与依次截图两张图的时间差，连续 50 次并行截图的失败率。

## 集成说明需要写
- 顶栏或菜单需要的控件：连拍间隔、数量、倒计时，以及运行中的进度和停止按钮。
- 每种失败的中文提示。
- 与录屏和预览的互斥需求（由 `OperationGate` 实现）。
