# 1 号包：能力档案与验收准备

离线实现完成。**没有连接 Thor，没有读取真实 getprop，没有实机通过结论。** P0-2/3/4 必须等用户重新明确授权后执行。

## 接口与线程

命名空间 `ThorCapture.Features.Capability`，全部为 internal，C# 5，无新增程序集或 NuGet。

- `CapabilityProbe.ProbeAsync(serial, ICapabilityRunner, CancellationToken)` 返回 `Task<DeviceCapabilities>`。任务开始固定 serial；档案仅存完整 SHA256，不存 serial。
- `CapabilityProbe.Parse(ProbeOutputs)` / `ParseHost(ProbeOutputs)` 纯解析，支持 `DisplayViewport` 和 `DisplayInfo`，64位物理 ID 保持字符串。重复相同映射合并，冲突整体拒绝；只有两块屏且其中逻辑 ID 0 时认下屏，外接/虚拟 ID 不替代物理 ID。
- `CapabilityProbe.ProbeHostAsync(runner, token)` 返回主机 ADB/FFmpeg 版本和 H264/AAC 编解码声明。只执行本机 `version/-version/-decoders/-encoders`，不枚举设备。
- `CapabilityProbe.CacheMatches(cached, fresh)` 必须同 serial hash、完整非空 fingerprint、逻辑/物理 ID、尺寸、角色且两次探测成功；未知/失败/空映射必失效。缓存的持久化、版本、过期策略由 Claude 数据目录服务管理，本包不写 Settings。
- `ICapabilityRunner.RunAsync` 用枚举允许列表，仅 `getprop`、`dumpsys display`、`screencap --help`、`screenrecord --help`、`getevent -lp`、`dumpsys media.codec` 和主机版本/编解码器列表。集成 A1 时可适配已有 `IAdbClient`；当前独立 `CapabilityProcessRunner` 只清理自己启动的短时子进程，token 与超时均有效。
- `ProbeReply` 有 Success/Cancelled/TimedOut/Disconnected/Failed/NotFound、exit、duration、Truncated；输出双管道同时排空，各限256Ki字符。可识别且 exit 1 的 usage 帮助可解析；不凭失败输出授予其他支持。
- 探测异步后台执行，UI 需 await 后切回 UI 上下文；无事件，不管理 OperationGate 或 busy。单命令5秒、6设备命令最坏约30秒（加有界清理），主机4命令约20秒，可取消。建议 A1/A3 分配设备只读租约，避免独占任务中并发探测。

## 状态与界面接入

每项 Supported / Unsupported / Unknown；`CanUse(key)` 只对 Supported 返回 true。帮助/服务库存的 Supported **仅是选项声明/节点可读，不是录屏成功或稳定能力**。

| 能力键 | 界面用途与默认文字 |
|---|---|
| display.mapping / display.dualMapping | 物理显示选择器；未知显示「未获得明确的物理/上下双屏映射」，禁用依赖物理屏的捕获入口 |
| screencap.display | 指定物理屏截图；帮助声明支持仍需捕获实际内容验收 |
| screenrecord.displayId / size / bitRate / h264Stream / frameRate | 对应录制/预览选项；原生 source fps 未声明则不开放，不把送显 fps 当 source fps |
| encoder.avcDeclared | 媒体服务 AVC 库存信息；不是双编码器或可用性保证 |
| screenrecord.dual | 永远 Unknown，直到独立实机双录制证据；「需要双屏短录制验收」 |
| input.getevent | 节点权限库存；拒绝时禁用输入并保持画面可用，禁止提权 |
| input.mapping | 永远 Unknown，人工按键/松开/斜向/极值/断线验收前实验或默认关闭 |
| audio.playback | 当前 Android13+保留外放后端；旧SDK明确Unsupported，新系统仍Unknown直到实际声音权限/游戏/外放/同步验收 |
| preview.lowLatency | 永远 Unknown；接收/绘制 fps 不能证明端到端延迟 |
| host.adb / host.ffmpeg / host.decode.h264 / host.decode.aac / host.encode.h264 / host.encode.aac | 缺依赖或完整库存缺实现禁用相关功能，空/超时/截断显示Unknown |

入口建议：连接后只读探测、设置「能力与诊断」手动刷新。状态显示「探测中，可取消」、完成状态和每项原因；禁止 Unknown 按钮偷偷按支持启用。系统/显示重连后刷新并比对缓存。无设置键。

日志仅命令枚举、outcome、耗时、exit，不记录 argv、serial、子进程输出、完整路径、异常文本。`ProbeOutputs`/`ProbeReply` 是临时原始数据，不得直接写到公开日志；公开报告仅 fingerprint hash 与结构化能力，仍需人工审查私密画面。

## 验收工具

新增 `tools/check-device-capability.ps1` 默认生成未测试的 Markdown 表，不运行 ADB/FFmpeg；写临时文件、校验、原子移动且拒绝覆盖。

```powershell
./tools/check-device-capability.ps1
# 仅主机依赖：不连接设备，显式注入可执行文件。
./tools/check-device-capability.ps1 -HostOnly -AdbPath $env:THOR_ADB -FfmpegPath $env:THOR_FFMPEG
# 用户再次明确同意后才允许；本次未执行。
./tools/check-device-capability.ps1 -Live -ConfirmLiveAuthorized -Serial '<本轮确认serial>' -AdbPath $env:THOR_ADB -FfmpegPath $env:THOR_FFMPEG
```

`-Live` 缺少确认或 serial 时编译/连接前拒绝。独立工具入口还检查本轮授权环境 guard。不是安全权限系统，调用者必须真正先获用户同意，不能自行设置guard绕过。工具 Ctrl+C 取消探测，模板状态仍未测试，不把探测当录制验收。正式采集、播放、导出沿用现有授权的 `test-capture-tool.ps1 -Live` 加手工流程，不自动触发。

生成表覆盖 P0-2/3/4 和声音；手工补候选hash、设备系统、USB/无线、尺寸码率、主机/DPI、场景、命令/结果、原始数据相对位置。完整解码、双屏启动差、偏移/漂移，动态帧率/同镜头延迟样本中位数/P95、30分钟CPU/内存/180秒续流均须真实记录。私密游戏画面公开前需人工审查。

## 回归与计划状态

固定fixture包含双屏 viewport、DisplayInfo/重复、权限拒绝；测试另覆盖空/冲突/虚拟、系统变化、重连/设备变化、编解码表、帮助exit1、截断、参数注入、真实子进程假ADB成功/拒绝/空/断线/超时/取消/输出排空。测试不连接设备。

2026-09-27：新增37项能力检查，完整基础186项、`-Stream` 258项全通过，无编译警告。证据 `evidence/20260927-pkg1/base-regression.txt`、`stream-regression.txt`。离线模板与缺少授权的 Live guard 均执行验证；主机只读探测 ADB 1.0.41、FFmpeg 6.0（H264/AAC 编解码声明可用），记录 `host-acceptance.md`。没有执行 `-Live`、getprop、显示探测或真实输入读取。

建议 Claude 标记：P0-5「离线核心与工具完成，界面接入/真实能力矩阵待验收」；P0-2/3/4、P3-2/3 **维持待实机**。不把 package 交付视作公开测试版发布完成。由 Claude 审查合并本分支、适配 A1、接 UI/cache 后再更新 ROADMAP。
