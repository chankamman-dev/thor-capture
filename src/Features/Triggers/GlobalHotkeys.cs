using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ThorCapture.Features.Triggers
{
    internal enum HotkeyOutcome { Success, Conflict, Invalid, SystemDenied, Disposed }
    internal sealed class HotkeyResult
    {
        public HotkeyOutcome Outcome;
        public string Detail;
        public int NativeError;
        public bool Succeeded { get { return Outcome == HotkeyOutcome.Success; } }
    }
    internal sealed class HotkeyEventArgs : EventArgs
    {
        public string Id { get; private set; }
        public Keys Keys { get; private set; }
        internal HotkeyEventArgs(string id, Keys keys) { Id = id; Keys = keys; }
    }
    // Must be created, changed and disposed on the owning UI thread with a message loop.
    internal sealed class GlobalHotkeys : NativeWindow, IDisposable
    {
        private const int WmHotkey = 0x0312, NoRepeat = 0x4000;
        private readonly int threadId;
        private readonly Dictionary<string, Registration> registrations = new Dictionary<string, Registration>(StringComparer.Ordinal);
        private int nextId = 1;
        private bool disposed;
        private sealed class Registration { public int NativeId; public Keys Keys; }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr handle, int id);
        /// <summary>Raised on the owning UI thread, never on a background thread.</summary>
        public event EventHandler<HotkeyEventArgs> Pressed;
        public GlobalHotkeys()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Hotkeys require the STA UI thread");
            threadId = Thread.CurrentThread.ManagedThreadId;
            CreateHandle(new CreateParams { Caption = "ThorCapture hotkey receiver", Parent = new IntPtr(-3) });
        }
        private void RequireThread() { if (Thread.CurrentThread.ManagedThreadId != threadId) throw new InvalidOperationException("Hotkeys must be used on their owning UI thread"); }
        public HotkeyResult Register(string id, Keys keys)
        {
            RequireThread();
            if (disposed) return Result(HotkeyOutcome.Disposed, "快捷键组件已关闭", 0);
            if (string.IsNullOrWhiteSpace(id) || !Valid(keys)) return Result(HotkeyOutcome.Invalid, "请选择 Ctrl 或 Alt 加字母、数字或功能键", 0);
            Registration previous;
            if (registrations.TryGetValue(id, out previous) && previous.Keys == keys) return Result(HotkeyOutcome.Success, "快捷键已启用", 0);
            if (nextId > 0xBFFF) return Result(HotkeyOutcome.SystemDenied, "快捷键注册编号已用尽，请关闭并重新建立组件", 0);
            int nativeId = nextId++;
            uint modifiers = NoRepeat;
            if ((keys & Keys.Control) != 0) modifiers |= 2;
            if ((keys & Keys.Alt) != 0) modifiers |= 1;
            if ((keys & Keys.Shift) != 0) modifiers |= 4;
            if (!RegisterHotKey(Handle, nativeId, modifiers, (uint)(keys & Keys.KeyCode)))
            {
                int error = Marshal.GetLastWin32Error();
                Log.Warn("Hotkey register outcome=" + (error == 1409 ? "Conflict" : "SystemDenied") + " nativeError=" + error);
                return Result(error == 1409 ? HotkeyOutcome.Conflict : HotkeyOutcome.SystemDenied,
                    error == 1409 ? "快捷键已被其他程序或另一个命令占用，请改键或关闭该快捷键" : "系统拒绝注册快捷键，请选择其他组合键", error);
            }
            // Register replacement first: failure preserves the old working shortcut.
            if (previous != null) UnregisterHotKey(Handle, previous.NativeId);
            registrations[id] = new Registration { NativeId = nativeId, Keys = keys };
            return Result(HotkeyOutcome.Success, "快捷键已启用", 0);
        }
        public void Unregister(string id)
        {
            RequireThread();
            if (disposed || id == null) return;
            Registration registration;
            if (!registrations.TryGetValue(id, out registration)) return;
            UnregisterHotKey(Handle, registration.NativeId);
            registrations.Remove(id);
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotkey && !disposed)
            {
                string id = null; Registration found = null;
                foreach (KeyValuePair<string, Registration> entry in registrations)
                    if (entry.Value.NativeId == message.WParam.ToInt32()) { id = entry.Key; found = entry.Value; break; }
                // Ignore delayed messages from disposed, unregistered or replaced shortcuts.
                if (found != null && ((long)message.LParam & 0xFFFF0000L) >> 16 == (int)(found.Keys & Keys.KeyCode))
                {
                    EventHandler<HotkeyEventArgs> handler = Pressed;
                    if (handler != null) handler(this, new HotkeyEventArgs(id, found.Keys));
                }
                return;
            }
            base.WndProc(ref message);
        }
        public void Dispose()
        {
            RequireThread();
            if (disposed) return;
            disposed = true;
            foreach (Registration registration in registrations.Values) UnregisterHotKey(Handle, registration.NativeId);
            registrations.Clear(); Pressed = null; DestroyHandle();
        }
        private static HotkeyResult Result(HotkeyOutcome outcome, string text, int error) { return new HotkeyResult { Outcome = outcome, Detail = text, NativeError = error }; }
        private static bool Valid(Keys keys)
        {
            Keys code = keys & Keys.KeyCode;
            if ((keys & ~(Keys.KeyCode | Keys.Control | Keys.Alt | Keys.Shift)) != 0 || (keys & (Keys.Control | Keys.Alt)) == 0) return false;
            return (code >= Keys.A && code <= Keys.Z) || (code >= Keys.D0 && code <= Keys.D9) || (code >= Keys.F1 && code <= Keys.F24);
        }
        public static bool TryParse(string text, out Keys keys)
        {
            keys = Keys.None;
            if (string.IsNullOrWhiteSpace(text)) return false;
            bool hasKey = false;
            foreach (string part in text.Split('+'))
            {
                string value = part.Trim(); Keys modifier = Keys.None;
                if (string.Equals(value, "Ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Control", StringComparison.OrdinalIgnoreCase)) modifier = Keys.Control;
                else if (string.Equals(value, "Alt", StringComparison.OrdinalIgnoreCase)) modifier = Keys.Alt;
                else if (string.Equals(value, "Shift", StringComparison.OrdinalIgnoreCase)) modifier = Keys.Shift;
                if (modifier != Keys.None)
                {
                    if ((keys & modifier) != 0) { keys = Keys.None; return false; }
                    keys |= modifier; continue;
                }
                Keys code;
                if (value.Length == 1 && value[0] >= '0' && value[0] <= '9') code = Keys.D0 + (value[0] - '0');
                else if (!Enum.TryParse<Keys>(value, true, out code) || code.ToString().IndexOf(',') >= 0 || value.Length == 0 || char.IsDigit(value[0])) { keys = Keys.None; return false; }
                if (hasKey || (code & Keys.Modifiers) != 0) { keys = Keys.None; return false; }
                keys |= code; hasKey = true;
            }
            if (!hasKey || !Valid(keys)) { keys = Keys.None; return false; }
            return true;
        }
        public static string Format(Keys keys)
        {
            if (!Valid(keys)) return "";
            List<string> parts = new List<string>();
            if ((keys & Keys.Control) != 0) parts.Add("Ctrl");
            if ((keys & Keys.Alt) != 0) parts.Add("Alt");
            if ((keys & Keys.Shift) != 0) parts.Add("Shift");
            Keys code = keys & Keys.KeyCode;
            parts.Add(code >= Keys.D0 && code <= Keys.D9 ? ((int)(code - Keys.D0)).ToString() : code.ToString());
            return string.Join("+", parts);
        }
    }
}
