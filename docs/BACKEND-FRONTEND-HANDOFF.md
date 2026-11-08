# 后端与前端协作入口

2026-10-14 用户决定（Claude 实现）：[应用到掌机](features/device-apply.md)。用户要求能写回 Thor，并把方式与实现交给 Claude：采用「停用 Cocoon → 取当前快照 → 电脑上只改审阅字段 → 原地换回 → 启用」，root 只读读取也已由用户允许。后端仓库新增独立的 `lib/device/snapshotPatch.js`、`tools/snapshot-patch.cjs` 及测试，未改 `server.js`；打包清单需加入这两个文件。离线、模拟设备和实机沙箱演练通过，**尚未对真实 Cocoon 游戏库写入**。旧整库写入路径继续冻结。

2026-10-11 临时场景：[素材联动](features/native-studio-links.md)。LogoPreviewVariantsAsync 使用未暂存的 Hero/Logo crop/layout token 组合预览，重新验证归属/有效期/实际字节，零永久素材和草稿写入。Node 446/446、C# 原生专项 286/286。

2026-10-11 原件交付：[原件导出与播放会话](features/native-original-content.md)。新增完整字节读取/校验、无覆盖导出和复用播放器的拥有文件生命周期；视频探测与播放使用固定格式/协议限制。前端接入与实机验收仍待完成。

2026-10-11 视频批：[Hero 视频与海报](features/native-video.md)。原视频导入、实际解码、时间选帧、海报场景和视频草稿/变更审阅已接通；设备兼容性保留专用阻塞，播放/字节导出继续推进。

2026-10-11 素材联动：[暂存变体与位置重置](features/native-studio-links.md)。Logo 裁剪可进入场景，支持候选/暂存切换；元数据改回原值现在正确撤销对应草稿覆盖，保留其他修改。

2026-10-11 Logo 批：[叠加预览与位置审阅](features/native-logo-preview.md)。新增双屏 Logo 场景、九宫格吸附、缩放及位置逐字段确认；组合预览不保存成 Hero，旧编辑器几何明确标为近似。

2026-10-11 布局批：[素材布局后端](features/native-layout.md)。新增 LayoutAsync、焦点/适应模式/留边/背景和上下屏画布预设；新版本配方可独立重现，明确暂存保存预览原字节。精确 Cocoon 遮罩与 Logo 叠加继续推进。

2026-10-11 素材工作室接入：[原件查询和裁剪生命周期](features/native-asset-originals.md)。新增 OriginalsAsync；CropAsync/StageAsync 形状兼容，永久 Recipe/Variant 延后到明确暂存时保存。历史候选没有原件证据时返回空列表，前端不要伪称恢复原始编码。

2026-10-11 变更中心接入：[原生草稿审阅客户端](features/native-changes.md)。PreviewChangesAsync → 审阅 → ConfirmChangesAsync；ChangeReviewsAsync/ChangeStatusAsync 恢复记录，RevokeChangesAsync 撤销本地未应用意图。草稿保留，设备执行仍关闭。

2026-10-11 迁移接入：[受管理迁移与恢复](features/native-migration.md)。NativeEditorClient 提供 MigrationPlanAsync、ConfirmMigrationAsync、MigrationStatusAsync、MigrationRecordsAsync、RestoreMigrationAsync；Claude 可据此接审阅与明确确认流程。生产库未迁移，设备写入仍关闭。

2026-10-11 最新后端：[来源调度与原生批量刮削](features/native-scrape-jobs.md)。新增 NativeEditorClient 的创建/轮询/列表恢复/取消方法，供 Claude 接批量任务页面；继续沿用 Codex 后端、Claude 前端。原生预览已固化为组件，当前来源/批量候选见链接的清单和验收范围。

更新：2026-09-27。用户最新分工：**Codex 负责后端，Claude 负责前端**。覆盖此前把后端框架也指定给 Claude 的分工；界面、外壳外观与动画仍由 Claude 负责。

## 当前接续：离线后端整合批次

**先读 [BACKEND-COMPLETION.md](BACKEND-COMPLETION.md)**：12项可离线后端核心、第二批7–10号包、恢复查询、Cocoon纯代码组件打包均已交付；设备权限/控制sender/危险源删除/包11实验与发布仍保留门槛。下面第一至第四批“下一批/尚未交付”是历史记录，不再代表当前状态。

共享入口已包括 ProcessRunner/BinaryProcessRunner、跨程序DeviceCommandBroker与清理占用、AppCoordinator/ShutdownCoordinator、SettingsStore、MediaRepository/MediaLibraryService、GameMediaClient、原子CaptureService、图库事务、VideoExporter和NativeEditorClient。Claude直接按最新接入顺序绑定，不重新造第二套队列/媒体身份/草稿层。Cocoon受管理模式仍冻结旧写入，新的只读broker不等于开放legacy adb。

源码提交/测试日志/构建hash/包manifest由独立工作目录 `evidence/backend-final-delivery.json` 固定。没有替换用户EXE、导入生产库或进行真实设备操作。原来菜单和busy仍需前端迁移，不能把后台接口可用说成已完成窗口验收。Node源码分支独立保留，分发由打包工具导出可校验的EXE旁cocoon组件。

按前端阻塞排序的 12 项、已有计划清点、依赖和执行记录统一见 [BACKEND-WORKPLAN.md](BACKEND-WORKPLAN.md)。原 ROADMAP、CI0–CI6、RC1–RC3 和 Cocoon M0–M7 编号保留。

后端范围：设备/进程生命周期、资源租约、数据模型与索引、设置持久化、截图录屏/回放核心、导出引擎、实时控制协议、Cocoon 后端组件与事务、协议和离线测试。前端范围：窗口和网页、交互、外观渲染、前端状态绑定、界面性能与集成。两边通过已交付的类型化接口/HTTP 合同协作；未经协调不各自定义第二套后端实现。

保留当前成果，独立批次开发并回归。Claude 正在主目录做性能与图库集成，Codex 不覆盖其修改；后端新批次先在独立分支交付，再以可审阅提交集成。既有功能包继续复用。

## 当前后端第一批

- 工作目录：`..\cocoon-art-backend`
- 分支：`backend/cocoon-componentization`
- 源码快照提交 `3c82f7a`；组件实现提交 `56d2220`。此分支来自 Cocoon 仓库，不可直接 cherry-pick 到 Thor 仓库；先按独立 Node 组件接入，统一源码目录由后续整合批次处理。
- 基线：保留 Cocoon 原目录当前未提交/未跟踪的源码与文档；没有复制 config.json、用户数据库、素材、备份、ROM。
- 内容：独立数据根、组件启动入口、已认证的后端版本/能力接口、拥有进程的就绪/退出合同、隔离 HTTP 流程测试。
- 原项目 `..\cocoon-art` 和 Thor Capture 前端不改动。
- 已交付说明：该分支 `docs/BACKEND-COMPONENT.md`，协议 `cocoon.backend/1`；接口以实际测试结果和合同为准。
- 本批基线 280 项、本批全量 285/285 通过（Node 24.18.0、本机 HTTP/离线）；组件已可接入，尚未合并原项目或公开发布。
- 入口 `backend.cjs` 强制关闭设备访问和旧写入；需要绝对路径 `COCOON_DATA_ROOT`，默认随机空闲端口；通过 stdout 就绪事件、GET `/api/backend/info` 与 stdin 关闭合同接入。

实时控制与 Cocoon 合并方向见主项目 `evidence/performance-20260927/` 下的功能计划。本批不启用设备写入，不做真实数据迁移。测试与模拟证明分别报告，实机验收仍须按项目要求先获得用户同意。

## 当前后端第二批：设备与短命令核心

- 工作目录：`..\thor-capture-pkg2-triggers`（复用已空闲的旧任务包 worktree，目录名不表示本次任务归属）。
- 分支：`backend/device-core`；来源主分支 `3715db9`，包含 Claude 性能 A–D 和触控反馈成果。
- 实现提交 `cf28edc` 已于本批快进合入 Thor 主分支；当前主目录已有后端类型，无需重复 cherry-pick。本次保留了旧协作入口备份和在途任务文档修改，用户运行中的 EXE 没有替换。
- 代码：`src/App/Backend/` 的 DeviceSession / OperationGate / ProcessRunner / ReadOnlyAdbClient；前端接入见该分支 [backend-core.md](features/backend-core.md)。
- 验证：新核心 46 项专项通过；包含合成录制、声音、回放的全量 522/522 通过；独立目录 EXE 构建成功。证据 `evidence/backend-core-stream-tests.txt`、`evidence/backend-core-delivery.json`。
- 回连会取消旧任务，但租约等实际清理完成再释放；截图取景和输入观察可与录制共存，独立视频预览与录制先保守互斥。
- 只读 ADB adapter 使用固定枚举和来源 SessionId；重连迟到输出丢弃。短命令 runner 仅清理自己启动的 Process；日志不写参数、输出、环境变量或用户路径。
- 尚未接管当前 MainForm/static Adb、二进制/长视频作业或 Node 设备请求；本批不是完整 P1-2/P1-11 验收。没有发送真实设备命令，也没有启用实际控制或 Cocoon 写入。

Claude 可基于该分支接入设备会话与只读查询，逐个功能迁移现有 busy 标志。不要提前 Dispose 待清理租约，不同步等待后台任务，不把未知能力设为支持。媒体模型/设置持久化、能力 adapter、长作业与跨进程仲裁留给后端后续批次，避免前端各自再建一套实现。

## 当前后端第三批：现有库导入与空库响应

- 用户 12 项中的 1/3 已交付后端，生产库导入和按钮由前端审阅流程触发。任务/所有既有计划见 [BACKEND-WORKPLAN.md](BACKEND-WORKPLAN.md)，原生接口见 [library-import.md](features/library-import.md)。
- Node 组件提交 `3d4ad20`，仍在 `backend/cocoon-componentization`；HTTP 合同 `cocoon.library-import/1`，详细清单、错误码、备份/恢复边界见 `..\cocoon-art-backend\docs\LIBRARY-IMPORT.md`。
- Thor `backend/device-core` 基于原生游戏库 `049be68` 和计划清单 `a2730aa` 增加 `LibraryImportClient`，修复拥有后端的环境变量启动、补受限 POST/类型化 GET；只改后端连接器和新增类型，未改 Claude 的页面。
- 缺库 games/platforms 返回 200 空数组；库状态区分未导入/空/有库，坏库 422；异步导入清单、目标备份、来源复制校验、失败自动回退、进程崩溃后启动恢复、成功显式撤销。来源不写、配置/实例锁/ROM/凭据不复制，管理模式设备访问仍关闭。
- Node 294/294 全量通过；原生导入专项12项、包含原生游戏库/合成录屏/音频/回放的 Thor全量546/546通过；独立EXE构建成功。没有运行本批实机验收。
- 真实源库导入隔离目录后总770/可见732/待处理5，380文件、259,766,698字节；缓存素材可读，源SHA-256/时间保持不变。`thor-capture/artifacts/data/cocoon` 生产根未改，正在运行的用户EXE/Node未结束或替换。
- 证据在两工作目录 `evidence/backend-import-*`、Node `evidence/real-library-import-acceptance.json`；交付清单 `evidence/backend-import-delivery.json` 固定最终提交/构建/验证边界。

Claude 可接“选择来源→后台生成清单→审阅确认→执行/轮询→刷新/可选撤销”。请求超时不等于任务取消，不重复execute；查状态或重新连接。导入按钮不直接操作设备，不复用旧的整库写入功能。下批顺序4缩略图→5跨进程设备协调→2只读设备拉取，再12+6媒体组/关联；编辑合同与全局生命周期尚未交付。

## 当前后端第四批：本地素材缩略图与缓存

- 用户第4项后端已交付；原生入口见 [artwork-thumbnails.md](features/artwork-thumbnails.md)，Cocoon详尽HTTP合同在 `..\cocoon-art-backend\docs\ARTWORK-THUMBNAILS.md`。
- Node组件提交 `bb2b83b`，仍在独立 `backend/cocoon-componentization` 分支；不能直接cherry-pick到Thor。受管理backend重启后提供 `cocoon.thumbnail/1`，用户当前运行的旧服务未停止。
- Thor `backend/device-core` 新增 `ArtworkThumbnailClient` 与验证后的PNG/304结果类型，CocoonBackend复用现有FFmpeg发现配置、仅后台认证请求。没有改Claude的页面/位图缓存或外壳渲染。
- 两项解码并发、64待处理、16MiB内存/256MiB磁盘LRU；内容/尺寸/fit/版本缓存、源变化失效、透明Logo、contain/cover、PNG/JPEG/WebP/GIF首帧。源32MiB/16MP、输出1024边界、5秒解码期限；JPEG等无可用外部codec时明确503，ICO等415。无尺寸原图入口保持兼容，缩略图不触发ADB/下载。
- Node305/305，无跳过；新增原生13项、库相关101/101，Thor含合成流/声音/回放559/559通过；独立目录构建成功。坏缓存/ETag、CRC、源变更/mtime恢复、透明图、队列/取消/停止、真实外部codec与拥有子进程超时均有离线检查。
- 隔离实库48封面样本（36PNG/12JPEG）传输body减少90.39%，条件请求48/48为304、零新增解码；源图和库hash/mtime不变。不是页面加载或实时视频性能验收，也不是整个游戏库的大规模指标。
- 证据Node `evidence/thumbnail-all-tests.txt`、`thumbnail-real-library.json`；Thor `evidence/thumbnail-native-tests.txt`、`thumbnail-stream-tests.txt`、`thumbnail-build.txt`；最终提交/构建与边界固定于 `thumbnail-delivery.json`。原Cocoon和生产根未改，用户EXE/Node未结束或替换，无设备或公开发布操作。

Claude可按可见项+少量预读接 `GetAsync/GetStagedAsync`，最多4个客户端请求并发；304保留该尺寸键的已有Bitmap，200在后台解码并移交所有权，过期generation丢弃并Dispose。切页取消、DPI/尺寸换键、导入后刷新；404占位、415格式提示、503解码器/队列等准确提示，不自动解除设备冻结。下批5跨程序协调是2只读拉取的前提，媒体组/关联、生命周期/诊断、原生编辑仍保留。

## Claude 本批可继续的工作

继续原生预览/图库性能修复和前端整体设计；游戏库可先设计模块入口、详情和媒体关联界面。后端组件交付前不要通过直接读取 config.json 或 Cocoon SQLite 建立另一套数据层，不自行解除 legacy 写入冻结。

现在可按 `..\cocoon-art-backend\docs\BACKEND-COMPONENT.md` 的版本合同接入健康状态、本地游戏库、复制导入和缩略图。前端只能管理自己启动的 Node 子进程；认证仍走本次入口页 token，不移除 Origin/Host 检查。设备会话/资源租约由上面的第二批提供，Node 设备请求尚未接入同一仲裁；“服务正常启动”不等于“设备写入可用”。
