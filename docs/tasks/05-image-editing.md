# 5 号包：非破坏性图片编辑引擎

- **ROADMAP 任务**：P5-7，只做引擎部分。编辑器窗口、工具栏、鼠标交互由 Track A 完成。
- **批次**：第一批，不需要设备。派生关系的记录方式要等 Track A 的 P1-10 合并后接入；在那之前先把派生信息写进一个独立的结构，集成时再对接。
- **短名**：`Editing`；代码 `src/Features/Editing/`，测试 `tests/EditingTests.cs`，说明 `docs/features/editing.md`。

## 目标

对截图做裁剪、旋转、遮盖、标注，结果**另存为新文件**，原图永远不变；支持撤销和重做；预览与导出像素一致。

## 建议接口

```csharp
internal abstract class EditOp { public abstract Rectangle Affects(Size canvas); }   // 所有坐标都用源图像素坐标
internal sealed class CropOp : EditOp { public Rectangle Area; }
internal sealed class RotateOp : EditOp { public int QuarterTurns; }
internal sealed class CoverOp : EditOp { public Rectangle Area; public CoverStyle Style; public Color Color; public int BlockSize; }  // Solid / Pixelate
internal sealed class ArrowOp : EditOp { public PointF From, To; public Color Color; public float Width; }
internal sealed class BoxOp : EditOp { public Rectangle Area; public Color Color; public float Width; }
internal sealed class TextOp : EditOp { public PointF At; public string Text; public Color Color; public float SizePx; }

internal sealed class EditDocument : IDisposable
{
    public EditDocument(string sourcePath);                 // 读入时不锁文件
    public Size CanvasSize { get; }                         // 裁剪、旋转后的尺寸
    public void Apply(EditOp op); public bool Undo(); public bool Redo(); public bool CanUndo { get; } public bool CanRedo { get; }
    public Bitmap Render(float scale);                      // 预览；scale = 1 时与导出一致
    public EditResult SaveAs(string path, ImageFormat format);   // 临时文件 → 校验 → 替换；绝不写回源路径
    public DerivedInfo Derivation { get; }                  // 源路径、源哈希、操作列表摘要，用于建立派生关系
}
```

要点：
- **隐私遮盖默认用不透明色块**。像素化需要足够大的块，并在说明里提示「模糊不等于可靠脱敏」；导出的像素必须是真的被覆盖，不能只是一个可移除的图层。
- 所有操作只记录参数，按顺序重放生成结果，这样撤销、重做和导出结果一致。
- 坐标在裁剪、旋转之后要正确换算（例如先裁剪再标注），边缘坐标要限制在画布内。
- 透明 PNG 的 alpha 要保留。
- 先只支持图片，视频剪辑不在本包范围。

## 测试
- 每种操作的像素结果：遮盖区域内不再残留原始像素；像素化块大小正确。
- 撤销、重做的序列与直接重放结果一致；`Render(1)` 与 `SaveAs` 像素一致。
- 裁剪加旋转后的坐标换算、越界坐标、透明图、中文文字。
- `SaveAs` 目标等于源路径时拒绝；写入失败时源图和目标都不受影响；源文件哈希始终不变。

## 集成说明需要写
- 每个工具需要的交互（拖出区域、点两点、输入文字），以及默认颜色和线宽。
- 预览刷新的性能：大图时 `Render` 的耗时，是否需要局部刷新。
- 另存的命名建议，例如 `原名-edit-01.png`。
