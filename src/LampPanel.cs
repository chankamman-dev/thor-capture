using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ThorCapture
{
    // Drop-down panel for the stick lamps: on/off, colour, brightness.
    internal sealed class LampPanel : Control
    {
        public static readonly Color[] Presets =
        {
            Color.FromArgb(70, 226, 240), Color.FromArgb(90, 168, 255), Color.FromArgb(128, 118, 255), Color.FromArgb(214, 155, 255),
            Color.FromArgb(255, 111, 181), Color.FromArgb(255, 77, 94), Color.FromArgb(255, 154, 60), Color.FromArgb(255, 200, 60),
            Color.FromArgb(76, 217, 123), Color.FromArgb(244, 246, 255)
        };
        private static readonly string[] PresetNames = { "青", "蓝", "靛", "紫", "粉", "红", "橙", "黄", "绿", "白" };

        private bool lampOn = true;
        private Color lampColor = Color.Empty;   // empty: follow the colourway
        private int level = 100;
        private int hover = -2;                  // -1 auto, 0..9 presets, 10 custom, -2 none
        private bool dragging;
        public Color AutoColor = Color.FromArgb(255, 200, 60);
        public event EventHandler Changed;
        public event EventHandler CustomRequested;

        public LampPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Ui.Surface;
            Font = Ui.Body;
            knobA = new Anim(this, 1, 260, Ease.OutBack);
            levelA = new Anim(this, 100, 160, Ease.OutCubic);
            Size = new Size(Ui.S(300), Ui.S(214));
        }

        private readonly Anim knobA, levelA;
        public bool LampOn { get { return lampOn; } set { lampOn = value; knobA.Snap(value ? 1 : 0); Invalidate(); } }
        public Color LampColor { get { return lampColor; } set { lampColor = value; Invalidate(); } }
        public int Level { get { return level; } set { level = Math.Max(10, Math.Min(100, value)); levelA.Snap(level); Invalidate(); } }

        private int Pad { get { return Ui.S(16); } }
        private Rectangle Toggle { get { return new Rectangle(Width - Pad - Ui.S(40), Ui.S(14), Ui.S(40), Ui.S(22)); } }

        // Index -1 is "follow colourway", 0..9 the presets, 10 the custom colour.
        private Rectangle Swatch(int index)
        {
            int slot = index + 1, d = Ui.S(26);
            int column = slot % 6, row = slot / 6;
            int cell = (Width - Pad * 2) / 6;
            return new Rectangle(Pad + column * cell + (cell - d) / 2, Ui.S(78) + row * Ui.S(36), d, d);
        }

        private Rectangle Track { get { return new Rectangle(Pad + Ui.S(8), Ui.S(180), Width - Pad * 2 - Ui.S(64), Ui.S(4)); } }

        private int Selected
        {
            get
            {
                if (lampColor.IsEmpty) return -1;
                for (int i = 0; i < Presets.Length; i++) if (Presets[i].ToArgb() == lampColor.ToArgb()) return i;
                return 10;
            }
        }

        private string NameOf(int index)
        {
            if (index == -1) return "跟随配色";
            if (index == 10) return lampColor.IsEmpty || Selected != 10 ? "自定义颜色…" : "自定义 #" + (lampColor.ToArgb() & 0xFFFFFF).ToString("X6");
            return PresetNames[index];
        }

        private void Raise() { if (Changed != null) Changed(this, EventArgs.Empty); }

        private void SetLevelFrom(int x)
        {
            Rectangle t = Track;
            int value = 10 + (int)Math.Round(90.0 * Math.Max(0, Math.Min(1, (x - t.X) / (double)t.Width)));
            if (value != level) { level = value; levelA.To(value, 160); Invalidate(); Raise(); }
        }

        private int HitSwatch(Point p)
        {
            for (int i = -1; i <= 10; i++)
            {
                Rectangle r = Swatch(i);
                r.Inflate(Ui.S(5), Ui.S(5));
                if (r.Contains(p)) return i;
            }
            return -2;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Rectangle toggle = Toggle;
            toggle.Inflate(Ui.S(6), Ui.S(6));
            if (toggle.Contains(e.Location)) { lampOn = !lampOn; knobA.To(lampOn ? 1 : 0, 260); Invalidate(); Raise(); return; }
            if (!lampOn) return;
            int swatch = HitSwatch(e.Location);
            if (swatch == 10) { if (CustomRequested != null) CustomRequested(this, EventArgs.Empty); return; }
            if (swatch >= -1)
            {
                lampColor = swatch == -1 ? Color.Empty : Presets[swatch];
                Invalidate();
                Raise();
                return;
            }
            Rectangle track = Track;
            track.Inflate(Ui.S(10), Ui.S(12));
            if (track.Contains(e.Location)) { dragging = true; Capture = true; SetLevelFrom(e.X); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging) { SetLevelFrom(e.X); return; }
            int swatch = lampOn ? HitSwatch(e.Location) : -2;
            if (swatch != hover) { hover = swatch; Invalidate(); }
            Rectangle toggle = Toggle;
            toggle.Inflate(Ui.S(6), Ui.S(6));
            Cursor = swatch >= -1 || toggle.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging) { dragging = false; Capture = false; }
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -2; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Pad;
            Ui.Write(g, "摇杆灯光", Ui.Strong, new Rectangle(pad, Ui.S(14), Width, Ui.S(22)), Ui.Text, TextFormatFlags.VerticalCenter);
            Ui.Write(g, lampOn ? "已开启" : "已关闭", Ui.Small, new Rectangle(pad, Ui.S(14), Toggle.X - pad - Ui.S(10), Ui.S(22)),
                lampOn ? Ui.Ok : Ui.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);

            // Switch.
            Rectangle toggle = Toggle;
            Ui.Switch(g, toggle, knobA.Value, true);

            int dim = lampOn ? 255 : 90;
            int shown = hover >= -1 ? hover : Selected;
            Ui.Write(g, "颜色", Ui.Small, new Rectangle(pad, Ui.S(50), Width, Ui.S(20)), Ui.Alpha(Ui.TextDim, dim), TextFormatFlags.VerticalCenter);
            Ui.Write(g, NameOf(shown), Ui.Small, new Rectangle(pad, Ui.S(50), Width - pad * 2, Ui.S(20)), Ui.Alpha(Ui.Text, dim), TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            for (int i = -1; i <= 10; i++)
            {
                Rectangle r = Swatch(i);
                if (i == Selected && lampOn)
                {
                    RectangleF ring = r;
                    ring.Inflate(Ui.S(3.5f), Ui.S(3.5f));
                    using (Pen pen = new Pen(Ui.Accent, Math.Max(1.5f, Ui.S(2)))) g.DrawEllipse(pen, ring);
                }
                else if (i == hover)
                {
                    RectangleF ring = r;
                    ring.Inflate(Ui.S(3.5f), Ui.S(3.5f));
                    using (Pen pen = new Pen(Ui.Wash(0.25f), 1.2f)) g.DrawEllipse(pen, ring);
                }
                if (i == 10 && Selected != 10)
                {
                    Ui.Fill(g, r, r.Width / 2f, Ui.Alpha(Ui.Bg3, dim));
                    using (Pen plus = new Pen(Ui.Alpha(Ui.TextDim, dim), 1.6f))
                    {
                        g.DrawLine(plus, r.X + r.Width / 2f, r.Y + Ui.S(7), r.X + r.Width / 2f, r.Bottom - Ui.S(7));
                        g.DrawLine(plus, r.X + Ui.S(7), r.Y + r.Height / 2f, r.Right - Ui.S(7), r.Y + r.Height / 2f);
                    }
                    continue;
                }
                Color c = i == -1 ? AutoColor : i == 10 ? lampColor : Presets[i];
                using (PathGradientBrush glow = GlowBrush(r, c, dim)) g.FillEllipse(glow, r);
                using (Pen rim = new Pen(Ui.Wash(0.08f * dim / 255f), 1f)) g.DrawEllipse(rim, r);
                if (i == -1)
                {
                    // "Follow colourway": the colourway's lamp colour with a small A.
                    using (Font letter = new Font("Segoe UI", 7.5f, FontStyle.Bold))
                        TextRenderer.DrawText(g, "A", letter, r, Color.FromArgb(dim * 200 / 255, 20, 20, 24), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }

            // Brightness.
            Ui.Write(g, "亮度", Ui.Small, new Rectangle(pad, Ui.S(152), Width, Ui.S(20)), Ui.Alpha(Ui.TextDim, dim), TextFormatFlags.VerticalCenter);
            Rectangle track = Track;
            Ui.Fill(g, track, track.Height / 2f, Ui.Alpha(Ui.Bg3, dim));
            int fill = (int)(track.Width * (levelA.Value - 10) / 90.0);
            Color lamp = lampColor.IsEmpty ? AutoColor : lampColor;
            Ui.Fill(g, new Rectangle(track.X, track.Y, Math.Max(track.Height, fill), track.Height), track.Height / 2f, Ui.Alpha(lamp, dim));
            int kd = Ui.S(16);
            RectangleF knobArea = new RectangleF(track.X + fill - kd / 2, track.Y + track.Height / 2 - kd / 2, kd, kd);
            Ui.Shadow(g, knobArea, kd / 2f, Ui.S(3), dim / 5, Ui.S(1), Ui.Ink);
            using (SolidBrush white = new SolidBrush(Ui.Alpha(Color.White, dim))) g.FillEllipse(white, knobArea);
            using (Pen rim = new Pen(Ui.Alpha(lamp, dim), Ui.S(2))) g.DrawEllipse(rim, knobArea.X + Ui.S(1), knobArea.Y + Ui.S(1), kd - Ui.S(2), kd - Ui.S(2));
            Ui.Write(g, level + "%", Ui.Strong, new Rectangle(track.Right + Ui.S(12), track.Y - Ui.S(10), Width - track.Right - pad - Ui.S(12), Ui.S(24)),
                Ui.Alpha(Ui.Text, dim), TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        private static PathGradientBrush GlowBrush(Rectangle r, Color c, int alpha)
        {
            PathGradientBrush brush;
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(r);
                brush = new PathGradientBrush(path);
            }
            brush.CenterPoint = new PointF(r.X + r.Width * 0.4f, r.Y + r.Height * 0.38f);
            brush.CenterColor = Ui.Alpha(Ui.Mix(c, Color.White, 0.45f), alpha);
            brush.SurroundColors = new Color[] { Ui.Alpha(c, alpha) };
            return brush;
        }
    }
}
