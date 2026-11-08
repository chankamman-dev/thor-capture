# 2 号包：输入触发核心

只读逻辑与可选桌面快捷键组件；默认关闭，未接主界面，没有设备操作或键盘/掌机按键注入。

## 接口与线程模型

命名空间 `ThorCapture.Features.Triggers`，C#5，仅系统程序集。

- `ComboTrigger(PadButton combo, TimeSpan hold, TimeSpan cooldown[, TimeSpan releaseDebounce])`；`Update(PadState, TimeSpan now)` 返回本次是否触发；`Phase` 为 Idle/Holding/Fired；`Reset()` 清除全部状态。`ComboParser.TryParse/Format` 可处理 `Select+Start` 等大小写不敏感文本。
- 纯同步状态机，调用方单线程串行访问；`now` 必须来自同一个 Stopwatch.Elapsed，不能用墙钟。倒退/负时间/null状态会Reset并拒绝触发。
- 首次启用、断线、重连或 Reset 后，必须看到所有组合成员稳定松开80ms才武装，防止已有按住状态触发。多键顺序按下时，从最后成员按下开始计长按；长持只一次。Holding中任意松开立即重新计时；Fired后任意成员稳定松开80ms才允许下一次，短抖动不重触发。冷却从上次触发计算。
- `GlobalHotkeys` 内部 NativeWindow，必须在 STA UI线程创建/注册/注销/Dispose，调用者须运行正常消息循环。`Pressed` **在拥有者 UI线程触发**，只给注册的 id/keys，延迟的注销/替换消息会被忽略。
- `Register(id, Keys)` 返回 Success/Conflict/Invalid/SystemDenied/Disposed 与中文原因和 native error。同id同键幂等；替换先注册新键，冲突保留原键；`Unregister(id)`、`Dispose` 释放所拥有注册。Windows `MOD_NOREPEAT` 抑制物理长持自动重复。不使用键盘hook、不监听其他键、不发送按键。
- `GlobalHotkeys.TryParse/Format` 支持 Ctrl/Alt/Shift+字母/数字/F1–F24；至少Ctrl或Alt，避免裸字母和Shift影响普通输入。Win/系统键组合不支持；保留窗口内F5/F6由主界面现有路由处理。

这些方法都短时完成，不创建后台任务或自行管理 busy/设备租约；慢截图/录屏要由 Claude 命令路由异步执行。不要在 Pressed 内阻塞 UI。

## Claude 集成需求

建议设置键（本包不写 Settings）：

| 键 | 默认/范围 |
|---|---|
| trigger.comboEnabled | false；输入权限/映射未实机验收时保持实验或关闭 |
| trigger.combo | 空；用户选择至少2个不同键，不预置可能干扰游戏的组合 |
| trigger.holdMs | 600；50–10000 |
| trigger.cooldownMs | 1000；0–60000 |
| trigger.releaseMs | 80；20–500 |
| trigger.hotkeysEnabled | false |
| trigger.captureHotkey / trigger.recordHotkey | 空；仅用户选键后注册 |

设置入口需要：组合选择、长按阈值、冷却、开关、目标动作；快捷键字段和冲突反馈，允许清空/关闭。Idle显示「等待组合键」，Holding「继续按住…」，Fired「已触发，请松开」；首次武装阶段提示「请先松开所选按键」。游戏仍收到这些原始按键，设置页明确说明可能影响游戏，组件不吞键。

输入读取失败、设备切换、禁用、重连立即Reset，禁用不再调用Update。InputMonitor给的是完整状态；长持无新的事件时，Claude需在有连接且输入流健康的时钟tick，用**最近真实原始PadState**调用Update，不用动画插值状态、回放CSV或鼠标演示。断线先清零/Reset；不能凭残留state继续计时。

截图/录屏/窗口F5/F6/全局快捷键进入同一命令路由和 OperationGate。busy时明确拒绝或提示，不堆积待执行动作；录屏中触发截图是否允许由 Track A 当前租约策略决定。核心的触发事件不是执行成功；界面应分别显示动作实际结果。关闭和第二实例必须释放/避免重复注册，初始化失败提示「快捷键已被占用，请改键或关闭」，保留其他窗口内操作可用。

## 验证边界和状态

构造状态/单调时间测试长按、按住启动、抖动、多键顺序、松开、冷却、Reset与非法配置；真实Windows注册一个少用 Ctrl+Alt+Shift+F20–F24组合，两个NativeWindow验证冲突与释放，事件测试仅向自有窗口PostMessage模拟WM_HOTKEY，没有实际按键注入。MOD_NOREPEAT请求已检查；真实物理按键长持和失焦操作仍待手工验收。

2026-09-27：新增39项检查，完整基础188项、`-Stream`260项全部通过，无编译警告。证据 `evidence/20260927-pkg2/base-regression.txt`、`stream-regression.txt`；未连接设备，未实机验收游戏冲突或按键20次触发。没有修改用户正在运行的程序或Settings。

P2-2建议「离线核心完成，界面/实机游戏冲突验收待完成」，需用户同意后5分钟游戏无误触发、20次动作触发；P2-3「核心组件与Windows注册生命周期验证完成，命令路由/忙碌/失焦UI验收待完成」。由 Claude 审查合并并更新 ROADMAP。本包没有读取设备、修改旧输入监控或接入系统键钩子。
