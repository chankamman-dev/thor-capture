# 后端工作总入口：前端阻塞与既有计划对应

更新：2026-09-27。用户指定 Codex 负责后端、Claude 负责前端。保留原任务编号；本页汇总依赖和交付顺序，不替代各计划的验收标准。优先级按用户本次的 12 项清单执行。当前接续入口为 [BACKEND-COMPLETION.md](BACKEND-COMPLETION.md)，下面旧批次记录仅作追溯。

## 已有文档清点

| 文档 | 仍需保留的任务/约束 |
|---|---|
| [ROADMAP.md](ROADMAP.md) | P0–P7；框架、采集、媒体、图库、桌面体验和公开测试版门槛 |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 当前实现与目标分层、媒体模型、依赖边界；不能把未接入接口标为已启用 |
| [tasks/README.md](tasks/README.md) 及 01–11 任务包 | 已有功能核心和交付依赖继续复用；旧 Track A 后端归属由最新用户分工覆盖 |
| [features/TRACK-B-HANDOFF.md](features/TRACK-B-HANDOFF.md) | 已交付包的接口、验证和未接入事项，避免重复开发 |
| [BACKEND-FRONTEND-HANDOFF.md](BACKEND-FRONTEND-HANDOFF.md) | 当前分支/目录、后端分批合同及证据 |
| [LIBRARY-FRONTEND.md](LIBRARY-FRONTEND.md) | 原生游戏库第一期已完成；素材编辑、元数据、ChangeSet、媒体关联与导入的前端后续 |
| `evidence/performance-20260927/Cocoon-Art合并计划.md` | CI0–CI6：组件隔离、原生界面、媒体关联、共享设备、写入转正与统一分发 |
| 同目录 `实时预览控制设备功能计划.md` | RC1 双屏触摸/导航、RC2 键盘/外壳按键、RC3 手柄；固定协议、释放、队列和断线边界 |
| 同目录 `性能诊断与修复计划.md` / `修复结果.md` | 已完成前端 A–D，性能目标、长运行、真实延迟和大窗口预算仍需验证 |
| [PREVIEW-TUNING.md](PREVIEW-TUNING.md)、[RECORDING-PREVIEW.md](RECORDING-PREVIEW.md)、[RECORDING-AUDIO.md](RECORDING-AUDIO.md) | 预览参数、录屏期间取景、声音及共享后端的现状和限制 |
| [OPEN-SOURCE-RELEASE.md](OPEN-SOURCE-RELEASE.md) 及 `docs/release/` | 许可、来源、隐私审计、版本、CI 与实际发布门槛 |
| Cocoon `docs/DEVELOPMENT_PLAN_V2.md` | M0–M7：安全、领域迁移、Application/ChangeSet、Adapter/Helper、素材工作室、Provider、文件管理、无线/Beta |
| Cocoon `docs/PRODUCT_ARCHITECTURE_V2.md` / `ASSET_AND_RECONCILIATION_V2.md` | 领域身份、证据、CAS/Recipe、目录重连及决定日志 |
| Cocoon `docs/MIGRATION_V1_TO_V2.md` | 扩展数据库结构升级的独立合同；本地库复制导入不等于执行 v1→v2 迁移 |
| Cocoon `docs/HANDOFF.md` / `WORKLOG.md` | 当前代码与历史验收；旧 PID、测试数和设备状态仅作追溯，不能当成当前事实 |
| Cocoon `docs/SECURITY_AND_DATA_SAFETY.md` / `HTTP_ROUTE_AND_WRITE_CHAIN_AUDIT_2026-08-29.md` | 鉴权、来源、下载、冻结旧整库写入、快照、备份和路由写入边界 |
| Cocoon `docs/COCOON_ADAPTER_BETA_304_CANDIDATE.md` / `HELPER_PROTOCOL.md` | 候选设备 profile 与 helper 协议，离线通过不能替代实机写入验收 |
| Cocoon `docs/BACKEND-COMPONENT.md` | `cocoon.backend/1`、独立数据根、启动/关闭、认证及管理模式禁用设备能力 |

Cocoon 当前组件工作目录为 `..\cocoon-art-backend`，原项目为 `..\cocoon-art`。V1 的 PRD/DEVELOPMENT_PLAN/ARCHITECTURE/API 文档保留为兼容参考；V2 与当前代码/最新交接优先。合并计划早期推荐的 WebView2 已被用户的“原生重写、分期”选择覆盖；不重新引入网页容器依赖。

## 用户 12 项清单与交付顺序

| 用户项 | 对应原计划 | 本轮核对状态 | 依赖与验收 |
|---|---|---|---|
| 1 现有 Cocoon 数据导入 | CI1/CI2，迁移合同的隔离/备份边界 | 后端已交付；前端按钮待接入，生产库未导入 | 选择来源→清单→备份目标→复制→校验→发布；源不写；失败自动回退，成功可撤销；实库隔离验收770/732/5，见 [接入说明](features/library-import.md) |
| 2 只读设备拉取 | CI4，Cocoon M3 Adapter | 管理模式 broker/清单接口与原生客户端已交付；真实私有库权限未验收 | 先绑定5；固定serial/session，读取DB/WAL两次校验，再审阅导入；旧pull与写入继续冻结 |
| 3 空库明确响应 | CI1/CI2 | 后端已交付 | `/api/games`、平台缺库200空数组；`/api/backend/library` 区分未导入/空/有库；损坏422 LIBRARY_INVALID、权限500 LIBRARY_UNREADABLE |
| 4 缩略图与缓存 | CI2，P5-2 | 后端已交付；前端页面待接入 | `cocoon.thumbnail/1`、原生异步客户端；PNG/JPEG/WebP/GIF首帧、透明Logo、尺寸/像素/并发限制、ETag/304、有界LRU、原图不变与源变更失效，见 [接入说明](features/artwork-thumbnails.md) |
| 5 跨程序设备协调 | CI4，P1-2/P1-11 | 已交付私有broker、命令队列及跨进程清理占用 | Node不执行ADB；固定会话，未取租约零调用，清理未确认继续占用；只协调采用本合同的程序，不控制外部用户ADB |
| 6 游戏与媒体组关联 | CI3，P1-10/P5-4 | 已交付 GameMediaClient 与真实仓库CAS关联/解除/反查 | 持久library UUID + canonical native ID，库/实例变化拒绝旧响应；原片不改，前端详情区待接 |
| 7 生命周期与脱敏诊断 | CI1，P1-4/P1-11 | 已交付拥有进程重启、关闭协调器和两侧诊断接口 | Stop超时保留所有权，可再确认；异常重启换token/instance并限频；诊断仅固定白名单，不导出raw日志 |
| 8 原生编辑稳定接口 | CI2，Cocoon M2/M4 | 已交付Facade、持久草稿、候选CAS/裁剪/上传与类型化客户端 | 审阅后暂存/撤销/字段确认，异库草稿明确归档；每次校验库/实例/revision，设备apply仍禁用 |
| 9 进程/ADB 调用统一 | P1-11/P1-3 | 文本/二进制runner及旧短ADB适配已交付；旧长流保留专属owner | 有界双流/超时/取消/清理确认；旧长流启动按显式serial排队，生命周期仍由其owner；旧同步UI入口需逐步迁移 |
| 10 全局状态与互斥 | P1-2 | 已交付 AppCoordinator/AppStateSnapshot/ShutdownCoordinator | 与既有registry/gate共用，超时结果不等于资源释放；Claude迁移busy和服务注册 |
| 11 图库目录监控 | P5-1 | 已交付 GalleryMonitor 与 MediaLibraryService | 实际watcher/半文件/溢出模拟/周期对账/目录generation；后台更新稳定组，选择/滚动由UI按UUID保持 |
| 12 统一媒体模型 | P1-10/P1-5 | 已交付真实 MediaRepository 与原子同组发布 | 稳定组/轨UUID、checksum备份/CAS、旧名一次适配、缺轨/改名/重绑；截图先预留组再发布，watcher不拆组 |

顺序：**1+3 → 4 → 5 → 2 → 12+6 → 7+8 → 9+10+11 的分批迁移**。9/10 的公共核心已存在，后续按 5/12 的需要补齐；不重建第二套会话/租约。生命周期最低保护随每批接口同时做，不推迟到所有功能完成后。

每批必须交付可接入合同、故障测试、验证证据、明确未完成项及前端阻塞变化。实机测试仍需要用户同意；只读离线库导入与合成故障测试可继续进行。远程控制 RC1–RC3、CI5 写入转正、Cocoon M5–M7 的来源/文件/无线功能和公开测试版发布门槛仍保留，未被这 12 项删除。

## 执行记录

- 2026-09-27 最终：12项清单的可离线后端核心及后续功能包已集成，接续入口为[后端当前交付](BACKEND-COMPLETION.md)。最终Thor含流1147/1147、Node368/368、打包工具17/17、纯组件HTTP/关闭8项和真实C# portable启动17项通过；独立候选编译成功。生产源冻结后只补文档，用户程序和生产库未替换。Claude接入、真实设备/控制协议、包11实验选择、干净机器/许可/公开发布门槛保持待完成；不是整个公开测试版已验收。
- 2026-09-27：从磁盘重新清点计划、原生游戏库第一期、两个后端分支和原库布局。开始 1/3 的隔离数据导入与空库合同；真实数据执行与自动化 fixture 分开报告。
- 2026-09-27：1/3 后端完成，Node 294/294，Thor全量离线546/546；真实原库仅复制到隔离验收目录，来源保持不变，生产根未改。原生导入/撤销接口可交 Claude 接入；下批按4→5继续，不能提前开放设备拉取。公开测试版门槛未完成。
- 2026-09-27：4后端完成，Node305/305、原生库相关101/101、Thor全量含流559/559，独立EXE构建成功；48张隔离实库封面样本图片body减少90.39%，重复请求48/48为304、零新增解码，源图/库不变。交付合同和错误码已供Claude接入；生产库、用户运行程序和页面未替换。下一批按5跨进程协调→2只读拉取继续，设备访问与写入仍冻结。
