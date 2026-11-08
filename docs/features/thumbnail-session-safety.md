# 缩略图会话隔离与原生编辑预览

更新：2026-10-11。沿用 Codex 后端、Claude 前端分工；本批只改客户端后端、测试与交接文档。

## 本批行为

- ArtworkThumbnailClient.GetAsync/GetStagedAsync 在调用时捕获拥有后端的地址、私有 token 和实例核验函数，然后排队。排队中的旧请求不能改投重启后的后端。
- 图片 200、条件请求 304 和错误响应返回时都复核拥有会话。失效返回 BackendRequestException("BACKEND_SESSION_STALE", 409)，不把旧图片交给界面缓存。
- 15 秒总 HTTP 期限覆盖响应体读取；读取半张 PNG 时取消返回 OperationCanceledException，超时返回 BACKEND_TIMEOUT。保留 PNG 合同、尺寸、ETag 和 8 MiB 校验，不跟随重定向。
- 原生编辑增加绑定会话的 NativeEditorClient.PreviewAsync。请求前后校验 editor instance/library UUID，响应来源 SHA256 必须等于所请求候选或裁剪产物；实际尺寸不能超过请求尺寸。这些检查也适用于 304。

~~~csharp
NativeEditorClient editor = new NativeEditorClient(backend, backend.CaptureNativeEditorTransport);
await editor.ConnectAsync(cancellation);
ArtworkThumbnail image = await editor.PreviewAsync(
    itemId, ArtworkSlot.Icon, sourceOrVariantSha256,
    160, 160, ArtworkFit.Contain, previousEtag, cancellation);
~~~

读取候选时使用 candidate 的 sourceBlobSha256，读取裁剪结果时使用 crop 的 blobSha256。预览只读取像素，不暂存、不应用草稿；调用方仍以页面 generation、item、slot、blob、尺寸及 fit 为键，取消过期意图并丢弃不再选中的结果。304 只复用同一个键的已有位图。

原生预览失败为 NativeEditorException：拥有会话变化映射为 EDITOR_SESSION_STALE，总 HTTP 超时映射为 EDITOR_TIMEOUT，HTTP 404 保留 THUMBNAIL_NOT_FOUND。换库或重新 Connect 后重新读取候选，不自动重放任何草稿写入。

## 与现有在途代码的关系

主目录已有未提交的 ArtworkThumbnailClient.GetNativeAsync，本批保留它。前端原生编辑接入推荐上述 NativeEditorClient.PreviewAsync，由 editor 自动绑定和复核库身份，不需要自行传 instanceId。同步内部 CocoonBackend.GetThumbnail 为兼容旧调用保留，使用相同传输校验；旧外层 Task 排队仍不能替代新版调用时捕获。

实际 Node 端 /api/native-editor/preview 来自 ..\cocoon-art-backend 已存在的在途修改（server.js、lib/application/nativeAssetService.js、test/deviceReadBroker.test.js）。本批未修改或提交这些文件；离线证据记录实际测试文件的 SHA256。打包时需包含这条 Node 路由，只有历史固定提交的旧组件不能宣称支持原生像素预览；客户端不会绕回任意文件/素材接口。

主目录另有未提交 src/UI/GameEditing.cs 和 src/UI/MetadataDialog.cs。本轮初始构建确认后者第 212 行 catch 内的 await 不符合 C# 5（CS1985），整棵主目录目前不能编译。保持文件原样交给前端处理；可把需要 await 的恢复步骤移到 catch 外，并处理恢复失败。独立候选基于已提交前端，不包含这两份在途界面文件。

## 验证入口与边界

- tools/test-capture-tool.ps1 -Filter ThumbnailSession：拥有的 loopback HTTP 服务、明确屏障、真正 PNG 响应体；覆盖会话失效、200/304/错误迟到、取消、总期限、库切换、重新连接、哈希和尺寸不匹配以及重定向。
- NativeEditorClientTests：实际独立 Cocoon 服务及生成的 demo 数据，候选像素、裁剪像素、304、跨槽位拒绝；预览前后草稿 revision 不变，原 demo 数据库字节不变。
- 完整测试与构建结果见 evidence/thumbnail-session-delivery.json，失败或旧轮次日志保留，不以历史数量替代本轮结果。

本轮结果：含合成流/声音/回放的全量回归 1178/1178 通过，无 SKIP；之后仅补充两项测试（304 来源哈希和超尺寸响应），最终专项 20/20、连同固定基础检查 84/84 通过，生产代码未再改变。独立 C# 5 候选构建通过，仅有既存 ImportDialog.cs 未等待调用警告。不要把两个轮次相加为单次全量检查数。

未运行真实 ADB、未改生产库、未替换用户 EXE；没有界面接入或实机验收声明。离线候选用于本批后端交付，不是带齐 Node/ADB/FFmpeg 的发布安装包。
