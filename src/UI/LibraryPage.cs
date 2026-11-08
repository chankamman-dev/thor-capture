// Game library page: owns the Cocoon backend child for this window, loads the local catalog
// through its API and shows it natively. States (starting, failed, empty) get the mascot and
// one plain sentence; filtering and search run in memory on the loaded list.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThorCapture
{
    internal sealed class LibraryPage : Control
    {
        private readonly CocoonBackend backend;
        private readonly GameArtCache art;
        private readonly Strip top = new Strip();
        private readonly SearchBox search = new SearchBox();
        private readonly Segmented platforms = new Segmented("全部");
        private readonly ChoiceBox platformChoice = new ChoiceBox();
        private readonly FlatButton favorites, missing, reload, openWeb, importButton, pendingButton;
        private readonly GameEditSession edits;
        private readonly ContextMenuStrip importMenu = Menus.Create(), scrapeMenu = Menus.Create();
        private FlatButton scrapeButton;
        private DevicePullRunner devicePull;
        // Supplied by the main window: the connected handheld's serial (null when none), and whether a
        // capture or recording currently owns the device.
        public Func<string> DeviceSerial;
        public Func<bool> DeviceBusy;
        private ImportRunner importer;
        private readonly SynchronizationContext uiContext;
        private string libraryState; // not-imported / empty / ready (cocoon.library/1)
        private ImportDialog importDialog;
        private readonly GameGridView grid = new GameGridView();
        private readonly GameDetailView detail = new GameDetailView();
        private readonly StateView state;
        private readonly ToolTip tips = new ToolTip();
        private List<GameEntry> all = new List<GameEntry>();
        private List<string> platformIds = new List<string>();
        private System.Windows.Forms.Timer filterDelay;
        private int loadGeneration;
        private bool loading, loaded;
        internal int GamesLoaded { get { return all.Count; } }
        internal string Describe { get { return Current + " error=" + state.Error + " library=" + libraryState + " games=" + all.Count + " loaded=" + loaded + " loading=" + loading + " backend=" + backend.State + " import=" + (importer == null || importer.Job == null ? "-" : importer.Job.State); } }
        internal CocoonBackend Backend { get { return backend; } }
        internal GameEditSession Edits { get { return edits; } }

        public LibraryPage()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Ui.Bg0;
            // The UI context is captured once here; nested pumps can clear Current later.
            uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            backend = new CocoonBackend(uiContext)
            {
                NodePath = Settings.Get("cocoon.node", "node"),
                BackendDir = CocoonBackend.DefaultBackendDir(),
                DataRoot = CocoonBackend.DefaultDataRoot()
            };
            backend.StateChanged += delegate { OnBackend(); };
            art = new GameArtCache(backend, uiContext);
            art.Ready += delegate { grid.Invalidate(); detail.Invalidate(); };
            grid.Art = detail.Art = art;
            edits = new GameEditSession(backend, uiContext);
            edits.Changed += delegate { if (IsDisposed) return; detail.Invalidate(); LayoutTop(); };
            detail.Edits = edits;
            detail.EditRequested += delegate { ShowMetadata(); };
            detail.ScrapeRequested += delegate { ShowScrape(); };
            detail.ArtworkRequested += ShowArtwork;
            detail.StagedArt = StagedArt;

            top.Dock = DockStyle.Top;
            top.Height = Ui.S(64);
            top.BackColor = Ui.Bg0;
            top.Painter = delegate(object sender, PaintEventArgs e)
            {
                Ui.Write(e.Graphics, "游戏库", Ui.Heading, new Rectangle(Ui.S(24), 0, Ui.S(90), top.Height), Ui.Text, TextFormatFlags.VerticalCenter);
                if (loaded)
                {
                    int w = Ui.Measure("游戏库", Ui.Heading);
                    string count = grid.Games.Count == all.Count ? all.Count + " 款" : grid.Games.Count + " / " + all.Count + " 款";
                    // While images are being copied from the handheld, the count gives way to that progress.
                    string syncing = ArtStatus;
                    if (syncing != null) count = "同步图片 " + syncing;
                    Ui.Write(e.Graphics, count, Ui.Small, new Rectangle(Ui.S(24) + w + Ui.S(10), 0, Ui.S(120), top.Height), Ui.TextFaint, TextFormatFlags.VerticalCenter);
                }
            };
            search.Placeholder = "搜索名称、开发商、类型";
            search.BackColor = Ui.Bg0;
            search.Box.TextChanged += delegate { QueueFilter(); };
            platforms.SelectedIndexChanged += delegate { Filter(); };
            platformChoice.SelectedIndexChanged += delegate { Filter(); };
            favorites = Button("", "收藏", ButtonKind.Subtle, "只看收藏的游戏", delegate { favorites.Checked = !favorites.Checked; Filter(); });
            missing = Button("", "缺素材", ButtonKind.Subtle, "只看封面、Logo 或背景不全的游戏", delegate { missing.Checked = !missing.Checked; Filter(); });
            reload = Button(Glyphs.Refresh, null, ButtonKind.Ghost, "重新读取游戏库", delegate { Load(); });
            openWeb = Button(Glyphs.OpenOut, "在浏览器中编辑", ButtonKind.Ghost, "找图、裁剪与刮削暂时在 Cocoon 网页中进行", delegate { OpenWeb(); });
            scrapeButton = Button(Glyphs.Search, "刮削", ButtonKind.Ghost, "从网上的资料库为游戏补全资料", delegate { scrapeMenu.Show(scrapeButton, new Point(0, scrapeButton.Height + Ui.S(4))); });
            scrapeButton.Chevron = true;
            Menus.Add(scrapeMenu, "刮削选中的游戏…", null, delegate { ShowScrape(); });
            Menus.Add(scrapeMenu, "批量刮削…", null, delegate { ShowBatchScrape(); });
            Menus.Add(scrapeMenu, "来源设置…", null, delegate { ShowScrapeSettings(); });
            pendingButton = Button(null, "暂存的修改", ButtonKind.Subtle, "查看或撤销暂存在本机的修改（尚未应用）", delegate { ShowPending(); });
            pendingButton.Dot = Ui.Warn;
            pendingButton.Visible = false;
            importButton = Button("\uE896", "导入", ButtonKind.Ghost, "从掌机读取游戏库，或从原 Cocoon Art 数据目录导入（都可撤销）", delegate { importMenu.Show(importButton, new Point(0, importButton.Height + Ui.S(4))); });
            importButton.Chevron = true;
            Menus.Add(importMenu, "从掌机读取…", null, delegate { ShowDevicePull(); });
            Menus.Add(importMenu, "从 Cocoon Art 目录导入…", null, delegate { ShowImport(); });
            top.Controls.Add(search); top.Controls.Add(platforms); top.Controls.Add(platformChoice);
            top.Layout += delegate { LayoutTop(); };

            detail.Dock = DockStyle.Right;
            detail.Width = Ui.S(420);
            grid.Dock = DockStyle.Fill;
            grid.SelectionChanged += delegate { detail.Game = grid.Selected; };
            state = new StateView(this) { Dock = DockStyle.Fill, Visible = false };
            Controls.Add(grid);
            Controls.Add(detail);
            Controls.Add(state);
            Controls.Add(top);
            Disposed += delegate { StopArtSync(); if (importer != null) importer.Dispose(); if (devicePull != null) devicePull.Dispose(); edits.Dispose(); backend.Dispose(); art.Dispose(); tips.Dispose(); importMenu.Dispose(); };
        }

        private FlatButton Button(string glyph, string text, ButtonKind kind, string tip, EventHandler click)
        {
            FlatButton button = new FlatButton(glyph, text) { Kind = kind };
            button.Click += click;
            tips.SetToolTip(button, tip);
            top.Controls.Add(button);
            return button;
        }

        private void LayoutTop()
        {
            int h = top.Height, x = top.Width - Ui.S(20), size = Ui.S(34), y = (h - size) / 2;
            scrapeButton.Enabled = loaded && libraryState == "ready" && all.Count > 0;
            foreach (FlatButton button in new FlatButton[] { openWeb, reload, importButton, scrapeButton })
            {
                button.Height = size; button.Fit();
                if (button.Text.Length == 0) button.Width = size;
                x -= button.Width; button.Location = new Point(x, y); x -= Ui.S(6);
            }
            x -= Ui.S(10);
            // The draft entry only takes room while there is something staged (or a foreign draft to deal with).
            bool staged = loaded && libraryState == "ready" && (edits.Count > 0 || edits.NeedsArchive);
            bool interrupted = ApplyInterrupted;
            pendingButton.Text = interrupted ? "需要恢复掌机" : "暂存的修改";
            pendingButton.Dot = interrupted ? Ui.Danger : Ui.Warn;
            pendingButton.Hint = !interrupted && edits.Count > 0 ? edits.Count.ToString() : null;
            pendingButton.Visible = staged || interrupted;
            foreach (FlatButton button in new FlatButton[] { pendingButton, missing, favorites })
            {
                if (!button.Visible) continue;
                button.Height = size; button.Fit();
                x -= button.Width; button.Location = new Point(x, y); x -= Ui.S(6);
            }
            int left = Ui.S(24) + Ui.Measure("游戏库", Ui.Heading) + Ui.S(90);
            search.SetBounds(left, y, Ui.S(240), size);
            int platformsLeft = search.Right + Ui.S(12), room = x - Ui.S(10) - platformsLeft;
            platforms.Height = size; platforms.Fit();
            // Many platforms: fall back to a drop-down instead of clipping the segments.
            bool fits = platforms.Width <= room;
            platforms.Visible = fits && platformIds.Count > 0;
            platformChoice.Visible = !fits && platformIds.Count > 0;
            platforms.Location = new Point(platformsLeft, y);
            platformChoice.SetBounds(platformsLeft, y, Math.Min(Ui.S(180), Math.Max(Ui.S(120), room)), size);
            top.Invalidate();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && backend.State == BackendState.Stopped) backend.Start();
            if (Visible) ShowState();
        }

        // ---- backend and loading -------------------------------------------------
        private void OnBackend()
        {
            if (IsDisposed) return;
            if (backend.State == BackendState.Ready && !loaded && !loading) Load();
            if (backend.State != BackendState.Ready) { loaded = false; art.Clear(); edits.Reset(); StopArtSync(); }
            ShowState();
        }

        public void Retry()
        {
            backend.NodePath = Settings.Get("cocoon.node", "node");
            backend.BackendDir = CocoonBackend.DefaultBackendDir();
            backend.DataRoot = CocoonBackend.DefaultDataRoot();
            if (backend.State == BackendState.Ready) Load(); else backend.Start();
            ShowState();
        }

        private void Load()
        {
            if (backend.State != BackendState.Ready) return;
            int generation = ++loadGeneration;
            loading = true;
            ShowState();
            SynchronizationContext ui = uiContext;
            Task.Run(delegate
            {
                List<GameEntry> games = new List<GameEntry>();
                List<PlatformCount> counts = new List<PlatformCount>();
                string error = null, stateName = null;
                try
                {
                    // not-imported / empty / ready are different pages; broken libraries are errors.
                    stateName = Json.Text(backend.GetJson("/api/backend/library", false), "state");
                    if (stateName == "ready")
                    {
                        object list = backend.GetJson("/api/games", false);
                        if (list is List<object>) foreach (object node in (List<object>)list) { GameEntry game = GameEntry.From(node); if (game.Id >= 0) games.Add(game); }
                        object platformList = backend.GetJson("/api/platforms", false);
                        if (platformList is List<object>)
                            foreach (object node in (List<object>)platformList)
                                counts.Add(new PlatformCount { Id = Json.Text(node, "id"), Visible = (int)Json.Number(node, "visible", 0), Missing = (int)Json.Number(node, "missing", 0) });
                    }
                }
                catch (BackendRequestException failure) { error = failure.Code; }
                catch (Exception) { error = "LIBRARY_READ_FAILED"; }
                ui.Post(delegate { Loaded(generation, games, counts, stateName, error); }, null);
            });
        }

        private void Loaded(int generation, List<GameEntry> games, List<PlatformCount> counts, string stateName, string error)
        {
            if (IsDisposed || generation != loadGeneration) return;
            loading = false;
            // An import or restore in progress: keep polling the job and read again when it ends.
            if (error == "LIBRARY_IMPORT_BUSY")
            {
                // Busy with an import or restore (ours or another client's): read again shortly.
                loaded = false; state.Error = null; ShowState();
                SynchronizationContext again = uiContext;
                Task.Delay(600).ContinueWith(delegate { if (again != null) again.Post(delegate { if (!IsDisposed && !loaded && !loading) Load(); }, null); });
                return;
            }
            if (error != null) { loaded = false; state.Error = error; ShowState(); return; }
            state.Error = null;
            libraryState = stateName;
            int keep = grid.Selected == null ? -1 : grid.Selected.Id;
            all = games;
            // Grouped by platform in the backend's order (most games first), then by name.
            Dictionary<string, int> rank = new Dictionary<string, int>();
            for (int i = 0; i < counts.Count; i++) if (counts[i].Id != null) rank[counts[i].Id] = i;
            all.Sort(delegate(GameEntry a, GameEntry b)
            {
                int ra, rb;
                if (!rank.TryGetValue(a.Platform, out ra)) ra = 999;
                if (!rank.TryGetValue(b.Platform, out rb)) rb = 999;
                return ra != rb ? ra.CompareTo(rb) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            platformIds = new List<string>();
            foreach (PlatformCount count in counts) if (count.Id != null && count.Visible > 0) platformIds.Add(count.Id);
            string[] labels = new string[platformIds.Count + 1];
            labels[0] = "全部";
            for (int i = 0; i < platformIds.Count; i++) labels[i + 1] = GameEntry.PlatformName(platformIds[i]);
            platforms.SetItems(labels, 0);
            platformChoice.Items.Clear();
            foreach (string label in labels) platformChoice.Items.Add(label);
            platformChoice.SelectedIndex = 0;
            loaded = true;
            art.Clear();
            // The draft is bound to this library: start over and read it again after every load.
            edits.Reset();
            if (stateName == "ready") RefreshEdits();
            StartArtSync();
            Filter(keep);
            LayoutTop();
            ShowState();
        }

        // ---- artwork from the handheld -------------------------------------------------
        // The catalog only says where images live on the device. While one is connected, copy the ones
        // this library refers to into the local cache in the background: covers first, then logos, then backgrounds.
        internal Func<string, IDeviceArtReader> ArtReaderFactory;
        private CancellationTokenSource artStop;
        private DeviceArtSync artSync;
        private bool artRefreshQueued;
        internal string ArtStatus { get { DeviceArtSync sync = artSync; return sync == null ? null : sync.Done + "/" + sync.Total; } }

        private void StopArtSync()
        {
            if (artStop != null) { artStop.Cancel(); artStop = null; }
            artSync = null;
        }

        internal void StartArtSync()
        {
            StopArtSync();
            string serial = DeviceSerial == null ? null : DeviceSerial();
            if (serial == null || libraryState != "ready" || all.Count == 0 || backend.State != BackendState.Ready) return;
            List<string> uris = new List<string>();
            foreach (GameEntry game in all) if (game.CoverUri != null) uris.Add(game.CoverUri);
            foreach (GameEntry game in all) if (game.LogoUri != null) uris.Add(game.LogoUri);
            foreach (GameEntry game in all) if (game.HeroUri != null) uris.Add(game.HeroUri);
            if (uris.Count == 0) return;
            IDeviceArtReader reader = ArtReaderFactory != null ? ArtReaderFactory(serial) : new AdbArtReader(Adb.ExePath, serial, Settings.Get("cocoon.rootRead", "0") == "1");
            CancellationTokenSource stop = artStop = new CancellationTokenSource();
            DeviceArtSync sync = artSync = new DeviceArtSync(reader, backend.DataRoot);
            SynchronizationContext ui = uiContext;
            // Newly cached images appear as they arrive, but the page is refreshed at most about once a second.
            sync.Progress += delegate
            {
                if (artRefreshQueued || sync.Fetched == 0) return;
                artRefreshQueued = true;
                Task.Delay(1200).ContinueWith(delegate { ui.Post(delegate { artRefreshQueued = false; if (!IsDisposed && artSync == sync) RefreshArt(); }, null); });
            };
            Task.Run(async delegate
            {
                try { await sync.RunAsync(uris, stop.Token).ConfigureAwait(false); }
                catch (Exception) { }
                IDisposable owned = reader as IDisposable;
                if (owned != null) owned.Dispose();
                ui.Post(delegate { if (IsDisposed || artSync != sync) return; RefreshArt(); artSync = null; top.Invalidate(); }, null);
            });
        }

        private void RefreshArt()
        {
            art.Invalidate(); // lets images that were missing a moment ago be asked for again
            grid.Invalidate(); detail.Invalidate(); top.Invalidate();
        }

        private async void RefreshEdits()
        {
            try { await edits.Refresh(); } catch (Exception) { }
        }

        // ---- editing (local draft only) -------------------------------------------
        private string GameName(int id)
        {
            foreach (GameEntry game in all) if (game.Id == id) return game.Name;
            return "（游戏库中已找不到）";
        }

        public void ShowMetadata()
        {
            GameEntry game = grid.Selected;
            if (game == null || backend.State != BackendState.Ready) return;
            // A draft from another library has to be archived before anything can be staged.
            if (edits.NeedsArchive) { ShowPending(); return; }
            using (MetadataDialog dialog = new MetadataDialog(edits, game)) dialog.ShowDialog(FindForm());
            detail.Invalidate();
        }

        // ---- artwork ------------------------------------------------------------------
        private readonly Dictionary<string, Bitmap> stagedArt = new Dictionary<string, Bitmap>();
        private readonly HashSet<string> stagedLoading = new HashSet<string>();

        public void ShowArtwork(ArtworkSlot slot)
        {
            GameEntry game = grid.Selected;
            if (game == null || !ScrapeReady()) return;
            using (ArtworkDialog dialog = new ArtworkDialog(edits, game, slot)) dialog.ShowDialog(FindForm());
            detail.Invalidate();
        }

        // Pixels of a staged replacement for the detail view, fetched once through the editor.
        private Bitmap StagedArt(int gameId, ArtworkSlot slot)
        {
            StagedAsset asset = edits.AssetFor(gameId, slot);
            if (asset == null || asset.ShownSha == null) return null;
            string key = asset.ShownSha + ":" + slot;
            Bitmap image;
            if (stagedArt.TryGetValue(key, out image)) return image;
            if (stagedLoading.Add(key)) LoadStaged(asset, key);
            return null;
        }

        private async void LoadStaged(StagedAsset asset, string key)
        {
            Bitmap image = null;
            try
            {
                NativeEditorClient editor = await edits.Editor();
                ArtworkThumbnail thumb = await editor.PreviewAsync(asset.ItemId, asset.Slot, asset.ShownSha, 480, 480, ArtworkFit.Contain, null, edits.Token);
                using (MemoryStream stream = new MemoryStream(thumb.Bytes)) image = new Bitmap(new Bitmap(stream));
            }
            catch (Exception) { } // a video, or no preview: the badge alone says it is staged
            if (IsDisposed) { if (image != null) image.Dispose(); return; }
            if (image != null) stagedArt[key] = image;
            detail.Invalidate();
        }

        // ---- scraping (candidates and staged fields only; nothing is applied here) -----
        private bool ScrapeReady()
        {
            if (backend.State != BackendState.Ready || libraryState != "ready") return false;
            if (edits.NeedsArchive) { ShowPending(); return false; }
            return true;
        }

        public void ShowScrape()
        {
            GameEntry game = grid.Selected;
            if (game == null || !ScrapeReady()) return;
            using (ScrapeDialog dialog = new ScrapeDialog(edits, game)) dialog.ShowDialog(FindForm());
            detail.Invalidate();
        }

        public void ShowBatchScrape()
        {
            if (!ScrapeReady()) return;
            List<GameEntry> missing = new List<GameEntry>();
            foreach (GameEntry game in all) if (string.IsNullOrWhiteSpace(game.Developer) && string.IsNullOrWhiteSpace(game.Summary)) missing.Add(game);
            using (BatchScrapeDialog dialog = new BatchScrapeDialog(edits, missing, new List<GameEntry>(grid.Games), new List<GameEntry>(all))) dialog.ShowDialog(FindForm());
            detail.Invalidate();
        }

        public void ShowScrapeSettings()
        {
            if (backend.State != BackendState.Ready) return;
            using (ScrapeSettingsDialog dialog = new ScrapeSettingsDialog(backend)) dialog.ShowDialog(FindForm());
        }

        public void ShowPending()
        {
            if (backend.State != BackendState.Ready) return;
            if (ApplyInterrupted) { ShowApply(); return; } // an unfinished device run is resolved before anything else
            bool apply;
            using (PendingDialog dialog = new PendingDialog(edits, GameName)) { dialog.ShowDialog(FindForm()); apply = dialog.ApplyRequested; }
            if (apply) ShowApply();
        }

        // ---- applying to the handheld ----------------------------------------------
        // Tests supply a stand-in device; the app talks to the connected handheld over adb.
        internal Func<string, ICocoonDevice> DeviceFactory;
        // Journal, working snapshot and dated backups live beside the library's data root, not inside it.
        internal string ApplyRoot { get { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(backend.DataRoot).TrimEnd(Path.DirectorySeparatorChar)), "device-apply"); } }
        private bool ApplyInterrupted { get { try { return File.Exists(DeviceApplyJournal.PathIn(Path.Combine(ApplyRoot, "state"))); } catch (Exception) { return false; } } }

        internal DeviceApplier NewApplier(ICocoonDevice device)
        {
            return new DeviceApplier(device, new NodeSnapshotPatcher(backend.NodePath, backend.BackendDir), Path.Combine(ApplyRoot, "state"), Path.Combine(ApplyRoot, "backups"));
        }

        private GameEntry GameById(int id)
        {
            foreach (GameEntry game in all) if (game.Id == id) return game;
            return null;
        }

        public void ShowApply()
        {
            if (backend.State != BackendState.Ready || importDialog != null) return;
            string serial = DeviceSerial == null ? null : DeviceSerial();
            string blocked = serial == null ? "DEVICE_NOT_CONNECTED" : DeviceBusy != null && DeviceBusy() ? "DEVICE_BUSY" : null;
            if (blocked != null)
            {
                using (ThemedDialog note = new ThemedDialog("应用到掌机", null, null, 440, 72))
                {
                    note.AddText(ApplyText.Error(blocked), Ui.Body, Ui.TextDim, 24, 8, 392, 56);
                    note.AddAction("知道了", ButtonKind.Primary, DialogResult.OK);
                    note.ShowDialog(FindForm());
                }
                return;
            }
            ICocoonDevice device = DeviceFactory != null ? DeviceFactory(serial) : new AdbCocoonDevice(Adb.ExePath, serial);
            try
            {
                using (ApplyDialog dialog = new ApplyDialog(edits, NewApplier(device), GameById, RefreshAfterApply)) dialog.ShowDialog(FindForm());
            }
            finally { IDisposable owned = device as IDisposable; if (owned != null) owned.Dispose(); }
            LayoutTop();
            ShowState();
        }

        // After a successful apply the device holds the staged values: drop them from the draft and
        // replace the local copy with the snapshot that was just installed. Returns an error code or null.
        internal async Task<string> RefreshAfterApply(DeviceApplyResult applied)
        {
            try
            {
                List<string> entries = new List<string>();
                foreach (StagedMetadata entry in edits.Metadata) entries.Add(entry.EntryId);
                foreach (StagedAsset entry in edits.Assets) entries.Add(entry.EntryId);
                foreach (string entry in entries) await edits.Undo(entry);
                LibraryImportPlan plan = await Importer.Client.PlanAsync(applied.SnapshotRoot, false, false);
                await Importer.Execute(plan);
                for (int waited = 0; Importer.Error == null && Importer.Running && waited < 300; waited++) await Task.Delay(200);
                if (Importer.Error != null) return Importer.Error;
                return Importer.Job != null && Importer.Job.State == "completed" ? null : "IMPORT_FAILED";
            }
            catch (BackendRequestException failure) { return failure.Code; }
            catch (NativeEditorException failure) { return failure.Code; }
        }

        private void QueueFilter()
        {
            if (filterDelay == null)
            {
                filterDelay = new System.Windows.Forms.Timer { Interval = 150 };
                filterDelay.Tick += delegate { filterDelay.Stop(); Filter(); };
                Disposed += delegate { filterDelay.Dispose(); };
            }
            filterDelay.Stop(); filterDelay.Start();
        }

        private void Filter() { Filter(grid.Selected == null ? -1 : grid.Selected.Id); }

        private void Filter(int keep)
        {
            int index = platforms.Visible ? platforms.SelectedIndex : platformChoice.SelectedIndex;
            string platform = index > 0 && index <= platformIds.Count ? platformIds[index - 1] : null;
            string query = search.Box.Text.Trim();
            List<GameEntry> shown = new List<GameEntry>();
            foreach (GameEntry game in all)
            {
                if (platform != null && game.Platform != platform) continue;
                if (favorites.Checked && !game.Favorite) continue;
                if (missing.Checked && game.ArtCount == 3) continue;
                if (query.Length > 0 && !Matches(game, query)) continue;
                shown.Add(game);
            }
            grid.Grouped = platform == null;
            grid.SetGames(shown, keep);
            if (grid.Selected == null && shown.Count > 0) grid.Select(0);
            detail.Game = grid.Selected;
            grid.EmptyText = all.Count > 0 && shown.Count == 0 ? "没有匹配的游戏" : null;
            top.Invalidate();
        }

        private static bool Matches(GameEntry game, string query)
        {
            foreach (string field in new string[] { game.Name, game.FileName, game.Developer, game.Publisher, game.Genres, game.Region, GameEntry.PlatformName(game.Platform) })
                if (field != null && field.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;
            return false;
        }

        private void OpenWeb()
        {
            Uri endpoint = backend.Endpoint;
            if (endpoint == null) return;
            try { Process.Start(new ProcessStartInfo(endpoint.ToString()) { UseShellExecute = true }); } catch { }
        }

        // ---- states ---------------------------------------------------------------
        internal enum Phase { Starting, Failed, NotImported, Importing, Empty, Ready }

        internal Phase Current
        {
            get
            {
                if (state.Error != null || backend.State == BackendState.Failed) return Phase.Failed;
                if (importer != null && importer.Running && all.Count == 0) return Phase.Importing;
                if (backend.State != BackendState.Ready || loading || !loaded) return Phase.Starting;
                if (libraryState == "not-imported") return Phase.NotImported;
                return all.Count == 0 ? Phase.Empty : Phase.Ready;
            }
        }

        // ---- import -------------------------------------------------------------
        private ImportRunner Importer
        {
            get
            {
                if (importer == null)
                {
                    importer = new ImportRunner(backend, uiContext);
                    importer.Changed += delegate { ShowState(); };
                    importer.Completed += delegate { art.Invalidate(); Load(); };
                }
                return importer;
            }
        }

        public void ShowImport()
        {
            if (backend.State != BackendState.Ready || importDialog != null) return;
            using (importDialog = new ImportDialog(Importer)) importDialog.ShowDialog(FindForm());
            importDialog = null;
            ShowState();
        }

        internal DevicePullRunner DevicePull
        {
            get { if (devicePull == null) devicePull = new DevicePullRunner(backend, uiContext); return devicePull; }
        }

        // Reads the library from the connected handheld into an import plan; the same review and
        // confirmation as a folder import follow. Nothing is sent to the device before the dialog's own button.
        public void ShowDevicePull()
        {
            if (backend.State != BackendState.Ready || importDialog != null) return;
            string serial = DeviceSerial == null ? null : DeviceSerial();
            string blocked = serial == null ? "DEVICE_NOT_CONNECTED" : DeviceBusy != null && DeviceBusy() ? "DEVICE_BUSY" : edits.Count > 0 ? "DEVICE_PULL_PENDING_CHANGES" : null;
            if (blocked != null)
            {
                using (ThemedDialog note = new ThemedDialog("从掌机读取游戏库", null, null, 440, 72))
                {
                    note.AddText(PullText.Error(blocked), Ui.Body, Ui.TextDim, 24, 8, 392, 56);
                    note.AddAction("知道了", ButtonKind.Primary, DialogResult.OK);
                    note.ShowDialog(FindForm());
                }
                return;
            }
            using (importDialog = new ImportDialog(Importer, DevicePull, serial)) importDialog.ShowDialog(FindForm());
            importDialog = null;
            ShowState();
        }

        private void ShowState()
        {
            Phase phase = Current;
            bool ready = phase == Phase.Ready;
            state.Visible = !ready;
            grid.Visible = detail.Visible = ready;
            foreach (Control control in new Control[] { search, favorites, missing }) control.Enabled = ready;
            platforms.Visible = platforms.Visible && ready;
            platformChoice.Visible = platformChoice.Visible && ready;
            openWeb.Enabled = backend.State == BackendState.Ready;
            importButton.Enabled = backend.State == BackendState.Ready;
            reload.Enabled = backend.State == BackendState.Ready && !loading;
            state.Invalidate();
            top.Invalidate();
        }

        // Mascot plus one sentence for anything that is not the library itself.
        private sealed class StateView : Control
        {
            private readonly LibraryPage page;
            private readonly MascotClock mascot;
            private readonly ProgressLine progress = new ProgressLine { Value = -1 };
            private readonly FlatButton retry, import, fromDevice;
            public string Error;

            public StateView(LibraryPage owner)
            {
                page = owner;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = Ui.Bg0;
                mascot = new MascotClock(this);
                retry = new FlatButton(Glyphs.Refresh, "重试") { Kind = ButtonKind.Primary };
                retry.Click += delegate { Error = null; page.Retry(); };
                import = new FlatButton("\uE896", "从目录导入…") { Kind = ButtonKind.Subtle };
                import.Click += delegate { page.ShowImport(); };
                fromDevice = new FlatButton(Glyphs.Game, "从掌机读取…") { Kind = ButtonKind.Primary };
                fromDevice.Click += delegate { page.ShowDevicePull(); };
                Controls.Add(progress);
                Controls.Add(retry);
                Controls.Add(import);
                Controls.Add(fromDevice);
            }

            private void Explain(out string title, out string detail, out MascotMood mood)
            {
                mood = MascotMood.Idle;
                detail = null;
                Phase phase = page.Current;
                if (phase == Phase.Starting) { title = "正在打开游戏库…"; return; }
                if (phase == Phase.NotImported) { title = "还没有游戏库"; detail = "可以从连接的掌机只读读取，或导入你已有的 Cocoon Art 数据目录。两种方式都先给你看清单，之后也能撤销。"; return; }
                if (phase == Phase.Importing) { title = ImportText.Stage(page.importer.Job); detail = "关闭窗口不会中断导入。"; return; }
                if (phase == Phase.Empty) { title = "游戏库是空的"; detail = "这个游戏库里还没有游戏。"; return; }
                mood = MascotMood.Sleep;
                switch (Error != null ? "LOAD" : page.backend.FailureCode ?? "")
                {
                    case "NODE_NOT_FOUND": title = "找不到 Node.js"; detail = "游戏库组件需要 Node.js 24。安装后重试，或在设置文件中填写 cocoon.node。"; break;
                    case "BACKEND_NOT_FOUND": title = "找不到游戏库组件"; detail = "需要 cocoon-art-backend（含 backend.cjs），可在设置文件中填写 cocoon.backendDir。"; break;
                    case "DATA_ROOT_IN_USE": title = "游戏库数据正被另一个窗口使用"; detail = "关闭另一个 Thor Capture 窗口后重试。"; break;
                    case "PORT_IN_USE": case "LISTEN_FAILED": title = "游戏库服务无法在本机启动"; detail = "本机端口不可用，稍后重试。"; break;
                    case "TOKEN_UNAVAILABLE": case "ENDPOINT_REJECTED": title = "游戏库服务没有通过校验"; detail = "为安全起见没有连接，重试会重新启动服务。"; break;
                    case "LOAD":
                        title = "读取游戏库失败";
                        detail = Error == "LIBRARY_INVALID" ? "游戏库文件已损坏或版本不兼容，没有显示为空库以免误导。"
                            : Error == "LIBRARY_UNREADABLE" ? "没有读取游戏库目录的权限。"
                            : Error == "BACKEND_STOPPING" ? "游戏库服务正在关闭。" : "读取没有完成（" + Error + "）。";
                        break;
                    default: title = "游戏库服务意外停止"; detail = page.backend.FailureCode; break;
                }
            }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                int mid = ClientSize.Height / 2;
                progress.SetBounds(ClientSize.Width / 2 - Ui.S(80), mid + Ui.S(64), Ui.S(160), Ui.S(8));
                retry.Height = Ui.S(38); retry.Fit(); retry.Width = Math.Max(retry.Width, Ui.S(110));
                retry.Location = new Point(ClientSize.Width / 2 - retry.Width / 2, mid + Ui.S(78));
                // Two ways in, side by side: the handheld first.
                import.Height = fromDevice.Height = Ui.S(38);
                import.Fit(); import.Width = Math.Max(import.Width, Ui.S(140));
                fromDevice.Fit(); fromDevice.Width = Math.Max(fromDevice.Width, Ui.S(140));
                int pair = fromDevice.Width + Ui.S(10) + import.Width;
                fromDevice.Location = new Point(ClientSize.Width / 2 - pair / 2, mid + Ui.S(84));
                import.Location = new Point(fromDevice.Right + Ui.S(10), mid + Ui.S(84));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(BackColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                string title, detail; MascotMood mood;
                Explain(out title, out detail, out mood);
                Phase phase = page.Current;
                progress.Visible = phase == Phase.Starting || phase == Phase.Importing;
                if (phase == Phase.Importing && page.importer.Job != null && page.importer.Job.State == "preparing" && page.importer.Job.TotalBytes > 0)
                    progress.Value = page.importer.Job.CopiedBytes / (float)page.importer.Job.TotalBytes;
                else progress.Value = -1;
                retry.Visible = phase == Phase.Failed;
                import.Visible = fromDevice.Visible = phase == Phase.NotImported;
                mascot.Run(Visible);
                int d = Ui.S(120), mid = ClientSize.Height / 2;
                mascot.Area = new Rectangle(ClientSize.Width / 2 - d / 2 - Ui.S(24), mid - d - Ui.S(46), d + Ui.S(56), d + Ui.S(40));
                Mascot.Draw(g, new RectangleF(ClientSize.Width / 2 - d / 2, mid - d - Ui.S(16), d, d), mood, mascot.Time);
                Ui.Write(g, title, Ui.Heading, new Rectangle(0, mid, ClientSize.Width, Ui.S(28)), Ui.Text, TextFormatFlags.HorizontalCenter);
                if (detail != null)
                    Ui.Write(g, detail, Ui.Body, new Rectangle(ClientSize.Width / 2 - Ui.S(240), mid + Ui.S(32), Ui.S(480), Ui.S(40)), Ui.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            }
        }
    }

    // Left navigation: icon + label items with a white pill that springs between them.
    internal sealed class NavRail : Control
    {
        private readonly List<KeyValuePair<string, string>> items = new List<KeyValuePair<string, string>>();
        private readonly Anim slide, hoverA;
        private int selected, hover = -1;
        public Color StatusDot = Color.Empty;
        public event EventHandler SelectedIndexChanged;

        public NavRail()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Ui.Bg1;
            Width = Ui.S(76);
            slide = new Anim(this, 0, 520, Ease.Spring);
            hoverA = new Anim(this, 0, 150, Ease.OutCubic);
            Cursor = Cursors.Hand;
        }

        public void Add(string glyph, string label) { items.Add(new KeyValuePair<string, string>(glyph, label)); Invalidate(); }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value == selected || value < 0 || value >= items.Count) return;
                selected = value;
                slide.To(value, 520);
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        private Rectangle Item(float index) { int h = Ui.S(64); return new Rectangle(Ui.S(8), Ui.S(12) + (int)(index * (h + Ui.S(6))), Width - Ui.S(16), h); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int index = -1;
            for (int i = 0; i < items.Count; i++) if (Item(i).Contains(e.Location)) index = i;
            if (index != hover) { hover = index; hoverA.Snap(0); if (index >= 0) hoverA.To(1, 150); Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left && hover >= 0) SelectedIndex = hover; base.OnMouseDown(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (items.Count > 0)
            {
                RectangleF pill = Item(slide.Value);
                pill.Inflate(-Ui.S(2), -Ui.S(2));
                Ui.Fill(g, pill, Ui.S(14), Ui.Bg2);
            }
            for (int i = 0; i < items.Count; i++)
            {
                Rectangle r = Item(i);
                float near = Math.Max(0, 1 - Math.Abs(slide.Value - i));
                if (i == hover && i != selected) { RectangleF wash = r; wash.Inflate(-Ui.S(2), -Ui.S(2)); Ui.Fill(g, wash, Ui.S(14), Ui.Wash(0.04f * hoverA.Value)); }
                Color ink = Ui.Mix(Ui.TextFaint, Ui.Text, Math.Max(near, i == hover ? 0.6f : 0));
                if (Ui.IconFont != null) Ui.Glyph(g, items[i].Key, Ui.IconFont, new Rectangle(r.X, r.Y + Ui.S(10), r.Width, Ui.S(24)), ink);
                Ui.Write(g, items[i].Value, i == selected ? Ui.Strong : Ui.Small, new Rectangle(r.X, r.Y + Ui.S(36), r.Width, Ui.S(20)), ink, TextFormatFlags.HorizontalCenter);
            }
            if (!StatusDot.IsEmpty)
            {
                // Library service state, bottom of the rail.
                int d = Ui.S(8);
                using (SolidBrush dot = new SolidBrush(StatusDot)) g.FillEllipse(dot, Width / 2 - d / 2, Height - Ui.S(24), d, d);
            }
            using (Pen line = new Pen(Ui.Line)) g.DrawLine(line, Width - 1, 0, Width - 1, Height);
        }
    }
}
