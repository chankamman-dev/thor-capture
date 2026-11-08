using System;
using ThorCapture.Backend;

namespace ThorCapture.Features.RemoteControl
{
    // All arguments are in the same caller coordinate space (for example DPI-adjusted
    // client pixels). The rectangle is actual image content, excluding shell/black bars.
    internal sealed class ControlViewport
    {
        public readonly DisplayTarget Display;
        public readonly long Revision;
        public readonly double Left, Top, Width, Height;
        public readonly int Rotation, CropLeft, CropTop, CropWidth, CropHeight;
        public ControlViewport(DisplayTarget display, long revision, double left, double top, double width, double height,
            int rotation, int cropLeft, int cropTop, int cropWidth, int cropHeight)
        {
            if (display == null || revision < 0 || !Finite(left) || !Finite(top) || !Finite(width) || !Finite(height) || width <= 0 || height <= 0
                || (rotation != 0 && rotation != 90 && rotation != 180 && rotation != 270) || cropLeft < 0 || cropTop < 0 || cropWidth < 1 || cropHeight < 1
                || (long)cropLeft + cropWidth > display.Width || (long)cropTop + cropHeight > display.Height) throw new ArgumentException("Invalid control viewport");
            Display = display; Revision = revision; Left = left; Top = top; Width = width; Height = height;
            Rotation = rotation; CropLeft = cropLeft; CropTop = cropTop; CropWidth = cropWidth; CropHeight = cropHeight;
        }
        private static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
        public bool TryMap(double x, double y, out int pixelX, out int pixelY)
        {
            pixelX = pixelY = 0;
            if (!Finite(x) || !Finite(y) || x < Left || y < Top || x >= Left + Width || y >= Top + Height) return false;
            double u = (x - Left) / Width, v = (y - Top) / Height, a = u, b = v;
            if (Rotation == 90) { a = v; b = 1 - u; }
            if (Rotation == 180) { a = 1 - u; b = 1 - v; }
            if (Rotation == 270) { a = 1 - v; b = u; }
            pixelX = CropLeft + Math.Min(CropWidth - 1, (int)(a * CropWidth));
            pixelY = CropTop + Math.Min(CropHeight - 1, (int)(b * CropHeight));
            return true;
        }
    }
}
