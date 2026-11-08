# 4 号包：缩略图磁盘缓存与相似图检查

- **ROADMAP 任务**：P5-2 磁盘缩略图缓存与按需加载（缓存组件）、P5-6 相似图片检查（算法与候选分组）。图库的滚动加载、优先级接线，以及相似图的审阅界面由 Track A 完成。
- **批次**：第一批，不需要设备。与 Track A 的 P5-1 / P1-10 只在集成时对接，本包不依赖它们。
- **短名**：`Thumbs`（缓存）、`Similarity`（相似图）；代码 `src/Features/Thumbs/`、`src/Features/Similarity/`，测试 `tests/ThumbsTests.cs`、`tests/SimilarityTests.cs`，说明 `docs/features/thumbs.md`。

## P5-2 缩略图缓存

```csharp
internal sealed class ThumbnailKey { public string Path; public long Length; public DateTime WriteUtc; public int Size; public int RenderVersion; }
internal sealed class ThumbnailCache : IDisposable
{
    public ThumbnailCache(string directory, long maxBytes);           // 目录由 Track A 的数据目录决定
    public bool TryGet(ThumbnailKey key, out Bitmap thumb);            // 只读磁盘缓存，命中即返回
    public void Request(ThumbnailKey key, ThumbnailPriority priority); // 可见项优先；有界队列，旧请求可被新请求挤掉
    public void Cancel(string path);
    public event EventHandler<ThumbnailReadyEventArgs> Ready;         // 在后台线程触发；Bitmap 归接收方所有
    public void Trim();                                               // LRU，超过空间上限时删除最久未用的
}
```

要点：
- 键包含源文件的「长度 + 修改时间 + 尺寸 + 渲染版本」，源文件变化后旧缓存自动失效。
- 图片解码不锁住源文件（参照现有 `MainForm.LoadUnlocked` 的做法）；视频通过 Windows 缩略图接口获取（参照现有 `ShellThumbnails`），失败时给占位图，不抛到界面。
- 缓存文件损坏或目录只读时降级为只用内存，不崩溃。
- 工作线程数有上限，绝不全库一次性解码。

性能验收（ROADMAP 参考目标）：用 100 / 1,000 / 10,000 项合成素材分别测冷启动和热启动的首屏时间、CPU 和内存，写进集成说明，并注明机器和素材类型。

## P5-6 相似图检查

```csharp
internal static class ImageHash { public static ulong Exact(string path); public static ulong DHash(Image image); public static int Distance(ulong a, ulong b); }
internal sealed class SimilarGroup { public List<string> Paths; public int MaxDistance; public string Reason; }  // Reason 用中文说明为什么判为相似
internal static class SimilarityFinder
{
    public static Task<IList<SimilarGroup>> FindAsync(IList<string> images, int threshold, IProgress<int> progress, CancellationToken token);
}
```

要点：
- 区分「完全相同」（精确哈希）和「相似」（dHash 距离）。**只给出候选，不自动删除，也不给出删哪张的结论。**
- 上屏和下屏不互相比较；尺寸不同的只在显式允许时比较。
- 黑屏和纯色画面单独归类，不要和正常画面混在一起。
- 视频只是首帧相同，不能判为重复视频（本包先只处理图片）。

## 测试
- 缓存：命中、源文件改动后失效、LRU 空间上限、缓存损坏、只读目录、取消、快速连续请求时队列有界。
- 相似：同图不同格式、轻微 UI 变化、黑屏、不同尺寸、上下屏隔离、阈值边界、取消。

## 集成说明需要写
- 推荐的缓存大小和目录位置。
- 图库应如何按「可见优先」发起请求。
- 相似结果界面需要展示的信息（差异说明、所属屏幕、时间），以及推荐的默认阈值。
