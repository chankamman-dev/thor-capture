// Themed dialog shell: borderless card with rounded corners and a drop shadow, a header
// with an accent icon tile, a body panel for content and a footer of action buttons.
// It fades and rises into place when shown. Esc cancels, Enter runs the primary action.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ThorCapture
{
    internal class ThemedDialog : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        public readonly Panel Body = new Panel();
        private readonly Strip head = new Strip(), foot = new Strip();
        private readonly List<FlatButton> actions = new List<FlatButton>();
        private readonly FlatButton close;
        private readonly string title, subtitle, glyph;
        private FlatButton accept;
        private DialogResult acceptResult = DialogResult.None;
        private Point grab;
        private bool dragging;
        private Action stopIntro;

        public ThemedDialog(string title, string subtitle, string glyph, int width, int bodyHeight)
        {
            this.title = title;
            this.subtitle = subtitle;
            this.glyph = glyph;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;
            Text = title;
            Font = Ui.Body;
            ForeColor = Ui.Text;
            // A 1px frame in the line colour shows through the padding.
            BackColor = Ui.Bg3;
            Padding = new Padding(1);
            DoubleBuffered = true;

            head.Dock = DockStyle.Top;
            head.Height = Ui.S(64);
            head.BackColor = Ui.Bg1;
            head.Painter = PaintHead;
            foot.Dock = DockStyle.Bottom;
            foot.Height = Ui.S(66);
            foot.BackColor = Ui.Bg1;
            Body.Dock = DockStyle.Fill;
            Body.BackColor = Ui.Bg1;
            Body.Font = Ui.Body;
            Controls.Add(Body);
            Controls.Add(head);
            Controls.Add(foot);
            ClientSize = new Size(Ui.S(width) + 2, head.Height + Ui.S(bodyHeight) + foot.Height + 2);

            close = new FlatButton(Glyphs.Cancel, null) { Kind = ButtonKind.Ghost };
            close.Click += delegate { Dismiss(); };
            head.Controls.Add(close);
            head.Layout += delegate
            {
                close.Size = new Size(Ui.S(32), Ui.S(32));
                close.Location = new Point(head.Width - close.Width - Ui.S(16), (head.Height - close.Height) / 2);
            };
            foot.Layout += delegate { LayoutActions(); };

            // The header doubles as a title bar.
            head.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { dragging = true; grab = e.Location; head.Capture = true; } };
            head.MouseMove += delegate(object sender, MouseEventArgs e) { if (dragging) Location = new Point(Left + e.X - grab.X, Top + e.Y - grab.Y); };
            head.MouseUp += delegate { dragging = false; head.Capture = false; };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { } // Windows 11 rounded corners
        }

        // Adds a footer button. Actions line up from the right in the order added, so add
        // secondary actions first and the primary one last.
        public FlatButton AddAction(string text, ButtonKind kind, DialogResult result)
        {
            FlatButton button = new FlatButton(null, text) { Kind = kind };
            if (result != DialogResult.None) button.Click += delegate { Finish(result); };
            if (kind == ButtonKind.Primary) { accept = button; acceptResult = result; }
            actions.Add(button);
            button.VisibleChanged += delegate { LayoutActions(); };
            foot.Controls.Add(button);
            LayoutActions();
            return button;
        }

        private void Finish(DialogResult result)
        {
            if (Modal) DialogResult = result;
            else Close();
        }

        private void Dismiss() { Finish(DialogResult.Cancel); }

        private void LayoutActions()
        {
            int x = foot.Width - Ui.S(20), h = Ui.S(38);
            for (int i = actions.Count - 1; i >= 0; i--)
            {
                FlatButton button = actions[i];
                if (!button.Visible) continue; // hidden actions take no room
                button.Height = h;
                button.Width = Math.Max(Ui.S(96), button.PreferredWidth + Ui.S(12));
                x -= button.Width;
                button.Location = new Point(x, (foot.Height - h) / 2);
                x -= Ui.S(10);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Dismiss(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && accept != null && accept.Enabled && acceptResult != DialogResult.None && !(ActiveControl is TextBoxBase && ((TextBoxBase)ActiveControl).Multiline))
            {
                // Let a focused field commit its text first.
                Body.Focus();
                Finish(acceptResult);
                e.Handled = e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (!Motion.Enabled) return;
            Opacity = 0;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!Motion.Enabled) return;
            // Fade in while rising a few pixels into place.
            int top = Top, rise = Ui.S(12);
            double start = Motion.Now;
            Top = top + rise;
            stopIntro = Motion.Pulse(delegate
            {
                if (IsDisposed) { StopIntro(); return; }
                float t = Math.Min(1, (float)((Motion.Now - start) / 220.0));
                float eased = Motion.Apply(Ease.OutCubic, t);
                Opacity = eased;
                if (!dragging) Top = top + (int)Math.Round(rise * (1 - eased));
                if (t >= 1) StopIntro();
            });
        }

        private void StopIntro()
        {
            if (stopIntro == null) return;
            stopIntro();
            stopIntro = null;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            StopIntro();
            base.OnFormClosed(e);
        }

        private void PaintHead(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Just the title: the content explains itself.
            int x = Ui.S(24);
            Ui.Write(g, title, Ui.Heading, new Rectangle(x, 0, head.Width - x - Ui.S(60), head.Height), Ui.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        // ---- body helpers (coordinates in design pixels, relative to the body) ----

        public Label AddText(string text, Font font, Color color, int x, int y, int width, int height)
        {
            Label label = new Label { Text = text, Font = font, ForeColor = color, BackColor = Ui.Bg1, AutoSize = false, UseMnemonic = false };
            label.SetBounds(Ui.S(x), Ui.S(y), Ui.S(width), Ui.S(height));
            Body.Controls.Add(label);
            return label;
        }

        // Small caption above a control; returns the control after placing it.
        public T AddField<T>(string caption, T control, int x, int y, int width) where T : Control
        {
            AddText(caption, Ui.Small, Ui.TextDim, x, y, width, 18);
            control.SetBounds(Ui.S(x), Ui.S(y + 22), Ui.S(width), control.Height);
            Body.Controls.Add(control);
            return control;
        }

        // Tinted note box with an icon.
        public Strip AddNote(string glyph, string text, int x, int y, int width, int height)
        {
            Strip note = new Strip { BackColor = Ui.Bg1 };
            note.SetBounds(Ui.S(x), Ui.S(y), Ui.S(width), Ui.S(height));
            note.Painter = delegate(object sender, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                RectangleF box = new RectangleF(0.5f, 0.5f, note.Width - 1.5f, note.Height - 1.5f);
                Ui.Fill(g, box, Ui.S(14), Ui.Card);
                using (SolidBrush dot = new SolidBrush(Ui.Warn)) g.FillEllipse(dot, Ui.S(16), note.Height / 2f - Ui.S(3), Ui.S(6), Ui.S(6));
                Ui.Write(g, text, Ui.Small, new Rectangle(Ui.S(32), 0, note.Width - Ui.S(46), note.Height), Ui.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            };
            Body.Controls.Add(note);
            return note;
        }
    }

    // Single-line text field matching the themed inputs.
    internal sealed class TextField : Control
    {
        public readonly TextBox Box = new TextBox();
        private readonly Anim focusA;

        public TextField()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            focusA = new Anim(this, 0, 200, Ease.OutCubic);
            Height = Ui.S(36);
            Cursor = Cursors.IBeam;
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Ui.Bg2;
            Box.ForeColor = Ui.Text;
            Box.Font = Ui.Body;
            Controls.Add(Box);
            Box.GotFocus += delegate { focusA.Target = 1; Box.BackColor = Ui.Surface; };
            Box.LostFocus += delegate { focusA.Target = 0; Box.BackColor = Ui.Bg2; };
        }

        public override string Text { get { return Box.Text; } set { Box.Text = value; } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            Box.Location = new Point(Ui.S(16), (Height - Box.Height) / 2 + 1);
            Box.Width = Math.Max(10, Width - Ui.S(32));
        }

        protected override void OnMouseDown(MouseEventArgs e) { Box.Focus(); base.OnMouseDown(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float f = focusA.Value;
            RectangleF field = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Ui.Fill(g, field, field.Height / 2, Ui.Mix(Ui.Bg2, Ui.Surface, f));
            if (f > 0) Ui.Stroke(g, field, field.Height / 2, Ui.Alpha(Ui.Bg3, (int)(255 * f)), 1f);
        }
    }
}
