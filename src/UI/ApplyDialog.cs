// "Apply to the handheld": review exactly what will be written, confirm, then follow the run.
// While it runs Cocoon is switched off on the device for a few seconds, so the dialog cannot be
// closed mid-run; if a run could not be undone, the same dialog offers to recover the device.

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
    internal static class ApplyText
    {
        public static string Error(string code)
        {
            switch (code ?? "")
            {
                case "PATCH_CONFLICT": return "掌机上的资料和你审阅时看到的不一样（可能在掌机上改过，或本机的游戏库不是最新）。没有写入任何内容。请重新从掌机读取，再做修改。";
                case "DEVICE_ROOT_REQUIRED": return "没有取得 root 权限。请在掌机上允许 Shell 获取超级用户权限后重试。没有写入任何内容。";
                case "DEVICE_APP_DISABLED": return "掌机上的 Cocoon 目前处于停用状态，为避免误操作没有继续。请先在掌机上确认 Cocoon 能正常打开。";
                case "DEVICE_CATALOG_MISSING": return "在掌机上找不到 Cocoon 的游戏库文件。没有写入任何内容。";
                case "DEVICE_APP_STILL_RUNNING": return "Cocoon 没有停下来，为安全起见没有写入。已重新启用 Cocoon。";
                case "DEVICE_TIMEOUT": case "DEVICE_LINK_LOST": case "DEVICE_COMMAND_FAILED": case "DEVICE_PUSH_FAILED": case "DEVICE_ADB_UNAVAILABLE":
                    return "和掌机的连接出了问题，这次没有应用。掌机上的游戏库已保持或恢复为原样，Cocoon 已重新启用。";
                case "DEVICE_BACKUP_FAILED": case "DEVICE_READ_MISMATCH": return "读取或备份掌机上的游戏库时校验不一致，没有写入。Cocoon 已重新启用。";
                case "DEVICE_INSTALL_MISMATCH": return "写入后的校验不符，已把掌机上的游戏库恢复为原来的内容，Cocoon 已重新启用。";
                case "PATCH_TOOL_MISSING": return "找不到游戏库组件里的修改工具（tools\\snapshot-patch.cjs）或 Node.js。没有写入任何内容。";
                case "APPLY_CANCELLED": return "已取消。掌机上的游戏库没有变化。";
                case "APPLY_RECOVERY_REQUIRED": return "这次应用被中断，而且没能自动恢复：掌机上的 Cocoon 可能仍处于停用状态。请保持连接，点「恢复掌机」。";
                case "APPLY_WRONG_DEVICE": return "当前连接的不是上次被中断的那台掌机。请连接原来那台再恢复。";
                case "DEVICE_BACKUP_MISSING": return "掌机上的备份和记录对不上，无法自动恢复。电脑上保留了这次读取的备份，请不要继续操作并反馈这个情况。";
                case "DEVICE_RESTORE_FAILED": case "DEVICE_ENABLE_FAILED": return "恢复没有完成。请保持连接后再试一次「恢复掌机」。";
                case "APPLY_ARTWORK_UNSUPPORTED": return "这一版只能把资料修改应用到掌机，封面等素材还不行。请先在「暂存的修改」里撤销素材修改。";
                case "DEVICE_NOT_CONNECTED": return "没有连接掌机。请用 USB 连接并在掌机上允许调试。";
                case "DEVICE_BUSY": return "正在截图或录屏，结束后再应用。";
                case "EDITOR_REVIEW_EMPTY": return "暂存的修改和游戏库里的内容相同，没有需要应用的改动。";
                default:
                    if ((code ?? "").StartsWith("PATCH_", StringComparison.Ordinal)) return "修改掌机游戏库副本时被拒绝（" + code + "）。没有写入任何内容，Cocoon 已重新启用。";
                    if ((code ?? "").StartsWith("EDITOR_", StringComparison.Ordinal) || (code ?? "").StartsWith("BACKEND_", StringComparison.Ordinal)) return EditText.Error(code);
                    return "没有应用（" + (string.IsNullOrEmpty(code) ? "未知原因" : code) + "）。";
            }
        }

        public static string Stage(string stage)
        {
            switch (stage)
            {
                case "inspect": return "正在检查掌机…";
                case "freeze": return "正在暂时关闭 Cocoon…";
                case "read": return "正在读取并备份掌机上的游戏库…";
                case "patch": return "正在写入你审阅过的修改…";
                case "push": return "正在传回掌机…";
                case "install": return "正在替换掌机上的游戏库并校验…";
                case "thaw": return "正在重新启用 Cocoon…";
                case "media": return "正在把新图片和视频传到掌机…";
                case "restore": return "出了问题，正在恢复原来的游戏库…";
                case "refresh": return "正在更新本机的游戏库副本…";
                default: return "正在处理…";
            }
        }
    }

    internal sealed class ApplyDialog : ThemedDialog
    {
        private enum Step { Loading, Migration, Review, Running, Done, Failed, Recovery }
        private readonly GameEditSession session;
        private readonly DeviceApplier applier;
        private readonly Func<int, GameEntry> gameOf;
        private readonly Func<DeviceApplyResult, Task<string>> afterApply; // refreshes the local library; returns an error code or null
        private readonly ReviewList list = new ReviewList();
        private readonly ProgressLine progress = new ProgressLine { Value = -1 };
        private readonly FlatButton primary, secondary;
        private Step step = Step.Loading;
        private string error, stage, notice;
        private NativeChangeReview review;
        private NativeMigrationPlan migration;
        private List<DeviceFieldChange> changes;
        private DeviceApplyResult result;
        private bool busy;
        internal string Describe { get { return step + " error=" + error + " rows=" + list.Count + " stage=" + stage + " notice=" + notice; } }
        // True once the device was changed, so the page knows to reload.
        public bool Applied { get { return result != null; } }

        public ApplyDialog(GameEditSession owner, DeviceApplier deviceApplier, Func<int, GameEntry> lookup, Func<DeviceApplyResult, Task<string>> refresh)
            : base("应用到掌机", null, null, 600, 400)
        {
            session = owner; applier = deviceApplier; gameOf = lookup; afterApply = refresh;
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(Body, true, null);
            Body.Paint += PaintCanvas;
            Body.Controls.Add(list); Body.Controls.Add(progress);
            list.SetBounds(Ui.S(24), Ui.S(44), Ui.S(552), Ui.S(268));
            progress.SetBounds(Ui.S(24), Ui.S(196), Ui.S(552), Ui.S(8));
            secondary = AddAction("取消", ButtonKind.Subtle, DialogResult.None);
            primary = AddAction("应用到掌机", ButtonKind.Primary, DialogResult.None);
            secondary.Click += delegate { if (!busy) Close(); };
            primary.Click += delegate { Primary(); };
            applier.Progress += OnStage;
            FormClosed += delegate { applier.Progress -= OnStage; };
            // An unfinished run comes first: nothing else may touch the device until it is resolved.
            bool interrupted;
            try { interrupted = applier.Interrupted() != null; } catch (DeviceApplyException) { interrupted = true; }
            if (interrupted) { error = ApplyText.Error("APPLY_RECOVERY_REQUIRED"); Go(Step.Recovery); }
            else { Arrange(); Shown += async delegate { await LoadReview(); }; }
        }

        private void OnStage(string name)
        {
            // Raised on a worker thread.
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) { stage = name; Body.Invalidate(); } }); } catch (InvalidOperationException) { }
        }

        // The run cannot be abandoned half-way from here: closing waits until it has finished.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy && step == Step.Running) e.Cancel = true;
            base.OnFormClosing(e);
        }

        private async Task LoadReview()
        {
            error = null;
            Go(Step.Loading);
            string code = null;
            try
            {
                NativeEditorClient editor = await session.Editor();
                review = await editor.PreviewChangesAsync(session.Revision, session.Token);
                if (IsDisposed) return;
                changes = Changes(review);
                list.SetRows(Rows(changes));
                Go(Step.Review);
                return;
            }
            catch (Exception failure) { code = failure is DeviceApplyException ? ((DeviceApplyException)failure).Code : EditText.Code(failure); }
            if (IsDisposed) return;
            if (code == "EDITOR_MIGRATION_REQUIRED") { Go(Step.Migration); return; }
            if (code == "EDITOR_REVISION_CONFLICT") await session.Refresh();
            if (IsDisposed) return;
            error = ApplyText.Error(code);
            Go(Step.Failed);
        }

        // Catalog columns behind each artwork slot.
        private static readonly Dictionary<string, string[]> SlotColumns = new Dictionary<string, string[]>
        { { "icon", new[] { "boxArtLocal", "boxArtSource" } }, { "logo", new[] { "logoLocal", "logoSource" } }, { "hero", new[] { "heroLocal", "heroSource" } } };

        // The reviewed change set as catalog changes, one per game: metadata fields as they are, artwork as
        // a file to put in Cocoon's media folder plus the new reference to it.
        private List<DeviceFieldChange> Changes(NativeChangeReview plan)
        {
            Dictionary<long, DeviceFieldChange> byGame = new Dictionary<long, DeviceFieldChange>();
            List<DeviceFieldChange> output = new List<DeviceFieldChange>();
            List<object> rows = Json.Get(plan.Data, "changes") as List<object>;
            if (rows == null) throw new DeviceApplyException("APPLY_REQUEST_INVALID");
            string staging = Path.Combine(session.Backend.DataRoot, "_staging");
            foreach (object row in rows)
            {
                long id = (long)Json.Number(row, "nativeGameId", 0);
                DeviceFieldChange change;
                if (!byGame.TryGetValue(id, out change))
                {
                    GameEntry game = gameOf((int)id);
                    change = new DeviceFieldChange { GameId = id, FileName = game == null ? null : game.FileName, PlatformId = game == null ? null : game.Platform };
                    byGame[id] = change; output.Add(change);
                }
                if (Json.Text(row, "kind") == "metadata")
                {
                    Dictionary<string, object> before = Json.Get(row, "before") as Dictionary<string, object>, after = Json.Get(row, "after") as Dictionary<string, object>;
                    if (before == null || after == null) throw new DeviceApplyException("APPLY_REQUEST_INVALID");
                    foreach (KeyValuePair<string, object> pair in after) { change.After[pair.Key] = pair.Value; change.Before[pair.Key] = before[pair.Key]; }
                    continue;
                }
                string slot = Json.Text(row, "slot");
                string[] columns;
                object asset = Json.Get(row, "after"), destination = Json.Get(asset, "destination");
                if (slot == null || !SlotColumns.TryGetValue(slot, out columns) || Json.Text(row, "action") != "set") throw new DeviceApplyException("APPLY_REQUEST_INVALID");
                string remote = CocoonMedia.Root + Json.Text(destination, "platform") + "/" + slot + "/" + Json.Text(destination, "fileName");
                string stagedRef = Json.Text(asset, "stagedRef") ?? "";
                string local = Path.GetFullPath(Path.Combine(staging, stagedRef.Replace('/', Path.DirectorySeparatorChar)));
                if (!CocoonMedia.IsMediaPath(remote) || !local.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new DeviceApplyException("APPLY_MEDIA_INVALID");
                // The binding is guarded by the reference the review saw; the source marker just becomes "user".
                object old = Json.Get(row, "before");
                change.Before[columns[0]] = old == null ? null : Json.Text(old, "uri");
                change.After[columns[0]] = CocoonMedia.UriFor(remote);
                change.After[columns[1]] = "user"; change.Unguarded.Add(columns[1]);
                change.Media.Add(new DeviceMediaFile { LocalPath = local, RemotePath = remote, Sha256 = Json.Text(asset, "sha256") });
                artwork[id + ":" + slot] = Json.Text(asset, "mime") + "|" + Json.Number(asset, "width", 0) + "|" + Json.Number(asset, "height", 0) + "|" + (old == null ? "" : "had");
            }
            return output;
        }

        // Shown text for artwork rows: "game:slot" -> "mime|width|height|had-before".
        private readonly Dictionary<string, string> artwork = new Dictionary<string, string>();

        private List<ReviewList.Row> Rows(List<DeviceFieldChange> items)
        {
            List<ReviewList.Row> rows = new List<ReviewList.Row>();
            foreach (DeviceFieldChange change in items)
            {
                GameEntry game = gameOf((int)change.GameId);
                string name = game == null ? "游戏 #" + change.GameId : game.Name;
                foreach (string field in EditText.Fields)
                    if (change.After.ContainsKey(field)) rows.Add(Row(name, field, change));
                foreach (KeyValuePair<string, string[]> slot in SlotColumns)
                {
                    string info;
                    if (!artwork.TryGetValue(change.GameId + ":" + slot.Key, out info)) continue;
                    string[] parts = info.Split('|');
                    bool video = parts[0].StartsWith("video/", StringComparison.Ordinal);
                    rows.Add(new ReviewList.Row { Game = name, Field = EditText.SlotName(slot.Key == "icon" ? ArtworkSlot.Icon : slot.Key == "logo" ? ArtworkSlot.Logo : ArtworkSlot.Hero),
                        Before = parts[3] == "had" ? "原来的" + (slot.Key == "hero" ? "背景" : "图片") : "（空）",
                        After = (video ? "新视频" : "新图片") + " · " + parts[1] + "×" + parts[2] + " " + (parts[0].Substring(parts[0].IndexOf('/') + 1).ToUpperInvariant()) });
                }
            }
            return rows;
        }

        private static ReviewList.Row Row(string game, string field, DeviceFieldChange change)
        {
            // The catalog stores switches as 0/1; show them the way the editor does.
            Func<object, object> shown = delegate(object value) { return (field == "isFavorite" || field == "isHidden" || field == "logoPlacementOverridden") && value is double ? (object)((double)value != 0) : value; };
            return new ReviewList.Row { Game = game, Field = EditText.Label(field), Before = EditText.Value(field, shown(change.Before[field])), After = EditText.Value(field, shown(change.After[field])) };
        }

        private async void Primary()
        {
            if (busy) return;
            if (step == Step.Migration) await Migrate();
            else if (step == Step.Review) await Apply();
            else if (step == Step.Recovery) await Recover();
            else if (step == Step.Failed && review == null) await LoadReview();
            else Close();
        }

        // Upgrading the local extension database is its own reviewed step in the backend; here it is the
        // explicit button press before the first review.
        private async Task Migrate()
        {
            busy = true; error = null; Arrange();
            string code = null;
            try
            {
                NativeEditorClient editor = await session.Editor();
                migration = await editor.MigrationPlanAsync(session.Token);
                await editor.ConfirmMigrationAsync(migration, true, session.Token);
            }
            catch (Exception failure) { code = EditText.Code(failure); }
            busy = false;
            if (IsDisposed) return;
            migration = null;
            if (code != null) { error = ApplyText.Error(code); Go(Step.Failed); return; }
            await LoadReview();
        }

        private async Task Apply()
        {
            busy = true; error = null; stage = "inspect";
            Go(Step.Running);
            string code = null;
            DeviceApplyResult applied = null;
            try
            {
                // Record exactly what was reviewed; a stale review must be looked at again, never re-confirmed silently.
                NativeEditorClient editor = await session.Editor();
                await editor.ConfirmChangesAsync(review, true, session.Token);
                List<DeviceFieldChange> reviewed = changes;
                applied = await Task.Run(() => applier.ApplyAsync(reviewed, CancellationToken.None));
            }
            catch (Exception failure) { code = failure is DeviceApplyException ? ((DeviceApplyException)failure).Code : EditText.Code(failure); }
            if (applied != null)
            {
                result = applied;
                stage = "refresh"; Body.Invalidate();
                string refresh = null;
                try { refresh = await afterApply(applied); } catch (Exception) { refresh = "REFRESH_FAILED"; }
                if (refresh != null) notice = "掌机已经更新，但本机的游戏库副本没有刷新成功。请重新从掌机读取。";
            }
            busy = false;
            if (IsDisposed) return;
            if (applied != null) { Go(Step.Done); return; }
            error = ApplyText.Error(code);
            if (code == "EDITOR_REVIEW_STALE" || code == "EDITOR_REVIEW_EXPIRED" || code == "EDITOR_REVISION_CONFLICT") { review = null; error = "草稿或游戏库在审阅之后有变化，请重新审阅。"; }
            Go(code == "APPLY_RECOVERY_REQUIRED" ? Step.Recovery : Step.Failed);
        }

        private async Task Recover()
        {
            busy = true; error = null; stage = "inspect";
            Step shown = step; step = Step.Running; Arrange();
            string code = null, outcome = null;
            try { outcome = await Task.Run(() => applier.RecoverAsync(CancellationToken.None)); }
            catch (Exception failure) { code = failure is DeviceApplyException ? ((DeviceApplyException)failure).Code : "APPLY_FAILED"; }
            busy = false;
            if (IsDisposed) return;
            if (code != null) { error = ApplyText.Error(code); Go(shown); return; }
            notice = outcome == "applied" ? "上次的修改其实已经写入掌机，Cocoon 已重新启用。请重新从掌机读取，让本机副本和掌机一致。"
                : outcome == "restored" ? "已把掌机上的游戏库恢复成应用之前的内容，Cocoon 已重新启用。" : "掌机上的游戏库没有被改动，Cocoon 已重新启用。";
            Go(Step.Done);
        }

        private void Go(Step next) { step = next; Arrange(); }

        private void Arrange()
        {
            list.Visible = step == Step.Review;
            progress.Visible = step == Step.Loading || step == Step.Running || (busy && step == Step.Migration);
            primary.Visible = step != Step.Loading && step != Step.Running;
            primary.Enabled = !busy;
            primary.Text = step == Step.Review ? "应用到掌机" : step == Step.Migration ? "升级并继续" : step == Step.Recovery ? "恢复掌机"
                : step == Step.Failed && review == null ? "重新审阅" : "完成";
            secondary.Visible = step == Step.Review || step == Step.Migration || step == Step.Failed || step == Step.Loading;
            secondary.Text = step == Step.Failed ? "关闭" : "取消";
            secondary.Enabled = !busy;
            if (primary.Parent != null) primary.Parent.PerformLayout();
            Body.Invalidate();
        }

        private void PaintCanvas(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Ui.S(24), width = Ui.S(552);
            Action<string, int, Font, Color, int> line = delegate(string text, int y, Font font, Color color, int height)
            { Ui.Write(g, text, font, new Rectangle(pad, Ui.S(y), width, Ui.S(height)), color, TextFormatFlags.Left | TextFormatFlags.WordBreak); };
            switch (step)
            {
                case Step.Loading:
                    line("正在整理要应用的修改…", 150, Ui.Body, Ui.Text, 30);
                    break;
                case Step.Migration:
                    line("需要先升级本机的扩展数据", 80, Ui.Heading, Ui.Text, 34);
                    line("审阅和记录变更要用到新版的本机数据结构。升级只改这台电脑上的扩展数据库，会先备份，掌机和导入的游戏库都不受影响。", 124, Ui.Body, Ui.TextDim, 70);
                    break;
                case Step.Review:
                    line("将写入掌机的 " + list.Count + " 处修改", 12, Ui.Strong, Ui.Text, 24);
                    Note(g, pad, Ui.S(322), width, "应用时掌机上的 Cocoon 会暂时关闭十几秒，桌面会短暂消失，完成后自动恢复。期间请不要拔线或操作掌机。", Ui.Warn);
                    Note(g, pad, Ui.S(362), width, "写入前会在掌机和电脑上各留一份原游戏库的备份；任何一步校验不符都会恢复原样。", Ui.TextFaint);
                    break;
                case Step.Running:
                    line(ApplyText.Stage(stage), 140, Ui.Heading, Ui.Text, 34);
                    line("请保持掌机连接。这个窗口在完成前不能关闭。", 220, Ui.Small, Ui.TextDim, 24);
                    break;
                case Step.Done:
                    line(result != null ? "已应用到掌机" : "掌机已恢复", 100, Ui.Heading, Ui.Text, 34);
                    if (result != null) line("写入了 " + result.FieldsWritten + " 处修改，游戏库共 " + result.Games + " 款。Cocoon 已重新启用。", 144, Ui.Body, Ui.TextDim, 30);
                    if (notice != null) Note(g, pad, Ui.S(result != null ? 190 : 144), width, notice, result != null ? Ui.Warn : Ui.TextFaint);
                    else if (result != null) Note(g, pad, Ui.S(190), width, "原游戏库的备份保存在电脑上，掌机上也留了一份，下次应用时会被新的备份替换。", Ui.TextFaint);
                    break;
                case Step.Failed:
                    line("没有应用", 100, Ui.Heading, Ui.Text, 34);
                    if (error != null) Note(g, pad, Ui.S(146), width, error, Ui.Danger);
                    break;
                case Step.Recovery:
                    line("需要恢复掌机", 90, Ui.Heading, Ui.Text, 34);
                    if (error != null) Note(g, pad, Ui.S(136), width, error, Ui.Danger);
                    Note(g, pad, Ui.S(220), width, "恢复会检查掌机上的游戏库：已经写好的保留，写到一半的用备份还原，然后重新启用 Cocoon。", Ui.TextFaint);
                    break;
            }
        }

        private static void Note(Graphics g, int x, int y, int width, string text, Color tone)
        {
            using (SolidBrush dot = new SolidBrush(tone)) g.FillEllipse(dot, x, y + Ui.S(6), Ui.S(6), Ui.S(6));
            Ui.Write(g, text, Ui.Small, new Rectangle(x + Ui.S(14), y, width - Ui.S(14), Ui.S(70)), tone == Ui.TextFaint ? Ui.TextDim : Ui.Text, TextFormatFlags.Left | TextFormatFlags.WordBreak);
        }
    }

    // Read-only list of what will be written: game and field, the device's current value struck through, the new value.
    internal sealed class ReviewList : Control
    {
        public sealed class Row { public string Game, Field, Before, After; }
        private List<Row> rows = new List<Row>();
        private float scroll;
        public int Count { get { return rows.Count; } }
        internal Row this[int index] { get { return rows[index]; } }

        public ReviewList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }

        public void SetRows(List<Row> list) { rows = list; scroll = 0; Invalidate(); }
        private int RowHeight { get { return Ui.S(58); } }
        protected override void OnMouseEnter(EventArgs e) { Focus(); base.OnMouseEnter(e); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll = Math.Max(0, Math.Min(Math.Max(0, rows.Count * RowHeight - Height), scroll - e.Delta * Ui.S(60) / 120f));
            Invalidate();
            base.OnMouseWheel(e);
        }

        private static string Short(string text) { text = (text ?? "").Replace("\r", " ").Replace("\n", " "); return text.Length > 160 ? text.Substring(0, 160) + "…" : text; }

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
                Ui.Fill(g, new RectangleF(0, y + Ui.S(3), Width - 1, h - Ui.S(6)), Ui.S(12), Ui.Card);
                int x = Ui.S(16), labelWidth = Ui.S(190);
                Ui.Write(g, row.Game, Ui.Strong, new Rectangle(x, y + Ui.S(9), labelWidth, Ui.S(22)), Ui.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                Ui.Write(g, row.Field, Ui.Small, new Rectangle(x, y + Ui.S(31), labelWidth, Ui.S(18)), Ui.TextDim, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                int vx = x + labelWidth + Ui.S(10), vw = Width - vx - Ui.S(14);
                string before = Short(row.Before);
                Ui.Write(g, before, Ui.Small, new Rectangle(vx, y + Ui.S(9), vw, Ui.S(18)), Ui.TextFaint, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                int strike = Math.Min(vw, Ui.Measure(before, Ui.Small));
                using (Pen pen = new Pen(Ui.TextFaint, 1f)) g.DrawLine(pen, vx, y + Ui.S(18), vx + strike, y + Ui.S(18));
                Ui.Write(g, Short(row.After), Ui.Body, new Rectangle(vx, y + Ui.S(28), vw, Ui.S(22)), Ui.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }
    }
}
