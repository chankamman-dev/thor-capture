# 设备会话、操作租约与短命令执行：前端接入

更新：2026-09-27。Codex 负责后端，Claude 负责前端。本批为 P1-2 / P1-11 的核心第一阶段，P1-8 增加相应故障回归；没有修改窗口、渲染或当前静态 ADB 调用。

## 文件与范围

生产代码在 `src/App/Backend/`，命名空间 `ThorCapture.Backend`。类型为 `internal`，按现有递归编译方式与主程序处在同一个程序集，无 NuGet 或额外运行时要求，使用 C# 5。

| 文件 | 合同 |
|---|---|
| DeviceSession.cs | 当前连接注册表、不可变设备/双屏快照、重连 generation、断线取消 |
| OperationGate.cs | 按设备序列号互斥的资源租约；取消与清理完成分别表达 |
| ProcessRunner.cs | 主机短文本命令；后台执行、参数转义、双输出排空、超时/取消及拥有进程清理 |
| ReadOnlyAdbClient.cs | 固定序列号、只读命令枚举、来源会话绑定与迟到输出丢弃 |

这批不提供二进制流、远端 PID、远端写入或实际控制发送。现有 Recording、LiveVideo、VideoPlayback 和 Cocoon Node 生命周期仍由各自实现管理，不能直接替换为短文本执行器。Cocoon 当前仍禁用设备访问；本机资源表尚未跨进程共享。

## 会话与能力

每个应用协调器创建一个 `DeviceSessionRegistry` 和一个 `OperationGate(registry)`。连接或重新探测确认后调用 `registry.Open(serial, capabilities, displays)`；同一 serial 的新快照获得新 Guid/Generation，并取消旧会话。每次操作携带所捕获的会话，不能在后台任务中再读取界面的“当前设备”。前端应用回复前须确认 `registry.IsCurrent(session)`，不能只比较 serial。

`DisplayTarget` 保存逻辑 ID、无符号 64 位十进制物理 ID、upper/lower 角色及尺寸。物理 ID 规范化，快照数组复制；`screencap` / `screenrecord` 始终使用物理 ID。显示映射变化需要新会话。断线调用 `Disconnect(serial)`；应用退出调用 registry.Dispose。

`DeviceCapabilities` 为 Capture、VideoPreview、Recording、InputObservation、RemoteControl、CocoonRead、CocoonWrite 的标志。生产调用方只设置已有能力档案明确确认的功能；未知/拒绝不能设为支持。`None` 仍允许 Inspect。测试使用的全能力集合只是模拟条件，不能照搬为真实设备默认值；本批没有验证或自动开启 RemoteControl / CocoonWrite。

## 租约规则

`TryAcquire(session, kind, out lease)` 返回 Acquired、Disconnected、Unsupported、Busy 或 Closed；Busy 立即返回，不阻塞界面或自动排队。租约包含 JobId、会话、Kind、取消 Token、IsCurrent。

| 同一设备上的组合 | 当前规则 |
|---|---|
| 视频 Preview 与 Recording，或重复启动任一编码器 | 互斥 |
| Recording 与 Capture / InputObservation | 允许，兼容录屏期间截图取景与按键反馈 |
| Capture 与 Capture | 互斥，一次 Capture 租约可包住整组双屏采集 |
| InputObservation / RemoteControl 的重复启动 | 各自互斥 |
| CocoonRead 与其他读取/采集/控制 | 允许 |
| CocoonWrite 与任意设备操作（包括 Inspect） | 互斥，先采用保守事务规则 |
| 不同设备 | 资源独立 |

同一 serial 重连后，旧租约仍计入互斥，直到旧任务完成清理。取消 Token 只表示“请停止”，不会释放资源。所有者先 await 专用 StopAsync、确认清理，再 Dispose 租约；不要把“发送停止”当作“已经停止”。`OperationGate.Dispose()` 关闭新请求并取消旧任务，ActiveCount 仍计入待清理租约。无法确认清理时保留阻塞状态，不能允许新编码器抢占。

截图取景使用 Capture 租约，不要在录屏时申请第二个 Preview 编码器租约。当前规则没有实现单流分发，不宣称高帧率共享预览。

## 短命令执行

`ProcessRunner.RunAsync(ProcessRequest, timeout, token)` 返回 ProcessResult。超时范围 1ms–5min，输出上限默认每个流 262144 字符、可配置 1–1048576。达到上限后继续排空 stdout/stderr，避免子进程因管道满而卡住；Truncated 为 true 的结果不能当作完整解析输入。

`ProcessRequest` 接收可执行路径、短操作 Label、参数数组和可选环境覆盖，构造时复制数组/字典。不通过 cmd/PowerShell 解释参数；Windows CRT 引号转义覆盖空串、中文空格、内嵌引号和结尾反斜杠。环境键按大小写合并，null 表示从子进程移除；兼容父环境同时含 Path/PATH 的旧 Framework 情况。不会修改父进程或系统环境变量。

Outcome 为 Success / Failed / Cancelled / TimedOut / NotFound / StartDenied，并保留 JobId、ExitCode、Duration、Truncated、CleanupComplete、Output/Error。FailureType 只给异常类型，不包含原始异常消息。日志只写随机 JobId、操作标签、结果、退出码和耗时；Label 使用固定代码标签，不放用户路径/设备 serial/令牌。原始输出仅返回调用者，禁止直接汇总进诊断日志。

仅终止自己启动的 Process 对象，不按进程名/PID 搜索，不杀共享 ADB server、用户 Node、用户 FFmpeg。超时/取消后清理等待有界；CleanupComplete=false 时作业继续由 runner 保持所有权。`StopAllAsync()` 关闭新请求并重试清理；await 后还需检查 ActiveCount==0。Dispose 非阻塞，只请求关闭，不能代替退出流程中的 await。

## 只读 ADB

`ReadOnlyAdbClient(adbPath, dedicatedRunner, operationGate)` 提供 `IAdbClient.RunAsync(session, query, timeout, token)`。此 adapter 独占其 runner；StopAsync 会关闭该 runner，不与无关任务共享。

| AdbQuery | 固定设备命令 |
|---|---|
| Properties | getprop |
| Displays | dumpsys display |
| InputNodes | getevent -lp |
| Activities | dumpsys activity activities |
| RecordHelp | screenrecord --help |

全部显式绑定 `-s session.Serial shell`，没有任意 shell、root、输入发送或文件写入入口。查询未知枚举在启动前拒绝。远端拒绝会表现为失败与 stderr，不尝试提权。查询占用 Inspect 租约，不会占用视频编码器。

DeviceCommandResult 保留 SessionId、JobId、类型化结果和清理标记。重连/断线/关闭门闸导致旧租约失效时返回 Disconnected，清空 Output/Error，避免旧设备结果覆盖新会话。CleanupRequired 表示子进程尚未确认停止，继续保留租约；调用 `await client.StopAsync()` 重试清理。返回 false 时前端保持停止失败状态，不恢复冲突操作。后端 await 不捕获 WinForms 上下文；前端自己把状态更新切回 UI 线程，禁止使用 .Wait() / .Result() 阻塞窗口。

## 回归与尚未完成

`tests/BackendCoreTests.cs` 通过假子进程验证真实 Windows 启动、输出洪水、非零退出、找不到/拒绝启动、预先取消、运行中取消、超时、环境覆盖、参数转义、中文路径和日志脱敏；验证并发租约、重连迟到回复、清理边界及无关进程保留。使用 `tools/test-capture-tool.ps1 -Filter BackendCore`；完整兼容性另跑 `-Stream`，不连接设备。

专项 46 项（含脚本默认核心检查共 110 项）已通过；合并 Claude 触控提交 `3715db9` 后，完整流回归 **522/522 通过**，证据 `evidence/backend-core-stream-tests.txt`。独立 worktree 的 `tools/build-capture-tool.ps1` 已成功，EXE SHA-256 `ECD28A737FC0546F504E0EA67C571FBD222257D657827CE4B387ECA7765A1111`。证据是离线假设备/合成媒体，尚无实机验收或前端接入验收；没有替换用户主目录运行中的程序。

下一步：对接现有 CapabilityProbe 的 runner，分批迁移静态 Adb 读取；补充二进制/长作业合同与关闭汇聚；再把 Node 组件的设备请求接入同一资源仲裁；独立实现控制协议和媒体事务。P1-2 的 AppState/UI 标志替换、P1-11 的全部调用迁移均保持部分完成。
