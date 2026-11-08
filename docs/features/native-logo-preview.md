# 原生 Logo 叠加预览

2026-10-11。新增 NativeLogoPlacement、NativeArtworkScreen 和 NativeEditorClient.LogoPreviewAsync。界面由 Claude 接入。

~~~csharp
int anchor = NativeLogoPlacement.SnapAnchor(normalizedX, normalizedY);
var placement = new NativeLogoPlacement(anchor, 0.5, 0.5);
NativeEditorResult scene = await client.LogoPreviewAsync(itemId, heroCandidateId,
    logoCandidateId, NativeArtworkScreen.Upper, placement, null, cancellation);
// PreviewAsync(itemId, ArtworkSlot.Hero, scene.Data["blobSha256"], ...).
// Read scene.Data["metadata"] for previewToken, revision and per-field changes.
// Confirm selected placement fields with MetadataConfirmAsync.
~~~

heroLayout 可选，省略时 Hero 铺满；提供时使用 NativeArtworkLayout 的裁剪/适应/焦点/留边。旧重载将完整 Logo 候选等比放入位置框；裁剪/暂存重载见 [素材联动](native-studio-links.md)。编辑过程中合并拖动请求并取消旧请求；检查页面 generation 后展示。

Data 返回 itemId、heroCandidateId、logoCandidateId、blobSha256、width/height、compatibility、placement、metadata、pendingChanged:false。客户端严格验证候选/游戏、屏幕尺寸、位置参数、嵌套元数据确认内容。metadata.changes 只包含实际改变的 Logo 字段；空集合表示无需确认，不调用空字段确认。

必须保留 `compatibility=EDITOR_APPROXIMATION` 的含义：这套位置来自旧编辑器，未通过实际 Cocoon 校准。不要把画布预览标成实机截图、遮罩准确或已应用到设备。九宫格/拖动吸附和缩放可供前端调用，但本批没有新增界面。

组合 PNG 是临时预览，不能拿去 StageAsync 当 Hero。MetadataConfirmAsync 只保存位置字段，选中的新 Hero/Logo 候选需要分别明确暂存。Logo 裁剪/暂存变体联动、位置重置与候选/暂存对比接口已补齐，见 [素材联动](native-studio-links.md)。设备写入保持关闭。
