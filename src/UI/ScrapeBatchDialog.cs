// Batch scraping and source settings. A batch only collects candidates for many games in the
// background; nothing is chosen or staged until each game is opened and reviewed.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThorCapture
{
    internal sealed class BatchScrapeDialog : ThemedDialog
    {
        private enum Step { Setup, Running, Done }
        private sealed class Scope { public string Label; public List<GameEntry> Games; }
        private readonly GameEditSession session;
        private readonly ScrapeClient client;
        private readonly List<Scope> scopes = new List<Scope>();
        private readonly ChoiceBox scopeBox = new ChoiceBox();
        private readonly List<FlatButton> chips = new List<FlatButton>();
        private readonly ProgressLine progress = new ProgressLine { Value = 0 };
        private readonly BatchResultList list = new BatchResultList();
        private readonly FlatButton primary, secondary;
        private readonly Dictionary<int, GameEntry> byId = new Dictionary<int, GameEntry>();
        private Step step = Step.Setup;
        private string error, note;
        private int total, completed, identified, failed;
        private volatile bool stopRequested;
        private NativeScrapeJob running;
        internal string Describe { get { return step + " error=" + error + " " + completed + "/" + total + " found=" + identified + " failed=" + failed + " rows=" + list.Count; } }

        // missing: games without developer and summary. shown: the page's current list. all: every game.
        public BatchScrapeDialog(GameEditSession owner, List<GameEntry> missing, List<GameEntry> shown, List<GameEntry> all) : base("批量刮削", null, null, 640, 440)
        {
            session = owner; client = new ScrapeClient(owner.Backend);
            foreach (GameEntry game in all) byId[game.Id] = game;
            scopes.Add(new Scope { Label = "缺少资料的游戏（" + missing.Count + "）", Games = missing });
            scopes.Add(new Scope { Label = "当前列表里的游戏（" + shown.Count + "）", Games = shown });
            scopes.Add(new Scope { Label = "全部游戏（" + all.Count + "）", Games = all });
            foreach (Scope scope in scopes) scopeBox.Items.Add(scope.Label);
            scopeBox.SelectedIndex = missing.Count > 0 ? 0 : 1;
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(Body, true, null);
            Body.Paint += PaintCanvas;
            foreach (Control control in new Control[] { scopeBox, progress, list }) Body.Controls.Add(control);
            list.Activated += delegate(object sender, int gameId) { Review(gameId); };
            secondary = AddAction("取消", ButtonKind.Subtle, DialogResult.None);
            primary = AddAction("开始", ButtonKind.Primary, DialogResult.None);
            secondary.Click += delegate { if (step == Step.Running) { stopRequested = true; secondary.Enabled = false; } else Close(); };
            primary.Click += delegate { if (step == Step.Setup) Run(); else if (step == Step.Done) Close(); };
            Arrange();
            Shown += async delegate { await LoadSources(); };
        }

        // Leaving while a batch runs asks the backend to stop it; what was found so far is kept there.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (step == Step.Running) { stopRequested = true; e.Cancel = true; }
            base.OnFormClosing(e);
        }

        private async Task LoadSources()
        {
            string code = null;
            try
            {
                foreach (ScrapeProvider provider in await client.ProvidersAsync(session.Token))
                {
                    if (!provider.Configured || IsDisposed) continue;
                    FlatButton chip = new FlatButton(null, provider.Label) { Kind = ButtonKind.Subtle, Tag = provider.Id };
                    // Sources that describe games start ticked; Steam (PC only) and artwork-only sources are opt-in for a batch.
                    chip.Checked = provider.Metadata && provider.Id != "steam";
                    chip.Click += delegate { chip.Checked = !chip.Checked; };
                    chips.Add(chip); Body.Controls.Add(chip);
                }
            }
            catch (Exception failure) { code = EditText.Code(failure); }
            if (IsDisposed) return;
            if (code != null) error = ScrapeText.Error(code);
            else if (chips.Count == 0) note = "还没有可用的来源。请先在「刮削 → 来源设置」里填写密钥。";
            Arrange();
        }

        private async void Run()
        {
            List<string> sources = new List<string>();
            foreach (FlatButton chip in chips) if (chip.Checked) sources.Add((string)chip.Tag);
            List<GameEntry> games = scopes[Math.Max(0, scopeBox.SelectedIndex)].Games;
            if (sources.Count == 0) { note = "请至少选一个来源。"; Arrange(); return; }
            if (games.Count == 0) { note = "这个范围里没有游戏。"; Arrange(); return; }
            error = note = null; total = games.Count; completed = identified = failed = 0; stopRequested = false;
            step = Step.Running; Arrange();
            List<BatchResultList.Row> rows = new List<BatchResultList.Row>();
            string code = null;
            try
            {
                NativeEditorClient editor = await session.Editor();
                // The backend takes at most 200 games per job; larger selections run as consecutive jobs.
                for (int offset = 0; offset < games.Count && !stopRequested; offset += 200)
                {
                    List<long> ids = new List<long>();
                    for (int i = offset; i < Math.Min(games.Count, offset + 200); i++) ids.Add(games[i].Id);
                    NativeScrapeJob job = running = await editor.StartScrapeAsync(ids, sources, "balanced", "zh", false, session.Token);
                    int baseDone = completed, baseFound = identified, baseFailed = failed;
                    while (!job.Finished)
                    {
                        await Task.Delay(800);
                        if (IsDisposed) return;
                        if (stopRequested && job.State != "cancelling") job = await editor.CancelScrapeAsync(job, session.Token);
                        else job = await editor.ScrapeStatusAsync(job, session.Token);
                        completed = baseDone + job.Completed; identified = baseFound + job.Identified; failed = baseFailed + job.Failed;
                        progress.Value = total == 0 ? 0 : completed / (float)total;
                        Body.Invalidate();
                    }
                    running = null;
                    List<object> details = Json.Get(job.Data, "games") as List<object>;
                    if (details != null)
                        foreach (object detail in details)
                        {
                            int id = (int)Json.Number(detail, "id", 0);
                            GameEntry game;
                            if (!byId.TryGetValue(id, out game)) continue;
                            List<object> errors = Json.Get(detail, "errors") as List<object>;
                            rows.Add(new BatchResultList.Row { GameId = id, Name = game.Name, Platform = GameEntry.PlatformName(game.Platform), Candidates = (int)Json.Number(detail, "candidateCount", 0),
                                Confident = Json.Number(detail, "highConfidence", 0) > 0, Error = errors != null && errors.Count > 0 ? Json.Text(errors[0], "message") : null });
                        }
                    if (job.State == "failed") { code = job.Code ?? "SCRAPE_FAILED"; break; }
                }
            }
            catch (Exception failure) { code = EditText.Code(failure); }
            if (IsDisposed) return;
            running = null;
            if (code != null) error = ScrapeText.Error(code);
            // Games with something to review first, the most promising on top.
            rows.Sort(delegate(BatchResultList.Row a, BatchResultList.Row b)
            {
                int rank = (b.Candidates > 0 ? 1 : 0).CompareTo(a.Candidates > 0 ? 1 : 0);
                if (rank != 0) return rank;
                rank = b.Confident.CompareTo(a.Confident);
                return rank != 0 ? rank : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            list.SetRows(rows);
            step = Step.Done; Arrange();
        }

        private void Review(int gameId)
        {
            GameEntry game;
            if (step != Step.Done || !byId.TryGetValue(gameId, out game)) return;
            using (ScrapeDialog dialog = new ScrapeDialog(session, game))
                if (dialog.ShowDialog(this) == DialogResult.OK) list.MarkStaged(gameId);
        }

        private void Arrange()
        {
            bool setup = step == Step.Setup;
            scopeBox.Visible = setup;
            foreach (FlatButton chip in chips) chip.Visible = setup;
            progress.Visible = step == Step.Running;
            list.Visible = step == Step.Done;
            primary.Visible = step != Step.Running;
            primary.Text = setup ? "开始" : "完成";
            primary.Enabled = !setup || chips.Count > 0;
            secondary.Visible = step != Step.Done;
            secondary.Text = step == Step.Running ? "停止" : "取消";
            if (step != Step.Running) secondary.Enabled = true;
            if (primary.Parent != null) primary.Parent.PerformLayout();
            scopeBox.SetBounds(Ui.S(24), Ui.S(40), Ui.S(320), Ui.S(36));
            int x = Ui.S(24);
            foreach (FlatButton chip in chips) { chip.Height = Ui.S(30); chip.Fit(); chip.Location = new Point(x, Ui.S(124)); x += chip.Width + Ui.S(6); }
            progress.SetBounds(Ui.S(24), Ui.S(196), Ui.S(592), Ui.S(8));
            list.SetBounds(Ui.S(24), Ui.S(48), Ui.S(592), Ui.S(336));
            Body.Invalidate();
        }

        private void PaintCanvas(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Ui.S(24), width = Ui.S(592);
            Action<string, int, Font, Color, int> line = delegate(string text, int y, Font font, Color color, int height)
            { Ui.Write(g, text, font, new Rectangle(pad, Ui.S(y), width, Ui.S(height)), color, TextFormatFlags.Left | TextFormatFlags.WordBreak); };
            if (step == Step.Setup)
            {
                line("范围", 16, Ui.Small, Ui.TextDim, 20);
                line("来源", 100, Ui.Small, Ui.TextDim, 20);
                line("批量刮削只为每款游戏找出候选结果，不会自动采用。完成后逐款打开核对，勾选的资料才会暂存，暂存的再由你应用到掌机。", 184, Ui.Body, Ui.TextDim, 70);
                line("每款游戏大约需要一两秒；数量多时可以随时停止，已找到的结果会保留。", 262, Ui.Small, Ui.TextDim, 24);
                if (error != null || note != null) Note(g, pad, Ui.S(310), width, error ?? note, error != null ? Ui.Danger : Ui.Warn);
            }
            else if (step == Step.Running)
            {
                line(stopRequested ? "正在停止…" : "正在查找候选结果…", 130, Ui.Heading, Ui.Text, 34);
                line("已处理 " + completed + " / " + total + " · 找到候选 " + identified + " 款" + (failed > 0 ? " · 出错 " + failed + " 款" : ""), 220, Ui.Body, Ui.TextDim, 26);
            }
            else
            {
                line("处理了 " + completed + " 款，" + identified + " 款有候选结果" + (failed > 0 ? "，" + failed + " 款出错" : "") + "。点一款游戏核对并暂存。", 14, Ui.Strong, Ui.Text, 24);
                if (error != null) Note(g, pad, Ui.S(392), width, error, Ui.Danger);
            }
        }

        private static void Note(Graphics g, int x, int y, int width, string text, Color tone)
        {
            using (SolidBrush dot = new SolidBrush(tone)) g.FillEllipse(dot, x, y + Ui.S(6), Ui.S(6), Ui.S(6));
            Ui.Write(g, text, Ui.Small, new Rectangle(x + Ui.S(14), y, width - Ui.S(14), Ui.S(40)), Ui.Text, TextFormatFlags.Left | TextFormatFlags.WordBreak);
        }
    }

    // One row per game of a finished batch; rows with candidates open the review.
    internal sealed class BatchResultList : Control
    {
        public sealed class Row { public int GameId, Candidates; public string Name, Platform, Error; public bool Confident, Staged; }
        private List<Row> rows = new List<Row>();
        private float scroll;
        private int hover = -1;
        public event Action<object, int> Activated;
        public int Count { get { return rows.Count; } }
        internal Row this[int index] { get { return rows[index]; } }

        public BatchResultList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }

        public void SetRows(List<Row> list) { rows = list; scroll = 0; Invalidate(); }
        public void MarkStaged(int gameId) { foreach (Row row in rows) if (row.GameId == gameId) row.Staged = true; Invalidate(); }
        private int RowHeight { get { return Ui.S(48); } }
        private int At(Point p) { int i = (int)((p.Y + scroll) / RowHeight); return i >= 0 && i < rows.Count ? i : -1; }
        internal void Open(int index) { if (index >= 0 && index < rows.Count && rows[index].Candidates > 0 && Activated != null) Activated(this, rows[index].GameId); }
        protected override void OnMouseEnter(EventArgs e) { Focus(); base.OnMouseEnter(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = At(e.Location);
            if (i != hover) { hover = i; Cursor = i >= 0 && rows[i].Candidates > 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Open(At(e.Location)); base.OnMouseDown(e); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll = Math.Max(0, Math.Min(Math.Max(0, rows.Count * RowHeight - Height), scroll - e.Delta * Ui.S(96) / 120f));
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
                bool open = row.Candidates > 0;
                Ui.Fill(g, new RectangleF(0, y + Ui.S(2), Width - 1, h - Ui.S(4)), Ui.S(10), open && i == hover ? Ui.Bg2 : Ui.Card);
                string status = row.Staged ? "已暂存" : open ? row.Candidates + " 个候选" + (row.Confident ? " · 很可能匹配" : "") : row.Error ?? "没有结果";
                int right = Ui.S(230), x = Ui.S(16);
                Ui.Write(g, row.Name, Ui.Body, new Rectangle(x, y, Width - x - right - Ui.S(70), h), open ? Ui.Text : Ui.TextFaint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                Ui.Write(g, row.Platform, Ui.Small, new Rectangle(Width - right - Ui.S(64), y, Ui.S(60), h), Ui.TextFaint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                Ui.Write(g, status, Ui.Small, new Rectangle(Width - right, y, right - Ui.S(16), h), row.Staged ? Ui.Ok : open ? Ui.TextDim : Ui.TextFaint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }
    }

    // Which sources can be used, a connection test for each, and where their credentials come from.
    internal sealed class ScrapeSettingsDialog : ThemedDialog
    {
        private static readonly string[][] Fields = {
            new[] { "sgdbKey", "SteamGridDB · API Key" }, new[] { "thegamesdbKey", "TheGamesDB · API Key" }, new[] { "retroachievementsKey", "RetroAchievements · Web API Key" },
            new[] { "screenscraperUser", "ScreenScraper · 账号" }, new[] { "screenscraperPassword", "ScreenScraper · 密码" },
            new[] { "screenscraperDevId", "ScreenScraper · 开发者 ID" }, new[] { "screenscraperDevPassword", "ScreenScraper · 开发者密码" },
            new[] { "igdbClientId", "IGDB · Client ID" }, new[] { "igdbToken", "IGDB · Access Token" } };
        private readonly ScrapeClient client;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly SourceList list = new SourceList();
        private readonly ChoiceBox field = new ChoiceBox();
        private readonly TextField value = new TextField();
        private readonly FlatButton save, import;
        private string note, error;
        private bool busy;
        internal string Describe { get { return "rows=" + list.Count + " note=" + note + " error=" + error; } }

        public ScrapeSettingsDialog(CocoonBackend backend) : base("刮削来源", null, null, 600, 430)
        {
            client = new ScrapeClient(backend);
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(Body, true, null);
            Body.Paint += PaintCanvas;
            foreach (string[] item in Fields) field.Items.Add(item[1]);
            field.SelectedIndex = 0;
            value.Box.UseSystemPasswordChar = true;
            save = new FlatButton(null, "保存") { Kind = ButtonKind.Subtle };
            save.Click += delegate { Save(); };
            import = new FlatButton(Glyphs.FolderOpen, "从 Cocoon Art 导入密钥…") { Kind = ButtonKind.Ghost };
            import.Click += delegate { Import(); };
            foreach (Control control in new Control[] { list, field, value, save, import }) Body.Controls.Add(control);
            list.SetBounds(Ui.S(24), Ui.S(12), Ui.S(552), Ui.S(264));
            field.SetBounds(Ui.S(24), Ui.S(304), Ui.S(230), Ui.S(36));
            value.SetBounds(Ui.S(262), Ui.S(304), Ui.S(226), Ui.S(36));
            save.SetBounds(Ui.S(496), Ui.S(304), Ui.S(80), Ui.S(36));
            import.Height = Ui.S(32); import.Fit(); import.Location = new Point(Ui.S(18), Ui.S(350));
            list.Test += delegate(object sender, string id) { Test(id); };
            AddAction("完成", ButtonKind.Primary, DialogResult.OK);
            FormClosed += delegate { stop.Cancel(); };
            Shown += async delegate { await Reload(); };
        }

        private async Task Reload()
        {
            string code = null;
            try { list.SetRows(await client.ProvidersAsync(stop.Token)); }
            catch (Exception failure) { code = EditText.Code(failure); }
            if (IsDisposed) return;
            if (code != null) error = ScrapeText.Error(code);
            Body.Invalidate();
        }

        private async void Test(string id)
        {
            if (busy) return;
            busy = true; list.SetResult(id, "正在测试…", null);
            KeyValuePair<bool, string> verdict = new KeyValuePair<bool, string>(false, "没有连上，请检查网络或密钥");
            try { verdict = await client.TestAsync(id, stop.Token); } catch (Exception) { }
            busy = false;
            if (IsDisposed) return;
            list.SetResult(id, verdict.Value, verdict.Key);
        }

        private async void Save()
        {
            string text = value.Text.Trim();
            if (busy || text.Length == 0) return;
            busy = true; error = note = null;
            string code = null;
            try { await client.SaveCredentialsAsync(new Dictionary<string, string> { { Fields[Math.Max(0, field.SelectedIndex)][0], text } }, stop.Token); }
            catch (Exception failure) { code = EditText.Code(failure); }
            busy = false;
            if (IsDisposed) return;
            if (code != null) error = ScrapeText.Error(code); else { note = "已保存。"; value.Text = ""; }
            await Reload();
        }

        // Copies the known credential entries from an existing Cocoon Art folder's config.json. Values are not shown.
        private async void Import()
        {
            if (busy) return;
            string folder;
            using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "选择原 Cocoon Art 所在的文件夹（里面有 config.json）", ShowNewFolderButton = false })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                folder = dialog.SelectedPath;
            }
            busy = true; error = note = null;
            string code = null;
            int copied = 0;
            try
            {
                string file = Path.Combine(folder, "config.json");
                if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024) throw new BackendRequestException("CONFIG_NOT_FOUND", 0);
                object old = Json.Parse(File.ReadAllText(file));
                Dictionary<string, string> values = new Dictionary<string, string>();
                foreach (string key in ScrapeClient.CredentialKeys) { string text = Json.Text(old, key); if (!string.IsNullOrWhiteSpace(text) && text.Length <= 512) values[key] = text; }
                copied = values.Count;
                if (copied > 0) await client.SaveCredentialsAsync(values, stop.Token);
            }
            catch (BackendRequestException failure) { code = failure.Code; }
            catch (Exception) { code = "CONFIG_INVALID"; }
            busy = false;
            if (IsDisposed) return;
            if (code == "CONFIG_NOT_FOUND" || code == "CONFIG_INVALID") error = "这个文件夹里没有可读取的 config.json。";
            else if (code != null) error = ScrapeText.Error(code);
            else note = copied == 0 ? "这份配置里没有填写任何来源密钥。" : "已导入 " + copied + " 项。可以逐个点「测试」确认。";
            await Reload();
        }

        private void PaintCanvas(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Ui.Write(g, "填写或更新一项密钥", Ui.Small, new Rectangle(Ui.S(24), Ui.S(282), Ui.S(552), Ui.S(18)), Ui.TextDim, TextFormatFlags.Left);
            string line = error ?? note ?? "密钥只保存在这台电脑的游戏库数据目录里，只会发给对应的来源网站。";
            Color tone = error != null ? Ui.Danger : note != null ? Ui.Ok : Ui.TextFaint;
            using (SolidBrush dot = new SolidBrush(tone)) g.FillEllipse(dot, Ui.S(24), Ui.S(396) + Ui.S(6), Ui.S(6), Ui.S(6));
            Ui.Write(g, line, Ui.Small, new Rectangle(Ui.S(38), Ui.S(396), Ui.S(538), Ui.S(30)), error != null ? Ui.Text : Ui.TextDim, TextFormatFlags.Left | TextFormatFlags.WordBreak);
        }
    }

    internal sealed class SourceList : Control
    {
        private sealed class Row { public ScrapeProvider Source; public string Result; public bool? Passed; }
        private readonly List<Row> rows = new List<Row>();
        private int hover = -1;
        public event Action<object, string> Test;
        public int Count { get { return rows.Count; } }

        public SourceList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void SetRows(List<ScrapeProvider> sources)
        {
            Dictionary<string, Row> old = new Dictionary<string, Row>();
            foreach (Row row in rows) old[row.Source.Id] = row;
            rows.Clear();
            foreach (ScrapeProvider source in sources)
            {
                Row kept;
                // A test verdict stays next to its source unless the source stopped being configured.
                rows.Add(old.TryGetValue(source.Id, out kept) && source.Configured ? new Row { Source = source, Result = kept.Result, Passed = kept.Passed } : new Row { Source = source });
            }
            Invalidate();
        }

        public void SetResult(string id, string text, bool? passed) { foreach (Row row in rows) if (row.Source.Id == id) { row.Result = text; row.Passed = passed; } Invalidate(); }
        internal void RaiseTest(string id) { if (Test != null) Test(this, id); }
        private int RowHeight { get { return Ui.S(44); } }
        private Rectangle TestRect(int index) { int w = Ui.Measure("测试", Ui.Small) + Ui.S(28); return new Rectangle(Width - w - Ui.S(12), index * RowHeight + (RowHeight - Ui.S(28)) / 2, w, Ui.S(28)); }
        private int Over(Point p) { for (int i = 0; i < rows.Count; i++) if (rows[i].Source.Configured && TestRect(i).Contains(p)) return i; return -1; }
        protected override void OnMouseMove(MouseEventArgs e) { int i = Over(e.Location); if (i != hover) { hover = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { int i = Over(e.Location); if (e.Button == MouseButtons.Left && i >= 0) RaiseTest(rows[i].Source.Id); base.OnMouseDown(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int h = RowHeight;
            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                int y = i * h;
                Ui.Fill(g, new RectangleF(0, y + Ui.S(2), Width - 1, h - Ui.S(4)), Ui.S(10), Ui.Card);
                Color tone = !row.Source.Configured ? Ui.TextFaint : row.Passed == false ? Ui.Danger : Ui.Ok;
                using (SolidBrush dot = new SolidBrush(tone)) g.FillEllipse(dot, Ui.S(14), y + h / 2f - Ui.S(3), Ui.S(6), Ui.S(6));
                Ui.Write(g, row.Source.Label, Ui.Strong, new Rectangle(Ui.S(30), y, Ui.S(150), h), Ui.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                string what = row.Source.Metadata && row.Source.Assets ? "资料和图片" : row.Source.Assets ? "图片" : "资料";
                string status = row.Result ?? (row.Source.Configured ? "可以使用 · " + what : "还没有填写密钥 · " + what);
                Ui.Write(g, status, Ui.Small, new Rectangle(Ui.S(184), y, Width - Ui.S(184) - Ui.S(84), h), row.Source.Configured ? Ui.TextDim : Ui.TextFaint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                if (!row.Source.Configured) continue;
                Rectangle test = TestRect(i);
                Ui.Fill(g, test, test.Height / 2f, i == hover ? Ui.Bg3 : Ui.Bg2);
                Ui.Write(g, "测试", Ui.Small, test, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
