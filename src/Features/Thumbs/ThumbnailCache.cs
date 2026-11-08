using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ThorCapture.Features.Thumbs
{
    public sealed class ThumbnailKey
    {
        public readonly string Path;
        public readonly long Length;
        public readonly DateTime WriteUtc;
        public readonly int Size;
        public readonly int RenderVersion;
        public ThumbnailKey(string path, long length, DateTime writeUtc, int size, int renderVersion)
        {
            if (size < 16 || size > 1024) throw new ArgumentOutOfRangeException("size");
            Path = System.IO.Path.GetFullPath(path); Length = length; WriteUtc = writeUtc; Size = size; RenderVersion = renderVersion;
        }
        public static ThumbnailKey FromFile(string path, int size, int renderVersion)
        {
            FileInfo file = new FileInfo(path);
            return new ThumbnailKey(file.FullName, file.Length, file.LastWriteTimeUtc, size, renderVersion);
        }
        internal bool Current()
        {
            try { FileInfo file = new FileInfo(Path); return file.Exists && file.Length == Length && file.LastWriteTimeUtc == WriteUtc; }
            catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
        }
        public string Id
        {
            get
            {
                string text = Path.ToUpperInvariant() + "\n" + Length.ToString(CultureInfo.InvariantCulture) + "\n" + WriteUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "\n" + Size.ToString(CultureInfo.InvariantCulture) + "\n" + RenderVersion.ToString(CultureInfo.InvariantCulture);
                using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
            }
        }
    }
    public enum ThumbnailPriority { Background, Visible }
    public enum ThumbnailOutcome { Success, Placeholder, SourceChanged }
    public sealed class ThumbnailReadyEventArgs : EventArgs
    {
        public readonly ThumbnailKey Key;
        public readonly Bitmap Bitmap;
        public readonly ThumbnailOutcome Outcome;
        public readonly string Reason;
        internal ThumbnailReadyEventArgs(ThumbnailKey key, Bitmap bitmap, ThumbnailOutcome outcome, string reason)
        { Key = key; Bitmap = bitmap; Outcome = outcome; Reason = reason; }
    }

    /// <summary>One bounded background worker; all returned bitmaps are independent caller-owned copies.</summary>
    public sealed class ThumbnailCache : IDisposable
    {
        sealed class Work { internal ThumbnailKey Key; internal ThumbnailPriority Priority; internal bool Cancelled; }
        sealed class MemoryEntry { internal Bitmap Image; internal long Used; }
        readonly object gate = new object();
        readonly object diskGate = new object();
        readonly List<Work> queue = new List<Work>();
        readonly Dictionary<string, MemoryEntry> memory = new Dictionary<string, MemoryEntry>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread worker;
        readonly string directory;
        readonly long maxBytes;
        readonly int capacity;
        readonly int memoryCapacity;
        readonly Func<ThumbnailKey, Bitmap> decode;
        long clock;
        Work active;
        bool disposed;
        bool diskDisabled;
        public int PendingCount { get { lock (gate) return queue.Count; } }
        public int MemoryCount { get { lock (gate) return memory.Count; } }
        public bool DiskAvailable { get { lock (diskGate) return !diskDisabled; } }
        /// <summary>Raised on the background worker. Each subscriber owns its Bitmap and must dispose it. No UI calls here.</summary>
        public event EventHandler<ThumbnailReadyEventArgs> Ready;
        public ThumbnailCache(string directory, long maxBytes) : this(directory, maxBytes, 64, 32, null) { }
        public ThumbnailCache(string directory, long maxBytes, int queueCapacity, int memoryCapacity, Func<ThumbnailKey, Bitmap> decoder)
        {
            if (maxBytes < 0 || queueCapacity < 1 || memoryCapacity < 1) throw new ArgumentOutOfRangeException("capacity");
            this.directory = System.IO.Path.GetFullPath(directory); this.maxBytes = maxBytes;
            capacity = queueCapacity; this.memoryCapacity = memoryCapacity; decode = decoder ?? Decode;
            try { Directory.CreateDirectory(this.directory); } catch (IOException) { diskDisabled = true; } catch (UnauthorizedAccessException) { diskDisabled = true; }
            worker = new Thread(Run); worker.IsBackground = true; worker.Name = "Thor thumbnail cache"; worker.SetApartmentState(ApartmentState.STA); worker.Start();
        }
        /// <summary>Cache lookup only. Disk hits read small PNGs synchronously; schedule on a background thread for UI use.</summary>
        public bool TryGet(ThumbnailKey key, out Bitmap thumb)
        {
            thumb = null;
            if (!key.Current()) return false;
            string id = key.Id;
            lock (gate)
            {
                if (disposed) return false;
                MemoryEntry entry;
                if (memory.TryGetValue(id, out entry)) { entry.Used = ++clock; thumb = new Bitmap(entry.Image); return true; }
            }
            lock (diskGate)
            {
                if (diskDisabled) return false;
                string path = CachePath(id);
                if (!File.Exists(path)) return false;
                try
                {
                    using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    using (Image image = Image.FromStream(stream))
                    {
                        if (image.Width > key.Size || image.Height > key.Size) throw new InvalidDataException("Invalid thumbnail dimensions");
                        thumb = new Bitmap(image);
                    }
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    if (!Recoverable(ex)) throw;
                    if (thumb != null) { thumb.Dispose(); thumb = null; }
                    Delete(path); return false;
                }
            }
            if (!key.Current()) { thumb.Dispose(); thumb = null; return false; }
            Remember(id, thumb); return true;
        }
        public void Request(ThumbnailKey key, ThumbnailPriority priority)
        {
            if (key == null) throw new ArgumentNullException("key");
            lock (gate)
            {
                if (disposed) return;
                Work existing = queue.FirstOrDefault(w => w.Key.Id == key.Id);
                if (existing != null) { existing.Priority = priority > existing.Priority ? priority : existing.Priority; return; }
                if (active != null && !active.Cancelled && active.Key.Id == key.Id) return;
                if (queue.Count == capacity)
                {
                    int drop = queue.FindIndex(w => w.Priority == ThumbnailPriority.Background);
                    if (drop < 0 && priority == ThumbnailPriority.Background) return;
                    queue.RemoveAt(drop < 0 ? 0 : drop);
                }
                queue.Add(new Work { Key = key, Priority = priority }); wake.Set();
            }
        }
        public void Cancel(string path)
        {
            string full = System.IO.Path.GetFullPath(path);
            lock (gate)
            {
                queue.RemoveAll(w => String.Equals(w.Key.Path, full, StringComparison.OrdinalIgnoreCase));
                if (active != null && String.Equals(active.Key.Path, full, StringComparison.OrdinalIgnoreCase)) active.Cancelled = true;
            }
        }
        static bool Recoverable(Exception ex)
        { return ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is OutOfMemoryException || ex is System.Runtime.InteropServices.ExternalException; }
        string CachePath(string id) { return System.IO.Path.Combine(directory, "thumb-" + id + ".png"); }
        static void Delete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        void Remember(string id, Bitmap image)
        {
            lock (gate)
            {
                if (disposed) return;
                MemoryEntry old;
                if (memory.TryGetValue(id, out old)) old.Image.Dispose();
                memory[id] = new MemoryEntry { Image = new Bitmap(image), Used = ++clock };
                while (memory.Count > memoryCapacity)
                { string oldest = memory.OrderBy(p => p.Value.Used).First().Key; memory[oldest].Image.Dispose(); memory.Remove(oldest); }
            }
        }
        void Store(ThumbnailKey key, Bitmap bitmap)
        {
            lock (diskGate)
            {
                if (diskDisabled || maxBytes == 0) return;
                string temp = System.IO.Path.Combine(directory, "thumb-tmp-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    bitmap.Save(temp, System.Drawing.Imaging.ImageFormat.Png);
                    using (Image check = Image.FromFile(temp)) { if (check.Width != bitmap.Width || check.Height != bitmap.Height) throw new InvalidDataException("Invalid cache write"); }
                    string target = CachePath(key.Id);
                    if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
                    Trim();
                }
                catch (Exception ex) { if (!Recoverable(ex)) throw; diskDisabled = true; Log.Warn("Thumbnail disk cache unavailable; memory fallback"); }
                finally { Delete(temp); }
            }
        }
        public void Trim()
        {
            lock (diskGate)
            {
                if (diskDisabled) return;
                try
                {
                    FileInfo[] files = new DirectoryInfo(directory).GetFiles("thumb-*.png");
                    long total = files.Sum(f => f.Length);
                    foreach (FileInfo file in files.OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name))
                    { if (total <= maxBytes) break; long size = file.Length; file.Delete(); total -= size; }
                }
                catch (Exception ex) { if (!Recoverable(ex)) throw; diskDisabled = true; }
            }
        }
        void Run()
        {
            try
            {
                for (;;)
                {
                    Work work = null;
                    lock (gate)
                    {
                        if (disposed) return;
                        int next = queue.FindIndex(w => w.Priority == ThumbnailPriority.Visible);
                        if (next < 0 && queue.Count > 0) next = 0;
                        if (next >= 0) { work = queue[next]; queue.RemoveAt(next); active = work; }
                    }
                    if (work == null) { wake.WaitOne(); continue; }
                    Bitmap bitmap = null;
                    ThumbnailOutcome outcome = ThumbnailOutcome.Success;
                    string reason = "";
                    try
                    {
                        if (!work.Key.Current()) { outcome = ThumbnailOutcome.SourceChanged; reason = "源文件已经改变"; }
                        else if (!TryGet(work.Key, out bitmap))
                        {
                            try { bitmap = decode(work.Key); }
                            catch (Exception ex) { if (!Recoverable(ex)) throw; }
                            if (bitmap == null) { outcome = ThumbnailOutcome.Placeholder; reason = "素材无法解码"; bitmap = Placeholder(work.Key.Size); }
                            else if (!work.Key.Current()) { bitmap.Dispose(); bitmap = null; outcome = ThumbnailOutcome.SourceChanged; reason = "源文件已经改变"; }
                            else { Remember(work.Key.Id, bitmap); Store(work.Key, bitmap); }
                        }
                        lock (gate)
                        {
                            if (disposed || work.Cancelled) continue;
                        }
                        EventHandler<ThumbnailReadyEventArgs> handlers = Ready;
                        if (handlers != null)
                            foreach (EventHandler<ThumbnailReadyEventArgs> handler in handlers.GetInvocationList())
                            {
                                lock (gate) { if (disposed || work.Cancelled) break; }
                                Bitmap copy = bitmap == null ? null : new Bitmap(bitmap);
                                try { handler(this, new ThumbnailReadyEventArgs(work.Key, copy, outcome, reason)); }
                                catch { if (copy != null) copy.Dispose(); Log.Warn("Thumbnail consumer failed"); }
                            }
                    }
                    catch (Exception ex) { Log.Warn("Thumbnail request failed: " + ex.GetType().Name); }
                    finally { if (bitmap != null) bitmap.Dispose(); lock (gate) { active = null; } }
                }
            }
            finally { wake.Dispose(); }
        }
        static Bitmap Decode(ThumbnailKey key)
        {
            if (String.Equals(System.IO.Path.GetExtension(key.Path), ".mp4", StringComparison.OrdinalIgnoreCase))
            { using (Bitmap video = ShellThumbnails.Get(key.Path, key.Size)) return video == null ? null : Fit(video, key.Size); }
            using (FileStream stream = File.Open(key.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (Image image = Image.FromStream(stream))
                return Fit(image, key.Size);
        }
        static Bitmap Fit(Image image, int size)
        {
            double ratio = Math.Min(1, (double)size / Math.Max(image.Width, image.Height));
            Bitmap result = new Bitmap(Math.Max(1, (int)Math.Round(image.Width * ratio)), Math.Max(1, (int)Math.Round(image.Height * ratio)));
            using (Graphics graphics = Graphics.FromImage(result)) { graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.DrawImage(image, 0, 0, result.Width, result.Height); }
            return result;
        }
        static Bitmap Placeholder(int size)
        {
            Bitmap bitmap = new Bitmap(size, size);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Pen pen = new Pen(Color.FromArgb(110, 115, 130), 2))
            { graphics.Clear(Color.FromArgb(35, 38, 46)); graphics.DrawRectangle(pen, size / 4, size / 4, size / 2, size / 2); graphics.DrawLine(pen, size / 4, size / 4, size * 3 / 4, size * 3 / 4); }
            return bitmap;
        }
        /// <summary>Bounded to 500 ms; a blocked native decoder may finish later on the background thread. No subsequent notifications are scheduled.</summary>
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return; disposed = true; queue.Clear(); if (active != null) active.Cancelled = true;
                foreach (MemoryEntry entry in memory.Values) entry.Image.Dispose(); memory.Clear(); wake.Set();
            }
            if (Thread.CurrentThread != worker) worker.Join(500);
        }
    }
}
