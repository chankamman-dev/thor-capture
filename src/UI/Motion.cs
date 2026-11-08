using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ThorCapture
{
    internal enum Ease { OutCubic, InOutCubic, OutBack, Linear, Spring }

    // One UI-thread clock drives every running animation, so idle controls cost nothing and
    // all motion shares the same frame. Times come from a monotonic Stopwatch, never the
    // timer tick count, so a slow frame skips ahead instead of stretching the animation.
    internal static class Motion
    {
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static readonly List<Anim> running = new List<Anim>();
        private static readonly List<Action> pulses = new List<Action>();
        private static Timer timer;

        // Global switch for reduced motion; animations jump straight to their target.
        public static bool Enabled = true;

        public static double Now { get { return clock.Elapsed.TotalMilliseconds; } }

        internal static void Start(Anim anim)
        {
            if (!running.Contains(anim)) running.Add(anim);
            EnsureTimer();
        }

        // Continuous per-frame callback (e.g. a breathing record dot) until the returned action is called.
        public static Action Pulse(Action frame)
        {
            pulses.Add(frame);
            EnsureTimer();
            return delegate { pulses.Remove(frame); };
        }

        private static void EnsureTimer()
        {
            if (timer == null)
            {
                timer = new Timer();
                timer.Interval = 15;
                timer.Tick += Tick;
            }
            if (!timer.Enabled) timer.Start();
        }

        private static void Tick(object sender, EventArgs e)
        {
            double now = Now;
            for (int i = running.Count - 1; i >= 0; i--)
            {
                Anim anim = running[i];
                if (!anim.Step(now)) running.RemoveAt(i);
            }
            foreach (Action pulse in pulses.ToArray()) pulse();
            if (running.Count == 0 && pulses.Count == 0) timer.Stop();
        }

        public static float Apply(Ease ease, float t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            switch (ease)
            {
                case Ease.OutCubic: { float u = 1 - t; return 1 - u * u * u; }
                case Ease.InOutCubic: return t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2;
                case Ease.OutBack: { const float c1 = 1.70158f, c3 = c1 + 1; float u = t - 1; return 1 + c3 * u * u * u + c1 * u * u; }
                // Damped spring: one soft overshoot and a small settle, ending exactly at 1.
                case Ease.Spring: return 1 - (float)(Math.Exp(-6 * t) * Math.Cos(2.5 * Math.PI * t));
                default: return t;
            }
        }
    }

    // An animated value owned by a control: set Target and the control repaints each frame
    // until the value settles.
    internal sealed class Anim
    {
        private readonly Control owner;
        private float from, to, value;
        private double start;
        public int Duration;
        public Ease Curve;
        // Optional client-area provider. When set, each frame invalidates the union of the
        // previous and current areas instead of the whole owner, so a toast or one card
        // animating does not repaint everything under it.
        public Func<Rectangle> Area;
        private Rectangle lastArea = Rectangle.Empty;

        public Anim(Control owner, float initial, int duration, Ease curve)
        {
            this.owner = owner;
            from = to = value = initial;
            Duration = duration;
            Curve = curve;
        }

        public float Value { get { return value; } }
        public bool Running { get { return value != to; } }

        public float Target
        {
            get { return to; }
            set
            {
                if (value == to) return;
                if (!Motion.Enabled || owner.IsDisposed || !owner.Visible) { Snap(value); return; }
                from = this.value;
                to = value;
                start = Motion.Now;
                Motion.Start(this);
            }
        }

        // Retargets with an explicit duration (e.g. quick press in, slow release).
        public void To(float target, int duration)
        {
            Duration = duration;
            Target = target;
        }

        public void Snap(float target)
        {
            bool changed = value != target || to != target;
            from = to = value = target;
            if (changed) Invalidate();
        }

        private void Invalidate()
        {
            if (owner.IsDisposed) return;
            if (Area == null) { owner.Invalidate(); return; }
            Rectangle now = Area();
            Rectangle dirty = lastArea.IsEmpty ? now : now.IsEmpty ? lastArea : Rectangle.Union(lastArea, now);
            lastArea = now;
            if (!dirty.IsEmpty) owner.Invalidate(dirty);
        }

        // Restarts from a given value, for one-shot effects such as ripples and flashes.
        public void Play(float fromValue, float target)
        {
            value = fromValue;
            to = fromValue;
            Target = target;
        }

        internal bool Step(double now)
        {
            if (owner.IsDisposed) return false;
            float t = Duration <= 0 ? 1 : (float)((now - start) / Duration);
            value = t >= 1 ? to : from + (to - from) * Motion.Apply(Curve, t);
            Invalidate();
            return t < 1;
        }
    }
}
