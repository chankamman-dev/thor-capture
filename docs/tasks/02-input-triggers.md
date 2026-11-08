# 2 号包：输入触发核心

- **ROADMAP 任务**：P2-2 实机按键组合触发（核心逻辑）、P2-3 桌面全局快捷键（组件）。设置界面、开关和命令路由由 Track A 完成。
- **批次**：第一批。P2-2 的实机验证依赖 1 号包的 P0-3 结论（能不能读到输入）；逻辑和测试可以先做。
- **短名**：`Triggers`；代码 `src/Features/Triggers/`，测试 `tests/TriggersTests.cs`，说明 `docs/features/triggers.md`。

## 目标

1. 在掌机上按住一个组合键时触发一次截图或录屏开关，**只读监听**，不吞掉、也不发送按键。
2. 电脑端注册全局快捷键，窗口不在前台时也能截图或录屏。

## P2-2 组合键触发

读取的是现有 `InputMonitor` 提供的 `PadState`（`src/InputCapture.cs`，只读）。建议接口：

```csharp
internal sealed class ComboTrigger
{
    public ComboTrigger(PadButton combo, TimeSpan hold, TimeSpan cooldown);
    // 每次收到新的输入状态时调用；满足「整组按住 >= hold」时只返回一次 true，
    // 松开任意一个键之后才能再次触发。
    public bool Update(PadState state, TimeSpan now);           // now 用单调时钟（Stopwatch），不用 DateTime.Now
    public void Reset();                                        // 断线、禁用、重连时调用：清空所有进行中的状态
    public TriggerPhase Phase { get; }                          // Idle / Holding / Fired，给界面显示「继续按住…」
}
internal static class ComboParser { public static bool TryParse(string text, out PadButton combo); public static string Format(PadButton combo); }  // "Select+Start"
```

要点：
- **默认关闭**，默认组合由用户选择（ROADMAP 7 节）。
- 长按不能重复触发，每次长按只触发一次；抖动（按下 → 短暂松开 → 再按下）不能误触发。
- 输入断开时必须 `Reset()`，重连后不能因为残留状态误触发。
- 集成说明里要写清楚限制：游戏也会收到这些按键。

## P2-3 全局快捷键

```csharp
internal sealed class GlobalHotkeys : IDisposable     // 允许引用 WinForms：内部用 NativeWindow 接收 WM_HOTKEY
{
    public GlobalHotkeys();                           // 必须在 UI 线程创建
    public HotkeyResult Register(string id, Keys keys);   // 成功，或冲突 / 系统拒绝 + 中文原因
    public void Unregister(string id);
    public event EventHandler<HotkeyEventArgs> Pressed;   // 在 UI 线程触发；按住不放产生的重复消息只报一次
    public static bool TryParse(string text, out Keys keys); public static string Format(Keys keys);  // "Ctrl+Alt+F5"
}
```

要点：
- 只注册用户选定的组合键，**不安装键盘钩子**，不采集其他按键。
- `Dispose` 时注销全部快捷键；进程第二个实例注册同一组合键时，要给出明确的冲突结果。

## 测试
- `ComboTrigger`：用构造的状态序列加可控时间测试长按、抖动、多键顺序按下、松开后再触发、`Reset`、冷却期。
- `ComboParser` / `GlobalHotkeys.TryParse`：往返转换和非法输入。
- `GlobalHotkeys`：在测试里真实注册一个不常用的组合键再注销；重复注册同一组合键得到冲突结果。测试运行在 STA 线程。
- 实机（经同意）：正常玩游戏 5 分钟不误触发，连续 20 次组合键都能触发。

## 集成说明需要写
- 设置项、默认值和取值范围。
- 触发阶段应显示的提示（「继续按住…」「已触发」）。
- 快捷键冲突时的提示文案。
- 录屏中触发截图时应遵循的规则，由 Track A 的 `OperationGate` 决定。
