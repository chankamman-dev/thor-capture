# 4号包交付：缩略图缓存与相似图片候选

2026-09-27。Track B 功能核心，供 Claude / Track A 审查、合并和界面集成；本包没有改主窗口、图库、框架或既有工具。P5-2 发布必需部分已有组件，P5-6 是可选择接入的 R3 功能。本包不连接 ADB，不修改原片，不执行删除。

## 入口与所有权

`ThorCapture.Features.Thumbs`：

```csharp
ThumbnailKey key = ThumbnailKey.FromFile(path, 160, 1);
ThumbnailCache cache = new ThumbnailCache(cacheDirectory, 256L * 1024 * 1024);
cache.Ready += OnThumbnailReady;
cache.Request(key, ThumbnailPriority.Visible);
// OnThumbnailReady: validate the gallery generation and marshal to the UI thread.
// Dispose e.Bitmap if the item left the viewport, the generation changed, or transfer failed.
// Otherwise transfer ownership to the gallery; dispose the previous gallery bitmap.
cache.Cancel(path);
cache.Dispose();
```

- `ThumbnailKey` 不可变：规范化完整路径、实际 `FileInfo.Length`、UTC 修改时间、最大边长、渲染版本。`FromFile` 可能因源消失/无权限抛文件异常，调用方显示普通占位并由目录索引重试。尺寸范围16–1024；推荐160，DPI变化时重新计算并生成新键。
- `Request` 非阻塞，默认一个 STA 后台工作线程、最多64排队请求；可见优先，队列满时先挤掉最旧后台项。全可见时新可见项挤掉最旧可见项，后台请求被忽略。相同键合并；提升排队请求优先级。
- `Ready` **在后台工作线程触发**，不可直接修改 WinForms。每个订阅者独立拥有自己的 `Bitmap`。无订阅者时不泄漏结果。监听器异常被记录，工作线程继续；监听器应快速返回，使用 `BeginInvoke`，避免阻塞或在事件内等待 UI。
- `ThumbnailReadyEventArgs.Outcome` 是 `Success` / `Placeholder` / `SourceChanged`；最后一种不提供位图，需要从当前索引重新取键。正常结果/占位位图均由接收方 Dispose。异步请求被挤掉或取消时无事件，不能把每次 Request 当作保证回调的任务。
- `TryGet` 同步查内存或小 PNG 磁盘缓存，不解码原片。命中返回独立、调用方拥有的位图；在 UI 上优先 `Request`，避免磁盘读阻塞界面。命中也再次核对实际源文件版本。不会把修改后旧缩略图当成当前结果。
- 工作线程每次只处理一个源，原片 stream 立即释放，不锁定原文件。视频只读调用既有 `ShellThumbnails.Get`，由 Windows 缩略图与 codec 支持决定成败，之后缩到指定边长。不启动 FFmpeg/设备进程。部署依赖同程序集中的只读 `ShellThumbnails` 和 `Log`；没有引用 WinForms 业务类型。
- 缓存只存成功结果；坏图/不支持视频返回普通灰色占位，不持久化失败。源在解码中改变时不发布/存储过期结果。命中键未哈希源内容：如果外部程序把内容改变却完整恢复长度和时间戳，需 Track A 显式提高渲染版本/清缓存。
- 默认内存32项 LRU，磁盘独立字节配额。最大1024边长时32张RGBA可能约128MiB；推荐160时约3.1MiB，另加解码/事件/图库接收方位图。解码巨大原片仍有 GDI+ 瞬时内存开销，不宣称对恶意压缩图片提供内存沙箱。
- 磁盘文件 `thumb-<SHA256 key>.png`，先写随机 `.tmp`、验证尺寸、原子替换。命中更新时间，`Trim()` 仅删除本组件 `thumb-*.png` 的最久未用项，保留无关文件。每次成功写入执行限额清理；Track A 可在后台启动时显式 `Trim()` 清理上次已有缓存。
- 坏缓存被删除并按需重建；写失败/目录不可用/只读目标降级内存，不影响原片。`DiskAvailable` 提示当前会话降级，可在设置里显示“磁盘缓存不可写，本次使用内存”。下次启动重新尝试。
- `Cancel(path)` 清掉排队项并标记正在解码项，不中断 GDI+/COM 调用；解码完成后不发取消结果。`Dispose` 清队列/内存并最多等待500ms。Windows原生视频缩略图调用如果卡住，可能在后台稍后结束；不强杀线程，不积累新线程。关闭后不安排新事件，已在执行的订阅者可能稍后返回，因此 Track A 必须做窗口存活/generation检查。

## Claude 需要接的界面与设置

缓存目录由 Track A 的应用数据目录决定，建议 `<dataDir>/cache/thumbnails`；不要放素材目录。建议设置 `thumbs.maxMiB=256`、`thumbs.size=160`、`thumbs.renderVersion=1`，后两项可内部管理，不需要首发设置窗口展示。

图库索引只提供路径与版本信息，**不可索引后遍历整库 Request**。滚动时请求当前可见行，邻近最多一屏作为 Background；离开预取区的路径 Cancel。Ready 回调按 key 与 gallery generation 重新确认仍是可见/预取素材。不要为了新缩略图把整个图库重建。占位项不占用设备忙碌互斥；没有设备/编码器租约需求。

## 相似候选核心

`ThorCapture.Features.Similarity`：

```csharp
SimilarityOptions options = new SimilarityOptions(); // threshold 4, same dimensions only
options.RoleResolver = ResolveRoleFromMediaMetadata;
SimilarityResult result = await SimilarityFinder.FindAsync(paths, options, progress, token);
```

- 结果是类型化 `Groups`、`FailedPaths`、`Scanned`、`Cancelled`、`LimitReached`。取消清掉候选，不把部分结果当完整结果。进度0–100只代表本次受限扫描；`LimitReached=true` 仍必须显示“已达到分析上限，结果不完整”。
- 使用完整 SHA-256 判断**字节完全一致**；不截断哈希为 ulong。`ImageHash.ExactSha256` 可供其他离线文件工具使用。`ImageHash.DHash` 返回64bit感知哈希，`Distance` 是0–64汉明距离。
- 精确候选单独输出；视觉候选包含同图不同编码/轻微变化，完整连接聚类使组内每对距离均 ≤ 阈值，避免链式传递扩大分组。SHA候选与视觉候选可以重叠，UI必须标清两类，不要重复默认勾选。
- 默认阈值4，支持0–16；最大10000输入、200万比较，单后台 Task，单图依次释放。比较达到上限时返回已发现的候选且明确不完整；不是全库保证发现所有配对的算法。
- SHA与图像读取都允许外部写入/删除（ReadWrite|Delete），避免扫描期间锁住原片。每张扫描前后核对实际长度/UTC修改时间；并发修改的项进入失败列表，不发布候选。完整组内最大距离在加入组时就累计，不重复执行额外组内比较；总dHash比较不超过一次200万预算。
- 默认同尺寸，只有 `AllowDifferentSizes=true` 才跨尺寸。角色来自 Track A 的元数据 resolver；未提供时仅从 `-upper-` / `-lower-` 文件名推断，未知与已知角色隔离。未知角色之间比较不代表已确认屏幕来源。
- 16×16采样RGB差≤3判近纯色，纯色只与同采样颜色纯色比较，与正常画面隔离；黑屏不会与其他颜色或普通画面混合。采样是启发式，会漏掉极细小UI，不能称为语义或重要画面判断。
- 仅 PNG/JPEG/BMP/GIF 图像；视频明确进 `FailedPaths`，不把首帧相同当重复视频。动画GIF只比较GDI默认图像帧，不判完整动画相同。文件错误单项继续；输入扫描前后核对长度与时间戳。
- 原片从不修改。结果不推荐“删哪一张”，不自动删除/保留，不接入永久删除。界面需要：分析入口、取消、角色/尺寸/来源时间、SHA或dHash原因、距离、预览对照、失败与不完整提示。允许“不同尺寸”需显式勾选，默认关闭。用户必须逐组审阅；相似不等于冗余，微小UI可能很重要。

## 离线证据与性能

证据目录 `evidence/20260927-pkg4`。测试通过的新行为覆盖：实际版本失效、解码中变化、尺寸/版本键、源无锁、损坏重建、不可写目录/只读目标内存降级、有限队列、可见优先、取消、内存LRU、磁盘LRU/配额/不删无关文件、阻塞解码Dispose上界、每订阅者独立位图；相似精确/跨格式/小UI、纯色/黑屏、角色/尺寸隔离、阈值边界、取消、限项/限比较、坏文件和视频排除。

完整离线 `tools/test-capture-tool.ps1 -Stream`：260项通过（既有221 + 缓存21 + 相似18），C#5编译无警告。包含持有外部写流继续扫描、解码期间写入成功但源变化不发布候选的复核检查。机器：Intel Core i7-13700KF，24逻辑处理器，64bit .NET Framework，Windows NT 10.0.26200.0，G盘普通文件目录。本包未记录用户名路径。以下来自本包完整回归的组件测量，原始记录 `thumbnail-performance-final.csv`：

| 合成库项数 | 冷缓存首48张 | 热磁盘缓存首48张 | 冷/热CPU总时 | 私有内存前/冷后/热后 |
|---|---:|---:|---:|---:|
|100|193ms|80ms|109.375 / 78.125ms|43.03 / 44.11 / 44.07MiB|
|1000|183ms|59ms|140.625 / 31.25ms|43.92 / 43.95 / 44.02MiB|
|10000|221ms|49ms|171.875 / 46.875ms|45.53 / 46.91 / 46.98MiB|

方法：真实生成100/1000/10000个独立160×90 PNG文件（同色合成素材），在测量前建立路径/版本数组，只向组件请求48张80边长可见缩略图。空磁盘缓存为冷；第二次创建全新组件，复用PNG磁盘缓存为热。队列最多48、工作线程1、内存上限32；冷热值包含创建/释放组件。CPU/private bytes来自当前测试进程，包括测试框架，不是独立图库占用；素材创建、数组索引、目录扫描不在时段内。没有清空OS文件缓存，不能称存储冷启动。暖缓存首屏≤1秒参考目标在本机组件通过。

**完整 GalleryView 尚未集成**；不能把上述测量说成软件万项图库达标。混合大图/视频/大量旧磁盘缓存、实际滚动、DPI、长期资源与Windows不同codec仍需 Track A 合并后验收。不可写测试包含路径被普通文件占用以及真实ReadOnly缓存目标；没有改系统目录ACL或模拟已撤销用户权限。没有本轮实机操作。

建议 ROADMAP：P5-2“缓存核心完成，待图库索引/可见队列/UI接线与整库验收”；P5-6“候选算法核心完成，待审阅界面与组事务集成，首发可关闭”。只由 Claude 回填主 ROADMAP；本分支不合并main，也未构建/替换用户在用exe。
