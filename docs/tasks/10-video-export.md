# 10 号包：视频导出计划、GIF 与批量导出

- **ROADMAP 任务**：P4-6 视频片段与布局预设（`ExportPlan`）、P4-5 片段 GIF 导出、P4-2 批量导出与逐项报告。
- **批次**：第二批。**等 Track A 的 A1（FFmpeg 适配器 / 进程生命周期）和 A2（媒体模型）合并后再写实现。** P4-2 的图片部分复用 3 号包的 `ImageExporter.Save`。
- **短名**：`VideoExport`；本包拥有现有的 `src/VideoExport.cs`（保持现有两个 `Export` 重载可用），新代码放 `src/Features/VideoExport/`，测试 `tests/VideoExportFeatureTests.cs`，说明 `docs/features/video-export.md`。

## 背景
现有的 `VideoExport.Export(...)` 用 FFmpeg 合成 900×1120、30fps 的带壳视频，支持单屏和双屏、按键时间线和同步偏移；音频通过共享的 `MediaAudio.Select` 选择（只读，归 Track A）。回放（`VideoPlayback`）和导出目前各自拼装参数。**本包要做一个不可变的 `ExportPlan`，让回放预览和导出使用同一份计划**（ROADMAP P3-5 / P4-6）。

## P4-6 ExportPlan

```csharp
internal sealed class ExportPlan                                  // 不可变：用 Builder 或构造函数创建
{
    public IList<ExportTrack> Tracks;                             // 每轨：源文件、屏幕角色、偏移
    public TimeSpan Start, Duration;                              // 片段区间
    public VideoLayout Layout;                                    // 原片单屏 / 上下 / 左右 / 带壳
    public DeviceSkin Skin;                                       // 快照，包含灯光
    public Size OutputSize; public int Fps; public int BitRate;
    public double InputOffsetMs; public EndPolicy End;             // 较短的一轨结束，还是冻结 / 留空另一屏
    public string Describe();                                     // 输出尺寸、时长、是否转码，用于界面展示
}
internal static class VideoExporter
{
    public static Task<ExportResult> ExportAsync(ExportPlan plan, string destination, IProgress<int> progress, CancellationToken token);
    public static ExportPlan Legacy900x1120(...);                 // 与现有 Export 输出兼容的预设
}
```

要点：
- 原片永远不改；输出先写临时文件，校验通过后再替换；取消只结束本任务拥有的 FFmpeg 进程。
- 精确裁剪需要转码，要说明原因；「复制模式」受关键帧限制，也要说明。
- 保留现有 900×1120 / 30fps 的兼容预设，确保旧的导出结果不变。

## P4-5 GIF / 短片

- 在 `ExportPlan` 基础上输出 GIF（palettegen / paletteuse）或体积更小的 MP4 / WebM；参数包括起止时间、帧率、宽度、调色板。
- 导出前根据计划估算文件大小（给出一个范围，说明是估算）。
- 缺少 FFmpeg 或缺少所需编码器时，**导出前就禁用并说明原因**，不能跑了很久才报错。

## P4-2 批量导出

```csharp
internal sealed class BatchJob { public string SourceMediaId; public object Plan; }        // ImageExportPlan 或 ExportPlan
internal static class BatchExporter
{
    public static Task<BatchReport> RunAsync(IList<BatchJob> jobs, string directory, NameTemplate template, ConflictPolicy conflicts, IProgress<BatchProgress> progress, CancellationToken token);
    // 重视频任务同一时间只跑一个；逐项结果（成功 / 跳过 / 失败 + 原因）；可以重试失败项
}
```

要点：部分失败时保留成功项；取消后保留已完成的；磁盘写满或目录不可写时提前提示；**进度不能冒充「已保存」**。

## 测试
- 用合成视频（参照 `tests/fixtures/recording-preview.mp4`）测试：非整秒区间、两轨长度不同、正负偏移、取消、逐帧解码首尾。
- 兼容预设与现有 `Export` 的输出一致（尺寸、帧率、时长、抽样帧的像素）。
- GIF：高变化、静态、估算大小与实际大小对比（记录误差）。
- 批量：同名、取消、源文件丢失、长路径、磁盘写满（模拟）。
- 需要 FFmpeg 的测试放进 `-Stream` 组（参照现有脚本）；缺少 FFmpeg 时明确跳过。

## 集成说明需要写
- 导出对话框的字段、默认值和预设。
- 回放工作台如何用同一份 `ExportPlan` 预览。
- 批量导出的进度和报告界面。
- 能力检测结果对应的禁用原因。
