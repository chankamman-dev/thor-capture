using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace ThorCapture
{
    // A display-only copy of an image rendered at one target size. It is rebuilt only when
    // the source object or the size changes; a live stream hands over a new frame object
    // each time, so new frames always redraw. The bitmap is reused when the size matches.
    internal sealed class ScaledImage : IDisposable
    {
        private Image source;
        private Bitmap bitmap;
        internal long Renders;

        public Bitmap Get(Image picture, Size size, Action<Graphics> render)
        {
            size = new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
            if (bitmap != null && ReferenceEquals(source, picture) && bitmap.Size == size) return bitmap;
            if (bitmap == null || bitmap.Size != size)
            {
                if (bitmap != null) bitmap.Dispose();
                bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            }
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                render(g);
            }
            source = picture;
            Renders++;
            return bitmap;
        }

        public void Dispose()
        {
            if (bitmap != null) { bitmap.Dispose(); bitmap = null; }
            source = null;
        }
    }
}
