# CocoonBackend 生命周期故障合同

本批基于主集成在途 `src/Library/CocoonBackend.cs` 副本，保留 cocoon.backend/1、DeviceBrokerAddress 和现有前端 Start/Stop/StopAsync 接口，只在独立工作树修改。它拥有自己的 Node child，绝不枚举、attach、Kill 或终止别人的进程。本批不用真实 Cocoon 数据/ADB，Node fixtures 均在 Tests.Root，自建临时 loopback HTTP token 页与进程。

## 调用与预算

默认构造 `CocoonBackend(context)` 保持启动预算30秒、异常重启间隔500ms、60秒窗口最多3次自动重启。internal 构造 overload 的 BackendLifecycleOptions 仅为代码注入短预算，既不读测试环境变量也不改生产 Settings；预算/重启次数有严格范围校验。

`Start()` 在锁内认领 launch revision 并设置 Starting，多个并发 Start 只拥有一个 child。Starting/Ready/Stopping 时重复调用直接返回。Failed 且旧 child 尚在运行时只请求旧进程 orderly shutdown，等实际退出才重试，不开第二个实例。前序启动验证和延迟任务均按 revision 拒绝，不得改写新一代状态。

`InstanceId` 与 `OwnedProcessId` 只读，供界面诊断及离线验收；token 没有公开 getter、不会通过这些属性进入环境、URL或日志。Ready 必须来自当前拥有 child、State=Starting、stopRequested=false；endpoint 仍只接受严格 loopback HTTP 根页。token 在工作线程读取，返回时再次检查当前 child / 状态。新的 launch 拒绝与上次成功 launch 重复的 instance 或 token（INSTANCE_REUSED / TOKEN_REUSED）；失败时不给出 API endpoint。关联身份只在本 owner 的内存中，不持久化。

`Stop()` 异步发 shutdown 并关闭 stdin fallback，即刻撤销 endpoint/token；保持用于 shutdown 的 instance。显式 Stop 递增 revision，取消挂起的手动重试和自动重启。child 实际退出前保留 Process 和 PID，不以状态 Failed/Stop 请求当作已经清理。

`StopAsync(timeout,cancellation)` 用 Stopwatch 单调预算等待真实 child exit；成功 true。超时 false + BACKEND_STOP_TIMEOUT；取消 false + BACKEND_STOP_CANCELLED；两者都保留所有权，不能开始重叠进程，也绝不 Kill。底层 Node 若不合作，前端显示“停止未完成”，保留 owner 并可再次 StopAsync；不能把 false 当作已退出，也不能 Dispose 后遗忘尚在运行的拥有进程。Dispose 保持旧异步 Stop 语义，窗口关闭仍应显式 await StopAsync 并处理 false。

Start 超时 BACKEND_START_TIMEOUT 后请求 orderly Stop，迟到 Ready/token completion 不会复活；超时子进程可能仍拥有、需等待真正退出。预 Ready 的异常退出 BACKEND_EXITED 不进入自动重启循环。Ready 后突发退出 BACKEND_CRASHED 按单调窗口限频重启，达到上限 BACKEND_RESTART_LIMIT 且不再自动启动。用户随后明确 Start 仍可尝试；停止与定时重启检查通过同一锁/修订协调。没有硬杀或“超时后已释放”的保证。

## 离线专项与边界

19 项专项检查：预算非法范围；并发Start仅一实例；私有token认证；正常Stop等待；手动重启fresh绑定；Stop超时有界并保留PID；停止撤销API入口；自然退出后重等；取消停止独立失败码；超时后手动重试再显式Stop；预Ready退出不重启；坏token页失败并持有child；迟到Ready/迟到token拒绝；异常退出fresh重启；连续crash限频；挂起自动重启被Stop取消；重复instance/token拒绝。

每个fixture cleanup只请求shutdown并等待自然退出（最长8秒）；失败抛明确“Owned fixture did not exit; never killed”。不使用生产Node项目，不连接设备，不Kill，没有窗口集成/真实数据/操作系统关机验收。专项证据 evidence/lifecycle-regression.txt；源码静态检查及独立候选构建由批次报告提供。root整合已将任意child输出替换为固定BACKEND_OUTPUT_OMITTED标记；公开诊断仍只使用白名单cocoon.diagnostics/1 DTO，不能把RecentLog或其他旧raw日志视为公开导出合同。

临时从主集成复制的 DeviceCommandBroker、DeviceTransportGate、BrokerFileReader、DeviceSession、BinaryProcessRunner、Diagnostics 仅为了编译，绝不纳入本批提交。主集成应只取本批 CocoonBackend / BackendLifecycleTests / 本文，再运行其最新依赖上的总回归；候选编译通过不代表这些依赖已在旧 main 中存在。

最终root整合补充：OnExit的Process.Dispose/退出工作计入exitCallbacks；无UI context时StateChanged在后台派发、异常隔离并计入eventCallbacks，StopAsync需两者排空才确认true。UI context的Post仍由UI线程消费，不要求后台owner等待UI消息循环。阻塞后台订阅者时按Stop预算返回false，可后续再确认；不在锁内同步触发用户回调。确定性屏障回归见BackendBoundaryTests。
