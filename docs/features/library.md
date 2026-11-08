# 9 号包：组级管理、批量注释与只读导入

此批提供后端核心，依赖真实 `MediaRepository`，没有接到主窗口，不操作 Thor。删除与跨目录移动中的源删除目前显式返回 `Unsupported / UNSUPPORTED_SAFE_RELEASE`；首发前隐藏这些入口。下面 API 均是 C# 5 / 系统框架实现，没有新包依赖。

## 同目录改名与恢复

`GroupOperations.PlanRenameAsync(repository, groupId, stem, token)` 在后台生成不可变 `GroupOperationPlan`，预览 `Moves / Conflicts / RequiresConfirmation`。输出 `<stem>-upper/lower/trackN.<ext>`；包含真实组内所有原片，以及存在的固定 `.thor-meta.txt`、`.input.csv` 和同 stem WAV。不猜测任意派生文件的关系；其他派生物需未来有显式引用模型后才能安全纳入。已 Missing/Changed、重解析、只读、同名、名称无效和哈希改变都构成计划冲突。不能将目录或任意用户提供的路径作为操作目标。

`ExecuteAsync(repository, plan, confirmed, token, progress)` 需要调用者把完整计划展示后确认，后台获取真实仓库锁，以计划中的 group revision 比较当前元数据。所有原片与侧车预先校验 SHA/大小并持有拒绝写入的文件句柄后才逐步改名。每步前后 flush 私有恢复日志，禁止覆盖目标；进度是处理步数，事件在后台线程，禁止同步回调同一仓库。等待仓库锁有 3 秒上限，取消在每个文件及哈希块检查。中途失败保留已完成改名及其余原片，通过 `TransactionId` 恢复，不能把取消当成全部回滚。

`RecoverAsync(repository, transactionId, rollback, confirmed, token)` 可继续或反向恢复。日志带原始用户元数据快照；重启刷新只改变已知路径/状态时允许恢复，真实用户收藏、标签、游戏关联变更后返回 `REVISION_CONFLICT`，不能绕过元数据 CAS。改名保留组/轨 UUID，完成后 CAS 保存当前注释。checksum 损坏、两边都存在/缺失、内容变化时停止且保留文件。恢复日志 schema 为 `thor.group-rename/1`，本期为新格式，尚无旧格式迁移需求。

## 跨目录安全复制

`PlanCopyAsync(source, destination, groupId, token)` / `ExecuteCopyAsync(source, destination, plan, confirmed, token, progress)` 复制整个已登记组到另一个已初始化的真实仓库。实现复制与原片保留，不声称完成跨卷移动或回收站转移。持有目标仓库锁并先持久预留成对 Missing 轨道与新 UUID，随后在源仓库持有锁校验元数据 revision 和文件指纹，每个文件临时写入、flush、SHA 校验、no-overwrite 原子发布。目标 watcher 刷新匹配预留轨，不会把部分成功拆成另一个单轨组。

文件步日志在源仓库中存储（`thor.group-copy/1`）；目标组 `LegacyKey=copy:<transactionId>`。`RecoverCopyAsync(source, destination, transactionId, confirmed, token)` 可跨实例重读固定计划恢复，原片一直保留。完成复制后才标记 committed；重复恢复保持目标 UUID 和后来修改的注释。局部取消返回目标仓库的实际部分组，Only Present 对应日志确认已发布的主轨。异常未完成的临时文件保留为 `.interrupted-<id>`，不冒充成功。若源在预留后预检失败，目标可能保留全 Missing 的事务占位组，需要恢复入口审阅；当前未提供自动清理入口。目标已关联其他事务的同名组不会被复用。

双仓库嵌套锁均有 3 秒争用上限，反向同时复制可能返回 Busy，应用应排队并显示重试；没有无限等待。目标媒体丢失/内容变化、恢复日志受损或源 revision 改变，停止并保留文件。日志含本地目录及引用，属于私有数据，禁止纳入公开诊断。

## 收藏、标签与游戏分组

`MediaTags.ApplyBatchAsync(IMediaRepository, IEnumerable<AnnotationRequest>, token)` 最多 200 个不可变 group UUID + expected revision 请求，顺序调用真实 `SetAnnotationsAsync`。每组独立 CAS 和备份，不保证整个批次事务原子性；返回逐组 `MediaRepositoryResult`，取消保留已完成结果。标签修剪和大小写去重由仓库统一处理。游戏分组使用已有 immutable `MediaSnapshot.ForGame(GameReference)`，不要用可变文件名或别名作为媒体身份。

## 只读 provider 导入

`IReadOnlyMediaProvider` 只暴露固定 `InstanceId`、`ListAsync(explicitDirectory, token)`、`OpenReadAsync(selectedDescriptor, offset, token)`；没有设备写入、删除或扫描根目录 API。本包只交付离线 fixture provider 合同，没有未经验证的实机默认目录、ADB 命令或设备 hash 计算。Thor provider 应由拥有设备会话的集成层另行实现并验收。

`ScanAsync(provider, selectedDirectories, token)` 仅调用用户明确选中的 1–8 个 `/sdcard/...` 平级目录，不递归，不接受 `..`、通配符、控制符；每目录 ≤2000 条、总计 ≤5000 条。每个 descriptor 需要长度、UTC 修改时间、SHA-256 和合法支持扩展的叶名称。固定 provider 发生重新绑定时拒绝结果。

`Plan(scan, selectedItem, repository, targetName)` 只接受成功扫描中的真实选中对象，冻结 provider/仓库身份；`ImportAsync(...confirmed...)` 与 `ResumeAsync(provider, repository, transactionId, confirmed, token)` 使用 `.partial`、每块 flush + 日志和最终大小/SHA 校验。断线/取消保留已下载字节并从记录偏移恢复；provider 身份变更不能静默重用旧计划。provider 列表、打开、每块读取均有 3 秒预算，迟到 stream 会被专属 continuation 释放，忽略取消的外部 provider task 本身无法被强制结束。固定 SHA 不匹配不发布。导入原片的屏幕为 Unknown，元数据 `device-import`，没有序列号；设备源始终只读且保留。同名与已有 SHA 拒绝重复副本。

## 前端集成与验收边界

前端先选择组并异步生成计划；展示每个文件的源/目标与所有冲突；确认之后执行并显示逐步状态。失败弹窗必须保留事务 ID、继续/回滚入口，不仅显示「失败」。收藏/标签批量编辑要保留逐组错误并刷新选择，用 UUID 重新定位；游戏分组显示未知项并提供人工关联。导入向导先选择目录、扫描、选择素材、预览目标/重复判定，再确认下载；不要自动选择私密目录或默认开启源删除。

35 项真实临时仓库专项通过（总计 99 PASS，包含 64 核心）：CAS/标签去重、双屏固定侧车计划、确认、改名、原片/UUID/标签保留、完成回滚/幂等、中断重启恢复、目标冲突、源 hash/只读/占用、用户修改后恢复拒绝、损坏日志、安全复制局部取消/成对预留/恢复/重复、危险操作 typed Unsupported、只读选定目录、中文文件名、确认/取消续传/偏移/来源、重复 hash/provider 重绑定、错误内容、3 秒 provider 超时和恢复。没有真实设备、跨卷回收站、生产图库或前端验收。自动恢复选择器、原片源删除、非固定派生图关系和真实 ADB provider 均未完成。

## 私有日志路径与临时所有权修复

`LibraryPathGuard` 从卷根按顺序检查每一个现存祖先，拒绝 ReparsePoint 后才访问下一级；不是先访问链接后事后检查目标。`CopyPath / JournalPath` 与导入日志入口在读取、创建目录、打开锁、写日志之前检查私有根、transactions、主日志、`.bak`、`.lock`；原子替换前再次检查。导入的媒体根、目标、partial 和元数据侧车也检查祖先及叶节点。若路径被 junction/symlink 引向其他目录，操作返回固定冲突，不尝试写入外部目标。后台前置校验也覆盖进入仓库 Run 之前的边界，不依赖仓库核心后来才发现链接。

三个日志写入器统一使用 `AtomicJournal`。只有 FileMode.CreateNew 成功打开本次临时文件后才设置 own；构造/打开失败时 finally 不删除已存在的临时文件。目标与备份不接受链接；清理自身临时文件前还会再次拒绝链接。路径检查防止可见的错误配置/链接重定向，不声称抵御同一用户在检查与原生系统调用之间恶意交换目录的竞争。

`LibraryPathSafetyTests` 20 项真实临时 Windows junction 回归（总计 84 PASS 含核心）：transactions 链接对改名/复制/导入/三类恢复的拒绝；主日志、备份、锁链接；私有根上层 junction；外部 sentinel 文件的字节与目录清单不变（零外部写入）；未泄漏日志临时文件；强制 CreateNew 碰撞保留原临时文件。junction 创建与非递归移除均限定在测试 fixture 根，未处理生产目录。导入来源元数据已在本包用固定 UTF-8 字段和 AtomicJournal 的 no-overwrite 模式写入，不再调用 CaptureMetadata.Save；碰撞保留原侧车，只清理本次拥有的临时文件。RecoveryCatalog 读取也逐级检查私有根祖先；真实祖先 junction 下返回零外部日志条目。仓库核心和原 CaptureMetadata.Save 未修改。
