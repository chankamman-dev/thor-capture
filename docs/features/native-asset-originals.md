# 原始素材查询与临时裁剪预览

2026-10-11。Node 实现 5fc0de5；仍由 Claude 接素材工作室界面。

## 查询原件

~~~csharp
NativeEditorResult originals = await client.OriginalsAsync(itemId, slot, candidateId, cancellation);
// Each item: sha256, mime, byteSize, width, height.
ArtworkThumbnail preview = await client.PreviewAsync(itemId, slot, originalSha256,
    640, 640, ArtworkFit.Contain, null, cancellation);
~~~

originals.Data 含 itemId、slot、candidateId、items、pendingChanged:false。客户端校验来源 SHA256、四种图像 MIME、字节/像素上限、重复条目、库和编辑实例。返回 Data 是深拷贝，前端照常检查页面 generation 并在 UI 线程更新。

新导入会保留完整原始 PNG/JPEG/WebP/GIF 字节，并另存完整分辨率解码 PNG。旧候选缺少原件证据时 items 为空，显示“历史原始编码未保留”，不能猜测原始格式。原件像素预览仍是 PNG 缩略图；原始字节导出接口尚未提供，不把缩略图另存称为原件导出。

## 裁剪兼容变化

CropAsync 的返回形状不变，但 crop 现在只创建当前编辑实例的临时预览，不写永久 Recipe/Variant。现有 PreviewAsync 可预览返回的 blobSha256。显式 StageAsync 附 variantToken 后，才重新校验源图和预览并保存永久配方/变体，再执行草稿 revision CAS。

- 不附 variantToken 的 StageAsync 绑定完整候选，可恢复未裁剪像素。
- 预览 token 最长 5 分钟、最多 128 个；页面应合并拖动中的重复请求。过期、被改动或重启后的 token 要重新预览再审阅。
- 取消/冲突不应声称已经删除保存过的素材；草稿不覆盖更新 revision，原件和已明确保存的变体保留。
- 缓存清理仅限本实例验证过的临时文件，旧/异常/其他实例缓存保留。关闭顺序已调整为先停止缩略图读者，再清理素材预览。
- JPEG 预览目前不自动应用 EXIF 旋转，GIF 为第一帧；保存的原件仍保留原始信息/动画。

Node 全量 416/416，无跳过；C# 原生专项 224/224，含实际 HTTP 原件查询/像素预览及原有 crop/stage/草稿/变更中心闭环。没有修改前端、替换用户 EXE 或操作掌机。交付记录位于 evidence/cocoon-completion-20261011/studio-originals-delivery.json。
