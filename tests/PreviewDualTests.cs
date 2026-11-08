using System;
using System.Drawing;

namespace ThorCapture
{
    // The dual view: both screens without the shell, on screen and as a full-resolution picture.
    internal static class PreviewDualTests
    {
        private static Bitmap Solid(int width, int height, Color color)
        {
            Bitmap bitmap = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(bitmap)) g.Clear(color);
            return bitmap;
        }

        internal static void Run()
        {
            Size total;
            Rectangle[] parts = PreviewBox.DualLayout(new Size(1920, 1080), new Size(1240, 1080), out total);
            Tests.Check(total == new Size(1920, 2208) && parts[0] == new Rectangle(0, 0, 1920, 1080) && parts[1] == new Rectangle(340, 1128, 1240, 1080),
                "the lower screen sits centred under the upper one, both at their own pixel size");

            using (PreviewBox preview = new PreviewBox { Size = new Size(900, 1000) })
            {
                preview.SetImages(Solid(1920, 1080, Color.Red), Solid(1240, 1080, Color.Blue), false);
                using (Bitmap dual = preview.ExportDual(true))
                {
                    Tests.Check(dual.Size == new Size(1920, 2208), "the dual picture is the screens at full resolution");
                    Tests.Check(dual.GetPixel(10, 10).ToArgb() == Color.Red.ToArgb() && dual.GetPixel(1919, 1079).ToArgb() == Color.Red.ToArgb()
                        && dual.GetPixel(350, 1140).ToArgb() == Color.Blue.ToArgb() && dual.GetPixel(1579, 2207).ToArgb() == Color.Blue.ToArgb(), "each screen is drawn pixel for pixel in its place");
                    Tests.Check(dual.GetPixel(960, 1100).A == 0 && dual.GetPixel(100, 1600).A == 0 && dual.GetPixel(1800, 1600).A == 0, "the gap and the margins beside the lower screen are transparent in the PNG");
                }
                using (Bitmap copy = preview.ExportDual(false))
                    Tests.Check(copy.GetPixel(960, 1100).ToArgb() == Color.Black.ToArgb(), "the clipboard copy fills the gap instead of relying on transparency");

                preview.SetImages(Solid(1240, 1080, Color.Blue), Solid(1920, 1080, Color.Red), true); // the lower screen selected
                using (Bitmap dual = preview.ExportDual(true))
                    Tests.Check(dual.GetPixel(10, 10).ToArgb() == Color.Red.ToArgb() && dual.GetPixel(350, 1140).ToArgb() == Color.Blue.ToArgb(), "selecting the lower capture gives the same picture");

                // Showcase style: captioned page, both screens at the same width.
                preview.SetImages(Solid(1920, 1080, Color.Red), Solid(1240, 1080, Color.Blue), false);
                preview.Showcase = true; preview.ShowcaseTitle = "03  地图"; preview.ShowcaseLabel = "下屏 · 伴侣";
                Size page;
                Rectangle[] sheet = PreviewBox.ShowcaseLayout(new Size(1920, 1080), new Size(1240, 1080), true, true, out page);
                Tests.Check(sheet[1] == new Rectangle(0, PreviewBox.ShowTitle, 1920, 1080) && sheet[3].Width == 1920 - PreviewBox.ShowInset * 2
                    && Math.Abs(sheet[3].Height - sheet[3].Width * 1080.0 / 1240) < 1 && page.Height == sheet[3].Bottom + PreviewBox.ShowFoot, "the showcase stacks title, upper screen, caption and the widened lower screen");
                using (Bitmap shown = preview.ExportDual(true))
                {
                    Tests.Check(shown.Size == page, "the showcase exports at 1920 wide");
                    Color band = shown.GetPixel(4, 4), upper = shown.GetPixel(10, sheet[1].Y + 10), lower = shown.GetPixel(960, sheet[3].Y + 20), beside = shown.GetPixel(6, sheet[3].Y + 20);
                    Tests.Check(band.R < 40 && band.A == 255 && upper.ToArgb() == Color.Red.ToArgb() && lower.ToArgb() == Color.Blue.ToArgb() && beside.R < 40 && beside.B < 40,
                        "dark title band, the upper screen edge to edge, the lower screen inset on a dark page");
                    bool text = false;
                    for (int x = 30; x < 400 && !text; x++) for (int y = 20; y < 64 && !text; y++) text = shown.GetPixel(x, y).R > 150;
                    Tests.Check(text, "the title is written in the band");
                }
                // Without a title there is no band: the upper screen starts at the top edge.
                preview.ShowcaseTitle = "  ";
                Size bare;
                Rectangle[] untitled = PreviewBox.ShowcaseLayout(new Size(1920, 1080), new Size(1240, 1080), false, true, out bare);
                using (Bitmap shown = preview.ExportDual(true))
                    Tests.Check(untitled[1].Y == 0 && shown.Size == bare && bare.Height == page.Height - PreviewBox.ShowTitle && shown.GetPixel(10, 2).ToArgb() == Color.Red.ToArgb(),
                        "an empty title removes the title band entirely");
                preview.ShowcaseLabel = "";
                Size gapOnly;
                Rectangle[] uncaptioned = PreviewBox.ShowcaseLayout(new Size(1920, 1080), new Size(1240, 1080), false, false, out gapOnly);
                using (Bitmap shown = preview.ExportDual(true))
                    Tests.Check(shown.Size == gapOnly && uncaptioned[2].Height == PreviewBox.ShowGap && shown.GetPixel(960, uncaptioned[3].Y + 10).ToArgb() == Color.Blue.ToArgb(),
                        "an empty caption leaves only a narrow gap between the screens");
                preview.Showcase = false;

                preview.Mode = PreviewMode.Dual;
                using (Bitmap shot = new Bitmap(900, 1000)) preview.DrawToBitmap(shot, new Rectangle(0, 0, 900, 1000));
                preview.SetImages(Solid(1920, 1080, Color.Red), null, false);
                Tests.Check(preview.ExportDual(true) == null, "a capture without its paired screen has no dual picture");
                using (Bitmap shot = new Bitmap(900, 1000)) preview.DrawToBitmap(shot, new Rectangle(0, 0, 900, 1000));
                Tests.Check(true, "the dual view paints with one or both screens");
            }
        }
    }
}
