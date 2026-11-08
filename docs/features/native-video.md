# Hero 视频和海报客户端

2026-10-11。新增 UploadVideoAsync、VideosAsync、VideoPosterAsync，沿用 StageAsync/PreviewAsync/变更审阅。Codex 后端，Claude 接界面。

~~~csharp
NativeVideoUploadResult upload = await client.UploadVideoAsync(itemId, bytes, cancellation);
string videoId = (string)upload.Video["id"];
NativeEditorResult poster = await client.VideoPosterAsync(itemId, videoId, 500, cancellation);
// poster.Data["video"] includes posterBlobSha256; preview with ArtworkSlot.Hero.
// On explicit save: StageAsync(itemId, ArtworkSlot.Hero, videoId, revision, null, cancellation).
~~~

上传支持 byte[] 快照和借用 Stream，32 MiB 上限；视频上传/海报提取 HTTP 最长 45 秒，取消中止拥有请求。宿主按现有 THOR_FFMPEG、旁置 ffmpeg.exe 或 ffmpeg-path.txt 定位解码器，同目录需有 ffprobe.exe。实际 C# 测试按同一配置运行。

NativeVideoUploadResult.Video、VideosAsync 的 Data.items、VideoPosterAsync 的 Data.video 都含 id/sourceBlobSha256、mime、byteSize、width/height、durationMs、codec、posterBlobSha256、posterAtMs、deviceCompatibility:UNVERIFIED。客户端限制身份、格式、字节/像素/时长和时间范围，防止错误响应误标设备兼容。

视频列表与图片候选分开。视频 id 表示原视频；posterBlobSha256 表示生成 PNG。分别暂存会得到不同 Hero，需用清楚文案让用户选择；选择海报时间不会自动改变草稿。重复导入保留已选海报。Logo 场景可使用视频 id，实际显示海报静帧，不是视频播放。

变更中心已接受 Hero MP4/WebM，并要求每条视频存在 VIDEO_PROFILE_UNVERIFIED 阻塞。原视频播放/字节导出接口见 [原件导出与播放会话](native-original-content.md)；前端流程与实机编码/硬件解码矩阵验收仍待完成。本机 FFmpeg 解码不能证明 Cocoon 能播放。

视频上限为 32 MiB、1 小时、8192/边且不超过 16 Mi 像素；海报不放大，最大 1920×1080，暂不自动应用旋转元数据。保留原视频全部字节。测试记录位于 evidence/cocoon-completion-20261011/video-delivery.json。
