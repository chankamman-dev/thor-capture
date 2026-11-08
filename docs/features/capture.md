# 截图批次与有限连拍后端

`ThorCapture.Features.Capture` 提供 `CaptureRequest`、`CaptureService`、`IScreenCaptureSource`、`AdbScreenSource`、`BurstPlan`、`BurstRun`。请求固定 Session/DisplayTarget、输出目录、超时、字节预算、并发意图及可选连拍 UUID。不可读取新的全局设备选择；显示目标必须属于该 session。

`CaptureService.RunAsync` 先取 Capture 租约，失败零设备调用。独立两屏各自有超时；只有 provider 明确支持并发才并行，普通 ADB provider 当前保守返回 `sequential-fallback`。正式 provider 只执行固定显式 serial/物理 display 的 PNG 读取，不写设备临时文件；命名保留旧双屏配对规则。PNG 尺寸/像素/每轨32MiB与请求总预算预检后，交真实 `MediaRepository.PublishGroupAsync` 在仓库锁内预留稳定组身份、暂存、Flush、不覆盖发布。请求目录必须与仓库根相同。目录监控不能在两轨发布之间抢先拆组。单侧成功建立一轨组；发布期间取消/写失败时保留真实已落地轨道与恢复日志，另一个轨道可为 Missing。`CAPTURE_PARTIAL`、`CAPTURE_PUBLICATION_PENDING`、`CAPTURE_METADATA_PENDING` 不冒充完整成功；恢复入口见 [media-publication.md](media-publication.md)。

每轨返回电脑端单调 `Issued/Completed`、状态、原片路径/大小；额外 `thor.capture-batch/1` JSON 保存批次/连拍/稳定媒体组身份。电脑命令时间不能称作设备采样同步。可注入现有只读活动查询及其关闭确认，在 request `ReadForeground` 中启用；查询失败不阻止截图，`.thor-meta.txt` 会写明确状态。默认不查询以避免额外延迟。

取消后不接受迟到 bytes；取消前已发布的文件保留并登记为部分成果。任何 provider 未确认清理时保留设备租约，`StopAsync(timeout)` 返回 false；只有随后真实清理确认才释放。停止后该 service/source 不再用于新截图。前端不得把 false 解释为已关闭，也不能同步等待后台方法。

`BurstPlan` 有最多500组、倒计时、最小50ms间隔、总预算、单组估算。超过估算总量先拒绝；运行中将剩余预算传入每张 CaptureRequest。每张独立批次共享 BurstId。慢截图顺延下一槽位且不积压队列；取消/断开/失败/预算不足停止并保留已完成结果。调用方在确认页显示估算并检查目标可用空间；实际写满返回失败。`Progress` 在后台线程触发；单个 run 只能执行一次。

前端接入：创建当前根 MediaRepository 与 OperationGate，将 `BurstRun` 委托绑定到 CaptureService，将收到的 burstId/剩余预算传入 CaptureRequest；界面显示每屏结果、顺延策略、停止与已保存组数。新图通知接到 MediaLibraryService，操作状态接 AppCoordinator，勿重新扫描 UI 或继续依赖文件名作为永久身份。

离线测试 `CaptureFeatureTests` 覆盖并发/回退、一侧拒绝、双屏配对、稳定组、真实目录监控不拆组、前台包归属/查询失败、时序、字节预算、迟到取消、互斥零调用、保留租约/重复关闭、连拍次数/估算/取消与故障停止。无实机并发能力、权限、50组稳定性或端到端延迟证据；界面由 Claude 接入。

最终故障审查补充：SaveNew仅删除本次CreateNew成功创建的临时文件；仓库事务目录/祖先/主日志/备份在写入前拒绝junction。Stop取消和provider关闭在后台执行，阻塞取消回调计入清理；超时返回false，重试等实际排空后才放租约。provider异常没有CleanupComplete确认时保持占用，不把异常返回当作资源已释放。BackendBoundaryTests以真实临时junction及阻塞事件验证，无仓库外写入。
