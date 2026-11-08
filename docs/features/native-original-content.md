# 原件导出与视频播放会话

2026-10-11。Codex 后端接口，Claude 接界面。

~~~csharp
var original = await client.DownloadOriginalAsync(itemId, ArtworkSlot.Hero, videoId, cancellation);
await client.ExportOriginalAsync(itemId, ArtworkSlot.Hero, videoId, absoluteDestination, cancellation);
var session = await NativeVideoPlaybackSession.StartAsync(client, itemId, videoId,
    existingCacheDirectory, VideoExport.Locate(), cancellation);
// Wait until session.Playback.Ready, or display its Error.
session.Playback.Play();
// Use existing Pause / Seek / TakeLatest; dispose or transfer every returned frame.
await session.StopAsync();
~~~

原始图片 SHA256 来自 OriginalsAsync；原视频 ID 来自 VideosAsync。生成 PNG 海报、裁剪图和组合图不能冒充原件。下载有 30 秒期限与 32 MiB 上限，校验合同、当前库/实例、MIME、精确长度与完整 SHA256；Bytes 返回独立副本。换库/关闭取消调用，迟到响应不应显示到新页面。

导出目录须已存在，目标路径为绝对路径；拒绝现存目标、路径中的 reparse point。同目录 CreateNew 临时写入、落盘、哈希校验后 File.Move 无覆盖发布。不悄悄覆盖旧文件，也不自动创建未知目录。取消或错误时只清理仍匹配完整原件的拥有临时文件；部分写入或外部改变的临时文件可能保留，不递归清理目录。导出是已经下载并验证的不可变快照。

播放复用 VideoPlayback：默认加载后暂停，显示 Ready/Error 后控制播放、暂停、跳转和最新帧。缓存目录须已存在；每个会话独立 UUID 文件。切视频/关闭先 await StopAsync，再释放界面资源；重复 StopAsync 共享同一停止任务。拥有解码器停止后，仅删除哈希仍匹配的源文件，被外部修改或忙碌则保留。进程崩溃后的遗留文件没有自动清理。

导入视频的探测、音频检测和音视频解码固定 MP4 mov/WebM matroska demuxer，协议限 file/pipe，MP4 禁止外部 data reference。既有录屏播放入口保留原默认行为。使用现有 FFmpeg 定位配置和旁置 ffprobe.exe，不把未经识别文件交给自动格式探测。原始视频音轨保持不变，界面应复用现有声音输出设置。

真实后端集成覆盖原始图片下载、视频导出 SHA256、播放器 Ready、实际帧解码、停止后拥有文件释放；尚未完成新功能窗口接入、真实音频听感或 Cocoon 设备解码矩阵。VIDEO_PROFILE_UNVERIFIED 阻塞继续保留。
