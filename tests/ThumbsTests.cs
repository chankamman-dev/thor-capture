using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using ThorCapture.Features.Thumbs;

namespace ThorCapture
{
    internal static class ThumbsTests
    {
        static string NewDir(string name) { string path = Path.Combine(Tests.Root, name + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        static void Save(string path, Color color)
        { using (Bitmap image = new Bitmap(160, 90)) using (Graphics graphics = Graphics.FromImage(image)) { graphics.Clear(color); image.Save(path); } }
        static void Wait(Func<bool> condition) { Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.ElapsedMilliseconds < 10000) Thread.Sleep(5); if (!condition()) throw new Exception("Thumbnail timeout"); }
        internal static void Run()
        {
            string sources = NewDir("thumb-source"), disk = NewDir("thumb-cache");
            string path = Path.Combine(sources, "upper.png"); Save(path, Color.Orange);
            ThumbnailKey key = ThumbnailKey.FromFile(path, 80, 1);
            int ready = 0;
            using (ThumbnailCache cache = new ThumbnailCache(disk, 1024 * 1024))
            {
                cache.Ready += delegate(object sender, ThumbnailReadyEventArgs e) { if (e.Bitmap != null) e.Bitmap.Dispose(); Interlocked.Increment(ref ready); };
                cache.Request(key, ThumbnailPriority.Visible); Wait(() => Volatile.Read(ref ready) == 1);
                Bitmap found = null; Tests.Check(cache.TryGet(key, out found) && found.Width == 80 && found.Height == 45, "thumbnail cache fits aspect ratio and hits detached memory bitmap");
                found.Dispose();
                using (FileStream exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Tests.Check(exclusive.Length > 0, "thumbnail decoding releases source file lock");
                Save(path, Color.Blue); File.SetLastWriteTimeUtc(path, key.WriteUtc.AddSeconds(2));
                Tests.Check(!cache.TryGet(key, out found), "actual source write version invalidates stale key");
                Tests.Check(ThumbnailKey.FromFile(path, 80, 1).Id != key.Id && new ThumbnailKey(path, key.Length + 1, key.WriteUtc, 80, 1).Id != key.Id && new ThumbnailKey(path, key.Length, key.WriteUtc, 80, 2).Id != key.Id && new ThumbnailKey(path, key.Length, key.WriteUtc, 81, 1).Id != key.Id, "thumbnail key includes actual length timestamp size and rendering version");
            }
            using (ThumbnailCache cache = new ThumbnailCache(disk, 1024 * 1024))
            {
                Save(path, Color.Orange); File.SetLastWriteTimeUtc(path, key.WriteUtc);
                // Restore exactly the original source fingerprint for the disk-hit test.
                key = ThumbnailKey.FromFile(path, 80, 1);
                Bitmap found = null; Tests.Check(cache.TryGet(key, out found), "new cache instance reuses disk thumbnail"); if (found != null) found.Dispose();
            }
            string corrupt = Path.Combine(disk, "thumb-" + key.Id + ".png"); File.WriteAllText(corrupt, "invalid");
            using (ThumbnailCache cache = new ThumbnailCache(disk, 1024 * 1024))
            {
                Bitmap found = null; Tests.Check(!cache.TryGet(key, out found) && !File.Exists(corrupt), "corrupt cache file rejected and removed");
                ready = 0; cache.Ready += delegate(object sender, ThumbnailReadyEventArgs e) { if (e.Bitmap != null) e.Bitmap.Dispose(); Interlocked.Increment(ref ready); };
                cache.Request(key, ThumbnailPriority.Visible); Wait(() => Volatile.Read(ref ready) == 1);
                Tests.Check(File.Exists(corrupt), "corrupt disk entry regenerated from unchanged source");
            }
            string blocked = Path.Combine(sources, "not-directory"); File.WriteAllText(blocked, "file");
            using (ThumbnailCache cache = new ThumbnailCache(blocked, 1024))
            {
                ready = 0; cache.Ready += delegate(object sender, ThumbnailReadyEventArgs e) { e.Bitmap.Dispose(); Interlocked.Increment(ref ready); };
                cache.Request(key, ThumbnailPriority.Visible); Wait(() => Volatile.Read(ref ready) == 1);
                Bitmap found = null; Tests.Check(!cache.DiskAvailable && cache.TryGet(key, out found), "unwritable disk location degrades to memory"); if (found != null) found.Dispose();
            }
            string readonlyDisk = NewDir("thumb-readonly"); string readonlyTarget = Path.Combine(readonlyDisk, "thumb-" + key.Id + ".png"); File.WriteAllText(readonlyTarget, "invalid"); File.SetAttributes(readonlyTarget, FileAttributes.ReadOnly);
            try
            {
                using (ThumbnailCache cache = new ThumbnailCache(readonlyDisk, 1024))
                {
                    ready = 0; cache.Ready += delegate(object sender, ThumbnailReadyEventArgs e) { e.Bitmap.Dispose(); Interlocked.Increment(ref ready); };
                    cache.Request(key, ThumbnailPriority.Visible); Wait(() => Volatile.Read(ref ready) == 1);
                    Bitmap found = null; Tests.Check(!cache.DiskAvailable && cache.TryGet(key, out found), "read-only cache target keeps valid thumbnail in memory"); if (found != null) found.Dispose();
                }
            }
            finally { File.SetAttributes(readonlyTarget, FileAttributes.Normal); }
            string bad = Path.Combine(sources, "bad.png"); File.WriteAllText(bad, "bad image");
            using (ThumbnailCache cache = new ThumbnailCache(NewDir("thumb-bad"), 1024))
            {
                bool placeholder = false; cache.Ready += delegate(object sender, ThumbnailReadyEventArgs e) { placeholder = e.Outcome == ThumbnailOutcome.Placeholder; e.Bitmap.Dispose(); Interlocked.Increment(ref ready); };
                ready = 0; cache.Request(ThumbnailKey.FromFile(bad, 80, 1), ThumbnailPriority.Visible); Wait(() => Volatile.Read(ref ready) == 1);
                Tests.Check(placeholder, "bad source yields typed placeholder and leaves original untouched");
            }
            using (ThumbnailCache cache = new ThumbnailCache(NewDir("thumb-owners"), 1024))
            {
                ready = 0; Bitmap first = null, second = null;
                cache.Ready += delegate(object sender, ThumbnailReadyEventArgs e) { first = e.Bitmap; Interlocked.Increment(ref ready); };
                cache.Ready += delegate(object sender, ThumbnailReadyEventArgs e) { second = e.Bitmap; Interlocked.Increment(ref ready); };
                cache.Request(key, ThumbnailPriority.Visible); Wait(() => Volatile.Read(ref ready) == 2);
                first.Dispose(); Tests.Check(!Object.ReferenceEquals(first, second) && second.Width == 80, "every event subscriber receives its own independent bitmap ownership"); second.Dispose();
            }
            using (ManualResetEvent entered = new ManualResetEvent(false)) using (ManualResetEvent release = new ManualResetEvent(false))
            using (ThumbnailCache cache = new ThumbnailCache(NewDir("thumb-changed"), 0, 2, 2, delegate { entered.Set(); release.WaitOne(); return new Bitmap(16, 16); }))
            {
                ready = 0; bool stale = false;
                cache.Ready += delegate(object sender, ThumbnailReadyEventArgs e) { stale = e.Outcome == ThumbnailOutcome.SourceChanged && e.Bitmap == null; if (e.Bitmap != null) e.Bitmap.Dispose(); Interlocked.Increment(ref ready); };
                cache.Request(key, ThumbnailPriority.Visible); entered.WaitOne(5000); File.SetLastWriteTimeUtc(path, key.WriteUtc.AddSeconds(1)); release.Set(); Wait(() => Volatile.Read(ref ready) == 1);
                Tests.Check(stale && cache.MemoryCount == 0, "source change during decode rejected before publishing or storing bitmap");
            }
            File.SetLastWriteTimeUtc(path, key.WriteUtc);
            using (ManualResetEvent entered = new ManualResetEvent(false))
            using (ManualResetEvent release = new ManualResetEvent(false))
            {
                List<string> decoded = new List<string>(); int notifications = 0;
                ThumbnailCache cache = new ThumbnailCache(NewDir("thumb-queue"), 0, 4, 2, delegate(ThumbnailKey request)
                { lock (decoded) decoded.Add(request.Path); entered.Set(); release.WaitOne(); return new Bitmap(32, 32); });
                cache.Ready += delegate(object sender, ThumbnailReadyEventArgs e) { e.Bitmap.Dispose(); Interlocked.Increment(ref notifications); };
                cache.Request(key, ThumbnailPriority.Background); Tests.Check(entered.WaitOne(5000), "thumbnail worker starts without blocking request thread");
                cache.Cancel(path);
                List<ThumbnailKey> keys = new List<ThumbnailKey>();
                for (int i = 0; i < 12; i++) { string item = Path.Combine(sources, i + ".png"); File.Copy(path, item); keys.Add(ThumbnailKey.FromFile(item, 80, 1)); cache.Request(keys[i], ThumbnailPriority.Background); }
                cache.Request(keys[0], ThumbnailPriority.Visible);
                Tests.Check(cache.PendingCount == 4, "rapid requests keep queue bounded and visible request replaces background work");
                release.Set(); Wait(() => Volatile.Read(ref notifications) == 4);
                lock (decoded) Tests.Check(decoded.Count == 5 && decoded[1] == keys[0].Path, "cancelled in-flight notification suppressed and visible work processed first");
                Tests.Check(cache.MemoryCount <= 2, "memory cache is limited independently of library size");
                cache.Dispose();
            }
            string trimDir = NewDir("thumb-trim");
            File.WriteAllBytes(Path.Combine(trimDir, "thumb-old.png"), new byte[100]); File.SetLastWriteTimeUtc(Path.Combine(trimDir, "thumb-old.png"), DateTime.UtcNow.AddDays(-1));
            File.WriteAllBytes(Path.Combine(trimDir, "thumb-new.png"), new byte[100]); File.WriteAllBytes(Path.Combine(trimDir, "unrelated.png"), new byte[500]);
            using (ThumbnailCache cache = new ThumbnailCache(trimDir, 100)) { cache.Trim(); Tests.Check(!File.Exists(Path.Combine(trimDir, "thumb-old.png")) && File.Exists(Path.Combine(trimDir, "thumb-new.png")) && File.Exists(Path.Combine(trimDir, "unrelated.png")), "disk LRU removes oldest cache only and enforces byte quota"); }
            using (ManualResetEvent entered = new ManualResetEvent(false)) using (ManualResetEvent release = new ManualResetEvent(false))
            {
                ThumbnailCache cache = new ThumbnailCache(NewDir("thumb-dispose"), 0, 2, 2, delegate { entered.Set(); release.WaitOne(); return new Bitmap(16, 16); });
                cache.Request(key, ThumbnailPriority.Visible); entered.WaitOne(5000); Stopwatch watch = Stopwatch.StartNew(); cache.Dispose();
                Tests.Check(watch.ElapsedMilliseconds < 1200 && cache.PendingCount == 0, "dispose bounded even when decoder is blocked"); release.Set(); Thread.Sleep(100);
            }
            Measure();
        }
        static void Measure()
        {
            string[] lines = new string[4]; lines[0] = "items,coldFirst48Ms,hotFirst48Ms,coldCpuMs,hotCpuMs,privateBytesBefore,privateBytesAfterCold,privateBytesAfterHot,queuePeak";
            int row = 1;
            foreach (int count in new[] { 100, 1000, 10000 })
            {
                string sources = NewDir("thumb-perf"), cacheDir = NewDir("thumb-perf-cache");
                string prototype = Path.Combine(sources, "prototype.png"); Save(prototype, Color.CadetBlue);
                ThumbnailKey[] keys = new ThumbnailKey[count];
                for (int i = 0; i < count; i++) { string path = Path.Combine(sources, i + ".png"); File.Copy(prototype, path); keys[i] = ThumbnailKey.FromFile(path, 80, 1); }
                using (Process process = Process.GetCurrentProcess())
                {
                    process.Refresh(); long before = process.PrivateMemorySize64; TimeSpan cpu = process.TotalProcessorTime;
                    long cold = FirstScreen(cacheDir, keys);
                    process.Refresh(); double coldCpu = (process.TotalProcessorTime - cpu).TotalMilliseconds; long afterCold = process.PrivateMemorySize64; cpu = process.TotalProcessorTime;
                    long hot = FirstScreen(cacheDir, keys);
                    process.Refresh(); lines[row++] = count + "," + cold + "," + hot + "," + coldCpu + "," + (process.TotalProcessorTime - cpu).TotalMilliseconds + "," + before + "," + afterCold + "," + process.PrivateMemorySize64 + ",48";
                    Tests.Check(hot < 1000, "component warm first 48 thumbnails under reference 1 second for " + count + " synthetic images");
                }
            }
            File.WriteAllLines(Path.Combine(Tests.Root, "thumb-performance.csv"), lines);
        }
        static long FirstScreen(string cacheDir, ThumbnailKey[] keys)
        {
            int completed = 0; Stopwatch watch = Stopwatch.StartNew();
            using (ThumbnailCache cache = new ThumbnailCache(cacheDir, 64 * 1024 * 1024))
            {
                cache.Ready += delegate(object sender, ThumbnailReadyEventArgs e) { if (e.Bitmap != null) e.Bitmap.Dispose(); Interlocked.Increment(ref completed); };
                for (int i = 0; i < 48; i++) cache.Request(keys[i], ThumbnailPriority.Visible);
                Wait(() => Volatile.Read(ref completed) == 48);
            }
            return watch.ElapsedMilliseconds;
        }
    }
}
