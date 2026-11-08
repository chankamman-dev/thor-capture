using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace ThorCapture
{
    // Small reusable layers: a D-pad change must not rebuild either stick or lamp.
    internal sealed class ControlLayers : IDisposable
    {
        private readonly Bitmap[] layers = new Bitmap[ShellGeometry.PartCount];
        private readonly PadState[] states = new PadState[ShellGeometry.PartCount];
        private readonly Rectangle[] positions = new Rectangle[ShellGeometry.PartCount];
        private DeviceSkin skin;
        private RectangleF bounds;
        internal readonly long[] RenderCounts = new long[ShellGeometry.PartCount];

        internal static bool SamePart(PadState a, PadState b, int part)
        {
            if (a == null || b == null) return a == b;
            if (part == 0) return a.LX == b.LX && a.LY == b.LY && a.Press(PadButton.L3) == b.Press(PadButton.L3);
            if (part == 3) return a.RX == b.RX && a.RY == b.RY && a.Press(PadButton.R3) == b.Press(PadButton.R3);
            if (part == 1) return (a.Animated ? a.TiltX : a.DirectionX) == (b.Animated ? b.TiltX : b.DirectionX)
                && (a.Animated ? a.TiltY : a.DirectionY) == (b.Animated ? b.TiltY : b.DirectionY);
            if (part == 2)
            {
                foreach (PadButton button in ShellGeometry.FaceButtons) if (a.Press(button) != b.Press(button)) return false;
            }
            if (part >= 4 && part <= 7) return a.Press(ShellGeometry.MiniButtons[part - 4]) == b.Press(ShellGeometry.MiniButtons[part - 4]);
            if (part == 9) return (a.Buttons & (PadButton.L1 | PadButton.L2 | PadButton.R1 | PadButton.R2))
                == (b.Buttons & (PadButton.L1 | PadButton.L2 | PadButton.R1 | PadButton.R2)) && (a.LT > .05f) == (b.LT > .05f) && (a.RT > .05f) == (b.RT > .05f);
            return true;
        }

        internal void Paint(Graphics graphics, RectangleF area, DeviceSkin currentSkin, PadState input)
        {
            if (ShellGeometry.Scale(area) <= 0) return;
            if (skin != currentSkin || bounds != area)
            {
                Clear(); skin = currentSkin; bounds = area;
                for (int i = 0; i < positions.Length; i++) positions[i] = ShellGeometry.ToClient(area, ShellGeometry.Areas[i]);
            }
            PointF origin = ShellGeometry.Origin(area);
            float scale = ShellGeometry.Scale(area);
            for (int i = 0; i < layers.Length; i++)
            {
                Rectangle position = positions[i];
                // Parts outside this paint's clip are neither rebuilt nor drawn; their stored
                // state stays stale so the next paint that covers them rebuilds correctly.
                if (!graphics.IsVisible(position)) continue;
                if (layers[i] == null || !SamePart(states[i], input, i))
                {
                    if (layers[i] == null) layers[i] = new Bitmap(Math.Max(1, position.Width), Math.Max(1, position.Height), PixelFormat.Format32bppPArgb);
                    using (Graphics canvas = Graphics.FromImage(layers[i]))
                    {
                        canvas.Clear(Color.Transparent);
                        canvas.TranslateTransform(origin.X - position.X, origin.Y - position.Y);
                        canvas.ScaleTransform(scale, scale);
                        canvas.SmoothingMode = SmoothingMode.AntiAlias;
                        canvas.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        canvas.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        DeviceArt.DrawControlPart(canvas, skin, input, i);
                    }
                    states[i] = input.Copy(); RenderCounts[i]++;
                }
                graphics.DrawImageUnscaled(layers[i], position.Location);
            }
        }

        internal void Clear()
        {
            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i] != null) layers[i].Dispose();
                layers[i] = null; states[i] = null;
            }
        }
        public void Dispose() { Clear(); }
    }
}
