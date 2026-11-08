// Scraping one game: search the chosen sources, pick the entry that is this game, review what it
// offers field by field against the library, and stage the ticked fields in the local draft.
// Picking a result never changes anything by itself.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThorCapture
{
    internal static class ScrapeText
    {
        public static string Error(string code)
        {
            switch (code ?? "")
            {
                case "SCRAPE_BUSY": return "已有刮削任务在进行，请等它结束。";
                case "SCRAPE_LIBRARY_CHANGED": return "游戏库在刮削期间被替换了，请重新开始。";
                case "SCRAPE_STOPPING": case "BACKEND_STOPPING": return "游戏库服务正在关闭。";
                case "SCRAPE_TIMEOUT": return "这批任务超过了 15 分钟的时限，已完成的结果保留。";
                case "SCRAPE_INPUT_INVALID": return "选择的游戏或来源无效。";
                case "BACKEND_TIMEOUT": return "来源响应太慢，请稍后重试。";
                case "BACKEND_REQUEST_FAILED": return "来源没有返回可用的结果，请换一个来源或稍后重试。";
                default:
                    if ((code ?? "").StartsWith("EDITOR_", StringComparison.Ordinal)) return EditText.Error(code);
                    return "刮削没有完成（" + (string.IsNullOrEmpty(code) ? "未知原因" : code) + "）。";
            }
        }
    }

    internal sealed class ScrapeDialog : ThemedDialog
    {
        private enum Step { Loading, Results, Working, Review, Saving }
        private readonly GameEditSession session;
        private readonly ScrapeClient client;
        private readonly GameEntry game;
        private readonly TextField term = new TextField();
        private readonly FlatButton searchButton, back, primary;
        private readonly List<FlatButton> chips = new List<FlatButton>();
        private readonly CandidateList list = new CandidateList();
        private readonly ChangeList changes = new ChangeList();
        private readonly ProgressLine progress = new ProgressLine { Value = -1 };
        private Step step = Step.Loading;
        private string note, error, working, itemId, previewToken;
        internal string Describe { get { return step + " error=" + error + " note=" + note + " candidates=" + list.Count + " changes=" + changes.Count; } }

        public ScrapeDialog(GameEditSession owner, GameEntry entry) : base("刮削 · " + entry.Name, null, null, 680, 440)
        {
            session = owner; game = entry; client = new ScrapeClient(owner.Backend);
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(Body, true, null);
            Body.Paint += PaintCanvas;
            term.Text = entry.Name;
            searchButton = new FlatButton(Glyphs.Search, "搜索") { Kind = ButtonKind.Subtle };
            searchButton.Click += delegate { Search(); };
            term.Box.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Search(); } };
            foreach (Control control in new Control[] { term, searchButton, list, changes, progress }) Body.Controls.Add(control);
            list.SelectionChanged += delegate { Arrange(); };
            list.Activated += delegate { Primary(); };
            changes.Changed += delegate { Arrange(); };
            back = AddAction("返回结果", ButtonKind.Subtle, DialogResult.None);
            AddAction("取消", ButtonKind.Subtle, DialogResult.Cancel);
            primary = AddAction("使用这个结果", ButtonKind.Primary, DialogResult.None);
            back.Click += delegate { error = null; Go(Step.Results); };
            primary.Click += delegate { Primary(); };
            Arrange();
            Shown += async delegate { await LoadSources(); };
        }

        private List<string> Sources()
        {
            List<string> ids = new List<string>();
            foreach (FlatButton chip in chips) if (chip.Checked) ids.Add((string)chip.Tag);
            return ids;
        }

        private async Task LoadSources()
        {
            string code = null;
            List<ScrapeCandidate> known = null;
            try
            {
                List<ScrapeProvider> providers = await client.ProvidersAsync(session.Token);
                if (IsDisposed) return;
                foreach (ScrapeProvider provider in providers)
                {
                    if (!provider.Configured) continue;
                    FlatButton chip = new FlatButton(null, provider.Label) { Kind = ButtonKind.Subtle, Tag = provider.Id };
                    // Steam only knows PC releases; it starts ticked for PC games only.
                    chip.Checked = provider.Id != "steam" || game.Platform == "windows" || game.Platform == "steam";
                    chip.Click += delegate { chip.Checked = !chip.Checked; };
                    chips.Add(chip); Body.Controls.Add(chip);
                }
                known = await client.CandidatesAsync(game.Id, session.Token);
            }
            catch (Exception failure) { code = EditText.Code(failure); }
            if (IsDisposed) return;
            if (code != null) { error = ScrapeText.Error(code); Go(Step.Results); return; }
            if (chips.Count == 0) { note = "还没有可用的来源。请先在「刮削 → 来源设置」里填写密钥。"; Go(Step.Results); return; }
            // Results of an earlier search or batch are shown as they are; otherwise search right away.
            if (known.Count > 0) { list.SetRows(known); Go(Step.Results); }
            else await RunSearch(null);
        }

        private async void Search()
        {
            if (step != Step.Results) return;
            string text = term.Text.Trim();
            await RunSearch(text.Length == 0 || text == game.Name ? null : text);
        }

        private async Task RunSearch(string custom)
        {
            List<string> sources = Sources();
            if (sources.Count == 0) { note = "请至少选一个来源。"; Arrange(); return; }
            error = note = null; working = "正在搜索 " + sources.Count + " 个来源…";
            Go(Step.Working);
            string code = null;
            ScrapeSearch found = null;
            List<ScrapeCandidate> all = null;
            try
            {
                found = await client.SearchAsync(game.Id, sources, custom, session.Token);
                all = await client.CandidatesAsync(game.Id, session.Token);
            }
            catch (Exception failure) { code = EditText.Code(failure); }
            if (IsDisposed) return;
            if (code != null) error = ScrapeText.Error(code);
            else
            {
                list.SetRows(all);
                if (found.Errors.Count > 0) note = string.Join("；", found.Errors.ToArray());
                else if (all.Count == 0) note = "没有找到结果。可以改一下上面的名称（比如换成英文或日文原名）再搜索。";
            }
            Go(Step.Results);
        }

        private async void Primary()
        {
            if (step == Step.Results) await Use();
            else if (step == Step.Review) await Save();
        }

        // Fetches the chosen entry's details and compares what it offers with the library.
        private async Task Use()
        {
            ScrapeCandidate chosen = list.Selected;
            if (chosen == null) return;
            error = note = null; working = "正在读取这条结果的详细资料…";
            Go(Step.Working);
            string code = null;
            List<ChangeList.Row> rows = new List<ChangeList.Row>();
            bool empty = false;
            try
            {
                Dictionary<string, object> fields = await client.SelectAsync(game.Id, chosen.Id, session.Token);
                if (fields.Count == 0) empty = true;
                else
                {
                    itemId = await session.ItemIdAsync(game.Id);
                    NativeEditorClient editor = await session.Editor();
                    NativeEditorResult preview = await editor.MetadataPreviewAsync(itemId, fields, session.Token);
                    Dictionary<string, object> data = preview.Data;
                    previewToken = Json.Text(data, "previewToken");
                    Dictionary<string, object> diff = Json.Get(data, "changes") as Dictionary<string, object> ?? new Dictionary<string, object>();
                    foreach (string field in EditText.Fields)
                    {
                        object pair;
                        if (!diff.TryGetValue(field, out pair)) continue;
                        // A value the library already has is ticked only when the library's own is empty: scraped text should not silently replace what is there.
                        object before = Json.Get(pair, "before");
                        bool blank = before == null || (before is string && ((string)before).Trim().Length == 0) || (before is double && (double)before == 0);
                        rows.Add(new ChangeList.Row { Field = field, Before = EditText.Value(field, before), After = EditText.Value(field, Json.Get(pair, "after")), Picked = blank });
                    }
                }
            }
            catch (Exception failure) { code = EditText.Code(failure); }
            if (IsDisposed) return;
            if (code != null) { error = ScrapeText.Error(code); Go(Step.Results); return; }
            if (empty) { note = "这条结果只提供图片或没有资料可用，换一条试试。"; Go(Step.Results); return; }
            if (rows.Count == 0) { note = "这条结果的资料和游戏库里的完全相同。"; Go(Step.Results); return; }
            changes.SetRows(rows);
            Go(Step.Review);
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
                session.Apply(await editor.MetadataConfirmAsync(previewToken, picked.ToArray(), session.Revision, session.Token));
                if (!IsDisposed) DialogResult = DialogResult.OK;
                return;
            }
            catch (Exception failure) { code = EditText.Code(failure); }
            if (IsDisposed) return;
            error = ScrapeText.Error(code);
            await session.Refresh(); // a failed confirm may still have committed
            if (IsDisposed) return;
            Go(code == "EDITOR_REVISION_CONFLICT" || code == "EDITOR_BASE_CHANGED" || code == "EDITOR_PREVIEW_EXPIRED" ? Step.Results : Step.Review);
        }

        private void Go(Step next) { step = next; Arrange(); }

        private void Arrange()
        {
            bool results = step == Step.Results, review = step == Step.Review || step == Step.Saving;
            term.Visible = searchButton.Visible = results;
            foreach (FlatButton chip in chips) chip.Visible = results;
            list.Visible = results;
            changes.Visible = review; changes.Enabled = step == Step.Review;
            progress.Visible = step == Step.Loading || step == Step.Working;
            back.Visible = step == Step.Review;
            primary.Visible = results || step == Step.Review;
            primary.Text = step == Step.Review ? "暂存所选（" + changes.Picked().Count + "）" : "使用这个结果";
            primary.Enabled = results ? list.Selected != null : changes.Picked().Count > 0;
            if (primary.Parent != null) primary.Parent.PerformLayout();
            term.SetBounds(Ui.S(24), Ui.S(14), Ui.S(520), Ui.S(36));
            searchButton.SetBounds(Ui.S(554), Ui.S(14), Ui.S(102), Ui.S(36));
            int x = Ui.S(24);
            foreach (FlatButton chip in chips) { chip.Height = Ui.S(30); chip.Fit(); chip.Location = new Point(x, Ui.S(60)); x += chip.Width + Ui.S(6); }
            list.SetBounds(Ui.S(24), Ui.S(100), Ui.S(632), Ui.S(290));
            changes.SetBounds(Ui.S(24), Ui.S(48), Ui.S(632), Ui.S(342));
            progress.SetBounds(Ui.S(240), Ui.S(232), Ui.S(200), Ui.S(8));
            Body.Invalidate();
        }

        private void PaintCanvas(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Ui.S(24), width = Ui.S(632);
            if (step == Step.Loading || step == Step.Working)
                Ui.Write(g, step == Step.Loading ? "正在准备…" : working, Ui.Body, new Rectangle(0, Ui.S(190), Body.Width, Ui.S(24)), Ui.TextDim, TextFormatFlags.HorizontalCenter);
            else if (step == Step.Results)
            {
                if (list.Count == 0 && error == null && note == null)
                    Ui.Write(g, "还没有结果", Ui.Body, new Rectangle(0, Ui.S(220), Body.Width, Ui.S(24)), Ui.TextDim, TextFormatFlags.HorizontalCenter);
                Note(g, pad, Ui.S(398), width, error ?? note ?? "选出就是这款游戏的那一条。之后会逐项对比资料，由你勾选要暂存的字段。", error != null ? Ui.Danger : note != null ? Ui.Warn : Ui.TextFaint);
            }
            else
            {
                Ui.Write(g, "勾选要暂存的资料", Ui.Strong, new Rectangle(pad, Ui.S(14), width, Ui.S(24)), Ui.Text, TextFormatFlags.Left);
                Note(g, pad, Ui.S(398), width, error ?? (step == Step.Saving ? "正在暂存…" : "游戏库里原来是空的字段已默认勾选；已有内容的需要你自己勾才会替换。"), error != null ? Ui.Danger : Ui.TextFaint);
            }
        }

        private static void Note(Graphics g, int x, int y, int width, string text, Color tone)
        {
            using (SolidBrush dot = new SolidBrush(tone)) g.FillEllipse(dot, x, y + Ui.S(6), Ui.S(6), Ui.S(6));
            Ui.Write(g, text, Ui.Small, new Rectangle(x + Ui.S(14), y, width - Ui.S(14), Ui.S(38)), tone == Ui.TextFaint ? Ui.TextDim : Ui.Text, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }
    }

    // Search results: title, where it came from and how sure the match is. One row can be selected.
    internal sealed class CandidateList : Control
    {
        private List<ScrapeCandidate> rows = new List<ScrapeCandidate>();
        private float scroll;
        private int selected = -1, hover = -1;
        public event EventHandler SelectionChanged, Activated;
        public int Count { get { return rows.Count; } }
        public ScrapeCandidate Selected { get { return selected >= 0 && selected < rows.Count ? rows[selected] : null; } }

        public CandidateList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
        }

        public void SetRows(List<ScrapeCandidate> list)
        {
            rows = list ?? new List<ScrapeCandidate>(); scroll = 0;
            // The entry chosen earlier stays selected; otherwise a confident first match is offered.
            selected = rows.FindIndex(row => row.Chosen);
            if (selected < 0 && rows.Count > 0 && rows[0].Decision != "reject") selected = 0;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        internal void Select(int index) { if (index < 0 || index >= rows.Count) return; selected = index; Invalidate(); if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty); }
        private int RowHeight { get { return Ui.S(66); } }
        private int At(Point p) { int i = (int)((p.Y + scroll) / RowHeight); return i >= 0 && i < rows.Count ? i : -1; }
        protected override void OnMouseEnter(EventArgs e) { Focus(); base.OnMouseEnter(e); }
        protected override void OnMouseMove(MouseEventArgs e) { int i = At(e.Location); if (i != hover) { hover = i; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Select(At(e.Location)); base.OnMouseDown(e); }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { if (At(e.Location) >= 0 && Activated != null) Activated(this, EventArgs.Empty); base.OnMouseDoubleClick(e); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll = Math.Max(0, Math.Min(Math.Max(0, rows.Count * RowHeight - Height), scroll - e.Delta * Ui.S(66) / 120f));
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
                ScrapeCandidate row = rows[i];
                RectangleF card = new RectangleF(1, y + Ui.S(3), Width - 3, h - Ui.S(6));
                Ui.Fill(g, card, Ui.S(12), i == selected ? Ui.Surface : i == hover ? Ui.Bg2 : Ui.Card);
                if (i == selected) Ui.Stroke(g, card, Ui.S(12), Ui.Text, 1.6f);
                string percent = Math.Round(row.Confidence * 100) + "%";
                string tag = row.Decision == "reject" ? "不太像" : row.Confidence >= 0.92 ? "很可能是" : "需要核对";
                int right = Ui.S(96), x = Ui.S(16), w = Width - x - right - Ui.S(12);
                Ui.Write(g, row.Title, Ui.Strong, new Rectangle(x, y + Ui.S(11), w, Ui.S(22)), Ui.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                string detail = ScrapeClient.ProviderName(row.Provider) + (row.Platform.Length > 0 ? " · " + GameEntry.PlatformName(row.Platform) : "") + (row.Year != null ? " · " + row.Year : "");
                if (!string.IsNullOrEmpty(row.Summary)) detail += " · " + row.Summary.Replace("\r", " ").Replace("\n", " ");
                Ui.Write(g, detail, Ui.Small, new Rectangle(x, y + Ui.S(34), w, Ui.S(18)), Ui.TextDim, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                Ui.Write(g, percent, Ui.Strong, new Rectangle(Width - right, y + Ui.S(11), right - Ui.S(16), Ui.S(22)), row.Decision == "reject" ? Ui.TextFaint : Ui.Text, TextFormatFlags.Right);
                Ui.Write(g, tag, Ui.Small, new Rectangle(Width - right, y + Ui.S(34), right - Ui.S(16), Ui.S(18)), row.Decision == "reject" ? Ui.TextFaint : Ui.TextDim, TextFormatFlags.Right);
            }
        }
    }
}
