using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using ThorCapture.Features.Similarity;

namespace ThorCapture
{
    internal static class SimilarityTests
    {
        static string Save(string dir, string name, Bitmap bitmap, ImageFormat format)
        { string path = Path.Combine(dir, name); bitmap.Save(path, format); return path; }
        internal static void Run()
        {
            string dir = Path.Combine(Tests.Root, "similarity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            string png, bmp, copy, lower, large, changed, black, black2, red, bad, video;
            using (Bitmap image = new Bitmap(90, 80))
            {
                using (Graphics graphics = Graphics.FromImage(image))
                { graphics.Clear(Color.Navy); graphics.FillRectangle(Brushes.White, 15, 10, 20, 40); graphics.FillEllipse(Brushes.Goldenrod, 45, 30, 30, 30); }
                png = Save(dir, "thor-upper-base.png", image, ImageFormat.Png);
                bmp = Save(dir, "thor-upper-base.bmp", image, ImageFormat.Bmp);
                copy = Path.Combine(dir, "thor-upper-copy.png"); File.Copy(png, copy);
                lower = Path.Combine(dir, "thor-lower-copy.png"); File.Copy(png, lower);
                using (Bitmap resized = new Bitmap(image, 180, 160)) large = Save(dir, "thor-upper-large.png", resized, ImageFormat.Png);
                using (Graphics graphics = Graphics.FromImage(image)) graphics.FillRectangle(Brushes.Red, 80, 70, 5, 5);
                changed = Save(dir, "thor-upper-changed.png", image, ImageFormat.Png);
            }
            using (Bitmap image = new Bitmap(90, 80))
            {
                using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.Black);
                black = Save(dir, "thor-upper-black.png", image, ImageFormat.Png); black2 = Save(dir, "thor-upper-black.bmp", image, ImageFormat.Bmp);
                using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.Red);
                red = Save(dir, "thor-upper-red.png", image, ImageFormat.Png);
            }
            bad = Path.Combine(dir, "bad.png"); File.WriteAllText(bad, "invalid"); video = Path.Combine(dir, "same.mp4"); File.Copy(png, video);
            SimilarityResult result = SimilarityFinder.FindAsync(new[] { png, copy, bmp, lower, large, changed, black, black2, red, bad, video }, new SimilarityOptions(), null, CancellationToken.None).Result;
            Tests.Check(result.Groups.Any(g => g.Exact && g.Paths.Count == 2 && g.Paths.Contains(copy)), "exact candidates use full SHA-256 and are separate from visual similarity");
            Tests.Check(result.Groups.Any(g => !g.Exact && g.Paths.Contains(png) && g.Paths.Contains(bmp)), "same picture across formats yields visual candidate without byte identity claim");
            Tests.Check(result.Groups.Any(g => !g.Exact && g.Paths.Contains(png) && g.Paths.Contains(changed)), "minor UI change remains a review-only similarity candidate");
            Tests.Check(!result.Groups.Any(g => g.Paths.Contains(lower)), "upper and lower screen candidates isolated");
            Tests.Check(!result.Groups.Any(g => g.Paths.Contains(large)), "different source dimensions isolated by default");
            Tests.Check(result.Groups.Any(g => g.Solid && g.Paths.Contains(black) && g.Paths.Contains(black2)) && !result.Groups.Any(g => g.Paths.Contains(black) && (g.Paths.Contains(red) || g.Paths.Contains(png))), "black and same-colour solid frames separated from normal images and other colours");
            Tests.Check(result.FailedPaths.Contains(bad) && result.FailedPaths.Contains(video) && result.Scanned == 11, "bad source and video rejected with explicit failed paths");
            SimilarityResult allowed = SimilarityFinder.FindAsync(new[] { png, large }, new SimilarityOptions { AllowDifferentSizes = true }, null, CancellationToken.None).Result;
            Tests.Check(allowed.Groups.Count == 1, "different dimensions compared only with explicit permission");
            Tests.Check(ImageHash.Distance(0, 15) == 4 && ImageHash.Distance(0, UInt64.MaxValue) == 64, "dHash Hamming distance covers threshold boundary and all 64 bits");
            using (Bitmap edge = new Bitmap(9, 8))
            {
                for (int y = 0; y < 8; y++) for (int x = 0; x < 9; x++) edge.SetPixel(x, y, Color.FromArgb(200 - x * 10, 200 - x * 10, 200 - x * 10));
                string edgeA = Save(dir, "thor-upper-edgeA.png", edge, ImageFormat.Png);
                ulong original = ImageHash.DHash(edge); edge.SetPixel(0, 0, Color.Black);
                string edgeB = Save(dir, "thor-upper-edgeB.png", edge, ImageFormat.Png);
                int distance = ImageHash.Distance(original, ImageHash.DHash(edge));
                Tests.Check(distance == 1 && SimilarityFinder.FindAsync(new[] { edgeA, edgeB }, new SimilarityOptions { Threshold = 0 }, null, CancellationToken.None).Result.Groups.Count == 0, "candidate just beyond threshold is excluded");
                Tests.Check(SimilarityFinder.FindAsync(new[] { edgeA, edgeB }, new SimilarityOptions { Threshold = 1 }, null, CancellationToken.None).Result.Groups.Count == 1, "candidate exactly at threshold is included");
            }
            using (CancellationTokenSource cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); SimilarityResult stopped = SimilarityFinder.FindAsync(new[] { png, bmp }, new SimilarityOptions(), null, cancelled.Token).Result;
                Tests.Check(stopped.Cancelled && stopped.Groups.Count == 0, "cancelled analysis returns typed cancellation without partial candidate claims");
            }
            SimilarityResult limited = SimilarityFinder.FindAsync(new[] { png, copy, bmp }, new SimilarityOptions { MaximumItems = 2 }, null, CancellationToken.None).Result;
            Tests.Check(limited.LimitReached && limited.Scanned == 2, "analysis item limit produces explicit incomplete scan");
            SimilarityResult budget = SimilarityFinder.FindAsync(new[] { png, copy, bmp }, new SimilarityOptions { MaximumComparisons = 1 }, null, CancellationToken.None).Result;
            Tests.Check(budget.LimitReached, "quadratic comparisons are bounded and incompleteness remains visible");
            using (FileStream source = File.Open(png, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Tests.Check(source.Length > 0, "similarity analysis releases source handles and never edits original");
            Tests.Reject(delegate { SimilarityFinder.FindAsync(new[] { png }, new SimilarityOptions { Threshold = 17 }, null, CancellationToken.None); }, "similarity threshold outside safe range rejected");
            using (FileStream writer = File.Open(png, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                Tests.Check(ImageHash.ExactSha256(png, CancellationToken.None).Length == 64 && SimilarityFinder.FindAsync(new[] { png, bmp }, new SimilarityOptions(), null, CancellationToken.None).Result.Groups.Count == 1, "hash and image scans allow an external write stream to remain open");
            }
            long originalLength = new FileInfo(png).Length;
            SimilarityOptions changing = new SimilarityOptions { RoleResolver = delegate(string file)
            {
                if (file == png) using (FileStream writer = File.Open(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { writer.Position = writer.Length; writer.WriteByte(0); }
                return "upper";
            } };
            SimilarityResult changedSource = SimilarityFinder.FindAsync(new[] { png, bmp }, changing, null, CancellationToken.None).Result;
            Tests.Check(changedSource.FailedPaths.Contains(png) && changedSource.Groups.Count == 0 && new FileInfo(png).Length == originalLength + 1, "concurrent source mutation succeeds but changed fingerprint is never published as a candidate");
        }
    }
}
