// The app's mascot: a small, soft Thor with a face on its upper screen. It is drawn in
// the monochrome UI greys and carries the personality of the app: it naps when no
// device is connected, idles (blinks and bobs) in empty views and hops after a capture.
// Motion follows squash-and-stretch: the body compresses on landing and stretches in
// the air, always around its feet.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ThorCapture
{
    internal enum MascotMood { Idle, Sleep, Happy }

    internal static class Mascot
    {
        private static readonly Color Body = Color.FromArgb(186, 184, 180);
        private static readonly Color Shade = Color.FromArgb(166, 164, 160);
        private static readonly Color Screen = Color.FromArgb(250, 249, 247);
        private static readonly Color Feature = Color.FromArgb(84, 83, 81);

        // Draws the mascot into a square area. `ms` is a free-running clock; pass 0 for a still pose.
        public static void Draw(Graphics g, RectangleF area, MascotMood mood, double ms)
        {
            float size = Math.Min(area.Width, area.Height);
            float u = size / 100f;
            float cx = area.X + area.Width / 2, floor = area.Y + (area.Height + size) / 2 - 4 * u;
            double t = ms / 1000.0;

            float lift = 0, squash = 0;
            if (mood == MascotMood.Idle)
            {
                // Slow breathing bob.
                double phase = Math.Sin(t * Math.PI * 2 / 2.6);
                lift = (float)(Math.Max(0, phase) * 2.5);
                squash = (float)(-phase * 0.015);
            }
            else if (mood == MascotMood.Sleep)
            {
                squash = (float)(Math.Sin(t * Math.PI * 2 / 3.4) * 0.03);
            }
            else
            {
                // Hop cycle: crouch, jump (stretch), land (squash), rest.
                double cycle = (t % 1.1) / 1.1;
                if (cycle < 0.15) squash = (float)(Math.Sin(cycle / 0.15 * Math.PI) * 0.1);
                else if (cycle < 0.6) { double air = (cycle - 0.15) / 0.45; lift = (float)(Math.Sin(air * Math.PI) * 14); squash = -0.07f * (float)Math.Sin(air * Math.PI); }
                else if (cycle < 0.75) squash = (float)(Math.Sin((cycle - 0.6) / 0.15 * Math.PI) * 0.12);
            }

            // Shadow shrinks as the body lifts.
            float shadowW = (62 - lift * 1.4f) * u;
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb((int)(30 - lift), 0, 0, 0)))
                g.FillEllipse(shadow, cx - shadowW / 2, floor - 3 * u, shadowW, 6 * u);

            GraphicsState state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(cx, floor - lift * u);
            g.ScaleTransform(1 + squash, 1 - squash);
            g.TranslateTransform(-50 * u, -96 * u);
            DrawBody(g, u, mood, t);
            g.Restore(state);

            if (mood == MascotMood.Sleep) DrawSleep(g, cx + 26 * u, floor - 88 * u, u, t);
        }

        private static void DrawBody(Graphics g, float u, MascotMood mood, double t)
        {
            using (SolidBrush body = new SolidBrush(Body))
            using (SolidBrush shade = new SolidBrush(Shade))
            using (SolidBrush screen = new SolidBrush(Screen))
            using (SolidBrush feature = new SolidBrush(Feature))
            {
                // Lower half with two sticks and a small screen.
                using (GraphicsPath basePath = Ui.Round(new RectangleF(8 * u, 54 * u, 84 * u, 42 * u), 16 * u)) g.FillPath(shade, basePath);
                using (GraphicsPath hinge = Ui.Round(new RectangleF(20 * u, 49 * u, 60 * u, 9 * u), 4 * u)) g.FillPath(body, hinge);
                g.FillEllipse(body, 17 * u, 64 * u, 14 * u, 14 * u);
                g.FillEllipse(body, 69 * u, 64 * u, 14 * u, 14 * u);
                using (GraphicsPath low = Ui.Round(new RectangleF(37 * u, 61 * u, 26 * u, 22 * u), 4 * u)) g.FillPath(screen, low);

                // Upper lid with the face.
                using (GraphicsPath lid = Ui.Round(new RectangleF(12 * u, 4 * u, 76 * u, 48 * u), 14 * u)) g.FillPath(body, lid);
                using (GraphicsPath face = Ui.Round(new RectangleF(19 * u, 10 * u, 62 * u, 36 * u), 8 * u)) g.FillPath(screen, face);

                float ey = 27 * u, lx = 39 * u, rx = 61 * u;
                using (Pen stroke = new Pen(Feature, 2.6f * u))
                {
                    stroke.StartCap = stroke.EndCap = LineCap.Round;
                    stroke.LineJoin = LineJoin.Round;
                    if (mood == MascotMood.Sleep)
                    {
                        g.DrawArc(stroke, lx - 4.5f * u, ey - 3 * u, 9 * u, 6 * u, 20, 140);
                        g.DrawArc(stroke, rx - 4.5f * u, ey - 3 * u, 9 * u, 6 * u, 20, 140);
                    }
                    else if (mood == MascotMood.Happy)
                    {
                        g.DrawLines(stroke, new PointF[] { new PointF(lx - 4 * u, ey + 2 * u), new PointF(lx, ey - 2.5f * u), new PointF(lx + 4 * u, ey + 2 * u) });
                        g.DrawLines(stroke, new PointF[] { new PointF(rx - 4 * u, ey + 2 * u), new PointF(rx, ey - 2.5f * u), new PointF(rx + 4 * u, ey + 2 * u) });
                    }
                    else
                    {
                        // Blink for 130 ms every 3.4 s.
                        bool blink = (t % 3.4) > 3.27;
                        if (blink)
                        {
                            g.DrawLine(stroke, lx - 3.5f * u, ey, lx + 3.5f * u, ey);
                            g.DrawLine(stroke, rx - 3.5f * u, ey, rx + 3.5f * u, ey);
                        }
                        else
                        {
                            g.FillEllipse(feature, lx - 3.4f * u, ey - 4 * u, 6.8f * u, 8 * u);
                            g.FillEllipse(feature, rx - 3.4f * u, ey - 4 * u, 6.8f * u, 8 * u);
                        }
                    }
                    // Small mouth.
                    using (Pen mouth = new Pen(Feature, 2f * u))
                    {
                        mouth.StartCap = mouth.EndCap = LineCap.Round;
                        if (mood == MascotMood.Happy) g.DrawArc(mouth, 46 * u, 31 * u, 8 * u, 6 * u, 0, 180);
                        else g.DrawArc(mouth, 47 * u, 33 * u, 6 * u, 3.5f * u, 20, 140);
                    }
                }
            }
        }

        // Just the face, for drawing on the Thor preview's own empty upper screen.
        public static void DrawFace(Graphics g, RectangleF screen, MascotMood mood, double ms, Color ink)
        {
            double t = ms / 1000.0;
            float u = screen.Height / 100f;
            float bob = mood == MascotMood.Idle ? (float)(Math.Sin(t * Math.PI * 2 / 2.6) * 1.2) * u : 0;
            float cx = screen.X + screen.Width / 2, ey = screen.Y + screen.Height * 0.46f + bob, gap = 17 * u;
            GraphicsState state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen stroke = new Pen(ink, 3.2f * u))
            using (SolidBrush fill = new SolidBrush(ink))
            {
                stroke.StartCap = stroke.EndCap = LineCap.Round;
                stroke.LineJoin = LineJoin.Round;
                if (mood == MascotMood.Sleep)
                {
                    g.DrawArc(stroke, cx - gap - 6 * u, ey - 4 * u, 12 * u, 8 * u, 20, 140);
                    g.DrawArc(stroke, cx + gap - 6 * u, ey - 4 * u, 12 * u, 8 * u, 20, 140);
                }
                else if (mood == MascotMood.Happy)
                {
                    foreach (float x in new float[] { cx - gap, cx + gap })
                        g.DrawLines(stroke, new PointF[] { new PointF(x - 5 * u, ey + 3 * u), new PointF(x, ey - 3 * u), new PointF(x + 5 * u, ey + 3 * u) });
                }
                else if ((t % 3.4) > 3.27)
                {
                    g.DrawLine(stroke, cx - gap - 5 * u, ey, cx - gap + 5 * u, ey);
                    g.DrawLine(stroke, cx + gap - 5 * u, ey, cx + gap + 5 * u, ey);
                }
                else
                {
                    // Eyes glance sideways now and then.
                    float look = (float)(Math.Sin(t * 0.9) > 0.85 ? 3 : Math.Sin(t * 0.9) < -0.85 ? -3 : 0) * u;
                    g.FillEllipse(fill, cx - gap - 4.5f * u + look, ey - 5.5f * u, 9 * u, 11 * u);
                    g.FillEllipse(fill, cx + gap - 4.5f * u + look, ey - 5.5f * u, 9 * u, 11 * u);
                }
                using (Pen mouth = new Pen(ink, 2.6f * u))
                {
                    mouth.StartCap = mouth.EndCap = LineCap.Round;
                    g.DrawArc(mouth, cx - 4 * u, ey + 6 * u, 8 * u, 5 * u, 20, 140);
                }
            }
            g.Restore(state);
            if (mood == MascotMood.Sleep) DrawSleep(g, cx + gap + 8 * u, ey - 12 * u, u * 1.2f, t, ink);
        }

        // Three z's drifting up and fading, staggered.
        private static void DrawSleep(Graphics g, float x, float y, float u, double t)
        { DrawSleep(g, x, y, u, t, Feature); }

        private static void DrawSleep(Graphics g, float x, float y, float u, double t, Color ink)
        {
            for (int i = 0; i < 3; i++)
            {
                double p = ((t / 2.4) + i / 3.0) % 1.0;
                float size = (5 + 3 * i) * u * (0.7f + 0.3f * (float)p);
                float px = x + (float)(p * 16 * u) + i * 3 * u, py = y - (float)(p * 26 * u);
                int alpha = (int)(170 * Math.Sin(p * Math.PI));
                using (Pen pen = new Pen(Color.FromArgb(alpha * ink.A / 255, ink), Math.Max(1f, 1.8f * u)))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new PointF[] { new PointF(px, py), new PointF(px + size, py), new PointF(px, py + size), new PointF(px + size, py + size) });
                }
            }
        }
    }

    // Drives a mascot inside a control: repaints its area every frame only while shown.
    internal sealed class MascotClock
    {
        private readonly Control owner;
        private Action stop;
        private readonly double origin = Motion.Now;
        public Rectangle Area;

        public MascotClock(Control owner) { this.owner = owner; }

        public double Time { get { return Motion.Now - origin; } }

        public void Run(bool running)
        {
            running &= Motion.Enabled;
            if (running && stop == null)
                stop = Motion.Pulse(delegate
                {
                    if (owner.IsDisposed || !owner.Visible) { Run(false); return; }
                    if (!Area.IsEmpty) owner.Invalidate(Area);
                });
            else if (!running && stop != null) { stop(); stop = null; }
        }
    }
}
