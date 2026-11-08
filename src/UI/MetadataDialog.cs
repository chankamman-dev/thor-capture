// Editing a game's facts: fill in the form, review every change against the library value,
// tick the ones to keep, then stage them. Staging only changes the local draft.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThorCapture
{
    internal sealed class MetadataDialog : ThemedDialog
    {
        private enum Step { Loading, Edit, Checking, Review, Saving }
        private readonly GameEditSession session;
        private readonly GameEntry game;
        private Step step = Step.Loading;
        private string itemId, error, notice;
        private Dictionary<string, object> baseline = new Dictionary<string, object>();
        private readonly Dictionary<string, TextField> texts = new Dictionary<string, TextField>();
        private readonly MultiLineField summary = new MultiLineField();
        private readonly ToggleSwitch favorite = new ToggleSwitch("收藏", false), hidden = new ToggleSwitch("隐藏游戏", false);
        private readonly ChangeList changes = new ChangeList();
        private readonly FlatButton back, primary;
        private string previewToken;
        internal string Describe { get { return step + " error=" + error + " changes=" + changes.Count; } }

        public MetadataDialog(GameEditSession owner, GameEntry entry) : base("编辑资料 · " + entry.Name, null, null, 640, 470)
        {
            session = owner;
            game = entry;
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(Body, true, null);
            Body.Paint += PaintCanvas;
            foreach (string field in new[] { "displayName", "title", "developer", "publisher", "releaseDate", "rating", "genres", "players", "region" })
            {
                TextField box = new TextField();
                texts[field] = box;
                Body.Controls.Add(box);
            }
            foreach (Control control in new Control[] { summary, favorite, hidden, changes }) Body.Controls.Add(control);
            changes.Changed += delegate { Arrange(); };
            back = AddAction("返回修改", ButtonKind.Subtle, DialogResult.None);
            AddAction("取消", ButtonKind.Subtle, DialogResult.Cancel);
            primary = AddAction("检查改动", ButtonKind.Primary, DialogResult.None);
            back.Click += delegate { error = null; Go(Step.Edit); };
            primary.Click += delegate { Primary(); };
            Arrange();
            Shown += async delegate { await LoadGame(); };
        }

        private async Task LoadGame()
        {
            try
            {
                itemId = await session.ItemIdAsync(game.Id);
                object node = await Task.Run(() => session.Backend.GetJson("/api/metadata?id=" + game.Id.ToString(CultureInfo.InvariantCulture), session.Token, true), session.Token);
                if (node == null) throw new NativeEditorException("EDITOR_ITEM_NOT_FOUND", 404);
                foreach (string field in EditText.Fields) baseline[field] = Json.Get(node, field);
                // Values already staged for this game start in the form so edits build on them.
                Dictionary<string, object> form = new Dictionary<string, object>(baseline);
                foreach (StagedMetadata entry in session.Metadata)
                    if (entry.ItemId == itemId)
                        foreach (KeyValuePair<string, object> field in entry.Fields) form[field.Key] = field.Value;
                Fill(form);
                Go(Step.Edit);
            }
            catch (Exception failure)
            {
                if (IsDisposed) return;
                error = EditText.Error(EditText.Code(failure));
                Go(Step.Edit);
                primary.Enabled = false;
            }
        }

        private void Fill(Dictionary<string, object> form)
        {
            foreach (KeyValuePair<string, TextField> pair in texts)
            {
                object value;
                form.TryGetValue(pair.Key, out value);
                if (pair.Key == "releaseDate") { DateTime? day = EditText.Date(value); pair.Value.Text = day.HasValue ? day.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : ""; }
                else if (pair.Key == "rating") pair.Value.Text = value is double ? ((double)value).ToString("0.##", CultureInfo.InvariantCulture) : "";
                else pair.Value.Text = value as string ?? "";
            }
            object text;
            form.TryGetValue("summary", out text);
            summary.Text = text as string ?? "";
            object flag;
            favorite.Checked = form.TryGetValue("isFavorite", out flag) && flag is bool && (bool)flag;
            hidden.Checked = form.TryGetValue("isHidden", out flag) && flag is bool && (bool)flag;
        }

        // Form values that differ from the library; empty text clears the field.
        private Dictionary<string, object> Proposed(out string invalid)
        {
            invalid = null;
            Dictionary<string, object> result = new Dictionary<string, object>();
            foreach (KeyValuePair<string, TextField> pair in texts)
            {
                string text = pair.Value.Text.Trim();
                object value;
                if (pair.Key == "releaseDate")
                {
                    DateTime day;
                    if (text.Length == 0) value = null;
                    else if (DateTime.TryParseExact(text, new[] { "yyyy-MM-dd", "yyyy-M-d", "yyyy/M/d", "yyyy.M.d" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
                    {
                        // Keep the unit the library already uses (milliseconds unless it stores seconds).
                        object old; baseline.TryGetValue("releaseDate", out old);
                        double ms = (DateTime.SpecifyKind(day, DateTimeKind.Utc) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                        bool seconds = old is double && (double)old > 0 && (double)old <= 1e11;
                        value = (long)(seconds ? ms / 1000 : ms);
                        if (EditText.Date(old) == day.Date) continue; // same day: leave the stored value alone
                    }
                    else { invalid = "发售日期请写成 2024-03-15 这样的格式。"; return null; }
                }
                else if (pair.Key == "rating")
                {
                    double rating;
                    if (text.Length == 0) value = null;
                    else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out rating) && rating >= 0 && rating <= 100) value = rating;
                    else { invalid = "评分请填写数字。"; return null; }
                }
                else value = text.Length == 0 ? null : text;
                if (!Same(baseline, pair.Key, value)) result[pair.Key] = value;
            }
            string body = summary.Text.Trim();
            if (!Same(baseline, "summary", body.Length == 0 ? null : body)) result["summary"] = body.Length == 0 ? null : body;
            if (!Same(baseline, "isFavorite", favorite.Checked)) result["isFavorite"] = favorite.Checked;
            if (!Same(baseline, "isHidden", hidden.Checked)) result["isHidden"] = hidden.Checked;
            return result;
        }

        private static bool Same(Dictionary<string, object> values, string field, object value)
        {
            object old;
            values.TryGetValue(field, out old);
            if (field == "isFavorite" || field == "isHidden") return (old is bool && (bool)old) == (value is bool && (bool)value);
            if (old == null || value == null) return old == null && value == null;
            if (old is double || value is long || value is double) return Math.Abs(Convert.ToDouble(old, CultureInfo.InvariantCulture) - Convert.ToDouble(value, CultureInfo.InvariantCulture)) < 1e-9;
            return string.Equals(Convert.ToString(old, CultureInfo.InvariantCulture), Convert.ToString(value, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        private async void Primary()
        {
            if (step == Step.Edit) await Check();
            else if (step == Step.Review) await Save();
        }

        private async Task Check()
        {
            string invalid;
            Dictionary<string, object> fields = Proposed(out invalid);
            if (invalid != null) { error = invalid; Arrange(); return; }
            if (fields.Count == 0) { error = null; notice = "和游戏库里的资料相同，没有需要暂存的改动。"; Arrange(); return; }
            error = notice = null;
            Go(Step.Checking);
            try
            {
                NativeEditorClient editor = await session.Editor();
                NativeEditorResult result = await editor.MetadataPreviewAsync(itemId, fields, session.Token);
                if (IsDisposed) return;
                Dictionary<string, object> data = result.Data;
                previewToken = Json.Text(data, "previewToken");
                List<ChangeList.Row> rows = new List<ChangeList.Row>();
                Dictionary<string, object> list = Json.Get(data, "changes") as Dictionary<string, object> ?? new Dictionary<string, object>();
                StagedMetadata staged = null;
                foreach (StagedMetadata entry in session.Metadata) if (entry.ItemId == itemId) staged = entry;
                foreach (string field in EditText.Fields)
                {
                    object pair;
                    if (!list.TryGetValue(field, out pair)) continue;
                    object after = Json.Get(pair, "after");
                    bool already = staged != null && staged.Fields.ContainsKey(field) && Same(staged.Fields, field, after);
                    rows.Add(new ChangeList.Row { Field = field, Before = EditText.Value(field, Json.Get(pair, "before")), After = EditText.Value(field, after), Picked = true, Staged = already });
                }
                changes.SetRows(rows);
                if (rows.Count == 0) { notice = "和游戏库里的资料相同，没有需要暂存的改动。"; Go(Step.Edit); return; }
                Go(Step.Review);
            }
            catch (Exception failure)
            {
                if (IsDisposed) return;
                error = EditText.Error(EditText.Code(failure));
                Go(Step.Edit);
            }
        }

        private async Task Save()
        {
            List<string> picked = changes.Picked();
            if (picked.Count == 0) return;
            Go(Step.Saving);
            string code = null;
            try
            {
                NativeEditorClient editor = await session.Editor();
                NativeEditorResult result = await editor.MetadataConfirmAsync(previewToken, picked.ToArray(), session.Revision, session.Token);
                session.Apply(result);
                if (!IsDisposed) { DialogResult = DialogResult.OK; }
                return;
            }
            catch (Exception failure) { code = EditText.Code(failure); }
            if (IsDisposed) return;
            error = EditText.Error(code);
            // A conflict or expiry needs a fresh preview against the current draft. A cancelled or
            // timed-out confirm may still have committed, so the draft is re-read in every case.
            await session.Refresh();
            if (IsDisposed) return;
            Go(code == "EDITOR_REVISION_CONFLICT" || code == "EDITOR_BASE_CHANGED" || code == "EDITOR_PREVIEW_EXPIRED" ? Step.Edit : Step.Review);
        }

        private void Go(Step next) { step = next; Arrange(); }

        private void Arrange()
        {
            bool edit = step == Step.Edit;
            foreach (TextField box in texts.Values) box.Visible = edit;
            summary.Visible = favorite.Visible = hidden.Visible = edit;
            changes.Visible = step == Step.Review || step == Step.Saving;
            changes.Enabled = step == Step.Review;
            back.Visible = step == Step.Review;
            primary.Visible = edit || step == Step.Review;
            primary.Text = step == Step.Review ? "暂存所选（" + changes.Picked().Count + "）" : "检查改动";
            primary.Enabled = edit ? itemId != null : step == Step.Review && changes.Picked().Count > 0;
            if (primary.Parent != null) primary.Parent.PerformLayout(); // the footer sizes buttons to their text
            // Two columns of single-line fields, the summary across, then the switches.
            int pad = 24, col = 288, gap = 16;
            string[] order = { "displayName", "title", "developer", "publisher", "releaseDate", "rating", "genres", "players", "region" };
            for (int i = 0; i < order.Length; i++)
            {
                int x = pad + (i % 2) * (col + gap), y = 34 + (i / 2) * 64;
                texts[order[i]].SetBounds(Ui.S(x), Ui.S(y), Ui.S(col), Ui.S(36));
            }
            favorite.SetBounds(Ui.S(pad + col + gap), Ui.S(34 + 4 * 64), Ui.S(136), favorite.Height);
            hidden.SetBounds(Ui.S(pad + col + gap + 144), Ui.S(34 + 4 * 64), Ui.S(144), hidden.Height);
            summary.SetBounds(Ui.S(pad), Ui.S(352), Ui.S(col * 2 + gap), Ui.S(92));
            changes.SetBounds(Ui.S(pad), Ui.S(52), Ui.S(col * 2 + gap), Ui.S(370));
            Body.Invalidate();
        }

        private void PaintCanvas(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Ui.S(24), width = Ui.S(592);
            if (step == Step.Edit)
            {
                string[] order = { "displayName", "title", "developer", "publisher", "releaseDate", "rating", "genres", "players", "region" };
                for (int i = 0; i < order.Length; i++)
                {
                    int x = 24 + (i % 2) * 304, y = 34 + (i / 2) * 64;
                    string caption = EditText.Label(order[i]) + (order[i] == "releaseDate" ? "  年-月-日" : "");
                    Ui.Write(g, caption, Ui.Small, new Rectangle(Ui.S(x), Ui.S(y - 20), Ui.S(288), Ui.S(18)), Ui.TextDim, TextFormatFlags.Left);
                }
                Ui.Write(g, EditText.Label("summary"), Ui.Small, new Rectangle(pad, Ui.S(332), width, Ui.S(18)), Ui.TextDim, TextFormatFlags.Left);
                string line = error ?? notice ?? "改动先暂存在本机，确认前会逐项对比；不会写入掌机。";
                Note(g, pad, Ui.S(452), width, line, error != null ? Ui.Danger : notice != null ? Ui.Warn : Ui.TextFaint);
            }
            else if (step == Step.Loading || step == Step.Checking || step == Step.Saving && !changes.Visible)
                Ui.Write(g, step == Step.Loading ? "正在读取资料…" : "正在对比…", Ui.Body, new Rectangle(0, Ui.S(200), Body.Width, Ui.S(24)), Ui.TextDim, TextFormatFlags.HorizontalCenter);
            else
            {
                Ui.Write(g, "勾选要暂存的改动", Ui.Strong, new Rectangle(pad, Ui.S(16), width, Ui.S(24)), Ui.Text, TextFormatFlags.Left);
                Note(g, pad, Ui.S(436), width, error ?? (step == Step.Saving ? "正在暂存…" : "只暂存勾选的项目。要恢复已暂存的项目，请在「暂存的修改」中撤销。"), error != null ? Ui.Danger : Ui.TextFaint);
            }
        }

        private static void Note(Graphics g, int x, int y, int width, string text, Color tone)
        {
            using (SolidBrush dot = new SolidBrush(tone)) g.FillEllipse(dot, x, y + Ui.S(6), Ui.S(6), Ui.S(6));
            Ui.Write(g, text, Ui.Small, new Rectangle(x + Ui.S(14), y, width - Ui.S(14), Ui.S(36)), tone == Ui.TextFaint ? Ui.TextDim : Ui.Text, TextFormatFlags.Left | TextFormatFlags.WordBreak);
        }
    }

    // Multi-line text matching TextField's look.
    internal sealed class MultiLineField : Control
    {
        public readonly TextBox Box = new TextBox();

        public MultiLineField()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Box.Multiline = true;
            Box.ScrollBars = ScrollBars.Vertical;
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Ui.Bg2;
            Box.ForeColor = Ui.Text;
            Box.Font = Ui.Body;
            Box.MaxLength = 16384;
            Controls.Add(Box);
            Box.GotFocus += delegate { Box.BackColor = Ui.Surface; Invalidate(); };
            Box.LostFocus += delegate { Box.BackColor = Ui.Bg2; Invalidate(); };
        }

        public override string Text { get { return Box.Text; } set { Box.Text = (value ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"); } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            Box.SetBounds(Ui.S(14), Ui.S(10), Math.Max(10, Width - Ui.S(20)), Math.Max(10, Height - Ui.S(20)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF field = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Ui.Fill(g, field, Ui.S(14), Box.Focused ? Ui.Surface : Ui.Bg2);
            if (Box.Focused) Ui.Stroke(g, field, Ui.S(14), Ui.Bg3, 1f);
        }
    }

    // Review list: one row per changed field with a tick, the library value struck through and the new value.
    internal sealed class ChangeList : Control
    {
        public sealed class Row { public string Field, Before, After; public bool Picked, Staged; }
        private List<Row> rows = new List<Row>();
        private float scroll;
        private int hover = -1;
        public event EventHandler Changed;
        public int Count { get { return rows.Count; } }

        public ChangeList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
        }

        public void SetRows(List<Row> list) { rows = list; scroll = 0; Invalidate(); }

        public List<string> Picked()
        {
            List<string> result = new List<string>();
            foreach (Row row in rows) if (row.Picked) result.Add(row.Field);
            return result;
        }

        private int RowHeight { get { return Ui.S(58); } }

        private int At(Point p) { int i = (int)((p.Y + scroll) / RowHeight); return i >= 0 && i < rows.Count ? i : -1; }

        protected override void OnMouseMove(MouseEventArgs e) { int i = At(e.Location); if (i != hover) { hover = i; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = At(e.Location);
            if (Enabled && e.Button == MouseButtons.Left && i >= 0)
            {
                rows[i].Picked = !rows[i].Picked;
                Invalidate();
                if (Changed != null) Changed(this, EventArgs.Empty);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll = Math.Max(0, Math.Min(Math.Max(0, rows.Count * RowHeight - Height), scroll - e.Delta * Ui.S(60) / 120f));
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int h = RowHeight;
            for (int i = 0; i < rows.Count; i++)
            {
                int y = (int)(i * h - scroll);
                if (y + h < 0 || y > Height) continue;
                Row row = rows[i];
                RectangleF card = new RectangleF(0, y + Ui.S(3), Width - 1, h - Ui.S(6));
                Ui.Fill(g, card, Ui.S(12), i == hover && Enabled ? Ui.Bg2 : Ui.Card);
                // Tick box.
                RectangleF box = new RectangleF(Ui.S(14), y + h / 2f - Ui.S(9), Ui.S(18), Ui.S(18));
                if (row.Picked)
                {
                    Ui.Fill(g, box, Ui.S(5), Ui.Accent);
                    using (Pen tick = new Pen(Ui.Surface, Ui.S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(tick, new[] { new PointF(box.X + box.Width * 0.24f, box.Y + box.Height * 0.52f), new PointF(box.X + box.Width * 0.43f, box.Y + box.Height * 0.7f), new PointF(box.X + box.Width * 0.77f, box.Y + box.Height * 0.32f) });
                }
                else Ui.Stroke(g, box, Ui.S(5), Ui.TextFaint, 1.4f);
                int x = Ui.S(46), labelWidth = Ui.S(92);
                Ui.Write(g, EditText.Label(row.Field), Ui.Strong, new Rectangle(x, y, labelWidth, h), Ui.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (row.Staged)
                {
                    Rectangle tag = new Rectangle(x, y + h - Ui.S(20), Ui.Measure("已暂存", Ui.Small) + Ui.S(10), Ui.S(16));
                    Ui.Write(g, "已暂存", Ui.Small, tag, Ui.TextFaint, TextFormatFlags.Left);
                }
                int vx = x + labelWidth + Ui.S(8), vw = Width - vx - Ui.S(14);
                Ui.Write(g, Short(row.Before), Ui.Small, new Rectangle(vx, y + Ui.S(9), vw, Ui.S(18)), Ui.TextFaint, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                int strike = Math.Min(vw, Ui.Measure(Short(row.Before), Ui.Small));
                using (Pen line = new Pen(Ui.TextFaint, 1f)) g.DrawLine(line, vx, y + Ui.S(18), vx + strike, y + Ui.S(18));
                Ui.Write(g, Short(row.After), Ui.Body, new Rectangle(vx, y + Ui.S(28), vw, Ui.S(22)), Ui.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }

        private static string Short(string text) { text = (text ?? "").Replace("\r", " ").Replace("\n", " "); return text.Length > 160 ? text.Substring(0, 160) + "…" : text; }
    }
}
