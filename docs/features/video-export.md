# 视频导出核心与前端接入

本包实现 P4-6、P4-5 和 P4-2 的离线后端。没有修改旧 `VideoExport.Export` 两个重载、播放器、主窗口或 `DeviceArt`；前端接线与实机验收仍由 Track A 完成。

## 入口与所有权

命名空间 `ThorCapture.Features.VideoExport`。`ExportPlan` 深复制皮肤和轨道集合，所有公开字段只读，获取 `Skin` 返回副本。每轨 `ExportTrack` 包含完整源路径、明确 `ScreenRole` 和时间偏移。正偏移表示该轨较晚开始，源时间等于共同时间减偏移。共同时间的 `Start`、`Duration` 同时适用于预览、视频和按键时间线；`Duration=0` 表示到有效轨道结束。

- `VideoExporter.CheckCapabilitiesAsync(plan, token)`：不读取媒体，检查工具、编码器和滤镜；用于对话框禁用导出并映射原因。结果 Success 仅表示能力可用，不表示媒体有效或文件已保存。实际导出会重新检查。
- `VideoExporter.ExportAsync(plan, destination, progress, token)`：精确转码、校验、源版本复查后发布新文件。`Success` 才表示已保存，`Bytes` 是实际大小。
- `VideoExporter.RenderPreviewAsync(plan, localPosition, token)`：按照相同计划采样单帧。`VideoPreviewResult.Frame` 属于返回结果，调用者必须 Dispose。它是离线 FFmpeg 采样器，会读取并校验源，不能当作实时播放器逐帧调用。滑块应去抖、取消旧请求，并以请求版本拒绝迟到结果。Legacy 计划单帧采样返回明确 Unsupported，由既有播放器处理。
- `VideoExporter.Legacy900x1120(...)`：整段、900×1120、30fps 兼容计划，直接委托原导出；保持原渲染和音轨选择。计划创建后若优先输入时间线出现或改变，拒绝发布而不偷偷切换。
- `BatchExporter.RunAsync(jobs, directory, template, conflicts, progress, token)`：最多 1000 项，视频串行；图片复用 `ImageExporter.SaveAsync`。`BatchReport.Items` 是逐项不可变报告，`RetryableJobs` 只含失败、取消、未开始项。

所有任务在后台运行；进度回调可能来自后台，前端使用 UI SynchronizationContext 的 `Progress<T>`，禁止直接修改控件。`BatchProgress.Index` 从零开始，`Saved=true` 只在最终文件发布后报告，失败不能渲染成“已保存”。取消保留此前成功项，后续标记 NotStarted。整个批次占用一个 Track A OperationGate 导出槽；本包没有跨调用全局队列，多个独立 ExportAsync 仍应由调用者串行调度。关闭窗口要取消并等待任务，不用进程名扫描杀 FFmpeg。

## 字段、默认值与支持范围

建议普通短片默认 MP4、30fps、4Mbps、Shortest、原比例 contain；上下或左右布局必须明确上下屏角色。构造计划时传入目标尺寸，不能推断控件尺寸。带壳默认可用旧 900×1120 预设；自定义带壳尺寸复用 `DeviceArt.Draw` 并等比例居中。默认按键时间线偏移为 0、显示触摸、256 色调色板。前端保存这些字段为自己的设置快照，不在任务运行中修改计划。

| 选项 | 当前行为 |
|---|---|
| 布局 | Single、Vertical、Horizontal、Framed；双轨按 Upper/Lower 排序 |
| 格式 | MP4 H.264/AAC；WebM VP9/Opus；GIF 两遍 palettegen/paletteuse |
| 结束策略 | Shortest 取对齐后短轨结束；Freeze 保持最后画面；Blank 留黑；晚开始的轨道在开始前留黑 |
| 片段 | 非整秒精确转码；帧数向上取整，成品时长量化到目标帧；GIF 另受厘秒时间单位影响 |
| 音轨 | 优先上屏中已有的音轨，其次下屏；不混音，按轨偏移和片段区间裁剪/补静音；GIF 无音频 |
| 按键动画 | Framed 使用只读 PadTimeline、固定目标帧率推进平滑状态；预览重放此前状态，保持同一时间映射 |
| 复制模式 | KeyframeCopy 明确 Unsupported，不假装关键帧复制能精确裁剪 |
| 透明 | 不提供透明 GIF/透明壳输出；背景和空轨为不透明画面 |

一般视频上限为 30 分钟、4 Mi 像素、每边 4096、120fps；编码位率 64kbps 至 100Mbps，YUV 视频必须偶数尺寸。GIF 必须显式指定 0 至 30 秒内的正时长，至多 50fps、1 Mi 像素、16 至 256 色。单个输入文件至多 8GiB；图片批次输入编码大小至多 64MiB、16 Mi 像素。这些固定上限不保证机器有足够 RAM。带壳流水线同时持有输入帧、输出帧、打包字节缓冲与 GDI 对象；尽量选择适度尺寸和帧率。

## 工具、失败与文件安全

通过既有 `VideoExport.Locate()` 找 FFmpeg，需要同目录 ffprobe.exe。UI 将 `FFMPEG_MISSING`、`FFPROBE_MISSING`、`ENCODER_MISSING_*`、`FILTER_MISSING_*`、复制模式/预算的 Unsupported 映射为中文原因。检查较保守，会要求该格式对应音频编码器和组合滤镜，即使具体输入没有音轨或只有单屏。

本包依赖 A1 的 `ProcessRequest`、`ProcessRunner`、`BinaryProcessRunner`。超过五分钟的任务使用 BinaryProcessRunner，避免短 runner 的超时上限。每个进程只由拥有方终止；带壳取消关闭编码与解码管道，有界等待并清理。长任务按阶段有超时，不承诺整项导出的实时速度或固定总耗时。

原片以 FileShare.ReadWrite|Delete 读取，不保持源文件锁；导出前后比较 SHA-256、实际长度和修改时间，变更返回 SourceChanged，不发布结果。所有输出先写独占保留的临时文件，再用 ffprobe 检查尺寸/时长/帧率/音轨，并完整解码视频和音频后 File.Move 发布。已有目标永不覆盖，源路径大小写、点路径或现存硬链接也因此不能被覆写。拒绝 ADS 和重解析点路径；临时文件、调色板、带壳中间片只清理本任务拥有的文件。

旧 Framework 对长路径支持不足，视频目标为嵌套中间文件预留 96 字符，图片预留 60 字符，超过边界返回 LONG_PATH_UNSUPPORTED。文件名模板允许 `{name}`、`{index}`、`{id}`，清理非法字符和 Windows 保留名；重名可 Skip/Rename/Fail，重命名仍不覆盖已有文件。目录写入探针和可用空间下界检查用于提前提示，空间估算不能保证整个批次不会写满。磁盘真实耗尽仍按项失败并清理未发布输出。

`Estimate()` 给粗略区间，不是容量保证。未知时长按 30 分钟预算并设置 DurationUnknown，界面应显示“时长未知，估算范围”。GIF 内容差异较大：本轮相同 160×120、12fps、0.55 秒计划，估算 1446–255615 字节；静态双屏实际 1840 字节，高变化实际 17720 字节。区间覆盖这些样本但很宽，不应显示单点预测或压缩比保证。

## 验证边界与交付证据

本包新增 41 项：17 项普通回归、24 项 Stream 回归；2026-09-27 全量 Stream 最终 682 PASS，候选构建成功。合成素材涵盖非整秒片段、正负偏移、长短轨结束策略、实际非静音 AAC、VP9/Opus、双屏带壳、自定义尺寸、带壳 GIF、按键时间线、原片哈希/时间不变、变更拒绝、缺编码器、缺 ffprobe、同名/取消/部分成功/重试/长路径及注入零剩余空间。

兼容预设和原重载分别输出，ffprobe 验证后比较首帧 framemd5 完全一致；其他成品完整视频/音频解码，GIF 静态及高变化实际大小单独记录。没有故意填满真实磁盘，也没有真实设备、交互式工作台或 UI 操作验收。输入原片和壳资源保持原样；本包不注册输出到媒体仓库、不自动分配新组、不修改任务总表。

离线总回归使用本 worktree 所有源和测试，替换仅编译的 A1/Cocoon 依赖快照（位于忽略的 evidence/game-media-dependencies，未提交、未覆盖框架源码）。依赖文件哈希见 evidence/media-repository/game-media-dependency-hashes.txt；总日志见 evidence/media-repository/video-export-full-stream.txt，候选构建为 evidence/ThorCapture-video-export-candidate.exe。这是组件兼容和合成流证据，不是正式安装版或公开测试版验收。
