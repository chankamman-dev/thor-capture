# 功能任务包：给并行开发的 agent

任务编号、验收标准和状态一律以 [`docs/ROADMAP.md`](../ROADMAP.md) 为准。本目录只做两件事：

1. **分工**：Track A 与 Track B 各做什么。
2. **拆包**：把 Track B 的功能拆成可以同时开发、互不踩文件的任务包，并规定协作规则。

开工前依次阅读：[`AGENTS.md`](../../AGENTS.md)（构建命令、C# 5 限制、已知的坑、用户偏好）→ ROADMAP 中对应的任务 → 本文件 → 自己的任务包。

**2026-09-27 最新用户分工**：Codex 负责后端，Claude 负责前端。以下 Track A 的旧后端框架归属已由用户覆盖，当前协作及后端交付目录见 [`BACKEND-FRONTEND-HANDOFF.md`](../BACKEND-FRONTEND-HANDOFF.md)。保留独立批次、测试与在途修改保护；界面和渲染继续归 Claude。

---

## 1. 分工

### Track A：框架与界面设计（Claude 负责）

| 内容 | ROADMAP 任务 |
|---|---|
| 框架 | P1-1 目录分层、P1-2 AppState / OperationGate、P1-3 服务提取、P1-4 日志与诊断、P1-5 设置、P1-7 格式、P1-8 测试框架、P1-9 渲染性能、P1-10 媒体 ID 与组元数据、P1-11 进程与作业生命周期 |
| 数据底座 | P5-1 目录监视与增量索引（与 P1-10 一起做） |
| 界面与桌面体验 | P1-6 对话框主题、P6-1 设置窗口、P6-2 首次引导、P6-3 版本与关于、P6-4 单实例与数据目录、P6-6 DPI 与键盘、P3-5 回看工作台的界面部分 |
| 外观 | 外壳、按键、灯光的渲染（`DeviceArt` / `Stick3D` / `Dpad3D` / `LampLight` / `ControlLayers`） |
| 集成 | 把每个 Track B 任务包接到界面上：按钮、对话框、状态、互斥规则 |

Track A 的顺序：

| 步骤 | 内容 | 状态 |
|---|---|---|
| A0 交接准备 | 测试自动发现、假 ADB 注册、`Log`、脚本递归编译 | 已完成 |
| A0 基线 | P0-1 提交当前全部成果，否则 worktree 拿不到现有代码 | 需用户同意 |
| A1 | P1-11 `ProcessRunner` / `IAdbClient` + P1-8 故障注入基础，**发布设备与进程接口** | |
| A2 | P1-10 `MediaItem` / `CaptureBatch` / `MediaRepository` + P5-1，**发布媒体接口** | |
| A3 | P1-2 `AppState` / `OperationGate`，**发布互斥与租约接口** | |
| A4 | P1-4 日志与诊断、P1-5 设置 | |
| A5 | P1-3 服务提取、P1-1 分批搬目录 | |
| A6 | P1-6、P6-1/2/3/4/6、P1-9 | |
| 持续 | 集成已交付的 Track B 包 | |

A1–A3 合并后，第二批任务包才能开始。

### Track B：细分功能（其他 agent 负责）

每个包只写**功能核心**（设备交互、数据处理、渲染输出），不做界面，通过公开接口 + 测试 + 集成说明交付，由 Claude 接到界面上。

| 包 | 名称 | ROADMAP 任务 | 实机 | 批次 |
|---|---|---|---|---|
| [1](01-device-acceptance.md) | 实机验收与设备能力档案 | P0-2、P0-3、P0-4、P0-5 | 是 | 第一批 |
| [2](02-input-triggers.md) | 输入触发：组合键核心、全局快捷键组件 | P2-2、P2-3（核心） | P2-2 是 | 第一批 |
| [3](03-image-export.md) | 图片导出引擎：预设、拼图、整数倍率 | P4-1、P4-3、P4-4（引擎） | 否 | 第一批 |
| [4](04-thumbs-similarity.md) | 缩略图磁盘缓存、相似图检查 | P5-2、P5-6（核心） | 否 | 第一批 |
| [5](05-image-editing.md) | 非破坏性图片编辑引擎 | P5-7（引擎） | 否 | 第一批 |
| [6](06-release-materials.md) | 开源发布材料：许可、审查、贡献文档、CI | P7-1 ~ P7-5、P7-8 | 否 | 第一批 |
| [7](07-device-watch-wireless.md) | 设备监视与安全重连、无线 ADB | P2-6、P2-7 | 是 | 第二批（等 A1、A3） |
| [8](08-capture-batches.md) | 双屏并行截图、定时与连拍 | P2-1、P2-4 | 是 | 第二批（等 A1、A2、A3） |
| [9](09-library-management.md) | 标签收藏、组级事务、导入机内素材 | P5-3、P5-5、P2-5 | P2-5 是 | 第二批（等 A1、A2） |
| [10](10-video-export.md) | 视频导出计划、GIF、批量导出 | P4-6、P4-5、P4-2 | 否 | 第二批（等 A1、A2） |
| [11](11-recording-experiments.md) | 分段录制、录音、预览录屏共享后端研究 | P3-1、P3-4、P3-6 | 是 | 第三批（实验，用户决定） |

批次说明：
- **第一批**：只依赖现有稳定代码，现在就可以开始。
- **第二批**：需要 Track A 的 A1–A3 接口。可以先读代码、写设计，但**在接口合并前不要写依赖它的实现**，接口以合并后的代码为准。
- **第三批**：ROADMAP 中为 C 级实验项，开工前先问用户。

---

## 2. 并行开发规则（所有包必须遵守）

### 2.1 工作目录与分支
- 每个包在独立的 git worktree 和分支上开发。**不要在仓库主目录里直接改代码**：多个 agent 同时改会互相覆盖文件和构建产物。

```powershell
git -c "safe.directory=*" worktree add "..\thor-capture-pkg<编号>" -b feature/pkg<编号>-<短名>
```

- 前提是 A0 基线已提交（主目录现有的全部成果都在 `main` 上）。在此之前不要创建 worktree。
- worktree 里没有 `artifacts/`（不纳入版本控制）。需要 ADB / FFmpeg 时：
  - 设置环境变量 `THOR_ADB`、`THOR_FFMPEG`，
  - 或从主目录复制 `artifacts\adb-path.txt`、`artifacts\ffmpeg-path.txt` 到自己的 `artifacts\`。
- 交付前 rebase 到最新的 `main`。

### 2.2 文件归属
- 新代码只放在 `src/Features/<包短名>/`，测试放在 `tests/<包短名>Tests.cs`（可以有多个文件，类名以 `Tests` 结尾），集成说明放在 `docs/features/<包短名>.md`，证据放在 `evidence/<日期>-pkg<编号>/`。
- 以下现有文件归 Track A，任务包**只能读、不能改**：

| 类别 | 文件 |
|---|---|
| 界面 | `src/MainForm*.cs`、`src/Controls.cs`、`src/Preview.cs`、`src/PreviewOptions.cs`、`src/ShellInteraction.cs`、`src/ControlLayers.cs`、`src/LampPanel.cs` |
| 渲染 | `src/DeviceArt.cs`、`src/Stick3D.cs`、`src/Dpad3D.cs`、`src/LampLight.cs` |
| 框架（P1-11 / P1-10 会迁移） | `src/App/*`、`src/ThorCapture.cs`、`src/Recording.cs`、`src/RecordingSession.cs`、`src/LiveVideo.cs`、`src/LiveFrame.cs`、`src/VideoPlayback.cs`、`src/CaptureMetadata.cs`、`src/MediaAudio.cs`（录屏、导出、回放共用） |
| 输入 | `src/InputCapture.cs`（2 号包在自己的目录里扩展，不改原文件） |
| 测试与工具 | `tests/Tests.cs`、`tests/InfrastructureTests.cs`、`tools/*.ps1`、`tools/*Check.cs`（新增脚本可以，改现有的不行） |
| 文档 | `AGENTS.md`、`README.md`、`docs/ROADMAP.md`（状态更新写进集成说明，由 Claude 回填） |

- 指定给某个包的现有文件：`src/VideoExport.cs` 归 10 号包；`src/RecordingAudio.cs` 归 11 号包。可以修改，但要保持现有公开接口，现有测试照常通过。
- 需要给 `Adb` 或其他 Track A 文件增加能力时：在自己的目录里写辅助类，并在集成说明里写明需求，由 Claude 决定是否合并进框架。**不要为了方便去改归属外的文件。**

### 2.3 接口约定
- 功能类**不引用 `System.Windows.Forms`**。例外：2 号包的全局快捷键组件，需要窗口句柄。
- 耗时操作返回 `Task` / `Task<T>`，接受 `CancellationToken`；需要进度的接受 `IProgress<int>`（0–100）。
- 结果用类型化对象返回（成功 / 取消 / 不支持 / 断线 / 超时 / 文件失败 + 原因），不要用 `null` 或吞异常表示失败。
- 可能在后台线程触发的事件，在 XML 注释里写明「在后台线程触发」。
- **不要自己管理忙碌状态或互斥**，由 Track A 的 `OperationGate` 统一处理。功能需要独占设备或编码器时，在集成说明里写清楚。
- 任务绑定的 serial、display ID 等参数在开始时固定下来，不读可变的全局状态。
- 设置键加包前缀（例如 `trigger.combo`）；在 P1-5 完成前用现有的 `Settings.Get/Set`。
- 日志用 `Log.Info / Warn / Error`：外部命令记录命令、耗时和退出码。**不记录配对码、完整 serial、用户名路径。**
- 文件写入一律「先写临时文件 → 校验 → 替换」，失败不留半成品，**原片永远不改**。
- 语言限制仍是 C# 5，只能用系统自带程序集（见 AGENTS.md）。

### 2.4 测试
- 在 `tests/` 下写 `internal static class <名字>Tests`：
  - `internal static void Run()`：必需，本机回归。
  - `internal static void RunLive()`：可选，只在 `-Live` 时运行。
  - `internal static int FakeAdb(string command, string mode, string dir)`：可选，回答假 ADB 命令；不认识的命令返回 `Tests.NotHandled`。
- 断言用 `Tests.Check` / `Tests.Reject`；切换假 ADB 模式用 `Tests.FakeMode("xxx")`；临时文件放在 `Tests.Root`。模板见 `tests/InfrastructureTests.cs`。
- 只跑自己的测试：`./tools/test-capture-tool.ps1 -Filter <名字>`。交付前跑一次全量（含 `-Stream`，如果本机有 FFmpeg），必须全部通过。
- 渲染类测试按区域平均值加容差判断，不要依赖单个像素（ROADMAP P1-8）。

### 2.5 实机
- 只有一台 Thor，**同一时间只能有一个 agent 连机**。需要实机时先问用户，同意后再连；测完告诉用户已经结束。
- 实测结果（数据、截图、脱敏日志）放 `evidence/<日期>-pkg<编号>/`，在集成说明里明确区分实测和模拟。
- 禁止 root、提权、安装设备端常驻程序、修改设备设置、向设备注入按键。

### 2.6 交付（完成的定义）
1. 分支已 rebase 到最新 `main`，全量回归通过，没有编译警告。
2. `docs/features/<包短名>.md` 包含：
   - 公开接口
   - 线程模型
   - 设置键
   - 错误与降级行为
   - **界面需要什么**（入口、对话框字段、默认值、状态文字、进度、提示语、禁用原因）
   - 实测数据与未测项
   - 对应 ROADMAP 任务建议标注的新状态
3. 提交信息用英文，风格参照 `git log`，结尾加 agent 自己的署名行。
4. 告诉用户「N 号包已完成，分支 `feature/pkgN-…`」。由 Claude 审查、合并、接到界面并更新 ROADMAP 状态；**不要自己合并到 `main`**。

---

## 3. 第二批依赖的接口预告

以下是 Track A 的设计方向，供第二批包提前规划；**最终以 A1–A3 合并后的代码为准**。

```csharp
// A1 (P1-11): every external process goes through this; owned processes only, bounded output.
internal interface IAdbClient
{
    Task<ProcessResult> RunAsync(string serial, AdbCommand command, TimeSpan timeout, CancellationToken token);
    Task<ProcessResult> RunBinaryAsync(string serial, AdbCommand command, Stream output, TimeSpan timeout, CancellationToken token);
    IOwnedProcess Start(string serial, AdbCommand command);        // long-running (track-devices, screenrecord, getevent)
}
// ProcessResult: Outcome (Success/Cancelled/TimedOut/Failed/NotFound), ExitCode, Stdout, Stderr (capped), Duration

// A2 (P1-10): stable ids and versioned group metadata; old names and sidecars stay readable.
internal sealed class CaptureBatch { string BatchId; DateTime CreatedUtc; List<MediaTrack> Tracks; ... }
internal sealed class MediaItem { string MediaId; string BatchId; ScreenRole Role; string Path; MediaKind Kind; ... }
internal interface IMediaRepository
{
    event EventHandler<MediaChangedEventArgs> Changed;              // raised on a background thread
    IList<MediaItem> Query(MediaQuery query);
    Task<MediaWriteResult> CommitAsync(MediaTransaction transaction, CancellationToken token);
}

// A3 (P1-2): resource leases; released in finally, stale generations rejected.
internal sealed class OperationGate
{
    bool TryAcquire(OperationKind kind, string serial, out OperationLease lease, out string reason);
}
```
