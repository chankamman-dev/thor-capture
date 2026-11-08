// Importing an existing Cocoon library (cocoon.library-import/1): pick the old data root,
// review the plan, confirm, follow the job. The job belongs to the page's ImportRunner, so
// closing the dialog does not stop or orphan it; restore is a separate, confirmed step.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThorCapture
{
    internal static class ImportText
    {
        public static string Error(string code)
        {
            switch (code ?? "")
            {
                case "IMPORT_SOURCE_INVALID": case "IMPORT_SOURCE_MISSING": return "所选目录不是有效的 Cocoon 数据目录，请选择包含 _data\\cocoon_db 的目录。";
                case "IMPORT_PATH_UNSAFE": return "来源与当前数据目录重叠或包含链接，为安全起见没有执行。";
                case "IMPORT_LIMIT_EXCEEDED": return "来源超出本次导入的大小或文件数量上限。";
                case "IMPORT_LIBRARY_INVALID": case "IMPORT_PENDING_INVALID": return "原游戏库或待处理记录已损坏，请先修复后重新检查。当前库没有被替换。";
                case "IMPORT_SOURCE_CHANGED": case "IMPORT_TARGET_CHANGED": return "检查之后数据发生了变化，请重新生成清单并审阅。";
                case "IMPORT_PLAN_EXPIRED": return "清单已过期（15 分钟），请重新生成并审阅。";
                case "IMPORT_BUSY": case "LIBRARY_IMPORT_BUSY": return "另一个导入或撤销正在进行，请稍后再试。";
                case "IMPORT_NOT_FOUND": return "找不到这次导入的记录。";
                case "IMPORT_IO_FAILED": return "复制时读写失败，已保留备份，当前库没有被替换。";
                case "IMPORT_BACKUP_INVALID": case "IMPORT_ROLLBACK_FAILED": return "撤销或回滚没有完成。备份与导入记录都已保留，请暂停写入并检查恢复记录。";
                case "BACKEND_STOPPING": return "游戏库服务正在关闭，请稍后重连。";
                case "BACKEND_NOT_READY": return "游戏库服务未就绪。";
                default: return "导入没有完成（" + (string.IsNullOrEmpty(code) ? "未知原因" : code) + "）。当前库没有被替换。";
            }
        }

        public static string Stage(LibraryImportJob job)
        {
            if (job == null) return "";
            switch (job.State)
            {
                case "preparing": return job.TotalFiles > 0 ? "正在复制文件 " + job.CopiedFiles + " / " + job.TotalFiles : "正在准备…";
                case "backing_up": return "正在备份当前数据…";
                case "committing": return "正在校验并写入…";
                case "completed": return "导入完成";
                case "restored": return "已撤销这次导入";
                case "failed": return "导入失败";
                case "rollback_failed": return "回滚失败";
                default: return "正在导入…";
            }
        }

        public static string Size(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            return Math.Max(1, bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB";
        }
    }

    // Owns the running import job and its polling, independent of any dialog. Polling is one
    // async loop (never overlapping requests); events are raised on the UI context captured at
    // construction, whatever thread an awaited call resumes on.
    internal sealed class ImportRunner
    {
        private readonly LibraryImportClient client;
        private readonly System.Threading.SynchronizationContext ui;
        private int loop;
        private bool disposed;
        public LibraryImportJob Job { get; private set; }
        public string Error { get; private set; }
        public event EventHandler Changed;
        public event EventHandler Completed;
        internal int Polls;

        public ImportRunner(CocoonBackend backend, System.Threading.SynchronizationContext context)
        {
            client = new LibraryImportClient(backend);
            ui = context;
        }

        public LibraryImportClient Client { get { return client; } }
        public bool Running { get { return Job != null && !Job.Finished; } }

        private void OnUi(Action action)
        {
            if (ui == null || System.Threading.SynchronizationContext.Current == ui) action();
            else ui.Post(delegate { action(); }, null);
        }

        private void Raise() { OnUi(delegate { if (!disposed && Changed != null) Changed(this, EventArgs.Empty); }); }
        private void RaiseCompleted() { OnUi(delegate { if (!disposed && Completed != null) Completed(this, EventArgs.Empty); }); }

        public async Task Execute(LibraryImportPlan plan)
        {
            Error = null;
            try { Job = await client.ExecuteAsync(plan.PlanId, true); }
            catch (BackendRequestException failure) { Error = failure.Code; Raise(); return; }
            Raise();
            int mine = ++loop;
            Task.Run(() => Follow(mine));
        }

        // A timeout is not a cancellation of the job: keep asking until it finishes.
        private async Task Follow(int mine)
        {
            while (!disposed && mine == loop && Job != null && !Job.Finished)
            {
                await Task.Delay(400);
                if (disposed || mine != loop) return;
                Polls++;
                try { Job = await client.StatusAsync(Job.JobId); }
                catch (BackendRequestException failure)
                {
                    if (failure.Code == "LIBRARY_IMPORT_BUSY" || failure.Code == "BACKEND_STOPPING") continue;
                    Error = failure.Code; Raise(); return;
                }
                catch (Exception) { continue; } // transient transport problem
                Raise();
            }
            if (!disposed && mine == loop && Job != null && Job.Finished) RaiseCompleted();
        }

        public async Task<bool> Restore()
        {
            if (Job == null || !Job.CanRestore || string.IsNullOrEmpty(Job.ImportId)) return false;
            Error = null;
            try
            {
                await client.RestoreAsync(Job.ImportId, true);
                Job = await client.StatusAsync(Job.JobId);
            }
            catch (BackendRequestException failure) { Error = failure.Code; }
            Raise();
            if (Error == null) RaiseCompleted();
            return Error == null;
        }

        public void Dispose() { disposed = true; loop++; }
    }

    internal sealed class ImportDialog : ThemedDialog
    {
        private enum Step { Source, Planning, Review, Running, Done }
        private readonly ImportRunner runner;
        private Step step;
        private string source;
        private LibraryImportPlan plan;
        private string error;
        private readonly TextField path = new TextField();
        private readonly FlatButton browse;
        private readonly ToggleSwitch cache = new ToggleSwitch("包含已缓存的封面与素材", true), staging = new ToggleSwitch("包含尚未应用的暂存修改", true);
        private readonly ProgressLine progress = new ProgressLine { Value = -1 };
        private readonly ListBox files = new ListBox();
        private readonly FlatButton showFiles;
        private readonly FlatButton back, primary, secondary;
        // Device mode: the plan comes from a read of the handheld instead of a folder.
        private readonly DevicePullRunner pull;
        private readonly string serial;
        private bool offerRoot;
        internal string Describe { get { return step + " error=" + error + " root=" + offerRoot + " plan=" + (plan == null ? "-" : plan.TotalGames.ToString(CultureInfo.InvariantCulture)); } }

        public ImportDialog(ImportRunner owner) : this(owner, null, null) { }

        public ImportDialog(ImportRunner owner, DevicePullRunner device, string deviceSerial) : base(device == null ? "导入游戏库" : "从掌机读取游戏库", null, null, 600, 320)
        {
            runner = owner;
            pull = device;
            serial = deviceSerial;
            browse = new FlatButton(Glyphs.FolderOpen, "选择…") { Kind = ButtonKind.Subtle };
            browse.Click += delegate { Choose(); };
            path.Box.ReadOnly = true;
            showFiles = new FlatButton(null, "查看文件清单") { Kind = ButtonKind.Ghost };
            showFiles.Click += delegate { files.Visible = !files.Visible; showFiles.Text = files.Visible ? "收起文件清单" : "查看文件清单"; Arrange(); };
            files.BorderStyle = BorderStyle.None; files.BackColor = Ui.Card; files.ForeColor = Ui.TextDim; files.Font = Ui.Small; files.IntegralHeight = false; files.Visible = false;
            // Text and figures are painted on the body itself; no overlapping canvas control.
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(Body, true, null);
            Body.Paint += PaintCanvas;
            foreach (Control control in new Control[] { path, browse, cache, staging, progress, files, showFiles }) Body.Controls.Add(control);
            back = AddAction("返回", ButtonKind.Subtle, DialogResult.None);
            secondary = AddAction("取消", ButtonKind.Subtle, DialogResult.None);
            primary = AddAction("生成清单", ButtonKind.Primary, DialogResult.None);
            back.Click += delegate { Go(Step.Source); };
            secondary.Click += delegate { Secondary(); };
            primary.Click += delegate { Primary(); };
            runner.Changed += OnRunner;
            EventHandler pulled = delegate { if (!IsDisposed) Arrange(); };
            if (pull != null) pull.Changed += pulled;
            // Leaving while the handheld is still being read abandons that read; a submitted import keeps going.
            FormClosed += delegate { runner.Changed -= OnRunner; if (pull != null) { pull.Changed -= pulled; pull.Cancel(); } };
            Go(runner.Running ? Step.Running : runner.Job != null && pull == null ? Step.Done : Step.Source);
        }

        private void OnRunner(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            if (step == Step.Running && (runner.Job == null || runner.Job.Finished || runner.Error != null)) Go(Step.Done);
            else Arrange();
        }

        private void Choose()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "选择原 Cocoon Art 数据目录（包含 _data\\cocoon_db）", ShowNewFolderButton = false })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                source = dialog.SelectedPath;
                path.Text = source;
                error = File.Exists(Path.Combine(Path.Combine(source, "_data"), "cocoon_db")) ? null : "这个目录里没有 _data\\cocoon_db，请选择 Cocoon Art 的数据目录。";
                Arrange();
            }
        }

        private async void Primary()
        {
            if (step == Step.Source && pull != null)
            {
                // The explicit go-ahead for touching the device: one read, no writes.
                // Root is a separate, explicit choice: offered only after a plain read was refused, and
                // remembered so later reads do not ask again.
                bool root = offerRoot || Settings.Get("cocoon.rootRead", "0") == "1";
                if (offerRoot) Settings.Set("cocoon.rootRead", "1");
                error = null; plan = null; offerRoot = false; files.Items.Clear();
                Go(Step.Planning);
                await pull.Start(serial, root);
                if (IsDisposed) return;
                if (pull.Plan != null) { plan = pull.Plan; Go(Step.Review); }
                else
                {
                    bool denied = pull.Error == "DEVICE_READ_DENIED";
                    offerRoot = denied && !root;
                    error = PullText.Error(denied && root ? "DEVICE_ROOT_DENIED" : pull.Error);
                    Go(Step.Source);
                }
            }
            else if (step == Step.Source)
            {
                if (source == null || error != null) return;
                Go(Step.Planning);
                try { plan = await runner.Client.PlanAsync(source, cache.Checked, staging.Checked); error = null; Go(Step.Review); }
                catch (BackendRequestException failure) { error = ImportText.Error(failure.Code); Go(Step.Source); }
                catch (Exception) { error = ImportText.Error(null); Go(Step.Source); }
            }
            else if (step == Step.Review)
            {
                Go(Step.Running);
                await runner.Execute(plan);
                if (runner.Error != null) Go(Step.Done);
            }
            else if (step == Step.Done) Close();
        }

        private async void Secondary()
        {
            if (step == Step.Done && runner.Job != null && runner.Job.CanRestore && runner.Error == null)
            {
                // Undo is its own decision.
                using (ThemedDialog confirm = new ThemedDialog("撤销这次导入？", null, null, 440, 64))
                {
                    confirm.AddText("将恢复导入前的游戏库，导入之后在这里做的修改也会一并撤销。", Ui.Body, Ui.TextDim, 24, 8, 392, 44);
                    confirm.AddAction("保留", ButtonKind.Subtle, DialogResult.Cancel);
                    confirm.AddAction("撤销导入", ButtonKind.Danger, DialogResult.OK);
                    if (confirm.ShowDialog(this) != DialogResult.OK) return;
                }
                secondary.Enabled = false;
                await runner.Restore();
                secondary.Enabled = true;
                Arrange();
                return;
            }
            if (step == Step.Planning && pull != null) { pull.Cancel(); secondary.Enabled = false; return; } // Start() returns with the outcome
            Close(); // leaving never cancels a submitted import
        }

        private void Go(Step next)
        {
            step = next;
            Arrange();
        }

        private void Arrange()
        {
            bool source0 = step == Step.Source, device = pull != null;
            path.Visible = browse.Visible = cache.Visible = staging.Visible = source0 && !device;
            if (step != Step.Planning) secondary.Enabled = true;
            progress.Visible = step == Step.Planning || step == Step.Running;
            showFiles.Visible = step == Step.Review;
            if (step != Step.Review) files.Visible = false;
            back.Visible = step == Step.Review;
            primary.Visible = step != Step.Planning && step != Step.Running;
            primary.Text = source0 ? (device ? (offerRoot ? "用 root 权限读取" : error != null ? "重试" : "开始读取") : "生成清单") : step == Step.Review ? "确认导入" : "完成";
            primary.Enabled = !source0 || device || (source != null && error == null);
            bool canUndo = step == Step.Done && runner.Job != null && runner.Job.CanRestore && runner.Error == null;
            secondary.Text = step == Step.Running ? "在后台继续" : canUndo ? "撤销这次导入" : step == Step.Done ? "关闭" : "取消";
            secondary.Visible = !(step == Step.Done && !canUndo);
            if (step == Step.Running && runner.Job != null)
                progress.Value = runner.Job.State == "preparing" && runner.Job.TotalBytes > 0 ? runner.Job.CopiedBytes / (float)runner.Job.TotalBytes : -1;
            else progress.Value = -1;
            if (step == Step.Review && plan != null && files.Items.Count == 0)
                foreach (LibraryImportFile file in plan.Files) files.Items.Add(file.RelativePath + "   " + ImportText.Size(file.Bytes));
            // Layout (design pixels).
            path.SetBounds(Ui.S(24), Ui.S(40), Ui.S(420), path.Height);
            browse.SetBounds(Ui.S(454), Ui.S(40), Ui.S(122), Ui.S(36));
            cache.SetBounds(Ui.S(24), Ui.S(128), Ui.S(552), cache.Height);
            staging.SetBounds(Ui.S(24), Ui.S(172), Ui.S(552), staging.Height);
            progress.SetBounds(Ui.S(24), Ui.S(140), Ui.S(552), Ui.S(8));
            showFiles.Height = Ui.S(32); showFiles.Fit();
            showFiles.Location = new Point(Ui.S(24) - Ui.S(6), Ui.S(files.Visible ? 176 : 222));
            files.SetBounds(Ui.S(24), Ui.S(212), Ui.S(552), Ui.S(96));
            Body.Invalidate();
        }

        private void PaintCanvas(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Ui.S(24), width = Ui.S(552);
            Action<string, int, Font, Color> line = delegate(string text, int y, Font font, Color color)
            { Ui.Write(g, text, font, new Rectangle(pad, Ui.S(y), width, Ui.S(60)), color, TextFormatFlags.Left | TextFormatFlags.WordBreak); };
            switch (step)
            {
                case Step.Source:
                    if (pull != null)
                    {
                        line("读取 " + serial + " 上的 Cocoon 游戏库", 20, Ui.Strong, Ui.Text);
                        line("只读取游戏库数据库，读两遍并比对一致后生成清单，由你确认才会替换本机的副本。不会向掌机写入任何内容，也不会停止或重启掌机上的 Cocoon。", 54, Ui.Body, Ui.TextDim);
                        line("这一步只带回游戏资料；封面等素材文件留在掌机上，暂时不会一起读取。", 124, Ui.Small, Ui.TextDim);
                        if (error != null) Warn(g, pad, Ui.S(172), width, error, Ui.Danger);
                        if (offerRoot) line("掌机已 root 的话，可以用 root 权限执行同一条只读命令。仍然只读取、不写入；掌机上可能弹出授权提示。", 222, Ui.Small, Ui.TextDim);
                        else if (error == null && Settings.Get("cocoon.rootRead", "0") == "1") line("普通读取被拒绝时，会按你之前的选择改用 root 权限只读读取。", 172, Ui.Small, Ui.TextDim);
                        break;
                    }
                    line("原游戏库目录", 14, Ui.Small, Ui.TextDim);
                    line("原目录只读取、不修改。导入前会先备份当前数据，完成后可以撤销。", 88, Ui.Small, Ui.TextDim);
                    if (error != null) Warn(g, pad, Ui.S(222), width, error, Ui.Danger);
                    break;
                case Step.Planning:
                    line(pull == null ? "正在检查原游戏库并生成导入清单…" : pull.Phase == "checking" ? "正在再读一遍，确认数据一致…" : pull.Phase == "reading" ? "正在读取掌机上的游戏库…" : "正在连接掌机…", 104, Ui.Body, Ui.Text);
                    break;
                case Step.Review:
                    if (plan == null) break;
                    // Four figures across, then warnings.
                    string[] labels = { "游戏", "可见", "待处理修改", "文件" };
                    string[] values = { plan.TotalGames.ToString(CultureInfo.InvariantCulture), plan.VisibleGames.ToString(CultureInfo.InvariantCulture),
                        plan.PendingChanges.ToString(CultureInfo.InvariantCulture), plan.Files.Length + " · " + ImportText.Size(plan.TotalBytes) };
                    int cell = (width - Ui.S(30)) / 4;
                    for (int i = 0; i < 4; i++)
                    {
                        RectangleF tile = new RectangleF(pad + i * (cell + Ui.S(10)), Ui.S(12), cell, Ui.S(78));
                        Ui.Fill(g, tile, Ui.S(14), Ui.Card);
                        Ui.Write(g, values[i], i == 3 ? Ui.Strong : Ui.Title, new Rectangle((int)tile.X + Ui.S(14), (int)tile.Y + Ui.S(12), (int)tile.Width - Ui.S(20), Ui.S(30)), Ui.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                        Ui.Write(g, labels[i], Ui.Small, new Rectangle((int)tile.X + Ui.S(14), (int)tile.Y + Ui.S(46), (int)tile.Width - Ui.S(20), Ui.S(20)), Ui.TextDim, TextFormatFlags.Left);
                    }
                    int y = Ui.S(104);
                    if (plan.ReplacesExistingLibrary) { Warn(g, pad, y, width, "将替换当前的游戏库。替换前会备份，之后可以撤销。", Ui.Warn); y += Ui.S(34); }
                    foreach (string warning in plan.Warnings) { if (y > Ui.S(170)) break; Warn(g, pad, y, width, warning, Ui.Warn); y += Ui.S(30); }
                    if (!files.Visible) line(pull == null ? "确认后开始复制。原目录保持不变，关闭窗口不会中断导入。" : "确认后写入本机的游戏库副本。掌机保持不变，关闭窗口不会中断导入。", 270, Ui.Small, Ui.TextDim);
                    break;
                case Step.Running:
                    line(ImportText.Stage(runner.Job), 96, Ui.Heading, Ui.Text);
                    if (runner.Job != null && runner.Job.State == "preparing" && runner.Job.TotalBytes > 0)
                        line(ImportText.Size(runner.Job.CopiedBytes) + " / " + ImportText.Size(runner.Job.TotalBytes), 162, Ui.Small, Ui.TextDim);
                    line("复制完成后还会备份和校验，请稍候。", 190, Ui.Small, Ui.TextFaint);
                    break;
                case Step.Done:
                    bool ok = runner.Error == null && runner.Job != null && (runner.Job.State == "completed" || runner.Job.State == "restored");
                    line(ok ? ImportText.Stage(runner.Job) : "导入没有完成", 90, Ui.Heading, Ui.Text);
                    if (!ok) Warn(g, pad, Ui.S(134), width, ImportText.Error(runner.Error ?? (runner.Job == null ? null : runner.Job.Code)), Ui.Danger);
                    else if (runner.Job.State == "completed") line(pull == null ? "游戏库已更新。原目录没有改动；如有需要可以撤销这次导入。" : "游戏库已更新。掌机没有改动；如有需要可以撤销这次导入。", 132, Ui.Body, Ui.TextDim);
                    else line("已恢复到导入前的游戏库。", 132, Ui.Body, Ui.TextDim);
                    break;
            }
        }

        private static void Warn(Graphics g, int x, int y, int width, string text, Color tone)
        {
            using (SolidBrush dot = new SolidBrush(tone)) g.FillEllipse(dot, x, y + Ui.S(7), Ui.S(7), Ui.S(7));
            Ui.Write(g, text, Ui.Small, new Rectangle(x + Ui.S(16), y, width - Ui.S(16), Ui.S(40)), Ui.Text, TextFormatFlags.Left | TextFormatFlags.WordBreak);
        }
    }
}
