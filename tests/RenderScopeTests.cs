using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace ThorCapture
{
    // Paint-scope regressions: animations and content changes must repaint only what they touch.
    internal static class RenderScopeTests
    {
        private sealed class Stage : PreviewBox
        {
            public void Render(Graphics g, Rectangle clip) { g.SetClip(clip); OnPaint(new PaintEventArgs(g, clip)); g.ResetClip(); }
        }

        private static Bitmap Solid(int w, int h, Color c)
        {
            Bitmap b = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(b)) g.Clear(c);
            return b;
        }

        private static long Sum(long[] values) { long total = 0; foreach (long v in values) total += v; return total; }

        private static void MediaCaches()
        {
            bool motion = Motion.Enabled;
            Motion.Enabled = false;
            try
            {
                using (Stage stage = new Stage { Size = new Size(760, 940) })
                using (Bitmap canvas = new Bitmap(760, 940))
                using (Graphics g = Graphics.FromImage(canvas))
                {
                    FieldInfo cacheField = typeof(PreviewBox).GetField("deviceCache", BindingFlags.Instance | BindingFlags.NonPublic);
                    stage.SetImages(Solid(192, 108, Color.Red), Solid(124, 108, Color.Blue), false);
                    stage.Render(g, stage.ClientRectangle);
                    object shell = cacheField.GetValue(stage);
                    long layers = Sum(stage.Layers.RenderCounts);
                    Image upper = Solid(192, 108, Color.Green);
                    stage.SetImages(upper, Solid(124, 108, Color.Yellow), false);
                    stage.Render(g, stage.ClientRectangle);
                    Tests.Check(shell != null && ReferenceEquals(shell, cacheField.GetValue(stage)) && Sum(stage.Layers.RenderCounts) == layers,
                        "switching pictures keeps the shell cache and control layers");

                    // The cached screen must match a direct draw of the same picture.
                    RectangleF bounds = stage.DeviceBounds();
                    Rectangle top = ShellGeometry.ToClient(bounds, DeviceArt.TopScreen);
                    using (Bitmap direct = (Bitmap)canvas.Clone())
                    {
                        using (Graphics d = Graphics.FromImage(direct)) DeviceArt.DrawScreen(d, bounds, true, upper);
                        long error = 0, count = 0;
                        for (int y = top.Y + 2; y < top.Bottom - 2; y += 3)
                            for (int x = top.X + 2; x < top.Right - 2; x += 3)
                            {
                                Color a = canvas.GetPixel(x, y), b = direct.GetPixel(x, y);
                                error += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B); count++;
                            }
                        Tests.Check(count > 0 && error / (double)count < 1.0, "cached screen picture matches a direct draw");
                    }

                    // Out-of-clip parts are not rebuilt; the next paint covering them is up to date.
                    long before = stage.Layers.RenderCounts[0];
                    stage.SetInput(new PadState { LX = .8f, LY = .3f });
                    stage.AdvanceAnimation(.25);
                    stage.Render(g, top);
                    bool skipped = stage.Layers.RenderCounts[0] == before;
                    stage.Render(g, stage.ClientRectangle);
                    Tests.Check(skipped && stage.Layers.RenderCounts[0] == before + 1, "stick layer rebuilds only when its own area is painted");

                    long rebuilt = Sum(stage.Layers.RenderCounts);
                    stage.Skin = DeviceSkin.All[0];
                    stage.Render(g, stage.ClientRectangle);
                    Tests.Check(!ReferenceEquals(shell, cacheField.GetValue(stage)) && Sum(stage.Layers.RenderCounts) > rebuilt, "changing the colourway still rebuilds shell and controls");
                }
            }
            finally { Motion.Enabled = motion; }
        }
        private static void Tick()
        {
            typeof(Motion).GetMethod("Tick", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { null, EventArgs.Empty });
        }

        internal static void Run()
        {
            MediaCaches();
            bool motion = Motion.Enabled;
            Motion.Enabled = true;
            try
            {
                using (PreviewBox stage = new PreviewBox { Size = new Size(760, 940) })
                {
                    stage.CreateControl();
                    List<Rectangle> dirty = new List<Rectangle>();
                    stage.Invalidated += delegate(object sender, InvalidateEventArgs e) { dirty.Add(e.InvalidRect); };
                    stage.Toast("scope", Ui.Ok);
                    Rectangle area = (Rectangle)typeof(PreviewBox).GetMethod("ToastArea", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(stage, null);
                    dirty.Clear();
                    Tick();
                    Tests.Check(dirty.Count > 0 && dirty.TrueForAll(r => area.Contains(r)), "toast animation frames invalidate only the toast band");
                }

                using (GalleryView gallery = new GalleryView { Size = new Size(745, 900) })
                {
                    gallery.CreateControl();
                    List<GalleryItem> items = new List<GalleryItem>();
                    for (int i = 0; i < 40; i++) items.Add(new GalleryItem { Path = "scope-" + i, Time = new DateTime(2026, 1, 1), Screen = 0 });
                    // Populate without the pop-in so only the thumbnail fade is animating.
                    Motion.Enabled = false; gallery.SetItems(items); Motion.Enabled = true;
                    List<Rectangle> dirty = new List<Rectangle>();
                    gallery.Invalidated += delegate(object sender, InvalidateEventArgs e) { dirty.Add(e.InvalidRect); };
                    gallery.SetThumb(3, new Bitmap(8, 8));
                    dirty.Clear();
                    Tick();
                    bool local = dirty.Count > 0 && dirty.TrueForAll(r => r.Width < gallery.Width / 2);
                    Tests.Check(local, "thumbnail fade-in invalidates its card, not the whole gallery");
                }
            }
            finally { Motion.Enabled = motion; }
        }
    }
}
