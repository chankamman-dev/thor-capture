using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Features.Similarity
{
    public static class ImageHash
    {
        // Full SHA-256 is retained for exact comparisons; truncated values are never used as identity.
        public static string ExactSha256(string path, CancellationToken token)
        {
            using (FileStream file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (SHA256 hash = SHA256.Create())
            {
                byte[] buffer = new byte[65536]; int length;
                while ((length = file.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); hash.TransformBlock(buffer, 0, length, null, 0); }
                hash.TransformFinalBlock(buffer, 0, 0);
                return BitConverter.ToString(hash.Hash).Replace("-", "");
            }
        }
        public static ulong DHash(Image image)
        {
            using (Bitmap sample = new Bitmap(9, 8))
            {
                using (Graphics graphics = Graphics.FromImage(sample)) { graphics.Clear(Color.White); graphics.DrawImage(image, 0, 0, 9, 8); }
                ulong hash = 0;
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                    if (Luma(sample.GetPixel(x, y)) > Luma(sample.GetPixel(x + 1, y))) hash |= 1UL << (y * 8 + x);
                return hash;
            }
        }
        internal static int Luma(Color color) { return (color.R * 299 + color.G * 587 + color.B * 114) / 1000; }
        public static int Distance(ulong a, ulong b)
        { ulong value = a ^ b; int count = 0; while (value != 0) { value &= value - 1; count++; } return count; }
    }
    public sealed class SimilarGroup
    {
        public readonly List<string> Paths = new List<string>();
        public int MaxDistance;
        public string Reason;
        public bool Exact;
        public bool Solid;
        public string Role;
    }
    public sealed class SimilarityResult
    {
        public readonly List<SimilarGroup> Groups = new List<SimilarGroup>();
        public readonly List<string> FailedPaths = new List<string>();
        public bool Cancelled;
        public bool LimitReached;
        public int Scanned;
    }
    public sealed class SimilarityOptions
    {
        public int Threshold = 4;
        public bool AllowDifferentSizes;
        public int MaximumItems = 10000;
        public int MaximumComparisons = 2000000;
        public Func<string, string> RoleResolver;
    }
    public static class SimilarityFinder
    {
        sealed class Fingerprint
        {
            internal string Path, Exact, Role;
            internal ulong Hash;
            internal int Width, Height, SolidColor;
            internal bool Solid;
        }
        // Unknown roles are kept isolated from known screens. Track A should supply metadata roles.
        static string Role(string path)
        {
            string name = System.IO.Path.GetFileName(path).ToLowerInvariant();
            return name.Contains("-upper-") ? "upper" : name.Contains("-lower-") ? "lower" : "unknown";
        }
        static bool IsSolid(Image image, out int color)
        {
            using (Bitmap sample = new Bitmap(16, 16))
            {
                using (Graphics graphics = Graphics.FromImage(sample)) { graphics.Clear(Color.White); graphics.DrawImage(image, 0, 0, 16, 16); }
                Color first = sample.GetPixel(0, 0); color = first.ToArgb() & 0xffffff;
                for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
                {
                    Color pixel = sample.GetPixel(x, y);
                    if (Math.Abs(pixel.R - first.R) > 3 || Math.Abs(pixel.G - first.G) > 3 || Math.Abs(pixel.B - first.B) > 3) return false;
                }
                return true;
            }
        }
        public static Task<SimilarityResult> FindAsync(IList<string> images, SimilarityOptions options, IProgress<int> progress, CancellationToken token)
        {
            if (images == null || options == null) throw new ArgumentNullException("options");
            if (options.Threshold < 0 || options.Threshold > 16 || options.MaximumItems < 1 || options.MaximumComparisons < 1) throw new ArgumentOutOfRangeException("options");
            // Snapshot inputs and options before handing them to the worker.
            string[] paths = images.ToArray();
            SimilarityOptions fixedOptions = new SimilarityOptions { Threshold = options.Threshold, AllowDifferentSizes = options.AllowDifferentSizes,
                MaximumItems = options.MaximumItems, MaximumComparisons = options.MaximumComparisons, RoleResolver = options.RoleResolver };
            return Task.Run(() => Find(paths, fixedOptions, progress, token));
        }
        static SimilarityResult Find(string[] paths, SimilarityOptions options, IProgress<int> progress, CancellationToken token)
        {
            SimilarityResult result = new SimilarityResult();
            List<Fingerprint> items = new List<Fingerprint>();
            try
            {
                string[] distinct = paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(options.MaximumItems).ToArray();
                result.LimitReached = paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() > options.MaximumItems;
                foreach (string path in distinct)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
                        if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".bmp" && extension != ".gif") throw new InvalidDataException();
                        FileInfo before = new FileInfo(path); long size = before.Length; DateTime modified = before.LastWriteTimeUtc;
                        string exact = ImageHash.ExactSha256(path, token);
                        using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (Image image = Image.FromStream(stream))
                        {
                            int color; bool solid = IsSolid(image, out color);
                            Fingerprint item = new Fingerprint { Path = path, Exact = exact, Role = options.RoleResolver == null ? Role(path) : options.RoleResolver(path),
                                Width = image.Width, Height = image.Height, Hash = ImageHash.DHash(image), Solid = solid, SolidColor = color };
                            FileInfo after = new FileInfo(path);
                            if (size != after.Length || modified != after.LastWriteTimeUtc) throw new IOException("Changed source");
                            items.Add(item);
                        }
                    }
                    catch (Exception ex)
                    { if (!(ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is OutOfMemoryException || ex is System.Runtime.InteropServices.ExternalException)) throw; result.FailedPaths.Add(path); }
                    result.Scanned++; if (progress != null) progress.Report(result.Scanned * 70 / Math.Max(1, distinct.Length));
                }
                int comparisons = 0;
                foreach (IGrouping<string, Fingerprint> exact in items.GroupBy(i => i.Role + "|" + i.Exact).Where(g => g.Count() > 1))
                {
                    SimilarGroup group = new SimilarGroup { Exact = true, Solid = exact.First().Solid, Role = exact.First().Role,
                        Reason = "文件字节完全相同（SHA-256）" };
                    group.Paths.AddRange(exact.Select(i => i.Path)); result.Groups.Add(group);
                }
                // Complete-link clusters prevent A~B~C chains from implying that A resembles C.
                List<List<Fingerprint>> clusters = new List<List<Fingerprint>>();
                Dictionary<List<Fingerprint>, int> distances = new Dictionary<List<Fingerprint>, int>();
                foreach (Fingerprint item in items)
                {
                    token.ThrowIfCancellationRequested();
                    List<Fingerprint> match = null;
                    int matchDistance = 0;
                    foreach (List<Fingerprint> cluster in clusters)
                    {
                        if (cluster[0].Role != item.Role || cluster[0].Solid != item.Solid) continue;
                        if (!options.AllowDifferentSizes && (cluster[0].Width != item.Width || cluster[0].Height != item.Height)) continue;
                        if (item.Solid && cluster[0].SolidColor != item.SolidColor) continue;
                        bool fits = true;
                        int candidateDistance = distances[cluster];
                        foreach (Fingerprint member in cluster)
                        {
                            token.ThrowIfCancellationRequested();
                            if (++comparisons > options.MaximumComparisons) { result.LimitReached = true; fits = false; break; }
                            int distance = ImageHash.Distance(item.Hash, member.Hash);
                            candidateDistance = Math.Max(candidateDistance, distance);
                            if (distance > options.Threshold) { fits = false; break; }
                        }
                        if (comparisons > options.MaximumComparisons) break;
                        if (fits) { match = cluster; matchDistance = candidateDistance; break; }
                    }
                    if (comparisons > options.MaximumComparisons) break;
                    if (match == null) { match = new List<Fingerprint>(); clusters.Add(match); distances.Add(match, 0); }
                    else distances[match] = matchDistance;
                    match.Add(item);
                }
                foreach (List<Fingerprint> cluster in clusters.Where(c => c.Count > 1))
                {
                    token.ThrowIfCancellationRequested();
                    SimilarGroup group = new SimilarGroup { Exact = cluster.All(i => i.Exact == cluster[0].Exact), Solid = cluster[0].Solid, Role = cluster[0].Role };
                    if (group.Exact) continue; // Already represented by the full SHA-256 group.
                    group.Paths.AddRange(cluster.Select(i => i.Path));
                    // Retain the maximum already computed within the single comparison budget.
                    group.MaxDistance = distances[cluster];
                    group.Reason = group.Exact ? "文件字节完全相同（SHA-256）" : group.Solid ? "纯色画面候选（与普通画面隔离）" : "视觉相似候选（dHash距离 " + group.MaxDistance + "），需要人工检查";
                    result.Groups.Add(group);
                }
                if (progress != null) progress.Report(100);
            }
            catch (OperationCanceledException) { result.Cancelled = true; result.Groups.Clear(); }
            return result;
        }
    }
}
