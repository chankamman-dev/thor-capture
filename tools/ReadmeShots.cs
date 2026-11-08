using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace ThorCapture
{
    // Renders the README pictures from real capture pairs with the app's own drawing code.
    //   ReadmeShots.exe <folder with thor-upper/lower pairs> <output folder> <hero pairs...> -- <framed> <stacked> <showcase>
    // Pairs are given by their shared suffix (the part after "thor-upper-").
    internal static class ReadmeShots
    {
        private static Image Load(string folder, string suffix, bool upper)
        {
            using (FileStream stream = File.OpenRead(Path.Combine(folder, (upper ? "thor-upper-" : "thor-lower-") + suffix)))
            using (Image raw = Image.FromStream(stream)) return new Bitmap(raw);
        }

        private static Bitmap Shrink(Image source, int width)
        {
            int height = (int)Math.Round(source.Height * (double)width / source.Width);
            Bitmap small = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, width, height));
            }
            return small;
        }

        private static void Save(Image picture, string folder, string name, int width)
        {
            using (Bitmap small = Shrink(picture, Math.Min(width, picture.Width))) small.Save(Path.Combine(folder, name), ImageFormat.Png);
            Console.WriteLine(name);
        }

        [STAThread]
        private static int Main(string[] args)
        {
            string folder = args[0], output = args[1];
            int split = Array.IndexOf(args, "--");
            string[] hero = args.Skip(2).Take(split - 2).ToArray();
            string framedPair = args[split + 1], stackedPair = args[split + 2], showcasePair = args[split + 3];
            Directory.CreateDirectory(output);

            // Four colourways side by side, a different game in each, on a soft page.
            const int cell = 700, gap = 40, pad = 60;
            int width = pad * 2 + cell * hero.Length + gap * (hero.Length - 1);
            float shellHeight = cell * DeviceArt.Height / (float)DeviceArt.Width;
            using (Bitmap sheet = new Bitmap(width, (int)(shellHeight + pad * 2 + 30), PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(sheet))
                {
                    using (LinearGradientBrush page = new LinearGradientBrush(new Rectangle(0, 0, sheet.Width, sheet.Height), Color.FromArgb(244, 243, 240), Color.FromArgb(226, 228, 233), 90f))
                        g.FillRectangle(page, 0, 0, sheet.Width, sheet.Height);
                    for (int i = 0; i < hero.Length; i++)
                        using (Image top = Load(folder, hero[i], true)) using (Image bottom = Load(folder, hero[i], false))
                            DeviceArt.Draw(g, new RectangleF(pad + i * (cell + gap), pad, cell, shellHeight), DeviceSkin.All[i % DeviceSkin.All.Length].WithLamp(true, Color.Empty, 1f), top, bottom, null, null);
                }
                Save(sheet, output, "hero.png", 2000);
            }

            using (Image top = Load(folder, framedPair, true)) using (Image bottom = Load(folder, framedPair, false))
            using (Bitmap framed = new ExportStyle { Kind = ExportKind.Framed }.Render(DeviceSkin.All[1].WithLamp(true, Color.Empty, 1f), top, bottom, "", "", null))
                Save(framed, output, "framed-transparent.png", 900);

            using (Image top = Load(folder, stackedPair, true)) using (Image bottom = Load(folder, stackedPair, false))
            using (Bitmap stacked = new ExportStyle { Kind = ExportKind.Stacked, Background = "grad:2B3048:0F1118", Padding = 2 }.Render(DeviceSkin.All[0], top, bottom, "", "", null))
                Save(stacked, output, "dual-stacked.png", 900);

            using (Image top = Load(folder, showcasePair, true)) using (Image bottom = Load(folder, showcasePair, false))
            using (Bitmap page = new ExportStyle { Kind = ExportKind.Showcase }.Render(DeviceSkin.All[0], top, bottom, "地图 — 下屏随时看楼层和道具", "下屏 · 伴侣界面", null))
                Save(page, output, "dual-showcase.png", 900);
            return 0;
        }
    }
}
