# 公开内容隐私审查

2026-09-27，范围与扫描证据独立于应用验收。扫描输出只含仓库相对位置/行/规则/提交，不写实际路径值、邮箱、序列号、配对码或凭据。下文按一次本地快照汇总；并行分支和最后集成会改变可达历史，冻结时必须重新扫描。

## 结论

**当前仓库不能直接公开。** README 的私人路径/设备标识和 AGENTS 的本机路径/作者联系方式需由 Track A 处理；历史中仍有旧内容，`.gitignore` 不会删除它。未发现可据此确认的 token/私钥模式，但扫描没有命中不证明没有秘密。实际构建 EXE 的盘符模式曾因二进制解码噪声误报，改为可打印字符串后没有相应私人路径命中，不能将其误报当真实泄漏。

实际构建目录仅含 EXE、server、server LICENSE/NOTICE，缺少 allowlist 的最终许可/快速开始/manifest 等；这是**构建输出审查，非 P7-6 最终候选 ZIP**，不能伪称完成包发布验收。server 要人工核来源；EXE 中版本/IP等保守匹配仍需人工判定。不会为凑齐包而虚构 LICENSE。

## 三个范围

- WorkingTree：`git ls-files --cached --others --exclude-standard`，没有扫描忽略的私人图库/照片/日志；这类文件全部排除公开。没有修改用户媒体或配置。
- History：`git rev-list --all` 的全部可达提交、所有不同 path/blob 和提交作者/消息；不是 reflog/不可达对象或外部远程的穷尽审查。旧提交作者邮箱需要确认公开身份，不能自动当秘密清除，也不能直接发布未经确认的身份。
- Package：实际临时构建目录，与精确 allowlist 比较并扫描 EXE 可打印 UTF-8/UTF-16 字符串。不解压/执行候选文件；ZIP 输入另有遍历、重复项和缺失项检查。

媒体：当前跟踪媒体为合成单色 MP4；自动扫描只标记人工复核，不声称已经完成 EXIF/视频内容权限检查。用户照片、游戏画面和输入 CSV 不在 proposed allowlist；未来加入任何示例必须人工核媒体内容/EXIF及授权。任意 ZIP/服务器/程序集的深度嵌入内容不由字符串扫描证明安全。

## 人工分类和后续

私人路径/实际设备信息需修改公共文案及必要历史（另行批准）。`tools/release-scan.ps1` 中的路径检测正则、`tools/test-release-scan.ps1` 的保留 example 测试邮箱、`src/RecordingAudio.cs` 的 loopback、代码里的短 hex/合成数据与已核哈希不是直接确认的秘密；工具仍返回待审，不能无依据统一忽略整个目录。最终门槛可由维护者逐项核对并形成有版本/内容哈希约束的审查记录，真实隐私项不得以误报为由放行。

建议保留私有原库，所有者批准后选择干净初始公开源码或经审阅的历史清理，保留贡献/来源记录；本包没有重写历史、推送、创建远程或发布。最终 ZIP 应只从精确 allowlist 组装，并对它本身重新扫描，而非复用当前输出报告。

## 位置快照

下表由本轮实际扫描报告生成，行号仅定位，**没有匹配内容**。历史提交以完整哈希标记；binary 行号指提取字符串的逻辑位置。扫描报告和原始回归证据在忽略的 `evidence/20260927-pkg6/`，不公开上传完整测试输出。

| 范围 | 输入数 | 待审位置数 |
|---|---:|---:|
| worktree | 92 | 13 |
| history | 107 | 34 |
| package | 4 | 14 |

| 范围 | 相对文件 | 行 | 规则 | 提交 |
|---|---|---:|---|---|
| worktree | `AGENTS.md` | 69 | email-review |  |
| worktree | `AGENTS.md` | 69 | private-drive-path |  |
| worktree | `docs/tasks/README.md` | 68 | private-drive-path |  |
| worktree | `README.md` | 5 | private-drive-path |  |
| worktree | `README.md` | 112 | private-drive-path |  |
| worktree | `src/MainForm.cs` | 58 | device-serial-review |  |
| worktree | `src/RecordingAudio.cs` | 114 | network-address-review |  |
| worktree | `tests/fixtures/recording-preview.mp4` | 0 | media-manual-review |  |
| worktree | `tests/Tests.cs` | 472 | device-serial-review |  |
| worktree | `third-party/scrcpy/scrcpy-server-v3.3.4` | 0 | binary-provenance-manual-review |  |
| worktree | `tools/release-scan.ps1` | 14 | private-user-path |  |
| worktree | `tools/test-release-scan.ps1` | 31 | email-review |  |
| worktree | `tools/test-release-scan.ps1` | 35 | email-review |  |
| history | `AGENTS.md` | 54 | email-review | cee53c7d432b387ab84b2d9408cf9519aaef3d70 |
| history | `AGENTS.md` | 54 | private-drive-path | cee53c7d432b387ab84b2d9408cf9519aaef3d70 |
| history | `AGENTS.md` | 69 | email-review | 0491be51c91ed9648d4069a78d51621ae50b6472 |
| history | `AGENTS.md` | 69 | private-drive-path | 0491be51c91ed9648d4069a78d51621ae50b6472 |
| history | `docs/tasks/README.md` | 68 | private-drive-path | 0491be51c91ed9648d4069a78d51621ae50b6472 |
| history | `git-commit-metadata` | 2 | email-review | 01537ee87437b57ab1f0df87bd5ede446f8807bd |
| history | `git-commit-metadata` | 2 | email-review | 0491be51c91ed9648d4069a78d51621ae50b6472 |
| history | `git-commit-metadata` | 2 | email-review | 70b1e668569eee6e3013c7ede76370b170a30c05 |
| history | `git-commit-metadata` | 2 | email-review | 8a8da24187bc88d127356a4056a352d46f2215e1 |
| history | `git-commit-metadata` | 2 | email-review | cee53c7d432b387ab84b2d9408cf9519aaef3d70 |
| history | `git-commit-metadata` | 2 | email-review | faaf1463c2331e854c308e07afad85c4d156bdbd |
| history | `git-commit-metadata` | 4 | email-review | 01537ee87437b57ab1f0df87bd5ede446f8807bd |
| history | `git-commit-metadata` | 4 | email-review | 0491be51c91ed9648d4069a78d51621ae50b6472 |
| history | `git-commit-metadata` | 4 | email-review | 70b1e668569eee6e3013c7ede76370b170a30c05 |
| history | `git-commit-metadata` | 4 | email-review | 8a8da24187bc88d127356a4056a352d46f2215e1 |
| history | `git-commit-metadata` | 4 | email-review | cee53c7d432b387ab84b2d9408cf9519aaef3d70 |
| history | `git-commit-metadata` | 4 | email-review | faaf1463c2331e854c308e07afad85c4d156bdbd |
| history | `git-commit-metadata` | 7 | email-review | 0491be51c91ed9648d4069a78d51621ae50b6472 |
| history | `git-commit-metadata` | 11 | email-review | 8a8da24187bc88d127356a4056a352d46f2215e1 |
| history | `git-commit-metadata` | 16 | email-review | cee53c7d432b387ab84b2d9408cf9519aaef3d70 |
| history | `git-commit-metadata` | 23 | email-review | 01537ee87437b57ab1f0df87bd5ede446f8807bd |
| history | `README.md` | 5 | private-drive-path | 0491be51c91ed9648d4069a78d51621ae50b6472 |
| history | `README.md` | 5 | private-drive-path | 8a8da24187bc88d127356a4056a352d46f2215e1 |
| history | `README.md` | 5 | private-drive-path | cee53c7d432b387ab84b2d9408cf9519aaef3d70 |
| history | `README.md` | 53 | private-drive-path | 8a8da24187bc88d127356a4056a352d46f2215e1 |
| history | `README.md` | 58 | private-drive-path | cee53c7d432b387ab84b2d9408cf9519aaef3d70 |
| history | `README.md` | 112 | private-drive-path | 0491be51c91ed9648d4069a78d51621ae50b6472 |
| history | `src/MainForm.cs` | 58 | device-serial-review | 0491be51c91ed9648d4069a78d51621ae50b6472 |
| history | `src/MainForm.cs` | 58 | device-serial-review | cee53c7d432b387ab84b2d9408cf9519aaef3d70 |
| history | `src/RecordingAudio.cs` | 114 | network-address-review | 0491be51c91ed9648d4069a78d51621ae50b6472 |
| history | `tests/CapabilityTests.cs` | 80 | network-address-review | 70b1e668569eee6e3013c7ede76370b170a30c05 |
| history | `tests/fixtures/recording-preview.mp4` | 0 | historical-binary-manual-review | 0491be51c91ed9648d4069a78d51621ae50b6472 |
| history | `tests/Tests.cs` | 472 | device-serial-review | 0491be51c91ed9648d4069a78d51621ae50b6472 |
| history | `third-party/scrcpy/scrcpy-server-v3.3.4` | 0 | historical-binary-manual-review | 0491be51c91ed9648d4069a78d51621ae50b6472 |
| package | `LICENSE` | 0 | required-package-file-missing |  |
| package | `QUICKSTART.en.md` | 0 | required-package-file-missing |  |
| package | `QUICKSTART.zh-CN.md` | 0 | required-package-file-missing |  |
| package | `release-manifest.json` | 0 | required-package-file-missing |  |
| package | `scrcpy-server-v3.3.4` | 0 | binary-provenance-manual-review |  |
| package | `THIRD-PARTY-NOTICES.md` | 0 | required-package-file-missing |  |
| package | `ThorCapture.exe` | 82344 | network-address-review |  |
| package | `ThorCapture.exe` | 88731 | device-serial-review |  |
| package | `ThorCapture.exe` | 88741 | network-address-review |  |
| package | `ThorCapture.exe` | 88757 | network-address-review |  |
| package | `ThorCapture.exe` | 88761 | network-address-review |  |
| package | `ThorCapture.exe` | 141650 | device-serial-review |  |
| package | `ThorCapture.exe` | 159592 | device-serial-review |  |
| package | `ThorCapture.exe` | 165631 | network-address-review |  |
