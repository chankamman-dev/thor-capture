# 后端恢复事务只读目录

启动时调用 `RecoveryCatalog.ScanAsync(realMediaRepository, expectedRepositoryId, cancellationToken)`。固定 schema 为 `thor.recovery-catalog/1`，返回 immutable `RecoveryCatalogResult`；只返回本仓库 UUID、事务 UUID、类型/阶段/方向 enum、固定结果码、计数。Entry 可选 PeerRepositoryId / ProviderId 也是校验后的 UUID，没有任意日志、路径、文件名、远端目录、序列号、自由文本或异常消息。

枚举本仓库 `DataRoot/transactions` 第一层的四个固定扩展 `.thor-publish / .thor-rename / .thor-copy / .thor-import`。不递归、不读媒体、另一仓库或设备；不调用恢复操作、provider、写锁、迁移、元数据更新，也不创建任何文件/目录。绑定文件必须与实际已初始化仓库和预期 UUID 一致，前端应先完成仓库初始化。备份及临时日志不会被当成主事务恢复；若缺少绑定或主日志，需要应用单独呈现固定错误并审阅，枚举不会自动修复。

最多检查 200 个目录项（包含忽略的临时/备份文件），达到上限返回 `Partial / RECOVERY_FILE_LIMIT` 与 Truncated，不声称发现全部事务。每个日志严格 UTF-8，最多 1 MiB；import/绑定最多 16 KiB，使用受限流读取避免检查长度后文件增长导致无限分配。检验 checksum、schema、事务/仓库 UUID、阶段白名单、组快照、轨道映射、唯一固定名称、合法派生关系和数量/字节边界。损坏主日志以 Corrupt + `RECOVERY_LOG_INVALID` 返回；占用/无读取权限为 Unreadable + `RECOVERY_LOG_UNREADABLE`，两者 Directions=None。不合法事务文件名仅增加 InvalidNames，绝不回显私密文件名。整个异步 API 有 5 秒返回预算；取消返回 `RECOVERY_CANCELLED`，时间预算耗尽为 `RECOVERY_SCAN_TIMEOUT`。原生文件系统调用可能迟到，后台只读任务会在返回后取消并观察结果，不触发任何迟到恢复动作。

publication 未完成只支持 Continue；rename 支持 Continue / Rollback；copy 支持 Continue 且需前端用户另外选择 PeerRepositoryId 对应的目标仓库；import 支持 Continue 且需同 ProviderId 的只读会话。committed 与 rolled-back 经完整解析后省略，计入 CompletedCount。枚举提供支持尝试的方向，未在启动时重新哈希全部原片或保证其可恢复；所有方向 `RequiresConfirmation=true`、`RequiresRevalidation=true`，实际操作仍必须走原恢复 API 的原片指纹、元数据 CAS、目标范围及 no-overwrite 校验。不能根据目录结果直接执行 arbitrary command、自动删日志、自动删除源文件或绑定新设备会话。

前端用 enum 做中文文案，仅展示 UUID 缩写、类型/阶段与固定原因；对方向为 None、Truncated、Partial 或身份冲突明确禁用恢复按钮并提供重新读取。用户选定 Continue / Rollback 后再确认，使用事务 UUID 调用既有 `RecoverPublicationAsync / GroupOperations.RecoverAsync / RecoverCopyAsync / DeviceImport.ResumeAsync`；不要拼接任何日志里的路径。此目录是本机应用服务，不是公开诊断/HTTP 导出；尚未增加公网接口。checksum 只防意外损坏，不构成同一用户恶意重写本地数据时的授权边界。

17 项专项真实临时仓库验证（总计 81 PASS，含 64 核心）：空仓库无写入、旧身份与预取消、四类真实中断日志、固定方向与 UUID 前置条件、严格只读的字节/快照比较、完成记录过滤、schema 不包含私密字符串字段、immutable 快照、占用/损坏/超大/私密文件名、合法 checksum 但外来仓库和未知阶段拒绝、200 项发现上限。没有真实设备、生产图库或前端恢复对话框验收。

链接修复：目录发现之前以及每个受限文件读取之前复用 `LibraryPathGuard.NoLinks`，从卷根逐级拒绝所有现存祖先/叶节点 ReparsePoint，不仅检查 DataRoot 自身。新增真实祖先 junction 回归中，外部存在绑定和日志但返回 `RECOVERY_ROOT_INVALID`、Entries/InspectedCount 均为零，外部日志字节及目录不变。此回归包含在 20 项 `LibraryPathSafetyTests` 中。
