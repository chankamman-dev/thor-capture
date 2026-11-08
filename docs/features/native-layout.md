# 素材布局后端接入

2026-10-11。Codex 后端，Claude 接素材工作室界面。新增 NativeArtworkLayout、NativeArtworkTarget 和 LayoutAsync，复用现有预览/暂存/撤销接口。

~~~csharp
var layout = new NativeArtworkLayout {
    Fit = NativeLayoutFit.Cover,
    FocusX = 0.5, FocusY = 0.5,
    PaddingTop = 0, PaddingRight = 0, PaddingBottom = 0, PaddingLeft = 0
};
NativeEditorResult result = await client.LayoutAsync(itemId, candidateId,
    NativeArtworkTarget.UpperHero, layout, cancellation);
// Preview result.Data["blobSha256"] with PreviewAsync.
// On explicit save, pass result.Data["variantToken"] to StageAsync.
~~~

NativeArtworkTarget.LowerHero 为 1240×1080；UpperHero 为 1920×1080。自定义目标通过构造函数指定 id/slot/width/height。这些是画布尺寸预设，不代表实际 Cocoon 遮罩、Logo 叠加位置或设备兼容性验证。

NativeArtworkLayout 默认完整源图、Contain、中心、透明背景。CropWidth/CropHeight 均为 0 时不裁剪；指定裁剪时必须同时提供正数，坐标以源像素计。FocusX/Y 在 Cover 中决定裁切位置，在 Contain 中对齐空余空间。Padding 为输出像素，Background 为 System.Drawing.Color。客户端在异步请求前序列化快照，后续 UI 修改不改变正在提交的请求。

返回 Data 形状与 CropAsync 一致；布局不会改变草稿。结果可经 PreviewAsync 显示，明确保存时 StageAsync 会验证并保存同一份预览字节。token 最长五分钟；页面需要合并拖动请求、取消旧请求，检查 generation 后展示。

新配方 asset-layout-render-v1 记录目标尺寸与全部布局参数，可独立重现。旧裁剪版本保持原行为；新版本使用预乘 Alpha 缩放消除透明 Logo 的隐藏颜色污染。

测试与构建证据记录在 evidence/cocoon-completion-20261011/layout-delivery.json。此批未接界面、未替换用户 EXE、未进行实机操作。Logo 叠加/九宫格、真实遮罩与安全区、视频素材仍未完成。
