using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ThorCapture
{
    // Light from a stick's lamp ring, worked out per pixel in 3D rather than painted
    // as flat gradients.
    //
    // The ring is sampled as a set of small emitters. Each point of the bowl gets
    // diffuse light from every emitter it can see (distance, angle to the bowl
    // surface, the ring's emission lobe) plus a glossy reflection of the ring in
    // the bowl wall. The cap is an occluder: emitters it covers stop lighting the
    // upper wall, and reflections of covered ring vanish, while light still leaks
    // out underneath it onto the floor, as on the device.
    //
    // Local coordinates are design units around the stick centre, z up, with the
    // top of the ring at z = 0.
    internal static class LampLight
    {
        private const float Reach = 78f;          // bowl lip radius plus a margin
        private const double FloorEdge = 49.5, WallTop = 70, WallHeight = 12;
        private const double CapBottom = 4, CapTop = 30;

        private struct Emitter { public double X, Y, W; }

        private static readonly List<Emitter> Emitters = BuildEmitters();

        private static List<Emitter> BuildEmitters()
        {
            List<Emitter> list = new List<Emitter>();
            double[] radii = { DeviceArt.RingInner + 4, (DeviceArt.RingInner + DeviceArt.RingOuter) / 2.0, DeviceArt.RingOuter - 3.5 };
            foreach (double r in radii)
                for (int i = 0; i < 36; i++)
                {
                    double a = (i + (r > 40 ? 0.5 : 0)) * Math.PI / 18;
                    Emitter e;
                    e.X = r * Math.Cos(a); e.Y = r * Math.Sin(a); e.W = r / 80.0;
                    list.Add(e);
                }
            return list;
        }

        // Height of the bowl surface: flat floor round the ring, a steep wall, then the flat rim.
        private static double Height(double r, out double slope)
        {
            if (r <= FloorEdge) { slope = 0; return 0; }
            if (r >= WallTop) { slope = 0; return WallHeight; }
            double t = (r - FloorEdge) / (WallTop - FloorEdge);
            slope = WallHeight * 6 * t * (1 - t) / (WallTop - FloorEdge);
            return WallHeight * t * t * (3 - 2 * t);
        }

        private static double Smooth(double a, double b, double x)
        {
            double t = Math.Max(0, Math.Min(1, (x - a) / (b - a)));
            return t * t * (3 - 2 * t);
        }

        private static int Resolution(Graphics g, double share)
        {
            float[] m = g.Transform.Elements;
            double pixels = Math.Sqrt(Math.Abs(m[0] * m[3] - m[1] * m[2]));
            return Math.Max(56, Math.Min(240, (int)Math.Ceiling(2 * Reach * pixels * share)));
        }

        // How much of the ring's light the cap blocks at this point, softened over a few
        // units because the ring is an area light, not a point.
        private static double Coverage(double x, double y, double capX, double capY, double capR)
        {
            double dx = x - capX, dy = y - capY;
            double distance = dx * dx + dy * dy;
            if (distance >= (capR + 3) * (capR + 3)) return 0;
            if (distance <= (capR - 3) * (capR - 3)) return 1;
            return Smooth(capR + 3, capR - 3, Math.Sqrt(distance));
        }

        // Diffuse and glossy light on the bowl, drawn before the cap.
        public static void Bowl(Graphics g, DeviceSkin s, float cx, float cy, double capX, double capY, double capR)
        {
            if (s.Glow.IsEmpty) return;
            int n = Resolution(g, 0.45);
            double step = 2.0 * Reach / n;
            // Reference: a vertical wall facing the ring from 8 units out, unshadowed.
            double reference = Irradiance(DeviceArt.RingOuter + 8, 0, 4, -1, 0, 0, 1e9, 0, 0, false);
            double level = s.GlowLevel;
            Color glossColor = Ui.Mix(s.Glow, Color.White, 0.3f);
            // Diffuse light is scaled by how much the bowl plastic reflects: black plastic only
            // picks up a faint wash, while its glossy reflection stays as strong as on white.
            double albedo = Math.Max(0.16, Math.Min(1, (0.3 * s.Well.R + 0.59 * s.Well.G + 0.11 * s.Well.B) / 225.0));
            int[] pixels = new int[n * n];
            for (int j = 0; j < n; j++)
            {
                double y = -Reach + (j + 0.5) * step;
                for (int i = 0; i < n; i++)
                {
                    double x = -Reach + (i + 0.5) * step;
                    double r = Math.Sqrt(x * x + y * y);
                    if (r < DeviceArt.RingOuter - 1.5 || r > 77.5) continue;
                    // Pixels outside the faded rim contribute nothing, regardless of the cap.
                    double fade = Smooth(DeviceArt.RingOuter - 1.5, DeviceArt.RingOuter + 0.5, r) * (1 - Smooth(WallTop - 6, WallTop + 1, r));
                    if (fade == 0) continue;
                    double slope, z = Height(r, out slope);
                    // The wall faces inwards and up.
                    double nx = -slope * x / r, ny = -slope * y / r, nz = 1;
                    double nl = Math.Sqrt(nx * nx + ny * ny + 1);
                    nx /= nl; ny /= nl; nz /= nl;
                    double gloss;
                    double diffuse = Irradiance(x, y, z, nx, ny, nz, capR, capX, capY, true, out gloss) / reference;
                    // Fade out as the wall rolls over into the rim, so there is no hard outer edge.
                    // Falls off with distance from the ring instead of flooding the whole wall.
                    double matte = 0.32 * albedo * Math.Min(1.3, diffuse), shine = 0.3 * (1 - Math.Exp(-gloss / reference / 0.8));
                    double a = Math.Min(0.78, (matte + shine) * level) * fade;
                    if (a <= 0.004) continue;
                    // Whiter where the glossy reflection dominates.
                    Color c = Ui.Mix(s.Glow, glossColor, (float)(shine / (matte + shine + 1e-9)));
                    pixels[j * n + i] = ((int)(a * 255) << 24) | (c.R << 16) | (c.G << 8) | c.B;
                }
            }
            Blit(g, pixels, n, cx, cy, null);
        }

        private static double Irradiance(double x, double y, double z, double nx, double ny, double nz, double capR, double capX, double capY, bool withCap)
        {
            double gloss;
            return Irradiance(x, y, z, nx, ny, nz, capR, capX, capY, withCap, out gloss);
        }

        private static double Irradiance(double x, double y, double z, double nx, double ny, double nz, double capR, double capX, double capY, bool withCap, out double gloss)
        {
            double sum = 0;
            gloss = 0;
            const double ez = 0.5;
            foreach (Emitter e in Emitters)
            {
                double lx = e.X - x, ly = e.Y - y, lz = ez - z;
                double d2 = lx * lx + ly * ly + lz * lz, d = Math.Sqrt(d2);
                lx /= d; ly /= d; lz /= d;
                double facing = lx * nx + ly * ny + lz * nz;
                if (facing <= 0) continue;
                if (withCap && z > CapBottom)
                {
                    // The path rises through the cap's height range only near the bowl point.
                    double t0 = (CapBottom - ez) / (z - ez);
                    double t1 = Math.Min(1, (CapTop - ez) / (z - ez));
                    double cover = 0;
                    for (int k = 0; k <= 2; k++)
                    {
                        double t = t0 + (t1 - t0) * k / 2.0;
                        cover = Math.Max(cover, Coverage(e.X + (x - e.X) * t, e.Y + (y - e.Y) * t, capX, capY, capR));
                    }
                    if (cover >= 0.999) continue;
                    facing *= 1 - cover;
                }
                // The frosted ring throws most light upwards but also sideways out of its edge.
                double lobe = 0.35 + 0.65 * Math.Max(0, -lz);
                double falloff = e.W * lobe / (d2 + 12);
                sum += facing * falloff;
                // Glossy reflection towards a viewer straight above (Blinn, exponent 64): a soft,
                // peaked band on the wall rather than a flat ring of colour.
                double hx = lx, hy = ly, hz = lz + 1;
                double hl = Math.Sqrt(hx * hx + hy * hy + hz * hz);
                double nh = (hx * nx + hy * ny + hz * nz) / hl;
                if (nh > 0)
                {
                    double p2 = nh * nh, p4 = p2 * p2, p8 = p4 * p4, p16 = p8 * p8, p32 = p16 * p16;
                    gloss += p32 * p32 * falloff * 6;
                }
            }
            return sum;
        }

        private static void Blit(Graphics g, int[] pixels, int n, float cx, float cy, GraphicsPath clip)
        {
            using (Bitmap light = new Bitmap(n, n, PixelFormat.Format32bppArgb))
            {
                BitmapData data = light.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                for (int row = 0; row < n; row++)
                    Marshal.Copy(pixels, row * n, new IntPtr(data.Scan0.ToInt64() + (long)row * data.Stride), n);
                light.UnlockBits(data);
                GraphicsState state = g.Save();
                if (clip != null) g.SetClip(clip, CombineMode.Intersect);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                using (ImageAttributes attributes = new ImageAttributes())
                {
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(light, new PointF[] { new PointF(cx - Reach, cy - Reach), new PointF(cx + Reach, cy - Reach), new PointF(cx - Reach, cy + Reach) },
                        new RectangleF(0, 0, n, n), GraphicsUnit.Pixel, attributes);
                }
                g.Restore(state);
            }
        }
    }
}
