# 5号包：非破坏性图片编辑引擎

2026-09-27，Track B，P5-7 引擎核心。分支 `feature/pkg5-editing`，只新增 `src/Features/Editing/`、独立测试和本说明；未改变主窗口、框架、既有测试/工具，没有合并main、替换用户exe或连接设备。编辑器窗口/指针交互/界面接线由 Claude / Track A 完成。

## 公开接口

命名空间 `ThorCapture.Features.Editing`。全体操作为 sealed、参数 readonly，不持有 Graphics/Font/源位图；历史只保存不可变参数。

```csharp
EditOpenResult opened = await EditDocument.OpenAsync(sourcePath, token);
EditDocument doc = opened.Document; // only when Outcome == Success; caller must Dispose
doc.Apply(new CropOp(new Rectangle(20, 10, 300, 200)));
doc.Apply(new RotateOp(1)); // clockwise 90 degrees
doc.Apply(new CoverOp(new Rectangle(10, 10, 80, 30))); // opaque black, default
bool undone = doc.Undo();
bool redone = doc.Redo();
EditRenderResult preview = await doc.RenderAsync(1, token);
// Success Bitmap belongs to caller; dispose stale generations, transfer current to editor.
EditResult saved = await doc.SaveAsAsync(newPath, ImageFormat.Png, token);
DerivedInfo provenance = saved.Derivation;
doc.Dispose();
```

- 同步 `new EditDocument(sourcePath)`、`Render(float scale)`、`SaveAs(path, format)` 适用于测试/后台；前者和Render会抛参数/文件/Dispose异常。界面使用 `OpenAsync` / `RenderAsync` / `SaveAsAsync` 的类型化结果。
- `EditOutcome`：Success / Cancelled / InvalidTarget / UnsupportedFormat / Failed。原因是中文，无完整用户路径/注解正文日志。成功Render拥有Bitmap，失败/取消无Bitmap；成功Open拥有Document，调用方Dispose。
- `CanvasSize`、`CanUndo/CanRedo`、Undo/Redo；Apply后当前redo分支被丢弃。参数检查失败不更改历史。`RotateOp.QuarterTurns` 归一化0–3，负数支持逆时针。
- 支持真实 PNG/JPEG/BMP 源（扩展名与实际格式均限制到这三种），不支持视频/GIF/TIFF，JPEG来源按GDI像素加载，不自动解读EXIF旋转。导出初版只支持**无损PNG**，传JPEG等格式返回UnsupportedFormat，避免把有损结果说成预览像素一致。
- PNG透明Alpha保留；遮盖/标注直接重放到最终像素，再导出一张扁平PNG，不留可移除图层。编辑工具不能追溯恢复已覆盖的像素；原片仍另存保留。

## 坐标与工具

所有坐标使用**当前操作之前的画布像素单位**，不使用控件/DPI尺寸。裁剪、旋转按顺序重放，每步之后画布原点/宽高改变；例如先crop再rotate再cover，cover区域针对旋转后的当前画布。Track A 从缩放预览/平移控件映射 `(pointer - imageOrigin) / scale` 后再创建操作，不把屏幕坐标直接传入。不会修改已经记录的旧操作去追踪后续几何。

- Crop：拖选矩形，保存 `CropOp(Rectangle)`。部分越界与画布求交，完全越界、非正尺寸、整型溢出拒绝。默认没有裁剪。
- Rotate：按钮90°左/右，`RotateOp(-1/1)`，整数像素无插值，宽高交换。
- Cover：拖选区域，`CoverOp(Rectangle)` 默认纯黑不透明；即使传入透明Color，Solid也强制Alpha255。像素化显式选择 `CoverStyle.Pixelate`，默认块24、范围4–256，在区域左上角对齐，逐块平均RGBA，边缘不足一块按实际像素计算。**像素化不等于可靠脱敏**，推荐不透明遮盖敏感信息，块大小由原片像素决定，4像素块并不保证隐私。
- Arrow：拖出起终点，`ArrowOp(PointF from, PointF to, Color, float width)`；推荐红色、3px，宽1–64；有限越界坐标裁到画布，NaN/Infinity拒绝。带箭头端帽，抗锯齿。
- Box：拖选矩形，`BoxOp(Rectangle, Color, float width)`；推荐红色、3px，内侧描边，矩形范围与cover同样检查。
- Text：点位置后输入，`TextOp(PointF at, string text, Color, float sizePx)`；推荐白色/24px，6–256px、非空、最多1024字符，Microsoft YaHei系统字体。中文已实际渲染检查；系统缺字体可能由GDI选择替代字体，不附带字体文件。文本超出边缘会被画布裁切，不自动扩展尺寸。
- `EditOp.Affects` 给出保守影响区域，箭头/文字/旋转可返回全画布。本版采用全图重放，不能把它误当成已实现局部刷新。

## 线程、取消与资源

Document内部锁保护原片与历史。Apply/Undo/Redo只改参数；Async调用**在调用时**复制源与历史，之后的编辑/Dispose不改变在途输出。复制源Bitmap可能花数毫秒，UI应合并频繁请求，不连续拖动每个事件都重新渲染。多余/迟到结果按generation Dispose。

耗时操作使用Task后台执行，不引用WinForms、不管理设备或busy状态。Track A 用OperationGate/编辑器自身调度保证同一文档最多一个渲染在途，取消旧请求并等待后续派发，避免多个全图快照同时占内存。无设备/编码器独占要求。

CancellationToken在文件哈希块、操作之间、像素化行/块行、写入提交前检查；GDI解码/Clone/文字/PNG编码单次调用不可中途打断，取消为合作式，不承诺立即终止。成功文件的最终 `File.Move` 是提交点；取消若晚于该点返回成功。Dispose只释放文档拥有的源和历史，不中断已捕获的快照任务，界面关闭时应取消并等待任务，Dispose收到的结果。

限制：源≤256MiB、单边≤8192、总≤1600万像素、历史≤256步；Render scale0.01–4，缩放输出也满足边长/像素上限。检查发生在GDI格式加载之后、创建本组件分离位图之前，不宣称对恶意编码器文件提供沙箱。约3–4份32bpp位图加像素化/PNG编码开销；1600万像素可能约256MiB或更多瞬时内存，多任务并发会增加，因此UI必须限流。参数超限显示原因而不静默降画质。

加载使用ReadWrite|Delete共享流，读取字节SHA与图像后检查源长度/UTC修改时间，变化则拒绝；加载完立即释放所有源文件句柄。在途渲染使用加载时分离的位图；若外部程序之后修改源文件，当前文档仍代表已加载快照，派生SHA明确指向该快照。长度/时间被外部程序完整伪造恢复时，无法靠元信息保证载入期间的稳定性，不宣称事务读取外部文件。

## 另存与派生信息

仅接受 `.png` 的不存在新目标；**原路径、大小写/点路径别名、已有文件/目录（含源硬链接/目录别名）、NTFS备用数据流、设备路径都拒绝**。不存在目标以同目录随机临时文件CreateNew编码，重新完整解码验证PNG格式与尺寸，然后File.Move发布；不会Replace/覆盖目标。两个写入者抢同名时只有一个能成功，另一方清理自己创建的临时文件；不创建缺失父目录。源从不写入。

建议默认命名 `原名-edit-01.png`，发生冲突让UI重新计算下一个可用名字，不自动覆盖。初版失败文字“另存失败，原片和已有目标不变”，成功只在原子发布后通知图库刷新。权限突然撤销时临时文件删除仍可能失败；不删除未拥有的文件。

`DerivedInfo` 是独立不可变结构：SourcePath、完整SourceSha256、ReadOnlyCollection操作摘要、OutputSize。`SaveAs`成功返回的Derivation对应保存的快照，不代表用户已经接着做的新编辑。操作摘要包含参数与Base64文本，是用于衍生关系的技术记录，不把文本/完整路径写入日志；未猜测未发布的MediaItem/MediaRepository字段。Track A 在P1-10对接真实源MediaId/新MediaId，原子保存自己的组元数据；本包不擅自写未知sidecar或框架模型。

建议设置键：`editing.toolColor=FF3B30`、`editing.coverColor=000000`、`editing.linePx=3`、`editing.textPx=24`、`editing.pixelBlock=24`。默认选不透明遮盖；这些设置由Track A保存，本包只接不可变参数，不调用全局Settings。

## 验证与性能

最终独立工作树 `tools/test-capture-tool.ps1 -Stream`：267项通过（既有221 + Editing46），C#5零编译警告。证据 `evidence/20260927-pkg5/full-stream.txt`、`editing-performance.txt`、`editing-annotations.png`、`editing-large.png`。均为离线合成输入，没有ADB/设备验收。

46项新增覆盖每种操作、中文文字、Alpha、真不透明覆盖、精确像素块、crop+rotate+annotation顺序坐标、UndoRedo/分支、Render(1)与PNG逐像素一致、完整source SHA不变、大小写/点路径/实际硬链接/备用流目标拒绝、已有目标不覆盖、写失败和取消清理、同目标竞争只发布一个完整PNG、异步历史快照和文档Dispose后在途输出、源解锁、超边长/像素数/历史/非有限参数、实际格式拒绝及长历史取消。

机器：i7-13700KF，24逻辑线程，64bit .NET Framework，Windows10.0.26200。本轮3840×2160合成PNG、1800×900像素化+中文文字：完整Render(1)24ms，四分之一尺寸预览93ms，PNG另存（渲染+编码+完整解码验证+发布）135ms。单次Stopwatch样本、暖本地文件与系统字体；不是完整编辑器交互fps或冷存储承诺。预览慢于原尺寸，因为先按原图像素执行，再高质量缩放保证操作一致。证据PNG写出不计入完整Render耗时。

未集成真实编辑器鼠标/DPI/撤销按钮，未测高DPI与其他机器字体、全部输入编码/长期峰值内存、权限在编码后撤销等系统边界；局部渲染、保存可继续编辑的工程文件与有损/WebP导出不在本包。建议P5-7标记“非破坏性引擎完成，待Claude编辑器接线与派生关系集成；R3扩展可在首发关闭”，只由Claude回填ROADMAP。
