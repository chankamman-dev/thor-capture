// Owner-drawn controls for the light UI. Stock WinForms chrome (combo arrows,
// spinners, track bars, toolstrips) cannot follow the theme, so the visible
// chrome is drawn here and only text boxes stay native.
//
// Every pixel size goes through Ui.S() so the layout follows the desktop DPI;
// fonts are in points and scale on their own once the process is DPI aware.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ThorCapture
{
    internal static class Ui
    {
        public static readonly float Scale;

        // Soft monochrome palette: warm greys, white pills, a dark grey for primary actions.
        // Colour is reserved for content (screenshots, the shell, the lamp) and for recording.
        public static readonly Color Bg0 = Color.FromArgb(241, 240, 238);      // canvas behind the stage
        public static readonly Color Bg1 = Color.FromArgb(250, 250, 249);      // bars, sidebar, dialogs
        public static readonly Color Bg2 = Color.FromArgb(233, 232, 230);      // tracks and fields
        public static readonly Color Bg3 = Color.FromArgb(222, 221, 218);      // hover / pressed fills
        public static readonly Color Surface = Color.White;                     // pills and raised cards
        public static readonly Color Card = Color.FromArgb(242, 241, 239);     // gallery card at rest
        public static readonly Color Line = Color.FromArgb(232, 231, 228);
        public static readonly Color Ink = Color.FromArgb(62, 61, 59);         // base for dark overlays
        public static readonly Color Text = Color.FromArgb(62, 61, 59);
        public static readonly Color TextDim = Color.FromArgb(128, 126, 122);
        public static readonly Color TextFaint = Color.FromArgb(172, 170, 166);
        public static readonly Color Accent = Color.FromArgb(62, 61, 59);      // primary actions: dark grey pill
        public static readonly Color AccentDeep = Color.FromArgb(40, 40, 38);
        public static readonly Color AccentAlt = Accent;
        public static readonly Color AccentSoft = Color.FromArgb(236, 235, 233);
        public static readonly Color Danger = Color.FromArgb(232, 88, 84);
        public static readonly Color Ok = Color.FromArgb(96, 176, 124);
        public static readonly Color Warn = Color.FromArgb(226, 164, 72);
        public static readonly Color UpperTint = Color.FromArgb(128, 126, 122);
        public static readonly Color LowerTint = Color.FromArgb(128, 126, 122);

        public static readonly Font Body;
        public static readonly Font Small;
        public static readonly Font Strong;
        public static readonly Font Heading;
        public static readonly Font Title;
        public static readonly Font IconFont;
        public static readonly Font IconSmall;
        public static readonly Font IconLarge;
        public static readonly string IconFamily;

        static Ui()
        {
            float dpi = 96f;
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX; } catch { }
            Scale = Math.Max(1f, dpi / 96f);
            Body = new Font("Microsoft YaHei UI", 9f);
            Small = new Font("Microsoft YaHei UI", 8.25f);
            Strong = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            Heading = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            Title = new Font("Segoe UI Semibold", 12.5f);
            IconFamily = FindFamily("Segoe Fluent Icons", "Segoe MDL2 Assets");
            if (IconFamily != null)
            {
                IconFont = new Font(IconFamily, 10.5f);
                IconSmall = new Font(IconFamily, 8f);
                IconLarge = new Font(IconFamily, 22f);
            }
        }

        private static string FindFamily(params string[] names)
        {
            foreach (string name in names)
                foreach (FontFamily family in FontFamily.Families)
                    if (string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase)) return name;
            return null;
        }

        public static int S(float value) { return (int)Math.Round(value * Scale); }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * t), (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int alpha)
        {
            return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c.R, c.G, c.B);
        }

        // Dark wash for hover / pressed states on light surfaces.
        public static Color Wash(float amount) { return Alpha(Ink, (int)(255 * amount)); }

        // Flat accent fill (kept as a brush so callers can fill paths directly).
        public static LinearGradientBrush AccentBrush(RectangleF r, float lighten)
        {
            RectangleF area = new RectangleF(r.X - 1, r.Y - 1, Math.Max(2, r.Width + 2), Math.Max(2, r.Height + 2));
            Color c = Mix(Accent, Color.White, lighten);
            return new LinearGradientBrush(area, c, c, 0f);
        }

        // Pictogram of the Thor: a wide upper screen over a narrower lower one. `screen`
        // 0 fills the upper, 1 the lower, 2 both, -1 neither.
        public static void Thor(Graphics g, RectangleF r, int screen, Color ink)
        {
            float w = r.Width, h = r.Height;
            RectangleF top = new RectangleF(r.X, r.Y + h * 0.04f, w, h * 0.44f);
            RectangleF bottom = new RectangleF(r.X + w * 0.2f, r.Y + h * 0.56f, w * 0.6f, h * 0.4f);
            float rad = Math.Max(1, w * 0.12f);
            float stroke = Math.Max(1.2f, w / 11f);
            using (Pen pen = new Pen(ink, stroke))
            using (SolidBrush fill = new SolidBrush(ink))
            {
                pen.LineJoin = LineJoin.Round;
                foreach (int i in new int[] { 0, 1 })
                {
                    RectangleF part = i == 0 ? top : bottom;
                    part.Inflate(-stroke / 2, -stroke / 2);
                    using (GraphicsPath path = Round(part, rad))
                    {
                        if (screen == i || screen == 2) g.FillPath(fill, path);
                        g.DrawPath(pen, path);
                    }
                }
            }
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            return Round(r, radius, radius, radius, radius);
        }

        public static GraphicsPath Round(RectangleF r, float tl, float tr, float br, float bl)
        {
            GraphicsPath path = new GraphicsPath();
            float max = Math.Min(r.Width, r.Height) / 2f;
            tl = Math.Min(tl, max); tr = Math.Min(tr, max); br = Math.Min(br, max); bl = Math.Min(bl, max);
            if (tl > 0) path.AddArc(r.X, r.Y, tl * 2, tl * 2, 180, 90); else path.AddLine(r.X, r.Y, r.X, r.Y);
            if (tr > 0) path.AddArc(r.Right - tr * 2, r.Y, tr * 2, tr * 2, 270, 90); else path.AddLine(r.Right, r.Y, r.Right, r.Y);
            if (br > 0) path.AddArc(r.Right - br * 2, r.Bottom - br * 2, br * 2, br * 2, 0, 90); else path.AddLine(r.Right, r.Bottom, r.Right, r.Bottom);
            if (bl > 0) path.AddArc(r.X, r.Bottom - bl * 2, bl * 2, bl * 2, 90, 90); else path.AddLine(r.X, r.Bottom, r.X, r.Bottom);
            path.CloseFigure();
            return path;
        }

        public static void Fill(Graphics g, RectangleF r, float radius, Color color)
        {
            using (GraphicsPath path = Round(r, radius))
            using (SolidBrush brush = new SolidBrush(color)) g.FillPath(brush, path);
        }

        public static void Stroke(Graphics g, RectangleF r, float radius, Color color, float width)
        {
            using (GraphicsPath path = Round(r, radius))
            using (Pen pen = new Pen(color, width)) g.DrawPath(pen, path);
        }

        // Stacked translucent rounded rects: cheap and good enough for a soft drop shadow.
        public static void Shadow(Graphics g, RectangleF r, float radius, float spread, int alpha, float offsetY)
        {
            Shadow(g, r, radius, spread, alpha, offsetY, Color.Black);
        }

        // Coloured variant doubles as a soft glow.
        public static void Shadow(Graphics g, RectangleF r, float radius, float spread, int alpha, float offsetY, Color tint)
        {
            const int layers = 8;
            for (int i = layers; i >= 1; i--)
            {
                float grow = spread * i / layers;
                RectangleF s = new RectangleF(r.X - grow, r.Y - grow + offsetY, r.Width + grow * 2, r.Height + grow * 2);
                using (GraphicsPath path = Round(s, radius + grow))
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(Math.Max(1, alpha / layers), tint)))
                    g.FillPath(brush, path);
            }
        }

        // iOS-style switch; amount 0..1 slides the knob and blends the track into the accent gradient.
        public static void Switch(Graphics g, Rectangle r, float amount, bool enabled)
        {
            float t = Math.Max(0, Math.Min(1, amount));
            RectangleF track = r;
            // Disabled switches keep their position but fade towards the track colour.
            Fill(g, track, track.Height / 2f, Mix(Bg3, enabled ? Accent : Mix(Accent, Bg3, 0.65f), t));
            float knob = track.Height - S(6);
            // The knob stretches mid-travel and springs round at either end.
            float travel = track.Width - knob - S(6);
            float stretch = (float)Math.Sin(Math.PI * t) * knob * 0.45f;
            float kx = track.X + S(3) + travel * Math.Max(0, Math.Min(1, amount)) - stretch * t;
            RectangleF k = new RectangleF(kx, track.Y + S(3), knob + stretch, knob);
            Fill(g, k, knob / 2, enabled ? Color.White : Mix(Color.White, Bg2, 0.4f));
        }

        public static void Write(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text, font, r, color, flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        public static int Measure(string text, Font font)
        {
            if (string.IsNullOrEmpty(text) || font == null) return 0;
            return TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
        }

        public static void Glyph(Graphics g, string glyph, Font font, Rectangle r, Color color)
        {
            if (font == null || string.IsNullOrEmpty(glyph)) return;
            TextRenderer.DrawText(g, glyph, font, r, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        public static string DayLabel(DateTime day)
        {
            DateTime today = DateTime.Today;
            string[] week = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            string date = day.Year == today.Year ? day.ToString("M月d日") : day.ToString("yyyy年M月d日");
            if (day == today) return "今天 · " + date;
            if (day == today.AddDays(-1)) return "昨天 · " + date;
            return date + " · " + week[(int)day.DayOfWeek];
        }

        public static string Bytes(long size)
        {
            if (size >= 1024 * 1024) return (size / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (size >= 1024) return (size / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
            return size + " B";
        }
    }

    // Segoe MDL2 / Fluent icon code points.
    internal static class Glyphs
    {
        public const string Camera = "";
        public const string Record = "";
        public const string Stop = "";
        public const string Refresh = "";
        public const string Copy = "";
        public const string SaveAs = "";
        public const string Delete = "";
        public const string FolderOpen = "";
        public const string Search = "";
        public const string Export = "";
        public const string ChevronDown = "";
        public const string Cancel = "";
        public const string Play = "";
        public const string Link = "";
        public const string Picture = "";
        public const string Game = "";
        public const string Zoom = "";
        public const string Locate = "";
        public const string Video = "";
        public const string Lamp = "";
        public const string Settings = "";
        public const string Volume = "";
        public const string Mute = "";
        public const string Tune = "";
        public const string Pause = "";
        public const string Replay = "";
        public const string OpenOut = "";
    }

    internal enum ButtonKind { Ghost, Primary, Danger, Subtle, Link }

    internal class FlatButton : Control
    {
        public string Glyph;
        public ButtonKind Kind;
        public string Hint;
        public Color Dot = Color.Empty;
        public Color GlyphColor = Color.Empty;
        public bool Chevron;
        public bool Pill = true; // fully rounded ends (circle when square)
        private bool compact;
        // Icon only, keeping Text for tooltips and callers; used when a bar runs out of room.
        public bool Compact { get { return compact; } set { if (compact != value) { compact = value; Invalidate(); } } }
        private string Shown { get { return compact && HasGlyph ? "" : Text; } }
        private bool isChecked;
        private readonly Anim hoverA, pressA, checkA;

        public FlatButton(string glyph, string text)
        {
            hoverA = new Anim(this, 0, 140, Ease.OutCubic);
            pressA = new Anim(this, 0, 90, Ease.OutCubic);
            checkA = new Anim(this, 0, 200, Ease.OutCubic);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Glyph = glyph;
            Text = text ?? "";
            Font = Ui.Body;
            Cursor = Cursors.Hand;
            Height = Ui.S(34);
        }

        public bool Checked
        {
            get { return isChecked; }
            set { if (isChecked != value) { isChecked = value; checkA.Target = value ? 1 : 0; Invalidate(); } }
        }

        private bool HasGlyph { get { return Glyph != null && Ui.IconFont != null; } }

        private int Gap { get { return Ui.S(7); } }

        private int ContentWidth()
        {
            int width = 0, parts = 0;
            if (!Dot.IsEmpty) { width += Ui.S(8); parts++; }
            if (HasGlyph) { width += Ui.Measure(Glyph, Ui.IconFont); parts++; }
            if (Shown.Length > 0) { width += Ui.Measure(Shown, Font); parts++; }
            if (!string.IsNullOrEmpty(Hint)) { width += Ui.Measure(Hint, Ui.Small) + Ui.S(12); parts++; }
            if (Chevron && Ui.IconSmall != null) { width += Ui.Measure(Glyphs.ChevronDown, Ui.IconSmall); parts++; }
            return width + Math.Max(0, parts - 1) * Gap;
        }

        public int PreferredWidth
        {
            get
            {
                int pad = Kind == ButtonKind.Link ? Ui.S(4) : (Shown.Length == 0 ? Ui.S(9) : Ui.S(16));
                return ContentWidth() + pad * 2;
            }
        }

        public void Fit() { Width = PreferredWidth; }

        protected override void OnMouseEnter(EventArgs e) { hoverA.To(1, 140); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hoverA.To(0, 240); Release(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            // Press squashes the button like a soft key.
            if (e.Button == MouseButtons.Left && Enabled) { pressA.Curve = Ease.OutCubic; pressA.To(1, 90); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { Release(); base.OnMouseUp(e); }
        private void Release()
        {
            // ...and it springs back past its rest size before settling.
            if (pressA.Target == 0) return;
            pressA.Curve = Ease.Spring;
            pressA.To(0, 460);
        }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = Enabled ? hoverA.Value : 0, p = Enabled ? pressA.Value : 0, c = checkA.Value;
            Color fill = Color.Empty, ink = Enabled ? Ui.Text : Ui.TextFaint;
            switch (Kind)
            {
                case ButtonKind.Primary:
                    fill = Enabled ? Ui.Mix(Ui.Accent, Color.White, 0.1f * h) : Ui.Bg3;
                    ink = Enabled ? Color.White : Ui.TextFaint;
                    break;
                case ButtonKind.Danger:
                    fill = Ui.Mix(Ui.Danger, Color.White, 0.1f * h);
                    ink = Color.White;
                    break;
                case ButtonKind.Subtle:
                    // Toggle on = dark key with light text.
                    fill = Ui.Mix(Ui.Mix(Ui.Bg2, Ui.Bg3, h), Ui.Accent, c);
                    if (Enabled) ink = Ui.Mix(Ui.Text, Color.White, c);
                    else fill = Ui.Mix(Ui.Bg2, Ui.Bg1, 0.4f);
                    break;
                case ButtonKind.Link:
                    ink = !Enabled ? Ui.TextFaint : Ui.Mix(Ui.TextDim, Ui.Text, h);
                    break;
                default:
                    fill = Ui.Mix(Ui.Alpha(Ui.Bg2, (int)(255 * h)), Ui.Accent, c);
                    ink = !Enabled ? Ui.TextFaint : Ui.Mix(Ui.Mix(Ui.TextDim, Ui.Text, h), Color.White, c);
                    break;
            }

            // Squash around the centre: wider and shorter while pressed, with the spring's rebound.
            float sx = 1 + 0.03f * p, sy = 1 - 0.08f * p;
            GraphicsState state = g.Save();
            g.TranslateTransform(Width / 2f, Height / 2f);
            g.ScaleTransform(sx, sy);
            g.TranslateTransform(-Width / 2f, -Height / 2f);

            RectangleF box = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float radius = Pill ? Math.Min(box.Width, box.Height) / 2f : Ui.S(10);
            if (!fill.IsEmpty && fill.A > 0) Ui.Fill(g, box, radius, fill);

            int x = (Width - ContentWidth()) / 2;
            int gap = Gap;
            if (!Dot.IsEmpty)
            {
                int d = Ui.S(8);
                using (SolidBrush dot = new SolidBrush(Dot)) g.FillEllipse(dot, x, Height / 2 - d / 2, d, d);
                x += d + gap;
            }
            if (HasGlyph)
            {
                int w = Ui.Measure(Glyph, Ui.IconFont);
                Color glyphInk = GlyphColor.IsEmpty || !Enabled ? ink : GlyphColor;
                Ui.Glyph(g, Glyph, Ui.IconFont, new Rectangle(x, 0, w, Height), glyphInk);
                x += w + gap;
            }
            if (Shown.Length > 0)
            {
                int w = Ui.Measure(Shown, Font);
                Ui.Write(g, Shown, Font, new Rectangle(x, 0, w + 2, Height), ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                x += w + gap;
            }
            if (!string.IsNullOrEmpty(Hint))
            {
                // Key cap.
                int w = Ui.Measure(Hint, Ui.Small) + Ui.S(12);
                Rectangle cap = new Rectangle(x, Height / 2 - Ui.S(10), w, Ui.S(20));
                Ui.Fill(g, cap, Ui.S(10), Kind == ButtonKind.Primary ? Ui.Alpha(Color.White, 40) : Ui.Wash(0.06f));
                Ui.Write(g, Hint, Ui.Small, cap, Ui.Alpha(ink, 220), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                x += w + gap;
            }
            if (Chevron && Ui.IconSmall != null)
            {
                int w = Ui.Measure(Glyphs.ChevronDown, Ui.IconSmall);
                Ui.Glyph(g, Glyphs.ChevronDown, Ui.IconSmall, new Rectangle(x, Ui.S(1), w, Height), Ui.Mix(Ui.TextDim, ink, c));
            }
            g.Restore(state);
        }
    }

    internal class Segmented : Control
    {
        private string[] items = new string[0];
        private int selected = -1;
        private int hover = -1;
        public bool Stretch;
        public bool Accented = true; // kept for callers; the selected label is always the strongest
        // Optional pictogram before each label: (graphics, icon box, index, ink).
        public Action<Graphics, Rectangle, int, Color> Icon;
        public event EventHandler SelectedIndexChanged;
        // The pill's two ends move separately: the leading end darts ahead, the trailing end
        // follows on a spring, so the pill stretches while it travels and snaps back round.
        private readonly Anim lead, trail, hoverA;
        private int hoverShown = -1;

        public Segmented(params string[] labels)
        {
            lead = new Anim(this, labels.Length > 0 ? 0 : -1, 220, Ease.OutCubic);
            trail = new Anim(this, labels.Length > 0 ? 0 : -1, 520, Ease.Spring);
            hoverA = new Anim(this, 0, 150, Ease.OutCubic);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Font = Ui.Body;
            Height = Ui.S(32);
            Cursor = Cursors.Hand;
            items = labels;
            selected = labels.Length > 0 ? 0 : -1;
        }

        public int Count { get { return items.Length; } }

        public void SetItems(string[] labels, int index)
        {
            items = labels;
            selected = Math.Min(index, items.Length - 1);
            hover = -1;
            lead.Snap(selected); trail.Snap(selected);
            Invalidate();
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value == selected || value < -1 || value >= items.Length) return;
                if (selected < 0 || value < 0) { lead.Snap(value); trail.Snap(value); }
                else { lead.To(value, 220); trail.To(value, 520); }
                selected = value;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        private int IconWidth { get { return Icon == null ? 0 : Ui.S(20); } }

        public int PreferredWidth
        {
            get
            {
                int width = Ui.S(6);
                foreach (string item in items) width += Ui.Measure(item, Font) + Ui.S(30) + IconWidth;
                return width;
            }
        }

        public void Fit() { Width = PreferredWidth; }

        private Rectangle[] Segments()
        {
            Rectangle[] result = new Rectangle[items.Length];
            int pad = Ui.S(3);
            int x = pad;
            int available = Width - pad * 2;
            for (int i = 0; i < items.Length; i++)
            {
                int w = Stretch ? (available * (i + 1) / items.Length) - (available * i / items.Length)
                                : Ui.Measure(items[i], Font) + Ui.S(30) + IconWidth;
                result[i] = new Rectangle(x, pad, w, Height - pad * 2);
                x += w;
            }
            return result;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int index = -1;
            Rectangle[] segments = Segments();
            for (int i = 0; i < segments.Length; i++) if (segments[i].Contains(e.Location)) index = i;
            if (index != hover)
            {
                hover = index;
                if (index >= 0) { hoverShown = index; hoverA.Snap(0); hoverA.To(1, 150); } else hoverA.To(0, 220);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; hoverA.To(0, 220); base.OnMouseLeave(e); }

        // Position along the segments for a fractional index.
        private static float EdgeAt(Rectangle[] segments, float position, bool right)
        {
            int a = Math.Max(0, Math.Min(segments.Length - 1, (int)Math.Floor(position)));
            int b = Math.Min(segments.Length - 1, a + 1);
            float t = position - a;
            float ea = right ? segments[a].Right : segments[a].X, eb = right ? segments[b].Right : segments[b].X;
            if (a == b) return ea + t * segments[a].Width;
            return ea + (eb - ea) * t;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled)
            {
                Rectangle[] segments = Segments();
                for (int i = 0; i < segments.Length; i++) if (segments[i].Contains(e.Location)) SelectedIndex = i;
            }
            base.OnMouseDown(e);
        }

        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF track = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Ui.Fill(g, track, track.Height / 2, Ui.Bg2);
            Rectangle[] segments = Segments();
            if (segments.Length == 0) return;
            if (Enabled && hoverShown >= 0 && hoverShown < segments.Length && hoverShown != selected && hoverA.Value > 0)
                Ui.Fill(g, segments[hoverShown], segments[hoverShown].Height / 2f, Ui.Wash(0.04f * hoverA.Value));
            float pos = lead.Value;
            if (selected >= 0)
            {
                float a = lead.Value, b = trail.Value;
                float left = EdgeAt(segments, Math.Min(a, b), false), right = EdgeAt(segments, Math.Max(a, b), true);
                // The spread between the ends also thins the pill a touch, like a stretched blob.
                float spread = Math.Min(1, Math.Abs(a - b));
                RectangleF pill = new RectangleF(left, segments[0].Y + spread * Ui.S(1.5f), right - left, segments[0].Height - spread * Ui.S(3));
                if (Enabled) Ui.Shadow(g, pill, pill.Height / 2, Ui.S(2), 18, Ui.S(1), Ui.Ink);
                Ui.Fill(g, pill, pill.Height / 2, Enabled ? Ui.Surface : Ui.Mix(Ui.Bg2, Ui.Surface, 0.5f));
            }
            for (int i = 0; i < segments.Length; i++)
            {
                float near = selected < 0 ? 0 : Math.Max(0, 1 - Math.Abs(pos - i));
                float lift = i == hoverShown && i != selected ? hoverA.Value * 0.6f : 0;
                Color ink = !Enabled ? Ui.TextFaint : Ui.Mix(Ui.TextDim, Ui.Text, Math.Max(near, lift));
                Rectangle r = segments[i];
                if (Icon != null)
                {
                    int tw = Ui.Measure(items[i], i == selected ? Ui.Strong : Font);
                    int start = r.X + (r.Width - tw - IconWidth) / 2;
                    Icon(g, new Rectangle(start, r.Y + (r.Height - Ui.S(14)) / 2, Ui.S(14), Ui.S(14)), i, ink);
                    r = new Rectangle(start + IconWidth, r.Y, tw + Ui.S(2), r.Height);
                    Ui.Write(g, items[i], i == selected ? Ui.Strong : Font, r, ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }
                else
                    Ui.Write(g, items[i], i == selected ? Ui.Strong : Font, r, ink,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }

    internal class SkinPicker : Control
    {
        private int selected;
        private int hover = -1;
        public event EventHandler SelectedIndexChanged;
        private readonly Anim slide, hoverA;

        public SkinPicker()
        {
            slide = new Anim(this, 0, 480, Ease.Spring);
            hoverA = new Anim(this, 0, 150, Ease.OutCubic);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Font = Ui.Body;
            Height = Ui.S(32);
            Cursor = Cursors.Hand;
            Width = PreferredWidth;
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value == selected || value < 0 || value >= DeviceSkin.All.Length) return;
                selected = value;
                if (IsHandleCreated) slide.To(value, 480); else slide.Snap(value);
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        private int Diameter { get { return Ui.S(18); } }
        private int Step { get { return Ui.S(27); } }

        private Rectangle Swatch(int i)
        {
            int d = Diameter;
            return new Rectangle(Ui.S(9) + i * Step, (Height - d) / 2, d, d);
        }

        public int PreferredWidth
        {
            get
            {
                int longest = 0;
                foreach (DeviceSkin skin in DeviceSkin.All) longest = Math.Max(longest, Ui.Measure(skin.Name, Ui.Strong));
                return Ui.S(9) + DeviceSkin.All.Length * Step + Ui.S(4) + longest + Ui.S(16);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int index = -1;
            for (int i = 0; i < DeviceSkin.All.Length; i++)
            {
                Rectangle r = Swatch(i);
                r.Inflate(Ui.S(4), Ui.S(6));
                if (r.Contains(e.Location)) index = i;
            }
            if (index != hover) { hover = index; hoverA.Snap(0); if (index >= 0) hoverA.To(1, 150); Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && hover >= 0) SelectedIndex = hover;
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF track = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Ui.Fill(g, track, track.Height / 2, Ui.Bg2);
            // White puck behind the chosen swatch, springing between them.
            RectangleF puck = Swatch(0);
            puck.X += slide.Value * Step;
            puck.Inflate(Ui.S(4), Ui.S(4));
            Ui.Shadow(g, puck, puck.Width / 2, Ui.S(2), 18, Ui.S(1), Ui.Ink);
            using (SolidBrush white = new SolidBrush(Ui.Surface)) g.FillEllipse(white, puck);
            for (int i = 0; i < DeviceSkin.All.Length; i++)
            {
                RectangleF r = Swatch(i);
                // Hovered swatch swells slightly; the chosen one sits a little smaller inside its puck.
                float grow = i == hover && i != selected ? hoverA.Value * Ui.S(1.5f) : i == selected ? -Ui.S(1) : 0;
                r.Inflate(grow, grow);
                Rectangle ri = Rectangle.Round(r);
                DeviceSkin.All[i].DrawSwatch(g, ri);
                using (Pen rim = new Pen(Ui.Wash(0.12f), 1f)) g.DrawEllipse(rim, ri);
            }
            int shown = hover >= 0 ? hover : selected;
            int x = Ui.S(9) + DeviceSkin.All.Length * Step + Ui.S(4);
            Ui.Write(g, DeviceSkin.All[shown].Name, Ui.Strong, new Rectangle(x, 0, Width - x, Height),
                shown == selected ? Ui.Text : Ui.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }

    internal class SearchBox : Control
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);

        public readonly TextBox Box = new TextBox();
        private readonly Anim focusA;
        public string Placeholder = "搜索文件名或日期，如 0926";

        public SearchBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Ui.S(34);
            Cursor = Cursors.IBeam;
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Ui.Bg2;
            Box.GotFocus += delegate { Box.BackColor = Ui.Surface; };
            Box.LostFocus += delegate { Box.BackColor = Ui.Bg2; };
            Box.ForeColor = Ui.Text;
            Box.Font = Ui.Body;
            Controls.Add(Box);
            focusA = new Anim(this, 0, 200, Ease.OutCubic);
            Box.GotFocus += delegate { focusA.Target = 1; };
            Box.LostFocus += delegate { focusA.Target = 0; };
            Box.TextChanged += delegate { Invalidate(); };
            Box.HandleCreated += delegate { SendMessage(Box.Handle, 0x1501, new IntPtr(1), Placeholder); };
        }

        private Rectangle ClearArea { get { return new Rectangle(Width - Ui.S(30), 0, Ui.S(30), Height); } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int left = Ui.S(36);
            Box.Location = new Point(left, (Height - Box.Height) / 2 + 1);
            Box.Width = Math.Max(10, Width - left - Ui.S(32));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Box.Text.Length > 0 && ClearArea.Contains(e.Location)) Box.Text = "";
            Box.Focus();
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = Box.Text.Length > 0 && ClearArea.Contains(e.Location) ? Cursors.Hand : Cursors.IBeam;
            base.OnMouseMove(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF box = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float f = focusA.Value;
            // The field lightens to white when focused; no rings or glows.
            Ui.Fill(g, box, box.Height / 2, Ui.Mix(Ui.Bg2, Ui.Surface, f));
            if (f > 0) Ui.Stroke(g, box, box.Height / 2, Ui.Alpha(Ui.Bg3, (int)(255 * f)), 1f);
            if (Ui.IconSmall != null)
            {
                Ui.Glyph(g, Glyphs.Search, Ui.IconSmall, new Rectangle(Ui.S(6), 0, Ui.S(30), Height), Ui.Mix(Ui.TextFaint, Ui.Text, f));
                if (Box.Text.Length > 0) Ui.Glyph(g, Glyphs.Cancel, Ui.IconSmall, ClearArea, Ui.TextDim);
            }
        }
    }

    // Plain panel that paints through a callback; used for bars and headers.
    internal class Strip : Panel
    {
        public bool TopLine;
        public bool BottomLine;
        public PaintEventHandler Painter;

        public Strip()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Painter != null) Painter(this, e);
            using (Pen line = new Pen(Ui.Line))
            {
                if (TopLine) e.Graphics.DrawLine(line, 0, 0, Width, 0);
                if (BottomLine) e.Graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
            }
        }
    }

    internal class MenuColors : ProfessionalColorTable
    {
        public override Color MenuBorder { get { return Ui.Line; } }
        public override Color MenuItemBorder { get { return Ui.AccentSoft; } }
        public override Color MenuItemSelected { get { return Ui.AccentSoft; } }
        public override Color MenuItemSelectedGradientBegin { get { return Ui.AccentSoft; } }
        public override Color MenuItemSelectedGradientEnd { get { return Ui.AccentSoft; } }
        public override Color MenuItemPressedGradientBegin { get { return Ui.Bg3; } }
        public override Color MenuItemPressedGradientEnd { get { return Ui.Bg3; } }
        public override Color ToolStripDropDownBackground { get { return Ui.Surface; } }
        public override Color ImageMarginGradientBegin { get { return Ui.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return Ui.Surface; } }
        public override Color ImageMarginGradientEnd { get { return Ui.Surface; } }
        public override Color SeparatorDark { get { return Ui.Line; } }
        public override Color SeparatorLight { get { return Ui.Surface; } }
        public override Color CheckBackground { get { return Ui.Alpha(Ui.Accent, 70); } }
        public override Color CheckSelectedBackground { get { return Ui.Alpha(Ui.Accent, 90); } }
        public override Color CheckPressedBackground { get { return Ui.Alpha(Ui.Accent, 90); } }
    }

    internal class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Ui.Text : Ui.TextFaint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush dot = new SolidBrush(Ui.Accent))
                e.Graphics.FillEllipse(dot, r.X + r.Width / 2 - Ui.S(4), r.Y + r.Height / 2 - Ui.S(4), Ui.S(8), Ui.S(8));
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Ui.TextDim;
            base.OnRenderArrow(e);
        }
    }

    internal static class Menus
    {
        public static ContextMenuStrip Create()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new MenuRenderer();
            menu.Font = Ui.Body;
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.BackColor = Ui.Surface;
            menu.ForeColor = Ui.Text;
            return menu;
        }

        public static ToolStripMenuItem Add(ContextMenuStrip menu, string text, string keys, EventHandler click)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.ShortcutKeyDisplayString = keys;
            item.Padding = new Padding(0, Ui.S(3), 0, Ui.S(3));
            if (click != null) item.Click += click;
            menu.Items.Add(item);
            return item;
        }
    }

    internal sealed class GalleryItem
    {
        public string Path;
        public string Name;
        public CaptureMetadata Metadata;
        public DateTime Time;
        public DateTime WriteUtc; // file version for the thumbnail cache
        public long Size;
        public bool IsVideo;
        public int Screen = -1; // 0 upper, 1 lower, -1 unknown
        public bool Paired;
        public bool Selected;
        public Bitmap Thumb;
        public RectangleF Bounds;

        public string ScreenLabel
        {
            get { return Screen == 0 ? "上屏" : Screen == 1 ? "下屏" : "图片"; }
        }
    }

    // Virtualised thumbnail grid with date sections, multi-select and a thin overlay scrollbar.
    internal class GalleryView : Control
    {
        private readonly List<GalleryItem> items = new List<GalleryItem>();
        private readonly List<KeyValuePair<RectangleF, string[]>> headers = new List<KeyValuePair<RectangleF, string[]>>();
        private readonly Timer scrollTimer = new Timer();
        private int contentHeight;
        private float scroll, target;
        private int hover = -1, focus = -1, anchor = -1;
        private float thumbHeight;
        private bool barDrag, barHover;
        private float barGrab;
        public string EmptyTitle = "图库是空的";
        public string EmptyHint = "连接 Thor 后按 F5 截图";
        public event EventHandler SelectionChanged;
        public event EventHandler ItemActivated;
        public event MouseEventHandler ItemContextMenu;
        public event EventHandler LockActionClicked;
        private MascotClock mascot;

        // Locked: the gallery stays visible but dimmed, with a pill saying why and an optional
        // action. Clicks elsewhere make the pill shake instead of silently doing nothing.
        private string lockLabel, lockAction;
        private Anim lockA, shakeA;
        private Rectangle lockChip = Rectangle.Empty, lockPill = Rectangle.Empty;

        public bool Locked { get { return lockLabel != null; } }

        public void SetLock(string label, string action)
        {
            bool was = lockLabel != null;
            lockLabel = label;
            lockAction = action;
            if (label != null) { hover = -1; Cursor = Cursors.Default; }
            if (was != (label != null)) lockA.To(label != null ? 1 : 0, label != null ? 520 : 200);
            lockA.Curve = label != null ? Ease.Spring : Ease.OutCubic;
            Invalidate();
        }

        // Per-card animation state, keyed by path so a refresh keeps cards that did not change.
        private sealed class CardFx { public Anim Hover, Select, Thumb; public double AppearAt = -1; public bool HadThumb; public GalleryItem Item; }
        private readonly Dictionary<string, CardFx> fx = new Dictionary<string, CardFx>(StringComparer.OrdinalIgnoreCase);
        private readonly Anim appearClock;
        private const int AppearMs = 620, StaggerMs = 34;

        private CardFx Fx(GalleryItem item)
        {
            CardFx card;
            if (item.Path == null) item.Path = "";
            if (!fx.TryGetValue(item.Path, out card))
            {
                card = NewCard(item, item.Thumb != null ? 1 : 0);
                fx[item.Path] = card;
            }
            return card;
        }

        // Card animations repaint only their own card (plus room for lift, shadow and the
        // selection outline), not the whole gallery.
        private CardFx NewCard(GalleryItem item, float thumb)
        {
            CardFx card = new CardFx
            {
                Item = item,
                Hover = new Anim(this, 0, 180, Ease.OutCubic),
                Select = new Anim(this, item.Selected ? 1 : 0, 220, Ease.OutCubic),
                Thumb = new Anim(this, thumb, 260, Ease.OutCubic)
            };
            Func<Rectangle> area = delegate { return card.Item == null ? Rectangle.Empty : CardArea(card.Item); };
            card.Hover.Area = card.Select.Area = card.Thumb.Area = area;
            return card;
        }

        private Rectangle CardArea(GalleryItem item)
        {
            Rectangle r = ToScreen(item.Bounds);
            r.Inflate(Ui.S(12), Ui.S(14));
            r.Intersect(ClientRectangle);
            return r;
        }

        public GalleryView()
        {
            appearClock = new Anim(this, 0, 0, Ease.Linear);
            mascot = new MascotClock(this);
            lockA = new Anim(this, 0, 520, Ease.Spring);
            shakeA = new Anim(this, 1, 460, Ease.Linear);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Ui.Bg1;
            Font = Ui.Body;
            scrollTimer.Interval = 15;
            scrollTimer.Tick += delegate
            {
                float diff = target - scroll;
                if (Math.Abs(diff) < 0.8f) { scroll = target; scrollTimer.Stop(); }
                else scroll += diff * 0.32f;
                Invalidate();
                RaiseViewport();
            };
        }

        // ---- viewport, for on-demand thumbnails ---------------------------------------
        public event EventHandler ViewportChanged;
        private float cardWidth;

        private void RaiseViewport() { if (ViewportChanged != null) ViewportChanged(this, EventArgs.Empty); }

        // Thumbnail edge length in pixels for the current card size, rounded so small
        // resizes reuse the same cache keys.
        public int ThumbPixels
        {
            get
            {
                int edge = (int)Math.Ceiling(Math.Max(cardWidth, thumbHeight));
                edge = (edge + 31) / 32 * 32;
                return Math.Max(96, Math.Min(1024, edge));
            }
        }

        // Items on screen, and items within `prefetch` screens above or below.
        public void Window(float prefetch, List<int> visible, List<int> nearby)
        {
            float height = Math.Max(1, ClientSize.Height);
            float top = scroll, bottom = scroll + height;
            float farTop = top - height * prefetch, farBottom = bottom + height * prefetch;
            for (int i = 0; i < items.Count; i++)
            {
                RectangleF b = items[i].Bounds;
                if (b.Bottom < farTop || b.Y > farBottom) continue;
                if (b.Bottom >= top && b.Y <= bottom) visible.Add(i); else nearby.Add(i);
            }
        }

        // Drops thumbnails more than `keep` screens away so the gallery's own bitmaps stay
        // bounded however large the library is. They come back from the cache on demand.
        public int ReleaseThumbs(float keep)
        {
            float height = Math.Max(1, ClientSize.Height);
            float farTop = scroll - height * keep, farBottom = scroll + height * (1 + keep);
            int released = 0;
            foreach (GalleryItem item in items)
                if (item.Thumb != null && (item.Bounds.Bottom < farTop || item.Bounds.Y > farBottom))
                { item.Thumb.Dispose(); item.Thumb = null; released++; }
            return released;
        }

        public int HeldThumbs
        {
            get { int held = 0; foreach (GalleryItem item in items) if (item.Thumb != null) held++; return held; }
        }

        public List<GalleryItem> Items { get { return items; } }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { scrollTimer.Dispose(); DisposeThumbs(); }
            base.Dispose(disposing);
        }

        private void DisposeThumbs()
        {
            foreach (GalleryItem item in items) if (item.Thumb != null) { item.Thumb.Dispose(); item.Thumb = null; }
        }

        public void SetItems(List<GalleryItem> list)
        {
            // Unchanged files keep their thumbnails across a refilter or rescan.
            Dictionary<string, GalleryItem> previous = new Dictionary<string, GalleryItem>(StringComparer.OrdinalIgnoreCase);
            foreach (GalleryItem old in items) if (old.Thumb != null && old.Path != null) previous[old.Path] = old;
            foreach (GalleryItem item in list)
            {
                GalleryItem old;
                if (item.Thumb == null && item.Path != null && previous.TryGetValue(item.Path, out old) && old.Time == item.Time && old.Size == item.Size)
                { item.Thumb = old.Thumb; old.Thumb = null; }
            }
            DisposeThumbs();
            // New cards rise into place; when the whole list changes (a filter or folder switch)
            // they arrive in a short cascade instead.
            HashSet<string> known = new HashSet<string>(fx.Keys, StringComparer.OrdinalIgnoreCase);
            int fresh = 0;
            foreach (GalleryItem item in list) if (item.Path != null && !known.Contains(item.Path)) fresh++;
            bool cascade = fresh == list.Count;
            Dictionary<string, CardFx> keep = new Dictionary<string, CardFx>(StringComparer.OrdinalIgnoreCase);
            double now = Motion.Now;
            int order = 0;
            foreach (GalleryItem item in list)
            {
                if (item.Path == null) continue;
                CardFx card;
                if (fx.TryGetValue(item.Path, out card)) { card.Item = item; keep[item.Path] = card; continue; }
                card = NewCard(item, 0);
                if (Motion.Enabled && Visible) card.AppearAt = now + (cascade ? Math.Min(order++, 14) * StaggerMs : 0);
                keep[item.Path] = card;
            }
            fx.Clear();
            foreach (KeyValuePair<string, CardFx> pair in keep) fx[pair.Key] = pair.Value;
            appearClock.Snap(0);
            if (fresh > 0) appearClock.To(1, AppearMs + 15 * StaggerMs + 40);
            items.Clear();
            items.AddRange(list);
            hover = focus = anchor = -1;
            scroll = target = 0;
            Relayout();
            Invalidate();
        }

        public void SetThumb(int index, Bitmap thumb)
        {
            if (index < 0 || index >= items.Count) { thumb.Dispose(); return; }
            if (items[index].Thumb != null) items[index].Thumb.Dispose();
            items[index].Thumb = thumb;
            // Only a card's first thumbnail fades in; reloads after a refresh swap in place.
            CardFx card = Fx(items[index]);
            if (card.HadThumb) card.Thumb.Snap(1); else card.Thumb.Play(0, 1);
            card.HadThumb = true;
            Invalidate(ToScreen(items[index].Bounds));
        }

        public List<GalleryItem> SelectedItems
        {
            get
            {
                List<GalleryItem> list = new List<GalleryItem>();
                foreach (GalleryItem item in items) if (item.Selected) list.Add(item);
                return list;
            }
        }

        public GalleryItem Primary
        {
            get
            {
                if (focus >= 0 && focus < items.Count && items[focus].Selected) return items[focus];
                foreach (GalleryItem item in items) if (item.Selected) return item;
                return null;
            }
        }

        public int IndexOf(string path)
        {
            for (int i = 0; i < items.Count; i++)
                if (string.Equals(items[i].Path, path, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public void SelectOnly(int index)
        {
            foreach (GalleryItem item in items) item.Selected = false;
            if (index >= 0 && index < items.Count)
            {
                items[index].Selected = true;
                focus = anchor = index;
                EnsureVisible(index);
            }
            else focus = anchor = -1;
            Invalidate();
            RaiseSelection();
        }

        private void RaiseSelection()
        {
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        private int Pad { get { return Ui.S(14); } }
        private int GapSize { get { return Ui.S(10); } }
        private int TextHeight { get { return Ui.S(36); } }

        private void Relayout()
        {
            headers.Clear();
            int pad = Pad, gap = GapSize;
            int width = Math.Max(Ui.S(120), ClientSize.Width - pad * 2 - Ui.S(4));
            int columns = Math.Max(1, (width + gap) / (Ui.S(150) + gap));
            cardWidth = (width - gap * (columns - 1)) / (float)columns;
            thumbHeight = (float)Math.Round(cardWidth * 0.66f);
            float cardHeight = thumbHeight + TextHeight;
            Dictionary<DateTime, int> perDay = new Dictionary<DateTime, int>();
            foreach (GalleryItem item in items)
            {
                int count;
                perDay.TryGetValue(item.Time.Date, out count);
                perDay[item.Time.Date] = count + 1;
            }
            float y = Ui.S(2);
            int column = 0;
            DateTime day = DateTime.MinValue;
            foreach (GalleryItem item in items)
            {
                if (item.Time.Date != day)
                {
                    if (column != 0) { y += cardHeight + gap; column = 0; }
                    day = item.Time.Date;
                    headers.Add(new KeyValuePair<RectangleF, string[]>(new RectangleF(pad, y, width, Ui.S(36)),
                        new string[] { Ui.DayLabel(day), perDay[day] + " 项" }));
                    y += Ui.S(36);
                }
                item.Bounds = new RectangleF(pad + column * (cardWidth + gap), y, cardWidth, cardHeight);
                if (++column == columns) { column = 0; y += cardHeight + gap; }
            }
            if (column != 0) y += cardHeight + gap;
            contentHeight = (int)(y + pad);
            target = Clamp(target);
            scroll = Clamp(scroll);
            RaiseViewport();
        }

        private float MaxScroll { get { return Math.Max(0, contentHeight - ClientSize.Height); } }

        private float Clamp(float value) { return Math.Max(0, Math.Min(MaxScroll, value)); }

        private Rectangle ToScreen(RectangleF bounds)
        {
            return new Rectangle((int)bounds.X - 2, (int)(bounds.Y - scroll) - 2, (int)bounds.Width + 4, (int)bounds.Height + 4);
        }

        public void EnsureVisible(int index)
        {
            if (index < 0 || index >= items.Count) return;
            RectangleF b = items[index].Bounds;
            float top = b.Y - Ui.S(40);
            float bottom = b.Bottom + GapSize;
            if (top < target) target = Clamp(top);
            else if (bottom > target + ClientSize.Height) target = Clamp(bottom - ClientSize.Height);
            if (Math.Abs(target - scroll) > 0.5f) scrollTimer.Start();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
            Invalidate();
        }

        private int HitTest(Point p)
        {
            float y = p.Y + scroll;
            for (int i = 0; i < items.Count; i++)
                if (items[i].Bounds.Contains(p.X, y)) return i;
            return -1;
        }

        private Rectangle BarThumb()
        {
            if (MaxScroll <= 0) return Rectangle.Empty;
            int h = ClientSize.Height;
            int thumb = Math.Max(Ui.S(36), (int)((long)h * h / contentHeight));
            int y = (int)((h - thumb) * (scroll / MaxScroll));
            int w = barHover || barDrag ? Ui.S(8) : Ui.S(5);
            return new Rectangle(ClientSize.Width - w - Ui.S(3), y, w, thumb);
        }

        private bool InBarZone(Point p) { return MaxScroll > 0 && p.X >= ClientSize.Width - Ui.S(14); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (Locked && !barDrag)
            {
                Cursor = lockChip.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
                return;
            }
            if (barDrag)
            {
                Rectangle thumb = BarThumb();
                float range = ClientSize.Height - thumb.Height;
                if (range > 0) scroll = target = Clamp((e.Y - barGrab) / range * MaxScroll);
                Invalidate();
                RaiseViewport();
                return;
            }
            bool nearBar = InBarZone(e.Location);
            int index = nearBar ? -1 : HitTest(e.Location);
            if (index != hover || nearBar != barHover)
            {
                // Repaint only the cards whose hover state changed, and the scrollbar lane.
                if (hover >= 0 && hover < items.Count) Invalidate(CardArea(items[hover]));
                if (index >= 0) Invalidate(CardArea(items[index]));
                if (nearBar != barHover) Invalidate(new Rectangle(ClientSize.Width - Ui.S(16), 0, Ui.S(16), ClientSize.Height));
                hover = index;
                barHover = nearBar;
            }
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hover >= 0 && hover < items.Count) Invalidate(CardArea(items[hover]));
            if (barHover) Invalidate(new Rectangle(ClientSize.Width - Ui.S(16), 0, Ui.S(16), ClientSize.Height));
            hover = -1;
            barHover = false;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (Locked && !InBarZone(e.Location))
            {
                if (lockChip.Contains(e.Location)) { if (LockActionClicked != null) LockActionClicked(this, EventArgs.Empty); }
                else shakeA.Play(0, 1);
                return;
            }
            if (e.Button == MouseButtons.Left && InBarZone(e.Location))
            {
                Rectangle thumb = BarThumb();
                if (thumb.Contains(e.X, e.Y) || (e.Y >= thumb.Y && e.Y <= thumb.Bottom)) barGrab = e.Y - thumb.Y;
                else
                {
                    barGrab = thumb.Height / 2f;
                    float range = ClientSize.Height - thumb.Height;
                    if (range > 0) scroll = target = Clamp((e.Y - barGrab) / range * MaxScroll);
                    RaiseViewport();
                }
                barDrag = true;
                Capture = true;
                Invalidate();
                return;
            }
            int index = HitTest(e.Location);
            if (e.Button == MouseButtons.Left)
            {
                if (index >= 0)
                {
                    if ((ModifierKeys & Keys.Control) != 0)
                    {
                        items[index].Selected = !items[index].Selected;
                        focus = anchor = index;
                        Invalidate();
                        RaiseSelection();
                    }
                    else if ((ModifierKeys & Keys.Shift) != 0 && anchor >= 0) SelectRange(anchor, index);
                    else SelectOnly(index);
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                if (index >= 0 && !items[index].Selected) SelectOnly(index);
                if (index >= 0 && ItemContextMenu != null) ItemContextMenu(this, e);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (barDrag) { barDrag = false; Capture = false; Invalidate(); }
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (Locked) return;
            if (e.Button == MouseButtons.Left && HitTest(e.Location) >= 0 && ItemActivated != null) ItemActivated(this, EventArgs.Empty);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            target = Clamp(target - e.Delta * Ui.S(110) / 120f);
            scrollTimer.Start();
            base.OnMouseWheel(e);
        }

        private void SelectRange(int from, int to)
        {
            int a = Math.Min(from, to), b = Math.Max(from, to);
            for (int i = 0; i < items.Count; i++) items[i].Selected = i >= a && i <= b;
            focus = to;
            EnsureVisible(to);
            Invalidate();
            RaiseSelection();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        // Up/Down follow the geometry, so they behave across date sections.
        private int Vertical(int from, int direction)
        {
            RectangleF b = items[from].Bounds;
            float cx = b.X + b.Width / 2;
            int best = -1;
            float bestRow = 0, bestDx = 0;
            for (int i = 0; i < items.Count; i++)
            {
                float y = items[i].Bounds.Y;
                if (direction < 0 ? y >= b.Y - 1 : y <= b.Y + 1) continue;
                float row = Math.Abs(y - b.Y);
                float dx = Math.Abs(items[i].Bounds.X + items[i].Bounds.Width / 2 - cx);
                if (best < 0 || row < bestRow - 1 || (Math.Abs(row - bestRow) <= 1 && dx < bestDx)) { best = i; bestRow = row; bestDx = dx; }
            }
            return best < 0 ? from : best;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Locked) { base.OnKeyDown(e); return; }
            if (items.Count == 0) { base.OnKeyDown(e); return; }
            if (e.Control && e.KeyCode == Keys.A)
            {
                foreach (GalleryItem item in items) item.Selected = true;
                Invalidate();
                RaiseSelection();
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.Enter) { if (ItemActivated != null) ItemActivated(this, EventArgs.Empty); e.Handled = true; return; }
            int from = focus < 0 ? 0 : focus;
            int next = -1;
            int rows = Math.Max(1, (int)(ClientSize.Height / (thumbHeight + TextHeight + GapSize)));
            switch (e.KeyCode)
            {
                case Keys.Left: next = Math.Max(0, from - 1); break;
                case Keys.Right: next = Math.Min(items.Count - 1, from + 1); break;
                case Keys.Up: next = Vertical(from, -1); break;
                case Keys.Down: next = Vertical(from, 1); break;
                case Keys.Home: next = 0; break;
                case Keys.End: next = items.Count - 1; break;
                case Keys.PageUp: next = from; for (int i = 0; i < rows; i++) next = Vertical(next, -1); break;
                case Keys.PageDown: next = from; for (int i = 0; i < rows; i++) next = Vertical(next, 1); break;
            }
            if (next >= 0)
            {
                if (focus < 0) next = 0;
                if (e.Shift && anchor >= 0) SelectRange(anchor, next);
                else SelectOnly(next);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            mascot.Run(items.Count == 0 && Visible);
            if (items.Count == 0)
            {
                Rectangle area = ClientRectangle;
                int d = Ui.S(96), mid = area.Height / 2 - Ui.S(30);
                mascot.Area = new Rectangle(area.Width / 2 - d / 2 - Ui.S(20), mid - d - Ui.S(20), d + Ui.S(40), d + Ui.S(26));
                Mascot.Draw(g, new RectangleF(area.Width / 2 - d / 2, mid - d, d, d), MascotMood.Idle, mascot.Time);
                Ui.Write(g, EmptyTitle, Ui.Strong, new Rectangle(0, mid + Ui.S(18), area.Width, Ui.S(24)), Ui.TextDim, TextFormatFlags.HorizontalCenter);
                return;
            }
            float top = scroll, bottom = scroll + ClientSize.Height;
            foreach (KeyValuePair<RectangleF, string[]> header in headers)
            {
                RectangleF h = header.Key;
                if (h.Bottom < top || h.Y > bottom) continue;
                Rectangle r = new Rectangle((int)h.X + Ui.S(2), (int)(h.Y - scroll), (int)h.Width, (int)h.Height);
                int w = Ui.Measure(header.Value[0], Ui.Strong);
                Ui.Write(g, header.Value[0], Ui.Strong, r, Ui.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                Ui.Write(g, header.Value[1], Ui.Small, new Rectangle(r.X + w + Ui.S(8), r.Y, r.Width - w, r.Height), Ui.TextFaint,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
            for (int i = 0; i < items.Count; i++)
            {
                RectangleF b = items[i].Bounds;
                if (b.Bottom < top || b.Y > bottom) continue;
                DrawCard(g, items[i], new RectangleF(b.X, b.Y - scroll, b.Width, b.Height), i == hover, i == focus && Focused);
                if (Motion.Enabled) Fx(items[i]).Hover.Target = i == hover ? 1 : 0;
            }
            PaintLock(g);
            Rectangle bar = BarThumb();
            if (!bar.IsEmpty)
                Ui.Fill(g, bar, bar.Width / 2f, Ui.Wash(barDrag ? 0.4f : barHover ? 0.28f : 0.14f));
        }

        private void PaintLock(Graphics g)
        {
            float v = lockA.Value;
            if (v <= 0.001f) { lockChip = lockPill = Rectangle.Empty; return; }
            using (SolidBrush veil = new SolidBrush(Color.FromArgb((int)(175 * Math.Min(1, v)), BackColor))) g.FillRectangle(veil, ClientRectangle);
            if (lockLabel == null) return;
            int chipW = lockAction == null ? 0 : Ui.Measure(lockAction, Ui.Strong) + Ui.S(24);
            int w = Ui.S(38) + Ui.Measure(lockLabel, Ui.Strong) + (chipW > 0 ? chipW + Ui.S(10) : Ui.S(18)), h = Ui.S(44);
            // Shake: a decaying side-to-side wobble.
            float t = shakeA.Value, shake = t >= 1 ? 0 : (float)(Math.Sin(t * Math.PI * 6) * (1 - t)) * Ui.S(9);
            // The pill pops in on the lock spring; TextRenderer ignores transforms, so the
            // geometry is scaled by hand and the text is laid out in client coordinates.
            float scale = Math.Max(0, v);
            float cx = ClientSize.Width / 2f + shake, cy = ClientSize.Height / 2f;
            RectangleF pill = new RectangleF(cx - w * scale / 2, cy - h * scale / 2, w * scale, h * scale);
            Ui.Shadow(g, pill, pill.Height / 2, Ui.S(10), 40, Ui.S(4), Ui.Ink);
            Ui.Fill(g, pill, pill.Height / 2, Ui.AccentDeep);
            lockChip = Rectangle.Empty;
            if (scale < 0.7f) return;
            float left = cx - w / 2f;
            using (SolidBrush dot = new SolidBrush(Ui.Ok)) g.FillEllipse(dot, left + Ui.S(18), cy - Ui.S(4), Ui.S(8), Ui.S(8));
            Ui.Write(g, lockLabel, Ui.Strong, new Rectangle((int)left + Ui.S(34), (int)(cy - h / 2f), Ui.Measure(lockLabel, Ui.Strong) + Ui.S(4), h), Color.White,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            if (chipW > 0)
            {
                RectangleF chip = new RectangleF(cx + w / 2f - chipW - Ui.S(6), cy - h / 2f + Ui.S(6), chipW, h - Ui.S(12));
                Ui.Fill(g, chip, chip.Height / 2, Color.White);
                lockChip = Rectangle.Round(chip);
                Ui.Write(g, lockAction, Ui.Strong, lockChip, Ui.AccentDeep, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private void DrawCard(Graphics g, GalleryItem item, RectangleF r, bool hot, bool focused)
        {
            float radius = Ui.S(14);
            CardFx card = Fx(item);
            card.Select.Target = item.Selected ? 1 : 0;
            float h = Motion.Enabled ? card.Hover.Value : (hot ? 1 : 0);
            float sel = card.Select.Value;
            float appear = 1, t = 1;
            if (card.AppearAt >= 0)
            {
                t = (float)((Motion.Now - card.AppearAt) / AppearMs);
                appear = Motion.Apply(Ease.Spring, t);
                if (t >= 1) card.AppearAt = -1;
            }
            if (t <= 0) return;
            // New cards pop in from their centre and settle with a small wobble.
            GraphicsState state = g.Save();
            if (appear != 1)
            {
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                float scale = 0.6f + 0.4f * appear;
                g.TranslateTransform(cx, cy);
                g.ScaleTransform(scale + (appear - 1) * 0.3f, scale - (appear - 1) * 0.3f);
                g.TranslateTransform(-cx, -cy);
            }
            // Hover brings the tile forward to white; nothing glows.
            r.Y -= h * Ui.S(2);
            if (h > 0.01f) Ui.Shadow(g, r, radius, Ui.S(8), (int)(22 * h), Ui.S(3), Ui.Ink);
            Ui.Fill(g, r, radius, Ui.Mix(Ui.Card, Ui.Surface, Math.Max(h, sel)));
            DrawCardBody(g, item, r, radius, card, h, sel, focused);
            g.Restore(state);
        }

        private void DrawCardBody(Graphics g, GalleryItem item, RectangleF r, float radius, CardFx card, float hoverAmount, float sel, bool focused)
        {
            int p = Ui.S(6);
            Rectangle thumbArea = new Rectangle((int)r.X + p, (int)r.Y + p, (int)r.Width - p * 2, (int)thumbHeight - p);
            using (GraphicsPath clip = Ui.Round(thumbArea, Ui.S(10)))
            {
                using (SolidBrush back = new SolidBrush(Ui.Bg2)) g.FillPath(back, clip);
                if (item.Thumb != null)
                {
                    float scale = Math.Min((float)thumbArea.Width / item.Thumb.Width, (float)thumbArea.Height / item.Thumb.Height);
                    if (item.IsVideo) scale = Math.Max((float)thumbArea.Width / item.Thumb.Width, (float)thumbArea.Height / item.Thumb.Height);
                    int w = (int)Math.Round(item.Thumb.Width * scale), h = (int)Math.Round(item.Thumb.Height * scale);
                    Region saved = g.Clip;
                    g.SetClip(clip, CombineMode.Intersect);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    Rectangle dest = new Rectangle(thumbArea.X + (thumbArea.Width - w) / 2, thumbArea.Y + (thumbArea.Height - h) / 2, w, h);
                    float shown = card.Thumb.Value;
                    if (shown >= 1 || !Motion.Enabled) g.DrawImage(item.Thumb, dest);
                    else
                        using (System.Drawing.Imaging.ImageAttributes fade = new System.Drawing.Imaging.ImageAttributes())
                        {
                            System.Drawing.Imaging.ColorMatrix matrix = new System.Drawing.Imaging.ColorMatrix();
                            matrix.Matrix33 = shown;
                            fade.SetColorMatrix(matrix);
                            g.DrawImage(item.Thumb, dest, 0, 0, item.Thumb.Width, item.Thumb.Height, GraphicsUnit.Pixel, fade);
                        }
                    g.Clip = saved;
                    saved.Dispose();
                }
                else
                {
                    Rectangle loading = thumbArea;
                    loading.Inflate(-thumbArea.Width / 3, -thumbArea.Height / 3);
                    Ui.Fill(g, loading, Ui.S(4), Ui.Wash(0.03f));
                }
                if (item.IsVideo)
                {
                    int d = Ui.S(34);
                    Rectangle play = new Rectangle(thumbArea.X + (thumbArea.Width - d) / 2, thumbArea.Y + (thumbArea.Height - d) / 2, d, d);
                    using (SolidBrush disc = new SolidBrush(Color.FromArgb(230, 255, 255, 255))) g.FillEllipse(disc, play);
                    float cx = play.X + d / 2f + Ui.S(1.5f), cy = play.Y + d / 2f, s = Ui.S(7);
                    using (SolidBrush arrow = new SolidBrush(Ui.Text))
                        g.FillPolygon(arrow, new PointF[] { new PointF(cx - s * 0.7f, cy - s), new PointF(cx - s * 0.7f, cy + s), new PointF(cx + s, cy) });
                }
            }

            // Footer: screen badge, time, pair marker.
            int footerY = (int)(r.Y + thumbHeight);
            int footerH = (int)r.Bottom - footerY;
            // Thor pictogram with the captured screen filled, then the time.
            int icon = Ui.S(14);
            RectangleF pict = new RectangleF(r.X + p + Ui.S(4), footerY + (footerH - icon) / 2f, icon, icon);
            Ui.Thor(g, pict, item.Screen, Ui.TextDim);
            int tx = (int)pict.Right + Ui.S(8);
            if (item.IsVideo)
            {
                using (SolidBrush rec = new SolidBrush(Ui.Danger)) g.FillEllipse(rec, tx, footerY + footerH / 2f - Ui.S(3), Ui.S(6), Ui.S(6));
                tx += Ui.S(12);
            }
            int right = (int)r.Right - p;
            if (item.Paired && Ui.IconSmall != null)
            {
                right -= Ui.S(16);
                Ui.Glyph(g, Glyphs.Link, Ui.IconSmall, new Rectangle(right, footerY, Ui.S(16), footerH), Ui.TextFaint);
            }
            Ui.Write(g, item.Time.ToString("HH:mm:ss"), Ui.Small, new Rectangle(tx, footerY, Math.Max(0, right - tx), footerH), Ui.TextDim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            if (sel > 0.01f)
            {
                // Selection: a dark outline that draws in from slightly outside the tile.
                RectangleF edge = r;
                float grow = (1 - sel) * Ui.S(4);
                edge.Inflate(Ui.S(1) + grow, Ui.S(1) + grow);
                Ui.Stroke(g, edge, radius + Ui.S(1) + grow, Ui.Alpha(Ui.Accent, (int)(255 * sel)), Math.Max(1.5f, Ui.S(2)));
            }
            else if (focused)
            {
                RectangleF edge = r;
                edge.Inflate(-0.5f, -0.5f);
                Ui.Stroke(g, edge, radius, Ui.Wash(0.2f), 1f);
            }
        }
    }
}
