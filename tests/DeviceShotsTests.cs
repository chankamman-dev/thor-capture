using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThorCapture
{
    // Handheld screenshots: listing, mirroring (with stacked pictures split into pairs), forgetting
    // deleted pictures, and the gallery's 电脑 / 掌机 switch.
    internal static class DeviceShotsTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private sealed class FakeShots : IShotDevice
        {
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
            public readonly Dictionary<string, long> Times = new Dictionary<string, long>();
            public int Reads;
            public int FailAfter = -1;
            public List<string> Deleted = new List<string>();
            public string Serial { get { return "fake-shots"; } }

            public void Add(string name, byte[] bytes, long mtime) { Files[name] = bytes; Times[name] = mtime; }

            public Task<List<RemoteShot>> ListAsync(CancellationToken token)
            {
                List<RemoteShot> list = new List<RemoteShot>();
                foreach (KeyValuePair<string, byte[]> file in Files) list.Add(new RemoteShot { Name = file.Key, Size = file.Value.Length, Mtime = Times[file.Key] });
                return Task.FromResult(list);
            }

            public Task<byte[]> ReadAsync(RemoteShot shot, CancellationToken token)
            {
                if (FailAfter >= 0 && Reads >= FailAfter) throw new DeviceShotException("SHOTS_LINK_LOST");
                Reads++;
                return Task.FromResult(Files[shot.Name]);
            }

            public Task DeleteAsync(IList<string> names, CancellationToken token)
            {
                foreach (string name in names) { Files.Remove(name); Deleted.Add(name); }
                return Task.FromResult(0);
            }
        }

        private static byte[] Png(int width, int height, Action<Graphics> paint, ImageFormat format)
        {
            using (Bitmap bitmap = new Bitmap(width, height))
            {
                using (Graphics g = Graphics.FromImage(bitmap)) paint(g);
                using (MemoryStream memory = new MemoryStream()) { bitmap.Save(memory, format); return memory.ToArray(); }
            }
        }

        private static byte[] Stacked()
        {
            return Png(1920, 2160, delegate(Graphics g)
            {
                g.Clear(Color.Black);
                g.FillRectangle(Brushes.Red, 0, 0, 1920, 1080);
                g.FillRectangle(Brushes.Blue, 340, 1080, 1240, 1080);
            }, ImageFormat.Png);
        }

        private static bool Pump(Func<bool> done, int ms)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!done()) { if (clock.ElapsedMilliseconds > ms) return false; Application.DoEvents(); Thread.Sleep(10); }
            return true;
        }

        internal static void Run()
        {
            // Listing: only plain picture names are taken; anything odd is skipped rather than quoted.
            List<RemoteShot> parsed = AdbShotDevice.Parse("12:1700000000:regular file:Screenshot_20261024-130533.png\n3:1:directory:Old.png\n5:1:notes.txt\n9:2:bad name.png\n7:3:x;rm.png\n44:1794055692:Thor_20261107_204811_dual.png\r\nTHOR junk\n");
            Tests.Check(parsed.Count == 2 && parsed[0].Size == 12 && parsed[1].Mtime == 1794055692 && parsed[1].Name == "Thor_20261107_204811_dual.png",
                "the listing keeps plain picture names with size and time and skips everything else");
            Tests.Check(DeviceShotMirror.TimeOf(new RemoteShot { Name = "Screenshot_20261024-130533.png" }) == new DateTime(2026, 10, 24, 13, 5, 33)
                && DeviceShotMirror.TimeOf(new RemoteShot { Name = "Thor_20261107_204811_dual.png" }) == new DateTime(2026, 11, 7, 20, 48, 11)
                && DeviceShotMirror.TimeOf(new RemoteShot { Name = "photo.png", Mtime = 0 }) == new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime(),
                "the shot time comes from the handheld's file name, else from the file time");
            Tests.Reject(() => new AdbShotDevice("adb", "0123abcd", "/sdcard/Pictures/../x"), "the screenshot folder cannot climb out with ..");

            string root = Path.Combine(Path.GetTempPath(), "thor-device-shots-" + Guid.NewGuid().ToString("N"));
            try
            {
                FakeShots device = new FakeShots();
                device.Add("Thor_20261107_204811_dual.png", Stacked(), 1794055692);
                device.Add("Thor_20261031_134428_top.png", Png(1920, 1080, g => g.Clear(Color.Green), ImageFormat.Png), 1793425469);
                device.Add("Screenshot_20261024-130533.png", Png(1240, 1080, g => g.Clear(Color.Yellow), ImageFormat.Png), 1792818333);
                device.Add("Screenshot_20261020-073241.jpg", Png(800, 600, g => g.Clear(Color.White), ImageFormat.Jpeg), 1792400000);

                DeviceShotMirror mirror = new DeviceShotMirror(root);
                List<ShotSyncProgress> reports = new List<ShotSyncProgress>();
                mirror.SyncAsync(device, new InlineProgress<ShotSyncProgress>(reports.Add), CancellationToken.None).GetAwaiter().GetResult();
                string[] files = Directory.GetFiles(root, "*.png");
                Tests.Check(files.Length == 5 && device.Reads == 4 && mirror.Count == 4 && reports.Count == 5 && reports[4].Done == 4 && reports[4].Total == 4,
                    "four handheld pictures become five local files, read once each with progress");

                List<string> pair = mirror.LocalsFor("Thor_20261107_204811_dual.png");
                Tests.Check(pair.Count == 2 && Path.GetFileName(pair[0]).StartsWith("thor-upper-20261107-204811-000-") && GalleryIndex.PeerName(Path.GetFileName(pair[0])) == Path.GetFileName(pair[1]),
                    "a stacked picture becomes an upper/lower pair the gallery pairs by name");
                using (Bitmap upper = new Bitmap(pair[0])) using (Bitmap lower = new Bitmap(pair[1]))
                    Tests.Check(upper.Size == new Size(1920, 1080) && lower.Size == new Size(1240, 1080) && upper.GetPixel(10, 10).ToArgb() == Color.Red.ToArgb()
                        && lower.GetPixel(0, 0).ToArgb() == Color.Blue.ToArgb() && lower.GetPixel(1239, 1079).ToArgb() == Color.Blue.ToArgb(),
                        "the split cuts the upper screen and the centred lower screen exactly");
                Tests.Check(File.GetLastWriteTime(pair[0]) == new DateTime(2026, 11, 7, 20, 48, 11), "local files carry the shot time so the gallery dates them correctly");
                string top = mirror.LocalsFor("Thor_20261031_134428_top.png")[0], small = mirror.LocalsFor("Screenshot_20261024-130533.png")[0], odd = mirror.LocalsFor("Screenshot_20261020-073241.jpg")[0];
                Tests.Check(GalleryIndex.ScreenOf(Path.GetFileName(top)) == 0 && GalleryIndex.ScreenOf(Path.GetFileName(small)) == 1 && Path.GetFileName(odd).StartsWith("thor-shot-") && odd.EndsWith(".png"),
                    "single pictures are named upper or lower by size; other sizes and JPEGs are kept as plain PNG pictures");
                Tests.Check(File.ReadAllBytes(top).Length == device.Files["Thor_20261031_134428_top.png"].Length, "a single PNG is stored byte for byte, not re-encoded");

                // A fresh mirror reads the index back; an unchanged handheld costs no reads.
                DeviceShotMirror again = new DeviceShotMirror(root);
                Tests.Check(again.RemoteFor(pair[1]) == "Thor_20261107_204811_dual.png" && again.Count == 4, "the index remembers which handheld file each local file came from");
                again.SyncAsync(device, null, CancellationToken.None).GetAwaiter().GetResult();
                Tests.Check(device.Reads == 4, "a second sync with nothing new reads nothing");

                // Deleted on the handheld: gone here too. Changed: read again.
                device.Files.Remove("Thor_20261031_134428_top.png");
                device.Add("Screenshot_20261024-130533.png", Png(1240, 1080, g => g.Clear(Color.Purple), ImageFormat.Png), 1792818400);
                again.SyncAsync(device, null, CancellationToken.None).GetAwaiter().GetResult();
                Tests.Check(!File.Exists(top) && again.RemoteFor(top) == null && device.Reads == 5, "pictures deleted on the handheld leave the mirror and changed ones are read again");
                using (Bitmap redone = new Bitmap(again.LocalsFor("Screenshot_20261024-130533.png")[0])) Tests.Check(redone.GetPixel(5, 5).ToArgb() == Color.Purple.ToArgb(), "the re-read picture replaces the old copy");

                // Forget after a delete removes both halves of a split picture.
                device.DeleteAsync(new[] { "Thor_20261107_204811_dual.png" }, CancellationToken.None).Wait();
                again.Forget(new[] { "Thor_20261107_204811_dual.png" });
                Tests.Check(!File.Exists(pair[0]) && !File.Exists(pair[1]) && new DeviceShotMirror(root).Count == 2, "forgetting a split picture removes both screens and is saved");

                // A broken link mid-way keeps what was already copied.
                string partialRoot = root + "-partial";
                FakeShots flaky = new FakeShots();
                for (int i = 0; i < 4; i++) flaky.Add("Screenshot_2026101" + i + "-120000.png", Png(1920, 1080, g => g.Clear(Color.Gray), ImageFormat.Png), 1790000000 + i);
                flaky.FailAfter = 2;
                bool broke = false;
                try { new DeviceShotMirror(partialRoot).SyncAsync(flaky, null, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (DeviceShotException error) { broke = error.Code == "SHOTS_LINK_LOST" && DeviceShotException.Describe(error).Contains("中断"); }
                Tests.Check(broke && new DeviceShotMirror(partialRoot).Count == 2, "a broken link reports in Chinese and keeps the pictures already copied");
                Directory.Delete(partialRoot, true);
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }

            // The gallery switch: 掌机 shows the mirrored handheld pictures, 电脑 goes back.
            WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            string serialName = "fake-shots-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string mirrorRoot = DeviceShotMirror.FolderFor(Adb.AppDir(), serialName);
            try
            {
                FakeShots handheld = new FakeShots();
                handheld.Add("Thor_20261107_204811_dual.png", Stacked(), 1794055692);
                handheld.Add("Thor_20261031_134428_top.png", Png(1920, 1080, g => g.Clear(Color.Green), ImageFormat.Png), 1793425469);
                using (MainForm form = new MainForm(true))
                {
                    form.ShotDeviceFactory = s => handheld;
                    typeof(MainForm).GetField("serial", Flags).SetValue(form, serialName);
                    GalleryView gallery = (GalleryView)typeof(MainForm).GetField("gallery", Flags).GetValue(form);
                    PreviewBox preview = (PreviewBox)typeof(MainForm).GetField("preview", Flags).GetValue(form);
                    Segmented source = (Segmented)typeof(MainForm).GetField("sourcePicker", Flags).GetValue(form);
                    source.SelectedIndex = 1;
                    bool shown = Pump(() => form.ShotSyncTask.IsCompleted && gallery.Items.Count == 3, 15000);
                    Tests.Check(shown && form.ShowingDeviceShots && handheld.Reads == 2, "switching to 掌机 reads the handheld and shows its pictures (a stacked one as two screens)");
                    gallery.SelectOnly(gallery.IndexOf(form.ShotMirror.LocalsFor("Thor_20261107_204811_dual.png")[0]));
                    Tests.Check(Pump(() => preview.Current != null && preview.HasCompanion, 8000), "a split handheld picture opens with its paired screen, ready for the shell preview");
                    source.SelectedIndex = 0;
                    Tests.Check(!form.ShowingDeviceShots && Pump(() => gallery.Items.Count == 0 || gallery.IndexOf(form.ShotMirror.LocalsFor("Thor_20261031_134428_top.png")[0]) < 0, 8000),
                        "switching back to 电脑 shows the computer gallery again");
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                WindowsFormsSynchronizationContext.AutoInstall = true;
                try { Directory.Delete(mirrorRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> action;
            public InlineProgress(Action<T> action) { this.action = action; }
            public void Report(T value) { action(value); }
        }
    }
}
