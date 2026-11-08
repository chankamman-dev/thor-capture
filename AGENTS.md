# Thor Capture：Agent 交接说明

面向接手开发的 agent。用户文档见 `README.md`；本文件讲怎么改、哪里容易踩坑、下一步做什么。

## 与用户协作

- **用中文和用户交流**，界面文案也用中文。代码注释和 commit message 用英文，风格参照现有代码与 `git log`。
- 用户在意外观是否贴近 AYN Thor 实机。用户给实机照片时，按照片取色和比例对齐（取色方法见下文「目视检查」）。
- 底部凸台黑键上的 **AYN 蚀刻 logo 是用户后来明确要求加上的**（`DeviceArt.AynMark`，低对比内凹）；除此之外不要加其他品牌字样，也不要加没有依据的装饰细节（如假零件、排线）。保持干净。
- 高光要**渐隐**，不要画两端截断的等亮弧线或直线（用 `EdgeLight` / `FadingLine`）；黑色机型的灯光反射按材质反照率减弱漫射，不能铺成一整圈平色。
- 改外观后先跑 `tools/render-check.ps1` 看效果，再交付，并把截图发给用户。
- 用户经常开着 `artifacts\ThorCapture.exe`。**不要结束用户的进程**；需要重新构建时，把运行中的 exe 改名为 `ThorCapture.old.exe`（Windows 允许改名正在运行的 exe），再构建，最后告诉用户重启程序。
- 实机测试（`-Live`）需要用户连接 Thor，先征得同意。

## 构建与测试

```powershell
./tools/build-capture-tool.ps1        # 输出 artifacts/ThorCapture.exe
./tools/dev.ps1                       # 开发模式：src 改动后自动编译并重启 artifacts/ThorCapture.dev.exe（与正式版共用数据，需先关正式版）
./tools/test-capture-tool.ps1         # 本机回归，当前数量以运行结果为准，全部应为 PASS
./tools/test-capture-tool.ps1 -Stream # 最新1147项，含后端/合成流/系统声音/内置回放/双屏带壳视频，不连接设备
./tools/test-capture-tool.ps1 -Live   # 经用户同意：单屏与双屏短录制 + 12 秒视频流接收采样
./tools/render-check.ps1 [-Window]    # 四种配色的外壳图（+主窗口截图）→ evidence/ui-check/
./tools/measure-animation.ps1         # 固定模拟姿态 CPU 绘制测量，不连接设备
./tools/check-playback.ps1 -Source artifacts/captures/thor-upper-批次.mp4 # 读取已有录屏，跳转/导出/逐帧解码；输出仅 evidence/
```

- 编译器是 Windows 自带的 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，**语言版本 C# 5**：不能用 `$""` 插值、`?.`、`nameof`、表达式体成员、自动属性初始化、`out var`、元组。可以用 lambda、`async/await`、对象初始化器。
- 不能用 NuGet，只能引用 .NET Framework 自带的程序集：System、System.Core、System.Drawing、System.Windows.Forms、Microsoft.VisualBasic。
- 脚本递归编译 `src/` 下的所有 C# 文件，新增源文件不用改脚本。
- 测试中的「假 adb」就是测试 exe 本身：`Adb.ExePath` 指向测试程序集，由 `THOR_CAPTURE_TEST_MODE` 环境变量切换行为（见 `tests/Tests.cs` 的 `Fake()`）。新增 adb 交互时，在这里补一个模式。

## 代码结构

| 文件 | 内容 |
|---|---|
| `src/ThorCapture.cs` | `DeviceDisplay`、`Adb`（查找 adb、执行命令、`Displays()` 解析 dumpsys、`Capture()` 截图）、`Program.Main` |
| `src/Recording.cs` | 单屏录屏：`screenrecord --display-id`，按 PID 发 SIGINT 停止，`.partial` 下载后校验 MP4 结构 |
| `src/RecordingSession.cs` | 单/双屏录制会话，共享批次，并发启停、单侧失败停止另一侧、复制共享输入时间线、共享系统音轨 |
| `src/RecordingAudio.cs` | 固定 scrcpy-server 3.3.4（SHA256 校验）的 PCM 时间戳采集、WAV、拥有通道清理；Android13+ playback/audio_dup，保留外放，不启用麦克风/控制 |
| `src/MediaAudio.cs` / `src/PcmOutput.cs` | FFmpeg 音轨探测/原子合成、Windows waveOut 单块 PCM 输出，暂停/跳转清空 |
| `src/LiveVideo.cs` | 每屏持续 H.264 → FFmpeg BGRA 管道，单帧邮箱、启动超时、按 PID 和启动时刻清理进程 |
| `src/MainForm.cs` | `Settings`（exe 旁的 `thor-capture-settings.txt`）、`ShellThumbnails`（MP4 缩略图）、主窗口布局与全部业务流程 |
| `src/Preview.cs` | `PreviewBox`：三种模式、鼠标外壳演示、独立动画时钟、局部刷新、平移、视频封面、导出 |
| `src/ShellInteraction.cs` | 外壳设计坐标命中、局部刷新范围、纯本地鼠标状态、按时间推进的 `PadMotion` |
| `src/ControlLayers.cs` | 可复用的 10 个控件图层，只按相关姿态/配色/尺寸失效 |
| `src/VideoPlayback.cs` | 本地 MP4 单/双屏共同解码与时钟、最新帧邮箱、跳转版本、时间线、拥有进程清理 |
| `src/MainForm.Playback.cs` | 内置播放器控制条、进度、播放/暂停/重播、与图库/忙碌/关闭的协调 |
| `src/DeviceArt.cs` | `DeviceSkin`（四种配色的全部颜色）与 `DeviceArt.Draw()`（在设计坐标系中绘制正面外壳） |
| `src/Controls.cs` | `Ui`（颜色、字体、`S()` DPI 缩放、绘图辅助）、`Glyphs` 图标码位、`FlatButton`、`Segmented`、`SkinPicker`、`SearchBox`、`Strip`、深色菜单、`GalleryView` |
| `tools/RenderCheck.cs` | 目视检查工具，无需设备 |

### 关键约定

- **显示 ID**：`screencap -d` / `screenrecord --display-id` 必须用 dumpsys 里的**物理** ID（`uniqueId='local:…'`）。用逻辑 ID 会静默输出空文件。下屏的逻辑 ID 会变，靠「两块屏且其一逻辑 ID 为 0」判断上下屏。
- **文件命名**：`thor-{upper|lower}-{yyyyMMdd-HHmmss-fff}-{6位hex}.png`，同一批双屏截图共用后缀。配对方式：把 `-upper-` 和 `-lower-` 互换后找同名文件（`MainForm.PeerOf`）。旧文件名没有随机后缀，同样适用。录屏为 `thor-{slug}-{时间}-{8位hex}.mp4`。
- **设置项**：`captureDir`、`skin`（black/white/rainbow/purple）、`view`（0 带壳、1 单图、2 为 1:1）、`sidebar`、`lampOn`（1/0）、`lampColor`（`auto` 跟随配色，或 6 位十六进制 RGB）、`lampLevel`（10–100）。
- **摇杆灯光**：`MainForm.ApplySkin()` 把灯光设置叠加到当前配色（`DeviceSkin.WithLamp`），预览、带壳导出、带壳视频都从 `preview.Skin` 取灯光。`LampLight.cs` 逐像素计算灯环对碗壁的漫射与光泽反射（环形光源采样、按杆帽位置软遮挡），不再在杆帽上叠加模糊溢光（用户认为像涂抹）；杆帽齿纹的受光在 `Stick3D.LampEnergy`。灯环本体：饱和主体、内外边缘最亮（光从亚克力边缘射出）、中部台阶淡亮带；中心轴孔是暗的。杆帽：顶面是**外凸**球面（不是内凹），侧面是整圈滚花的圆弧“轮胎”截面，不是直壁。几何尺寸（碗、灯环 29–49、杆帽 43、支点在机身下方）取自用户实拍特写，调整时对照照片。
- **外壳几何**：设计坐标系为 877×1045。上屏 `(44,26,790,444.4)`，下屏 `(223,591,432,376.2)`，比例对应 1920×1080 和 1240×1080。按键坐标见 `DeviceArt.Controls()`。导出倍率为 `1920/790`，使上屏按原生像素绘制。
- **鼠标演示边界**：用户明确选择「仅演示外壳动画」。`ShellPointer` 与实机输入只在 Preview 中合并；不能向 ADB 发送操作，不能写入 `.input.csv` 或带壳视频的时间线。画面下面的肩键条可点击；预览为其保留 40 设计单位，不改变壳体和导出比例。松开、失去捕获/焦点、禁用、切模式均释放本地状态。不得把实时画面刷新重新接回整块控件失效。
- **视频回放**：`VideoPlayback` 同时解码两轨到一帧，使用共享单调时钟；不能改成两个播放器各自跑。回放输入来自录制 CSV，优先所选屏、缺失时读配对屏；不受当前实机状态覆盖，不记录鼠标演示。跳转/重播清掉旧帧并换 revision，音频暂停/清缓冲并等待解码结束。帧转交给 Preview 后必须移交位图所有权；切选/删除/关闭等等待 StopAsync，不能杀用户的其他 FFmpeg。内置可播放原片音轨，最大960×540/屏；双屏只播放/导出一份声音，回看和导出都以较短轨结束。输入偏移自定义目前仅在导出，逐帧和轨道校正仍待开发。
- **透明紫**（`Clear = true`）：先画不透明的深色底，再叠加半透明颜色，保证导出的 PNG 不透底。颜色取自用户的实机照片，调整时对照该照片，别凭感觉改。
- **忙碌状态**：`busy` 为 true 时（检测设备、截图、录屏），禁止关闭窗口和切换设备，操作完成后由 `SetBusy(false)` 刷新按钮状态。

## 已知的坑

- **`Ui.Text` 是颜色字段**，画文字请用 `Ui.Write()`（两者重名会报 CS0102）。
- 所有像素尺寸都要经过 `Ui.S()`；字体用 pt，会自动随 DPI 缩放。
- `TextRenderer` 不认 `Graphics` 的变换。`DeviceArt` 是在缩放后的坐标系里绘制的，里面只能用 `DrawString`。
- **Git**：如果项目目录属于另一个 Windows 账户，git 会报 dubious ownership。没把目录加入 `safe.directory` 时，每条命令需带 `git -c "safe.directory=<仓库路径>" …`。
- **PowerShell 5.1**：含中文的 `.ps1` 必须保存为 UTF-8 带 BOM，否则会乱码甚至解析失败（现有两个构建/测试脚本都带 BOM）。
- 用 `sed` 写反斜杠路径时，`\r` 会被当成回车，编辑 `.ps1` 请用编辑工具。
- GDI+ 的 `Image.Save` 在路径超过 260 字符时会失败，临时文件别放在很深的目录。
- 在非交互 shell 里用 `Start-Process` 启动 winexe 可能会最小化，应直接调用并用 `| Out-Null` 等待。
- `Form.DrawToBitmap` 截不到深色标题栏（DWM 绘制的），这是正常现象。
- `evidence/`、`artifacts/`、`backups/` 都不纳入版本控制。
- **测试里的 `await` 界面代码**：用 `Application.DoEvents()` 泵消息时，它是最外层循环，每次结束 WinForms 都会卸载自动安装的同步上下文，续体会偶发跑到线程池线程上操作控件（表现为随机失败或「执行 CreateHandle() 时无法调用 Dispose()」）。套件开头设 `WindowsFormsSynchronizationContext.AutoInstall = false` 并固定一个上下文，结束时恢复，见 `tests/GameEditingTests.cs`。正式程序有 `Application.Run`，不受影响。

## 开发计划

完整的任务计划见 [`docs/ROADMAP.md`](docs/ROADMAP.md)，开源交付清单见 [`docs/OPEN-SOURCE-RELEASE.md`](docs/OPEN-SOURCE-RELEASE.md)。保留 P0–P6 编号，增加 P7 开源发布和 P2-8 鼠标外壳演示，共 57 项；任务状态以 ROADMAP 为准。任务包含范围、依赖、验收、验证/回退、优先级和工作量。

按计划 B0 → B1 开始：保存现有成果，再完善进程生命周期、日志、测试和状态管理；随后拆服务/迁移设置/媒体模型，优先完善双屏录屏与预览性能，再做图库、桌面体验和开源发布。双屏录屏及 H.264 预览已有实现，不重做，也不能把合成证据视为实机验收。等待设备期间继续离线工作；实机测试仍遵循上文同意要求。

普通可恢复开发不为每个任务重复请求确认；许可证/权属、公开仓库与实际发布等决定在相应门槛处理，先准备可审阅材料。完成任务后更新 ROADMAP 的状态与证据，未测项保留；0.1.0 公开测试版和 1.0 稳定版分别验收，不要求全部实验扩展完成才首次发布。

**分工（2026-09-27 起，用户指定）**：框架与界面设计（P1 全部、P5-1、P6 的应用外壳和界面、外观渲染，以及所有功能的界面集成）由 Claude 负责（Track A）；细分功能拆成 11 个任务包交给其他 agent（Track B）。领取任务包前必须读 [`docs/tasks/README.md`](docs/tasks/README.md)：每个包在独立的 worktree 和分支上开发，只在 `src/Features/<包>/` 新建代码，不改界面和框架文件，交付接口 + 测试 + 集成说明，由 Claude 合并并接到界面上。

**最新分工覆盖（2026-09-27，用户明确指定）**：Codex 负责后端，Claude 负责前端。后端框架、数据与功能核心不再保留给 Claude；界面、外壳渲染及前端集成仍归 Claude。继续独立批次、保留在途修改；协作与当前后端目录见 [`docs/BACKEND-FRONTEND-HANDOFF.md`](docs/BACKEND-FRONTEND-HANDOFF.md)。`docs/tasks/README.md` 的旧文件归属与此冲突时，以本条和用户最新分工为准。

## 当前状态（2026-09-27）

最新后端整合状态优先读 [`docs/BACKEND-COMPLETION.md`](docs/BACKEND-COMPLETION.md)，用户12项、既有计划清点和状态见 BACKEND-WORKPLAN。Process/Device broker/状态/设置/媒体组/监听/编辑/恢复/导出核心已交付；旧测试数和“等待A1”段落仅作历史。Claude接UI、Codex后端；未替换在用EXE、未导入生产库或授权实机。最终固定证据在独立后端worktree的 evidence/backend-final-delivery.json，不能用源码回归替代发布验收。

- **Cocoon 合并 · 游戏库前端第一期**：用户选择原生重写、分期（不用 WebView2）。左侧导航「工作台 / 游戏库」；游戏库页由本窗口按需启动并拥有 `node backend.cjs`（`cocoon.backend/1`，后端归 Codex），token 只存内存，关闭时通过 stdin 发送 shutdown，绝不接触非本程序启动的进程。浏览、搜索、筛选、详情、素材槽位已原生实现；深度编辑暂时「在浏览器中编辑」。数据根默认 `data\cocoon`，不读原项目数据。详见 [`docs/LIBRARY-FRONTEND.md`](docs/LIBRARY-FRONTEND.md)。演示库用 `tools/cocoon-demo.cjs` 生成；`TextRenderer` 忽略变换，详情页的滑入和滚动偏移直接加进坐标。

- **触屏操作反馈（离线完成，待实机）**：「操作反馈」同时读取手柄和触摸屏（`src/Touch.cs`）。`getevent -lp` 中带 `ABS_MT_POSITION_X/Y` 的节点视为触摸面板，按长宽比猜测对应上/下屏，竖向面板按旋转 90° 处理；可在设置 `touch.<serial.name.path>` 写 `屏幕,旋转,镜像` 覆盖。多点触控支持协议 B（slot）与协议 A，SYN_DROPPED 清空。触摸样本以 `T,<ms>,<屏:id:x:y|...>` 行写入同一 `.input.csv`，实时预览、内置回放、带壳导出共用 `TouchHistory` 生成按下圆点、拖动尾迹、松开波纹、长按外圈。导出默认显示，导出对话框可关闭；原片不改动。触摸读取失败不影响手柄。**旋转方向、面板与屏幕对应、坐标镜像、实际延迟都未实机验证**，首次实机测试需确认，必要时补「触摸校准」界面。样张 `evidence/ui-check/touch-feedback.png`、`touch-motion.gif` 为模拟触摸。

- **录屏声音批次**：新增系统媒体声音/无声切换（默认系统声音），固定未修改 scrcpy-server 3.3.4 的临时 ADB shell 采集，Android13+保留设备外放；不安装APK、不提权、不启用麦克风。单双屏共用 PCM 时间戳声音，AAC 合成保留原始 H264 数据，失败保留原片/WAV。内置 waveOut 回放和带壳导出保留一份音轨；暂停/跳转/关闭已做离线验证。149基础、221含流检查通过，含真实Windows全零PCM暂停/恢复；实际窗口与四配色见ui-check，文档RECORDING-AUDIO.md，交付audio-delivery.json。没有真实ADB采集，权限、游戏声音、音画偏移/长期漂移和外放仍待实机。旧无声视频无法恢复声音。构建自动复制固定服务器和第三方许可，发布时必须随附。

- **录屏期间取景批次**：单/双屏录制自动连续截图取景，支持暂停/恢复，取景失败不停止录屏，不开启额外视频编码器。录屏 busy 分支保持输入/画面刷新，ShowSelected 不覆盖取景；停止后按generation拒绝迟到帧、等待在途截图、恢复此前普通预览意图。新增9项基础与5项流流程检查，全量193项通过；离线窗口/四配色与模拟录屏界面已检查。文档 docs/RECORDING-PREVIEW.md，交付 evidence/record-preview-delivery.json。连续截图不称高帧率共享视频流，实际设备并发与影响仍待验收。


- **2026-09-27 预览调参批次**：新增 PreviewOptions/PreviewOptionsDialog，每屏0.1–100Mbps、0–2160高度上限（0原生）、1–120电脑送显上限、单/双屏、原生PNG；保持旧默认，设置与截图/录屏独立。LiveVideo及时Flush、raw帧尾flush_packets、绝对节拍、最新帧邮箱；状态区分接收/送显，不能称源端fps或端到端延迟。新增测试共179项通过，说明 docs/PREVIEW-TUNING.md，交付 evidence/preview-settings-delivery.json。本轮用户明确选择先离线开发，没有实机ADB测试；P3-3仍待实机。


- **最新内置双屏回放批次**：同组任一屏视频都在带壳视图显示两屏；内置播放/暂停、空格、进度跳转和重播。右上角/底部/Ctrl+E 提供视频导出，外部播放器保留为显式可选入口。回放显示录制时间线，不接管真实输入记录。114项基础、含视频流和播放器共153项通过；四配色/实际窗口渲染已执行。已有双屏录屏3个时间点跳转与约111.85秒完整带壳导出/逐帧解码通过，原片/CSV哈希未变。交付见 `evidence/playback-delivery.json`，原片检查见 `evidence/saved-playback/verification.json`；没有本轮实机采集。P3-5保持部分完成：偏移/逐帧/片段工作台仍未实现。
- **本轮鼠标演示与动画优化**：点击 ABXY/系统键/肩键条、拖动十字键与摇杆、右键 L3/R3，仅本地动画。预览独立 15ms 定时调度，按真实 elapsed 平滑按压力度；静止停表、部件缓存、局部失效，保留 3D 与灯光。角度预计算和灯碗覆盖裁剪降低 CPU。109 项基础检查及 `-Stream` 共 125 项通过；含鼠标捕获/失焦、模式切换、时间步长、缓存隔离、直接绘制对照。四配色与离线窗口渲染已执行，`mouse-demo.gif` 为模拟鼠标事件。交付见 `evidence/mouse-demo-delivery.json`；性能方法见 `docs/ANIMATION-PERFORMANCE.md`。本轮没有 `-Live`；大窗口双摇杆仍有 CPU 开销，不承诺实际帧率。
- **双屏录屏与视频流预览更新**：选择「双屏」后并行启动两路录屏、统一停止，原生分辨率的两段 MP4 共用文件名后缀，图库视频也会配对。两路输入时间线共用原点；带壳视频可同时显示双屏，按较短原片结束。单侧编码或下载失败会尝试停止另一侧，保留成功原片与未完成文件。不保证帧精确同步，暂无实机证明设备支持两路编码器。
- **预览实现**：`LiveVideo.cs` 使用 `adb exec-out` 的 H.264 输出与现有 FFmpeg，每屏最大 960×540、3 Mbps，只留一个最新帧；接收帧不等待另一屏。外壳、屏幕、按键图层分离，静止按键/灯光缓存。截图、录屏、合成前释放预览编码器，操作结束恢复；断流或缺依赖显式回退截图预览。旧 Android 180 秒正常到期才自动重连。关闭窗口异步等待清理，不结束用户其他进程。状态 fps 是解码接收速率，静态场景为 0 不代表断线；未测端到端延迟。
- **上一批视频流验证**：默认本机回归 66 项；`-Stream` 共 82 项通过，含合成 H.264 流的实时管道、双屏并行、单帧邮箱、启动失败、卡住时停止、真实主窗口的降级/重启/关闭、双屏带壳逐帧解码和取消。证据 `evidence/dual-recording-regression.txt`、`evidence/stream-regression.txt`、`evidence/thor-capture-tests/dual-framed.mp4`；均为模拟设备/合成片源。离线 `render-check.ps1 -Window` 已执行。**没有运行本次 -Live**，实际 fps、端到端延迟、双屏启动偏移、三分钟续流及长期内存仍待用户同意后验证。后续可直接用新的 `-Live` 采集双屏短录屏与 12 秒流接收数据（需要动态场景）。

以下为前几轮外观与输入更新记录，旧测试数量及单屏限制不表示当前状态。

- 灯光后续打磨：灯环由均匀色块改为近白亮芯和饱和彩色径向衰减，添加凹槽内的柔和泛光、下侧弧面反光和轴座弱染色；`Stick3D.LampBounce` 对固定环形光源进行 12 点采样，按转动后的侧壁法线、距离计算杆帽下缘染色。顶部哑光保持克制，无光配色不产生反射。几何位置仍固定，遮挡由前后绘制和杆帽网格决定。模拟样张 `lamp-lighting.png`、`lamp-reflection.gif` 在 evidence/ui-check/；51 项本机回归通过，包括灯圈外的凹槽反光检查。CPU 渲染时长见 controls-render-timing.txt，不代表实机帧率。

- 最新用户要求：十字键反馈要比写实版更醒目，允许适度夸张。倾角由 0.055 增至 0.12 弧度，下压系数由 0.7 增至 1.6；保持中心支点，不做整块滑动。摇杆改为 `Stick3D.cs` 回转体网格：杆帽含圆角底缘、侧壁、齿纹肩部和凹面，绕轴三维旋转、按深度排序，凹面插值光照；不能退回二维缩放圆片。露出轴座改为与杆帽同族石墨灰，取消近黑色空洞；灯环保持固定。ABXY 增加圆角倒边和受压时减少的侧壁。最新动画 `stick-volume.gif`、`dpad-emphasis.gif`（均在 evidence/ui-check/，模拟输入），完整四配色及窗口渲染检查已执行。绘制开销记录在 controls-render-timing.txt，仅本机离屏渲染，不代表实机 fps。

- 摇杆照片校正：灰色 collar 原先错误地随杆帽移动，已替换为固定在机身中心的灯环扩散罩；只有橡胶杆帽、深色底面和轴杆移动，偏转露出相反侧的固定灯环，L3 仅压低杆帽。两侧摇杆及视频导出共用 `DeviceArt.Stick`。灯色仍按配色设置，不从输入推断真实硬件灯色。新增固定灯环与遮挡的像素回归，目前 50 项。`evidence/ui-check/stick-fixed-ring.png`、`stick-fixed-ring.gif` 为模拟输入证据。

- 十字键后续改为 `Dpad3D.cs` 软件绘制的立体网格：有高度的侧壁、外圈倒角、内凹接触面；根据旋转后法线计算漫反射和塑料高光，凹面插值光照。用户明确指出平面变色不够，不要退回纯多边形填色。仍保持固定槽位和小行程。`render-check.ps1 -Window` 额外生成 90 帧模拟输入样张，动态预览为 `evidence/ui-check/dpad-3d-motion.gif`。48 项本机回归通过；没有新的实机验收。

- 用户提供实机按上/左/右照片后再次修正十字键：原大幅投影会产生滑动和长黑底座，已改为固定窄槽内轻微倾斜；圆形区域跟随壳色，四个内凹五边形键面、细中心缝和低对比压印方向标记。不要恢复大幅滑动或彩色高亮。照片保存在 `evidence/dpad-photo-reference/`；静态照片不能证明实际回弹时间。

- 按键功能：`InputCapture.cs` 发现游戏手柄节点、解析 SYN_REPORT 完整状态、校准、记录 `.input.csv`；`MainForm.Input.cs` 提供实时反馈、低帧率预览与导出选项；`VideoExport.cs` 使用 FFmpeg 合成 900×1120/30fps 带壳视频。原片保留。
- 本机回归 48 项；带壳视频合成与取消另有 3 项集成检查。`evidence/dpad-motion-framed.mp4` 来自历史录屏和模拟按键，不是实机同步证据。十字键重做为中心支点倾斜、接触阴影、厚度和短促复位；ABXY 下压取代高亮圈。实际预览 fps 仍待实机测量。
- 外观检查：`tools/render-check.ps1 -Window` 使用离线 MainForm，不探测设备；新增 `evidence/ui-check/input-feedback.png` 模拟按压对比。
- **本次没有连接实机验收**。下一步经用户同意后确认 getevent 权限、手柄节点、ABXY/肩键/摇杆映射、录制同步偏移及断线行为。Android 不开放输入节点时不能声称按键功能已可用；禁止自动提权。
- 输入时间以电脑收到事件为准，视频启动时间为近似，导出支持 ±5000ms 校正。当前录制暂停画面取景、继续按键动画；单屏录制，另一屏占位。
