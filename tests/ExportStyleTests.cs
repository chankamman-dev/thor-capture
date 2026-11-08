using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace ThorCapture
{
    // Export settings: transparent / solid / gradient backgrounds, padding, shadows, format and the dialog.
    internal static class ExportStyleTests
    {
        private static Bitmap Solid(int width, int height, Color color)
        {
            Bitmap bitmap = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(bitmap)) g.Clear(color);
            return bitmap;
        }

        private static ImageExportPlan Mosaic(ExportLayout layout, bool shadow, int padding, ExportBackground back, Color color)
        {
            return new ImageExportPlan(layout, DeviceSkin.All[0], back, color, color, null, 1, 10, Color.Transparent, 0, ImageFormat.Png, 90, false, shadow, padding);
        }

        internal static void Run()
        {
            DeviceSkin skin = DeviceSkin.All[0];
            using (Bitmap top = Solid(192, 108, Color.Red))
            using (Bitmap bottom = Solid(124, 108, Color.Blue))
            {
                // Framed: padding is measured from the shell (and its shadow), not the old fixed margins.
                ImageExportPlan tight = new ImageExportPlan(ExportLayout.Framed, skin, ExportBackground.Transparent, Color.White, Color.White, null, 1, 0, Color.Transparent, 0, ImageFormat.Png, 90, false, false, 0);
                Size tightSize = ImageExporter.MeasureOutput(tight, top.Size, bottom.Size);
                float scale = 1920f / 790f;
                Tests.Check(tightSize == new Size((int)Math.Round(DeviceArt.Width * scale), (int)Math.Round(DeviceArt.Height * scale)), "no padding and no shadow crops the picture to the shell");
                using (Bitmap shell = ImageExporter.Render(tight, top, bottom))
                    Tests.Check(shell.GetPixel(0, 0).A == 0 && shell.GetPixel(shell.Width / 2, shell.Height / 2).A == 255, "the cropped shell keeps transparent rounded corners and an opaque body");

                ImageExportPlan lit = new ImageExportPlan(ExportLayout.Framed, skin, ExportBackground.Transparent, Color.White, Color.White, null, 1, 0, Color.Transparent, 0, ImageFormat.Png, 90, false, true, 20);
                ImageExportPlan flat = new ImageExportPlan(ExportLayout.Framed, skin, ExportBackground.Transparent, Color.White, Color.White, null, 1, 0, Color.Transparent, 0, ImageFormat.Png, 90, false, false, 20);
                using (Bitmap withShadow = ImageExporter.Render(lit, top, bottom))
                using (Bitmap without = ImageExporter.Render(flat, top, bottom))
                {
                    int below = (int)Math.Round((20 + DeviceArt.Height + 14) * scale);
                    Tests.Check(withShadow.Height > without.Height && withShadow.GetPixel(withShadow.Width / 2, below).A > 0 && withShadow.GetPixel(withShadow.Width / 2, below).A < 255,
                        "the shell shadow is a soft translucent edge inside the padding");
                    Tests.Check(without.GetPixel(without.Width / 2, without.Height - 3).A == 0, "turning the shadow off leaves the padding fully transparent");
                }

                // Mosaics: padding round the screens, scaled with the pixel scale, and an optional soft shadow.
                using (Bitmap padded = ImageExporter.Render(Mosaic(ExportLayout.Single, false, 12, ExportBackground.Transparent, Color.White), top, null))
                    Tests.Check(padded.Size == new Size(192 + 24, 108 + 24) && padded.GetPixel(5, 5).A == 0 && padded.GetPixel(12, 12).ToArgb() == Color.Red.ToArgb(), "padding surrounds a single screen with transparency");
                ImageExportPlan doubled = new ImageExportPlan(ExportLayout.Single, skin, ExportBackground.Transparent, Color.White, Color.White, null, 2, 0, Color.Transparent, 0, ImageFormat.Png, 90, false, false, 12);
                Tests.Check(ImageExporter.MeasureOutput(doubled, top.Size, null) == new Size(192 * 2 + 48, 108 * 2 + 48), "padding grows with the integer pixel scale");
                using (Bitmap shaded = ImageExporter.Render(Mosaic(ExportLayout.StackVertical, true, 20, ExportBackground.Transparent, Color.White), top, bottom))
                using (Bitmap plain = ImageExporter.Render(Mosaic(ExportLayout.StackVertical, false, 20, ExportBackground.Transparent, Color.White), top, bottom))
                {
                    Tests.Check(shaded.Size == new Size(192 + 40, 108 + 10 + 108 + 40) && shaded.GetPixel(20 + 96, 20 + 108 + 10 + 108 + 4).A > 0, "screen shadows fall softly below each screen");
                    Tests.Check(plain.GetPixel(20 + 96, 20 + 108 + 10 + 108 + 4).A == 0 && plain.GetPixel(20 + 96, 20 + 108 + 10 + 50).ToArgb() == Color.Blue.ToArgb(), "without shadows the margins stay clear and the screens untouched");
                }
                using (Bitmap side = ImageExporter.Render(Mosaic(ExportLayout.SideBySide, false, 8, ExportBackground.Solid, Color.FromArgb(20, 30, 40)), top, bottom))
                    Tests.Check(side.Size == new Size(8 + 192 + 10 + 124 + 8, 8 + 108 + 8) && side.GetPixel(2, 2).ToArgb() == Color.FromArgb(20, 30, 40).ToArgb() && side.GetPixel(8 + 192 + 5, 50).ToArgb() == Color.FromArgb(20, 30, 40).ToArgb(),
                        "a solid background fills the padding and the gap");

                // Style: backdrop parsing, JPEG fallback and gradients.
                ExportBackdrop none = new ExportBackdrop("bogus", null), gradient = new ExportBackdrop("grad:EEF1F6:C9D3E2", null), custom = new ExportBackdrop(ExportBackdrop.Solid(Color.FromArgb(1, 2, 3)), null);
                Tests.Check(none.Transparent && none.Key == "none" && gradient.Kind == ExportBackground.Gradient && gradient.Color2.ToArgb() == Color.FromArgb(0xC9, 0xD3, 0xE2).ToArgb()
                    && custom.Key == "solid:010203" && custom.Color1.ToArgb() == Color.FromArgb(1, 2, 3).ToArgb(), "backgrounds round-trip through their stored keys and junk falls back to transparent");
                ExportStyle style = new ExportStyle { Kind = ExportKind.Stacked, Background = "none", Padding = 1, Shadow = true, Rounded = true };
                Tests.Check(style.HasAlpha && style.Extension == ".png" && style.FilePrefix == "thor-dual-", "a transparent PNG keeps its alpha");
                style.Jpeg = true;
                Tests.Check(!style.HasAlpha && style.Backdrop.Kind == ExportBackground.Solid && style.Backdrop.Color1.ToArgb() == ExportBackdrop.Fallback.ToArgb() && style.Extension == ".jpg",
                    "JPEG cannot be transparent and falls back to the light grey");
                using (Bitmap jpegLook = style.Render(skin, top, bottom, "", "", null))
                    Tests.Check(jpegLook.GetPixel(1, 1).ToArgb() == ExportBackdrop.Fallback.ToArgb(), "the JPEG export is drawn on the fallback colour");
                style.Jpeg = false;
                using (Bitmap rounded = style.Render(skin, top, bottom, "", "", null))
                    Tests.Check(rounded.GetPixel(32, 32).A < 255 && rounded.GetPixel(32 + 96, 32 + 54).ToArgb() == Color.Red.ToArgb(), "rounded screens clip their corners and keep the middle");
                style.Background = "grad:FFFFFF:000000";
                using (Bitmap fade = style.Render(skin, top, bottom, "", "", null))
                    Tests.Check(fade.GetPixel(1, 1).R > 230 && fade.GetPixel(1, fade.Height - 2).R < 25, "the gradient runs from the first colour at the top to the second at the bottom");

                // Preview renders stay inside the room they are given and keep the picture's proportions.
                style.Background = "none";
                foreach (ExportKind kind in new[] { ExportKind.Framed, ExportKind.Stacked, ExportKind.SideBySide, ExportKind.Showcase })
                {
                    style.Kind = kind;
                    Size full = style.Measure(skin, top, bottom, "标题", "下屏");
                    using (Bitmap small = style.Render(skin, top, bottom, "标题", "下屏", new Size(200, 240)))
                        Tests.Check(small.Width <= 200 && small.Height <= 240 && Math.Abs(small.Width / (double)small.Height - full.Width / (double)full.Height) < .03, "the preview of " + kind + " fits its room at the export's proportions");
                }
                style.Kind = ExportKind.Framed; style.Scale = 3;
                bool refused = false;
                try { style.Measure(skin, Solid(1920, 1080, Color.Red), Solid(1240, 1080, Color.Blue), "", ""); }
                catch (ArgumentException) { refused = true; }
                Tests.Check(refused, "a framed 3x export of full-size screens is refused as too large instead of exhausting memory");

                // Saving: PNG keeps alpha, JPEG is written flat, an existing file is replaced.
                string folder = Path.Combine(Path.GetTempPath(), "thor-export-style-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    string png = Path.Combine(folder, "a.png"), jpg = Path.Combine(folder, "a.jpg");
                    File.WriteAllText(png, "old");
                    using (Bitmap picture = ImageExporter.Render(Mosaic(ExportLayout.Single, false, 6, ExportBackground.Transparent, Color.White), top, null))
                    {
                        ImageOutput.Save(picture, png, false);
                        ImageOutput.Save(picture, jpg, true);
                    }
                    using (Bitmap back = new Bitmap(png)) Tests.Check(back.GetPixel(1, 1).A == 0 && back.GetPixel(10, 10).ToArgb() == Color.Red.ToArgb(), "the saved PNG replaces the old file and keeps transparency");
                    using (Image back = Image.FromFile(jpg)) Tests.Check(back.RawFormat.Guid == ImageFormat.Jpeg.Guid && back.Size == new Size(204, 120), "the JPEG is written at full size");
                    Tests.Check(Directory.GetFiles(folder).Length == 2, "no temporary files are left behind");
                }
                finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
                using (Bitmap clear = new Bitmap(4, 4, PixelFormat.Format32bppArgb))
                using (Bitmap flatCopy = ImageOutput.Flatten(clear, ExportBackdrop.Fallback))
                    Tests.Check(flatCopy.GetPixel(1, 1).ToArgb() == ExportBackdrop.Fallback.ToArgb(), "the clipboard's plain bitmap puts transparency on the light grey");

                // The dialog: options switch on and off with the layout and it disposes cleanly.
                using (ExportImageDialog dialog = new ExportImageDialog(new ExportStyle { Kind = ExportKind.Showcase }, skin, top, bottom, "标题", "下屏"))
                {
                    dialog.CreateControl();
                    Tests.Check(dialog.Style.Kind == ExportKind.Showcase, "the dialog opens on the requested layout");
                }
                using (ExportImageDialog dialog = new ExportImageDialog(new ExportStyle { Kind = ExportKind.Stacked }, skin, top, null, "", ""))
                    Tests.Check(dialog.Style.Kind == ExportKind.Framed, "a single screen cannot be stitched, so the dialog falls back to the framed layout");
            }
        }
    }
}
