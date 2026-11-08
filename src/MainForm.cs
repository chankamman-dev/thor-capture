using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThorCapture
{
    // key=value settings beside the EXE. The older capture-folder.txt is still read.
    internal static class Settings
    {
        private static readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string FilePath { get { return Path.Combine(Adb.AppDir(), "thor-capture-settings.txt"); } }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int split = line.IndexOf('=');
                    if (split > 0) values[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
                }
            }
            catch { }
        }

        public static string Get(string key, string fallback)
        {
            string value;
            return values.TryGetValue(key, out value) && value.Length > 0 ? value : fallback;
        }

        public static bool Set(string key, string value)
        {
            values[key] = value;
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> pair in values) lines.Add(pair.Key + "=" + pair.Value);
                File.WriteAllLines(FilePath, lines.ToArray(), Encoding.UTF8);
                return true;
            }
            catch { return false; }
        }
    }

    internal static class ShellThumbnails
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(NativeSize size, int flags, out IntPtr bitmap);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize { public int Width; public int Height; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr context,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        // Uses the Windows thumbnail cache, so MP4 frames come from the system codecs.
        public static Bitmap Get(string path, int size)
        {
            IShellItemImageFactory factory = null;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
                NativeSize request;
                request.Width = size;
                request.Height = size;
                IntPtr handle;
                if (factory.GetImage(request, 0x8 | 0x1, out handle) != 0 || handle == IntPtr.Zero) return null; // THUMBNAILONLY | BIGGERSIZEOK
                try { using (Bitmap raw = Image.FromHbitmap(handle)) return new Bitmap(raw); }
                finally { DeleteObject(handle); }
            }
            catch { return null; }
            finally { if (factory != null) Marshal.ReleaseComObject(factory); }
        }
    }

    internal partial class MainForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        private readonly Strip header = new Strip();
        private readonly Strip sideHead = new Strip();
        private readonly Strip stageBar = new Strip();
        private readonly Strip infoBar = new Strip();
        private readonly Strip statusBar = new Strip();
        private readonly SplitContainer split = new SplitContainer();
        private readonly GalleryView gallery = new GalleryView();
        private readonly PreviewBox preview = new PreviewBox();
        // Modules: the capture workspace and the game library, switched from the left rail.
        private readonly NavRail rail = new NavRail();
        private LibraryPage library;
        private readonly SearchBox search = new SearchBox();
        private readonly Segmented filterPicker = new Segmented("全部", "上屏", "下屏", "视频");
        private readonly Segmented screenPicker = new Segmented("上屏", "下屏", "双屏");
        private readonly Segmented viewPicker = new Segmented("带壳", "双屏", "单图", "1:1");
        // Picker order differs from the stored mode numbers (Dual was added later).
        private static readonly PreviewMode[] ViewModes = { PreviewMode.Device, PreviewMode.Dual, PreviewMode.Fit, PreviewMode.Actual };
        // Dual view style: the screens as they are, or a captioned showcase page.
        private readonly Segmented dualStyle = new Segmented("原始", "展示");
        private FlatButton showcaseText;
        private string showcaseKey;
        private readonly SkinPicker skinPicker = new SkinPicker();
        private readonly ToolTip tips = new ToolTip();
        private readonly ContextMenuStrip deviceMenu = Menus.Create();
        private readonly ContextMenuStrip itemMenu = Menus.Create();
        private readonly System.Windows.Forms.Timer recordingTimer = new System.Windows.Forms.Timer();
        private FlatButton deviceButton, rescanButton, recordButton, captureButton;
        private FlatButton exportButton, copyButton, saveButton, locateButton, deleteButton;
        private FlatButton folderButton, pathButton, changeButton;
        private FlatButton lampButton;
        private readonly LampPanel lampPanel = new LampPanel();
        private ToolStripDropDown lampDrop;
        private readonly System.Windows.Forms.Timer lampApply = new System.Windows.Forms.Timer();
        private bool lampOn = true;
        private Color lampColor = Color.Empty; // empty: the colourway's own lamp colour
        private int lampLevel = 100;

        private List<string> devices = new List<string>();
        private string serial;
        private List<DeviceDisplay> displays = new List<DeviceDisplay>();
        private bool busy;
        private bool detecting;
        private string deviceError;
        private RecordingSession recording;
        private DateTime recordingStarted;
        private Action stopBreathing;
        private string captureDir;
        private volatile int thumbGeneration;
        private int totalFiles;
        private string statusText = "";
        private bool statusBad;
        private string infoTitle = "";
        private string infoDetail = "";

        public MainForm(bool offline = false)
        {
            AutoScaleMode = AutoScaleMode.None;
            Settings.Load();
            captureDir = Path.Combine(Adb.AppDir(), "captures");
            string saved = Settings.Get("captureDir", null);
            if (saved == null)
            {
                try { saved = File.ReadAllText(Path.Combine(Adb.AppDir(), "capture-folder.txt")).Trim(); } catch { }
            }
            if (!string.IsNullOrEmpty(saved) && Path.IsPathRooted(saved) && Directory.Exists(saved)) captureDir = saved;
            try { Directory.CreateDirectory(captureDir); } catch { }

            Text = "Thor Capture";
            Font = Ui.Body;
            BackColor = Ui.Bg0;
            ForeColor = Ui.Text;
            KeyPreview = true;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(Ui.S(1400), work.Width * 94 / 100), Math.Min(Ui.S(900), work.Height * 94 / 100));
            MinimumSize = new Size(Math.Min(Ui.S(1080), work.Width), Math.Min(Ui.S(680), work.Height));
            try { Icon = MakeIcon(); } catch { }
            tips.InitialDelay = 350;

            BuildHeader();
            BuildSidebar();
            BuildStage();
            BuildStatus();

            split.Dock = DockStyle.Fill;
            split.FixedPanel = FixedPanel.Panel1;
            split.BackColor = Ui.Bg0;
            split.SplitterWidth = Math.Max(3, Ui.S(3));
            split.Panel1.BackColor = Ui.Bg1;
            split.Panel2.BackColor = Ui.Bg0;
            split.Panel1.Controls.Add(gallery);
            split.Panel1.Controls.Add(sideHead);
            split.Panel2.Controls.Add(preview);
            split.Panel2.Controls.Add(stageBar);
            split.Panel2.Controls.Add(infoBar);
            InitializeInputUi();
            InitializePlaybackUi();

            library = new LibraryPage { Dock = DockStyle.Fill, Visible = false };
            library.Backend.StateChanged += delegate { SyncRailStatus(); };
            library.DeviceSerial = delegate { return serial; };
            library.DeviceBusy = delegate { return busy; };
            rail.Dock = DockStyle.Left;
            rail.Add(Glyphs.Camera, "工作台");
            rail.Add(Glyphs.Game, "游戏库");
            rail.SelectedIndexChanged += delegate { ShowModule(rail.SelectedIndex); if (!offline) Settings.Set("ui.module", rail.SelectedIndex.ToString()); };
            // Reopen where the user left off (also what the dev loop relies on after each rebuild).
            if (!offline && Settings.Get("ui.module", "0") == "1") Shown += delegate { rail.SelectedIndex = 1; };

            Controls.Add(split);
            Controls.Add(library);
            Controls.Add(rail);
            Controls.Add(header);
            Controls.Add(statusBar);

            LoadLampSettings();
            int skin = DeviceSkin.IndexOf(Settings.Get("skin", "white"));
            skinPicker.SelectedIndex = skin;
            ApplySkin();
            int view = Math.Max(0, Math.Min(3, (int)ParseInt(Settings.Get("view", "0"))));
            viewPicker.SelectedIndex = Math.Max(0, Array.IndexOf(ViewModes, (PreviewMode)view));
            preview.Mode = (PreviewMode)view;
            skinPicker.Visible = lampButton.Visible = view == 0;

            KeyDown += OnShortcut;
            Shown += delegate
            {
                try
                {
                    split.Panel1MinSize = Ui.S(300);
                    split.Panel2MinSize = Ui.S(460);
                    int wanted = (int)ParseInt(Settings.Get("sidebar", Ui.S(400).ToString()));
                    split.SplitterDistance = Math.Max(split.Panel1MinSize, Math.Min(wanted, split.Width - split.Panel2MinSize - split.SplitterWidth));
                }
                catch (Exception) { } // window smaller than both minimums
                split.SplitterMoved += delegate { Settings.Set("sidebar", split.SplitterDistance.ToString()); };
                UpdatePathButton();
                RefreshList(null);
                UpdateControls();
                if(!offline) LoadDevices();
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy) { e.Cancel = true; Say("正在处理设备操作，完成或超时后即可关闭。", true); preview.Toast("设备操作进行中，暂时不能关闭", Ui.Warn); }
                else if(DelayCloseForLive(e)) return;
                else thumbGeneration++;
            };
            FormClosed += delegate { recordingTimer.Dispose(); DisposeInput(); library.Backend.Stop(); };
            Disposed += delegate { if (thumbs != null) { thumbs.Dispose(); thumbs = null; } };
            recordingTimer.Interval = 500;
            recordingTimer.Tick += delegate
            {
                int seconds = (int)(DateTime.UtcNow - recordingStarted).TotalSeconds;
                recordButton.Text = "停止  " + (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00");
                recordButton.Fit();
                LayoutHeader();
            };
        }

        private void ShowModule(int index)
        {
            split.Visible = index == 0;
            library.Visible = index == 1;
            if (index == 0) preview.Focus();
        }

        private void SyncRailStatus()
        {
            BackendState state = library.Backend.State;
            rail.StatusDot = state == BackendState.Ready ? Ui.Ok : state == BackendState.Failed ? Ui.Danger
                : state == BackendState.Starting || state == BackendState.Stopping ? Ui.Warn : Color.Empty;
            tips.SetToolTip(rail, state == BackendState.Ready ? "游戏库服务运行中" : state == BackendState.Failed ? "游戏库服务启动失败" : "");
            rail.Invalidate();
        }

        private static long ParseInt(string text)
        {
            long value;
            return long.TryParse(text, out value) ? value : 0;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                // Light title bar tinted to the header, so the frame reads as one surface.
                int on = 0;
                if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref on, 4);
                int caption = Ui.Bg1.R | (Ui.Bg1.G << 8) | (Ui.Bg1.B << 16);
                DwmSetWindowAttribute(Handle, 35, ref caption, 4);
            }
            catch { }
        }

        // ---- layout --------------------------------------------------------

        private FlatButton MakeButton(Control parent, string glyph, string text, ButtonKind kind, string tip, EventHandler click)
        {
            FlatButton button = new FlatButton(glyph, text);
            button.Kind = kind;
            if (click != null) button.Click += click;
            if (tip != null) tips.SetToolTip(button, tip);
            parent.Controls.Add(button);
            button.Fit();
            return button;
        }

        private void BuildHeader()
        {
            header.Dock = DockStyle.Top;
            header.Height = Ui.S(62);
            header.BackColor = Ui.Bg1;
            header.BottomLine = false;
            header.Painter = PaintHeader;
            deviceButton = MakeButton(header, null, "正在检测…", ButtonKind.Subtle, "选择设备", delegate { ShowDeviceMenu(); });
            deviceButton.Chevron = true;
            deviceButton.Dot = Ui.Warn;
            rescanButton = MakeButton(header, Glyphs.Refresh, null, ButtonKind.Ghost, "重新检测设备和屏幕", delegate { LoadDevices(); });
            screenPicker.Enabled = false;
            header.Controls.Add(screenPicker);
            tips.SetToolTip(screenPicker, "选择截图 / 录屏的屏幕");
            screenPicker.Icon = delegate(Graphics g, Rectangle r, int i, Color ink) { Ui.Thor(g, r, i, ink); };
            recordButton = MakeButton(header, Glyphs.Record, "录屏", ButtonKind.Subtle, "录制所选上屏、下屏或双屏 · 系统声音可开关 · 最长 3 分钟  (F6)", delegate { ToggleRecording(); });
            recordButton.GlyphColor = Ui.Danger;
            captureButton = MakeButton(header, Glyphs.Camera, "截图", ButtonKind.Primary, "截取所选屏幕  (F5)", delegate { CaptureNow(); });
            captureButton.Hint = "F5";
            captureButton.Fit();
            header.Layout += delegate { LayoutHeader(); };
        }

        private void LayoutHeader()
        {
            int h = header.Height, x = header.Width - Ui.S(18);
            int tall = Ui.S(38);
            captureButton.Height = recordButton.Height = tall;
            captureButton.Width = Math.Max(captureButton.PreferredWidth, Ui.S(118));
            x -= captureButton.Width;
            captureButton.Location = new Point(x, (h - tall) / 2);
            x -= Ui.S(8) + recordButton.Width;
            recordButton.Location = new Point(x, (h - tall) / 2);
            screenPicker.Height = Ui.S(34);
            screenPicker.Fit();
            x -= Ui.S(20) + screenPicker.Width;
            screenPicker.Location = new Point(x, (h - screenPicker.Height) / 2);
            rescanButton.Size = new Size(Ui.S(34), Ui.S(34));
            x -= Ui.S(14) + rescanButton.Width;
            rescanButton.Location = new Point(x, (h - rescanButton.Height) / 2);
            deviceButton.Height = Ui.S(34);
            deviceButton.Width = Math.Min(deviceButton.PreferredWidth, Ui.S(300));
            x -= Ui.S(6) + deviceButton.Width;
            deviceButton.Location = new Point(Math.Max(Ui.S(280), x), (h - deviceButton.Height) / 2);
            header.Invalidate();
        }

        private void PaintHeader(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int size = Ui.S(34);
            Rectangle logo = new Rectangle(Ui.S(18), (header.Height - size) / 2, size, size);
            DrawLogo(g, logo);
            int x = logo.Right + Ui.S(12);
            Ui.Write(g, "Thor Capture", Ui.Title, new Rectangle(x, 0, Ui.S(220), header.Height), Ui.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        private static void DrawLogo(Graphics g, Rectangle r)
        {
            // The mascot doubles as the logo.
            Mascot.Draw(g, r, MascotMood.Idle, 0);
        }

        private static Icon MakeIcon()
        {
            using (Bitmap bitmap = new Bitmap(64, 64))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    DrawLogo(g, new Rectangle(2, 2, 60, 60));
                }
                return Icon.FromHandle(bitmap.GetHicon());
            }
        }

        private void BuildSidebar()
        {
            sideHead.Dock = DockStyle.Top;
            sideHead.Height = Ui.S(136);
            sideHead.BackColor = Ui.Bg1;
            sideHead.Painter = delegate(object sender, PaintEventArgs e)
            {
                Ui.Write(e.Graphics, "图库", Ui.Heading, new Rectangle(Ui.S(16), Ui.S(14), Ui.S(80), Ui.S(28)), Ui.Text, TextFormatFlags.VerticalCenter);
                string count = gallery.Items.Count == totalFiles ? totalFiles + " 项" : gallery.Items.Count + " / " + totalFiles + " 项";
                int w = Ui.Measure("图库", Ui.Heading);
                Ui.Write(e.Graphics, count, Ui.Small, new Rectangle(Ui.S(16) + w + Ui.S(10), Ui.S(15), Ui.S(160), Ui.S(28)), Ui.TextFaint, TextFormatFlags.VerticalCenter);
            };
            folderButton = MakeButton(sideHead, Glyphs.FolderOpen, null, ButtonKind.Ghost, "打开图库文件夹", delegate { if (deviceShots) StartShotSync(); else OpenFolder(null); });
            BuildShotSource();
            sideHead.Controls.Add(search);
            search.Placeholder = "搜索日期、文件名、应用或别名";
            filterPicker.Stretch = true;
            filterPicker.Icon = delegate(Graphics g, Rectangle r, int i, Color ink)
            {
                if (i == 1 || i == 2) Ui.Thor(g, r, i - 1, ink);
                else if (i == 3) using (SolidBrush dot = new SolidBrush(i == filterPicker.SelectedIndex ? Ui.Danger : ink)) g.FillEllipse(dot, r.X + r.Width / 2f - Ui.S(3.5f), r.Y + r.Height / 2f - Ui.S(3.5f), Ui.S(7), Ui.S(7));
                else Ui.Thor(g, r, 2, ink);
            };
            sideHead.Controls.Add(filterPicker);
            sideHead.Layout += delegate
            {
                int pad = Ui.S(14);
                folderButton.Size = new Size(Ui.S(32), Ui.S(32));
                folderButton.Location = new Point(sideHead.Width - pad - folderButton.Width, Ui.S(12));
                LayoutShotSource(pad);
                search.SetBounds(pad, Ui.S(50), sideHead.Width - pad * 2, Ui.S(34));
                filterPicker.SetBounds(pad, Ui.S(92), sideHead.Width - pad * 2, Ui.S(32));
            };
            filterPicker.SelectedIndexChanged += delegate { ApplyFilter(null); };
            search.Box.TextChanged += delegate { QueueFilter(); };

            gallery.Dock = DockStyle.Fill;
            gallery.SelectionChanged += delegate { ShowSelected(); };
            gallery.ItemActivated += delegate { OpenSelected(); };
            gallery.ItemContextMenu += delegate(object sender, MouseEventArgs e) { ShowItemMenu(e.Location); };
            gallery.ViewportChanged += delegate { QueueThumbRequests(); };
            gallery.LockActionClicked += delegate { if (livePreview && recording == null) ToggleLive(); };

            Menus.Add(itemMenu, "打开", "Enter", delegate { OpenSelected(); });
            Menus.Add(itemMenu, "用系统查看器打开原片", null, delegate { OpenOriginal(); });
            Menus.Add(itemMenu, "视频导出…", null, delegate { ExportVideo(); });
            Menus.Add(itemMenu, "复制图片", "Ctrl+C", delegate { CopyToClipboard(false); });
            Menus.Add(itemMenu, "复制带壳图", "Ctrl+Shift+C", delegate { CopyToClipboard(true); });
            Menus.Add(itemMenu, "导出图片…", "Ctrl+E", delegate { ExportFramed(); });
            Menus.Add(itemMenu, "复制双屏图", null, delegate { CopyDual(); });
            Menus.Add(itemMenu, "导出双屏图…", null, delegate { ExportDual(); });
            Menus.Add(itemMenu, "另存为…", "Ctrl+S", delegate { SaveSelectedAs(); });
            Menus.Add(itemMenu, "设置应用别名…", null, delegate { EditAppAlias(); });
            Menus.Add(itemMenu, "查找此应用截图", null, delegate
            {
                if (gallery.Primary != null && gallery.Primary.Metadata != null)
                    search.Box.Text = gallery.Primary.Metadata.Package;
            });
            itemMenu.Items.Add(new ToolStripSeparator());
            Menus.Add(itemMenu, "在文件夹中显示", null, delegate { OpenFolder(SelectedPath()); });
            Menus.Add(itemMenu, "移入回收站", "Del", delegate { DeleteSelected(); });
        }

        private void BuildStage()
        {
            stageBar.Dock = DockStyle.Top;
            stageBar.Height = Ui.S(58);
            stageBar.BackColor = Ui.Bg0;
            stageBar.Controls.Add(viewPicker);
            tips.SetToolTip(viewPicker, "带壳：放进 Thor 外壳，自动配对同组上下屏\n双屏：不要外壳，上下屏按原始像素排在一起\n单图：适应窗口\n1:1：原始像素，可拖动平移");
            stageBar.Controls.Add(skinPicker);
            tips.SetToolTip(skinPicker, "外壳配色：黑色 / 白色 / 彩虹 / 透明紫");
            lampButton = MakeButton(stageBar, Glyphs.Lamp, "灯光", ButtonKind.Subtle, "摇杆灯光：开关、颜色、亮度", delegate { ShowLampPanel(); });
            lampButton.Chevron = true;
            deleteButton = MakeButton(stageBar, Glyphs.Delete, null, ButtonKind.Ghost, "移入回收站  (Del)", delegate { DeleteSelected(); });
            locateButton = MakeButton(stageBar, Glyphs.Locate, null, ButtonKind.Ghost, "在文件夹中显示", delegate { OpenFolder(SelectedPath()); });
            saveButton = MakeButton(stageBar, Glyphs.SaveAs, null, ButtonKind.Ghost, "另存为  (Ctrl+S)", delegate { SaveSelectedAs(); });
            copyButton = MakeButton(stageBar, Glyphs.Copy, null, ButtonKind.Ghost, "复制图片  (Ctrl+C)\n复制带壳图  (Ctrl+Shift+C)", delegate { CopyToClipboard((ModifierKeys & Keys.Shift) != 0); });
            exportButton = MakeButton(stageBar, Glyphs.Export, "导出带壳图", ButtonKind.Subtle, "导出图片：背景（含透明 PNG）、留白、投影、尺寸  (Ctrl+E)", delegate { ExportFramed(); });
            stageBar.Controls.Add(dualStyle);
            tips.SetToolTip(dualStyle, "原始：上下屏按原始像素排在一起\n展示：深色底、标题和下屏说明，下屏放大到与上屏同宽");
            dualStyle.SelectedIndex = Settings.Get("dualStyle", "0") == "1" ? 1 : 0;
            preview.Showcase = dualStyle.SelectedIndex == 1;
            dualStyle.SelectedIndexChanged += delegate
            {
                preview.Showcase = dualStyle.SelectedIndex == 1;
                Settings.Set("dualStyle", dualStyle.SelectedIndex.ToString());
                UpdateControls(); stageBar.PerformLayout();
            };
            showcaseText = MakeButton(stageBar, null, "文字…", ButtonKind.Subtle, "编辑展示图的标题和下屏说明", delegate { EditShowcaseText(); });
            stageBar.Layout += delegate
            {
                int h = stageBar.Height, pad = Ui.S(16);
                bool dualView = preview.Mode == PreviewMode.Dual;
                dualStyle.Visible = dualView;
                showcaseText.Visible = dualView && preview.Showcase;
                dualStyle.Height = Ui.S(34); dualStyle.Fit();
                dualStyle.Location = new Point(viewPicker.Right + Ui.S(10), (h - dualStyle.Height) / 2);
                showcaseText.Height = Ui.S(34); showcaseText.Fit();
                showcaseText.Location = new Point(dualStyle.Right + Ui.S(8), (h - showcaseText.Height) / 2);
                viewPicker.Height = skinPicker.Height = Ui.S(34);
                viewPicker.Fit();
                viewPicker.Location = new Point(pad, (h - viewPicker.Height) / 2);
                skinPicker.Width = skinPicker.PreferredWidth;
                skinPicker.Location = new Point(viewPicker.Right + Ui.S(10), (h - skinPicker.Height) / 2);
                lampButton.Height = Ui.S(34);
                lampButton.Fit();
                lampButton.Location = new Point(skinPicker.Right + Ui.S(8), (h - lampButton.Height) / 2);
                int x = stageBar.Width - pad;
                foreach (FlatButton button in new FlatButton[] { deleteButton, locateButton, saveButton, copyButton })
                {
                    button.Size = new Size(Ui.S(34), Ui.S(34));
                    x -= button.Width;
                    button.Location = new Point(x, (h - button.Height) / 2);
                    x -= Ui.S(4);
                }
                exportButton.Height = Ui.S(34);
                // Collapse to icon-only rather than sliding under the lamp button.
                exportButton.Compact = false;
                exportButton.Fit();
                int limit = (lampButton.Visible ? lampButton.Right : showcaseText.Visible ? showcaseText.Right : dualStyle.Visible ? dualStyle.Right : viewPicker.Right) + Ui.S(12);
                if (x - Ui.S(8) - exportButton.Width < limit) { exportButton.Compact = true; exportButton.Width = Ui.S(34); }
                x -= Ui.S(8) + exportButton.Width;
                exportButton.Location = new Point(x, (h - exportButton.Height) / 2);
            };
            viewPicker.SelectedIndexChanged += delegate
            {
                PreviewMode chosen = ViewModes[Math.Max(0, viewPicker.SelectedIndex)];
                preview.Mode = chosen;
                skinPicker.Visible = lampButton.Visible = chosen == PreviewMode.Device;
                Settings.Set("view", ((int)chosen).ToString());
                UpdateControls(); stageBar.PerformLayout();
            };
            skinPicker.SelectedIndexChanged += delegate
            {
                ApplySkin();
                Settings.Set("skin", preview.Skin.Key);
            };

            preview.Dock = DockStyle.Fill;
            preview.PlayRequested += delegate { OpenSelected(); };

            infoBar.Dock = DockStyle.Bottom;
            infoBar.Height = Ui.S(44);
            infoBar.BackColor = Ui.Bg1;
            infoBar.TopLine = true;
            infoBar.Painter = delegate(object sender, PaintEventArgs e)
            {
                int right = infoBar.Width - Ui.S(18);
                int x = Ui.S(20);
                int tw = Math.Min(Ui.Measure(infoTitle, Ui.Strong), Math.Max(0, right - x));
                Ui.Write(e.Graphics, infoTitle, Ui.Strong, new Rectangle(x, 0, tw + 2, infoBar.Height), Ui.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                x += tw + Ui.S(14);
                if (right > x)
                    Ui.Write(e.Graphics, infoDetail, Ui.Small, new Rectangle(x, 0, right - x, infoBar.Height), Ui.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
        }

        // ---- lamp ----------------------------------------------------------

        private void LoadLampSettings()
        {
            lampOn = Settings.Get("lampOn", "1") != "0";
            lampLevel = Math.Max(10, Math.Min(100, (int)ParseInt(Settings.Get("lampLevel", "100"))));
            string colour = Settings.Get("lampColor", "auto");
            int rgb;
            lampColor = colour != "auto" && int.TryParse(colour, System.Globalization.NumberStyles.HexNumber, null, out rgb)
                ? Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255) : Color.Empty;

            lampPanel.Changed += delegate
            {
                lampOn = lampPanel.LampOn;
                lampColor = lampPanel.LampColor;
                lampLevel = lampPanel.Level;
                UpdateLampButton();
                // Coalesce slider drags: the full frame is re-rendered on each apply.
                lampApply.Stop();
                lampApply.Start();
            };
            lampPanel.CustomRequested += delegate
            {
                if (lampDrop != null) lampDrop.Close();
                using (ColorDialog dialog = new ColorDialog())
                {
                    dialog.FullOpen = true;
                    dialog.Color = lampColor.IsEmpty ? preview.Skin.DefaultGlow : lampColor;
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    lampColor = Color.FromArgb(255, dialog.Color);
                    SaveLamp();
                    ApplySkin();
                }
            };
            lampApply.Interval = 60;
            lampApply.Tick += delegate
            {
                lampApply.Stop();
                SaveLamp();
                ApplySkin();
            };
        }

        private void SaveLamp()
        {
            Settings.Set("lampOn", lampOn ? "1" : "0");
            Settings.Set("lampLevel", lampLevel.ToString());
            Settings.Set("lampColor", lampColor.IsEmpty ? "auto" : (lampColor.ToArgb() & 0xFFFFFF).ToString("X6"));
        }

        // The preview, framed exports and framed video all take the lamp from preview.Skin.
        private void ApplySkin()
        {
            DeviceSkin skin = DeviceSkin.All[Math.Max(0, skinPicker.SelectedIndex)];
            preview.Skin = skin.WithLamp(lampOn, lampColor, lampLevel / 100f);
            UpdateLampButton();
        }

        private void UpdateLampButton()
        {
            if (lampButton == null) return;
            DeviceSkin skin = DeviceSkin.All[Math.Max(0, skinPicker.SelectedIndex)];
            Color shown = lampColor.IsEmpty ? skin.DefaultGlow : lampColor;
            lampButton.Dot = lampOn ? shown : Ui.TextFaint;
            lampButton.Text = lampOn ? "灯光 " + lampLevel + "%" : "灯光 关";
            lampButton.Fit();
            stageBar.PerformLayout();
            lampButton.Invalidate();
        }

        private void ShowLampPanel()
        {
            DeviceSkin skin = DeviceSkin.All[Math.Max(0, skinPicker.SelectedIndex)];
            lampPanel.AutoColor = skin.DefaultGlow;
            lampPanel.LampOn = lampOn;
            lampPanel.LampColor = lampColor;
            lampPanel.Level = lampLevel;
            if (lampDrop == null)
            {
                lampDrop = new ToolStripDropDown();
                lampDrop.Renderer = new MenuRenderer();
                lampDrop.BackColor = Ui.Surface;
                lampDrop.Padding = Padding.Empty;
                ToolStripControlHost host = new ToolStripControlHost(lampPanel);
                host.Margin = Padding.Empty;
                host.Padding = Padding.Empty;
                host.AutoSize = false;
                host.Size = lampPanel.Size;
                lampDrop.Items.Add(host);
            }
            lampDrop.Show(lampButton, new Point(0, lampButton.Height + Ui.S(4)));
        }

        private void BuildStatus()
        {
            statusBar.Dock = DockStyle.Bottom;
            statusBar.Height = Ui.S(30);
            statusBar.BackColor = Ui.Bg1;
            statusBar.TopLine = true;
            changeButton = MakeButton(statusBar, null, "更改…", ButtonKind.Link, "选择新的截图与录屏保存目录（已有文件保留在原目录）", delegate { ChooseDirectory(); });
            pathButton = MakeButton(statusBar, Glyphs.FolderOpen, "", ButtonKind.Link, "打开保存目录", delegate { OpenFolder(null); });
            pathButton.Font = Ui.Small;
            changeButton.Font = Ui.Small;
            statusBar.Layout += delegate
            {
                int h = statusBar.Height, x = statusBar.Width - Ui.S(12);
                changeButton.Height = pathButton.Height = h - 2;
                changeButton.Fit();
                x -= changeButton.Width;
                changeButton.Location = new Point(x, 1);
                pathButton.Width = Math.Min(pathButton.PreferredWidth, Math.Max(Ui.S(120), statusBar.Width / 2));
                x -= pathButton.Width + Ui.S(4);
                pathButton.Location = new Point(x, 1);
                statusBar.Invalidate();
            };
            statusBar.Painter = delegate(object sender, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int d = Ui.S(7);
                Color dot = statusBad ? Ui.Danger : busy ? Ui.Warn : displays.Count > 0 ? Ui.Ok : Ui.TextFaint;
                using (SolidBrush brush = new SolidBrush(dot))
                    e.Graphics.FillEllipse(brush, Ui.S(14), statusBar.Height / 2 - d / 2, d, d);
                int right = pathButton.Left - Ui.S(16);
                Ui.Write(e.Graphics, statusText, Ui.Small, new Rectangle(Ui.S(28), 0, Math.Max(0, right - Ui.S(28)), statusBar.Height),
                    statusBad ? Ui.Danger : Ui.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
        }

        private void UpdatePathButton()
        {
            string shown = captureDir;
            pathButton.Text = shown;
            tips.SetToolTip(pathButton, "打开保存目录\n" + captureDir);
            statusBar.PerformLayout();
        }

        // ---- state ---------------------------------------------------------

        private void Say(string message, bool bad)
        {
            statusText = message;
            statusBad = bad;
            statusBar.Invalidate();
        }

        private void Notify(string message, bool bad)
        {
            Say(message, bad);
            preview.Toast(message, bad ? Ui.Danger : Ui.Ok);
        }

        private void SetBusy(bool value)
        {
            busy = value;
            if(value) { SuspendLive(); PausePlayback(); }
            UpdateControls();
        }

        private void UpdateControls()
        {
            bool connected = displays.Count > 0 && serial != null;
            preview.Sleepy = !connected;
            rescanButton.Enabled = !busy;
            deviceButton.Enabled = !busy;
            screenPicker.Enabled = !busy && connected;
            captureButton.Enabled = !busy && connected;
            recordButton.Enabled = recording != null || (!busy && connected);
            changeButton.Enabled = !busy;
            UpdateInputControls();

            bool hasImage = preview.Current != null;
            bool hasSelection = gallery.Primary != null;
            bool selectedVideo=hasSelection&&gallery.Primary.IsVideo;
            copyButton.Enabled = hasImage;
            exportButton.Enabled = !busy&&(hasImage||selectedVideo);
            bool dual=preview.Mode==PreviewMode.Dual;
            SyncShowcaseText();
            string exportText=selectedVideo?"导出带壳视频":dual?(preview.Showcase?"导出展示图":"导出双屏图"):"导出带壳图";
            if(exportButton.Text!=exportText) { exportButton.Text=exportText; stageBar.PerformLayout(); }
            tips.SetToolTip(exportButton,selectedVideo?"导出所选视频；同组双屏合成一个带壳 MP4（Ctrl+E）":"导出图片：版式、背景（含透明 PNG）、留白、投影、尺寸（Ctrl+E）");
            UpdatePlaybackControls();
            saveButton.Enabled = hasSelection;
            locateButton.Enabled = hasSelection;
            deleteButton.Enabled = hasSelection;

            if (detecting) { deviceButton.Dot = Ui.Warn; deviceButton.Text = "正在检测…"; }
            else if (connected)
            {
                deviceButton.Dot = Ui.Ok;
                deviceButton.Text = serial + "  ·  " + displays.Count + " 屏";
            }
            else if (serial != null) { deviceButton.Dot = Ui.Warn; deviceButton.Text = serial + "  ·  未识别屏幕"; }
            else { deviceButton.Dot = deviceError != null ? Ui.Danger : Ui.TextFaint; deviceButton.Text = "未连接设备"; }
            string tip = "选择设备";
            if (connected)
            {
                List<string> parts = new List<string>();
                foreach (DeviceDisplay display in displays) parts.Add(display.ToString());
                tip = serial + "\n" + string.Join("\n", parts.ToArray());
            }
            else if (deviceError != null) tip = deviceError;
            tips.SetToolTip(deviceButton, tip);
            LayoutHeader();
            statusBar.Invalidate();
        }

        private void ShowDeviceMenu()
        {
            deviceMenu.Items.Clear();
            if (devices.Count == 0)
            {
                ToolStripMenuItem none = Menus.Add(deviceMenu, "未检测到已授权的设备", null, null);
                none.Enabled = false;
            }
            foreach (string device in devices)
            {
                string value = device;
                ToolStripMenuItem item = Menus.Add(deviceMenu, device, null, delegate
                {
                    if (value == serial) return;
                    serial = value;
                    LoadDisplays();
                });
                item.Checked = device == serial;
            }
            deviceMenu.Items.Add(new ToolStripSeparator());
            Menus.Add(deviceMenu, "重新检测", null, delegate { LoadDevices(); });
            deviceMenu.Show(deviceButton, new Point(0, deviceButton.Height + Ui.S(4)));
        }

        private void ShowItemMenu(Point location)
        {
            bool image = preview.Current != null;
            bool single = gallery.SelectedItems.Count == 1;
            foreach (ToolStripItem item in itemMenu.Items)
            {
                if (item.Text == "复制图片" || item.Text == "复制带壳图" || item.Text == "导出图片…") item.Enabled = image && single;
                else if (item.Text == "复制双屏图" || item.Text == "导出双屏图…") item.Enabled = image && single && preview.HasCompanion;
                else if (item.Text == "打开" || item.Text == "另存为…" || item.Text == "在文件夹中显示") item.Enabled = single;
                else if (item.Text == "设置应用别名…" || item.Text == "查找此应用截图")
                    item.Enabled = single && gallery.Primary.Metadata != null && gallery.Primary.Metadata.Package.Length > 0;
            }
            itemMenu.Show(gallery, location);
        }

        private void EditAppAlias()
        {
            GalleryItem item = gallery.Primary;
            if (item == null || item.Metadata == null || item.Metadata.Package.Length == 0) return;
            string package = item.Metadata.Package;
            using (ThemedDialog dialog = new ThemedDialog("设置应用别名", null, null, 460, 104))
            {
                TextField name = new TextField { Text = Settings.Get("appAlias." + package, "") };
                name.Box.MaxLength = 80;
                dialog.AddField(package, name, 24, 16, 412);
                dialog.AddAction("取消", ButtonKind.Subtle, DialogResult.Cancel);
                dialog.AddAction("保存", ButtonKind.Primary, DialogResult.OK);
                dialog.Shown += delegate { name.Box.Focus(); name.Box.SelectAll(); };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                bool persisted = Settings.Set("appAlias." + package, name.Text.Trim());
                RefreshList(item.Path);
                Notify(persisted ? "已更新应用别名" : "别名暂时生效，但设置文件写入失败", !persisted);
            }
        }

        private void OnShortcut(object sender, KeyEventArgs e)
        {
            bool typing = search.Box.Focused;
            if (e.KeyCode == Keys.F5) { CaptureNow(); e.Handled = true; }
            else if (e.KeyCode == Keys.F6) { ToggleRecording(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.F) { search.Box.Focus(); search.Box.SelectAll(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && typing) { search.Box.Text = ""; gallery.Focus(); e.Handled = true; }
            else if (typing) return;
            else if (e.KeyCode == Keys.Space && playback != null) { TogglePlayback(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.C) { CopyToClipboard(e.Shift); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.S) { SaveSelectedAs(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.E) { ExportFramed(); e.Handled = true; }
            else if (!e.Control && !e.Alt && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D4) { viewPicker.SelectedIndex = e.KeyCode - Keys.D1; e.Handled = true; }
            if (e.Handled) e.SuppressKeyPress = true;
        }

        // ---- device --------------------------------------------------------

        private async void LoadDevices()
        {
            if (busy) return;
            StopInput();
            detecting = true;
            deviceError = null;
            SetBusy(true);
            Say("正在检测设备…", false);
            try
            {
                devices = await Task.Run(() => Adb.Devices());
                displays.Clear();
                if (devices.Count == 0)
                {
                    serial = null;
                    Say("未连接设备 · 请连接 USB 并在掌机上允许调试。图库仍可使用。", false);
                }
                else if (serial == null || !devices.Contains(serial)) serial = devices[0];
            }
            catch (Exception failure)
            {
                devices.Clear();
                displays.Clear();
                serial = null;
                deviceError = failure.Message;
                Say("设备检测失败：" + failure.Message, true);
            }
            finally
            {
                detecting = false;
                SetBusy(false);
            }
            if (serial != null) LoadDisplays();
        }

        private async void LoadDisplays()
        {
            if (busy || serial == null) return;
            StopInput();
            string target = serial;
            displays.Clear();
            detecting = true;
            SetBusy(true);
            try
            {
                List<DeviceDisplay> found = await Task.Run(() => Adb.Displays(target));
                displays = found;
                List<string> labels = new List<string>();
                foreach (DeviceDisplay display in displays) labels.Add(display.Label);
                if (displays.Count > 1) labels.Add(displays.Count == 2 ? "双屏" : "全部");
                screenPicker.SetItems(labels.ToArray(), displays.Count > 1 ? displays.Count : 0);
                if (displays.Count == 0) Say("未识别到物理显示屏，请重新检测。", true);
                else Say("已连接 " + target + " · " + displays.Count + " 个屏幕", false);
            }
            catch (Exception failure)
            {
                deviceError = failure.Message;
                Say(failure.Message, true);
            }
            finally
            {
                detecting = false;
                SetBusy(false);
            }
        }

        private async void CaptureNow()
        {
            if (busy) return;
            if (serial == null || displays.Count == 0) { Notify("未连接设备，无法截图", true); return; }
            List<DeviceDisplay> targets = new List<DeviceDisplay>();
            int index = screenPicker.SelectedIndex;
            if (index >= displays.Count) targets.AddRange(displays);
            else if (index >= 0) targets.Add(displays[index]);
            else return;

            string target = serial;
            SetBusy(true);
            captureButton.Text = "截图中…";
            captureButton.Fit();
            LayoutHeader();
            Say("正在截图…", false);

            string newest = null;
            string failure = null;
            int savedCount = 0;
            string group = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string directory = captureDir;
            try
            {
                await WaitForLiveStop();
                await Task.Run(delegate
                {
                    Task<ForegroundSnapshot> foreground = Task.Run(() => ForegroundSnapshot.Read(target));
                    List<Action<ForegroundSnapshot>> metadataWrites = new List<Action<ForegroundSnapshot>>();
                    foreach (DeviceDisplay display in targets)
                    {
                        string error;
                        byte[] data = Adb.Capture(target, display.PhysicalId, out error);
                        if (data == null)
                        {
                            failure = display.Label + "：" + error;
                            continue;
                        }
                        string path = Path.Combine(directory, string.Format("thor-{0}-{1}.png", display.Slug, group));
                        try
                        {
                            using (FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.Write(data, 0, data.Length);
                            // Prefer showing the lower screen: it pairs with the upper one in the shell view.
                            if (newest == null || display.IsLower) newest = path;
                            savedCount++;
                            DateTime captured = DateTime.UtcNow;
                            DeviceDisplay capturedDisplay = display;
                            metadataWrites.Add(snapshot => CaptureMetadata.Create(snapshot, target, capturedDisplay, captured).Save(path));
                        }
                        catch (Exception writeFailure) { failure = writeFailure.Message; }
                    }
                    ForegroundSnapshot observed = foreground.Result;
                    foreach (Action<ForegroundSnapshot> write in metadataWrites)
                    {
                        try { write(observed); }
                        catch (Exception metadataFailure) { failure = "截图已保存，但应用信息写入失败：" + metadataFailure.Message; }
                    }
                });
            }
            catch (Exception error) { failure = error.Message; }
            finally
            {
                captureButton.Text = "截图";
                captureButton.Fit();
                SetBusy(false);
            }
            filterPicker.SelectedIndex = 0;
            search.Box.Text = "";
            RefreshList(newest);
            if (failure != null) Notify(failure, true);
            else if (newest != null) { preview.Flash(); Notify(savedCount > 1 ? "已保存 " + savedCount + " 张截图（同组配对）" : "已保存 " + Path.GetFileName(newest), false); }
        }

        // ---- gallery -------------------------------------------------------

        private static int ScreenOf(string name) { return GalleryIndex.ScreenOf(name); }

        private static string PeerOf(string path)
        {
            string peer = GalleryIndex.PeerName(Path.GetFileName(path));
            return peer == null ? null : Path.Combine(Path.GetDirectoryName(path), peer);
        }

        // ---- gallery index ---------------------------------------------------
        // The folder is scanned on a worker; the UI only binds the published snapshot.
        // Search and filter run in memory on that snapshot, debounced while typing.
        private readonly GalleryIndex indexer = new GalleryIndex();
        private List<IndexEntry> index = new List<IndexEntry>();
        private int scanGeneration;
        private string pendingSelect;
        private System.Windows.Forms.Timer filterDelay;
        internal int ScansApplied;

        private void RefreshList(string selectPath)
        {
            // A new capture or a changed save folder belongs to the computer gallery.
            if (deviceShots && selectPath != null && shotMirror != null && !string.Equals(Path.GetDirectoryName(Path.GetFullPath(selectPath)), Path.GetFullPath(shotMirror.Root), StringComparison.OrdinalIgnoreCase))
            {
                SetShotSource(false, selectPath);
                return;
            }
            if (selectPath != null) pendingSelect = selectPath;
            int generation = ++scanGeneration;
            string folder = GalleryFolder;
            SynchronizationContext ui = SynchronizationContext.Current;
            Task<List<IndexEntry>> scan = Task.Run(delegate
            {
                string error;
                List<IndexEntry> entries = indexer.Scan(folder, out error);
                if (error != null) throw new IOException(error);
                return entries;
            });
            Action<Task<List<IndexEntry>>> apply = delegate(Task<List<IndexEntry>> done)
            {
                if (IsDisposed || generation != scanGeneration) return;
                if (done.IsFaulted) Say("读取图库失败：" + done.Exception.GetBaseException().Message, true);
                else index = done.Result;
                string keep = pendingSelect;
                pendingSelect = null;
                ScansApplied++;
                ApplyFilter(keep);
            };
            if (ui == null) { scan.Wait(); apply(scan); }
            else scan.ContinueWith(delegate(Task<List<IndexEntry>> done) { ui.Post(delegate { apply(done); }, null); });
        }

        private void QueueFilter()
        {
            if (filterDelay == null)
            {
                filterDelay = new System.Windows.Forms.Timer { Interval = 150 };
                filterDelay.Tick += delegate { filterDelay.Stop(); ApplyFilter(null); };
                Disposed += delegate { filterDelay.Dispose(); };
            }
            filterDelay.Stop();
            filterDelay.Start();
        }

        // Rebinds the gallery from the snapshot. The current selection is kept when it is still
        // in the results; an unchanged selection does not reload its picture.
        private void ApplyFilter(string selectPath)
        {
            if (filterDelay != null) filterDelay.Stop();
            thumbGeneration++;
            string keep = selectPath ?? SelectedPath();
            totalFiles = index.Count;
            List<GalleryItem> items = new List<GalleryItem>();
            foreach (IndexEntry entry in GalleryIndex.Filter(index, filterPicker.SelectedIndex, search.Box.Text.Trim()))
                items.Add(new GalleryItem
                {
                    Path = entry.Path, Name = entry.Name, Time = entry.Time, WriteUtc = entry.WriteUtc, Size = entry.Size,
                    IsVideo = entry.IsVideo, Screen = entry.Screen, Metadata = entry.Metadata, Paired = entry.Paired
                });
            gallery.SetItems(items);
            if (items.Count == 0)
            {
                gallery.EmptyTitle = totalFiles == 0 ? (deviceShots ? (shotSync != null ? "正在读取掌机截图…" : "掌机上没有截图") : "图库是空的") : "没有匹配的项目";
                gallery.EmptyHint = "";
            }
            sideHead.Invalidate();

            int found = keep != null ? gallery.IndexOf(keep) : -1;
            gallery.SelectOnly(found >= 0 ? found : items.Count > 0 ? 0 : -1);
            StartThumbnails(items);
        }

        // ---- thumbnails -----------------------------------------------------------
        // Thumbnails come from the shared cache (one bounded worker, memory + disk cache).
        // Only on-screen cards are requested, plus one screen of prefetch; requests that
        // scroll out of range are cancelled and far-away bitmaps are released.
        private ThorCapture.Features.Thumbs.ThumbnailCache thumbs;
        private readonly Dictionary<string, int> thumbIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> thumbRequested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int thumbSize;
        private bool thumbQueued;

        private ThorCapture.Features.Thumbs.ThumbnailCache Thumbs
        {
            get
            {
                if (thumbs == null)
                {
                    thumbs = new ThorCapture.Features.Thumbs.ThumbnailCache(Path.Combine(Path.Combine(Adb.AppDir(), "cache"), "thumbnails"), 256L * 1024 * 1024, 96, 96, null);
                    thumbs.Ready += OnThumbReady;
                    ThorCapture.Features.Thumbs.ThumbnailCache cache = thumbs;
                    Task.Run(delegate { try { cache.Trim(); } catch (Exception error) { Log.Warn("Thumbnail cache trim failed: " + error.Message); } });
                }
                return thumbs;
            }
        }

        private void StartThumbnails(List<GalleryItem> items)
        {
            thumbIndex.Clear();
            for (int i = 0; i < items.Count; i++) thumbIndex[items[i].Path] = i;
            if (thumbs != null) foreach (string path in thumbRequested) if (!thumbIndex.ContainsKey(path)) thumbs.Cancel(path);
            thumbRequested.Clear();
            RequestVisibleThumbs();
        }

        // Coalesce scroll ticks: at most one request pass per UI message turn.
        private void QueueThumbRequests()
        {
            if (thumbQueued || !IsHandleCreated) return;
            thumbQueued = true;
            BeginInvoke((MethodInvoker)delegate { thumbQueued = false; RequestVisibleThumbs(); });
        }

        private static ThorCapture.Features.Thumbs.ThumbnailKey ThumbKey(GalleryItem item, int size)
        {
            // Built from the index snapshot; no disk access on the UI thread.
            return new ThorCapture.Features.Thumbs.ThumbnailKey(item.Path, item.Size, item.WriteUtc, size, 1);
        }

        private void RequestVisibleThumbs()
        {
            if (IsDisposed || !IsHandleCreated || gallery.Items.Count == 0) return;
            int size = gallery.ThumbPixels;
            if (size != thumbSize)
            {
                // Card size changed: drop held bitmaps so the sharper size is fetched.
                thumbSize = size;
                if (thumbs != null) foreach (string path in thumbRequested) thumbs.Cancel(path);
                thumbRequested.Clear();
            }
            List<int> visible = new List<int>(), nearby = new List<int>();
            gallery.Window(1f, visible, nearby);
            HashSet<string> wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<GalleryItem> items = gallery.Items;
            foreach (int i in visible) Want(items[i], ThorCapture.Features.Thumbs.ThumbnailPriority.Visible, wanted, size);
            foreach (int i in nearby) Want(items[i], ThorCapture.Features.Thumbs.ThumbnailPriority.Background, wanted, size);
            foreach (string path in thumbRequested) if (!wanted.Contains(path)) Thumbs.Cancel(path);
            thumbRequested = wanted;
            gallery.ReleaseThumbs(3f);
        }

        private void Want(GalleryItem item, ThorCapture.Features.Thumbs.ThumbnailPriority priority, HashSet<string> wanted, int size)
        {
            if (item.Thumb != null) return;
            wanted.Add(item.Path);
            if (!thumbRequested.Contains(item.Path)) Thumbs.Request(ThumbKey(item, size), priority);
        }

        // Raised on the cache worker. Hand the bitmap to the UI thread, which checks the card
        // is still the same file version and still missing its thumbnail; otherwise it is released.
        private void OnThumbReady(object sender, ThorCapture.Features.Thumbs.ThumbnailReadyEventArgs e)
        {
            Bitmap bitmap = e.Bitmap;
            if (bitmap == null) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    int i;
                    if (IsDisposed || !thumbIndex.TryGetValue(e.Key.Path, out i) || i >= gallery.Items.Count) { bitmap.Dispose(); return; }
                    GalleryItem item = gallery.Items[i];
                    if (item.Thumb != null || item.Size != e.Key.Length || item.WriteUtc != e.Key.WriteUtc) { bitmap.Dispose(); return; }
                    thumbRequested.Remove(item.Path);
                    gallery.SetThumb(i, bitmap);
                });
            }
            catch { bitmap.Dispose(); }
        }

        // Image.FromFile keeps the file locked, which breaks delete. Copy instead.
        private static Image LoadUnlocked(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (MemoryStream stream = new MemoryStream(bytes))
            using (Image decoded = Image.FromStream(stream))
            {
                return new Bitmap(decoded);
            }
        }

        private string SelectedPath()
        {
            GalleryItem item = gallery.Primary;
            return item == null ? null : item.Path;
        }

        // What the preview currently shows for the selection, so an unchanged selection
        // (for example after a search keystroke) is not decoded again.
        private Image shownImage;
        private string shownPath, shownDetail;
        private DateTime shownTime;
        private long shownSize;
        private int selectGeneration;
        internal int PictureLoads;

        private bool Showing(GalleryItem item)
        {
            if (item == null || item.Path != shownPath || item.Time != shownTime || item.Size != shownSize) return false;
            if (item.IsVideo) return playback != null;
            return shownImage != null && ReferenceEquals(preview.Current, shownImage);
        }

        private void ShowSelected()
        {
            if(recording!=null) return;
            GalleryItem item = gallery.Primary;
            int count = gallery.SelectedItems.Count;
            string many = count > 1 ? "已选 " + count + " 项 · " : "";
            if (Showing(item))
            {
                infoDetail = many + shownDetail;
                infoBar.Invalidate();
                UpdateControls();
                return;
            }
            StopPlayback();
            selectGeneration++;
            shownImage = null; shownPath = null;
            if (item == null)
            {
                preview.SetImage(null);
                infoTitle = "";
                infoDetail = totalFiles == 0 ? "" : "未选择";
                infoBar.Invalidate();
                UpdateControls();
                return;
            }
            if (item.IsVideo)
            {
                infoTitle = item.Name;
                shownDetail = "MP4 视频 · " + Ui.Bytes(item.Size) + " · " + item.Time.ToString("yyyy-MM-dd HH:mm:ss") + (item.Paired ? " · 同组双屏，可带壳合成" : "");
                infoDetail = many + shownDetail;
                StartPlayback(item);
                if (playback != null) Remember(item, null);
                infoBar.Invalidate();
                UpdateControls();
                return;
            }
            // Pictures decode on a worker; the previous picture stays up until the new one is
            // ready, and a result that arrives after a newer selection is discarded.
            int generation = selectGeneration;
            string path = item.Path, peer = PeerOf(item.Path);
            SynchronizationContext ui = SynchronizationContext.Current;
            Task<Image[]> load = Task.Run(delegate
            {
                Image image = LoadUnlocked(path);
                Image companion = null;
                if (peer != null && File.Exists(peer)) { try { companion = LoadUnlocked(peer); } catch { } }
                return new Image[] { image, companion };
            });
            Action<Task<Image[]>> apply = delegate(Task<Image[]> done)
            {
                bool stale = IsDisposed || generation != selectGeneration || recording != null || livePreview;
                if (done.IsFaulted)
                {
                    if (stale) return;
                    preview.SetImage(null);
                    infoTitle = item.Name;
                    infoDetail = "无法读取：" + done.Exception.GetBaseException().Message;
                }
                else if (stale)
                {
                    foreach (Image image in done.Result) if (image != null) image.Dispose();
                    return;
                }
                else
                {
                    Image image = done.Result[0], companion = done.Result[1];
                    PictureLoads++;
                    preview.SetImages(image, companion, item.Screen == 1);
                    infoTitle = item.Name;
                    string pair = item.Screen < 0 ? "" : companion != null ? " · 已配对" + (item.Screen == 1 ? "上屏" : "下屏") : " · 无同组" + (item.Screen == 1 ? "上屏" : "下屏");
                    string detail = image.Width + "×" + image.Height + " · " + Ui.Bytes(item.Size) + " · " + item.Time.ToString("yyyy-MM-dd HH:mm:ss") + pair;
                    if (item.Metadata != null)
                    {
                        string package = item.Metadata.Package;
                        infoTitle = package.Length > 0 ? CaptureMetadata.AppName(package) + " · " + item.Name : item.Name;
                        detail += package.Length > 0 ? " · " + package : " · 前台应用未识别";
                    }
                    Remember(item, image);
                    shownDetail = detail;
                    int selected = gallery.SelectedItems.Count;
                    infoDetail = (selected > 1 ? "已选 " + selected + " 项 · " : "") + detail;
                }
                infoBar.Invalidate();
                UpdateControls();
            };
            if (ui == null) { try { load.Wait(); } catch { } apply(load); }
            else load.ContinueWith(delegate(Task<Image[]> done) { ui.Post(delegate { apply(done); }, null); });
        }

        private void Remember(GalleryItem item, Image image)
        {
            shownImage = image; shownPath = item.Path; shownTime = item.Time; shownSize = item.Size;
        }

        // The shell thumbnail COM objects want an STA thread.
        private static TaskScheduler staScheduler;
        private static TaskScheduler StaTaskScheduler()
        {
            if (staScheduler == null) staScheduler = new StaScheduler();
            return staScheduler;
        }

        private sealed class StaScheduler : TaskScheduler
        {
            private readonly System.Collections.Concurrent.BlockingCollection<Task> queue = new System.Collections.Concurrent.BlockingCollection<Task>();

            public StaScheduler()
            {
                Thread worker = new Thread(delegate() { foreach (Task task in queue.GetConsumingEnumerable()) TryExecuteTask(task); });
                worker.IsBackground = true;
                worker.SetApartmentState(ApartmentState.STA);
                worker.Start();
            }

            protected override void QueueTask(Task task) { queue.Add(task); }
            protected override bool TryExecuteTaskInline(Task task, bool previouslyQueued) { return false; }
            protected override IEnumerable<Task> GetScheduledTasks() { return queue.ToArray(); }
        }

        private void OpenSelected()
        {
            if(gallery.Primary!=null&&gallery.Primary.IsVideo) { TogglePlayback(); return; }
            OpenOriginal();
        }

        private void OpenOriginal()
        {
            string path = SelectedPath();
            if (path == null) return;
            try { Process.Start(path); } catch (Exception failure) { Notify(failure.Message, true); }
        }

        private void OpenFolder(string select)
        {
            try
            {
                if (select != null) Process.Start("explorer.exe", "/select,\"" + select + "\"");
                else Process.Start("explorer.exe", "\"" + captureDir + "\"");
            }
            catch (Exception failure) { Notify(failure.Message, true); }
        }

        private void CopyToClipboard(bool framed)
        {
            if (preview.Current == null) return;
            // In the dual view the "framed" copy is what the view shows: both screens without the shell.
            if (framed && preview.Mode == PreviewMode.Dual) { CopyDual(); return; }
            try
            {
                if (framed)
                {
                    CopyStyled(ExportKind.Framed);
                    return;
                }
                else
                {
                    Clipboard.SetImage(preview.Current);
                    Notify("已复制图片到剪贴板", false);
                }
            }
            catch (Exception failure) { Notify(failure.Message, true); }
        }

        private async void DeleteSelected()
        {
            if(busy) return;
            List<string> paths = new List<string>();
            foreach (GalleryItem item in gallery.SelectedItems) paths.Add(item.Path);
            if (paths.Count == 0) return;
            if (deviceShots) { await DeleteDeviceShots(paths); return; }
            if (MessageBox.Show(this, "将选中的 " + paths.Count + " 项移入回收站？", "Thor Capture",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            await StopPlayback();
            preview.SetImage(null);
            int next = gallery.IndexOf(paths[0]);
            try
            {
                foreach (string path in paths)
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                string neighbour = null;
                List<GalleryItem> items = gallery.Items;
                for (int i = Math.Max(0, next); i < items.Count; i++) if (!paths.Contains(items[i].Path)) { neighbour = items[i].Path; break; }
                if (neighbour == null)
                    for (int i = Math.Min(items.Count - 1, next); i >= 0; i--) if (!paths.Contains(items[i].Path)) { neighbour = items[i].Path; break; }
                RefreshList(neighbour);
                Notify("已移入回收站 " + paths.Count + " 项", false);
            }
            catch (Exception failure) { RefreshList(null); Notify(failure.Message, true); }
        }

        private void ChooseDirectory()
        {
            if (busy) return;
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择截图与录屏保存目录（已有文件保留在原目录）";
                dialog.SelectedPath = captureDir;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                captureDir = dialog.SelectedPath;
                UpdatePathButton();
                if (deviceShots) SetShotSource(false, null);
                else RefreshList(null);
                if (Settings.Set("captureDir", captureDir)) Notify("保存位置已切换", false);
                else Notify("目录已切换，但无法记住设置", true);
            }
        }

        private void SaveSelectedAs()
        {
            string path = SelectedPath();
            if (path == null) return;
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.FileName = Path.GetFileName(path);
                dialog.Filter = Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? "MP4 视频|*.mp4" : "PNG 图片|*.png";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (!string.Equals(path, dialog.FileName, StringComparison.OrdinalIgnoreCase)) File.Copy(path, dialog.FileName, true);
                    Notify("已另存为 " + Path.GetFileName(dialog.FileName), false);
                }
                catch (Exception failure) { Notify(failure.Message, true); }
            }
        }

        // Showcase captions belong to a capture group (keyed by the upper screen's file name) and are kept in the settings.
        private string ShowcaseKey()
        {
            string path = SelectedPath();
            if (path == null) return null;
            string name = Path.GetFileName(path);
            return name.Contains("-lower-") ? name.Replace("-lower-", "-upper-") : name;
        }

        private void SyncShowcaseText()
        {
            string key = ShowcaseKey();
            if (key == showcaseKey) return;
            showcaseKey = key;
            string title = "", label = "下屏";
            GalleryItem item = gallery.Primary;
            if (item != null)
            {
                string app = item.Metadata == null ? "" : CaptureMetadata.AppName(item.Metadata.Package);
                if (app.Length > 0) label = "下屏 · " + app;
            }
            string saved = key == null ? "" : Settings.Get("showcase." + key, "");
            int split = saved.IndexOf('\u001f');
            if (split >= 0) { title = saved.Substring(0, split); label = saved.Substring(split + 1); }
            preview.ShowcaseTitle = title;
            preview.ShowcaseLabel = label;
        }

        private void EditShowcaseText()
        {
            if (preview.Current == null) { Notify("先选择一张截图", true); return; }
            SyncShowcaseText();
            using (ThemedDialog dialog = new ThemedDialog("展示图文字", null, null, 520, 190))
            {
                TextField title = dialog.AddField("标题（顶部一行，留空则不显示标题栏）", new TextField { Text = preview.ShowcaseTitle }, 24, 8, 472);
                TextField label = dialog.AddField("下屏说明（两屏之间，留空则只留一道窄缝）", new TextField { Text = preview.ShowcaseLabel }, 24, 78, 472);
                dialog.AddText("例如「03  地图 — 城镇图标和七个 HUD 项」「下屏 · 暗黑破坏神 III 伴侣」。只用于这组截图的展示图。", Ui.Small, Ui.TextDim, 24, 148, 472, 36);
                dialog.AddAction("取消", ButtonKind.Subtle, DialogResult.Cancel);
                dialog.AddAction("确定", ButtonKind.Primary, DialogResult.OK);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string t = title.Text.Replace("\r", " ").Replace("\n", " ").Replace("\u001f", " ").Trim(), l = label.Text.Replace("\r", " ").Replace("\n", " ").Replace("\u001f", " ").Trim();
                preview.ShowcaseTitle = t; preview.ShowcaseLabel = l;
                if (showcaseKey != null) Settings.Set("showcase." + showcaseKey, t + "\u001f" + l);
            }
        }

        private void CopyDual()
        {
            if (preview.Current == null) return;
            if (!preview.HasCompanion) { Notify("这张截图没有配对的另一屏，无法拼成双屏图", true); return; }
            CopyStyled(preview.Showcase ? ExportKind.Showcase : ExportKind.Stacked);
        }

        // Quick copy with the export settings last used in the export dialog.
        private void CopyStyled(ExportKind kind)
        {
            SyncShowcaseText();
            ExportStyle style = ExportStyle.Load();
            style.Kind = kind;
            style.Jpeg = false;
            try
            {
                using (Bitmap bitmap = style.Render(preview.Skin, preview.TopImage, preview.BottomImage, preview.ShowcaseTitle, preview.ShowcaseLabel, null))
                    ImageOutput.ToClipboard(bitmap);
                string what = kind == ExportKind.Showcase ? "展示图" : kind == ExportKind.Stacked ? "双屏图" : "带壳图";
                Notify("已复制" + what + "到剪贴板" + (style.HasAlpha ? "（PNG 保留透明）" : ""), false);
            }
            catch (Exception failure) { Notify(failure.Message, true); }
        }

        private void ExportDual()
        {
            if (preview.Current == null) { Notify("先选择一张截图，再导出双屏图", true); return; }
            if (!preview.HasCompanion) { Notify("这张截图没有配对的另一屏，无法拼成双屏图", true); return; }
            ShowExportDialog(preview.Showcase ? ExportKind.Showcase : ExportKind.Stacked);
        }

        private void ExportFramed()
        {
            if(gallery.Primary!=null&&gallery.Primary.IsVideo) { ExportVideo(); return; }
            if (preview.Current == null) { Notify("先选择一张截图，再导出图片", true); return; }
            bool dual = preview.Mode == PreviewMode.Dual && preview.HasCompanion;
            ShowExportDialog(dual ? (preview.Showcase ? ExportKind.Showcase : ExportKind.Stacked) : ExportKind.Framed);
        }

        // Export settings with a live preview; the dialog renders and saves on its own.
        private void ShowExportDialog(ExportKind kind)
        {
            SyncShowcaseText();
            ExportStyle style = ExportStyle.Load();
            style.Kind = kind;
            using (ExportImageDialog dialog = new ExportImageDialog(style, preview.Skin, preview.TopImage, preview.BottomImage, preview.ShowcaseTitle, preview.ShowcaseLabel))
            {
                dialog.ShowDialog(this);
                if (dialog.Outcome != null) Notify(dialog.Outcome, dialog.OutcomeIsError);
            }
        }

        // ---- recording -----------------------------------------------------

        private async void ToggleRecording()
        {
            if (recording != null)
            {
                RecordingSession active = recording;
                recordButton.Enabled = false;
                Say("正在结束录屏并保存 MP4…", false);
                try { await Task.Run(() => active.Stop()); }
                catch (Exception failure) { Notify(failure.Message, true); }
                finally { if (recording == active) recordButton.Enabled = true; }
                return;
            }
            if (busy || serial == null || displays.Count == 0) return;
            if (inputMonitor != null && inputMonitor.Error != null) { Notify("输入反馈异常，请重新连接反馈或关闭后录制原片", true); return; }
            int index = screenPicker.SelectedIndex;
            if (index < 0)
            {
                Notify("请选择录制的屏幕", true);
                return;
            }
            List<DeviceDisplay> targets=index>=displays.Count?new List<DeviceDisplay>(displays):new List<DeviceDisplay> { displays[index] };
            RecordingSession next = new RecordingSession(serial, targets, captureDir,recordSystemAudio);
            SetBusy(true);
            try
            {
                Say("正在检测录屏支持…", false);
                await StopPlayback();
                await WaitForLiveStop();
                await Task.Run(() => next.CheckSupport());
                if (inputMonitor != null) inputMonitor.BeginTimeline(next.TimelineSource);
                recording = next;
                BeginRecordingPreview();
                recordingStarted = DateTime.UtcNow;
                recordButton.Kind = ButtonKind.Danger;
                recordButton.Glyph = Glyphs.Stop;
                recordButton.GlyphColor = Color.Empty;
                recordButton.Dot = Color.White;
                recordButton.Text = "停止  00:00";
                recordButton.Fit();
                recordingTimer.Start();
                // The recording dot breathes on a 1.4 s sine instead of blinking.
                if (stopBreathing != null) stopBreathing();
                double breathStart = Motion.Now;
                stopBreathing = Motion.Pulse(delegate
                {
                    double phase = (Motion.Now - breathStart) / 1400.0 * Math.PI * 2;
                    int alpha = (int)(150 + 105 * Math.Cos(phase));
                    recordButton.Dot = Ui.Alpha(Color.White, alpha);
                    recordButton.Invalidate();
                });
                UpdateControls();
                Notify("正在录制" + (targets.Count>1?"双屏":targets[0].Label) + (recordSystemAudio?" · 系统声音":" · 无声")+" · 实时取景已开启 · F6 停止录制", false);
                string[] saved = await Task.Run(() => next.Execute());
                filterPicker.SelectedIndex = 0;
                search.Box.Text = "";
                RefreshList(saved[0]);
                Notify(targets.Count>1?"双屏录屏已保存 · 两段原片已配对，可带壳导出":"录屏已保存：" + Path.GetFileName(saved[0]), false);
                if(next.Warning!=null) Notify(next.Warning,true);
            }
            catch (Exception failure)
            {
                Notify(failure.Message, true);
                MessageBox.Show(this, failure.Message, "录屏未完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                if (inputMonitor != null)
                {
                    try { inputMonitor.EndTimeline(); }
                    catch (Exception error) { Notify("原始录屏保留，但按键时间线保存失败：" + error.Message, true); }
                }
                try { next.CopyTimeline(); }
                catch(Exception error) { Notify("原片已保留，但双屏按键时间线配对失败："+error.Message,true); }
                foreach(string saved in next.Saved) if(saved!=null) { RefreshList(saved); break; }
                EndRecordingPreview();
                recording = null;
                recordingTimer.Stop();
                if (stopBreathing != null) { stopBreathing(); stopBreathing = null; }
                recordButton.Kind = ButtonKind.Subtle;
                recordButton.Glyph = Glyphs.Record;
                recordButton.GlyphColor = Ui.Danger;
                recordButton.Dot = Color.Empty;
                recordButton.Text = "录屏";
                recordButton.Fit();
            }
            try { await fallbackCapture; }
            finally { SetBusy(false); if(!livePreview) ShowSelected(); }
        }
    }
}
