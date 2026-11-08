# 后端当前交付与前端接续

2026-10-11 临时场景：[素材联动](features/native-studio-links.md)。LogoPreviewVariantsAsync 使用未暂存的 Hero/Logo crop/layout token 组合预览，重新验证归属/有效期/实际字节，零永久素材和草稿写入。Node 446/446、C# 原生专项 286/286。

2026-10-11 原件交付：[原件导出与播放会话](features/native-original-content.md)。新增完整字节读取/校验、无覆盖导出和复用播放器的拥有文件生命周期；视频探测与播放使用固定格式/协议限制。前端接入与实机验收仍待完成。

2026-10-11 视频批：[Hero 视频与海报](features/native-video.md)。原视频导入、实际解码、时间选帧、海报场景和视频草稿/变更审阅已接通；设备兼容性保留专用阻塞，播放/字节导出继续推进。

2026-10-11 素材联动：[暂存变体与位置重置](features/native-studio-links.md)。Logo 裁剪可进入场景，支持候选/暂存切换；元数据改回原值现在正确撤销对应草稿覆盖，保留其他修改。

2026-10-11 Logo 批：[叠加预览与位置审阅](features/native-logo-preview.md)。新增双屏 Logo 场景、九宫格吸附、缩放及位置逐字段确认；组合预览不保存成 Hero，旧编辑器几何明确标为近似。

2026-10-11 布局批：[素材布局后端](features/native-layout.md)。新增 LayoutAsync、焦点/适应模式/留边/背景和上下屏画布预设；新版本配方可独立重现，明确暂存保存预览原字节。精确 Cocoon 遮罩与 Logo 叠加继续推进。

2026-10-11 素材原件批：[原始素材与临时裁剪](features/native-asset-originals.md)。原始编码和解码候选分开保存；原件查询/预览已接通。裁剪预览不再直接创建永久配方，明确暂存时才保存，保护原件与草稿。

2026-10-11 原生变更批：[草稿到变更中心](features/native-changes.md)。原生元数据和 CAS 草稿可生成审阅计划、明确确认、持久记录、重启恢复与撤销意图，C# 客户端已接通。DevicePlan 保留能力阻塞，未执行掌机写入。

2026-10-11 最新批：[受管理迁移与恢复](features/native-migration.md)。副本预演、确认迁移、持久记录、重复提交、中断恢复与还原已有后端及 C# 5 客户端。仅隔离库验证，原生草稿到 ChangeSet 和前端接入继续推进。

2026-10-11 后续批：[来源调度与原生批量刮削](features/native-scrape-jobs.md)。异步任务支持创建/状态/列表恢复/取消，凭据隔离缓存、来源有界请求和 C# 5 客户端已实现；任务绑定当前库并保留明确复核步骤。当前 Node 开发分支为 backend/cocoon-completion，Thor 为 backend/thumbnail-session-safety。全功能状态以 Cocoon 的 docs/COMPLETION-TRACKER.md 为准。

2026-10-11 续批：[缩略图会话隔离与原生编辑预览](features/thumbnail-session-safety.md)。新增绑定 editor/library 的像素预览客户端，补齐迟到响应、304、读取中取消和总期限校验。主目录的在途前端文件保持原样，其中 MetadataDialog.cs 的 C# 5 编译错误独立记录；本批候选及离线验证不包含未提交界面。

更新：2026-09-27。Codex负责后端，Claude负责界面、外壳渲染和前端接入。此页是最新入口；[工作计划](BACKEND-WORKPLAN.md)保留全部旧任务编号和文档清点，旧交付页中的“未实现/等待A1”只描述当时状态。

本轮已完成12项清单的可离线后端核心，并补齐第二批功能包、恢复查询与组件打包。**代码存在、整合回归、前端可用、实机验收、公开发布是不同阶段。** 当前用户运行程序没有替换，生产Cocoon库没有导入，没有发送真实ADB命令。缺少硬件、协议证据或明确实验选择的功能继续保守禁用；不把未验证能力写为Supported。

## 两个源码域

- Thor：主目录 `thor-capture`；后端独立集成目录 `thor-capture-pkg2-triggers`、分支 `backend/device-core`。所有新C#文件递归编译，保持C#5/系统Framework，不增加NuGet。
- Cocoon：`cocoon-art-backend`、分支 `backend/cocoon-componentization`。独立Node组件，不能把其Git提交直接cherry-pick到Thor。原 `cocoon-art` 不改；不读它的config或默认打开原库。
- 分发选择EXE旁 `cocoon/backend.cjs`；开发期仍可显式配置BackendDir。Node需要外置受支持运行时（package engines≥22），ADB/FFmpeg按现有发现配置外置；源码包不是附带全部运行时的安装器。

## 后端接入顺序

1. **共有设备与作业**：建立唯一 `DeviceSessionRegistry` / `OperationGate`，共享给 `AppCoordinator`、只读adapter、broker、截图、控制。物理display ID来自当前会话。设备更换/授权降级退休旧会话，不能把旧任务改指新设备。后端事件均后台线程，前端marshal后再核对generation/revision。
2. **关闭**：把真实owner的有界 `StopAsync` 注册到 `ShutdownCoordinator`。true才是清理确认；Pending/false保留owner与租约。作业Cancelled/TimedOut可早于底层停止，Draining期间不能解锁设备或重启第二个进程。再次Stop可以重试失败ack。
3. **本地库**：先接 `LibraryImportClient` 清单/确认/轮询/恢复/撤销；空库200空列表与坏库422区分。然后接缩略图条件请求，304沿用已有尺寸缓存。生产数据导入必须由UI审阅清单后触发，不能启动即偷偷复制原库。
4. **只读设备库**：启动 `DeviceCommandBroker(registry,gate,new BrokerFileReader(adbPath))`，拥有Cocoon Ready后 `BindDeviceBrokerAsync(broker.Address)`。`DevicePullClient.StartPlanAsync(session,token)`→状态轮询→展示原有导入清单→明确确认。Node不接触ADB，凭据只经拥有child的stdin。私有DB读取可能被Android拒绝，拒绝时不提权、不停Cocoon、不写设备临时文件。原生草稿或旧pending非空先阻止拉取。
5. **稳定图库**：`MediaLibraryService.SwitchAsync(mediaRoot,repositoryRoot,token)`；不要再让UI扫描/解码整库。`MediaRepository`持久组/轨UUID、checksum/备份/CAS，文件名只在首次旧图库适配使用。选择、收藏、标签、关联、导出输入与恢复必须按组UUID，不按快照下标。
6. **游戏与媒体**：`GameMediaClient`先读持久library UUID，再查真实v2目录项；canonical `native:N` + library UUID组成游戏引用。组级关联/解除/`ForGame`反查，不改原片；库切换/后端重启后拒绝旧响应。
7. **原生编辑**：`new NativeEditorClient(backend,backend.CaptureNativeEditorTransport)`→`ConnectAsync`。候选/上传→crop预览→显式stage/undo；元数据先preview再逐字段confirm。上传只生成本地候选CAS；选择不自动应用。草稿checksum备份、revision CAS、库指纹隔离；异库归档必须明确确认，保留旧草稿文件。`DiagnosticsAsync`返回白名单DTO，不能把RecentLog/异常原文当公开诊断。
8. **截图/连拍**：`CaptureService`须用真实仓库，输出目录等于仓库媒体根；`PublishGroupAsync`先持久预留组UUID，再发布固定PNG和侧车，实际watcher与部分失败都不会拆组。`BurstRun`有数量/时长/间隔/容量上限；慢截图推迟下一次，不无限排队。实机source的并行能力默认false，不能把两路离线fake成功当Thor并行PNG支持。
9. **图库事务/恢复**：同目录组改名支持计划、确认、继续/回滚；跨仓库复制先预留目标组、校验SHA，始终保留源。批量收藏/标签逐组CAS。按RecoveryCatalog列出的安全结果显示恢复选择器，恢复不是自动执行。私有恢复日志含本地引用，不能进入公开日志。
10. **导出**：图片/视频使用同一不可变计划生成预览和成品；VideoExport包支持单屏/竖排/横排/带壳、片段/偏移/结束策略、MP4/WebM/GIF、串行批量和一份音轨。复用DeviceArt/录制CSV；鼠标演示不写时间线。导出放本地作业队列，输出注册到媒体仓库由调用方显式完成。
11. **设备监视**：有界devices-l轮询默认可用；确认本机工具hex4格式后才能开启track。Changed仅通知，不自动续录/控制。无线guard与能力必须绑定已验收的会话/身份，未绑定保持禁用。
12. **设置/数据根**：`SettingsStore`后台原子checksum写入、备份和旧格式迁移；`DataDirectoryPolicy`确定可写位置。前端切换后停旧writer，不能新旧格式同时写同一文件。便携模式不可写明确报错，不悄悄迁移用户目录。

## 合同与详细说明

| 模块 | 固定入口/合同 | 接入与限制 |
|---|---|---|
| 设备/进程 | DeviceSession、OperationGate、ProcessRunner、BinaryProcessRunner | [backend-core](features/backend-core.md)、[设备broker](features/device-broker.md)、[binary取消](features/binary-process-cancellation.md) |
| 状态/设置/监控 | AppStateSnapshot、SettingsStore、GalleryMonitor | [catalog-watch-state](features/catalog-watch-state.md) |
| Node生命期 | cocoon.backend/1、私有stdin关闭/绑定 | [backend-lifecycle](features/backend-lifecycle.md) |
| 导入/空库 | cocoon.library-import/1、LibraryImportClient | [library-import](features/library-import.md) |
| 缩略图 | cocoon.thumbnail/1、ETag/304 | [artwork-thumbnails](features/artwork-thumbnails.md) |
| 只读拉取 | thor.device-broker/1、cocoon.device-read/1 | Node docs/DEVICE-READ-BROKER.md |
| 原生编辑/诊断 | NativeEditorClient、cocoon.diagnostics/1 | [native-editor-client](features/native-editor-client.md)、Node docs/NATIVE-EDITOR-HTTP.md |
| 媒体/关联 | thor.media-group/1、稳定library UUID | [media-repository](features/media-repository.md)、[media-library-service](features/media-library-service.md)、[game-media-client](features/game-media-client.md) |
| 原子PNG发布 | thor.publish/1、PublishGroupAsync | [media-publication](features/media-publication.md)、[capture](features/capture.md) |
| 图库事务 | 固定rename/copy/import恢复合同、thor.recovery-catalog/1 | [library](features/library.md)、[recovery-catalog](features/recovery-catalog.md) |
| 导出 | ExportPlan / VideoExporter / BatchExporter | [video-export](features/video-export.md)、[image-export](features/image-export.md) |
| 远程控制 | thor.control/1内部合同、默认关闭 | [remote-control-core](features/remote-control-core.md) |
| 设备/无线 | DeviceWatcher / WirelessAdb | [device-watch](features/device-watch.md) |
| 组件打包 | thor.cocoon-code-package/1、固定源码/manifest SHA | [cocoon-code-package](features/cocoon-code-package.md) |

## 尚未完成的验收和有意收窄的功能

- **前端接入**：新状态、稳定组、监听、设置、导入/恢复、编辑、标签和导出均需Claude绑定。旧MainForm/static截图入口仍有同步包装，不能据此宣称当前EXE已经消除所有卡顿。旧长预览/录制/输入owner继续保留，启动调用已按serial排队，但尚未统一成一条共享采集流。
- **实机与协议**：双编码器、声音/外放/长期漂移、USB端到端延迟、输入映射、目录/私有库权限、无线配对和真实插拔没有本轮实测。远控目前只有固定内部消息/队列/释放核心，没有通过验证的scrcpy控制sender；手柄轴也未实现，不公开启用。媒体导入有固定只读provider合同与续传核心，实机provider尚未绑定/验收。
- **危险图库操作**：永久删除、系统回收站恢复与跨仓库复制后的源删除明确Unsupported。首发隐藏这些入口；同组改名/保留源复制可用。不能把安全复制文案写成“移动已完成”。未知派生物缺显式引用时不猜测删除。
- **包11实验**：[任务说明](tasks/11-recording-experiments.md)明确要求先选择实验，并需要实机。系统声音、录屏期间连续截图取景已有实现和离线验证；仍需长漂移验收。分段跨180秒边界、共享高帧率采集后端未启动，不能承诺无缝/无损实时预览。
- **公开测试版**：许可/资源权属、公开身份/安全渠道、历史与最终ZIP隐私审计、干净机器和hosted CI、核心实机与30分钟稳定性验收仍是门槛。未公开仓库、push/tag或Release。已有Cocoon V2/Adapter/Helper功能复用；CI5设备写入转正不由本轮离线测试替代。

## 最终离线验证

2026-09-27 最终生产源码冻结后，Thor `test-capture-tool.ps1 -Stream` **1147/1147**，Cocoon **368/368**，组件打包工具 **17/17**；纯代码组件另有8项真实HTTP/关闭检查。最终C# portable探针17项通过，run `86c7fde83686417db5f3bf09aab17224`，记录92个生产源文件与组件运行前后哈希一致。

独立候选 `evidence/backend-final-application/ThorCapture.exe` 编译成功，SHA256 `DCBDC15CA8D4944E79200EB1D03183E9F989ABACDCC0F7B9B5E2996059EF6D5B`。这是后端集成候选，未替换用户程序，不表示新功能已接入全部前端。

Node组件固定提交 `ec4cde82409d050ee7dd72e8e05ac9c898d572da`，101个文件/821045字节，manifest SHA256 `9d20a4a31e4fe0af02095ed4cbcc4f230907aba5e5420c3cbc00f3ae5d95ee74`。组件候选与原生启动探针分别验证；没有打入用户库、配置或运行时。

最终源码、完整日志与候选hash见 `evidence/backend-final-delivery.json`；证据仅在独立工作目录，不纳入公开包。历史失败日志保留，当前结论只依据最后成功完整日志。

最终审查还单独复现并修复临时文件碰撞误删、日志写入前链接检查缺失、阻塞取消超出预算、退出回调未排空、迟到Connect覆盖新库以及Invoke返回途中换库。修复使用真实临时文件/junction、屏障和回环HTTP回归，来源媒体与库不改；不是仅检查实现分支或忽略首次失败。
