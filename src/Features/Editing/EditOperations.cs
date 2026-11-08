using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace ThorCapture.Features.Editing
{
    public abstract class EditOp
    {
        public abstract Rectangle Affects(Size canvas);
        internal virtual Size NextSize(Size canvas) { return canvas; }
        internal abstract Bitmap Execute(Bitmap canvas, CancellationToken token);
        internal abstract string Summary { get; }
        internal static Rectangle Clip(Rectangle area, Size canvas)
        {
            if (area.Width <= 0 || area.Height <= 0 || (long)area.X + area.Width > Int32.MaxValue || (long)area.Y + area.Height > Int32.MaxValue) throw new ArgumentException("Invalid area");
            Rectangle clipped = Rectangle.Intersect(new Rectangle(Point.Empty, canvas), area);
            if (clipped.Width <= 0 || clipped.Height <= 0) throw new ArgumentException("Area outside canvas");
            return clipped;
        }
        internal static void Number(float value, float min, float max)
        { if (Single.IsNaN(value) || Single.IsInfinity(value) || value < min || value > max) throw new ArgumentOutOfRangeException("value"); }
        internal static PointF ClipPoint(PointF point, Size canvas)
        {
            if (Single.IsNaN(point.X) || Single.IsInfinity(point.X) || Single.IsNaN(point.Y) || Single.IsInfinity(point.Y)) throw new ArgumentException("Invalid point");
            return new PointF(Math.Max(0, Math.Min(canvas.Width - 1, point.X)), Math.Max(0, Math.Min(canvas.Height - 1, point.Y)));
        }
        internal static string Rect(Rectangle value) { return value.X + "," + value.Y + "," + value.Width + "," + value.Height; }
        internal static string PointText(PointF value) { return value.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + value.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture); }
        internal static string Float(float value) { return value.ToString("R", System.Globalization.CultureInfo.InvariantCulture); }
    }
    public sealed class CropOp : EditOp
    {
        public readonly Rectangle Area;
        public CropOp(Rectangle area) { Area = area; }
        public override Rectangle Affects(Size canvas) { return Clip(Area, canvas); }
        internal override Size NextSize(Size canvas) { return Affects(canvas).Size; }
        internal override Bitmap Execute(Bitmap canvas, CancellationToken token) { return canvas.Clone(Affects(canvas.Size), PixelFormat.Format32bppArgb); }
        internal override string Summary { get { return "crop:" + Rect(Area); } }
    }
    public sealed class RotateOp : EditOp
    {
        public readonly int QuarterTurns;
        public RotateOp(int quarterTurns) { QuarterTurns = ((quarterTurns % 4) + 4) % 4; }
        public override Rectangle Affects(Size canvas) { return new Rectangle(Point.Empty, canvas); }
        internal override Size NextSize(Size canvas) { return QuarterTurns % 2 == 0 ? canvas : new Size(canvas.Height, canvas.Width); }
        internal override Bitmap Execute(Bitmap canvas, CancellationToken token)
        { Bitmap result = new Bitmap(canvas); result.RotateFlip((RotateFlipType)QuarterTurns); return result; }
        internal override string Summary { get { return "rotate:" + QuarterTurns; } }
    }
    public enum CoverStyle { Solid, Pixelate }
    public sealed class CoverOp : EditOp
    {
        public readonly Rectangle Area;
        public readonly CoverStyle Style;
        public readonly Color Color;
        public readonly int BlockSize;
        public CoverOp(Rectangle area) : this(area, CoverStyle.Solid, System.Drawing.Color.Black, 24) { }
        public CoverOp(Rectangle area, CoverStyle style, Color color, int blockSize)
        {
            if (style != CoverStyle.Solid && style != CoverStyle.Pixelate) throw new ArgumentException("style");
            if (blockSize < 4 || blockSize > 256) throw new ArgumentOutOfRangeException("blockSize");
            Area = area; Style = style; Color = System.Drawing.Color.FromArgb(255, color.R, color.G, color.B); BlockSize = blockSize;
        }
        public override Rectangle Affects(Size canvas) { return Clip(Area, canvas); }
        internal override Bitmap Execute(Bitmap canvas, CancellationToken token)
        {
            Rectangle area = Affects(canvas.Size);
            if (Style == CoverStyle.Solid)
            { using (Graphics graphics = Graphics.FromImage(canvas)) using (Brush brush = new SolidBrush(Color)) { graphics.CompositingMode = CompositingMode.SourceCopy; graphics.FillRectangle(brush, area); } }
            else
            {
                BitmapData data = canvas.LockBits(area, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = area.Width * 4; byte[] pixels = new byte[checked(stride * area.Height)];
                    for (int y = 0; y < area.Height; y++) { token.ThrowIfCancellationRequested(); Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * stride, stride); }
                    for (int by = 0; by < area.Height; by += BlockSize)
                    {
                        token.ThrowIfCancellationRequested();
                        for (int bx = 0; bx < area.Width; bx += BlockSize)
                        {
                            int width = Math.Min(BlockSize, area.Width - bx), height = Math.Min(BlockSize, area.Height - by);
                            long b = 0, g = 0, r = 0, a = 0;
                            for (int y = by; y < by + height; y++) for (int x = bx; x < bx + width; x++)
                            { int index = y * stride + x * 4; b += pixels[index]; g += pixels[index + 1]; r += pixels[index + 2]; a += pixels[index + 3]; }
                            int count = width * height;
                            for (int y = by; y < by + height; y++) for (int x = bx; x < bx + width; x++)
                            { int index = y * stride + x * 4; pixels[index] = (byte)(b / count); pixels[index + 1] = (byte)(g / count); pixels[index + 2] = (byte)(r / count); pixels[index + 3] = (byte)(a / count); }
                        }
                    }
                    for (int y = 0; y < area.Height; y++) { token.ThrowIfCancellationRequested(); Marshal.Copy(pixels, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), stride); }
                }
                finally { canvas.UnlockBits(data); }
            }
            return canvas;
        }
        internal override string Summary { get { return "cover:" + Rect(Area) + ":" + Style + ":" + Color.ToArgb() + ":" + BlockSize; } }
    }
    public sealed class ArrowOp : EditOp
    {
        public readonly PointF From, To;
        public readonly Color Color;
        public readonly float Width;
        public ArrowOp(PointF from, PointF to, Color color, float width)
        { Number(width, 1, 64); From = from; To = to; Color = color; Width = width; }
        public override Rectangle Affects(Size canvas) { ClipPoint(From, canvas); ClipPoint(To, canvas); return new Rectangle(Point.Empty, canvas); }
        internal override Bitmap Execute(Bitmap canvas, CancellationToken token)
        {
            using (Graphics graphics = Graphics.FromImage(canvas)) using (Pen pen = new Pen(Color, Width))
            using (AdjustableArrowCap cap = new AdjustableArrowCap(3, 4, true))
            { graphics.SmoothingMode = SmoothingMode.AntiAlias; pen.CustomEndCap = cap; graphics.DrawLine(pen, ClipPoint(From, canvas.Size), ClipPoint(To, canvas.Size)); }
            return canvas;
        }
        internal override string Summary { get { return "arrow:" + PointText(From) + ":" + PointText(To) + ":" + Color.ToArgb() + ":" + Float(Width); } }
    }
    public sealed class BoxOp : EditOp
    {
        public readonly Rectangle Area;
        public readonly Color Color;
        public readonly float Width;
        public BoxOp(Rectangle area, Color color, float width) { Number(width, 1, 64); Area = area; Color = color; Width = width; }
        public override Rectangle Affects(Size canvas) { return Clip(Area, canvas); }
        internal override Bitmap Execute(Bitmap canvas, CancellationToken token)
        {
            Rectangle area = Affects(canvas.Size);
            using (Graphics graphics = Graphics.FromImage(canvas)) using (Pen pen = new Pen(Color, Width))
            { pen.Alignment = PenAlignment.Inset; graphics.DrawRectangle(pen, area.X, area.Y, Math.Max(0, area.Width - 1), Math.Max(0, area.Height - 1)); }
            return canvas;
        }
        internal override string Summary { get { return "box:" + Rect(Area) + ":" + Color.ToArgb() + ":" + Float(Width); } }
    }
    public sealed class TextOp : EditOp
    {
        public readonly PointF At;
        public readonly string Text;
        public readonly Color Color;
        public readonly float SizePx;
        public TextOp(PointF at, string text, Color color, float sizePx)
        { Number(sizePx, 6, 256); if (String.IsNullOrWhiteSpace(text) || text.Length > 1024) throw new ArgumentException("text"); At = at; Text = text; Color = color; SizePx = sizePx; }
        public override Rectangle Affects(Size canvas) { ClipPoint(At, canvas); return new Rectangle(Point.Empty, canvas); }
        internal override Bitmap Execute(Bitmap canvas, CancellationToken token)
        {
            using (Graphics graphics = Graphics.FromImage(canvas)) using (Font font = new Font("Microsoft YaHei", SizePx, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Brush brush = new SolidBrush(Color))
            { graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; graphics.DrawString(Text, font, brush, ClipPoint(At, canvas.Size), StringFormat.GenericTypographic); }
            return canvas;
        }
        internal override string Summary { get { return "text:" + PointText(At) + ":" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Text)) + ":" + Color.ToArgb() + ":" + Float(SizePx); } }
    }
}
