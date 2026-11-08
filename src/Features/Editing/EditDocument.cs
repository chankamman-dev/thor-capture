using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Features.Editing
{
    public enum EditOutcome { Success, Cancelled, InvalidTarget, UnsupportedFormat, Failed }
    public sealed class DerivedInfo
    {
        public readonly string SourcePath;
        public readonly string SourceSha256;
        public readonly ReadOnlyCollection<string> Operations;
        public readonly Size OutputSize;
        internal DerivedInfo(string source, string hash, IEnumerable<EditOp> operations, Size size)
        { SourcePath = source; SourceSha256 = hash; Operations = new ReadOnlyCollection<string>(operations.Select(o => o.Summary).ToList()); OutputSize = size; }
    }
    public sealed class EditRenderResult
    {
        public readonly EditOutcome Outcome;
        public readonly Bitmap Bitmap;
        public readonly string Reason;
        internal EditRenderResult(EditOutcome outcome, Bitmap bitmap, string reason) { Outcome = outcome; Bitmap = bitmap; Reason = reason; }
    }
    public sealed class EditResult
    {
        public readonly EditOutcome Outcome;
        public readonly string Path;
        public readonly string Reason;
        public readonly DerivedInfo Derivation;
        internal EditResult(EditOutcome outcome, string path, string reason, DerivedInfo derivation)
        { Outcome = outcome; Path = path; Reason = reason; Derivation = derivation; }
    }
    public sealed class EditOpenResult
    {
        public readonly EditOutcome Outcome;
        public readonly EditDocument Document;
        public readonly string Reason;
        internal EditOpenResult(EditOutcome outcome, EditDocument document, string reason) { Outcome = outcome; Document = document; Reason = reason; }
    }
    /// <summary>Immutable operation history over a detached source bitmap. No device or UI state.</summary>
    public sealed class EditDocument : IDisposable
    {
        public const int MaximumPixels = 16000000;
        public const int MaximumDimension = 8192;
        public const int MaximumOperations = 256;
        readonly object gate = new object();
        readonly string sourcePath;
        readonly string sourceHash;
        Bitmap original;
        readonly List<EditOp> history = new List<EditOp>();
        int cursor;
        sealed class Snapshot : IDisposable
        {
            internal Bitmap Original;
            internal EditOp[] Operations;
            internal DerivedInfo Derived;
            public void Dispose() { Original.Dispose(); }
        }
        public EditDocument(string sourcePath) : this(sourcePath, CancellationToken.None) { }
        EditDocument(string path, CancellationToken token)
        {
            sourcePath = System.IO.Path.GetFullPath(path);
            if (sourcePath.IndexOf(':', 2) >= 0 || sourcePath.StartsWith("\\\\?\\", StringComparison.Ordinal)) throw new ArgumentException("Alternate streams and device paths are not supported");
            string extension = System.IO.Path.GetExtension(sourcePath).ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".bmp") throw new ArgumentException("Only PNG, JPEG and BMP sources are supported");
            token.ThrowIfCancellationRequested();
            FileInfo sourceBefore = new FileInfo(sourcePath); long sourceLength = sourceBefore.Length; DateTime sourceTime = sourceBefore.LastWriteTimeUtc;
            using (FileStream stream = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (SHA256 hash = SHA256.Create())
            {
                if (stream.Length > 256L * 1024 * 1024) throw new ArgumentException("Source file too large");
                byte[] buffer = new byte[65536]; int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); hash.TransformBlock(buffer, 0, read, null, 0); }
                hash.TransformFinalBlock(buffer, 0, 0); sourceHash = BitConverter.ToString(hash.Hash).Replace("-", ""); stream.Position = 0;
                using (Image image = Image.FromStream(stream))
                {
                    if (image.RawFormat.Guid != ImageFormat.Png.Guid && image.RawFormat.Guid != ImageFormat.Jpeg.Guid && image.RawFormat.Guid != ImageFormat.Bmp.Guid) throw new ArgumentException("Unsupported source format");
                    ValidateSize(image.Size); token.ThrowIfCancellationRequested();
                    original = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
                    try
                    {
                        using (Graphics graphics = Graphics.FromImage(original)) { graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; graphics.DrawImageUnscaled(image, 0, 0); }
                        token.ThrowIfCancellationRequested();
                        FileInfo after = new FileInfo(sourcePath);
                        if (sourceLength != after.Length || sourceTime != after.LastWriteTimeUtc) throw new IOException("Source changed while loading");
                    }
                    catch { original.Dispose(); original = null; throw; }
                }
            }
        }
        public static Task<EditOpenResult> OpenAsync(string path, CancellationToken token)
        {
            return Task.Run(() =>
            {
                try { return new EditOpenResult(EditOutcome.Success, new EditDocument(path, token), ""); }
                catch (OperationCanceledException) { return new EditOpenResult(EditOutcome.Cancelled, null, "读取图片已取消"); }
                catch (Exception ex) { return new EditOpenResult(EditOutcome.Failed, null, "无法读取图片：" + ex.GetType().Name); }
            });
        }
        static void ValidateSize(Size size)
        {
            if (size.Width < 1 || size.Height < 1 || size.Width > MaximumDimension || size.Height > MaximumDimension || (long)size.Width * size.Height > MaximumPixels) throw new ArgumentOutOfRangeException("size");
        }
        void Alive() { if (original == null) throw new ObjectDisposedException("EditDocument"); }
        Size SizeAtCursor()
        { Size size = original.Size; for (int i = 0; i < cursor; i++) size = history[i].NextSize(size); return size; }
        public Size CanvasSize { get { lock (gate) { Alive(); return SizeAtCursor(); } } }
        public bool CanUndo { get { lock (gate) return original != null && cursor > 0; } }
        public bool CanRedo { get { lock (gate) return original != null && cursor < history.Count; } }
        public void Apply(EditOp operation)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            lock (gate)
            {
                Alive(); if (cursor >= MaximumOperations) throw new InvalidOperationException("Edit history limit reached");
                Size canvas = SizeAtCursor(); operation.Affects(canvas); ValidateSize(operation.NextSize(canvas));
                if (cursor < history.Count) history.RemoveRange(cursor, history.Count - cursor);
                history.Add(operation); cursor++;
            }
        }
        public bool Undo() { lock (gate) { Alive(); if (cursor == 0) return false; cursor--; return true; } }
        public bool Redo() { lock (gate) { Alive(); if (cursor == history.Count) return false; cursor++; return true; } }
        public DerivedInfo Derivation
        { get { lock (gate) { Alive(); return new DerivedInfo(sourcePath, sourceHash, history.Take(cursor), SizeAtCursor()); } } }
        Snapshot Capture(float scale)
        {
            EditOp.Number(scale, 0.01f, 4);
            lock (gate)
            {
                Alive(); Size canvas = SizeAtCursor();
                ValidateSize(new Size(Math.Max(1, (int)Math.Round(canvas.Width * scale)), Math.Max(1, (int)Math.Round(canvas.Height * scale))));
                return new Snapshot { Original = original.Clone(new Rectangle(Point.Empty, original.Size), PixelFormat.Format32bppArgb), Operations = history.Take(cursor).ToArray(), Derived = new DerivedInfo(sourcePath, sourceHash, history.Take(cursor), canvas) };
            }
        }
        static Bitmap RenderSnapshot(Snapshot snapshot, float scale, CancellationToken token)
        {
            Bitmap bitmap = null;
            try
            {
                token.ThrowIfCancellationRequested(); bitmap = snapshot.Original.Clone(new Rectangle(Point.Empty, snapshot.Original.Size), PixelFormat.Format32bppArgb);
                foreach (EditOp operation in snapshot.Operations)
                {
                    token.ThrowIfCancellationRequested(); Bitmap next = operation.Execute(bitmap, token);
                    if (!Object.ReferenceEquals(next, bitmap)) { bitmap.Dispose(); bitmap = next; }
                }
                token.ThrowIfCancellationRequested();
                if (scale != 1)
                {
                    Bitmap resized = new Bitmap(Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale)), PixelFormat.Format32bppArgb);
                    try { using (Graphics graphics = Graphics.FromImage(resized)) { graphics.CompositingMode = CompositingMode.SourceCopy; graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.DrawImage(bitmap, 0, 0, resized.Width, resized.Height); } }
                    catch { resized.Dispose(); throw; }
                    bitmap.Dispose(); bitmap = resized;
                }
                token.ThrowIfCancellationRequested(); Bitmap result = bitmap; bitmap = null; return result;
            }
            finally { if (bitmap != null) bitmap.Dispose(); }
        }
        /// <summary>The caller owns the result. scale=1 is exactly the PNG export bitmap.</summary>
        public Bitmap Render(float scale) { using (Snapshot snapshot = Capture(scale)) return RenderSnapshot(snapshot, scale, CancellationToken.None); }
        /// <summary>Returns a caller-owned bitmap. Operation state is snapshotted before background execution.</summary>
        public Task<EditRenderResult> RenderAsync(float scale, CancellationToken token)
        {
            Snapshot snapshot;
            try { token.ThrowIfCancellationRequested(); snapshot = Capture(scale); }
            catch (OperationCanceledException) { return Task.FromResult(new EditRenderResult(EditOutcome.Cancelled, null, "渲染已取消")); }
            catch (Exception ex) { return Task.FromResult(new EditRenderResult(EditOutcome.Failed, null, "无法渲染：" + ex.GetType().Name)); }
            return Task.Run(() =>
            {
                using (snapshot)
                {
                    try { return new EditRenderResult(EditOutcome.Success, RenderSnapshot(snapshot, scale, token), ""); }
                    catch (OperationCanceledException) { return new EditRenderResult(EditOutcome.Cancelled, null, "渲染已取消"); }
                    catch (Exception ex) { return new EditRenderResult(EditOutcome.Failed, null, "无法渲染：" + ex.GetType().Name); }
                }
            });
        }
        public EditResult SaveAs(string path, ImageFormat format) { return SaveAsAsync(path, format, CancellationToken.None).GetAwaiter().GetResult(); }
        public Task<EditResult> SaveAsAsync(string path, ImageFormat format, CancellationToken token)
        {
            Snapshot snapshot;
            string target;
            try
            {
                token.ThrowIfCancellationRequested();
                if (format == null || format.Guid != ImageFormat.Png.Guid) return Task.FromResult(new EditResult(EditOutcome.UnsupportedFormat, null, "初版编辑导出仅支持无损 PNG", null));
                if (path != null && path.IndexOf(':', 2) >= 0) return Task.FromResult(new EditResult(EditOutcome.InvalidTarget, null, "不能写入 NTFS 备用数据流", null));
                target = System.IO.Path.GetFullPath(path);
                // Existing targets also reject hard links, junction aliases and alternate source spellings.
                if (String.Equals(sourcePath, target, StringComparison.OrdinalIgnoreCase) || File.Exists(target) || Directory.Exists(target) || target.IndexOf(':', 2) >= 0 || target.StartsWith("\\\\?\\", StringComparison.Ordinal))
                    return Task.FromResult(new EditResult(EditOutcome.InvalidTarget, null, "必须另存为不存在的新 PNG 文件，不能覆盖原片或已有目标", null));
                if (!String.Equals(System.IO.Path.GetExtension(target), ".png", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(new EditResult(EditOutcome.InvalidTarget, null, "目标扩展名必须是 .png", null));
                snapshot = Capture(1);
            }
            catch (OperationCanceledException) { return Task.FromResult(new EditResult(EditOutcome.Cancelled, null, "另存已取消", null)); }
            catch (Exception ex) { return Task.FromResult(new EditResult(EditOutcome.Failed, null, "无法另存：" + ex.GetType().Name, null)); }
            return Task.Run(() => Save(snapshot, target, token));
        }
        static EditResult Save(Snapshot snapshot, string target, CancellationToken token)
        {
            string temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target), ".thor-edit-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool ownsTemp = false;
            using (snapshot)
            {
                try
                {
                    using (Bitmap bitmap = RenderSnapshot(snapshot, 1, token))
                    {
                        token.ThrowIfCancellationRequested();
                        using (FileStream file = File.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { ownsTemp = true; bitmap.Save(file, ImageFormat.Png); }
                        using (Image image = Image.FromFile(temp))
                        using (Bitmap decoded = new Bitmap(image))
                        { if (decoded.Width != bitmap.Width || decoded.Height != bitmap.Height || image.RawFormat.Guid != ImageFormat.Png.Guid) throw new InvalidDataException("Invalid exported PNG"); }
                    }
                    token.ThrowIfCancellationRequested(); File.Move(temp, target);
                    return new EditResult(EditOutcome.Success, target, "", snapshot.Derived);
                }
                catch (OperationCanceledException) { return new EditResult(EditOutcome.Cancelled, null, "另存已取消，原片不变", snapshot.Derived); }
                catch (Exception ex) { Log.Warn("Image editing save failed: " + ex.GetType().Name); return new EditResult(EditOutcome.Failed, null, "另存失败，原片和已有目标不变：" + ex.GetType().Name, snapshot.Derived); }
                finally { try { if (ownsTemp && File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }
        public void Dispose()
        { lock (gate) { if (original == null) return; original.Dispose(); original = null; history.Clear(); cursor = 0; } }
    }
}
