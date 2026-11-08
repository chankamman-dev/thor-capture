using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace ThorCapture
{
    // Background folder index, debounced in-memory search and background picture loading.
    internal static class GalleryIndexTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        private static bool Pump(Func<bool> done, int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < timeoutMs) { Application.DoEvents(); System.Threading.Thread.Sleep(2); }
            return done();
        }

        private static void Png(string path, int width, Color color)
        {
            using (Bitmap bitmap = new Bitmap(width, 36))
            {
                using (Graphics g = Graphics.FromImage(bitmap)) g.Clear(color);
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        internal static void Run()
        {
            string folder = Path.Combine(Tests.Root, "gallery-index-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            // Three single captures; the width identifies which one is on screen.
            string[] names = { "thor-upper-20260101-000001-000-aaaaaa.png", "thor-upper-20260101-000002-000-bbbbbb.png", "thor-upper-20260101-000003-000-cccccc.png" };
            for (int i = 0; i < names.Length; i++)
            {
                string path = Path.Combine(folder, names[i]);
                Png(path, 64 + i * 16, Color.FromArgb(40 * i, 90, 160));
                File.SetLastWriteTime(path, new DateTime(2026, 1, 1, 0, 0, i));
            }
            Tests.Check(GalleryIndex.PeerName("thor-upper-x-1.png") == "thor-lower-x-1.png" && GalleryIndex.ScreenOf("thor-lower-x.png") == 1, "index pairs upper/lower names");

            using (MainForm form = new MainForm(true))
            {
                typeof(MainForm).GetField("captureDir", Flags).SetValue(form, folder);
                MethodInfo refresh = typeof(MainForm).GetMethod("RefreshList", Flags);
                GalleryView gallery = (GalleryView)typeof(MainForm).GetField("gallery", Flags).GetValue(form);
                PreviewBox preview = (PreviewBox)typeof(MainForm).GetField("preview", Flags).GetValue(form);
                SearchBox search = (SearchBox)typeof(MainForm).GetField("search", Flags).GetValue(form);
                Func<int> loads = () => (int)typeof(MainForm).GetField("PictureLoads", Flags).GetValue(form);

                Stopwatch call = Stopwatch.StartNew();
                refresh.Invoke(form, new object[] { null });
                call.Stop();
                bool indexed = Pump(() => gallery.Items.Count == 3 && preview.Current != null, 8000);
                Tests.Check(indexed && gallery.Primary != null && gallery.Primary.Name == names[2], "background index publishes the folder and selects the newest capture");

                // Typing a query that still contains the selection neither rescans nor reloads it.
                int before = loads();
                search.Box.Text = "thor"; search.Box.Text = "thor-"; search.Box.Text = "thor-up";
                Stopwatch settle = Stopwatch.StartNew();
                Pump(() => settle.ElapsedMilliseconds > 500, 1000);
                Tests.Check(gallery.Primary != null && gallery.Primary.Name == names[2] && loads() == before, "search keeps the selection without decoding it again");

                // A narrower query filters in memory after the debounce.
                search.Box.Text = "bbbbbb";
                Tests.Check(Pump(() => gallery.Items.Count == 1, 2000) && gallery.Items[0].Name == names[1], "debounced search filters the in-memory index");
                search.Box.Text = "";
                Pump(() => gallery.Items.Count == 3, 2000);

                // Rapid A -> B -> C: only the last selection may reach the preview.
                gallery.SelectOnly(gallery.IndexOf(Path.Combine(folder, names[0])));
                gallery.SelectOnly(gallery.IndexOf(Path.Combine(folder, names[1])));
                gallery.SelectOnly(gallery.IndexOf(Path.Combine(folder, names[2])));
                gallery.SelectOnly(gallery.IndexOf(Path.Combine(folder, names[0])));
                Pump(() => preview.Current != null && preview.Current.Width == 64, 4000);
                Stopwatch late = Stopwatch.StartNew();
                Pump(() => late.ElapsedMilliseconds > 400, 1000);
                Tests.Check(preview.Current != null && preview.Current.Width == 64, "rapid selection shows only the last choice; late decodes are discarded");

                // A new file shows up after a rescan and can be selected by path.
                string added = Path.Combine(folder, "thor-lower-20260101-000004-000-dddddd.png");
                Png(added, 40, Color.Orange);
                File.SetLastWriteTime(added, new DateTime(2026, 1, 1, 0, 0, 9));
                refresh.Invoke(form, new object[] { added });
                Tests.Check(Pump(() => gallery.Primary != null && gallery.Primary.Path == added && preview.Current != null && preview.Current.Width == 40, 8000),
                    "rescan picks up a new capture and selects it");
                Tests.Check(call.ElapsedMilliseconds < 1000, "folder refresh returns without waiting for the scan");
            }
        }
    }
}
