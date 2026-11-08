# Track B 第一批交付与公开测试版接续

**2026-09-27 当前覆盖**：用户已改为Codex后端/Claude前端，A1–A4后端底座及第二批7–10已离线整合，不再等待旧框架分工。最新范围、前端接线和仍未实现的sender/真实provider/危险图库操作/实验见 [BACKEND-COMPLETION.md](../BACKEND-COMPLETION.md)。本文后续保留第一批历史，不以旧提交、测试数或“未合并”作为当前事实。

2026-09-27，用户已确认“保留 Claude 分工，你完成 Track B”。本文件为统一交接入口，主 ROADMAP 的任务状态仍由 Claude 更新。不是公开测试版完成声明。

## 已固定的基线

主库 `main` 为 `faaf1463c2331e854c308e07afad85c4d156bdbd`：保存此前已测试的截图、双屏录制/回放、预览调参、鼠标演示、系统声音代码与文档。此前原片/配置/运行进程不动；备份 `backups/before-track-b-20260927-011333` 保留。基线完整离线流检查221项。

所有功能均在与主库同级的独立 worktree 开发，未合并 main、未接主界面、未替换在用 EXE、未连接 Thor、未推送或发布。分支已 rebase 到上述 main；项目未有 A1–A3 的正式接口合并，因此第二批尚未实现。

## 第一批六个包

| 包 | 分支 / 已验证提交 | 同级目录与说明 | 已验证范围 |
|---|---|---|---|
|1 能力档案|`feature/pkg1-capability` / `70b1e668569eee6e3013c7ede76370b170a30c05`|`thor-capture-pkg1-capability`，`docs/features/capability.md`|37新检查；258 Stream PASS。只读可取消探测、保守能力矩阵、主机检查、默认离线验收工具；实机能力未知|
|2 输入触发|`feature/pkg2-triggers` / `651e68835f7e9fa012dfdea0f10d6a709f9cde16`|`thor-capture-pkg2-triggers`，`docs/features/triggers.md`|39新检查；260 Stream PASS。长按/防抖/冷却/Reset防误触、真实Windows快捷键注册冲突与释放；默认关闭|
|3 图片导出|`feature/pkg3-image-export` / `bbf7293bde50ec691a0f67e3fb7bf72ad8cc189d`|`thor-capture-pkg3-image-export`，`docs/features/image-export.md`|58新检查；279 Stream PASS。四配色/灯开关/单双屏旧版逐像素兼容、拼图、整数倍率、预设、安全保存|
|4 缓存/相似|`feature/pkg4-thumbs` / `15ae63c77605b2d3b6797b4b5940862b06a3eae1`|`thor-capture-pkg4-thumbs`，`docs/features/thumbs.md`|39新检查；260 Stream PASS。可见优先有界队列、磁盘/内存LRU、降级/取消、只读SHA/dHash候选；实际允许外部写源，严格比较预算|
|5 图片编辑|`feature/pkg5-editing` / `30858dc918aed0ea43c419aafd1f08c201e7338a`|`thor-capture-pkg5-editing`，`docs/features/editing.md`|46新检查；267 Stream PASS。不可变历史、UndoRedo、裁剪/旋转/遮盖/标注、透明PNG、原片保护和安全另存|
|6 开源材料|`feature/pkg6-release` / `52e13aa7474b75970fda3be3b77498f265bc3108`|`thor-capture-pkg6-release`，`docs/features/release.md`|干净克隆149基础/221 Stream PASS；原配置FFmpeg6.0及固定CI8.1.2各221 PASS；PS5.1/7扫描器14 PASS|

3号分支的后续提交只增加联合离线检查工具和本交接文件；实际交付时以该分支最新 HEAD 为准。4号交付需包含前提交 `0491be51c91ed9648d4069a78d51621ae50b6472` 及后续修正；6号需包含 `bf59511` 前提交。审查完整分支，不只挑选后一个修正提交。

每包证据位于自己的 `evidence/20260927-pkgN/`；3号额外原生合成图片/448ms离屏绘制记录在 `evidence/thor-capture-tests/`。缓存100/1000/10000合成库只测组件首48张，不能当完整图库性能验收；编辑4K合成图只测离屏引擎。均无编译警告，不以检查数量替代设备证据。

## 联合验证方法

3号 `tools/test-track-b-offline.ps1` 从各 worktree 的 Feature 和新测试编译同一独立测试程序集，核对 Track A 基线文件哈希一致、拒绝重复特性/冲突测试与夹具，在忽略的 evidence 中建立虚拟测试目录。它**不合并分支、不改主库、不接UI、无Live参数**，只调用假设备/合成 `--stream-check`。在Windows PowerShell5.1执行；设置 `THOR_FFMPEG` 或传 `-FfmpegPath`，可通过 `-PackageRoots` 指定各目录。

首次尝试因能力夹具按程序集父目录查找而失败，未误报通过。工具已改为建立完整虚拟测试项目并复制校验过的测试夹具。最终Windows PowerShell5.1 + FFmpeg6.0联合编译/完整流回归 **440项全PASS（221基线 + 219新检查），无编译警告**；输出 `evidence/20260927-track-b/combined-stream.txt`，只认其最后 TOTAL PASS 和非零退出检查；失败尝试另存 `attempt-fixture-path.txt`。这证明各包可以在同一测试程序集共存，仍不是实际应用/窗口集成。

## Claude 的集成顺序

1. 读 AGENTS、ROADMAP、任务分工及本文件，确认 main 基线、工作树和用户在用进程。按 A1/A2/A3 发布正式 ProcessRunner/IAdbClient、MediaItem/CaptureBatch/Repository、OperationGate/租约接口；所有进程只管理自身拥有者，输出/超时/取消有界。
2. 优先审查并接1号能力档案、4号缩略图缓存，与 P5-1 索引/可见行配合。不要索引时请求整库缩略图，不在 UI 同步查磁盘；Ready后台线程的Bitmap移交与generation必须正确。能力 Unknown 不偷偷开放“双屏编码/系统声音/实际按键映射/低延迟”等未经实机确认的功能。
3. 按 A4–A6 完成日志脱敏/轮转、设置迁移、可写数据目录、桌面与主题/DPI；按照用户选择保留鼠标只演示外壳，不控制设备或写录制时间线。不要退回平面按钮或移动固定灯环。
4. 按发布优先级接入2/3/5可选入口。2号使用真实原始PadState和单调时钟定时Update，启用/断线Reset且默认关闭；所有动作走OperationGate。3号预览/保存绑定同一不可变计划，旧版兼容缺屏需UI badge。5号默认不透明遮盖、另存新PNG，大图快照去抖/单个在途渲染，派生信息在 A2 接入时转换。
5. 审查6号材料，依据用户最后决定完成 LICENSE/归属、公开文案、版本、反馈/私密安全通道；CI仅在本地复现，真实hosted runner仍须执行。重生成来源清单（当前仅faaf146的68文件），重扫最终所有可达历史和最终候选ZIP，不能直接压缩artifacts/evidence。
6. 逐批合并后运行全部离线检查、渲染与窗口验收；冻结具体候选/hash，再向用户申请真实设备测试，完成双屏录制/声音/录制中取景、输入映射、断线、USB延迟与30分钟稳定性。用户此前选先离线开发，本轮并无连机授权。

本轮不代替 Claude 合并或修改 Track A，不把第一批组件交付标成完整公开测试版。

## 第二批 Track B 继续条件

正式接口合并后，先重新读取其真实代码及各包说明，再在新的独立 worktree 开工；不能根据接口“预告”自造实现。

| 包 | 必需接口 | 接续重点 |
|---|---|---|
|7 设备监视/无线|A1、A3|只读监听、安全重连、绑定身份、显式无线配对；不恢复危险动作，配对码不日志|
|8 捕获批次|A1、A2、A3|双屏并行截图、定时/连拍、原子批次与单侧失败，不抢录制编码器|
|9 图库管理|A1、A2|组级安全事务、收藏标签、显式机内导入；未知组禁止猜测删除|
|10 视频导出|A1、A2|不可变计划、双屏/一份音轨、GIF、批量，复用3号图片引擎；取消不损原片|
|11 实验|用户决定后|P3-1/4/6为第三批C级扩展；不因为此前修复声音就默认授权所有录制后端实验|

公开测试版 R2 首先完成 ROADMAP 明确发布必需项，不强制把所有R3/C扩展接入首发。需要设备的任务继续区分离线实现、构建、UI、实机与发布验收。

## 发布前仍需所有者决定

6号材料已经给出可审阅选项：自有代码许可证和版权主体/公开署名；名称及刻字logo边界；ADB/FFmpeg外置与固定server分发；真实维护者/私密通道；公开历史处理方案。当前README/开发文档/旧历史有实际私人信息和需人工复核项，忽略文件规则不能清除历史。保留私有原库，不自行重写或公开。

最终公开仓库、push/tag/Release须在真实候选可审阅、必要验收和上述决定完成之后获明确批准。没有执行任何公网发布。
