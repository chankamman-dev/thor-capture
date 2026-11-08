using System;
using System.Collections.Generic;

namespace ThorCapture.Features.Triggers
{
    internal enum TriggerPhase { Idle, Holding, Fired }
    // Pure state machine: no device calls, timers, hooks or operation routing.
    internal sealed class ComboTrigger
    {
        private readonly PadButton combo;
        private readonly TimeSpan hold, cooldown, releaseDebounce;
        private bool initialized, armed, releasing, hasFired;
        private TimeSpan previous, holdStart, releaseStart, lastFire;
        public TriggerPhase Phase { get; private set; }
        public ComboTrigger(PadButton combo, TimeSpan hold, TimeSpan cooldown)
            : this(combo, hold, cooldown, TimeSpan.FromMilliseconds(80)) { }
        public ComboTrigger(PadButton combo, TimeSpan hold, TimeSpan cooldown, TimeSpan releaseDebounce)
        {
            if (!ComboParser.IsValid(combo)) throw new ArgumentException("At least two distinct valid buttons are required", "combo");
            if (hold.TotalMilliseconds < 50 || hold.TotalMilliseconds > 10000) throw new ArgumentOutOfRangeException("hold");
            if (cooldown.TotalMilliseconds < 0 || cooldown.TotalMilliseconds > 60000) throw new ArgumentOutOfRangeException("cooldown");
            if (releaseDebounce.TotalMilliseconds < 20 || releaseDebounce.TotalMilliseconds > 500) throw new ArgumentOutOfRangeException("releaseDebounce");
            this.combo = combo; this.hold = hold; this.cooldown = cooldown; this.releaseDebounce = releaseDebounce;
            Reset();
        }
        public void Reset()
        {
            initialized = armed = releasing = hasFired = false;
            previous = holdStart = releaseStart = lastFire = TimeSpan.Zero;
            Phase = TriggerPhase.Idle;
        }
        public bool Update(PadState state, TimeSpan now)
        {
            if (state == null || now < TimeSpan.Zero || (initialized && now < previous)) { Reset(); return false; }
            bool first = !initialized;
            initialized = true; previous = now;
            bool all = (state.Buttons & combo) == combo;
            bool none = (state.Buttons & combo) == 0;
            // On creation/reset every selected button must be released stably before arming.
            if (!armed)
            {
                if (!none) { releasing = false; return false; }
                if (!releasing || first) { releaseStart = now; releasing = true; }
                if (now - releaseStart >= releaseDebounce) { armed = true; releasing = false; }
                return false;
            }
            if (Phase == TriggerPhase.Fired)
            {
                if (all) { releasing = false; return false; }
                if (!releasing) { releaseStart = now; releasing = true; }
                if (now - releaseStart >= releaseDebounce) { Phase = TriggerPhase.Idle; releasing = false; }
                return false;
            }
            if (!all) { Phase = TriggerPhase.Idle; return false; }
            if (Phase == TriggerPhase.Idle) { holdStart = now; Phase = TriggerPhase.Holding; }
            if (now - holdStart < hold || (hasFired && now - lastFire < cooldown)) return false;
            Phase = TriggerPhase.Fired; lastFire = now; hasFired = true;
            return true;
        }
    }
    internal static class ComboParser
    {
        private static readonly PadButton[] ordered = { PadButton.Select, PadButton.Start, PadButton.L1, PadButton.R1,
            PadButton.L2, PadButton.R2, PadButton.L3, PadButton.R3, PadButton.A, PadButton.B, PadButton.X, PadButton.Y,
            PadButton.Up, PadButton.Down, PadButton.Left, PadButton.Right, PadButton.Home, PadButton.Back };
        internal static bool IsValid(PadButton combo)
        {
            int bits = (int)combo, allowed = 0, count = 0;
            foreach (PadButton button in ordered) { allowed |= (int)button; if ((combo & button) != 0) count++; }
            return bits > 0 && (bits & ~allowed) == 0 && count >= 2 &&
                (combo & (PadButton.Up | PadButton.Down)) != (PadButton.Up | PadButton.Down) &&
                (combo & (PadButton.Left | PadButton.Right)) != (PadButton.Left | PadButton.Right);
        }
        public static bool TryParse(string text, out PadButton combo)
        {
            combo = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (string part in text.Split('+'))
            {
                bool found = false;
                foreach (PadButton button in ordered)
                {
                    if (!string.Equals(part.Trim(), button.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
                    if ((combo & button) != 0) { combo = 0; return false; }
                    combo |= button; found = true; break;
                }
                if (!found) { combo = 0; return false; }
            }
            if (!IsValid(combo)) { combo = 0; return false; }
            return true;
        }
        public static string Format(PadButton combo)
        {
            if (!IsValid(combo)) return "";
            List<string> names = new List<string>();
            foreach (PadButton button in ordered) if ((combo & button) != 0) names.Add(button.ToString());
            return string.Join("+", names);
        }
    }
}
