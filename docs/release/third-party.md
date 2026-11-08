# 实际第三方审查（2026-09-27）

仅在电脑执行版本查询与构建配置查询，没有启动 ADB 服务、枚举设备或连接 Thor。以下记录不含本机私人安装位置，不能替代用户的分发授权决定。

| 对象 | 实际版本 | SHA-256 | 当前处理 |
|---|---|---|---|
| ADB 可执行文件 | Android Debug Bridge 1.0.41；Platform-Tools 37.0.0-14910828 | `957E46B8615F7AF5B7292A2DDABE98D2E61940C3FB2B0545756507F080613E71` | 电脑外部工具，不在源码/拟发布包中 |
| FFmpeg 可执行文件 | `6.0-essentials_build-www.gyan.dev`；GCC 12.2.0 | `E9FD5E711DEBAB9D680955FC1E38A2C1160FD280B144476CC3F62BC43EF49DB1` | 外部工具，不捆绑；实际启用 GPL/v3 |
| scrcpy server | 3.3.4，未修改官方发行二进制 | `8588238C9A5A00AA542906B6EC7E6D5541D9FFB9B5D0F6E1BC0E365E2303079E` | 已在源码内，当前构建脚本自动复制；需批准分发方案 |
| Windows 系统程序集 / 编译器 | 机器自带 Framework 编译器 | 不是随包分发物 | 不把系统字体/程序集复制入包 |

## ADB

[实际组件源码 NOTICE](https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/NOTICE) 使用 Apache 2.0，但不能据此认定整套 Google Platform-Tools 下载包只有一种许可。[SDK 条款](https://developer.android.com/studio/terms) 3.4/3.5 区分 SDK 的分发限制与开源组件的条款。实际下载包的组件许可证和对应版本源码尚未完整审查；首发建议用户从 [官方入口](https://developer.android.com/tools/releases/platform-tools) 自行配置。查询到版本不等于完成整个 SDK 的再分发审查。

## FFmpeg

实测 `-version` 与 `-buildconf` 一致，配置见 [ffmpeg-buildconf.txt](ffmpeg-buildconf.txt)。`--enable-gpl --enable-version3 --enable-static` 和 libx264/libx265 等说明此二进制不能统称 LGPL。没有 `--enable-nonfree`，仅能说明此显式开关未出现，不能替代所有嵌入组件权属审查。[FFmpeg 官方说明](https://ffmpeg.org/legal.html) 明确可选 GPL 部分影响整个 FFmpeg 的许可。

当前通过外部进程调用 FFmpeg，没有动态/静态链接其库。若捆绑此实际二进制，应查对应构建者 [构建入口](https://www.gyan.dev/ffmpeg/builds/) 的归档、准确对应源码/补丁/构建方式、所有嵌入组件通知，以及适用 GPLv3+ 分发义务；尚未取得这些材料，不批准捆绑。源码获取不应只指向“最新 FFmpeg”。

## scrcpy

[固定版本源码](https://github.com/Genymobile/scrcpy/tree/v3.3.4)、[官方二进制与校验文件](https://github.com/Genymobile/scrcpy/releases/tag/v3.3.4)、[Apache 2.0 许可](https://github.com/Genymobile/scrcpy/blob/v3.3.4/LICENSE) 已核对。仓库已有完整 LICENSE、归属说明和 SHA256SUMS；本包不改服务器，也不包含桌面客户端及其依赖。Apache 2.0 第4节要求相应许可、保留归属/通知、修改标注以及适用的 NOTICE；本项目现有 NOTICE 是组件说明，不能宣称已获商标授权。官方源树的 NOTICE 有无及内容在候选冻结前复核。校验和验证不等于上游数字签名验证。

## 给用户决定的两种方案

1. **建议：ADB/FFmpeg 外置、scrcpy-server 随包**。保留当前系统声音能力，不增加 FFmpeg 包分发；批准 Apache 通知与品牌边界后附 server、LICENSE、NOTICE。应用必须说明将临时上传服务器、收集系统声音而非麦克风、断线残留与清理范围。
2. **全部依赖外置**。减少随包第三方二进制，但 Track A 必须改变当前强制复制 server 的构建/查找机制，为缺 server 的声音提供明确降级；不能仅从 ZIP 删除它而声称声音默认可用。

未来若捆绑 ADB/FFmpeg，先补全对应源码/组件许可审查及哈希清单，另行审批。选择本项目 MIT 或 Apache 2.0 不会消除第三方二进制义务。
