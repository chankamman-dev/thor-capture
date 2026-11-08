# 9 号包：标签收藏、组级事务与导入机内素材

- **ROADMAP 任务**：P5-3 收藏、标签与游戏分组，P5-5 组级改名、移动与删除事务，P2-5 导入机器内截图与视频。
- **批次**：第二批。**等 Track A 的 A1（`IAdbClient`）和 A2（媒体 ID / `MediaRepository` / 事务接口，含 P5-1 增量索引）合并后再写实现。** 顺序建议：P5-5 → P5-3 → P2-5（导入依赖事务能力）。
- **短名**：`Library`；代码 `src/Features/Library/`，测试 `tests/LibraryTests.cs`，说明 `docs/features/library.md`。

## P5-5 组级事务（数据安全，最优先）

```csharp
internal sealed class GroupOperationPlan                        // 执行前先生成计划，给用户预览
{
    public IList<PlannedMove> Moves;                            // 每个文件（原片、另一块屏、.thor-meta、.input.csv、音频、派生文件）的源 → 目标
    public IList<string> Conflicts;                             // 同名、被占用、跨卷等，用中文说明
    public bool RequiresConfirmation;                           // 永久删除必须显式确认
}
internal static class GroupOperations
{
    public static GroupOperationPlan PlanRename(IMediaRepository repo, IList<string> mediaIds, string template);   // 模板如 {game}-{date}-{screen}
    public static GroupOperationPlan PlanMove(IMediaRepository repo, IList<string> mediaIds, string targetDirectory);
    public static GroupOperationPlan PlanRecycle(IMediaRepository repo, IList<string> mediaIds);
    public static Task<GroupOperationResult> ExecuteAsync(GroupOperationPlan plan, IProgress<int> progress, CancellationToken token);  // 写恢复日志；中断后可继续或回滚
}
```

要点：
- 同组文件一起处理，**不误伤别的组**；每一步写恢复日志，中断后可以找回半组。
- 跨卷移动：先复制并校验（大小加哈希），再把源文件移入回收站。
- 删除默认进回收站；没有回收站的卷（如网络盘）要明确提示，由用户确认。
- 文件被占用、只读、路径太长时，给出逐项结果，不整体失败。

## P5-3 收藏、标签、游戏分组

```csharp
internal static class MediaTags
{
    public static void SetFavorite(IMediaRepository repo, IList<string> mediaIds, bool favorite);
    public static void AddTags(IMediaRepository repo, IList<string> mediaIds, IList<string> tags); public static void RemoveTags(...);
    public static IList<GameGroup> GroupByGame(IList<MediaItem> items);    // 用前台包名或别名；未知单独成组，可以人工归类
}
```

要点：数据跟媒体 ID 走，改名、移动后标签不丢；同组可以统一打标签；重建索引时保留用户数据；不联网识别游戏。

## P2-5 导入机内素材

```csharp
internal static class DeviceImport
{
    public static Task<IList<RemoteMedia>> ScanAsync(IAdbClient adb, string serial, IList<string> directories, CancellationToken token); // 只扫用户选定的目录
    public static IList<ImportCandidate> Compare(IList<RemoteMedia> remote, IMediaRepository repo);   // 按「名称 + 大小 + 修改时间」判断重复
    public static Task<ImportResult> ImportAsync(IAdbClient adb, string serial, IList<ImportCandidate> selected, string directory, IProgress<int> progress, CancellationToken token);
}
```

要点：
- 默认目录要先在实机上确认（常见的是 `/sdcard/Pictures/Screenshots`、`/sdcard/DCIM/Screenshots`），允许用户修改；**不扫描无关的私人目录**。
- 先拉取到 `.partial`，校验后再入库；中断后能续上。
- 默认保留设备上的源文件。「导入后删除」是另一个操作，本期不做。
- 导入的素材屏幕标记为「未知」，来源标记为「设备导入」。

## 测试
- 事务：同名、文件被占用、部分移动后中断再恢复、跨卷复制校验、无回收站的卷、旧图库（没有媒体 ID）。
- 标签：改名后保留、重建索引后保留、重复标签、别名变化。
- 导入：假 ADB 提供文件列表和拉取；中文文件名、同名、断线、重试、不可读文件。
- 实机（经同意）：确认截图目录，导入 10 个文件，重复导入不产生新文件。

## 集成说明需要写
- 计划预览对话框需要展示的内容。
- 标签编辑和游戏分组在图库中的入口、筛选方式。
- 导入向导的步骤和进度。
- 哪些危险操作在首发版中应该收窄或隐藏（对应 ROADMAP P5-5 的首发门槛）。
