using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ThorCapture
{
    // The stick cap as a lathed solid, tipped about a pivot below the housing and
    // drawn as depth-sorted facets under a slight top-front view, so that, as in
    // photos of the device, a sliver of the side wall shows even at rest.
    //
    // Profile from close-ups of the retail cap: a smooth, barely dished top about
    // 70% of the width, inside a wide diamond-knurled band that wraps over the
    // rounded shoulder and down the side wall. Only the cap moves; the housing,
    // lamp ring and pivot dome are drawn by DeviceArt and stay put.
    internal static class Stick3D
    {
        private struct V
        {
            public double X, Y, Z;
            public V(double x, double y, double z) { X = x; Y = y; Z = z; }
        }

        private sealed class Facet
        {
            public PointF[] Points;
            public double Depth;
            public Color Color;
        }

        private struct Pose
        {
            private readonly double nx, ny, c, s, scale, drop;
            public Pose(double x, double y, double angle, double size, double down)
            { nx = x; ny = y; c = Math.Cos(angle); s = Math.Sin(angle); scale = size; drop = down; }
            public V Rotate(V p)
            {
                double dot = -ny * p.X + nx * p.Y;
                return new V(p.X * c + nx * p.Z * s - ny * dot * (1 - c),
                    p.Y * c + ny * p.Z * s + nx * dot * (1 - c), p.Z * c + (-ny * p.Y - nx * p.X) * s);
            }
            public V Tip(V p)
            {
                V q = Rotate(new V(p.X * scale, p.Y * scale, p.Z - Pivot));
                q.Z += Pivot - drop; return q;
            }
        }

        private sealed class Angles
        {
            public readonly double[] Cos, Sin, MidCos, MidSin;
            public Angles(int segments)
            {
                Cos = new double[segments + 1]; Sin = new double[segments + 1];
                MidCos = new double[segments]; MidSin = new double[segments];
                for (int i = 0; i <= segments; i++)
                {
                    double a = i * 2 * Math.PI / segments;
                    Cos[i] = Math.Cos(a); Sin[i] = Math.Sin(a);
                    if (i < segments)
                    {
                        double mid = (a + (i + 1) * 2 * Math.PI / segments) / 2;
                        MidCos[i] = Math.Cos(mid); MidSin[i] = Math.Sin(mid);
                    }
                }
            }
        }
        // Only 45 possible segment counts; shared immutable geometry is safe for video exports.
        private static readonly Dictionary<int, Angles> angles = new Dictionary<int, Angles>();
        private static Angles Rays(int segments)
        {
            lock (angles)
            {
                Angles value;
                if (!angles.TryGetValue(segments, out value)) angles.Add(segments, value = new Angles(segments));
                return value;
            }
        }
        private static readonly V[] LampSamples = BuildLampSamples();
        private static V[] BuildLampSamples()
        {
            List<V> samples = new List<V>();
            foreach (double radius in new double[] { DeviceArt.RingInner + 5, DeviceArt.RingOuter - 4 })
                for (int i = 0; i < 16; i++) samples.Add(new V(radius * Math.Cos(i * Math.PI / 8), radius * Math.Sin(i * Math.PI / 8), 0));
            return samples.ToArray();
        }

        // Profile, outside in: flat underside, then a knurled "tyre" whose cross-section is an
        // elliptical arc from under the cap, round the widest point, over the shoulder, then a
        // short smooth lip down to the convex top. Measured against close-up photos.
        private static readonly double[] ProfileR, ProfileZ;
        private static readonly int KnurlFirst, KnurlLast;
        private const double TyreCentreR = 34.5, TyreCentreZ = 17, TyreHalfWidth = 8.5, TyreHalfHeight = 10.5;
        private const double DomeEdgeR = 29.5, DomeEdgeZ = 26.3, DomeRise = 4.4;

        static Stick3D()
        {
            List<double> r = new List<double>(), z = new List<double>();
            r.Add(27); z.Add(7.2);
            for (int deg = -90; deg <= 105; deg += 15)
            {
                double t = deg * Math.PI / 180;
                r.Add(TyreCentreR + TyreHalfWidth * Math.Cos(t));
                z.Add(TyreCentreZ + TyreHalfHeight * Math.Sin(t));
            }
            KnurlFirst = 1;
            KnurlLast = r.Count - 2;
            r.Add(DomeEdgeR); z.Add(DomeEdgeZ);
            ProfileR = r.ToArray();
            ProfileZ = z.ToArray();
        }
        private const double Pivot = -30;      // gimbal centre, well below the housing surface
        private const double MaxTilt = 0.38;   // radians at full deflection, about 22 degrees
        private const double Oblique = 0.18;   // screen y = y - (z - Reference) * Oblique
        private const double Reference = 18;   // height that projects onto the housing centre

        private static readonly V Key = Unit(new V(-0.36, -0.46, 0.81));
        private static readonly V View = Unit(new V(0, Oblique, 1));
        private static readonly V Half = Unit(new V(Key.X + View.X, Key.Y + View.Y, Key.Z + View.Z));

        private static V Unit(V v)
        {
            double length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return length < 1e-9 ? v : new V(v.X / length, v.Y / length, v.Z / length);
        }

        private static double Dot(V a, V b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        // Rodrigues rotation about the horizontal axis perpendicular to (nx, ny):
        // a positive angle tips the top of the cap towards the input direction.
        private static V Rotate(V p, double nx, double ny, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle), dot = -ny * p.X + nx * p.Y;
            return new V(p.X * c + nx * p.Z * s - ny * dot * (1 - c),
                         p.Y * c + ny * p.Z * s + nx * dot * (1 - c),
                         p.Z * c + (-ny * p.Y - nx * p.X) * s);
        }

        private static V Tip(V p, double nx, double ny, double angle, double scale, double drop)
        {
            V q = new V(p.X * scale, p.Y * scale, p.Z - Pivot);
            q = Rotate(q, nx, ny, angle);
            q.Z += Pivot - drop;
            return q;
        }

        private static PointF Project(V v, float cx, float cy)
        {
            return new PointF(cx + (float)v.X, cy + (float)(v.Y - (v.Z - Reference) * Oblique));
        }

        private static int Byte(double value) { return Math.Max(0, Math.Min(255, (int)Math.Round(value))); }

        // Matte rubber under a soft key light from the upper left.
        private static Color Shade(Color material, V n, double gain)
        {
            n = Unit(n);
            double diffuse = Math.Max(0, Dot(n, Key));
            double spec = Math.Pow(Math.Max(0, Dot(n, Half)), 18) * 0.06;
            double amount = (0.52 + 0.6 * diffuse) * gain;
            return Color.FromArgb(Byte(material.R * amount + 255 * spec), Byte(material.G * amount + 255 * spec), Byte(material.B * amount + 255 * spec));
        }

        // Light from the uncovered part of the fixed ring falling on a point of the cap.
        private static double LampEnergy(V p, V n, double coverX, double coverY, double coverRadius)
        {
            n = Unit(n);
            double energy = 0;
            foreach (V emitter in LampSamples)
                {
                    double ex = emitter.X, ey = emitter.Y;
                    double hx = ex - coverX, hy = ey - coverY;
                    if (hx * hx + hy * hy < coverRadius * coverRadius) continue; // under the cap
                    double dx = ex - p.X, dy = ey - p.Y, dz = -p.Z;
                    double d2 = dx * dx + dy * dy + dz * dz, d = Math.Sqrt(d2);
                    double facing = Math.Max(0, (dx * n.X + dy * n.Y + dz * n.Z) / d);
                    double emit = Math.Max(0, p.Z / d); // the ring faces up
                    energy += facing * (0.35 + 0.65 * emit) * 140 / (d2 + 60);
                }
            return Math.Min(0.55, energy);
        }

        private static Color AddLight(Color surface, Color lamp, double energy)
        {
            if (lamp.IsEmpty || energy <= 0) return surface;
            return Color.FromArgb(Byte(surface.R + lamp.R * energy), Byte(surface.G + lamp.G * energy), Byte(surface.B + lamp.B * energy));
        }

        // Centre and radius of the cap's footprint over the housing, relative to the stick centre.
        public static void Footprint(float x, float y, bool pressed, out double capX, out double capY, out double capR)
        {
            Footprint(x, y, pressed ? 1f : 0f, out capX, out capY, out capR);
        }

        public static void Footprint(float x, float y, float pressure, out double capX, out double capY, out double capR)
        {
            double travel = Math.Sqrt(x * x + y * y);
            double nx = travel < 1e-4 ? 1 : x / travel, ny = travel < 1e-4 ? 0 : y / travel;
            double scale = 1 - .025 * pressure;
            V foot = Tip(new V(0, 0, 9), nx, ny, Math.Min(1, travel) * MaxTilt, scale, 2.5 * pressure);
            capX = foot.X;
            capY = foot.Y;
            capR = 43 * scale;
        }

        // Draws the cap and returns its outline (convex hull) in the caller's coordinates.
        public static PointF[] Draw(Graphics g, DeviceSkin skin, float cx, float cy, float x, float y, bool pressed)
        {
            return Draw(g, skin, cx, cy, x, y, pressed ? 1f : 0f);
        }

        public static PointF[] Draw(Graphics g, DeviceSkin skin, float cx, float cy, float x, float y, float pressure)
        {
            double travel = Math.Sqrt(x * x + y * y);
            double nx = travel < 1e-4 ? 1 : x / travel, ny = travel < 1e-4 ? 0 : y / travel;
            travel = Math.Min(1, travel);
            double angle = travel * MaxTilt;
            double scale = 1 - .025 * pressure;
            double drop = 2.5 * pressure;
            Pose pose = new Pose(nx, ny, angle, scale, drop);
            Color material = Ui.Mix(skin.Cap, Color.Black, .07f * pressure);
            bool lit = !skin.Glow.IsEmpty;

            // Facet count follows the on-screen size so the knurl stays a texture, not noise.
            float[] m = g.Transform.Elements;
            double pixels = Math.Sqrt(Math.Abs(m[0] * m[3] - m[1] * m[2]));
            int segments = Math.Max(72, Math.Min(160, (int)(2 * Math.PI * 43 * Math.Max(0.4, pixels) / 2.2)));
            segments -= segments % 2;
            Angles rays = Rays(segments);
            double pitch = 2 * Math.PI * 43 / segments;
            double knurl = 0.16 * Math.Max(0, Math.Min(1, (pitch * pixels - 1.2) / 2.0));

            V foot = pose.Tip(new V(0, 0, 9));
            PointF footCentre = Project(foot, cx, cy);

            // Contact shadow: the cap blocks the lamp and the room light just under its edge.
            using (GraphicsPath shadow = new GraphicsPath())
            {
                float r = (float)(43 * scale + 3.5 - .5 * pressure);
                shadow.AddEllipse(footCentre.X + 0.8f - r, footCentre.Y + 2.5f - r, r * 2, r * 2);
                using (PathGradientBrush fill = new PathGradientBrush(shadow))
                {
                    ColorBlend blend = new ColorBlend();
                    // Soft-edged: the room light is broad, so the contact shadow fades out gradually.
                    blend.Colors = new Color[] { Color.FromArgb(0, 0, 0, 0), Color.FromArgb(lit ? 70 : 40, 0, 0, 0), Color.FromArgb(lit ? 120 : 70, 0, 0, 0) };
                    blend.Positions = new float[] { 0f, 0.3f, 1f };
                    fill.InterpolationColors = blend;
                    g.FillPath(fill, shadow);
                }
            }

            // Subdivide the knurled bands so each bump is roughly square.
            List<double> pr = new List<double>(), pz = new List<double>();
            List<int> bump = new List<int>(); // knurl row index, or -1 for smooth
            pr.Add(ProfileR[0]); pz.Add(ProfileZ[0]);
            int row = 0;
            for (int i = 0; i < ProfileR.Length - 1; i++)
            {
                double dr = ProfileR[i + 1] - ProfileR[i], dz = ProfileZ[i + 1] - ProfileZ[i];
                bool textured = i >= KnurlFirst && i <= KnurlLast;
                int steps = textured ? Math.Max(1, (int)Math.Round(Math.Sqrt(dr * dr + dz * dz) / pitch)) : 1;
                for (int k = 1; k <= steps; k++)
                {
                    pr.Add(ProfileR[i] + dr * k / steps);
                    pz.Add(ProfileZ[i] + dz * k / steps);
                    bump.Add(textured ? row++ : -1);
                }
            }

            List<Facet> facets = new List<Facet>();
            for (int band = 0; band < bump.Count; band++)
            {
                double r0 = pr[band], r1 = pr[band + 1], z0 = pz[band], z1 = pz[band + 1];
                double br = r1 - r0, bz = z1 - z0, bl = Math.Sqrt(br * br + bz * bz);
                for (int seg = 0; seg < segments; seg++)
                {
                    double cosm = rays.MidCos[seg], sinm = rays.MidSin[seg];
                    // Outward unit normal of this band in the profile plane.
                    V n = new V(bz * cosm / bl, bz * sinm / bl, -br / bl);
                    if (Dot(pose.Rotate(n), View) <= 0.02) continue;
                    double gain = 0.95;
                    if (bump[band] >= 0)
                    {
                        // Diamond knurl seen from above is a staggered lattice of lit bump tops:
                        // a checkerboard of lit and shaded facets, plus slight per-bump variation.
                        // Contrast fades out when the bumps would be smaller than a pixel or two.
                        int r2 = bump[band];
                        int hash = unchecked(seg * 73856093 ^ r2 * 19349663);
                        double jitter = ((hash >> 7) & 255) / 255.0 - 0.5;
                        gain = 1.02 + knurl * (((seg + r2) % 2 == 0 ? 1 : -1) + 0.6 * jitter);
                    }
                    V turned = pose.Rotate(n);

                    V[] corners =
                    {
                        new V(r0 * rays.Cos[seg], r0 * rays.Sin[seg], z0), new V(r0 * rays.Cos[seg + 1], r0 * rays.Sin[seg + 1], z0),
                        new V(r1 * rays.Cos[seg + 1], r1 * rays.Sin[seg + 1], z1), new V(r1 * rays.Cos[seg], r1 * rays.Sin[seg], z1)
                    };
                    PointF[] points = new PointF[4];
                    double depth = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        V world = pose.Tip(corners[i]);
                        points[i] = Project(world, cx, cy);
                        depth += world.Z + world.Y * Oblique;
                    }
                    V centre = pose.Tip(new V((r0 + r1) * 0.5 * cosm, (r0 + r1) * 0.5 * sinm, (z0 + z1) * 0.5));
                    Color color = Shade(material, turned, gain);
                    if (lit) color = AddLight(color, skin.Glow, LampEnergy(centre, turned, foot.X, foot.Y, 43 * scale) * skin.GlowLevel);
                    facets.Add(new Facet { Points = points, Depth = depth, Color = color });
                }
            }
            facets.Sort(delegate(Facet p, Facet q) { return p.Depth.CompareTo(q.Depth); });

            SmoothingMode smoothing = g.SmoothingMode;
            PixelOffsetMode offset = g.PixelOffsetMode;
            // Unsmoothed fills leave no hairline cracks between facets; the silhouette is smoothed below.
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            foreach (Facet facet in facets)
                using (SolidBrush fill = new SolidBrush(facet.Color)) g.FillPolygon(fill, facet.Points);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = offset;

            // Smooth convex top, Gouraud-shaded from its apex to its rim, with a soft sheen
            // where its normal lines up with the key light.
            // Same angular steps as the knurled band, and a hair wider, so no seam opens between them.
            int ring = segments;
            PointF[] dome = new PointF[ring];
            Color[] rimColours = new Color[ring];
            double slope = 2 * DomeRise / DomeEdgeR;
            for (int seg = 0; seg < ring; seg++)
            {
                dome[seg] = Project(pose.Tip(new V((DomeEdgeR + 0.6) * rays.Cos[seg], (DomeEdgeR + 0.6) * rays.Sin[seg], DomeEdgeZ)), cx, cy);
                V normal = pose.Rotate(Unit(new V(slope * rays.Cos[seg], slope * rays.Sin[seg], 1)));
                rimColours[seg] = Shade(material, normal, 0.9);
            }
            PointF apex = Project(pose.Tip(new V(0, 0, DomeEdgeZ + DomeRise)), cx, cy);
            using (GraphicsPath top = new GraphicsPath())
            {
                top.AddPolygon(dome);
                using (PathGradientBrush fill = new PathGradientBrush(top))
                {
                    fill.CenterPoint = apex;
                    fill.CenterColor = Shade(material, pose.Rotate(new V(0, 0, 1)), 0.9);
                    fill.SurroundColors = rimColours;
                    g.FillPath(fill, top);
                }
                // Where on the dome the surface faces the half vector: work in the cap's own frame.
                V half = Rotate(Half, nx, ny, -angle);
                double sx = half.X / half.Z, sy = half.Y / half.Z;
                double reach = Math.Min(DomeEdgeR * 0.85, Math.Sqrt(sx * sx + sy * sy) / slope * DomeEdgeR);
                double heading = Math.Atan2(sy, sx);
                double hr = reach;
                double hz = DomeEdgeZ + DomeRise * (1 - hr * hr / (DomeEdgeR * DomeEdgeR));
                PointF glint = Project(pose.Tip(new V(hr * Math.Cos(heading), hr * Math.Sin(heading), hz)), cx, cy);
                GraphicsState state = g.Save();
                g.SetClip(top, CombineMode.Intersect);
                using (GraphicsPath spot = new GraphicsPath())
                {
                    spot.AddEllipse(glint.X - 14, glint.Y - 12, 28, 24);
                    using (PathGradientBrush fill = new PathGradientBrush(spot))
                    {
                        fill.CenterColor = Color.FromArgb(46, 255, 255, 255);
                        fill.SurroundColors = new Color[] { Color.FromArgb(0, 255, 255, 255) };
                        g.FillPath(fill, spot);
                    }
                }
                g.Restore(state);
                using (Pen groove = new Pen(Color.FromArgb(90, 0, 0, 0), 0.9f)) g.DrawPath(groove, top);
            }

            // Smooth the silhouette with the convex hull of the outer wall.
            List<PointF> rim = new List<PointF>();
            for (int seg = 0; seg < segments; seg += 2)
            {
                for (int i = 1; i < ProfileR.Length; i++)
                    if (ProfileR[i] >= 38)
                        rim.Add(Project(pose.Tip(new V(ProfileR[i] * rays.Cos[seg], ProfileR[i] * rays.Sin[seg], ProfileZ[i])), cx, cy));
            }
            PointF[] hull = Hull(rim);
            using (Pen edge = new Pen(Color.FromArgb(150, Ui.Mix(skin.CapDark, Color.Black, 0.3f)), 0.8f)) g.DrawPolygon(edge, hull);
            g.SmoothingMode = smoothing;
            return hull;
        }

        // Andrew's monotone chain.
        private static PointF[] Hull(List<PointF> points)
        {
            points.Sort(delegate(PointF a, PointF b) { int c = a.X.CompareTo(b.X); return c != 0 ? c : a.Y.CompareTo(b.Y); });
            List<PointF> hull = new List<PointF>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                for (int i = 0; i < points.Count; i++)
                {
                    PointF p = pass == 0 ? points[i] : points[points.Count - 1 - i];
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }
                hull.RemoveAt(hull.Count - 1);
            }
            return hull.ToArray();
        }

        private static double Cross(PointF o, PointF a, PointF b)
        {
            return (a.X - o.X) * (double)(b.Y - o.Y) - (a.Y - o.Y) * (double)(b.X - o.X);
        }
    }
}
