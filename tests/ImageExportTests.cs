using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ThorCapture
{
    internal static class ImageExportTests
    {
        internal static void Run()
        {
            using (Bitmap top = Checker(16, 9))
            using (Bitmap bottom = Checker(12, 10))
            {
                foreach (DeviceSkin original in DeviceSkin.All)
                    foreach (bool lamp in new bool[] { false, true })
                    {
                        DeviceSkin skin = original.WithLamp(lamp, Color.Empty, .7f);
                        using (PreviewBox preview = new PreviewBox())
                        {
                            preview.Skin = skin; preview.SetImage((Image)top.Clone()); preview.SetCompanion((Image)bottom.Clone());
                            using (Bitmap expected = preview.ExportDevice(true))
                            using (Bitmap actual = ImageExporter.Render(ImageExportPlan.Default(skin), top, bottom))
                            {
                                Tests.Check(Equal(expected, actual), "default image export exactly matches legacy " + skin.Key + " lamp=" + lamp);
                                Tests.Check(actual.GetPixel(0, 0).A == 0, "framed image preserves transparent outer edge " + skin.Key + " lamp=" + lamp);
                            }
                        }
                    }
                DeviceSkin snap = ImageExportPlan.CopySkin(DeviceSkin.All[2]);
                foreach (DeviceSkin skin in DeviceSkin.All)
                    foreach (bool lower in new bool[] { false, true })
                        using (PreviewBox legacy = new PreviewBox())
                        {
                            legacy.Skin = skin; legacy.LowerScreen = lower; legacy.SetImage((Image)(lower ? bottom : top).Clone());
                            using (Bitmap expected = legacy.ExportDevice(true))
                            using (Bitmap actual = ImageExporter.Render(ImageExportPlan.Default(skin), lower ? null : top, lower ? bottom : null))
                                Tests.Check(Equal(expected, actual), "legacy default missing-screen compatibility " + skin.Key + " lower=" + lower);
                        }
                ImageExportPlan snapshot = ImageExportPlan.Default(snap); int body = snapshot.Skin.Body.ToArgb(), abxy = snapshot.Skin.Abxy[0].ToArgb();
                snap.Body = Color.Red; snap.Abxy[0] = Color.Green; DeviceSkin getter = snapshot.Skin; getter.Body = Color.Blue; getter.Abxy[0] = Color.Blue;
                Tests.Check(snapshot.Skin.Body.ToArgb() == body && snapshot.Skin.Abxy[0].ToArgb() == abxy, "export skin snapshot cannot be mutated through source or getter");
                for (int n = 1; n <= 4; n++)
                    using (Bitmap actual = ImageExporter.Render(Plan(ExportLayout.Single, n, 0, 0, null), top, null))
                        Tests.Check(PixelBlocks(top, actual, n), "nearest neighbour preserves every " + n + "x" + n + " pixel block");
                using (Bitmap stack = ImageExporter.Render(Plan(ExportLayout.StackVertical, 1, 3, 0, null), top, bottom))
                    Tests.Check(stack.Size == new Size(16, 22) && stack.GetPixel(4, 10).ToArgb() == Color.Magenta.ToArgb() && stack.GetPixel(2, 12).ToArgb() == bottom.GetPixel(0, 0).ToArgb(), "vertical mosaic has native pixels, centered lower screen and exact gap");
                using (Bitmap side = ImageExporter.Render(Plan(ExportLayout.SideBySide, 2, 5, 0, null), top, bottom))
                    Tests.Check(side.Size == new Size(61, 20) && side.GetPixel(33, 6).ToArgb() == Color.Magenta.ToArgb(), "side by side integer mosaic measures both independent aspect ratios");
                using (Bitmap single = ImageExporter.Render(Plan(ExportLayout.StackVertical, 1, 3, 0, null), null, bottom))
                    Tests.Check(Equal(single, bottom), "missing track in raw mosaic exports only the supplied screen");
                using (Bitmap rounded = ImageExporter.Render(Plan(ExportLayout.Single, 1, 0, 4, null), top, null))
                    Tests.Check(rounded.GetPixel(0, 0).A == 0 && rounded.GetPixel(8, 4).A == 255, "round corners clip only edges and preserve opaque center");
                using (Bitmap canvas = ImageExporter.Render(Plan(ExportLayout.Single, 2, 0, 0, new Size(40, 30)), top, null))
                    Tests.Check(canvas.GetPixel(0, 0).A == 0 && canvas.GetPixel(4, 6).ToArgb() == top.GetPixel(0, 0).ToArgb(), "larger integer canvas centers pixels without fractional resize");
                using (Bitmap canvasOdd = ImageExporter.Render(Plan(ExportLayout.Single, 2, 0, 0, new Size(41, 31)), top, null))
                {
                    bool blocks = true;
                    for (int y = 0; y < top.Height * 2; y++) for (int x = 0; x < top.Width * 2; x++) blocks &= canvasOdd.GetPixel(x + 4, y + 6).ToArgb() == top.GetPixel(x / 2, y / 2).ToArgb();
                    Tests.Check(blocks, "odd spare canvas pixel does not introduce half-pixel translation");
                }
                using (Bitmap framedPixels = ImageExporter.Render(Plan(ExportLayout.Framed, 2, 0, 0, null), top, bottom))
                {
                    float scale = 1920f / 790f, margin = 44f * scale;
                    int x = (int)Math.Round(margin + 44f * scale + (790f * scale - top.Width * 2) / 2);
                    int y = (int)Math.Round(margin * .6f + 26f * scale + (444.4f * scale - top.Height * 2) / 2);
                    bool exact = true;
                    for (int yy = 0; yy < top.Height * 2; yy++) for (int xx = 0; xx < top.Width * 2; xx++)
                        exact &= framedPixels.GetPixel(x + xx, y + yy).ToArgb() == top.GetPixel(xx / 2, yy / 2).ToArgb();
                    Tests.Check(exact, "framed integer mode redraws screen as exact blocks while shell retains smooth rendering");
                }
                using (Bitmap alpha = new Bitmap(5, 5, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(alpha)) g.Clear(Color.FromArgb(96, 40, 80, 120));
                    using (Bitmap alphaOut = ImageExporter.Render(Plan(ExportLayout.Single, 2, 0, 0, null), alpha, null))
                        Tests.Check(alphaOut.GetPixel(4, 4).A == 96, "raw PNG enlargement preserves source semi-transparency");
                }
                Tests.Reject(delegate { ImageExporter.MeasureOutput(Plan(ExportLayout.Single, 4, 0, 0, null), new Size(10000, 1000), null); }, "oversize output rejected before allocation");
                Tests.Reject(delegate { ImageExporter.MeasureOutput(Plan(ExportLayout.Single, 2, 0, 0, new Size(10, 10)), top.Size, null); }, "integer canvas cannot shrink pixel blocks");
                Tests.Reject(delegate { ImageExporter.MeasureOutput(Plan(ExportLayout.Single, 1, 0, 0, null), top.Size, bottom.Size); }, "single layout refuses ambiguous pair");
                Tests.Reject(delegate { ImageExporter.MeasureOutput(snapshot, null, null); }, "no media rejected explicitly");
                Tests.Reject(delegate { new ImageExportPlan(ExportLayout.Single, snap, ExportBackground.Transparent, Color.White, Color.White, null, 1, 0, Color.Transparent, 0, ImageFormat.Jpeg, 90); }, "transparent JPEG rejected before render");
                string directory = Path.Combine(Tests.Root, "image-export-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                string target = Path.Combine(directory, "result.png");
                ExportResult saved = ImageExporter.Save(Plan(ExportLayout.Single, 2, 0, 0, null), top, null, target);
                using (Bitmap disk = new Bitmap(target))
                    Tests.Check(saved.Success && PixelBlocks(top, disk, 2), "atomic PNG save reopens with exact enlarged pixels");
                byte[] before = File.ReadAllBytes(target);
                ExportResult conflict = ImageExporter.Save(Plan(ExportLayout.Single, 1, 0, 0, null), bottom, null, target);
                Tests.Check(conflict.Outcome == ImageExportOutcome.AlreadyExists && Convert.ToBase64String(before) == Convert.ToBase64String(File.ReadAllBytes(target)), "existing destination is preserved");
                ExportResult failed = ImageExporter.Save(Plan(ExportLayout.Single, 1, 0, 0, null), top, null, Path.Combine(directory, "missing", "result.png"));
                Tests.Check(failed.Outcome == ImageExportOutcome.FileFailed && Directory.GetFiles(directory, ".thor-export-*.tmp").Length == 0, "failed write removes temporary files");
                string raced = Path.Combine(directory, "raced.png");
                ExportResult race = ImageExporter.SaveAsync(Plan(ExportLayout.Single, 1, 0, 0, null), top, null, raced, CancellationToken.None,
                    new ImmediateProgress(delegate(int value) { if (value == 60) File.WriteAllText(raced, "external writer"); })).GetAwaiter().GetResult();
                Tests.Check(!race.Success && File.ReadAllText(raced) == "external writer" && Directory.GetFiles(directory, ".thor-export-*.tmp").Length == 0, "destination created during export wins race without damage or leftover");
                Tests.Check(ImageExporter.SaveAsync(Plan(ExportLayout.Single, 1, 0, 0, null), top, null, Path.Combine(directory, "observer.png"), CancellationToken.None,
                    new ImmediateProgress(delegate(int value) { throw new InvalidOperationException("observer"); })).GetAwaiter().GetResult().Success, "progress observer cannot invalidate committed export");
                CancellationTokenSource cancel = new CancellationTokenSource(); cancel.Cancel();
                ExportResult cancelled = ImageExporter.SaveAsync(snapshot, top, bottom, Path.Combine(directory, "cancel.png"), cancel.Token, null).GetAwaiter().GetResult();
                Tests.Check(cancelled.Outcome == ImageExportOutcome.Cancelled && !File.Exists(Path.Combine(directory, "cancel.png")), "pre-cancelled async save writes nothing"); cancel.Dispose();
                using (CancellationTokenSource lateCancel = new CancellationTokenSource())
                {
                    string latePath = Path.Combine(directory, "cancel-before-encode.png");
                    ExportResult late = ImageExporter.SaveAsync(Plan(ExportLayout.Single, 1, 0, 0, null), top, null, latePath, lateCancel.Token,
                        new ImmediateProgress(delegate(int value) { if (value == 60) lateCancel.Cancel(); })).GetAwaiter().GetResult();
                    Tests.Check(late.Outcome == ImageExportOutcome.Cancelled && !File.Exists(latePath) && Directory.GetFiles(directory, ".thor-export-*.tmp").Length == 0, "cancellation after render stops before encoding or publishing a temporary file");
                }
                ImageExportPlan jpeg = new ImageExportPlan(ExportLayout.Single, snap, ExportBackground.Gradient, Color.White, Color.Gray, null, 1, 0, Color.Transparent, 0, ImageFormat.Jpeg, 85);
                Tests.Check(ImageExporter.Save(jpeg, top, null, Path.Combine(directory, "result.jpg")).Success, "JPEG background and quality produce validated file");
                ImageExportPlan presetPlan = new ImageExportPlan(ExportLayout.SideBySide, snap.WithLamp(false, Color.Empty, .4f), ExportBackground.Gradient, Color.White, Color.DarkBlue, new Size(120, 80), 2, 7, Color.Magenta, 3, ImageFormat.Png, 91);
                IList<ExportPreset> presets = ExportPresetStore.Decode(ExportPresetStore.Encode(new List<ExportPreset> { new ExportPreset("双屏像素", presetPlan) }));
                Tests.Check(presets.Count == 1 && presets[0].Plan.Canvas == presetPlan.Canvas && presets[0].Plan.Skin.Glow.IsEmpty && presets[0].Plan.Gap == 7 && presets[0].Plan.Skin.Abxy[0].ToArgb() == presetPlan.Skin.Abxy[0].ToArgb(), "versioned presets preserve Chinese name, all skin fields and lamp-off semantics");
                Tests.Check(ExportPresetStore.Decode("invalid").Count == 0 && ExportPresetStore.Decode(new string('A', 131073)).Count == 0, "corrupt and oversized presets fall back without exception");
                IList<ExportPreset> legacyPresets = ExportPresetStore.Decode(ExportPresetStore.Encode(new List<ExportPreset> { new ExportPreset("旧版兼容", snapshot) }));
                Tests.Check(legacyPresets.Count == 1 && !legacyPresets[0].Plan.MarkMissingScreens && presetPlan.MarkMissingScreens, "presets retain explicit missing-screen annotation and legacy compatibility choice");
                using (Bitmap framed = ImageExporter.Render(Plan(ExportLayout.Framed, 1, 0, 0, null), null, bottom))
                    Tests.Check(framed.Size == PreviewBox.ExportSize, "missing framed screen keeps legacy layout and visible missing label");
            }
            using (Bitmap nativeTop = Checker(1920, 1080))
            using (Bitmap nativeBottom = Checker(1240, 1080))
            {
                Stopwatch timing = Stopwatch.StartNew();
                using (Bitmap result = ImageExporter.Render(ImageExportPlan.Default(DeviceSkin.All[1].WithLamp(true, Color.Empty, 1)), nativeTop, nativeBottom))
                {
                    timing.Stop(); result.Save(Path.Combine(Tests.Root, "image-export-framed-synthetic.png"), ImageFormat.Png);
                    File.WriteAllText(Path.Combine(Tests.Root, "image-export-performance.txt"), "Native synthetic checkerboard, single cold framed export, wall milliseconds=" + timing.ElapsedMilliseconds + ", size=" + result.Size + ", excludes save; not UI or device performance.");
                    Tests.Check(result.Size == PreviewBox.ExportSize, "native synthetic export stays within output memory budget");
                }
                Tests.Reject(delegate { ImageExporter.MeasureOutput(Plan(ExportLayout.Framed, 4, 0, 0, null), nativeTop.Size, nativeBottom.Size); }, "oversized native framed integer preset fails before rendering");
                Tests.Check(ImageExporter.MeasureOutput(Plan(ExportLayout.Single, 4, 0, 0, null), nativeTop.Size, null) == new Size(7680, 4320), "four-times native upper-screen pixels fit the preflight budget");
                Size framedDouble = ImageExporter.MeasureOutput(Plan(ExportLayout.Framed, 2, 0, 0, null), nativeTop.Size, nativeBottom.Size);
                Tests.Check((long)framedDouble.Width * framedDouble.Height <= ImageExporter.MaxPixels && framedDouble.Width > PreviewBox.ExportSize.Width, "double-pixel native dual frame expands geometry within budget");
            }
        }
        private static ImageExportPlan Plan(ExportLayout layout, int n, int gap, float radius, Size? canvas)
        { return new ImageExportPlan(layout, DeviceSkin.All[1], ExportBackground.Transparent, Color.White, Color.Black, canvas, n, gap, Color.Magenta, radius, ImageFormat.Png, 90); }
        private static Bitmap Checker(int w, int h)
        {
            Bitmap image = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) image.SetPixel(x, y, (x + y) % 2 == 0 ? Color.Red : Color.Blue);
            return image;
        }
        private sealed class ImmediateProgress : IProgress<int>
        {
            private readonly Action<int> callback;
            internal ImmediateProgress(Action<int> callback) { this.callback = callback; }
            public void Report(int value) { callback(value); }
        }
        private static bool PixelBlocks(Bitmap source, Bitmap output, int n)
        {
            if (output.Size != new Size(source.Width * n, source.Height * n)) return false;
            for (int y = 0; y < output.Height; y++) for (int x = 0; x < output.Width; x++) if (output.GetPixel(x, y).ToArgb() != source.GetPixel(x / n, y / n).ToArgb()) return false;
            return true;
        }
        private static bool Equal(Bitmap a, Bitmap b)
        {
            if (a.Size != b.Size) return false;
            Rectangle r = new Rectangle(Point.Empty, a.Size);
            BitmapData ad = a.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb), bd = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] aa = new byte[r.Width * 4], bb = new byte[aa.Length];
                for (int y = 0; y < r.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(ad.Scan0, y * ad.Stride), aa, 0, aa.Length); Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), bb, 0, bb.Length);
                    for (int x = 0; x < aa.Length; x++) if (aa[x] != bb[x]) { Console.WriteLine("Pixel mismatch at " + (x / 4) + "," + y + " channel " + (x % 4) + ": " + aa[x] + "/" + bb[x]); return false; }
                }
                return true;
            }
            finally { a.UnlockBits(ad); b.UnlockBits(bd); }
        }
    }
}
