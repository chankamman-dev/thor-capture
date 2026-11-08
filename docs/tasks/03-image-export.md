# 3 号包：图片导出引擎

- **ROADMAP 任务**：P4-1 图片导出计划与预设、P4-3 双屏拼图、P4-4 像素整数倍率，只做引擎部分。导出对话框和预览界面由 Track A 完成。
- **批次**：第一批，不需要设备。
- **短名**：`ImageExport`；代码 `src/Features/ImageExport/`，测试 `tests/ImageExportTests.cs`，说明 `docs/features/image-export.md`。

## 目标

给定截图（上屏、下屏，可缺一块）和一份不可变的导出计划，输出与界面预览**完全一致**的位图，并写成文件。

## 现有代码（只读）
- 带壳绘制：`DeviceArt.Draw(g, bounds, skin, top, bottom, topLabel, bottomLabel, input, controls)`。只画屏幕时用 `DeviceArt.DrawScreens`。
- 现有的透明带壳导出：`PreviewBox.ExportDevice(bool transparent)` 和 `PreviewBox.ExportSize`（上屏按原生 1920 宽）。**新引擎的默认预设要和它逐像素一致**，这样界面切换过来时用户看不到差别。
- 配色和灯光：`DeviceSkin`、`DeviceSkin.WithLamp(...)`。计划里保存的是一份快照，不读界面上可变的当前设置。

## 建议接口

```csharp
internal enum ExportBackground { Transparent, Solid, Gradient }        // 截图模糊背景可作为后续选项，先不做
internal enum ExportLayout { Framed, StackVertical, SideBySide, Single }
internal sealed class ImageExportPlan                                    // 不可变：构造后字段只读
{
    public ExportLayout Layout; public DeviceSkin Skin;                  // Skin 已包含灯光设置
    public ExportBackground Background; public Color Color1, Color2;
    public Size? Canvas;                                                 // null = 原生尺寸；否则按比例放入画布，不拉伸
    public int PixelScale;                                               // 1/2/3/4，最近邻；只作用于屏幕内容
    public int Gap; public Color GapColor; public float CornerRadius;    // 拼图用
    public ImageFormat Format; public int JpegQuality;                    // PNG 默认；JPEG 需要提前确定背景色
    public string Describe();                                            // 给界面显示输出尺寸、格式
}
internal static class ImageExporter
{
    public static Size MeasureOutput(ImageExportPlan plan, Size? top, Size? bottom);     // 导出前就能告诉用户最终尺寸
    public static Bitmap Render(ImageExportPlan plan, Image top, Image bottom);
    public static ExportResult Save(ImageExportPlan plan, Image top, Image bottom, string path);   // 临时文件 → 校验 → 替换
    public static IList<ExportPreset> LoadPresets(); public static bool SavePresets(IList<ExportPreset> presets);  // 设置键 export.presets
}
```

要点：
- **原片永远不改**；目标文件已存在时由调用方决定冲突策略，引擎本身不覆盖。
- 缺一块屏时标注缺屏或只导出单屏，**不要把单屏冒充成双屏**。
- 整数倍率只用于屏幕内容，外壳按正常质量绘制；超过内存或尺寸上限时，在渲染前就拒绝并说明原因。
- 像素放大时，每个源像素要变成一个 N×N 的色块，不能出现插值产生的新颜色。
- 拼图不裁剪、不拉伸，上屏保持 1:1。

## 测试
- 默认预设与 `PreviewBox.ExportDevice(true)` 输出一致（尺寸相同，像素差异为 0 或在极小容差内）。
- 四种配色、灯光开 / 关、透明边缘的 alpha 值。
- 合成棋盘图做整数放大，逐块检查没有新颜色；超大尺寸被拒绝。
- 拼图：原生比例、缺屏、间隔和圆角。
- 保存：写入失败时不留临时文件，目标文件不被破坏。

## 集成说明需要写
- 对话框字段和默认值、每个预设的用途。
- 预览时如何调用 `Render` 得到缩略图（大小和性能）。
- 批量导出（10 号包的 P4-2）会怎样复用本包的 `Save`。
