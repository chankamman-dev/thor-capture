using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using ThorCapture.Features.Editing;

namespace ThorCapture
{
    internal static class EditingTests
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr reserved);
        static string Hash(string path) { using (FileStream file = File.OpenRead(path)) using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", ""); }
        static bool Equal(Bitmap a, Bitmap b)
        {
            if (a.Size != b.Size) return false;
            for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++) if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb()) return false;
            return true;
        }
        static int NonTransparent(Bitmap bitmap, Rectangle area)
        { int count = 0; for (int y = area.Top; y < area.Bottom; y++) for (int x = area.Left; x < area.Right; x++) if (bitmap.GetPixel(x, y).A > 0) count++; return count; }
        internal static void Run()
        {
            string dir = Path.Combine(Tests.Root, "editing-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            string source = Path.Combine(dir, "源截图.png");
            using (Bitmap bitmap = new Bitmap(120, 80))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Transparent);
                for (int y = 10; y < 70; y++) for (int x = 10; x < 110; x++) bitmap.SetPixel(x, y, Color.FromArgb(255, x * 2, y * 3, 50));
                bitmap.Save(source, ImageFormat.Png);
            }
            string originalHash = Hash(source);
            using (FileStream writer = File.Open(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            using (EditDocument document = new EditDocument(source)) Tests.Check(document.CanvasSize == new Size(120, 80), "editor load permits an existing external write handle");
            using (EditDocument document = new EditDocument(source))
            {
                using (FileStream exclusive = File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Tests.Check(exclusive.Length > 0, "loaded editor never retains a source file lock");
                document.Apply(new CoverOp(new Rectangle(20, 20, 30, 20), CoverStyle.Solid, Color.FromArgb(0, 255, 20, 60), 24));
                using (Bitmap covered = document.Render(1))
                {
                    bool all = true; for (int y = 20; y < 40; y++) for (int x = 20; x < 50; x++) all &= covered.GetPixel(x, y).ToArgb() == Color.FromArgb(255, 255, 20, 60).ToArgb();
                    Tests.Check(all && covered.GetPixel(0, 0).A == 0, "solid privacy cover forces fully opaque replacement while preserving outside alpha");
                }
                Tests.Check(document.CanUndo && document.Undo() && document.CanRedo, "edit history exposes undo and redo state");
                using (Bitmap before = document.Render(1)) using (Bitmap sourceBitmap = new Bitmap(source)) Tests.Check(Equal(before, sourceBitmap), "undo restores original pixel content");
                document.Redo();
                string saved = Path.Combine(dir, "新编辑.png"); EditResult result = document.SaveAs(saved, ImageFormat.Png);
                using (Bitmap expected = document.Render(1)) using (Bitmap exported = new Bitmap(saved)) Tests.Check(result.Outcome == EditOutcome.Success && Equal(expected, exported), "Render scale one and flattened PNG export are pixel-identical");
                Tests.Check(result.Derivation.SourceSha256 == originalHash && result.Derivation.Operations.Count == 1 && result.Derivation.OutputSize == document.CanvasSize, "derived metadata records actual source SHA and immutable operation summary");
                string targetHash = Hash(saved);
                Tests.Check(document.SaveAs(source, ImageFormat.Png).Outcome == EditOutcome.InvalidTarget && document.SaveAs(Path.Combine(dir, ".", "源截图.png"), ImageFormat.Png).Outcome == EditOutcome.InvalidTarget && document.SaveAs(source.ToUpperInvariant(), ImageFormat.Png).Outcome == EditOutcome.InvalidTarget, "source path aliases including case and dot segments are rejected");
                string alias = Path.Combine(dir, "hardlink.png");
                bool hardlink = CreateHardLink(alias, source, IntPtr.Zero);
                Tests.Check(hardlink && document.SaveAs(alias, ImageFormat.Png).Outcome == EditOutcome.InvalidTarget && Hash(alias) == originalHash, "existing hard-link alias of source cannot be overwritten");
                Tests.Check(document.SaveAs(saved, ImageFormat.Png).Outcome == EditOutcome.InvalidTarget && Hash(saved) == targetHash, "existing target is never overwritten");
                Tests.Check(document.SaveAs(source + ":alternate.png", ImageFormat.Png).Outcome == EditOutcome.InvalidTarget, "NTFS alternate-stream targets are rejected before writing");
                Tests.Check(document.SaveAs(Path.Combine(dir, "wrong.jpg"), ImageFormat.Jpeg).Outcome == EditOutcome.UnsupportedFormat && !File.Exists(Path.Combine(dir, "wrong.jpg")), "unsupported lossy export returns typed unsupported without creating output");
                string failed = Path.Combine(dir, "missing", "output.png");
                Tests.Check(document.SaveAs(failed, ImageFormat.Png).Outcome == EditOutcome.Failed && !File.Exists(failed) && Directory.GetFiles(dir, ".thor-edit-*.tmp").Length == 0, "save failure preserves source and cleans up unpublished temporary output");
                using (CancellationTokenSource cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel(); string cancelledTarget = Path.Combine(dir, "cancel.png");
                    Tests.Check(document.SaveAsAsync(cancelledTarget, ImageFormat.Png, cancelled.Token).Result.Outcome == EditOutcome.Cancelled && !File.Exists(cancelledTarget), "cancelled save publishes no file");
                    Tests.Check(document.RenderAsync(1, cancelled.Token).Result.Outcome == EditOutcome.Cancelled, "cancelled rendering returns typed cancellation");
                    Tests.Check(EditDocument.OpenAsync(source, cancelled.Token).Result.Outcome == EditOutcome.Cancelled, "cancelled image opening returns typed cancellation");
                }
                document.Undo(); document.Apply(new CoverOp(new Rectangle(0, 0, 5, 5)));
                Tests.Check(!document.CanRedo && document.Derivation.Operations.Count == 1, "new operation after undo discards redo branch without mutable history");
            }
            using (EditDocument document = new EditDocument(source))
            {
                document.Apply(new CropOp(new Rectangle(10, 10, 40, 30))); document.Apply(new RotateOp(1)); document.Apply(new CoverOp(new Rectangle(1, 2, 4, 5), CoverStyle.Solid, Color.Magenta, 24));
                using (Bitmap rendered = document.Render(1))
                {
                    Tests.Check(rendered.Size == new Size(30, 40) && rendered.GetPixel(1, 2).ToArgb() == Color.Magenta.ToArgb() && rendered.GetPixel(0, 0).ToArgb() == Color.FromArgb(255, 20, 117, 50).ToArgb(), "crop then clockwise rotation then cover uses current pixel canvas coordinates");
                    string combined = Path.Combine(dir, "组合坐标.png"); document.SaveAs(combined, ImageFormat.Png);
                    using (Bitmap exported = new Bitmap(combined)) Tests.Check(Equal(rendered, exported), "combined geometry preview and export are identical");
                }
                Tests.Check(document.Undo() && document.Undo() && document.CanvasSize == new Size(40, 30) && document.Redo() && document.CanvasSize == new Size(30, 40), "canvas dimensions follow undo and redo through geometry operations");
                document.Apply(new CropOp(new Rectangle(-2, -2, 10, 10)));
                Tests.Check(document.CanvasSize == new Size(8, 8), "partially out-of-bounds crop is safely intersected with canvas");
                Tests.Reject(delegate { document.Apply(new CropOp(new Rectangle(1000, 0, 4, 4))); }, "fully out-of-canvas area rejected without mutating history");
            }
            using (EditDocument document = new EditDocument(source))
            {
                document.Apply(new CoverOp(new Rectangle(10, 10, 10, 8), CoverStyle.Pixelate, Color.Black, 4));
                using (Bitmap rendered = document.Render(1))
                {
                    bool blocks = true;
                    for (int by = 10; by < 18; by += 4) for (int bx = 10; bx < 20; bx += 4)
                    { int color = rendered.GetPixel(bx, by).ToArgb(); for (int y = by; y < Math.Min(18, by + 4); y++) for (int x = bx; x < Math.Min(20, bx + 4); x++) blocks &= rendered.GetPixel(x, y).ToArgb() == color; }
                    Tests.Check(blocks && rendered.GetPixel(10, 10).R == 23 && rendered.GetPixel(10, 10).G == 34 && rendered.GetPixel(9, 10).A == 0, "pixelation averages exact blocks and handles partial edge blocks without bleeding outside area");
                }
            }
            using (EditDocument document = new EditDocument(source))
            {
                document.Apply(new ArrowOp(new PointF(-10, -10), new PointF(35, 20), Color.Red, 3)); document.Apply(new BoxOp(new Rectangle(50, 10, 30, 20), Color.Lime, 2));
                document.Apply(new TextOp(new PointF(10, 40), "中文截图", Color.White, 18));
                using (Bitmap rendered = document.Render(1))
                {
                    Tests.Check(NonTransparent(rendered, new Rectangle(0, 0, 8, 8)) > 0, "arrow endpoints clamp to canvas and render arrow stroke");
                    bool green = false; for (int x = 50; x < 80; x++) green |= rendered.GetPixel(x, 10).G > 200;
                    Tests.Check(green, "box annotation renders a coloured outline");
                    int white = 0; for (int y = 40; y < 70; y++) for (int x = 10; x < 110; x++) { Color p = rendered.GetPixel(x, y); if (p.R > 230 && p.G > 230 && p.B > 230) white++; }
                    Tests.Check(white > 30, "Chinese text renders with a system CJK font using pixel sizes");
                    string output = Path.Combine(dir, "标注.png"); document.SaveAs(output, ImageFormat.Png); using (Bitmap exported = new Bitmap(output)) Tests.Check(Equal(rendered, exported), "text arrow and box replay identically in preview and PNG export");
                    rendered.Save(Path.Combine(Tests.Root, "editing-annotations.png"), ImageFormat.Png);
                }
                EditRenderResult asynchronous = document.RenderAsync(1, CancellationToken.None).Result;
                using (Bitmap direct = document.Render(1)) using (asynchronous.Bitmap) Tests.Check(asynchronous.Outcome == EditOutcome.Success && Equal(direct, asynchronous.Bitmap), "asynchronous renderer uses the same deterministic edit replay");
                using (Bitmap small = document.Render(0.5f)) Tests.Check(small.Size == new Size(60, 40), "preview scaling occurs after source-pixel operation replay");
                Tests.Reject(delegate { document.Render(Single.NaN); }, "non-finite preview scale rejected");
                Tests.Reject(delegate { document.Apply(new ArrowOp(new PointF(Single.NaN, 0), PointF.Empty, Color.Red, 3)); }, "non-finite annotation coordinate rejected");
            }
            using (EditDocument document = new EditDocument(source))
            {
                for (int i = 0; i < EditDocument.MaximumOperations; i++) document.Apply(new RotateOp(0));
                Tests.Reject(delegate { document.Apply(new RotateOp(0)); }, "edit history has a hard operation count limit");
            }
            using (EditDocument document = new EditDocument(source))
            {
                System.Threading.Tasks.Task<EditRenderResult> rendering = document.RenderAsync(1, CancellationToken.None);
                document.Apply(new CoverOp(new Rectangle(0, 0, 20, 20)));
                EditRenderResult previous = rendering.Result;
                using (previous.Bitmap) Tests.Check(previous.Outcome == EditOutcome.Success && previous.Bitmap.GetPixel(0, 0).A == 0 && document.Derivation.Operations.Count == 1, "async render snapshots history before later edits");
                string race = Path.Combine(dir, "race.png");
                System.Threading.Tasks.Task<EditResult> a = document.SaveAsAsync(race, ImageFormat.Png, CancellationToken.None), b = document.SaveAsAsync(race, ImageFormat.Png, CancellationToken.None);
                EditResult[] results = new[] { a.Result, b.Result };
                Tests.Check(results.Count(r => r.Outcome == EditOutcome.Success) == 1 && Directory.GetFiles(dir, ".thor-edit-*.tmp").Length == 0, "concurrent saves cannot replace a target published by the other writer");
                using (Bitmap saved = new Bitmap(race)) Tests.Check(saved.GetPixel(0, 0).A == 255, "atomic save race leaves one complete flattened PNG");
            }
            EditDocument disposed = new EditDocument(source);
            System.Threading.Tasks.Task<EditRenderResult> detached = disposed.RenderAsync(1, CancellationToken.None); disposed.Dispose();
            EditRenderResult detachedResult = detached.Result;
            using (detachedResult.Bitmap) Tests.Check(detachedResult.Outcome == EditOutcome.Success && detachedResult.Bitmap.Size == new Size(120, 80), "in-flight snapshot remains valid after document disposal");
            Tests.Reject(delegate { disposed.Render(1); }, "disposed document rejects new rendering");
            string tooWide = Path.Combine(dir, "too-wide.png"); using (Bitmap image = new Bitmap(EditDocument.MaximumDimension + 1, 1)) image.Save(tooWide, ImageFormat.Png);
            Tests.Check(EditDocument.OpenAsync(tooWide, CancellationToken.None).Result.Outcome == EditOutcome.Failed, "oversized source dimension rejected before detached pixel allocation");
            string tooManyPixels = Path.Combine(dir, "too-many.png"); using (Bitmap image = new Bitmap(4001, 4000)) image.Save(tooManyPixels, ImageFormat.Png);
            Tests.Check(EditDocument.OpenAsync(tooManyPixels, CancellationToken.None).Result.Outcome == EditOutcome.Failed, "source pixel count above resource limit rejected");
            Tests.Check(Hash(source) == originalHash, "source original SHA remains unchanged through all edit undo render save and failure cases");
            Tests.Reject(delegate { new CoverOp(new Rectangle(0, 0, 10, 10), CoverStyle.Pixelate, Color.Black, 1); }, "unsafe tiny pixelation block setting rejected");
            Tests.Check(EditDocument.OpenAsync(Path.Combine(dir, "no-file.png"), CancellationToken.None).Result.Outcome == EditOutcome.Failed, "missing source reported as typed load failure");
            string renamedGif = Path.Combine(dir, "renamed-gif.png"); using (Bitmap image = new Bitmap(10, 10)) image.Save(renamedGif, ImageFormat.Gif);
            Tests.Check(EditDocument.OpenAsync(renamedGif, CancellationToken.None).Result.Outcome == EditOutcome.Failed, "renamed unsupported image format is rejected by content format");
            Measure(dir);
        }
        static void Measure(string dir)
        {
            string source = Path.Combine(dir, "large.png");
            using (Bitmap bitmap = new Bitmap(3840, 2160)) using (Graphics graphics = Graphics.FromImage(bitmap)) { graphics.Clear(Color.CadetBlue); graphics.FillEllipse(Brushes.Coral, 500, 400, 1800, 900); bitmap.Save(source, ImageFormat.Png); }
            using (EditDocument document = new EditDocument(source))
            {
                document.Apply(new CoverOp(new Rectangle(10, 10, 1800, 900), CoverStyle.Pixelate, Color.Black, 24)); document.Apply(new TextOp(new PointF(50, 1000), "编辑性能测试", Color.White, 48));
                Stopwatch watch = Stopwatch.StartNew(); long fullMs;
                using (Bitmap full = document.Render(1)) { fullMs = watch.ElapsedMilliseconds; full.Save(Path.Combine(Tests.Root, "editing-large.png"), ImageFormat.Png); }
                watch.Restart(); using (Bitmap preview = document.Render(0.25f)) { } long previewMs = watch.ElapsedMilliseconds;
                watch.Restart(); EditResult saved = document.SaveAs(Path.Combine(dir, "large-export.png"), ImageFormat.Png); long saveMs = watch.ElapsedMilliseconds;
                File.WriteAllText(Path.Combine(Tests.Root, "editing-performance.txt"), "source=3840x2160; operations=pixelate1800x900,ChineseText; fullRenderMs=" + fullMs + "; quarterPreviewMs=" + previewMs + "; saveMs=" + saveMs);
                Tests.Check(saved.Outcome == EditOutcome.Success, "large synthetic image renders and exports under configured resource limits");
                for (int i = 0; i < 20; i++) document.Apply(new CoverOp(new Rectangle(10, 10, 1800, 900), CoverStyle.Pixelate, Color.Black, 24));
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    cancel.CancelAfter(30); EditRenderResult result = document.RenderAsync(1, cancel.Token).Result;
                    Tests.Check(result.Outcome == EditOutcome.Cancelled && result.Bitmap == null, "long-running history rendering responds to cancellation without leaking a partial bitmap");
                }
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    string cancelled = Path.Combine(dir, "large-cancel.png"); cancel.CancelAfter(30);
                    Tests.Check(document.SaveAsAsync(cancelled, ImageFormat.Png, cancel.Token).Result.Outcome == EditOutcome.Cancelled && !File.Exists(cancelled) && Directory.GetFiles(dir, ".thor-edit-*.tmp").Length == 0, "long-running save cancellation does not publish a partial file");
                }
            }
        }
    }
}
