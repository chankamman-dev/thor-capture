# 架构说明草案 / Architecture draft

本文件区分当前源码与 ROADMAP 目标。功能任务包尚未集成时不能描述为已启用。

2026-09-27后端当前层：`App/Backend`已提供会话/租约/作业/拥有进程/关闭/诊断/设置；`Media`已有稳定UUID仓库、后台目录监视组合、checksum备份和原子PNG发布；`Library`已有受管理Node生命周期、导入/缩略图/编辑/游戏关联客户端；`Features`提供设备监视、捕获连拍、图库事务与视频导出核心。Node编辑/只读拉取仍为独立组件。旧MainForm状态和Settings writer尚需前端迁移，旧长视频owner保留，不把新层存在说成所有业务已迁移。最新接口和边界见 [BACKEND-COMPLETION.md](BACKEND-COMPLETION.md)；以下“目标与集成”的旧归属仅是原设计历史。

## 当前边界

`MainForm*.cs` 承担 Windows UI、连接、图库和录制协调；`Settings` 位于 MainForm，平面文件放 EXE 旁。`ThorCapture.cs/Adb` 查找设备工具、执行外部命令并解析屏幕；显示操作使用物理 display ID，逻辑 ID 仅用于上下屏角色识别。

`RecordingSession` 并发启动单/双屏 `Recording`，共享批次和输入时间线；原生 MP4 先拉取 `.partial`、校验再移动，失败保留可恢复原片。`RecordingAudio` 固定哈希验证的 scrcpy server 获取系统 PCM，`MediaAudio` 合成一份声音；不录麦克风、不注入控制。

`LiveVideo` 每屏 H.264 经外部 FFmpeg 解码为最新帧邮箱，过期帧替换，流停止清理拥有的 PID。当前录屏期间兼容取景是连续截图，并非同一高帧率视频流。预览送显 FPS 不代表源端 FPS 或端到端延迟。

`VideoPlayback` 一次解码两屏到共享帧，单调时钟、revision 拒绝旧跳转帧。`PcmOutput` 本地 waveOut，暂停/跳转清缓冲，双屏声音不叠加。`VideoExport` 合成带壳画面与记录输入；鼠标 `ShellPointer` 只用于本地演示。

`DeviceArt`、立体按键与灯光生成外壳，`ControlLayers` 缓存局部控件；灯环固定，只有杆帽/轴杆移动。`PreviewBox` 管理帧所有权、局部刷新与动画时钟。`Log` 写本地日志，当前异常文本尚需统一脱敏和保留策略，不能声称日志已自动安全。

## 目标与集成

后端由Codex维护上述真实接口；功能核心接受共享会话、仓库与协调器，不自建第二套资源域。UI、外壳渲染与前端集成由Claude维护。共享事件后台派发、generation/CAS和清理ack是接入合同的一部分；原界面busy仍须逐流程替换。

发布材料属于文档/审查工具，不持有设备或后台任务。扫描只返回相对位置和规则；不能证明法律许可、没有任何秘密或硬件能力。线程/数据/版本的最终说明必须随集成源码更新。

English: MainForm currently coordinates UI and operations. Capture, recording, playback, audio and software-rendered housing use distinct ownership boundaries. Future process, media and operation services are plans until merged. Offline fixtures do not prove real-device support.
