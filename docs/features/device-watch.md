# 设备监视与无线 ADB 核心（离线交付）

本包只新增 `Features/DeviceWatch` 后端、专项测试与本说明，不修改 UI、设备选择、录屏、旧 Adb 或配对码设置。当前结论是离线实现/假进程回归通过，尚未证明 Thor 固件支持无线配对、真实插拔或服务重启后的恢复。

## 格式依据

2026-09-27 仅静态读取已配置 adb.exe 的可打印字符串，未执行 adb help/version/devices/getprop。文件 SHA256：`957E46B8615F7AF5B7292A2DDABE98D2E61940C3FB2B0545756507F080613E71`；包含 `track-devices`、`track-devices-l`、`host:track-devices%s`、`pair HOST[:PORT] [PAIRING CODE]`。字符串只证明命令存在，不能证明当前服务支持跟踪或 Thor 无线能力。

官方 [ADB host services](https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/docs/dev/services.md) 指定列表以四字节十六进制长度和文本返回，track 持续发送新列表；官方 [commandline.cpp](https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/client/commandline.cpp) 的 `track-devices [-l]` 调用 `adb_connect_command`，通过 `read_and_dump` 原样输出。核对源文件 blob `5cd3ab66dd0a7ba0173b01f954fbe76c21269483`。本实现只处理 hex4 + UTF-8 文本，不误解 proto-text/binary，不处理 OKAY（CLI 已完成握手）。本地版本尚无真实 stream 捕获证据，因此 provider 默认 `TrackFormat.Unknown`，由集成层确认具体工具后显式启用 `HexUtf8Text`。

## 监视接口与边界

```csharp
var provider = new OwnedAdbWatchProvider(adbPath); // 默认有界 devices -l 轮询
var watcher = new DeviceWatcher(provider);       // 1s轮询，150ms稳定窗口
watcher.Changed += OnDeviceListChanged;
watcher.Start();
bool clean = await watcher.StopAsync(TimeSpan.FromSeconds(3), token);
```

`IDeviceWatchProvider` 提供 PollAsync、TrackAsync、StopAsync 和 TrackSupported；独立拥有 ProcessRunner/BinaryProcessRunner，只能调用固定 `devices -l` / `track-devices -l`。不会调用 connect、reconnect、tcpip、kill-server 或任何设备 shell。CLI 可能按 ADB 自身行为启动服务，此包不会主动结束其他服务或进程。

- 快照不可变，最多256设备、65535字节/字符；重复 serial、缺少 devices 头、NUL、异常行、非法 hex/UTF-8 均拒绝。未知合法状态保留 Unknown，不视为授权。
- Device/Unauthorized/Offline/NoPermission/Unknown；移除事件为 Missing。Wireless 根据 IP:端口或 `_adb-tls-connect._tcp` 服务名判定，仅作菜单提示，不赋予权限。
- 完整空列表才移除设备。失败/超时/截断保留上一成功快照并报告 Failure，避免把通讯失败当成拔出。
- 跟踪 sink 单帧最多65535字节、整次16MiB/30分钟；持续串流不累计无限内存。完整块才发布；EOF，包括完整边界 EOF，触发重试。连续三次失败后永久退回当前实例的轮询，退避最大30s。格式未经确认直接轮询。
- 邮箱只保留最新一个快照，一个后台派发任务合并突发事件。Changed 在后台线程触发；订阅者异常隔离。订阅者需短时返回并通过 UI BeginInvoke 刷新，阻塞订阅者会保持关闭待清理。
- Mode：TrackDevices/Polling/Stopped/CleanupPending；Failure：None/ProviderFailed/InvalidSnapshot/CleanupPending。无模式自动采集动作。
- StopAsync 为调用方全局期限；取消/超时返回false、Mode保留CleanupPending。在途 provider/派发任务和拥有进程未清理前，不假报停止。晚到清理可再次 StopAsync 确认；失败的清理 ack 可重试。Stop 后不允许 Start，请重新创建明确的新实例。

集成层拥有唯一 DeviceSessionRegistry/OperationGate：Removed 或授权降级时按对应 serial 退休旧会话、取消其流/输入并等待各 owner 清理；不要切换正在录制的设备。Device 恢复只通知用户，重新读取能力/物理显示映射后创建新 generation；不要复用旧支持档案、自动续录或恢复控制。此包不会直接退休 registry 或停止业务组件。

## 无线接口与集成门控

```csharp
// guard 必须来自共享设备操作协调器；测试中的 Guard 不能用于生产。
var transport = new OwnedWirelessTransport(adbPath, sharedGuard, currentVerifiedAuthorization);
var capability = new WirelessCapability(pairSupport, connectSupport, currentVerifiedAuthorization);
var wireless = new WirelessAdb(transport, capability);
var result = await wireless.PairAsync(pairAddress, sixDigitCode, true, token);
// 用户另行填写调试连接地址，明确点击连接。
var connected = await wireless.ConnectAsync(connectAddress, true, token);
```

`WirelessCapability` 默认 Unknown，IsCurrent 默认false；Pair 和 Connection 独立，支持档案必须绑定经实机确认的设备/固件身份与当前会话，由集成层用 registry generation/能力 fingerprint 失效检查实现 callback。版本号或 help 不能设为 Supported。Connection 可单独确认已配置的调试地址；不会默认从 USB 切 tcpip。

`IWirelessOperationGuard.AcquireAsync(endpoint, token)` 必須提供可取消独占 lease；覆盖共享 device transport/操作协调，禁止和旧会话未清理操作并发。后台持有 until cleanup，不用临时 Semaphore 替代全局协调。OwnedWirelessTransport 在取得 lease 后再次调用 currentVerifiedAuthorization；guard 应保证此检查与启动期间的身份稳定。尚未接入共享 guard/能力验收时，UI 保持禁用；此包不自建第二套 registry。

接口固定 Pair/Connect/Disconnect，无任意路由、shell 或参数拼接；每次只有一个操作。断开必填 endpoint，不能执行无地址 disconnect-all。15秒进程期限，stdout/stderr各4096字符，截断或未知输出拒绝；成功文本需与请求地址精确匹配。只有收到 owned child/双管道清理 ack 才释放共享 lease；未完成时保留Busy/CleanupPending直到确认。StopAsync 还检查 operation lease 已释放。

输入仅接受明确 IPv4 或方括号 IPv6 + 1–65535端口，拒绝主机名、URI、shell字符、环回、广播/多播、IPv6 scope/link-local和含歧义IPv4。配对码仅六位ASCII数字，短时用于官方 CLI 参数；不会进入日志、设置、事件或结果，原 stdout/stderr 丢弃，仅返回枚举与固定中文提示。不要记录 ProcessRequest、配对请求或 raw异常/输出；ProcessRunner 只写随机 jobId/固定类型/时间/退出码。`explicitRequest:false`拒绝写入，成功配对不自动连接，设备消失不自动重连。

Recent/Remember/Forget 只持有规范化连接地址，最多5项；本包不写现有 Settings。UI 若持久化，单独用 `wireless.recent` 且只保存地址，不保存code/pair地址；重载时重新校验。Pair成功不进入recent；Connect成功才记住连接地址。

中文菜单：未授权→在实机确认USB授权；离线→检查线缆/调试页面后手动刷新；无权限→检查主机ADB/驱动权限；未知→禁止采集并核查状态；缺失→保留之前素材/录制恢复记录。无线对话框：分开的配对地址、六位码、连接地址；只在可信网络使用，按设备显示的新端口更新。错误结果区分地址/码格式错误、未知能力、不支持、超时、取消、设备拒绝、繁忙和待清理；无线设备拒绝未明确指出密码时不能谎称配对码错误。

## 验证与待验收

root整合补记：ShutdownCoordinator的false ack/取消回调竞态已用确定性屏障复现并修复：后续Stop预算内等待旧CancelWork完成后可重试一次，不提前释放owner；34项进程专项通过。最新统一后端Stream回归已通过，最终日志/数量见backend-final-delivery；下面保留首次失败与旧专项证据，不将历史失败删去或称为仅偶发。

`./tools/test-capture-tool.ps1 -Filter DeviceWatch`：66项新检查 +64基础；实际 self-owned fake process 覆盖轮询、分片/完整/空/截断跟踪块、取消、配对/连接/定向断开、脱敏；fake provider 覆盖三次失败降级、退避、去抖、授权变化、移除、挂住/晚到/false ack 与重试。日志检查读取真实 `Log.CurrentFile`，不是不存在的占位日志。首次全量 Stream 在既有 BackendProcessTests.Shutdown 的 false ack + CancelWork 重试竞态处失败，尚未进入本包；证据 `evidence/device-watch-stream-regression.txt` 保留。该共享源码由 root 修复，不能把此轮全量测试写成通过。BackendProcess 独立重跑32项通过，仅说明此竞态取决于时序，不否定缺陷。没有执行真实 adb、没有连接设备或安装helper。

实机验收需另行征得同意：记录 adb hash/version、固件fingerprint脱敏标识；USB拔插/授权/离线/服务退出/多设备选择不切换；核对 track 字节样本；无线页面是否存在、安全配对码流程、分开的端口、拒绝/超时/取消、断开不影响其他设备、无线截图。未完成这些不能称公开版设备验收通过。
