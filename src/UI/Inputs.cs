// Owner-drawn replacements for the stock inputs (TrackBar, ComboBox, NumericUpDown,
// ProgressBar) so dialogs and the player match the light theme and animate like the
// rest of the chrome.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace ThorCapture
{
    // Media seek bar: a thin track that thickens on hover, an accent fill, a knob that
    // grows while dragging and a time bubble that follows the pointer.
    internal sealed class SeekBar : Control
    {
        private int minimum, maximum = 10000, current;
        private readonly Anim hoverA, dragA;
        private int hoverX = -1;
        private bool dragging;
        public Func<int, string> HoverLabel;
        public event EventHandler ValueChanged;

        public SeekBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            hoverA = new Anim(this, 0, 160, Ease.OutCubic);
            dragA = new Anim(this, 0, 260, Ease.OutBack);
            Cursor = Cursors.Hand;
            Height = Ui.S(46);
        }

        public int Minimum { get { return minimum; } set { minimum = value; Invalidate(); } }
        public int Maximum { get { return maximum; } set { maximum = Math.Max(minimum + 1, value); Invalidate(); } }

        public int Value
        {
            get { return current; }
            set
            {
                int v = Math.Max(minimum, Math.Min(maximum, value));
                if (v == current) return;
                current = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        private int Pad { get { return Ui.S(9); } }
        private float Center { get { return Height - Ui.S(13); } }
        private float Fraction(int v) { return (v - minimum) / (float)Math.Max(1, maximum - minimum); }

        private int FromX(int x)
        {
            float t = (x - Pad) / (float)Math.Max(1, Width - Pad * 2);
            return minimum + (int)Math.Round(Math.Max(0, Math.Min(1, t)) * (maximum - minimum));
        }

        protected override void OnMouseEnter(EventArgs e) { hoverA.To(1, 160); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hoverA.To(0, 260); hoverX = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            hoverX = e.X;
            if (dragging) Value = FromX(e.X);
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // Raise MouseDown first so listeners can pause before the value moves.
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            dragging = true;
            Capture = true;
            dragA.To(1, 260);
            Value = FromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging)
            {
                Value = FromX(e.X);
                dragging = false;
                dragA.To(0, 220);
                Capture = false;
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (dragging && !Capture) { dragging = false; dragA.To(0, 220); }
            base.OnMouseCaptureChanged(e);
        }

        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = Enabled ? hoverA.Value : 0, d = dragA.Value;
            float thick = Ui.S(4) + Ui.S(2) * Math.Max(h, Math.Min(1, d));
            float x0 = Pad, width = Width - Pad * 2, cy = Center;
            RectangleF track = new RectangleF(x0, cy - thick / 2, width, thick);
            Ui.Fill(g, track, thick / 2, Enabled ? Ui.Bg3 : Ui.Bg2);
            if (hoverX >= 0 && Enabled && h > 0.01f)
            {
                float hx = Math.Max(x0, Math.Min(x0 + width, hoverX));
                Ui.Fill(g, new RectangleF(x0, track.Y, Math.Max(thick, hx - x0), thick), thick / 2, Ui.Wash(0.07f * h));
            }
            float fx = x0 + width * Fraction(current);
            if (fx > x0 + 0.5f)
                Ui.Fill(g, new RectangleF(x0, track.Y, Math.Max(thick, fx - x0), thick), thick / 2, Enabled ? Ui.Accent : Ui.Bg3);
            if (!Enabled) return;

            // Knob: a white bead that swells on hover and flattens into a lozenge while dragged.
            float r = Ui.S(6) + Ui.S(1.5f) * h;
            float squash = Math.Min(1.2f, d);
            RectangleF knob = new RectangleF(fx - r * (1 + 0.25f * squash), cy - r * (1 - 0.15f * squash), r * 2 * (1 + 0.25f * squash), r * 2 * (1 - 0.15f * squash));
            Ui.Shadow(g, knob, knob.Height / 2, Ui.S(3), 36, Ui.S(1), Ui.Ink);
            Ui.Fill(g, knob, knob.Height / 2, Color.White);

            // Time bubble: follows the pointer on hover, sits on the knob while dragging.
            float show = Math.Max(h, Math.Min(1, d));
            if (HoverLabel != null && show > 0.02f && (hoverX >= 0 || dragging))
            {
                int at = dragging ? current : FromX(hoverX);
                string label = HoverLabel(at);
                if (string.IsNullOrEmpty(label)) return;
                float bx = dragging ? fx : Math.Max(x0, Math.Min(x0 + width, hoverX));
                int bw = Ui.Measure(label, Ui.Small) + Ui.S(14), bh = Ui.S(20);
                float left = Math.Max(0, Math.Min(Width - bw, bx - bw / 2f));
                float top = cy - Ui.S(12) - bh - (1 - show) * Ui.S(4);
                RectangleF bubble = new RectangleF(left, top, bw, bh);
                int alpha = (int)(235 * show);
                Ui.Fill(g, bubble, Ui.S(6), Ui.Alpha(Ui.Ink, alpha));
                using (SolidBrush tip = new SolidBrush(Ui.Alpha(Ui.Ink, alpha)))
                    g.FillPolygon(tip, new PointF[] { new PointF(bx - Ui.S(4), bubble.Bottom - 0.5f), new PointF(bx + Ui.S(4), bubble.Bottom - 0.5f), new PointF(bx, bubble.Bottom + Ui.S(4)) });
                Ui.Write(g, label, Ui.Small, Rectangle.Round(bubble), Ui.Alpha(Color.White, alpha), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    // Drop-down field: shows the current choice, opens a themed menu under itself and
    // turns its chevron while open. Mirrors the bits of the ComboBox API the app used.
    internal sealed class ChoiceBox : Control
    {
        private readonly List<object> items = new List<object>();
        private int selected = -1;
        private readonly Anim hoverA, openA;
        private ContextMenuStrip menu;
        public event EventHandler SelectedIndexChanged;

        public ChoiceBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            hoverA = new Anim(this, 0, 150, Ease.OutCubic);
            openA = new Anim(this, 0, 240, Ease.OutBack);
            Font = Ui.Body;
            Cursor = Cursors.Hand;
            Height = Ui.S(36);
        }

        public List<object> Items { get { return items; } }
        public object SelectedItem { get { return selected >= 0 && selected < items.Count ? items[selected] : null; } }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value < -1 || value >= items.Count || value == selected) return;
                selected = value;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hoverA.To(1, 150); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hoverA.To(0, 240); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled || items.Count == 0) return;
            if (menu != null) { menu.Close(); return; }
            menu = Menus.Create();
            menu.ShowCheckMargin = true;
            menu.MinimumSize = new Size(Width, 0);
            for (int i = 0; i < items.Count; i++)
            {
                int index = i;
                ToolStripMenuItem item = Menus.Add(menu, Convert.ToString(items[i]), null, delegate { SelectedIndex = index; });
                item.Checked = i == selected;
                item.Padding = new Padding(0, Ui.S(5), 0, Ui.S(5));
            }
            ContextMenuStrip shown = menu;
            menu.Closed += delegate
            {
                openA.To(0, 200);
                BeginInvoke((Action)delegate { if (menu == shown) menu = null; shown.Dispose(); });
            };
            openA.To(1, 240);
            menu.Show(this, new Point(0, Height + Ui.S(4)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = Enabled ? hoverA.Value : 0, o = openA.Value;
            RectangleF box = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Ui.Fill(g, box, box.Height / 2, !Enabled ? Ui.Bg2 : Ui.Mix(Ui.Mix(Ui.Bg2, Ui.Bg3, h), Ui.Surface, Math.Min(1, o)));
            if (o > 0.01f) Ui.Stroke(g, box, box.Height / 2, Ui.Alpha(Ui.Bg3, (int)(255 * Math.Min(1, o))), 1f);
            string text = SelectedItem == null ? "" : Convert.ToString(SelectedItem);
            Rectangle area = new Rectangle(Ui.S(16), 0, Width - Ui.S(48), Height);
            Ui.Write(g, text, Font, area, Enabled ? Ui.Text : Ui.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            // Chevron drawn as strokes so it can turn with the open state.
            float cx = Width - Ui.S(20), cy = Height / 2f, s = Ui.S(4);
            GraphicsState state = g.Save();
            g.TranslateTransform(cx, cy);
            g.RotateTransform(180 * o);
            using (Pen pen = new Pen(Enabled ? Ui.Mix(Ui.TextDim, Ui.Text, Math.Min(1, Math.Max(h, o))) : Ui.TextFaint, Math.Max(1.4f, Ui.S(1.6f))))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new PointF[] { new PointF(-s, -s / 2), new PointF(0, s / 2), new PointF(s, -s / 2) });
            }
            g.Restore(state);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && menu != null) { menu.Dispose(); menu = null; }
            base.Dispose(disposing);
        }
    }

    // Numeric field with − / + steppers, press-and-hold repeat, wheel and arrow keys,
    // and an optional unit label. Mirrors the NumericUpDown API the dialogs used.
    internal sealed class NumberBox : Control
    {
        private readonly TextBox box = new TextBox();
        private decimal minimum, maximum = 100, increment = 1, current;
        private int decimals;
        private string unit = "";
        private readonly Anim focusA, minusA, plusA, pressA;
        private readonly Timer repeat = new Timer();
        private int repeatStep, hoverZone;
        public event EventHandler ValueChanged;

        public NumberBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            focusA = new Anim(this, 0, 200, Ease.OutCubic);
            minusA = new Anim(this, 0, 150, Ease.OutCubic);
            plusA = new Anim(this, 0, 150, Ease.OutCubic);
            pressA = new Anim(this, 0, 90, Ease.OutCubic);
            Height = Ui.S(36);
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Ui.Bg2;
            box.ForeColor = Ui.Text;
            box.Font = Ui.Strong;
            box.TextAlign = HorizontalAlignment.Center;
            Controls.Add(box);
            box.GotFocus += delegate { focusA.Target = 1; box.BackColor = Ui.Surface; };
            box.LostFocus += delegate { focusA.Target = 0; box.BackColor = Enabled ? Ui.Bg2 : Ui.Bg1; Commit(); };
            box.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { Commit(); box.SelectAll(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Up) { Step(1); e.Handled = true; }
                else if (e.KeyCode == Keys.Down) { Step(-1); e.Handled = true; }
            };
            box.MouseWheel += delegate(object sender, MouseEventArgs e) { Step(e.Delta > 0 ? 1 : -1); };
            repeat.Tick += delegate { repeat.Interval = 55; Step(repeatStep); };
            Display();
        }

        public decimal Minimum { get { return minimum; } set { minimum = value; if (current < value) Value = value; } }
        public decimal Maximum { get { return maximum; } set { maximum = value; if (current > value) Value = value; } }
        public decimal Increment { get { return increment; } set { increment = value; } }
        public int DecimalPlaces { get { return decimals; } set { decimals = Math.Max(0, value); Display(); } }
        public string Unit { get { return unit; } set { unit = value ?? ""; PerformLayout(); Invalidate(); } }

        public decimal Value
        {
            get { return current; }
            set
            {
                decimal v = Math.Round(Math.Max(minimum, Math.Min(maximum, value)), decimals);
                if (v == current) { Display(); return; }
                current = v;
                Display();
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        private void Display() { box.Text = current.ToString("F" + decimals, CultureInfo.InvariantCulture); }

        private void Commit()
        {
            decimal parsed;
            if (decimal.TryParse(box.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out parsed)) Value = parsed;
            else Display();
        }

        private void Step(int direction)
        {
            if (!Enabled) return;
            Commit();
            // Snap to the increment grid so repeated steps land on round numbers.
            decimal next = direction > 0 ? Math.Floor(current / increment + 1) * increment : Math.Ceiling(current / increment - 1) * increment;
            Value = next;
            pressA.Play(1, 0);
        }

        private int Zone { get { return Height; } }
        private Rectangle MinusArea { get { return new Rectangle(0, 0, Zone, Height); } }
        private Rectangle PlusArea { get { return new Rectangle(Width - Zone, 0, Zone, Height); } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int unitWidth = unit.Length == 0 ? 0 : Ui.Measure(unit, Ui.Small) + Ui.S(6);
            int left = Zone + Ui.S(2), width = Math.Max(10, Width - Zone * 2 - Ui.S(4) - unitWidth);
            box.Location = new Point(left, (Height - box.Height) / 2);
            box.Width = width;
        }

        protected override void OnEnabledChanged(EventArgs e) { box.Enabled = Enabled; box.BackColor = Enabled ? Ui.Bg2 : Ui.Bg1; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int zone = MinusArea.Contains(e.Location) ? -1 : PlusArea.Contains(e.Location) ? 1 : 0;
            if (zone != hoverZone)
            {
                hoverZone = zone;
                minusA.Target = zone == -1 ? 1 : 0;
                plusA.Target = zone == 1 ? 1 : 0;
            }
            Cursor = zone != 0 && Enabled ? Cursors.Hand : Cursors.IBeam;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hoverZone = 0; minusA.Target = plusA.Target = 0; repeat.Stop(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled)
            {
                int zone = MinusArea.Contains(e.Location) ? -1 : PlusArea.Contains(e.Location) ? 1 : 0;
                if (zone != 0) { repeatStep = zone; Step(zone); repeat.Interval = 380; repeat.Start(); }
                else box.Focus();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { repeat.Stop(); base.OnMouseUp(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { Step(e.Delta > 0 ? 1 : -1); base.OnMouseWheel(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float f = focusA.Value;
            RectangleF field = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Ui.Fill(g, field, field.Height / 2, !Enabled ? Ui.Bg1 : Ui.Mix(Ui.Bg2, Ui.Surface, f));
            if (f > 0 || !Enabled) Ui.Stroke(g, field, field.Height / 2, Enabled ? Ui.Alpha(Ui.Bg3, (int)(255 * f)) : Ui.Bg2, 1f);
            Stepper(g, MinusArea, false, minusA.Value);
            Stepper(g, PlusArea, true, plusA.Value);
            if (unit.Length > 0)
            {
                int uw = Ui.Measure(unit, Ui.Small) + Ui.S(6);
                Ui.Write(g, unit, Ui.Small, new Rectangle(Width - Zone - uw - Ui.S(2), 0, uw, Height), Ui.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
        }

        private void Stepper(Graphics g, Rectangle area, bool plus, float hover)
        {
            RectangleF pad = area;
            pad.Inflate(-Ui.S(4), -Ui.S(4));
            // The stepper under the pointer becomes a white button that dips when pressed.
            float dip = pressA.Value * hover;
            pad.Inflate(-dip * Ui.S(1.5f), -dip * Ui.S(1.5f));
            if (hover > 0.01f && Enabled) Ui.Fill(g, pad, pad.Height / 2, Ui.Alpha(Ui.Surface, (int)(255 * hover)));
            float cx = area.X + area.Width / 2f, cy = area.Y + area.Height / 2f, s = Ui.S(5);
            Color ink = !Enabled ? Ui.TextFaint : Ui.Mix(Ui.TextDim, Ui.Text, hover);
            using (Pen pen = new Pen(ink, Math.Max(1.4f, Ui.S(1.6f))))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx - s, cy, cx + s, cy);
                if (plus) g.DrawLine(pen, cx, cy - s, cx, cy + s);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) repeat.Dispose();
            base.Dispose(disposing);
        }
    }

    // A grid of selectable cards (icon, title, detail). The chosen card tints to the
    // accent and gets a check badge that pops in.
    internal sealed class OptionCards : Control
    {
        public sealed class Option { public string Glyph, Title, Detail; }

        private readonly List<Option> options = new List<Option>();
        private readonly List<Anim> hoverA = new List<Anim>(), selectA = new List<Anim>();
        private int selected = -1, hover = -1;
        public int Columns = 3;
        public event EventHandler SelectedIndexChanged;

        public OptionCards()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Font = Ui.Body;
        }

        public void Add(string glyph, string title, string detail)
        {
            options.Add(new Option { Glyph = glyph, Title = title, Detail = detail });
            hoverA.Add(new Anim(this, 0, 170, Ease.OutCubic));
            selectA.Add(new Anim(this, 0, 480, Ease.Spring));
            Invalidate();
        }

        public int Count { get { return options.Count; } }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value < -1 || value >= options.Count || value == selected) return;
                if (selected >= 0) selectA[selected].To(0, 180);
                selected = value;
                if (selected >= 0) selectA[selected].To(1, 480);
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        private int Gap { get { return Ui.S(10); } }

        private RectangleF Card(int i)
        {
            int rows = (options.Count + Columns - 1) / Columns;
            float w = (Width - 1 - Gap * (Columns - 1)) / (float)Columns;
            float h = (Height - 1 - Gap * (rows - 1)) / (float)Math.Max(1, rows);
            return new RectangleF(0.5f + (i % Columns) * (w + Gap), 0.5f + (i / Columns) * (h + Gap), w, h);
        }

        private int Hit(Point p)
        {
            for (int i = 0; i < options.Count; i++) if (Card(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int index = Enabled ? Hit(e.Location) : -1;
            if (index != hover)
            {
                if (hover >= 0) hoverA[hover].To(0, 240);
                hover = index;
                if (hover >= 0) hoverA[hover].To(1, 170);
            }
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hover >= 0) hoverA[hover].To(0, 240);
            hover = -1;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled) { int index = Hit(e.Location); if (index >= 0) SelectedIndex = index; }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < options.Count; i++)
            {
                RectangleF r = Card(i);
                float h = hoverA[i].Value, s = Math.Max(0, selectA[i].Value);
                float sel = Math.Min(1, s);
                float radius = Ui.S(14);
                Ui.Fill(g, r, radius, Ui.Mix(Ui.Mix(Ui.Card, Ui.Bg2, h), Ui.Surface, sel));
                if (sel > 0.01f)
                {
                    // The outline draws in from slightly outside, overshooting with the spring.
                    RectangleF edge = r;
                    float grow = (1 - s) * Ui.S(5);
                    edge.Inflate(-Ui.S(1) + grow, -Ui.S(1) + grow);
                    Ui.Stroke(g, edge, radius - Ui.S(1) + grow, Ui.Alpha(Ui.Accent, (int)(255 * sel)), Math.Max(1.5f, Ui.S(2)));
                }
                Option o = options[i];
                int x = (int)r.X + Ui.S(16), y = (int)(r.Y + r.Height / 2) - Ui.S(20);
                Ui.Write(g, o.Title, Ui.Strong, new Rectangle(x, y, (int)r.Right - x - Ui.S(8), Ui.S(20)), Enabled ? Ui.Text : Ui.TextFaint,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                if (!string.IsNullOrEmpty(o.Detail))
                    Ui.Write(g, o.Detail, Ui.Small, new Rectangle(x, y + Ui.S(22), (int)r.Right - x - Ui.S(8), Ui.S(20)), Ui.TextDim,
                        TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // Label with a switch; the whole row toggles.
    internal sealed class ToggleSwitch : Control
    {
        private bool on;
        private readonly Anim knob, hoverA;
        public event EventHandler CheckedChanged;

        public ToggleSwitch(string text, bool initial)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Text = text;
            Font = Ui.Body;
            on = initial;
            knob = new Anim(this, initial ? 1 : 0, 420, Ease.Spring);
            hoverA = new Anim(this, 0, 150, Ease.OutCubic);
            Cursor = Cursors.Hand;
            Height = Ui.S(36);
        }

        public bool Checked
        {
            get { return on; }
            set
            {
                if (on == value) return;
                on = value;
                knob.To(value ? 1 : 0, 420);
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hoverA.To(1, 150); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hoverA.To(0, 220); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left && Enabled) Checked = !Checked; base.OnMouseDown(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF row = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Ui.Fill(g, row, row.Height / 2, Ui.Mix(Ui.Bg2, Ui.Bg3, hoverA.Value * 0.6f));
            Ui.Write(g, Text, Font, new Rectangle(Ui.S(16), 0, Width - Ui.S(80), Height), Enabled ? Ui.Text : Ui.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            Rectangle toggle = new Rectangle(Width - Ui.S(54), (Height - Ui.S(22)) / 2, Ui.S(40), Ui.S(22));
            Ui.Switch(g, toggle, knob.Value, Enabled);
        }
    }

    // Progress line: eases towards the reported value; a negative value shows a
    // travelling shimmer for work whose length is unknown.
    internal sealed class ProgressLine : Control
    {
        private readonly Anim valueA;
        private float target = -1;
        private Action stopShimmer;

        public ProgressLine()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            valueA = new Anim(this, 0, 300, Ease.OutCubic);
            Height = Ui.S(8);
        }

        public float Value
        {
            get { return target; }
            set
            {
                target = value;
                if (value >= 0) valueA.To(Math.Min(1, value), 300);
                Invalidate();
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && stopShimmer == null) stopShimmer = Motion.Pulse(delegate { if (!IsDisposed) Invalidate(); });
            else if (!Visible && stopShimmer != null) { stopShimmer(); stopShimmer = null; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF track = new RectangleF(0, (Height - Ui.S(6)) / 2f, Width - 1, Ui.S(6));
            Ui.Fill(g, track, track.Height / 2, Ui.Bg2);
            double phase = (Motion.Now % 1400) / 1400.0;
            if (target < 0)
            {
                float w = track.Width * 0.3f, x = (float)(track.X - w + phase * (track.Width + w));
                GraphicsState state = g.Save();
                using (GraphicsPath clip = Ui.Round(track, track.Height / 2)) g.SetClip(clip, CombineMode.Intersect);
                using (GraphicsPath bar = Ui.Round(new RectangleF(x, track.Y, w, track.Height), track.Height / 2))
                using (LinearGradientBrush brush = Ui.AccentBrush(new RectangleF(x, track.Y, w, track.Height), 0)) g.FillPath(brush, bar);
                g.Restore(state);
                return;
            }
            float fill = track.Width * Math.Max(0, Math.Min(1, valueA.Value));
            if (fill < 1) return;
            RectangleF done = new RectangleF(track.X, track.Y, Math.Max(track.Height, fill), track.Height);
            Ui.Fill(g, done, track.Height / 2, Ui.Accent);
        }

        protected override void Dispose(bool disposing)
        {
            if (stopShimmer != null) { stopShimmer(); stopShimmer = null; }
            base.Dispose(disposing);
        }
    }
}
