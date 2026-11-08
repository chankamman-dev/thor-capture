// Changing a game's cover, logo or background: browse what the scrape sources offer (or pick a file,
// including a video for the background), and stage the chosen one in the local draft. Staging is
// per slot; nothing reaches the handheld until the draft is applied.

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
    internal sealed class ArtworkDialog : ThemedDialog
    {
        private static readonly ArtworkSlot[] Slots = { ArtworkSlot.Icon, ArtworkSlot.Logo, ArtworkSlot.Hero };
        private static readonly string[] AssetSources = { "steamgriddb", "thegamesdb", "screenscraper", "igdb", "steam" };
        private readonly GameEditSession session;
        private readonly ScrapeClient client;
        private readonly GameEntry game;
        private readonly Segmented slots = new Segmented("封面", "Logo", "背景");
        private readonly TextField term = new TextField();
        private readonly FlatButton search, pickFile, primary;
        private readonly AssetGrid grid = new AssetGrid();
        private readonly ProgressLine progress = new ProgressLine { Value = -1 };
        private readonly Dictionary<ArtworkSlot, List<ScrapeAsset>> offered = new Dictionary<ArtworkSlot, List<ScrapeAsset>>();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private List<ScrapeProvider> sources;
        // One toggle per artwork source, with how many images it offered for this slot; unticked sources are hidden.
        private readonly List<FlatButton> chips = new List<FlatButton>();
        private readonly ToolTip tips = new ToolTip();
        private List<ScrapeAsset> all = new List<ScrapeAsset>();
        private ArtworkSlot slot;
        private string note, error, working;
        private bool busy, searched;
        private int generation;
        public bool Staged { get; private set; }
        internal string Describe { get { return "slot=" + slot + " busy=" + busy + " assets=" + grid.Count + " error=" + error + " note=" + note + " staged=" + Staged; } }

        public ArtworkDialog(GameEditSession owner, GameEntry entry, ArtworkSlot start) : base("图片与视频 · " + entry.Name, null, null, 780, 520)
        {
            session = owner; game = entry; client = new ScrapeClient(owner.Backend); slot = start;
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(Body, true, null);
            Body.Paint += PaintCanvas;
            slots.SelectedIndex = Array.IndexOf(Slots, start);
            slots.SelectedIndexChanged += delegate { slot = Slots[Math.Max(0, slots.SelectedIndex)]; ShowSlot(); };
            term.Text = entry.Name;
            term.Box.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Research(); } };
            search = new FlatButton(Glyphs.Search, "重新搜索") { Kind = ButtonKind.Subtle };
            search.Click += delegate { Research(); };
            pickFile = new FlatButton(Glyphs.FolderOpen, "从电脑选择…") { Kind = ButtonKind.Subtle };
            pickFile.Click += delegate { PickFile(); };
            foreach (Control control in new Control[] { slots, term, search, pickFile, grid, progress }) Body.Controls.Add(control);
            grid.SelectionChanged += delegate { Arrange(); };
            grid.Activated += delegate { UseSelected(); };
            AddAction("完成", ButtonKind.Subtle, DialogResult.OK);
            primary = AddAction("使用这张", ButtonKind.Primary, DialogResult.None);
            primary.Click += delegate { UseSelected(); };
            FormClosed += delegate { stop.Cancel(); grid.Clear(); tips.Dispose(); };
            Arrange();
            Shown += delegate { ShowSlot(); };
        }

        private CancellationToken Token { get { return stop.Token; } }

        private async void ShowSlot()
        {
            int mine = ++generation;
            grid.SetSlot(slot);
            error = note = null;
            List<ScrapeAsset> known;
            if (offered.TryGetValue(slot, out known)) { ShowAssets(known); Arrange(); return; }
            ShowAssets(new List<ScrapeAsset>());
            busy = true; working = "正在查找" + EditText.SlotName(slot) + "…"; Arrange();
            string code = null;
            List<ScrapeAsset> found = new List<ScrapeAsset>();
            try { found = await Collect(slot, null, mine); }
            catch (Exception failure) { code = EditText.Code(failure); }
            if (IsDisposed || mine != generation) return;
            busy = false;
            if (code != null) error = ScrapeText.Error(code);
            else
            {
                offered[slot] = found;
                ShowAssets(found);
                if (found.Count == 0) note = "来源里没有找到" + EditText.SlotName(slot) + "。可以改名称重新搜索，或从电脑选择文件。";
            }
            Arrange();
        }

        // Candidates for this game from sources that offer artwork; searches once when there are none yet.
        private async Task<List<ScrapeAsset>> Collect(ArtworkSlot target, string customTerm, int mine)
        {
            if (sources == null) { sources = await client.ProvidersAsync(Token); BuildChips(); }
            List<string> usable = new List<string>();
            foreach (ScrapeProvider provider in sources) if (provider.Configured && provider.Assets && Array.IndexOf(AssetSources, provider.Id) >= 0) usable.Add(provider.Id);
            if (usable.Count == 0) throw new BackendRequestException("NO_ARTWORK_SOURCE", 0);
            List<ScrapeCandidate> candidates = await client.CandidatesAsync(game.Id, Token);
            // Sources that have not looked for this game yet (or all of them, for a new name) search once.
            List<string> missing = customTerm != null ? usable : usable.FindAll(id => !candidates.Exists(c => c.Provider == id));
            if (missing.Count > 0 && (customTerm != null || !searched))
            {
                searched = true;
                await client.SearchAsync(game.Id, missing, customTerm, Token);
                candidates = await client.CandidatesAsync(game.Id, Token);
            }
            // The best few entries per source; artwork-only sources rate weakly against catalog titles, so their low scores are kept too.
            List<ScrapeAsset> assets = new List<ScrapeAsset>();
            HashSet<string> seen = new HashSet<string>();
            Dictionary<string, int> perSource = new Dictionary<string, int>();
            foreach (ScrapeCandidate candidate in candidates)
            {
                if (!usable.Contains(candidate.Provider)) continue;
                int taken; perSource.TryGetValue(candidate.Provider, out taken);
                if (taken >= 3) continue;
                perSource[candidate.Provider] = taken + 1;
                if (mine != generation) break;
                List<ScrapeAsset> list;
                try { list = await client.AssetsAsync(game.Id, candidate, target, Token); }
                catch (BackendRequestException) { continue; } // one source failing does not hide the others
                foreach (ScrapeAsset asset in list) if (seen.Add(asset.Url)) assets.Add(asset);
            }
            return assets;
        }

        private void BuildChips()
        {
            foreach (ScrapeProvider provider in sources)
            {
                if (!provider.Assets || Array.IndexOf(AssetSources, provider.Id) < 0) continue;
                bool usable = provider.Configured;
                FlatButton chip = new FlatButton(null, provider.Label) { Kind = ButtonKind.Subtle, Tag = provider.Id, Checked = usable, Enabled = usable };
                chip.Click += delegate { chip.Checked = !chip.Checked; Filter(); };
                tips.SetToolTip(chip, !provider.Configured ? "还没有填写密钥：在「刮削 → 来源设置」里填写后可用"
                    : provider.Id == "steam" ? "Steam 官方图片（竖版封面、Logo、背景大图）。商店连不上时通过 SteamGridDB 找到对应的 Steam 游戏" : "点一下只看或隐藏这个来源的图片");
                chips.Add(chip);
                Body.Controls.Add(chip);
            }
            Arrange();
        }

        // Everything fetched for the slot, shown through the source toggles; each toggle shows its count.
        private void ShowAssets(List<ScrapeAsset> list)
        {
            all = list;
            foreach (FlatButton chip in chips)
            {
                string id = (string)chip.Tag;
                string name = ScrapeClient.ProviderName(id);
                chip.Text = chip.Enabled ? name + " " + list.FindAll(asset => asset.Provider == id).Count : name;
            }
            Filter();
        }

        private void Filter()
        {
            HashSet<string> shown = new HashSet<string>();
            foreach (FlatButton chip in chips) if (chip.Checked) shown.Add((string)chip.Tag);
            grid.SetAssets(chips.Count == 0 ? all : all.FindAll(asset => shown.Contains(asset.Provider)));
            Arrange();
        }

        private async void Research()
        {
            if (busy) return;
            string text = term.Text.Trim();
            int mine = ++generation;
            offered.Clear();
            ShowAssets(new List<ScrapeAsset>());
            busy = true; error = note = null; working = "正在重新搜索…"; Arrange();
            string code = null;
            List<ScrapeAsset> found = null;
            try { found = await Collect(slot, text.Length == 0 || text == game.Name ? null : text, mine); }
            catch (Exception failure) { code = EditText.Code(failure); }
            if (IsDisposed || mine != generation) return;
            busy = false;
            if (code != null) error = code == "NO_ARTWORK_SOURCE" ? "没有能提供图片的来源。请在「刮削 → 来源设置」里填写 SteamGridDB 或 TheGamesDB 的密钥。" : ScrapeText.Error(code);
            else { offered[slot] = found; ShowAssets(found); if (found.Count == 0) note = "没有找到。可以换一个名称（比如英文或日文原名）再搜。"; }
            Arrange();
        }

        private async void UseSelected()
        {
            ScrapeAsset asset = grid.Selected;
            if (busy || asset == null) return;
            busy = true; error = note = null; working = "正在下载并暂存…"; Arrange();
            string code = null;
            try
            {
                byte[] bytes = await Task.Run(() => WebImage.Download(asset.Url, WebImage.MaxBytes, 60000, Token));
                await StageImage(bytes);
            }
            catch (System.Net.WebException) { code = "DOWNLOAD_FAILED"; }
            catch (Exception failure) { code = EditText.Code(failure); }
            Finish(code);
        }

        private async void PickFile()
        {
            if (busy) return;
            string path;
            using (OpenFileDialog dialog = new OpenFileDialog
            {
                Title = slot == ArtworkSlot.Hero ? "选择背景图片或视频" : "选择" + EditText.SlotName(slot) + "图片",
                Filter = slot == ArtworkSlot.Hero ? "图片或视频|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.mp4;*.webm|所有文件|*.*" : "图片|*.png;*.jpg;*.jpeg;*.webp;*.gif|所有文件|*.*"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                path = dialog.FileName;
            }
            busy = true; error = note = null; working = "正在读取并暂存…"; Arrange();
            string code = null;
            try
            {
                if (new FileInfo(path).Length > WebImage.MaxBytes) throw new BackendRequestException("FILE_TOO_LARGE", 0);
                byte[] bytes = File.ReadAllBytes(path);
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (slot == ArtworkSlot.Hero && (extension == ".mp4" || extension == ".webm")) await StageVideo(bytes);
                else await StageImage(bytes);
            }
            catch (IOException) { code = "FILE_UNREADABLE"; }
            catch (UnauthorizedAccessException) { code = "FILE_UNREADABLE"; }
            catch (Exception failure) { code = EditText.Code(failure); }
            Finish(code);
        }

        private async Task StageImage(byte[] bytes)
        {
            string item = await session.ItemIdAsync(game.Id);
            NativeEditorClient editor = await session.Editor();
            NativeEditorUploadResult upload = await editor.UploadAsync(item, slot, bytes, Token);
            session.Apply(await editor.StageAsync(item, slot, upload.Candidate.Id, session.Revision, null, Token));
        }

        // A background video is staged as the video itself; the poster frame is only for previews here.
        private async Task StageVideo(byte[] bytes)
        {
            string item = await session.ItemIdAsync(game.Id);
            NativeEditorClient editor = await session.Editor();
            NativeVideoUploadResult upload = await editor.UploadVideoAsync(item, bytes, Token);
            string video = (string)upload.Video["id"];
            try { await editor.VideoPosterAsync(item, video, 1000, Token); } catch (NativeEditorException) { }
            session.Apply(await editor.StageAsync(item, ArtworkSlot.Hero, video, session.Revision, null, Token));
        }

        private async void Finish(string code)
        {
            if (code != null && (code == "EDITOR_REVISION_CONFLICT" || code.StartsWith("EDITOR_", StringComparison.Ordinal))) { try { await session.Refresh(); } catch (Exception) { } }
            if (IsDisposed) return;
            busy = false;
            if (code == null) { Staged = true; note = "已暂存新的" + EditText.SlotName(slot) + "。可以继续换其他位置；全部好了再在「暂存的修改」里应用到掌机。"; }
            else error = code == "DOWNLOAD_FAILED" ? "下载这张图片失败，请换一张或稍后再试。"
                : code == "FILE_TOO_LARGE" ? "文件超过 32 MB。" : code == "FILE_UNREADABLE" ? "读取这个文件失败。"
                : code == "EDITOR_CODEC_UNAVAILABLE" ? "处理 JPG、WebP 和视频需要 FFmpeg（和 ffprobe），请检查 FFmpeg 设置。" : ScrapeText.Error(code);
            Arrange();
        }

        private void Arrange()
        {
            progress.Visible = busy;
            primary.Enabled = !busy && grid.Selected != null;
            search.Enabled = pickFile.Enabled = slots.Enabled = !busy;
            grid.Enabled = !busy;
            slots.Height = Ui.S(34); slots.Fit(); slots.Location = new Point(Ui.S(24), Ui.S(12));
            pickFile.Height = Ui.S(34); pickFile.Fit(); pickFile.Location = new Point(Ui.S(756) - pickFile.Width, Ui.S(12));
            search.Height = Ui.S(34); search.Fit(); search.Location = new Point(pickFile.Left - Ui.S(8) - search.Width, Ui.S(12));
            term.SetBounds(slots.Right + Ui.S(12), Ui.S(12), Math.Max(Ui.S(120), search.Left - Ui.S(8) - slots.Right - Ui.S(12)), Ui.S(34));
            int cx = Ui.S(24);
            foreach (FlatButton chip in chips) { chip.Height = Ui.S(30); chip.Fit(); chip.Location = new Point(cx, Ui.S(56)); cx += chip.Width + Ui.S(6); chip.Visible = true; }
            int gridTop = chips.Count > 0 ? 96 : 58;
            grid.SetBounds(Ui.S(24), Ui.S(gridTop), Ui.S(732), Ui.S(462 - gridTop));
            progress.SetBounds(Ui.S(290), Ui.S(250), Ui.S(200), Ui.S(8));
            Body.Invalidate();
        }

        private void PaintCanvas(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (busy) Ui.Write(g, working, Ui.Body, new Rectangle(0, Ui.S(222), Body.Width, Ui.S(24)), Ui.TextDim, TextFormatFlags.HorizontalCenter);
            StagedAsset staged = session.AssetFor(game.Id, slot);
            string line = error ?? note ?? (staged != null ? "这个位置已有暂存的新" + EditText.SlotName(slot) + "；再选一张会替换它。" : slot == ArtworkSlot.Hero ? "可以选图片，也可以从电脑选 MP4/WebM 视频当背景。" : "双击或点「使用这张」暂存。");
            Color tone = error != null ? Ui.Danger : note != null ? (Staged ? Ui.Ok : Ui.Warn) : Ui.TextFaint;
            using (SolidBrush dot = new SolidBrush(tone)) g.FillEllipse(dot, Ui.S(24), Ui.S(474) + Ui.S(6), Ui.S(6), Ui.S(6));
            Ui.Write(g, line, Ui.Small, new Rectangle(Ui.S(38), Ui.S(474), Ui.S(718), Ui.S(40)), error != null ? Ui.Text : Ui.TextDim, TextFormatFlags.Left | TextFormatFlags.WordBreak);
        }
    }

    // Thumbnails offered for one slot, laid out in the slot's own shape; the images load as they arrive.
    internal sealed class AssetGrid : Control
    {
        private List<ScrapeAsset> assets = new List<ScrapeAsset>();
        private readonly Dictionary<string, Bitmap> thumbs = new Dictionary<string, Bitmap>();
        private readonly HashSet<string> loading = new HashSet<string>(), failed = new HashSet<string>();
        private readonly SemaphoreSlim lanes = new SemaphoreSlim(6);
        private ArtworkSlot slot;
        private float scroll;
        private int selected = -1, hover = -1;
        public event EventHandler SelectionChanged, Activated;
        public int Count { get { return assets.Count; } }
        public ScrapeAsset Selected { get { return selected >= 0 && selected < assets.Count ? assets[selected] : null; } }

        public AssetGrid()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
        }

        public void SetSlot(ArtworkSlot value) { slot = value; }
        public void SetAssets(List<ScrapeAsset> list) { assets = list; selected = -1; scroll = 0; Invalidate(); if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty); }
        internal void Select(int index) { selected = index >= 0 && index < assets.Count ? index : -1; Invalidate(); if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty); }
        public void Clear() { foreach (Bitmap bitmap in thumbs.Values) bitmap.Dispose(); thumbs.Clear(); }

        private Size Tile { get { return slot == ArtworkSlot.Icon ? new Size(Ui.S(132), Ui.S(196)) : slot == ArtworkSlot.Logo ? new Size(Ui.S(232), Ui.S(116)) : new Size(Ui.S(352), Ui.S(116)); } }
        private int Gap { get { return Ui.S(12); } }
        private int Columns { get { return Math.Max(1, (Width + Gap) / (Tile.Width + Gap)); } }
        private int RowHeight { get { return Tile.Height + Ui.S(26) + Gap; } }
        private Rectangle Cell(int index) { return new Rectangle((index % Columns) * (Tile.Width + Gap), (int)((index / Columns) * RowHeight - scroll), Tile.Width, Tile.Height); }
        private int At(Point p) { for (int i = 0; i < assets.Count; i++) if (Cell(i).Contains(p)) return i; return -1; }

        protected override void OnMouseEnter(EventArgs e) { Focus(); base.OnMouseEnter(e); }
        protected override void OnMouseMove(MouseEventArgs e) { int i = At(e.Location); if (i != hover) { hover = i; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (Enabled && e.Button == MouseButtons.Left) { int i = At(e.Location); if (i >= 0) Select(i); } base.OnMouseDown(e); }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { if (Enabled && At(e.Location) >= 0 && Activated != null) Activated(this, EventArgs.Empty); base.OnMouseDoubleClick(e); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int rows = (assets.Count + Columns - 1) / Columns;
            scroll = Math.Max(0, Math.Min(Math.Max(0, rows * RowHeight - Height), scroll - e.Delta * Ui.S(90) / 120f));
            Invalidate();
            base.OnMouseWheel(e);
        }

        private void Load(string url)
        {
            if (thumbs.ContainsKey(url) || loading.Contains(url) || failed.Contains(url)) return;
            loading.Add(url);
            Task.Run(async delegate
            {
                Bitmap image = null;
                await lanes.WaitAsync().ConfigureAwait(false);
                try { using (MemoryStream stream = new MemoryStream(WebImage.Download(url, 8 * 1024 * 1024, 30000, CancellationToken.None))) image = new Bitmap(stream); }
                catch (Exception) { }
                finally { lanes.Release(); }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        loading.Remove(url);
                        if (IsDisposed) { if (image != null) image.Dispose(); return; }
                        if (image != null) thumbs[url] = image; else failed.Add(url);
                        Invalidate();
                    });
                }
                catch (InvalidOperationException) { if (image != null) image.Dispose(); }
            });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            for (int i = 0; i < assets.Count; i++)
            {
                Rectangle cell = Cell(i);
                if (cell.Bottom + Ui.S(26) < 0 || cell.Top > Height) continue;
                ScrapeAsset asset = assets[i];
                // Logos are mostly light artwork meant for dark backgrounds.
                Ui.Fill(g, cell, Ui.S(10), slot == ArtworkSlot.Logo ? Color.FromArgb(46, 46, 44) : Ui.Card);
                Bitmap image;
                if (thumbs.TryGetValue(asset.Thumb, out image))
                {
                    using (GraphicsPath clip = Ui.Round(cell, Ui.S(10)))
                    {
                        GraphicsState state = g.Save();
                        g.SetClip(clip, CombineMode.Intersect);
                        RectangleF inner = cell; inner.Inflate(-Ui.S(4), -Ui.S(4));
                        float scale = Math.Min(inner.Width / image.Width, inner.Height / image.Height);
                        g.DrawImage(image, inner.X + (inner.Width - image.Width * scale) / 2, inner.Y + (inner.Height - image.Height * scale) / 2, image.Width * scale, image.Height * scale);
                        g.Restore(state);
                    }
                }
                else
                {
                    Load(asset.Thumb);
                    Ui.Write(g, failed.Contains(asset.Thumb) ? "预览失败" : "载入中", Ui.Small, cell, Ui.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                if (i == selected) Ui.Stroke(g, new RectangleF(cell.X - 1.5f, cell.Y - 1.5f, cell.Width + 3, cell.Height + 3), Ui.S(11), Ui.Text, 2.2f);
                else if (i == hover && Enabled) Ui.Stroke(g, cell, Ui.S(10), Ui.TextFaint, 1.2f);
                string caption = (asset.Provider == "steamgriddb" ? "SGDB" : asset.Provider == "thegamesdb" ? "TGDB" : ScrapeClient.ProviderName(asset.Provider)) + (asset.Width > 0 ? " · " + asset.Width + "×" + asset.Height : "") + (string.IsNullOrEmpty(asset.Style) ? "" : " · " + asset.Style);
                Ui.Write(g, caption, Ui.Small, new Rectangle(cell.X, cell.Bottom + Ui.S(4), cell.Width, Ui.S(18)), Ui.TextDim, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }
    }
}
