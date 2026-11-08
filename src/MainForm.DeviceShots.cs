using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThorCapture
{
    // The gallery's second source: the screenshots stored on the handheld. They are mirrored into a
    // local folder (see DeviceShotMirror) and the gallery simply shows that folder instead.
    internal partial class MainForm
    {
        private readonly Segmented sourcePicker = new Segmented("电脑", "掌机");
        private FlatButton shotsCopyButton;
        private ToolStripMenuItem shotsCopyItem;
        private bool deviceShots, shotsDeleting;
        private DeviceShotMirror shotMirror;
        private CancellationTokenSource shotSync;
        // Tests swap in a fake handheld.
        internal Func<string, IShotDevice> ShotDeviceFactory;
        internal Task ShotSyncTask = Task.FromResult(0);

        internal bool ShowingDeviceShots { get { return deviceShots; } }
        internal DeviceShotMirror ShotMirror { get { return shotMirror; } }

        private string GalleryFolder { get { return deviceShots && shotMirror != null ? shotMirror.Root : captureDir; } }

        private void BuildShotSource()
        {
            sourcePicker.Font = Ui.Small;
            sourcePicker.Height = Ui.S(30);
            sourcePicker.Fit();
            sideHead.Controls.Add(sourcePicker);
            tips.SetToolTip(sourcePicker, "电脑：本机图库\n掌机：Thor 上的截图（/sdcard/Pictures/Screenshots）");
            shotsCopyButton = MakeButton(sideHead, Glyphs.SaveAs, null, ButtonKind.Ghost, "把选中的掌机截图复制到电脑图库", delegate { CopyShotsToComputer(); });
            shotsCopyButton.Visible = false;
            sourcePicker.SelectedIndexChanged += delegate { SetShotSource(sourcePicker.SelectedIndex == 1, null); };
            shotsCopyItem = Menus.Add(itemMenu, "复制到电脑图库", null, delegate { CopyShotsToComputer(); });
            shotsCopyItem.Visible = false;
            FormClosing += delegate { CancelShotSync(); };
        }

        // Title row, right to left: folder (or refresh) button, copy button in handheld mode, source picker.
        private void LayoutShotSource(int pad)
        {
            int x = folderButton.Left;
            if (shotsCopyButton.Visible)
            {
                shotsCopyButton.Size = new Size(Ui.S(32), Ui.S(32));
                x -= Ui.S(4) + shotsCopyButton.Width;
                shotsCopyButton.Location = new Point(x, folderButton.Top);
            }
            x -= Ui.S(8) + sourcePicker.Width;
            sourcePicker.Location = new Point(x, folderButton.Top + (folderButton.Height - sourcePicker.Height) / 2);
        }

        private void SetShotSource(bool device, string select)
        {
            if (device == deviceShots) { if (device && select == null) StartShotSync(); return; }
            if (device)
            {
                string target = serial ?? Settings.Get("shots.serial", "");
                if (target.Length == 0)
                {
                    Notify("先连接 Thor 掌机，才能查看掌机上的截图", true);
                    sourcePicker.SelectedIndex = 0;
                    return;
                }
                try { shotMirror = new DeviceShotMirror(DeviceShotMirror.FolderFor(Adb.AppDir(), target)); }
                catch (Exception failure) { Notify("无法准备掌机截图目录：" + failure.Message, true); sourcePicker.SelectedIndex = 0; return; }
                deviceShots = true;
            }
            else
            {
                CancelShotSync();
                deviceShots = false;
            }
            if (sourcePicker.SelectedIndex != (device ? 1 : 0)) sourcePicker.SelectedIndex = device ? 1 : 0;
            shotsCopyButton.Visible = shotsCopyItem.Visible = device;
            folderButton.Glyph = device ? Glyphs.Refresh : Glyphs.FolderOpen;
            folderButton.Invalidate();
            tips.SetToolTip(folderButton, device ? "重新读取掌机上的截图" : "打开图库文件夹");
            sideHead.PerformLayout();
            sideHead.Invalidate();
            RefreshList(select);
            if (device)
            {
                if (serial != null) StartShotSync();
                else Say("掌机未连接，显示上次读取的截图", false);
            }
        }

        private void CancelShotSync()
        {
            if (shotSync == null) return;
            try { shotSync.Cancel(); } catch (ObjectDisposedException) { }
            shotSync = null;
        }

        private void StartShotSync()
        {
            if (!deviceShots || shotMirror == null) return;
            if (serial == null) { Notify("掌机未连接", true); return; }
            CancelShotSync();
            ShotSyncTask = SyncShots();
        }

        private async Task SyncShots()
        {
            CancellationTokenSource cancel = shotSync = new CancellationTokenSource();
            DeviceShotMirror mirror = shotMirror;
            IShotDevice device = ShotDeviceFactory != null ? ShotDeviceFactory(serial) : new AdbShotDevice(Adb.ExePath, serial);
            Settings.Set("shots.serial", serial);
            DateTime lastRefresh = DateTime.UtcNow;
            Say("正在读取掌机上的截图…", false);
            Progress<ShotSyncProgress> progress = new Progress<ShotSyncProgress>(delegate(ShotSyncProgress p)
            {
                if (cancel.IsCancellationRequested || IsDisposed || mirror != shotMirror) return;
                if (p.Total > 0 && p.Done < p.Total) Say("正在读取掌机截图 " + p.Done + " / " + p.Total + "…", false);
                // New pictures appear while the rest are still coming in.
                if (p.Changed && (DateTime.UtcNow - lastRefresh).TotalMilliseconds > 900) { lastRefresh = DateTime.UtcNow; RefreshList(null); }
            });
            string failure = null;
            bool cancelled = false;
            try { await Task.Run(() => mirror.SyncAsync(device, progress, cancel.Token)); }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception error) { failure = DeviceShotException.Describe(error); }
            finally
            {
                IDisposable owned = device as IDisposable;
                if (owned != null) owned.Dispose();
            }
            if (shotSync == cancel) shotSync = null;
            cancel.Dispose();
            if (IsDisposed || cancelled || !deviceShots || mirror != shotMirror) return;
            RefreshList(null);
            if (failure != null) Notify("读取掌机截图失败：" + failure, true);
            else Notify(mirror.Count == 0 ? "掌机上没有截图" : "掌机上共有 " + mirror.Count + " 张截图", false);
        }

        // Copies the selected handheld pictures (with their paired screen) into the computer gallery.
        private void CopyShotsToComputer()
        {
            if (!deviceShots) return;
            List<string> files = new List<string>();
            foreach (GalleryItem item in gallery.SelectedItems)
            {
                if (!files.Contains(item.Path)) files.Add(item.Path);
                string peer = PeerOf(item.Path);
                if (peer != null && File.Exists(peer) && !files.Contains(peer)) files.Add(peer);
            }
            if (files.Count == 0) { Notify("先选择要复制的截图", true); return; }
            int copied = 0, existed = 0;
            try
            {
                Directory.CreateDirectory(captureDir);
                foreach (string file in files)
                {
                    string target = Path.Combine(captureDir, Path.GetFileName(file));
                    if (File.Exists(target)) { existed++; continue; }
                    File.Copy(file, target);
                    File.SetLastWriteTime(target, File.GetLastWriteTime(file));
                    copied++;
                }
                Notify("已复制 " + copied + " 张到电脑图库" + (existed > 0 ? "（" + existed + " 张已存在，未覆盖）" : ""), false);
            }
            catch (Exception failure) { Notify("复制失败：" + failure.Message, true); }
        }

        // Deleting in handheld mode removes the originals on the handheld, after a confirmation.
        private async Task DeleteDeviceShots(List<string> paths)
        {
            if (shotsDeleting || shotMirror == null) return;
            if (serial == null) { Notify("掌机未连接，无法删除掌机上的截图", true); return; }
            DeviceShotMirror mirror = shotMirror;
            List<string> remotes = new List<string>();
            bool split = false;
            foreach (string path in paths)
            {
                string remote = mirror.RemoteFor(path);
                if (remote == null || remotes.Contains(remote)) continue;
                remotes.Add(remote);
                if (mirror.LocalsFor(remote).Count > 1) split = true;
            }
            if (remotes.Count == 0) return;
            string message = "从掌机上永久删除 " + remotes.Count + " 张截图？\n\n掌机上的原图会被删除，不能撤销。已复制到电脑图库的副本不受影响。"
                + (split ? "\n\n上下屏拼在同一张图里的截图，上屏和下屏会一起删除。" : "");
            if (MessageBox.Show(this, message, "删除掌机截图", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            shotsDeleting = true;
            CancelShotSync();
            await StopPlayback();
            int next = gallery.IndexOf(paths[0]);
            IShotDevice device = ShotDeviceFactory != null ? ShotDeviceFactory(serial) : new AdbShotDevice(Adb.ExePath, serial);
            Say("正在删除掌机上的截图…", false);
            string failure = null;
            List<string> removed = new List<string>(remotes);
            try
            {
                await Task.Run(async delegate
                {
                    await device.DeleteAsync(remotes, CancellationToken.None);
                    // Only forget what is really gone.
                    HashSet<string> left = new HashSet<string>(StringComparer.Ordinal);
                    foreach (RemoteShot shot in await device.ListAsync(CancellationToken.None)) left.Add(shot.Name);
                    removed.RemoveAll(left.Contains);
                    if (removed.Count < remotes.Count) throw new DeviceShotException("SHOTS_DELETE_INCOMPLETE");
                });
            }
            catch (Exception error) { failure = DeviceShotException.Describe(error); }
            finally
            {
                IDisposable owned = device as IDisposable;
                if (owned != null) owned.Dispose();
                shotsDeleting = false;
            }
            if (IsDisposed || mirror != shotMirror) return;
            if (removed.Count > 0) mirror.Forget(removed);
            string neighbour = null;
            List<GalleryItem> items = gallery.Items;
            for (int i = Math.Max(0, next); i < items.Count && neighbour == null; i++) if (File.Exists(items[i].Path) && mirror.RemoteFor(items[i].Path) != null) neighbour = items[i].Path;
            preview.SetImage(null);
            RefreshList(neighbour);
            if (failure != null) Notify((removed.Count > 0 ? "已删除 " + removed.Count + " 张，" : "") + failure, true);
            else Notify("已从掌机删除 " + removed.Count + " 张截图", false);
        }
    }
}
