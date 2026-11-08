# 3号包：图片导出引擎集成说明

Track B 只交付图片引擎，未修改主窗口、Preview、DeviceArt、现有测试或构建工具。由 Claude 审查合并并接 UI。对应 P4-1/P4-3/P4-4；公开测试版之前可以保留旧入口，不把核心交付当成界面已完成。

## 接口与所有权

同程序集命名空间 `ThorCapture`，类型 internal，C#5，仅系统程序集。

```csharp
ImageExportPlan plan = ImageExportPlan.Default(skin);
Size output = ImageExporter.MeasureOutput(plan, top == null ? (Size?)null : top.Size,
    bottom == null ? (Size?)null : bottom.Size);
ExportResult result = await ImageExporter.SaveAsync(plan, top, bottom, destination, token, progress);
// Or: Bitmap rendered = await ImageExporter.RenderAsync(plan, top, bottom, token);
// The receiving preview owns rendered and must dispose the replaced bitmap.
```

- `ImageExportPlan` 为不可变快照，包含布局、背景、两色、Canvas、1–4倍率、间隔/间隔色、圆角、PNG/JPEG质量和 `MarkMissingScreens`。Skin 在构造时深复制；getter 每次另给副本，ABXY 数组不会共享。必须传入已叠加灯光的 Skin，不重新读取 UI 灯光设置。
- `MeasureOutput` 无位图分配，立即报告最终尺寸或拒绝原因。无图片、单屏布局却传入两张图、非法计划、过大的源/结果或不足的整数画布抛 `ArgumentException`。UI 字段改变后先测量、显示尺寸/禁用原因，再启动渲染。
- 同步 `Render`/`Save` 可供离线工具使用。UI 应使用 `RenderAsync`/`SaveAsync`，在后台 Task 绘制/编码；不直接回调控件。没有自己的设备忙碌状态或编码器互斥，没有设备操作。Track A 用操作门/任务协调器限制同时导出，预览变更去抖、取消旧任务并拒收旧 generation，避免无限创建大图任务。
- 输入 Image 为借用：任务完成前保持存活，禁止同时 Dispose/修改。引擎不锁源文件、不改输入图片、不读隐式素材路径；从图库取得独立已加载位图后调用。返回 Bitmap 的所有权转给调用方。
- `RenderAsync` 取消返回取消的 Task；参数/绘制异常也通过 Task 抛出。`SaveAsync` 返回类型化 `ExportResult`：Success、Cancelled、InvalidPlan、AlreadyExists、FileFailed，并附中文原因与测量尺寸。未预期的程序错误不会伪装成功。

## 布局和像素语义

| 字段 | 推荐默认与行为 |
|---|---|
| Layout | Framed；可选 StackVertical/SideBySide/Single |
| Skin | 当前已应用灯色/亮度的快照；不改变外壳绘制代码 |
| Background | Transparent + PNG；Solid/Gradient 支持独立画布背景 |
| Color1/Color2 | 默认背景色 `(236,237,241)` / 白；渐变纵向 |
| Canvas | null 为计算出的自然尺寸；设置后按比例适配且居中，不裁剪或拉伸 |
| PixelScale | 默认1为兼容/适配模式；2/3/4为保持源像素 N×N 色块的整数模式 |
| Gap/GapColor | 24输出像素 / 透明；只作用于两张原图拼接 |
| CornerRadius | 默认0；单位输出像素，仅原图拼图和单图，不重绘壳体圆角 |
| Format/JpegQuality | PNG / 90；JPEG为1–100，必须提前选择不透明背景 |
| MarkMissingScreens | 普通构造默认true；缺屏槽内显示“缺少上屏/下屏”；旧版兼容预设为false |

`Default(skin)` 是旧版透明带壳兼容预设：尺寸与 `PreviewBox.ExportSize` 一致，倍率 `1920/790`、44设计单位外边距，调用现有绘制器。双屏、仅上屏、仅下屏均与原 `ExportDevice(true)` 逐像素一致。其空屏保持旧版黑色槽位，无字；**界面仍必须明确显示缺屏 badge 和实际轨道数**。新增带壳预设应保留 `MarkMissingScreens=true`；不得把单图复制到另一屏冒充双屏。原图布局缺一屏时只导出真实存在的单图。

单图/拼图在无 Canvas 时保持源像素和各自纵横比，横向/纵向拼接对较短一侧居中。默认1×带壳保持原上屏1920宽、下屏适配槽位的旧行为，不声称下屏也原生1:1。整数模式中只将屏幕内容按最近邻绘制，外壳仍正常高质量渲染：带壳自然尺寸按两屏所需倍率扩大，各屏准确 N×N、黑色留边，不按槽位拉伸。可在画布上留空白；2–4× Canvas 不得小于自然尺寸，余量为奇数时按整数坐标居中，避免半像素偏移。1× Canvas 使用适配缩放，与整数模式不同；圆角会裁掉边缘像素，不改变中间像素颜色。

每图边长最多16384、源/结果最多4800万像素，计算时使用宽整数，在分配前拒绝超限。可测量原生上屏4×（7680×4320）和原生双屏带壳2×；更高带壳倍率可能因面积被拒绝。单张最大RGBA结果约183MiB，另有编码/原片/预览内存，**固定限额不保证机器一定有足够RAM**。渲染器和GDI编码本身不可中途打断，取消在开始、绘制后、编码前、校验后、提交前检查；不要声称即时取消大型GDI调用。

## 保存、错误与预设

目标已存在返回 AlreadyExists，不做覆盖。写同目录随机 `.tmp`、Flush、重新打开校验尺寸/格式，再以不覆盖 `File.Move` 提交。目标在导出途中出现时保留外部新文件，返回 FileFailed；不会替换原片。失败/取消尝试删除临时文件；文件系统拒绝清理时无法保证删除成功。选择目标目录和冲突命名由 UI/批量调用方决定，引擎不创建陌生目录。未记录完整目标路径或异常内容。

进度0/60/100分别代表开始、绘制完毕、原子提交完成，不能当作准确编码进度。progress消费者抛异常仅写固定日志，不反转已提交结果；编码前取消不产生目标文件。磁盘满、目录不存在、权限/目标占用均有类型化失败，UI需保留原片并显示可恢复提示。

`ExportPresetStore.LoadPresets/SavePresets` 通过现有 `Settings.Get/Set` 读写 `export.presets`；另有纯 `Encode/Decode` 便于 A4 的 SettingsStore 适配。最多32个预设、中文名称1–80字符，版本2的 Base64 二进制记录保存全部Skin字段（含Empty灯色语义）、缺屏选项和所有计划字段。坏版本/损坏/超长/重复或非法字段/尾部多数据一律返回空列表，UI继续内置兼容预设，不自动覆盖坏设置。保存失败返回false，显示“预设未保存”，不把失败当成功。

UI推荐内置预设：旧版透明带壳、带缺屏提示的带壳、原图纵向双屏、原图横向双屏、单屏2×像素；保存自定义预设时让用户命名。输出尺寸必须在界面可见。JPEG透明模式直接禁用并提示先选背景。没有新增界面字段或按钮的实际接线。

## 预览与批量复用

对导出对话框，先 `MeasureOutput`；合法时后台 `RenderAsync` 返回最终分辨率图，UI缩放显示同一张图，保存时仍用同一计划。不要通过改变 Canvas 来声称“缩略图与最终像素一致”；那是另一个输出计划。大型整数图先显示现有封面和尺寸，用户主动确认预览；避免每次鼠标移动都渲染近4800万像素。

10号包批量导出复用 `SaveAsync`，串行或受控并发；每个目标独立处理 AlreadyExists，不覆写或删除输入，取消时已提交图片保留。同一批绑定计划/两屏图快照，不读取当前变化的 UI。本包没有依赖未合并的媒体接口，源角色/配对真实性由 Claude 的 MediaRepository 提供。

## 证据与建议计划状态

全部使用合成棋盘/纯色、临时目录；没有用户图库或设备测试。`evidence/20260927-pkg3/stream-regression.txt` 为本包完整离线流回归：279项全PASS（原221 + 新增58），没有编译警告，FFmpeg6.0。新增检查覆盖四种配色、灯开/关、单双屏旧版逐像素兼容、透明alpha、深快照、1–4整数块、奇数Canvas/布局/缺屏/间隔/圆角/内存拒绝、JPEG背景、预设、目的地竞态/原文件保护、写失败/取消/坏progress。

合成原生1920×1080 + 1240×1080、白壳灯开的一次冷带壳绘制448ms、结果2345×2818，原始记录 `evidence/thor-capture-tests/image-export-performance.txt`；PNG样张 `image-export-framed-synthetic.png`。测量排除保存，只证明本机离屏引擎调用，不是UI帧率、实际截图或端到端延迟。首次界面接线后仍需验证字段/预设迁移、大图内存、取消与DPI。

建议 Claude 将 P4-1/P4-3/P4-4 标“引擎完成，等待导出对话框/预览与批量接入”。按 AGENTS 由 Claude 更新 ROADMAP、合并分支，本包不改主目录或用户正在运行的EXE。

## 界面接入（2026-10-18，Claude）

- 主窗口 Ctrl+E / 导出按钮 / 「导出图片…」「导出双屏图…」菜单打开 `ExportImageDialog`（`src/UI/ExportImageDialog.cs`）：左侧棋盘格实时预览（去抖、后台渲染、只用图片副本），右侧版式（带壳 / 上下拼接 / 左右拼接 / 展示图；单屏时为带壳 / 仅截图）、背景（透明、浅灰、白、石墨、纯黑、三种渐变、自定义色）、留白（无/窄/标准/宽）、投影、屏幕圆角、PNG/JPEG、1–3× 整数倍率，并显示最终像素尺寸；超过 4800 万像素时禁用导出。视频仍走「视频导出」。
- 计划新增可选参数 `shadow`（带壳：外壳投影；拼接：每块屏幕下的柔和投影）和 `padding`（-1 保持旧边距；带壳按外壳设计单位、拼接按源像素×倍率）。默认计划与旧 `ExportDevice(true)` 逐像素一致。预设编码未包含这两项，解码后为默认值。
- 选择保存在设置 `export.bg`（`none` / `solid:RRGGBB` / `grad:RRGGBB:RRGGBB`）、`export.pad`、`export.shadow`、`export.round`、`export.format`、`export.scale`。Ctrl+Shift+C 和「复制双屏图」按上次的设置直接复制。
- 剪贴板同时放入 PNG 数据（保留透明，支持的软件可直接粘贴透明图）和铺在浅灰底上的普通位图。JPEG 不支持透明，选透明背景时改用浅灰底。展示图使用固定深色版式，背景/留白/投影/倍率不适用。
- 测试 `tests/ExportStyleTests.cs`；样张 `evidence/ui-check/export-dialog-framed.png`、`export-dialog-dual.png`、`export-transparent.png`。
