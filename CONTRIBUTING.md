# 贡献指南 / Contributing

当前是公开测试版准备阶段：项目许可证、维护者身份及公开仓库仍待确认。本文不会授予尚未确认的代码或素材许可。英文入口见文末。

## 从干净源码开始

Windows PowerShell 5.1 或 PowerShell 7、Windows 自带 .NET Framework 4.x 编译器及 Git 足够进行基础构建，不需要 Visual Studio、NuGet、私有目录或 ADB。将批准公开的源码克隆到任意可写的短目录后运行：

```powershell
./tools/build-capture-tool.ps1
./tools/test-capture-tool.ps1
```

完整合成视频回归需要用户自行取得 FFmpeg，把其绝对路径放入 `THOR_FFMPEG` 环境变量，再运行 `./tools/test-capture-tool.ps1 -Stream`。此项会解码、编码合成画面，并使用 Windows 窗口/音频输出接口；不连接设备。不要将 `--live` 或 `-Live` 加入默认 CI。编译器找不到、任意检查失败或 STA/窗口不可用都必须报告失败，不能以基础通过替代完整结果。依赖许可见 [third-party](docs/release/third-party.md)。

Windows PowerShell 5.1 若执行策略阻止已审阅的源码脚本，可仅为本次进程运行 `powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-capture-tool.ps1`（测试脚本同样形式）；不需要修改系统全局策略。PowerShell 7 亦已验证。CI 的固定开发依赖可用 `./tools/prepare-release-ci.ps1 -Download` 获取并核对，在同一进程设置 `THOR_FFMPEG`；此下载不打包为产品资产。

## 代码和证据

C# 5，只使用现有系统程序集，不引入 NuGet；PowerShell 含中文时保存 UTF-8 BOM。功能核心与界面分离，后台任务接受取消；进程仅清理自己拥有的实例，原始媒体不覆盖，提交通过临时文件校验后替换。设置、输入 CSV、媒体组格式变化提供迁移和回退说明。

外观按真实硬件比例；固定灯环不能随摇杆移动，十字键与摇杆保持体积、行程和光照。涉及外观时运行 `tools/render-check.ps1 -Window`，明确标识合成输入，不把模拟 FPS 当作实机结果。鼠标按钮仅演示，不向设备注入操作。

每个 PR 描述具体触发场景、结果、风险、所跑检查及未测项。修复只需相关必要验证；涉及录制/原片/进程生命周期时完整离线回归。实机测试必须先获得设备所有者明确同意，记录构建哈希、设备能力与未测项，不能自动提权、安装常驻组件或操作游戏。

## 许可和隐私

贡献者应说明新增代码/素材的来源及许可，不提交官网照片、真实游戏画面、设备序列号、无线地址/配对码、账号、路径配置、日志或个人图库。不要将私人报告粘贴到 Issue。发布审查：`./tools/release-scan.ps1 -Scope WorkingTree`；扫描命中会返回非零，不能简单删除检查来通过。

## English entry

Use Windows, Git and the in-box .NET Framework compiler. Build with `tools/build-capture-tool.ps1`, run `tools/test-capture-tool.ps1`, then configure `THOR_FFMPEG` for `-Stream`. No device is needed. Keep C# 5 compatibility and preserve original media. Disclose provenance and redact private data. Hardware evidence is separate from synthetic checks. The project license and public reporting channels are pending owner approval; no contribution license is implied yet.
