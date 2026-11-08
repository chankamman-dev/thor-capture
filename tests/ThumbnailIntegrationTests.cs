using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace ThorCapture
{
    // Gallery thumbnails through the shared cache: visible-first, bounded, never mismatched.
    internal static class ThumbnailIntegrationTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        private static bool Pump(Func<bool> done, int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < timeoutMs) { Application.DoEvents(); System.Threading.Thread.Sleep(2); }
            return done();
        }

        // File i is filled with a colour that encodes i, so a thumbnail can be traced to its file.
        private static Color Code(int i) { return Color.FromArgb(255, 20 + (i % 200), 60 + (i / 200) * 40, 200); }

        private static bool Matches(GalleryItem item)
        {
            int i = int.Parse(Path.GetFileNameWithoutExtension(item.Path).Substring("thor-upper-20260101-".Length, 6));
            Color want = Code(i), got = item.Thumb.GetPixel(item.Thumb.Width / 2, item.Thumb.Height / 2);
            return Math.Abs(want.R - got.R) <= 2 && Math.Abs(want.G - got.G) <= 2 && Math.Abs(want.B - got.B) <= 2;
        }

        internal static void Run()
        {
            string folder = Path.Combine(Tests.Root, "thumbs-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            const int count = 300;
            for (int i = 0; i < count; i++)
            {
                string path = Path.Combine(folder, "thor-upper-20260101-" + i.ToString("D6") + "-000-abcdef.png");
                using (Bitmap bitmap = new Bitmap(96, 54))
                {
                    using (Graphics g = Graphics.FromImage(bitmap)) g.Clear(Code(i));
                    bitmap.Save(path, ImageFormat.Png);
                }
                File.SetLastWriteTime(path, new DateTime(2026, 1, 1).AddSeconds(i));
            }

            using (MainForm form = new MainForm(true))
            {
                typeof(MainForm).GetField("captureDir", Flags).SetValue(form, folder);
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-4000, 0);
                form.ShowInTaskbar = false;
                form.Show();
                GalleryView gallery = (GalleryView)typeof(MainForm).GetField("gallery", Flags).GetValue(form);
                typeof(MainForm).GetMethod("RefreshList", Flags).Invoke(form, new object[] { null });

                Func<bool> visibleLoaded = delegate
                {
                    List<int> visible = new List<int>(), nearby = new List<int>();
                    gallery.Window(0, visible, nearby);
                    if (visible.Count == 0) return false;
                    foreach (int i in visible) if (gallery.Items[i].Thumb == null) return false;
                    return true;
                };
                Tests.Check(Pump(() => gallery.Items.Count == count && visibleLoaded(), 15000), "visible cards receive thumbnails from the cache");

                List<int> onScreen = new List<int>(), around = new List<int>();
                gallery.Window(0, onScreen, around);
                bool right = true;
                foreach (int i in onScreen) right &= Matches(gallery.Items[i]);
                Tests.Check(right, "each visible thumbnail comes from its own file");
                Tests.Check(gallery.HeldThumbs < count / 2, "only cards near the viewport are loaded, not the whole library");

                // Jump to the far end: the new viewport fills in, the old one is released.
                gallery.SelectOnly(count - 1);
                Pump(() => false, 200);
                Tests.Check(Pump(visibleLoaded, 15000), "scrolling far requests the new visible cards");
                List<int> far = new List<int>(), unused = new List<int>();
                gallery.Window(0, far, unused);
                right = true;
                foreach (int i in far) right &= Matches(gallery.Items[i]);
                Tests.Check(right && gallery.Items[0].Thumb == null, "far-away thumbnails are released and new ones match their files");

                ThorCapture.Features.Thumbs.ThumbnailCache cache = (ThorCapture.Features.Thumbs.ThumbnailCache)typeof(MainForm).GetField("thumbs", Flags).GetValue(form);
                Tests.Check(cache != null && cache.PendingCount <= 96 && cache.MemoryCount <= 96, "cache queue and memory stay within their limits");
                form.Close();
            }
        }
    }
}
