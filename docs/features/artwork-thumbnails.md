# 原生游戏库缩略图：后端接入

2026-10-11 补充：[会话隔离与原生预览](thumbnail-session-safety.md)。当前/暂存缩略图调用时捕获拥有会话，迟到 200/304/错误响应拒绝；15 秒期限覆盖完整响应体。原生编辑像素使用绑定 editor/library 的 NativeEditorClient.PreviewAsync。

更新：2026-09-27。用户清单第4项的后端交付；Claude负责页面接入。Node合同 `cocoon.thumbnail/1`；详尽HTTP、格式、限制与错误见 `..\cocoon-art-backend\docs\ARTWORK-THUMBNAILS.md`。没有修改游戏库页面或外壳渲染。

## 已交付接口

`src/Library/ArtworkThumbnailClient.cs` 提供 `ArtworkThumbnailClient(CocoonBackend)`。使用本窗口拥有且Ready的backend，不另建Node服务、固定端口或认证实现。

```csharp
ArtworkThumbnailClient thumbnails = new ArtworkThumbnailClient(backend);
ArtworkThumbnail result = await thumbnails.GetAsync(
    gameId, ArtworkSlot.Icon, 160, 160, ArtworkFit.Contain,
    previousEtag, cancellationToken);
```

- `ArtworkSlot`：Icon、Logo、Hero；`ArtworkFit`：Contain、Cover。尺寸边界16–1024，不放大小图；result.Width/Height是实际输出尺寸。
- `GetAsync`读取当前本地缓存；`GetStagedAsync(gameId, slot, extension, width, height, fit, previousEtag, cancellation)`读取暂存。extension严格为jpg/png/webp/gif，不能传任意路径。
- 返回 `ArtworkThumbnail`：Bytes、ETag、SourceSha256、Width、Height、Cache、NotModified。正常为PNG字节；304时NotModified=true且Bytes=null，保留已有位图。不能把它解释为删除图片。
- 客户端校验合同头、PNG类型/尺寸、内容版本及8MiB响应上限；后台执行HTTP，有15秒请求期限，取消调用Abort。参数错误在发HTTP前拒绝。
- 同源 `/api/` 入口、内存token、不跟随重定向；类型化失败为 `BackendRequestException.Code/Status`。GetAsync取消不保证服务器上的共享工作已取消；Node关闭会清理自己拥有的worker/解码进程。

## 页面接入要求

1. 列表只请求可见项和少量预读；最多4个客户端请求并发，服务默认两项解码并发。用分页/可见项队列，不能为整个库同时创建Task。
2. 位图缓存键包含backend实例/页面generation、gameId、槽位、current/staged、尺寸、fit。DPI或控件尺寸变化要换键；ETag只用于同一键的条件请求。304使用该键已有位图，200再替换。
3. await后复核当前页面generation、选择和控件尺寸。切页、筛选或关闭取消该页面请求；旧响应不能更新新页面。导入/撤销完成后刷新，不能因为同gameId便保留旧绑定。
4. PNG解码也在后台完成；从流解码后复制成独立Bitmap，再关闭流。页面取得Bitmap所有权；替换、缓存淘汰、切目录/关闭时Dispose。不在OnPaint中请求网络、解码或扫描磁盘。
5. 请求与解码失败显示占位和准确原因，不把503当空库，也不无限立即重试。404表示本地素材未缓存；415表示格式不支持；503 CODEC_UNAVAILABLE提示配置已有FFmpeg。缺失图不能自动触发设备拉取或下载原片。

后台解码示例（调用方仍须处理取消/过期时的Dispose）：

```csharp
Bitmap decoded = await Task.Run(delegate {
    using (MemoryStream input = new MemoryStream(result.Bytes))
    using (Image image = Image.FromStream(input))
        return new Bitmap(image);
}, cancellationToken);
```

`result.NotModified`须在运行以上代码前处理。前端已有位图缓存、布局/selection逻辑由Claude维护，本批未增加另一套界面缓存。

## 格式、缓存和配置

常规8-bit PNG用内置worker；JPEG、WebP、GIF首帧、交错/16-bit PNG等复用现有FFmpeg。`CocoonBackend.Start()`发现Capture的解码器并传绝对路径 `COCOON_THUMBNAIL_FFMPEG`；发现失败不阻止库/普通PNG启动。`GET /api/backend/thumbnails`可查询合同、计数、配置状态与上限；configured只表示路径已配置，不是兼容性证明。

内存16MiB、磁盘256MiB/2000条的有界LRU；每次重新读取/哈希本地源，未变不重复解码，源变化或参数变化返回新版本。原图最多32MiB/16MP，输出最多1024×1024，单项解码5秒。旧不带尺寸的 `/api/art` 仍返回原图。所有派生图保存到独立dataRoot的 `_cache/thumbnails-v1`，不覆写原图、pending或数据库；导入清单排除这些可再生文件。

GIF不播放动画；ICO/SVG/视频不支持此入口，JPEG暂不应用EXIF方向，不承诺ICC/HDR一致。此接口缩小图片，不是原生分辨率无损，也不改善实时视频延迟。

## 本批验证

- 新增原生13项检查：真实拥有后端HTTP、可解码PNG、304无body、比例/裁剪、透明暂存Logo、变更、参数、取消和关闭。库相关101/101；Thor含合成流/声音/回放的全量559/559，独立EXE构建成功。
- Node新增11项综合检查，全量305/305，无跳过。覆盖认证/Origin、并发/队列/关闭、坏缓存与错误ETag、源变化/mtime恢复、链接/大小限制、真实FFmpeg JPEG/WebP/GIF、worker/拥有子进程超时、导入清单与派生缓存并存。
- 隔离真实库48封面样本（36PNG/12JPEG），四个HTTP请求并发：首次约1385ms，再次132ms；图片body由23,433,534减至2,251,135字节，减少90.39%；48/48再次返回304、新增解码0，相同内容复用后生成45条。源图片和库SHA/mtime不变。样本不代表整个界面或10,000游戏的耗时。
- 证据 `evidence/thumbnail-native-tests.txt`、`thumbnail-stream-tests.txt`、`thumbnail-build.txt`；Node `evidence/thumbnail-all-tests.txt`、`thumbnail-real-library.json`；最终提交/构建边界见 `thumbnail-delivery.json`。
- 原Cocoon、生产数据根和用户运行的EXE/Node未替换；未进行设备、前端页面或公开测试版验收。

后续第5项跨进程设备协调是第2项只读拉取的前提；不能通过解除受管理模式的设备访问冻结接入拉取。
