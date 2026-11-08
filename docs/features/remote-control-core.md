# 远程控制核心：离线交付

本批交付 `src/Features/RemoteControl/` 的控制会话、内部命令合同与坐标映射。**没有真实传输实现，也没有控制实机、安装 helper 或修改前端。** `thor.control/1` 是本项目内部语义合同，不是 scrcpy 的二进制协议；后续真实适配器必须验证固定服务器来源、官方协议、实际显示绑定与释放行为后才可以声明支持。

## 接入合同

1. 默认查看/鼠标演示仍不创建控制会话。用户明确启用实时设备控制时，调用 `RemoteControlSession.TryStart(device, operationGate, transport, true, mappingRevision, capacity, out session)`。`Starting` 表示租约已取得、握手后台进行；只有 `Enabled` 为 true 才接受输入。容量 4–1024，建议 64。能力或协议未知、设备不支持、租约忙、旧设备会话均失败关闭。
2. 必须复用现有 `DeviceSessionRegistry` / `OperationGate`；不要自建设备锁。Transport 只拥有本次通道并严格绑定 `ControlBinding.Serial`、session ID、generation、双屏 logical/physical ID、mapping revision。绑定数组返回副本。没有任意命令/ADB shell 字符串入口。
3. `IControlTransport` 提供固定 `Protocol`、已验证 `Support`，以及 `OpenAsync`、`SendAsync`、`CloseAsync`。`OpenAsync` 成功须代表真实协商通过；`SendAsync` 返回与命令 session/generation/revision/sequence 完全匹配的 typed acknowledgment。任何错误/旧响应终止会话。实际 scrcpy 单向写入是否能获得应用层确认必须由适配器明确说明；不能伪造设备执行确认。
4. `Touch(Down/Move/Up,pointer,role,x,y,revision)` 使用原生屏幕像素，最多 10 指。同一指不能重复 down、跨屏、在未 down 时 move/up。`Key(down,androidKey,navigation,revision)` 维护按住状态，拒绝重复 down/孤立 up；导航限定 Back=4、Home=3、Recent=187。SDK gamepad BUTTON 范围明确拒绝，本批不提供模拟摇杆、UHID 或游戏手柄能力。
5. `ControlViewport` 输入实际屏幕内容矩形（排除外壳/黑边），坐标须使用同一 DPI 空间。支持 0/90/180/270° clockwise 预览旋转和原生像素 crop；映射中心、边缘、非数值均受检。布局版本更新后旧 revision 会结束会话。拖出当前屏幕应由前端结束当前控制会话或回到该屏范围后释放，不能根据鼠标所在的另一屏重绑定手势。
6. 用户失焦、鼠标失捕、切图库/回放、切设备、禁用控制、关闭、布局改变时调用 `RequestStop()`，之后 `StopAsync(deadline,token)` 汇聚清理。`RequestStop` 同步禁用/清空待发队列，取消回调后台执行；不会阻塞 UI。当前会话永久结束，重连必须再明确启用。现有外壳点击默认仍只演示，不改鼠标 CSV 或录制 `getevent` 时间线。

## 排队和生命周期

独立 sender 串行写入，不依赖绘制或视频帧率。队列有上限，连续 move 原地替换成最新坐标并保留原序号，不越过该指的 down/up 生命周期边界。down/up/key release 为关键事件：release 先剔除旧 move，关键事件绝不静默丢弃。关键队列仍满时，停止输入、丢弃未发事件、尝试 ReleaseAll 后关闭拥有通道；不把积压拖动继续发送。

握手 5 秒、普通 send 500ms 后请求取消并禁用；若传输忽略取消，保留其在途工作与租约，`StopAsync` 到调用方 deadline 返回 false。串行 in-flight 完成后才发固定 `ReleaseAll`（独立 500ms 取消窗口），再 `CloseAsync`。无法确认关闭时租约继续保持，后续 StopAsync 可重试 Close；不杀设备/外来进程、不按进程名清理。

`CleanupComplete=true` 仅证明适配器确认所拥有控制通道资源结束。`ReleasesConfirmed` 仅在当前会话收到匹配 ReleaseAll acknowledgment 时为 true；断线或切 generation 为 false，即使本地清理成功也不能说实机已松开全部键。真实适配器还须证明服务器关闭/UHID 销毁的释放语义。本批无原始命令、serial、用户路径、输入坐标或异常内容日志。

Transport 实现须让 Close 重试幂等，避免同步阻塞其 async 入口，遵守取消并持有自己的资源直到确认关闭；如果 ReleaseAll 因断线失败仍要关闭。未确认 cleanup 时全局 ShutdownCoordinator 注册组件应返回 false 并保持阻塞；控制与录制/预览之间复用已有 Gate 相容策略，停止录制不得顺带停其他会话。

## 验证与剩余工作

`tools/test-capture-tool.ps1 -Filter RemoteControl` 使用 fake transport，验证默认零调用、未知能力/协议、双屏绑定、数组不可变、重复控制器冲突、坐标/crop/旋转、move 合并、顺序、拥堵、重复键、游戏手柄禁用、取消/超时保留租约、关闭重试、错误响应、布局切代、重连、失焦终结和 send 故障。没有发送真实设备命令。

仍需：Claude 接前端模式/命中/焦点路由与可见状态；固定 scrcpy 控制服务器与传输适配器/官方协议查证；session ownership 与跨程序 broker 控制租约接线；按用户同意开展真实双屏触摸、导航、键盘/长按、不粘键与断线验收。真实模拟摇杆/UHID 支持保持未知且关闭，不能把本批假传输通过称为 RC1–RC3 实机功能完成。
