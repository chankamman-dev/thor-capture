# 6 号包：开源发布材料

- **ROADMAP 任务**：P7-1 代码归属与许可、P7-2 ADB / FFmpeg / 可选后端许可审查、P7-3 仓库历史与隐私审查、P7-4 贡献与安全文档、P7-5 Windows CI、P7-8 版本维护规则。细化清单见 [`docs/OPEN-SOURCE-RELEASE.md`](../OPEN-SOURCE-RELEASE.md)。
- **不在本包**：P7-6 发布包（依赖 Track A 的 P6-3 / P6-4）、P7-7 公开发布（必须由用户确认后才能执行）。
- **批次**：第一批。以文档和审查为主，几乎不碰源码，冲突最少。
- **短名**：`Release`；产出放 `docs/release/`、`.github/`、`tools/`（新增脚本），说明 `docs/features/release.md`。

## 重要边界
- **不做任何公开动作**：不创建远程仓库、不 push、不发 Release、不改仓库可见性。
- **不替用户做法律决定**：许可证选择、版权主体、是否捆绑依赖，都整理成「选项 + 影响 + 建议」交给用户决定。
- 审查中发现的私人资料（路径、serial、邮箱、照片 EXIF 等）只报告位置，**不自行重写 git 历史**。

## 产出清单

| 任务 | 产出 |
|---|---|
| P7-1 | `docs/release/provenance.md`：逐文件、逐素材的来源表（用途、来源、作者、许可、改动、是否分发）；列出 AYN / Thor 名称、外壳和刻字 logo 的品牌边界问题；许可证选项对比 |
| P7-2 | `docs/release/third-party.md`：本机 ADB、FFmpeg（含 `-buildconf` 输出）、`third-party/scrcpy` 的实际版本、哈希、许可和再分发义务；`THIRD-PARTY-NOTICES` 草稿；捆绑与不捆绑两种方案的对比 |
| P7-3 | `docs/release/privacy-audit.md`：工作树、git 历史、候选发布包三个范围的扫描结果（私人路径、账号、serial、配对码、秘密、媒体 EXIF）；`tools/release-scan.ps1` 扫描脚本；发布文件 allowlist 草案 |
| P7-4 | `CONTRIBUTING.md`、`SECURITY.md`、行为准则、Issue / PR 模板（Bug 模板提醒用户脱敏）、`docs/ARCHITECTURE.md` 草稿（根据 ROADMAP 第 3 节和实际代码）；中文为主，附英文入口 |
| P7-5 | `.github/workflows/build.yml`：在干净的 Windows runner 上编译并跑离线回归；Actions 固定到完整 commit SHA；默认只读权限；不接触发布密钥。UI / STA 测试不能运行时要明确标注跳过，不能静默忽略 |
| P7-8 | `docs/release/versioning.md`：语义化版本和预发布规则、兼容政策、`CHANGELOG.md` 格式、问题分级（数据损坏、误杀进程、隐私泄漏优先修复）、1.0 的条件 |

## 要点
- 事实要有出处：许可条款附官方链接；FFmpeg 的许可取决于实际构建配置，不能一律当作 LGPL。
- 不捏造联系邮箱、响应时限、安全通道。不确定的地方写「待定（需用户提供）」。
- `tools/release-scan.ps1` 要能在本地和 CI 中运行，发现问题时返回非零退出码。

## 验收
- 按 `CONTRIBUTING.md` 从干净克隆开始，能在一台没有私人盘符的机器上构建并跑完离线测试。
- CI 工作流能在本地用 `act` 或人工逐步核对（无法实际运行时，说明原因）。
- 所有「需要用户决定」的事项汇总在 `docs/features/release.md` 开头。

## 集成说明需要写
- 需要 Track A 配合的事项：P6-3 版本号来源、P6-4 数据目录、诊断信息脱敏规则、README 公开版的改写要点。
