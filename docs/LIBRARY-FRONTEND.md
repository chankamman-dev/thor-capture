# 游戏库前端（Cocoon 合并 · 原生界面第一期）

更新：2026-09-27。用户选定**原生重写、分期**：不引入 WebView2，游戏库用 WinForms 按现有设计语言重做；尚未重写的深度编辑暂时在浏览器中打开 Cocoon 网页。后端由 Codex 负责（`cocoon.backend/1`，见 `..\cocoon-art-backend\docs\BACKEND-COMPONENT.md`）。

## 第一期已完成

- **导航**：主窗口左侧导航栏「工作台 / 游戏库」，选中底块弹簧滑动；栏底圆点表示游戏库服务状态（运行中 / 启动中 / 失败）。顶部设备选择、截图、录屏保持全局。
- **后端进程**（`src/Library/CocoonBackend.cs`）：仅在首次打开游戏库时，由本窗口启动并拥有一个 `node backend.cjs` 子进程。
  - 启动参数：`COCOON_DATA_ROOT` 为绝对路径，`PORT=0`，隐藏窗口，重定向三个流，stdin 保持打开。
  - 就绪校验：只解析带 `COCOON_BACKEND_EVENT ` 前缀的行，核对产品、合同版本、`instanceId`，且地址必须是 127.0.0.1。
  - token：只从本次入口页 meta 读取，放在内存里，请求时带 `X-Cocoon-Art-Token`；不写日志、不放进 URL。
  - 关闭：窗口关闭时通过 stdin 发送带 `instanceId` 的 shutdown 并关闭 stdin，不等待、不强杀，也不接触其他 Node / Cocoon 进程。
  - 失败码映射为一句话说明，并提供重试：
    - `NODE_NOT_FOUND`
    - `BACKEND_NOT_FOUND`
    - `DATA_ROOT_IN_USE`
    - `PORT_IN_USE` / `LISTEN_FAILED`
    - `TOKEN_UNAVAILABLE` / `ENDPOINT_REJECTED`
    - `BACKEND_EXITED`
- **路径**（设置文件可覆盖）：

  | 设置项 | 默认值 |
  |---|---|
  | `cocoon.node` | PATH 中的 `node` |
  | `cocoon.backendDir` | 程序旁的 `cocoon\`，其次 `..\..\cocoon-art-backend` |
  | `cocoon.dataRoot` | 程序旁的 `data\cocoon`（与截图数据分区；不读原项目数据） |

  工具与测试可用环境变量 `THOR_COCOON_DATA_ROOT` 指向演示数据根。
- **游戏库页**（`src/UI/LibraryPage.cs`、`GameGridView.cs`、`GameDetailView.cs`）：
  - 顶栏：搜索（150ms 防抖，内存过滤）；平台筛选（放得下时用分段控件，放不下时改为下拉）；收藏、缺素材两个开关；重新读取；在浏览器中编辑。
  - 卡片网格：按平台分组，方形封面（没有封面时显示首字和平台名），名称、平台，三个小点表示封面 / Logo / 背景是否齐全，收藏标记。交互与图库一致：弹入、悬停浮起、深色描边选中，支持键盘方向键。
  - 详情：
    - 背景图横幅，Logo 叠在左侧，配从左侧压暗的渐变；没有背景图时显示平台名，不放 Logo。
    - 封面压在横幅边缘，下面是标题和平台标签。
    - 资料两列，未知的项跳过；评分用五个点表示，可部分填充；简介。
    - 素材三个槽位：Logo 用深色底；缺失时显示虚线框和「缺失」；每个槽位标注来源。
  - 状态页（吉祥物 + 一句话）：启动中（带进度条）、失败（附重试）、空库（「从掌机读取游戏库需要设备访问，这一版暂未开放」）、无匹配。
  - 封面图：3 个并发拉取，按用途缩放后放进 160 项的内存缓存；缺失的图记住不再重复请求；后端重启或重新读取时清空。
- **演示数据**：`tools/cocoon-demo.cjs <绝对路径>` 生成 24 款虚构游戏和抽象合成图，拒绝覆盖已有库。`render-check.ps1 -Window` 自动使用 `evidence/cocoon-demo-root`，输出 `window-library.png`。

## 验证

- `tests/LibraryTests.cs` 共 12 项：
  - JSON 读取与引号转义、游戏条目解析。
  - 缺组件、缺 Node 两种失败码。
  - 真实启动 `backend.cjs`：就绪、鉴权读库 24 款、封面有无、同一数据根第二实例被拒、stdin 正常关闭。
- 缺少组件或 Node 时，真实启动部分记为 SKIP。
- 全量回归通过，测试结束后没有残留 Node 进程。
- 离线窗口截图：`evidence/ui-check/window-library.png`。
- 没有连接设备，没有读取或迁移原 Cocoon 数据。

## 第二期（进行中，2026-10-11）：资料编辑与本机草稿

后端接口见 [native-editor-client](features/native-editor-client.md)；界面只走 `NativeEditorClient`，不读草稿文件。

- **草稿会话**（`src/UI/GameEditing.cs` 的 `GameEditSession`）：每个游戏库页一个，持有编辑器绑定、草稿 revision 和暂存条目。后端离开 Ready、每次重新读取游戏库后都会 `Reset()` 并重新读取草稿；旧 epoch 的迟到响应丢弃。
- **编辑资料**（`MetadataDialog`）：详情页标题行右侧「编辑资料」。表单 → 「检查改动」（`MetadataPreviewAsync`，逐项对比库内原值）→ 勾选 → 「暂存所选」（`MetadataConfirmAsync`）。没有改动、日期/评分格式不对时不发请求。确认失败（含取消、超时）后一律重新读取草稿，因为无法证明服务端没有提交；revision 冲突或预览过期回到表单。
- **暂存的修改**（`PendingDialog`）：有暂存条目时顶栏出现带数量的入口；列出每条的游戏与字段/槽位，逐条「撤销」。草稿属于另一个游戏库时，这里是唯一的「归档旧草稿」入口（明确点击才归档，旧草稿文件保留）。
- **详情页**：有暂存资料时显示「已暂存 N 项资料修改，尚未应用」，下面仍显示库内原值。
- 所有修改只进本机草稿：不改导入的游戏库、不写掌机。「应用」（变更中心）尚未接入。
- 验证：`tests/GameEditingTests.cs` 16 项（真实后端 + 演示库：表单、格式拦截、审阅、只暂存勾选项、撤销、数据库字节不变）。样张 `evidence/ui-check/metadata-edit.png`、`metadata-review.png`、`pending-list.png`、`window-library-staged.png`。

### 从掌机读取游戏库（界面已接，实机未验收）

- 入口：顶栏「导入 ▾ → 从掌机读取…」，以及「还没有游戏库」页面的主按钮。未连接、正在截图/录屏、本机有暂存修改时，先给一句说明，不碰设备。
- `src/UI/DevicePull.cs` 的 `DevicePullRunner`：按需创建本窗口的 `DeviceSessionRegistry` / `OperationGate` / `DeviceCommandBroker`，经 stdin 绑定到自有后端，每次读取新开一个会话，轮询 `cocoon.device-read/1` 任务直到得到导入清单或错误码。
- 界面复用 `ImportDialog`（设备模式）：说明页 →「开始读取」才发第一条设备命令 → 读两遍比对 → 与目录导入相同的清单审阅 → 明确确认 → 进度 → 可撤销。读取中取消或关窗会放弃这次读取；已提交的导入不受影响。
- 只带回数据库：清单 `includeCache:false`，封面等素材文件仍在掌机上。
- 验证：`tests/DevicePullTests.cs` 12 项（真实后端 + 夹具 reader：打开对话框零设备调用、拒绝读取如实报告且本机库不变、重试得到 24 款清单、四次读取、确认后页面显示、夹具字节不变、取消、broker 干净停止）。
- **root 只读（2026-10-14，用户明确允许）**：普通 `adb exec-out cat` 读私有数据库在用户的 Thor 上被拒绝（`Permission denied`，且 adb 退出码为 0、报错文字被当成文件内容）。`BrokerFileReader.Classify` 现在按 SQLite/WAL 文件头判定，非数据库一律 `DEVICE_READ_DENIED`。被拒后对话框才出现「用 root 权限读取」，点击后 `BrokerFileReader.AllowRoot` 允许用 `su -c` 重读同一条固定命令，并记入设置 `cocoon.rootRead=1`；首次读取永不使用 root，root 下仍被拒显示 `DEVICE_ROOT_DENIED` 文案。仍然只读：不写设备、不停 Cocoon、不落临时文件。实机上直接执行这两条命令得到 10.9 MB 数据库和 512 KB WAL，文件头正确；完整的界面流程尚未在实机走通。
- 顺带修复 `CocoonBackend` 的 stdin 命令：系统或控制台代码页为 UTF-8 时，.NET 在子进程启动时就往 stdin 写入 BOM，粘在第一条命令前导致该命令被丢弃（表现为设备通道绑定失败）。现在每条命令以原始 UTF-8 字节发送并另起一行。测试夹具里自己写 stdin 的两个套件（`NativeEditorClientTests`、`ThumbnailSessionTests`）在 UTF-8 控制台下仍会失败，代码页 936 下通过，留给后端处理。

### 封面 / Logo / 背景与背景视频（2026-10-14）

- 入口：详情页的三个素材格可直接点击，打开 `ArtworkDialog`（`src/UI/ArtworkDialog.cs`）。分「封面 / Logo / 背景」三页；来源图片来自已配置且提供图片的来源（SteamGridDB、TheGamesDB 等）对这款游戏的候选，每个来源取前 3 条候选的 `/api/scrape/assets`；没有候选时先搜一次，也可改名重新搜索。缩略图由 `WebImage` 直接从 CDN 下载（仅 HTTPS、有大小上限）。
- 选中 → 下载原图 → `UploadAsync` 生成本地候选 → `StageAsync` 暂存。「从电脑选择…」支持图片；背景页还支持 MP4/WebM，走 `UploadVideoAsync` → `VideoPosterAsync` → 以视频 id `StageAsync`。上限 32 MB。
- 详情页：暂存的新图直接显示在对应格子里并标「已暂存」（像素经 `PreviewAsync` 取得）；掌机上原本是视频的背景显示「视频背景」。
- 应用：见 [features/device-apply.md](features/device-apply.md) 的「素材」一节。应用成功后图片类暂存也一并清除。
- 验证：`DeviceApplyFlowTests` 用真实后端暂存 PNG 封面与 2 秒 MP4 背景并写回模拟设备；`ScrapeTests` 实流程用真实 SteamGridDB 取到 27 张封面、2 个 Logo 并暂存。样张 `artwork-picker.png`。
- 未验证：向真实掌机推送素材文件（掌机在测试时断开）；Cocoon 3.07 对新背景视频的实际播放。

### 刮削（2026-10-14，资料部分）

入口：详情页「刮削」，顶栏「刮削 ▾」（刮削选中的游戏 / 批量刮削 / 来源设置）。

- `src/Library/ScrapeClient.cs`：来源列表与连接测试、保存凭据（只接受九个已知键）、单个搜索、候选列表、选定候选。`NativeFields` 按后端 `lib/metadata/nativeSelection.js` 的规则把选定候选转成目录字段值（数组用 `, ` 连接、日期转毫秒、评分 0–100、锁定字段跳过）。
- `ScrapeDialog`：已有候选直接显示，否则自动搜索；可改搜索名（改过才作为 `term` + `strictTerm` 发送）；选一条 → `/api/scrape/select` → 用 `MetadataPreviewAsync` 与库内原值逐项对比 → 勾选 → `MetadataConfirmAsync` 进本机草稿。**库里原来为空的字段默认勾选，已有内容的默认不勾。** 不走旧的 `/api/scrape/merge-apply`（那条路写旧 pending）。
- `BatchScrapeDialog`：范围（缺资料 / 当前列表 / 全部）+ 来源，用 `StartScrapeAsync` 每 200 款一个任务顺序执行，可停止；结果按「有候选、很可能匹配」排序，点一款进入上面的核对。批量只产生候选，不自动采用。
- `ScrapeSettingsDialog`：六个来源的状态与「测试」，单项密钥填写，「从 Cocoon Art 导入密钥…」读取所选文件夹的 `config.json` 里的已知键（不显示内容）。
- 用户已同意把原 `cocoon-art\config.json` 的密钥复制到 `artifacts\data\cocoon\config.json`（SteamGridDB、TheGamesDB、ScreenScraper 账号、RetroAchievements）。
- 实测网络：本机直连和经本地代理都连不上 Steam 商店（`ECONNRESET`）；其余五个来源可达。ScreenScraper 缺开发者 ID/密码、IGDB 未配置，二者不可用。RetroAchievements 对 PSP 等平台返回「尚未接入」。
- 验证：`tests/ScrapeTests.cs` 离线 7 项；设置 `THOR_SCRAPE_CONFIG` 和 `THOR_SCRAPE_CATALOG` 后另有 13 项实流程（隔离数据根、真实来源：测试密钥、搜索、选定、对比、暂存、3 款批量），已跑通一次。样张 `scrape-*.png`。
- **未做**：刮削图片（封面/Logo/背景的候选浏览、下载、暂存），以及图片写回掌机；「一键采纳高置信度结果」。

### 掌机图片同步（2026-10-14）

从掌机读回的游戏库只记录图片在设备上的位置（`content://…/document/primary%3A…` 或私有目录路径），后端的缩略图只查本地 `_cache`，不访问设备。`src/Features/DeviceApply/DeviceArtSync.cs` 在游戏库载入且掌机已连接时后台运行：按「全部封面 → Logo → 背景」的顺序，对每个尚未缓存的图片执行一次只读 `cat`，写入 `<数据根>\_cache\media\<sha256(设备路径)><扩展名>`（与 `server.js` 的 `thumbnailSource` 第二候选同名）。三路并发、已缓存的跳过、只收 PNG/JPEG/GIF/WebP（按文件头判断，报错文字和视频不入缓存）。`/storage/` 下的文件用普通权限读；Cocoon 私有目录的应用图标只在用户已允许 root 读取时读，且只接受不含特殊字符的路径。进度显示在标题旁「同步图片 n/总数」，约每秒刷新一次页面。

验证：`tests/DeviceArtSyncTests.cs` 11 项；实机读回 12 张（含中文与空格文件名、私有目录图标），14 个路径的缓存名与后端算法逐一相同。**未在正式程序里看过同步后的页面效果。**

### 应用到掌机（2026-10-14，未做首次真实写入）

「暂存的修改 → 应用到掌机…」：审阅 → 确认 → 停用 Cocoon、取快照、打补丁、换回、启用 → 刷新本机副本并清掉已应用的暂存。只支持元数据。方案、设备命令、恢复机制和未验证项见 [features/device-apply.md](features/device-apply.md)。

尚未接入的后端能力（均已交付客户端）：素材候选/原件/裁剪/布局/Logo 位置/Hero 视频、批量刮削任务、变更中心审阅与确认、受管理迁移。

## 后续分期（原生重写顺序建议）

1. **素材编辑**：候选图浏览（SGDB / Steam / 刮削候选）、裁剪与构图、暂存与撤销。沿用现有 `/api/sgdb/*`、`/api/steam/*`、`/api/scrape/*`、`/api/upload`、`/api/unstage`；暂存必须明确提示「未应用」。
2. **元数据审阅**：逐字段比较候选、锁定字段、merge-preview；不能一选候选就自动应用。
3. **变更预览**：ChangeSet 预览列表；在设备写入能力通过验收前只做预览。
4. **媒体关联**：等后端交付「媒体组与游戏关联」接口后，在详情里加「截图与录屏」区，并在图库里提供「关联到游戏」。
5. **设置与导入**：Provider 凭据、数据导入与迁移（先列清单、备份、校验）。

每期完成后移除对应的浏览器入口。设备读取和写入按后端契约与实机验收逐步开放，前端不自行解除冻结。
