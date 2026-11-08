using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal enum ExportBackground { Transparent, Solid, Gradient }
    internal enum ExportLayout { Framed, StackVertical, SideBySide, Single }
    internal enum ImageExportOutcome { Success, Cancelled, InvalidPlan, AlreadyExists, FileFailed }

    internal sealed class ImageExportPlan
    {
        private readonly DeviceSkin skin;
        public readonly ExportLayout Layout;
        public DeviceSkin Skin { get { return CopySkin(skin); } }
        public readonly ExportBackground Background;
        public readonly Color Color1, Color2, GapColor;
        public readonly Size? Canvas;
        public readonly int PixelScale, Gap, JpegQuality;
        public readonly float CornerRadius;
        public readonly ImageFormat Format;
        public readonly bool MarkMissingScreens;
        // Shadow: the shell's drop shadow (framed) or a soft shadow under each screen (mosaics).
        // Padding: -1 keeps the layout's original margins; otherwise design units round the shell
        // (framed) or source pixels round the mosaic, multiplied by the pixel scale.
        public readonly bool Shadow;
        public readonly int Padding;

        public ImageExportPlan(ExportLayout layout, DeviceSkin skin, ExportBackground background,
            Color color1, Color color2, Size? canvas, int pixelScale, int gap, Color gapColor,
            float cornerRadius, ImageFormat format, int jpegQuality, bool markMissingScreens = true, bool? shadow = null, int padding = -1)
        {
            if (padding < -1 || padding > 2048) throw new ArgumentException("留白无效。");
            if (!Enum.IsDefined(typeof(ExportLayout), layout) || !Enum.IsDefined(typeof(ExportBackground), background)) throw new ArgumentException("布局或背景无效。");
            if (skin == null) throw new ArgumentNullException("skin");
            if (skin.Abxy != null && skin.Abxy.Length != 4) throw new ArgumentException("ABXY 配色应为四项。");
            if (float.IsNaN(skin.GlowLevel) || float.IsInfinity(skin.GlowLevel) || skin.GlowLevel < .1f || skin.GlowLevel > 1f) throw new ArgumentException("灯光亮度无效。");
            if (pixelScale < 1 || pixelScale > 4 || gap < 0 || gap > 4096 || cornerRadius < 0 || float.IsNaN(cornerRadius) || float.IsInfinity(cornerRadius)) throw new ArgumentException("倍率、间隔或圆角无效。");
            if (format == null || (format.Guid != ImageFormat.Png.Guid && format.Guid != ImageFormat.Jpeg.Guid)) throw new ArgumentException("仅支持 PNG 和 JPEG。");
            if (jpegQuality < 1 || jpegQuality > 100) throw new ArgumentException("JPEG 质量应为 1–100。");
            if (canvas.HasValue && (canvas.Value.Width < 1 || canvas.Value.Height < 1 || canvas.Value.Width > ImageExporter.MaxDimension || canvas.Value.Height > ImageExporter.MaxDimension || (long)canvas.Value.Width * canvas.Value.Height > ImageExporter.MaxPixels)) throw new ArgumentException("画布尺寸超过导出上限。");
            if (format.Guid == ImageFormat.Jpeg.Guid && (background == ExportBackground.Transparent || color1.A != 255 || (background == ExportBackground.Gradient && color2.A != 255))) throw new ArgumentException("JPEG 必须使用不透明背景。");
            Layout = layout; this.skin = CopySkin(skin); Background = background;
            Color1 = color1; Color2 = color2; Canvas = canvas; PixelScale = pixelScale;
            Gap = gap; GapColor = gapColor; CornerRadius = cornerRadius; Format = format; JpegQuality = jpegQuality;
            MarkMissingScreens = markMissingScreens;
            Shadow = shadow ?? layout == ExportLayout.Framed; Padding = padding;
        }

        public static ImageExportPlan Default(DeviceSkin skin)
        {
            return new ImageExportPlan(ExportLayout.Framed, skin, ExportBackground.Transparent,
                Color.FromArgb(236, 237, 241), Color.White, null, 1, 24, Color.Transparent, 0, ImageFormat.Png, 90, false);
        }

        public string Describe() { return Layout + " · " + (Format.Guid == ImageFormat.Png.Guid ? "PNG" : "JPEG") + " · " + PixelScale + "×"; }

        internal static DeviceSkin CopySkin(DeviceSkin value)
        {
            DeviceSkin copy = new DeviceSkin();
            foreach (FieldInfo field in typeof(DeviceSkin).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object item = field.GetValue(value);
                Color[] colors = item as Color[];
                field.SetValue(copy, colors == null ? item : colors.Clone());
            }
            return copy;
        }
    }

    internal sealed class ExportResult
    {
        public readonly ImageExportOutcome Outcome;
        public readonly string Reason;
        public readonly Size Size;
        public bool Success { get { return Outcome == ImageExportOutcome.Success; } }
        internal ExportResult(ImageExportOutcome outcome, string reason, Size size) { Outcome = outcome; Reason = reason; Size = size; }
    }

    internal static class ImageExporter
    {
        // The ceiling covers the output plus compositing and encoding buffers on the 64-bit host.
        public const long MaxPixels = 48000000;
        public const int MaxDimension = 16384;
        private const float LegacyScale = 1920f / 790f;
        private const float Margin = 44f;

        public static Size MeasureOutput(ImageExportPlan plan, Size? top, Size? bottom)
        {
            Size natural = MeasureNatural(plan, top, bottom);
            if (!plan.Canvas.HasValue) return natural;
            Size canvas = plan.Canvas.Value;
            ValidateSize(canvas);
            if (plan.PixelScale > 1 && (canvas.Width < natural.Width || canvas.Height < natural.Height)) throw new ArgumentException("整数像素倍率需要足够大的画布；不能缩小像素块。");
            return canvas;
        }

        private static Size MeasureNatural(ImageExportPlan plan, Size? top, Size? bottom)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            if (!top.HasValue && !bottom.HasValue) throw new ArgumentException("没有可导出的截图。");
            if (top.HasValue) ValidateSize(top.Value);
            if (bottom.HasValue) ValidateSize(bottom.Value);
            if (plan.Layout == ExportLayout.Single && top.HasValue && bottom.HasValue) throw new ArgumentException("单屏布局只能传入一张截图。");
            if (plan.Layout == ExportLayout.Framed)
            {
                float scale = FrameScale(plan, top, bottom);
                if (plan.Padding < 0) return CheckedSize(Math.Round((DeviceArt.Width + Margin * 2) * scale), Math.Round((DeviceArt.Height + Margin * 2.6f) * scale));
                RectangleF extent = FrameExtent(plan);
                return CheckedSize(Math.Round((extent.Width + plan.Padding * 2) * scale), Math.Round((extent.Height + plan.Padding * 2) * scale));
            }
            Size inner = MeasureMosaic(plan, top, bottom);
            long pad = (long)Math.Max(0, plan.Padding) * plan.PixelScale;
            return CheckedSize(inner.Width + pad * 2, inner.Height + pad * 2);
        }

        // The shell plus, when drawn, the reach of its shadow (see DeviceArt.Draw), in design units.
        private static RectangleF FrameExtent(ImageExportPlan plan)
        {
            return plan.Shadow ? RectangleF.FromLTRB(-16, 0, DeviceArt.Width + 16, DeviceArt.Height + 44) : new RectangleF(0, 0, DeviceArt.Width, DeviceArt.Height);
        }

        private static Size MeasureMosaic(ImageExportPlan plan, Size? top, Size? bottom)
        {
            Size a = top ?? bottom.Value;
            Size b = bottom ?? a;
            long aw = (long)a.Width * plan.PixelScale, ah = (long)a.Height * plan.PixelScale;
            long bw = (long)b.Width * plan.PixelScale, bh = (long)b.Height * plan.PixelScale;
            bool pair = top.HasValue && bottom.HasValue;
            if (!pair || plan.Layout == ExportLayout.Single) return CheckedSize(aw, ah);
            if (plan.Layout == ExportLayout.StackVertical) return CheckedSize(Math.Max(aw, bw), ah + bh + plan.Gap);
            return CheckedSize(aw + bw + plan.Gap, Math.Max(ah, bh));
        }

        private static float FrameScale(ImageExportPlan plan, Size? top, Size? bottom)
        {
            if (plan.PixelScale == 1) return LegacyScale;
            float scale = LegacyScale;
            if (top.HasValue) scale = Math.Max(scale, Math.Max(top.Value.Width / 790f, top.Value.Height / 444.4f) * plan.PixelScale);
            if (bottom.HasValue) scale = Math.Max(scale, Math.Max(bottom.Value.Width / 432f, bottom.Value.Height / 376.2f) * plan.PixelScale);
            return scale;
        }

        private static Size CheckedSize(double width, double height)
        {
            if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension || width * height > MaxPixels) throw new ArgumentException("导出超过尺寸或内存上限（4800 万像素）。");
            Size size = new Size((int)width, (int)height); ValidateSize(size); return size;
        }
        private static void ValidateSize(Size size)
        {
            if (size.Width < 1 || size.Height < 1 || size.Width > MaxDimension || size.Height > MaxDimension || (long)size.Width * size.Height > MaxPixels) throw new ArgumentException("图片尺寸超过导出上限。");
        }

        public static Bitmap Render(ImageExportPlan plan, Image top, Image bottom)
        {
            return RenderCore(plan, top, bottom, CancellationToken.None);
        }

        /// <summary>Runs on a worker thread. Images are borrowed and must remain alive until completion; the caller owns the returned bitmap.</summary>
        public static Task<Bitmap> RenderAsync(ImageExportPlan plan, Image top, Image bottom, CancellationToken token)
        {
            return Task.Run(delegate { return RenderCore(plan, top, bottom, token); }, token);
        }

        private static Bitmap RenderCore(ImageExportPlan plan, Image top, Image bottom, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Size? topSize = top == null ? (Size?)null : top.Size, bottomSize = bottom == null ? (Size?)null : bottom.Size;
            Size natural = MeasureNatural(plan, topSize, bottomSize), output = MeasureOutput(plan, topSize, bottomSize);
            Bitmap bitmap = new Bitmap(output.Width, output.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    PaintBackground(g, plan, output);
                    float fit = plan.PixelScale > 1 ? 1 : Math.Min(output.Width / (float)natural.Width, output.Height / (float)natural.Height);
                    if (output != natural)
                    {
                        float x = (output.Width - natural.Width * fit) / 2f, y = (output.Height - natural.Height * fit) / 2f;
                        if (plan.PixelScale > 1) { x = (float)Math.Floor(x); y = (float)Math.Floor(y); }
                        g.TranslateTransform(x, y);
                        g.ScaleTransform(fit, fit);
                    }
                    if (plan.Layout == ExportLayout.Framed)
                    {
                        float scale = FrameScale(plan, topSize, bottomSize), m = Margin * scale;
                        // The legacy origin keeps its original constant expression so default exports stay bit-identical.
                        PointF origin = plan.PixelScale == 1 ? new PointF(Margin * LegacyScale, Margin * LegacyScale * .6f) : new PointF(m, m * .6f);
                        if (plan.Padding >= 0) { RectangleF extent = FrameExtent(plan); origin = new PointF((plan.Padding - extent.X) * scale, (plan.Padding - extent.Y) * scale); }
                        RectangleF frame = plan.PixelScale == 1
                            ? new RectangleF(origin.X, origin.Y, DeviceArt.Width * LegacyScale, DeviceArt.Height * LegacyScale)
                            : new RectangleF(origin.X, origin.Y, DeviceArt.Width * scale, DeviceArt.Height * scale);
                        DeviceArt.Draw(g, frame, plan.Skin, top, bottom,
                            plan.MarkMissingScreens && top == null ? "缺少上屏" : null,
                            plan.MarkMissingScreens && bottom == null ? "缺少下屏" : null, null, true, plan.Shadow);
                        if (plan.PixelScale > 1)
                        {
                            // Geometry stays smooth; only source screen pixels use integer nearest-neighbour blocks.
                            DrawIntegerScreen(g, top, new RectangleF(origin.X + 44 * scale, origin.Y + 26 * scale, 790 * scale, 444.4f * scale), plan.PixelScale);
                            DrawIntegerScreen(g, bottom, new RectangleF(origin.X + 223 * scale, origin.Y + 591 * scale, 432 * scale, 376.2f * scale), plan.PixelScale);
                        }
                    }
                    else
                    {
                        Image a = top ?? bottom;
                        bool pair = top != null && bottom != null;
                        int aw = a.Width * plan.PixelScale, ah = a.Height * plan.PixelScale;
                        int pad = Math.Max(0, plan.Padding) * plan.PixelScale;
                        Size inner = new Size(natural.Width - pad * 2, natural.Height - pad * 2);
                        if (pad > 0) g.TranslateTransform(pad, pad);
                        Rectangle first, second = Rectangle.Empty;
                        if (!pair) first = new Rectangle((inner.Width - aw) / 2, (inner.Height - ah) / 2, aw, ah);
                        else if (plan.Layout == ExportLayout.StackVertical)
                        {
                            FillGap(g, new Rectangle(0, ah, inner.Width, plan.Gap), plan.GapColor);
                            first = new Rectangle((inner.Width - aw) / 2, 0, aw, ah);
                            int bw = bottom.Width * plan.PixelScale;
                            second = new Rectangle((inner.Width - bw) / 2, ah + plan.Gap, bw, bottom.Height * plan.PixelScale);
                        }
                        else
                        {
                            FillGap(g, new Rectangle(aw, 0, plan.Gap, inner.Height), plan.GapColor);
                            first = new Rectangle(0, (inner.Height - ah) / 2, aw, ah);
                            int bh = bottom.Height * plan.PixelScale;
                            second = new Rectangle(aw + plan.Gap, (inner.Height - bh) / 2, bottom.Width * plan.PixelScale, bh);
                        }
                        if (plan.Shadow) { TileShadow(g, first, plan); if (pair) TileShadow(g, second, plan); }
                        DrawTile(g, pair ? top : a, first, plan);
                        if (pair) DrawTile(g, bottom, second, plan);
                    }
                }
                token.ThrowIfCancellationRequested();
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }

        private static void PaintBackground(Graphics g, ImageExportPlan plan, Size size)
        {
            g.Clear(plan.Background == ExportBackground.Transparent ? Color.Transparent : plan.Color1);
            if (plan.Background == ExportBackground.Gradient)
                using (LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(Point.Empty, size), plan.Color1, plan.Color2, 90f)) g.FillRectangle(brush, 0, 0, size.Width, size.Height);
        }
        // A soft shadow under a screen, sized from the screen so 1x and 3x exports look alike.
        private static void TileShadow(Graphics g, Rectangle rect, ImageExportPlan plan)
        {
            GraphicsState state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float spread = Math.Max(4f, Math.Min(rect.Width, rect.Height) * .035f);
            Ui.Shadow(g, rect, Math.Max(1f, plan.CornerRadius), spread, 110, spread * .45f);
            g.Restore(state);
        }
        private static void FillGap(Graphics g, Rectangle rect, Color color) { if (rect.Width > 0 && rect.Height > 0) using (SolidBrush brush = new SolidBrush(color)) g.FillRectangle(brush, rect); }
        private static void DrawIntegerScreen(Graphics g, Image image, RectangleF slot, int factor)
        {
            if (image == null) return;
            using (SolidBrush brush = new SolidBrush(Color.Black)) g.FillRectangle(brush, slot);
            int w = image.Width * factor, h = image.Height * factor;
            DrawPixels(g, image, new Rectangle((int)Math.Round(slot.X + (slot.Width - w) / 2), (int)Math.Round(slot.Y + (slot.Height - h) / 2), w, h));
        }
        private static void DrawTile(Graphics g, Image image, Rectangle rect, ImageExportPlan plan)
        {
            GraphicsState state = g.Save();
            try
            {
                float radius = Math.Min(plan.CornerRadius, Math.Min(rect.Width, rect.Height) / 2f);
                if (radius > 0)
                {
                    using (GraphicsPath path = new GraphicsPath())
                    {
                        float d = radius * 2;
                        path.AddArc(rect.X, rect.Y, d, d, 180, 90); path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
                        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90); path.CloseFigure();
                        g.SetClip(path, CombineMode.Intersect);
                    }
                }
                DrawPixels(g, image, rect);
            }
            finally { g.Restore(state); }
        }
        private static void DrawPixels(Graphics g, Image image, Rectangle rect)
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
            using (ImageAttributes attributes = new ImageAttributes())
            {
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(image, rect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            }
        }

        public static ExportResult Save(ImageExportPlan plan, Image top, Image bottom, string path)
        {
            return SaveCore(plan, top, bottom, path, CancellationToken.None, null);
        }
        /// <summary>Runs on a worker thread. No UI callbacks; images remain caller-owned. Progress is 0–100.</summary>
        public static Task<ExportResult> SaveAsync(ImageExportPlan plan, Image top, Image bottom, string path, CancellationToken token, IProgress<int> progress)
        {
            return Task.Run(delegate { return SaveCore(plan, top, bottom, path, token, progress); });
        }
        private static ExportResult SaveCore(ImageExportPlan plan, Image top, Image bottom, string path, CancellationToken token, IProgress<int> progress)
        {
            string temporary = null; Size size = Size.Empty;
            try
            {
                token.ThrowIfCancellationRequested();
                size = MeasureOutput(plan, top == null ? (Size?)null : top.Size, bottom == null ? (Size?)null : bottom.Size);
                string destination = Path.GetFullPath(path);
                if (File.Exists(destination)) return new ExportResult(ImageExportOutcome.AlreadyExists, "目标已存在，请另选文件名。", size);
                string directory = Path.GetDirectoryName(destination);
                temporary = Path.Combine(directory, ".thor-export-" + Guid.NewGuid().ToString("N") + ".tmp");
                Report(progress, 0);
                using (Bitmap bitmap = RenderCore(plan, top, bottom, token))
                {
                    Report(progress, 60);
                    token.ThrowIfCancellationRequested();
                    using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        if (plan.Format.Guid == ImageFormat.Jpeg.Guid)
                        {
                            ImageCodecInfo codec = null;
                            foreach (ImageCodecInfo candidate in ImageCodecInfo.GetImageEncoders()) if (candidate.FormatID == ImageFormat.Jpeg.Guid) codec = candidate;
                            using (EncoderParameters parameters = new EncoderParameters(1))
                            {
                                parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)plan.JpegQuality);
                                bitmap.Save(stream, codec, parameters);
                            }
                        }
                        else bitmap.Save(stream, ImageFormat.Png);
                        stream.Flush(true);
                    }
                }
                token.ThrowIfCancellationRequested();
                using (Image check = Image.FromFile(temporary))
                    if (check.Size != size || check.RawFormat.Guid != plan.Format.Guid) throw new IOException("输出校验失败。");
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination); temporary = null;
                Report(progress, 100);
                return new ExportResult(ImageExportOutcome.Success, "导出完成。", size);
            }
            catch (OperationCanceledException) { return new ExportResult(ImageExportOutcome.Cancelled, "已取消。", size); }
            catch (ArgumentException) { return new ExportResult(ImageExportOutcome.InvalidPlan, "导出参数、尺寸或路径无效。", size); }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is System.Runtime.InteropServices.ExternalException)) throw;
                return new ExportResult(ImageExportOutcome.FileFailed, "无法保存图片；请检查目录权限、磁盘空间和目标占用。", size);
            }
            finally { if (temporary != null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        private static void Report(IProgress<int> progress, int value)
        {
            if (progress == null) return;
            try { progress.Report(value); }
            catch (Exception) { Log.Warn("Image export progress observer failed."); }
        }
    }
}
