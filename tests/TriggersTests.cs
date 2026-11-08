using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ThorCapture.Features.Triggers;

namespace ThorCapture
{
    internal static class TriggersTests
    {
        private static readonly PadButton combo = PadButton.Select | PadButton.Start;
        private static PadState State(PadButton buttons) { return new PadState { Buttons = buttons }; }
        private static bool At(ComboTrigger trigger, PadButton buttons, double ms) { return trigger.Update(State(buttons), TimeSpan.FromMilliseconds(ms)); }
        private static ComboTrigger Trigger() { return new ComboTrigger(combo, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500)); }
        private static void Arm(ComboTrigger trigger, double ms) { At(trigger, 0, ms); At(trigger, 0, ms + 80); }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        internal static void Run()
        {
            ComboTrigger trigger = Trigger();
            Tests.Check(!At(trigger, combo, 0) && !At(trigger, combo, 5000) && trigger.Phase == TriggerPhase.Idle, "initial held combo cannot trigger after enable");
            At(trigger, PadButton.Start, 5100); At(trigger, PadButton.Start, 5200);
            Tests.Check(!At(trigger, combo, 5800), "all selected buttons must release to arm after start");
            Arm(trigger, 6000);
            Tests.Check(!At(trigger, combo, 6100) && trigger.Phase == TriggerPhase.Holding && !At(trigger, combo, 6599), "hold waits complete threshold on monotonic clock");
            Tests.Check(At(trigger, combo, 6600) && trigger.Phase == TriggerPhase.Fired, "hold fires once at exact threshold");
            Tests.Check(!At(trigger, combo, 7000) && !At(trigger, combo, 9000), "long hold never repeats even beyond cooldown");
            At(trigger, PadButton.Start, 9100); At(trigger, combo, 9130);
            Tests.Check(!At(trigger, combo, 9700) && trigger.Phase == TriggerPhase.Fired, "short release bounce cannot rearm fired combo");
            At(trigger, PadButton.Start, 9800); At(trigger, PadButton.Start, 9880); At(trigger, combo, 9900);
            Tests.Check(At(trigger, combo, 10400), "stable release of one member allows new hold");
            trigger.Reset(); Arm(trigger, 0); At(trigger, PadButton.Select, 100); At(trigger, PadButton.Select, 800); At(trigger, combo, 900);
            Tests.Check(!At(trigger, combo, 1399) && At(trigger, combo, 1400), "sequential buttons count hold from last member down");
            trigger.Reset(); Arm(trigger, 0); At(trigger, combo, 100); At(trigger, PadButton.Start, 590); At(trigger, combo, 600);
            Tests.Check(!At(trigger, combo, 1099) && At(trigger, combo, 1100), "holding bounce restarts the whole hold duration");
            trigger.Reset(); Arm(trigger, 0); At(trigger, combo, 100); At(trigger, combo, 600); At(trigger, 0, 610); At(trigger, 0, 690); At(trigger, combo, 700);
            Tests.Check(!At(trigger, combo, 1200) && trigger.Phase == TriggerPhase.Holding && At(trigger, combo, 2100), "cooldown delays completed hold until deadline");
            trigger.Reset(); At(trigger, combo, 0); At(trigger, combo, 5000);
            Tests.Check(trigger.Phase == TriggerPhase.Idle && !At(trigger, combo, 6000), "reset disconnect clears and blocks residual held state");
            Arm(trigger, 7000); At(trigger, combo, 7100);
            Tests.Check(!At(trigger, combo, 100) && !At(trigger, combo, 1000) && trigger.Phase == TriggerPhase.Idle, "clock regression fails closed and requires fresh release");
            Tests.Check(!trigger.Update(null, TimeSpan.FromSeconds(2)) && !trigger.Update(State(combo), TimeSpan.FromMilliseconds(-1)), "invalid state or clock resets rather than triggering");
            trigger.Reset(); Arm(trigger, 0); At(trigger, combo | PadButton.A, 100);
            Tests.Check(At(trigger, combo | PadButton.A, 600), "extra buttons do not mutate selected combo or input state");
            PadButton parsed;
            Tests.Check(ComboParser.TryParse(" start + select ", out parsed) && parsed == combo && ComboParser.Format(parsed) == "Select+Start", "combo parser canonical case-insensitive roundtrip");
            Tests.Check(!ComboParser.TryParse("A+A", out parsed) && !ComboParser.TryParse("A", out parsed) && !ComboParser.TryParse("1+2", out parsed) && !ComboParser.TryParse("Up+Down", out parsed), "combo parser rejects duplicates single numeric and opposing directions");
            Tests.Check(!ComboParser.TryParse("Select+", out parsed) && !ComboParser.TryParse(null, out parsed) && ComboParser.Format((PadButton)0x1000000) == "", "combo parser rejects empty or unknown bits");
            Tests.Reject(() => new ComboTrigger(PadButton.A, TimeSpan.FromMilliseconds(500), TimeSpan.Zero), "trigger rejects accidental single key configuration");
            Tests.Reject(() => new ComboTrigger(combo, TimeSpan.Zero, TimeSpan.Zero), "trigger rejects zero hold duration");
            Tests.Reject(() => new ComboTrigger(combo, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(-1)), "trigger rejects negative cooldown");
            Keys key;
            Tests.Check(GlobalHotkeys.TryParse("alt+CTRL+shift+f23", out key) && GlobalHotkeys.Format(key) == "Ctrl+Alt+Shift+F23", "hotkey parser canonical modifiers roundtrip");
            Tests.Check(GlobalHotkeys.TryParse("Ctrl+5", out key) && key == (Keys.Control | Keys.D5) && GlobalHotkeys.Format(key) == "Ctrl+5", "hotkey digit keys roundtrip without enum numeric injection");
            Tests.Check(!GlobalHotkeys.TryParse("Ctrl+Ctrl+A", out key) && !GlobalHotkeys.TryParse("Ctrl+A+B", out key) && !GlobalHotkeys.TryParse("Ctrl+65", out key), "hotkey parser rejects duplicate modifiers multiple keys and numeric enum values");
            Tests.Check(!GlobalHotkeys.TryParse("F5", out key) && !GlobalHotkeys.TryParse("Shift+A", out key) && !GlobalHotkeys.TryParse("Ctrl+LWin", out key) && !GlobalHotkeys.TryParse("Ctrl+", out key), "hotkey parser rejects unmodified text keys reserved and incomplete combinations");
            NativeChecks();
        }
        private static void NativeChecks()
        {
            Keys selected = Keys.None;
            GlobalHotkeys first = new GlobalHotkeys(), second = new GlobalHotkeys();
            try
            {
                for (Keys code = Keys.F20; code <= Keys.F24; code++)
                {
                    Keys candidate = Keys.Control | Keys.Alt | Keys.Shift | code;
                    if (first.Register("capture", candidate).Succeeded) { selected = candidate; break; }
                }
                Tests.Check(selected != Keys.None, "real native RegisterHotKey accepts an uncommon selected shortcut");
                Tests.Check(first.Register("capture", selected).Succeeded, "identical registration is idempotent");
                HotkeyResult conflict = second.Register("record", selected);
                Tests.Check(conflict.Outcome == HotkeyOutcome.Conflict && conflict.NativeError == 1409 && conflict.Detail.Length > 0, "second native receiver reports actual shortcut conflict");
                Tests.Check(first.Register("other", selected).Outcome == HotkeyOutcome.Conflict, "one shortcut cannot silently trigger two commands");
                Tests.Check(first.Register("capture", Keys.A).Outcome == HotkeyOutcome.Invalid, "invalid replacement does not discard active shortcut");
                Keys replacement = Keys.None;
                for (Keys code = Keys.F20; code <= Keys.F24; code++)
                {
                    Keys candidate = Keys.Control | Keys.Alt | Keys.Shift | code;
                    if (candidate != selected && second.Register("other", candidate).Succeeded) { replacement = candidate; break; }
                }
                Tests.Check(replacement != Keys.None && first.Register("capture", replacement).Outcome == HotkeyOutcome.Conflict,
                    "conflicting replacement preserves the working original registration");
                int calls = 0, eventThread = 0;
                first.Pressed += delegate(object sender, HotkeyEventArgs e) { if (e.Id == "capture" && e.Keys == selected) { calls++; eventThread = Thread.CurrentThread.ManagedThreadId; } };
                // Post only to our owned message window; this does not synthesize a keyboard key.
                long param = ((long)(selected & Keys.KeyCode) << 16) | 7;
                Tests.Check(PostMessage(first.Handle, 0x312, new IntPtr(1), new IntPtr(param)), "synthetic hotkey message can target owned receiver without key injection");
                for (int i = 0; i < 10 && calls == 0; i++) { Application.DoEvents(); Thread.Sleep(5); }
                Tests.Check(calls == 1 && eventThread == Thread.CurrentThread.ManagedThreadId, "hotkey event routes only registered id on UI thread");
                PostMessage(first.Handle, 0x312, new IntPtr(999), new IntPtr(param)); Application.DoEvents();
                Tests.Check(calls == 1, "unowned native message id never routes action");
                Tests.Check((int)typeof(GlobalHotkeys).GetField("NoRepeat", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue() == 0x4000, "native registration requests OS no-repeat behavior");
                Tests.Reject(() => Task.Run(() => first.Unregister("capture")).GetAwaiter().GetResult(), "hotkeys reject cross-thread registration lifetime changes");
                Tests.Reject(() => Task.Run(() => new GlobalHotkeys()).GetAwaiter().GetResult(), "hotkey receiver rejects construction outside STA UI thread");
                first.Unregister("capture");
                PostMessage(first.Handle, 0x312, new IntPtr(1), new IntPtr(param)); Application.DoEvents();
                Tests.Check(calls == 1 && second.Register("record", selected).Succeeded, "unregister drops queued events and frees shortcut ownership");
                second.Dispose(); second.Dispose();
                Tests.Check(first.Register("capture", selected).Succeeded, "dispose releases registration and is idempotent");
                first.Dispose();
                Tests.Check(first.Register("capture", selected).Outcome == HotkeyOutcome.Disposed, "registration after disposal returns explicit outcome");
            }
            finally { second.Dispose(); first.Dispose(); }
        }
    }
}
