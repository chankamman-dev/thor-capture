# Windows CI 核查记录

2026-09-27。工作流已人工核对，尚无 GitHub hosted runner run URL；没有公开仓库、没有触发外部发布或上传本机产物。当前工作树隐私问题会使 CI 审查门槛明确失败，不能拿编译通过当可公开。

| 核查 | 结果 |
|---|---|
| Windows Framework 编译 | 本地 candidate 编译通过，无警告；系统 compiler、递归源码，无 NuGet |
| 基础/完整合成回归 | 原配置 FFmpeg 6.0，全量 Stream 221 项通过（包含基础检查） |
| scanner 非零失效路径 | 14 项通过；真实历史/当前待审返回1，缺少输入/枚举失败返回2；ZIP重复项/穿越均拒绝 |
| PowerShell兼容 | PowerShell7及Windows PowerShell5.1均完成14项scanner；5.1固定CI依赖准备通过，本次进程执行策略Bypass，不修改全局策略 |
| 干净源码克隆 | 新本地克隆无私人路径配置，按贡献指南构建、149项基础及221项完整Stream通过；不是新电脑/标准用户完整应用验收 |
| hosted STA/UI/音频设备 | 尚未在 Windows runner 运行；不可跳过导致假通过 |
| 固定 FFmpeg CI 依赖 | Gyan 8.1.2 zip 已实际下载/哈希/版本验证，同机完整221项回归通过；未分发依赖 |
| act | 未运行；本地 act 一般不能等同 Windows hosted runner，不伪称其验证 Windows UI |
| 发布作业 | 未配置；无写权限、发布密钥、tag/Release 操作 |

SHA 依据上游一手提交与发行页：

- [checkout 4.2.2 的 release commit](https://github.com/actions/checkout/commit/11bd71901bbe5b1630ceea73d27597364c9af683)
- [upload-artifact 4.6.2](https://github.com/actions/upload-artifact/releases/tag/v4.6.2) 对应 [完整 commit](https://github.com/actions/upload-artifact/commit/ea165f8d65b6e75b540449e92b4886f43607fa02)
- [GitHub 安全实践](https://docs.github.com/en/actions/reference/security/secure-use)：默认只读权限、完整 SHA、PR 不给予秘密。
- [开发依赖构建页](https://www.gyan.dev/ffmpeg/builds/) 和 [固定 zip 官方 SHA-256](https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip.sha256)。测试下载不等于批准产品捆绑，CI 不上传 FFmpeg。

公开前在目标仓库运行真正工作流，检查基础/完整流/STA退出码、失败日志、取消和不泄露产物；任何依赖或版本变更重新固定并核对哈希，不使用浮动 latest。clean checkout 编译不等于 EXE 字节可重现，也不等于 clean standard-user 应用验收。
