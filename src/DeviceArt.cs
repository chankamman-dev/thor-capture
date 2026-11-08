// Front view of the AYN Thor, drawn procedurally.
//
// Geometry is in "design units" measured from a flat front render of the
// device: 877 wide, 1045 tall, lid open flat. The two screen rectangles match
// the panels' real aspect ratios (1920x1080 on top, 1240x1080 below), so
// screenshots drop in without cropping. The four skins follow AYN's retail
// colours; the drawing is our own and is not an official product image.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace ThorCapture
{
    internal sealed class DeviceSkin
    {
        public string Key;
        public string Name;
        public Color Body, BodyDark, Face, Rim, Hinge, HingeDark, Glass, Outline;
        public Color Button, ButtonDark, Ink, Dpad, DpadDark, Cap, CapDark, Skirt, Well, Detail, Glow;
        public Color[] Abxy; // X, A, B, Y; null for single-colour buttons
        public bool Clear;

        public float GlowLevel = 1f;   // lamp brightness, 0..1
        public Color DefaultGlow;      // the lamp colour this colourway ships with in AYN's photos

        public static readonly DeviceSkin[] All = Build();
        internal DeviceSkin WithGlow(Color color) { DeviceSkin copy=(DeviceSkin)MemberwiseClone(); copy.Glow=color; return copy; }

        // Applies the user's lamp settings; an empty colour means this colourway's own lamp colour.
        internal DeviceSkin WithLamp(bool on, Color color, float level)
        {
            DeviceSkin copy = (DeviceSkin)MemberwiseClone();
            copy.Glow = !on ? Color.Empty : color.IsEmpty ? DefaultGlow : color;
            copy.GlowLevel = Math.Max(0.1f, Math.Min(1f, level));
            return copy;
        }

        private static Color C(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }
        private static Color C(int alpha, int rgb) { return Color.FromArgb(alpha, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        private static DeviceSkin[] Build()
        {
            DeviceSkin black = new DeviceSkin();
            black.Key = "black"; black.Name = "黑色";
            black.Body = C(0x2C2D31); black.BodyDark = C(0x1B1C1F); black.Face = C(0x2A2B2F); black.Rim = C(0x303135);
            black.Hinge = C(0x36373C); black.HingeDark = C(0x18191C); black.Glass = C(0x08090C); black.Outline = C(46, 0xFFFFFF);
            black.Button = C(0x222326); black.ButtonDark = C(0x131416); black.Ink = C(0xC6CAD3);
            black.Dpad = C(0x222326); black.DpadDark = C(0x111214); black.Cap = C(0x2A2B2F); black.CapDark = C(0x141517);
            black.Skirt = C(0x3A3B40); black.Well = C(0x232427); black.Detail = C(0x101113); black.Glow = C(0x46E2F0);

            DeviceSkin white = new DeviceSkin();
            white.Key = "white"; white.Name = "白色";
            white.Body = C(0xF3F3F1); white.BodyDark = C(0xD4D4D1); white.Face = C(0xF6F6F4); white.Rim = C(0xFAFAF9);
            white.Hinge = C(0xF2F2F0); white.HingeDark = C(0xC4C4C1); white.Glass = C(0x0D1016); white.Outline = C(70, 0x000000);
            white.Button = C(0xFBFBFB); white.ButtonDark = C(0xDADAD7); white.Ink = C(0x50535B);
            white.Dpad = C(0xFAFAFA); white.DpadDark = C(0xD2D2CF); white.Cap = C(0x92959B); white.CapDark = C(0x5F6268);
            white.Skirt = C(0xC9CACC); white.Well = C(0xEDEDEA); white.Detail = C(0x26272B); white.Glow = Color.Empty;

            DeviceSkin rainbow = new DeviceSkin();
            rainbow.Key = "rainbow"; rainbow.Name = "彩虹";
            // Super Famicom-style warm light grey: greyer than the white unit, with a slightly
            // darker grey hinge and wells.
            rainbow.Body = C(0xC6C4BF); rainbow.BodyDark = C(0x9E9C97); rainbow.Face = C(0xCBC9C4); rainbow.Rim = C(0xC3C1BC);
            rainbow.Hinge = C(0xC4C2BD); rainbow.HingeDark = C(0x8F8D88); rainbow.Glass = C(0x0D1016); rainbow.Outline = C(70, 0x000000);
            rainbow.Button = C(0xD2D0CB); rainbow.ButtonDark = C(0xAEACA7); rainbow.Ink = C(0x4A4D55);
            rainbow.Dpad = C(0x3E3F44); rainbow.DpadDark = C(0x242529); rainbow.Cap = C(0x4B4D53); rainbow.CapDark = C(0x2C2E33);
            rainbow.Skirt = C(0x8E8C88); rainbow.Well = C(0xBFBDB8); rainbow.Detail = C(0x3A3B40); rainbow.Glow = C(0x5AA8FF);
            rainbow.Abxy = new Color[] { C(0x3F5FD0), C(0xD63E3E), C(0xE8B323), C(0x2FA055) };

            DeviceSkin purple = new DeviceSkin();
            purple.Key = "purple"; purple.Name = "透明紫";
            // Sampled from a photo of the retail unit: a muted, slightly warm violet, with
            // clear wells around the sticks and buttons and dark grey controls.
            purple.Body = C(232, 0x7A6489); purple.BodyDark = C(242, 0x3A2850); purple.Face = C(234, 0x684A7C); purple.Rim = C(236, 0x5A4070);
            purple.Hinge = C(175, 0x87679C); purple.HingeDark = C(225, 0x4A3262); purple.Glass = C(0x0B0A10); purple.Outline = C(120, 0xB9A2D2);
            purple.Button = C(0x3F454C); purple.ButtonDark = C(0x22262B); purple.Ink = C(0xE9EAEE);
            purple.Dpad = C(0x40464D); purple.DpadDark = C(0x23272C); purple.Cap = C(0x464C54); purple.CapDark = C(0x22262B);
            purple.Skirt = C(0x8A909C); purple.Well = C(150, 0x9476AE); purple.Detail = C(0x2B2E35); purple.Glow = Color.Empty;
            purple.Clear = true;

            // Lamp colours when the user turns the light on without picking one.
            black.DefaultGlow = black.Glow;
            white.DefaultGlow = C(0xFFC83C);
            rainbow.DefaultGlow = rainbow.Glow;
            purple.DefaultGlow = C(0xD69BFF);
            return new DeviceSkin[] { black, white, rainbow, purple };
        }

        public static int IndexOf(string key)
        {
            for (int i = 0; i < All.Length; i++) if (string.Equals(All[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
            return 1;
        }

        public void DrawSwatch(Graphics g, Rectangle r)
        {
            if (Abxy != null)
            {
                using (SolidBrush b = new SolidBrush(Body)) g.FillEllipse(b, r);
                float q = r.Width * 0.24f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, d = r.Width * 0.3f;
                PointF[] spots = { new PointF(cx, cy - q), new PointF(cx + q, cy), new PointF(cx, cy + q), new PointF(cx - q, cy) };
                for (int i = 0; i < 4; i++)
                    using (SolidBrush b = new SolidBrush(Abxy[i])) g.FillEllipse(b, spots[i].X - d / 2, spots[i].Y - d / 2, d, d);
            }
            else
            {
                using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 2),
                    Ui.Mix(Body, Color.White, 0.18f), Clear ? Ui.Alpha(BodyDark, 200) : BodyDark, 90f))
                    g.FillEllipse(b, r);
                if (!Glow.IsEmpty)
                    using (Pen p = new Pen(Glow, Math.Max(1f, r.Width / 11f)))
                        g.DrawEllipse(p, r.X + r.Width * 0.3f, r.Y + r.Height * 0.3f, r.Width * 0.4f, r.Height * 0.4f);
            }
            using (Pen edge = new Pen(Color.FromArgb(70, 255, 255, 255), 1f)) g.DrawEllipse(edge, r);
        }
    }

    internal static class DeviceArt
    {
        public const float Width = 877f;
        public const float Height = 1045f;
        public static readonly RectangleF TopScreen = new RectangleF(44f, 26f, 790f, 444.4f);
        public static readonly RectangleF BottomScreen = new RectangleF(223f, 591f, 432f, 376.2f);

        // Fits the device inside bounds, centred, and draws it.
        public static void Draw(Graphics g, RectangleF bounds, DeviceSkin skin, Image top, Image bottom, string topLabel, string bottomLabel, PadState input = null, bool controls = true, bool shadow = true)
        {
            float scale = Math.Min(bounds.Width / Width, bounds.Height / Height);
            if (scale <= 0) return;
            GraphicsState state = g.Save();
            g.TranslateTransform(bounds.X + (bounds.Width - Width * scale) / 2f, bounds.Y + (bounds.Height - Height * scale) / 2f);
            g.ScaleTransform(scale, scale);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            if (shadow) Ui.Shadow(g, new RectangleF(10, 30, Width - 20, Height - 30), 40, 26, 150, 18);
            Body(g, skin);
            if(controls) Controls(g, skin, input ?? new PadState());
            Hinge(g, skin);
            Lid(g, skin);
            LidJoint(g, skin);
            Bezel(g, skin, new RectangleF(BottomScreen.X - 9, BottomScreen.Y - 9, BottomScreen.Width + 18, BottomScreen.Height + 18), 11);
            Screen(g, TopScreen, top, topLabel);
            Screen(g, BottomScreen, bottom, bottomLabel);
            if (input != null) InputIndicators(g, input);
            g.Restore(state);
        }

        public static void DrawControls(Graphics g,RectangleF bounds,DeviceSkin skin,PadState input)
        {
            float scale=Math.Min(bounds.Width/Width,bounds.Height/Height);
            if(scale<=0) return;
            GraphicsState state=g.Save();
            g.TranslateTransform(bounds.X+(bounds.Width-Width*scale)/2,bounds.Y+(bounds.Height-Height*scale)/2);
            g.ScaleTransform(scale,scale);
            g.SmoothingMode=SmoothingMode.AntiAlias;
            g.PixelOffsetMode=PixelOffsetMode.HighQuality;
            g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            Controls(g,skin,input??new PadState());
            if(input!=null) InputIndicators(g,input);
            g.Restore(state);
        }

        public static void DrawScreens(Graphics g,RectangleF bounds,Image top,Image bottom,string topLabel,string bottomLabel)
        {
            float scale=Math.Min(bounds.Width/Width,bounds.Height/Height);
            if(scale<=0) return;
            GraphicsState state=g.Save();
            g.TranslateTransform(bounds.X+(bounds.Width-Width*scale)/2,bounds.Y+(bounds.Height-Height*scale)/2);
            g.ScaleTransform(scale,scale);
            Screen(g,TopScreen,top,topLabel); Screen(g,BottomScreen,bottom,bottomLabel);
            g.Restore(state);
        }

        // One screen only, so the preview can cache each screen's scaled picture separately.
        public static void DrawScreen(Graphics g,RectangleF bounds,bool top,Image image)
        {
            float scale=Math.Min(bounds.Width/Width,bounds.Height/Height);
            if(scale<=0) return;
            GraphicsState state=g.Save();
            g.TranslateTransform(bounds.X+(bounds.Width-Width*scale)/2,bounds.Y+(bounds.Height-Height*scale)/2);
            g.ScaleTransform(scale,scale);
            Screen(g,top?TopScreen:BottomScreen,image,null);
            g.Restore(state);
        }

        private static LinearGradientBrush Vertical(RectangleF r, Color top, Color bottom)
        {
            return new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, Math.Max(1, r.Width), r.Height + 2), top, bottom, 90f);
        }


        private static void Circle(Graphics g, Brush brush, float cx, float cy, float r)
        {
            g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
        }

        private static RectangleF Disc(float cx, float cy, float r) { return new RectangleF(cx - r, cy - r, r * 2, r * 2); }

        // These labels are an overlay, not invented hardware on the front shell.
        private static void InputIndicators(Graphics g, PadState input)
        {
            PadButton[] buttons={PadButton.L1,PadButton.L2,PadButton.R2,PadButton.R1};
            using(Font font=new Font("Segoe UI Semibold",10.5f))
            using(StringFormat center=new StringFormat { Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center })
            for(int i=0;i<buttons.Length;i++)
            {
                // Analog triggers fill with their travel; digital shoulders light fully.
                float level=i==1?input.LT:i==2?input.RT:0;
                bool active=input.Down(buttons[i]) || level>0.05f;
                if(input.Down(buttons[i])) level=1;
                RectangleF chip=new RectangleF(298+i*74,1055,62,22);
                if(active) Ui.Shadow(g,chip,11,8,(int)(90*Math.Max(level,0.5f)),0,Ui.Accent);
                using(GraphicsPath path=Ui.Round(chip,11))
                {
                    using(LinearGradientBrush bg=Vertical(chip,Color.FromArgb(235,255,255,255),Color.FromArgb(235,242,243,248))) g.FillPath(bg,path);
                    if(level>0)
                    {
                        GraphicsState state=g.Save();
                        g.SetClip(path,CombineMode.Intersect);
                        RectangleF fill=new RectangleF(chip.X,chip.Y,chip.Width*level,chip.Height);
                        using(LinearGradientBrush lit=Vertical(fill,Ui.Mix(Ui.Accent,Color.White,0.18f),Ui.Accent)) g.FillRectangle(lit,fill);
                        g.Restore(state);
                    }
                    using(Pen edge=new Pen(active?Ui.Alpha(Color.White,90):Ui.Wash(0.12f),1f)) g.DrawPath(edge,path);
                }
                using(SolidBrush ink=new SolidBrush(active?Color.White:Ui.TextDim)) g.DrawString(buttons[i].ToString(),font,ink,chip,center);
            }
        }

        // ---- shell ---------------------------------------------------------

        private static void Body(Graphics g, DeviceSkin s)
        {
            RectangleF body = new RectangleF(0, 556, Width, Height - 556);
            using (GraphicsPath path = Ui.Round(body, 22, 22, 40, 40))
            {
                if (s.Clear) Internals(g, path);
                using (LinearGradientBrush fill = Vertical(body, Ui.Mix(s.Body, Color.White, 0.05f), s.Body)) g.FillPath(fill, path);
                using (Pen edge = new Pen(s.Outline, 1.6f)) g.DrawPath(edge, path);
            }
            // Recessed face plate, slightly lighter than the outer shell.
            RectangleF face = new RectangleF(11, 566, Width - 22, 470);
            using (GraphicsPath path = Ui.Round(face, 16, 16, 31, 31))
            {
                using (LinearGradientBrush fill = Vertical(face, Ui.Mix(s.Face, Color.White, 0.06f), Ui.Mix(s.Face, s.BodyDark, 0.18f))) g.FillPath(fill, path);
                using (Pen lip = new Pen(Ui.Alpha(s.BodyDark, 150), 1.4f)) g.DrawPath(lip, path);
            }
            using (LinearGradientBrush sheen = new LinearGradientBrush(new RectangleF(20, 566, Width - 40, 4), Color.Transparent, Color.Transparent, 0f))
            {
                Color lit = Glint(s, 70);
                ColorBlend blend = new ColorBlend();
                blend.Colors = new Color[] { Color.FromArgb(0, lit), Color.FromArgb(lit.A, lit), Color.FromArgb(lit.A * 6 / 10, lit), Color.FromArgb(0, lit) };
                blend.Positions = new float[] { 0f, 0.25f, 0.6f, 1f };
                sheen.InterpolationColors = blend;
                using (Pen shine = new Pen(sheen, 1f)) g.DrawLine(shine, 20, 568.3f, Width - 20, 568.3f);
            }

            BottomTab(g, s);
        }

        private static bool IsDark(Color c) { return c.R + c.G + c.B < 240; }

        // Light (or shade) along a circular edge that is strongest where the edge faces
        // direction and fades out smoothly by the circle's midline, with a soft halo, instead
        // of a hard-ended arc. direction is a LinearGradientBrush angle: 45 is the upper left,
        // 225 the lower right, 270 the bottom.
        private static void EdgeLight(Graphics g, RectangleF circle, Color color, float width, float direction)
        {
            RectangleF span = circle;
            span.Inflate(width * 2, width * 2);
            float[] positions = { 0f, 0.17f, 0.34f, 0.5f, 1f };
            foreach (bool halo in new bool[] { true, false })
            {
                int peak = halo ? color.A / 3 : color.A;
                using (LinearGradientBrush brush = new LinearGradientBrush(span, Color.Transparent, Color.Transparent, direction))
                {
                    ColorBlend blend = new ColorBlend();
                    blend.Colors = new Color[] { Color.FromArgb(peak, color), Color.FromArgb(peak, color), Color.FromArgb(peak * 45 / 100, color), Color.FromArgb(0, color), Color.FromArgb(0, color) };
                    blend.Positions = positions;
                    brush.InterpolationColors = blend;
                    using (Pen pen = new Pen(brush, halo ? width * 2.6f : width)) g.DrawEllipse(pen, circle);
                }
            }
        }

        // Edge highlight tuned to the shell: strong on light plastic, faint on dark or clear.
        private static Color Glint(DeviceSkin s, int alpha)
        {
            return Color.FromArgb(s.Clear ? alpha * 45 / 100 : IsDark(s.Face) ? alpha * 30 / 100 : alpha, 255, 255, 255);
        }

        // Bottom-centre tab, from a photo of the device: a recessed frame whose sloped walls
        // show as a bevel band, a raised platform, two shallow debossed circles and a dark key.
        private static void BottomTab(Graphics g, DeviceSkin s)
        {
            // A raised platform: bevels climb from the shell to its flat top - the top bevel faces
            // the light, the left one partly, the right one is in shade - with mitres at the corners.
            // Proportions measured against a photo of the device: a clear gap below the screen,
            // a narrow top bevel and slim side bevels.
            RectangleF frame = new RectangleF(283, 990, 311, Height - 990);
            RectangleF tab = new RectangleF(298, 1002, 281, Height - 1002);
            Color baseTone = s.Clear ? Ui.Alpha(Ui.Mix(s.Hinge, Color.White, 0.1f), 170) : s.Face;
            using (LinearGradientBrush cast = new LinearGradientBrush(new RectangleF(frame.Right - 1, frame.Y, 8, frame.Height), Color.FromArgb(55, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 0f))
                g.FillRectangle(cast, frame.Right, frame.Y + 4, 6, frame.Height - 4);
            using (GraphicsPath outer = Ui.Round(frame, 12, 12, 0, 0))
            {
                if (s.Clear) using (SolidBrush under = new SolidBrush(Color.FromArgb(255, 40, 34, 52))) g.FillPath(under, outer);
                using (LinearGradientBrush top = Vertical(new RectangleF(frame.X, frame.Y, frame.Width, 13), Ui.Mix(baseTone, Color.White, 0.2f), Ui.Mix(baseTone, Color.White, 0.08f)))
                    g.FillPath(top, outer);
                GraphicsState state = g.Save();
                g.SetClip(outer, CombineMode.Intersect);
                using (SolidBrush left = new SolidBrush(Ui.Mix(baseTone, Color.White, 0.07f)))
                    g.FillPolygon(left, new PointF[] { new PointF(frame.X, frame.Y), new PointF(tab.X, tab.Y), new PointF(tab.X, Height), new PointF(frame.X, Height) });
                using (SolidBrush right = new SolidBrush(Ui.Mix(baseTone, s.BodyDark, 0.28f)))
                    g.FillPolygon(right, new PointF[] { new PointF(frame.Right, frame.Y), new PointF(tab.Right, tab.Y), new PointF(tab.Right, Height), new PointF(frame.Right, Height) });
                using (Pen mitre = new Pen(Color.FromArgb(40, 0, 0, 0), 0.8f))
                {
                    g.DrawLine(mitre, frame.X + 3.5f, frame.Y + 2.2f, tab.X, tab.Y);
                    g.DrawLine(mitre, frame.Right - 3.5f, frame.Y + 2.2f, tab.Right, tab.Y);
                }
                g.Restore(state);
                using (Pen crease = new Pen(Ui.Alpha(Ui.Mix(s.BodyDark, Color.Black, 0.3f), 120), 1f)) g.DrawPath(crease, outer);
            }
            using (GraphicsPath path = Ui.Round(tab, 6, 6, 0, 0))
            {
                if (s.Clear)
                {
                    using (SolidBrush under = new SolidBrush(Color.FromArgb(255, 40, 34, 52))) g.FillPath(under, path);
                    using (LinearGradientBrush fill = Vertical(tab, Ui.Alpha(Ui.Mix(s.Hinge, Color.White, 0.2f), 150), Ui.Alpha(s.Hinge, 120))) g.FillPath(fill, path);
                }
                else
                    using (LinearGradientBrush fill = Vertical(tab, Ui.Mix(s.Body, Color.White, 0.05f), Ui.Mix(s.Body, s.BodyDark, 0.08f))) g.FillPath(fill, path);
            }
            // Ridge where the bevels meet the flat top: lit along the top, shaded down the right.
            using (Pen lit = new Pen(Glint(s, 140), 1f)) { g.DrawLine(lit, tab.X + 6, tab.Y + 0.5f, tab.Right - 6, tab.Y + 0.5f); g.DrawLine(lit, tab.X + 0.5f, tab.Y + 6, tab.X + 0.5f, Height - 1); }
            using (Pen dim = new Pen(Color.FromArgb(50, 0, 0, 0), 1f)) g.DrawLine(dim, tab.Right - 0.5f, tab.Y + 6, tab.Right - 0.5f, Height - 1);

            // Two low round pads in the tab colour: only just proud of the surface.
            Color tabFace = s.Clear ? s.Button : Ui.Mix(s.Body, Color.White, 0.06f);
            Color tabSide = s.Clear ? s.ButtonDark : Ui.Mix(s.Body, s.BodyDark, 0.45f);
            foreach (float x in new float[] { 320, 557 })
                Cylinder(g, s, x, 1025, 12.5f, 1.2f, tabFace, tabSide, 0, 0.35f);

            // The dark key, sitting in a narrow gap.
            RectangleF key = new RectangleF(405.5f, 1009f, 66, 28);
            Color plate = s.Clear ? s.Button : IsDark(s.Body) ? Ui.Mix(s.Body, Color.Black, 0.35f) : Color.FromArgb(76, 77, 82);
            using (GraphicsPath gap = Ui.Round(new RectangleF(key.X - 1.8f, key.Y - 1.8f, key.Width + 3.6f, key.Height + 3.6f), 8))
            using (SolidBrush fill = new SolidBrush(Ui.Alpha(Ui.Mix(s.BodyDark, Color.Black, 0.4f), 150))) g.FillPath(fill, gap);
            using (GraphicsPath path = Ui.Round(key, 6.5f))
            {
                using (LinearGradientBrush fill = Vertical(key, Ui.Mix(plate, Color.White, 0.12f), Ui.Mix(plate, Color.Black, 0.12f))) g.FillPath(fill, path);
                using (Pen edge = new Pen(Color.FromArgb(120, 0, 0, 0), 0.9f)) g.DrawPath(edge, path);
            }
            using (Pen lit = new Pen(Color.FromArgb(55, 255, 255, 255), 1f))
                g.DrawLine(lit, key.X + 6, key.Y + 1.3f, key.Right - 6, key.Y + 1.3f);
            AynMark(g, plate, key.X + key.Width / 2, key.Y + key.Height / 2);
        }

        // The AYN wordmark etched into the key, traced from a close-up photo (units are relative to
        // the key centre). Every stroke is the same width (2.5): the outlines were generated from
        // centre lines, with stroke ends cut flat along the cap line and baseline. An A with a
        // flat-cut apex, a Y whose right arm is a separate hook reaching down towards the left arm,
        // and an N whose right stem is a separate short bar with a slanted foot.
        private static readonly PointF[][] AynLetters =
        {
            new PointF[] { new PointF(-19.38f, 5.60f), new PointF(-13.98f, -6.00f), new PointF(-11.22f, -6.00f), new PointF(-5.82f, 5.60f), new PointF(-8.58f, 5.60f), new PointF(-12.60f, -3.04f), new PointF(-16.62f, 5.60f) },
            new PointF[] { new PointF(-6.59f, -6.00f), new PointF(-3.31f, -6.00f), new PointF(1.15f, -0.76f), new PointF(1.15f, 5.60f), new PointF(-1.35f, 5.60f), new PointF(-1.35f, 0.16f) },
            new PointF[] { new PointF(2.32f, -6.00f), new PointF(5.36f, -6.00f), new PointF(2.26f, -1.49f), new PointF(0.20f, -2.91f) },
            new PointF[] { new PointF(7.05f, -6.00f), new PointF(9.93f, -6.00f), new PointF(19.63f, 5.60f), new PointF(16.37f, 5.60f), new PointF(9.55f, -2.56f), new PointF(9.55f, 5.60f), new PointF(7.05f, 5.60f) },
            new PointF[] { new PointF(16.70f, -6.00f), new PointF(19.20f, -6.00f), new PointF(19.20f, 2.15f), new PointF(16.70f, 0.10f) }
        };

        private static void AynMark(Graphics g, Color plate, float cx, float cy)
        {
            Color floor = Ui.Mix(plate, Color.Black, 0.06f);
            Color groove = Ui.Mix(plate, Color.Black, 0.5f);
            Color lip = Color.FromArgb(34, 255, 255, 255);
            foreach (PointF[] letter in AynLetters)
                using (GraphicsPath path = new GraphicsPath())
                {
                    PointF[] points = new PointF[letter.Length];
                    for (int i = 0; i < letter.Length; i++) points[i] = new PointF(cx + letter[i].X, cy + letter[i].Y);
                    path.AddPolygon(points);
                    using (Matrix shift = new Matrix(1, 0, 0, 1, 0.4f, 0.45f))
                    using (GraphicsPath lit = (GraphicsPath)path.Clone())
                    {
                        lit.Transform(shift);
                        using (Pen pen = new Pen(lip, 0.8f)) { pen.LineJoin = LineJoin.Round; g.DrawPath(pen, lit); }
                    }
                    using (SolidBrush fill = new SolidBrush(floor)) g.FillPath(fill, path);
                    using (Pen pen = new Pen(groove, 0.8f)) { pen.LineJoin = LineJoin.Round; g.DrawPath(pen, path); }
                }
        }

        // Dark backing under the translucent shell, so exports stay opaque.
        private static void Internals(Graphics g, GraphicsPath outline)
        {
            using (SolidBrush board = new SolidBrush(Color.FromArgb(255, 30, 25, 44))) g.FillPath(board, outline);
        }

        // The hinge is three cylinders: short knuckles on the base at each end, with rounded
        // outer ends, and the long barrel of the lid between them, split by hairline gaps.
        private const float HingeKnuckle = 130f, HingeGap = 1f;

        private static void Hinge(Graphics g, DeviceSkin s)
        {
            // Shadow the hinge throws onto the top of the base.
            using (LinearGradientBrush shade = Vertical(new RectangleF(10, 556, Width - 20, 12), Color.FromArgb(90, 0, 0, 0), Color.FromArgb(0, 0, 0, 0)))
                g.FillRectangle(shade, 10, 556, Width - 20, 12);
            // The shaft shows as a dark line through the gaps.
            using (SolidBrush shaft = new SolidBrush(Ui.Mix(s.HingeDark, Color.Black, 0.45f)))
                g.FillRectangle(shaft, 120, 502, Width - 240, 54);
            HingePart(g, s, new RectangleF(3, 496, HingeKnuckle, 62), 26, 4, true, false);
            HingePart(g, s, new RectangleF(Width - 3 - HingeKnuckle, 496, HingeKnuckle, 62), 4, 26, false, true);
            HingePart(g, s, new RectangleF(3 + HingeKnuckle + HingeGap, 497, Width - 6 - 2 * (HingeKnuckle + HingeGap), 63), 4, 4, false, false);
            if (s.Clear)
                using (Pen gloss = new Pen(Color.FromArgb(70, 255, 255, 255), 2.5f)) g.DrawLine(gloss, 30, 510, Width - 30, 510);
        }

        private static void HingePart(Graphics g, DeviceSkin s, RectangleF r, float leftRadius, float rightRadius, bool leftEnd, bool rightEnd)
        {
            using (GraphicsPath path = Ui.Round(r, leftRadius, rightRadius, rightRadius, leftRadius))
            {
                if (s.Clear) HingeShaft(g, path);
                // Cylinder lit from above: dark at the top edge as it turns away, a soft highlight
                // band a third of the way down, then falling into shadow underneath.
                using (LinearGradientBrush fill = Vertical(r, s.Hinge, s.HingeDark))
                {
                    ColorBlend blend = new ColorBlend();
                    float lift = IsDark(s.Hinge) ? 0.12f : 0.22f;
                    blend.Colors = new Color[]
                    {
                        Ui.Mix(s.Hinge, s.HingeDark, 0.45f), Ui.Mix(s.Hinge, Color.White, lift * 0.45f), Ui.Mix(s.Hinge, Color.White, lift),
                        s.Hinge, Ui.Mix(s.Hinge, s.HingeDark, 0.5f), Ui.Mix(s.Hinge, s.HingeDark, 0.9f)
                    };
                    blend.Positions = new float[] { 0f, 0.16f, 0.32f, 0.58f, 0.84f, 1f };
                    fill.InterpolationColors = blend;
                    g.FillPath(fill, path);
                }
                GraphicsState state = g.Save();
                g.SetClip(path, CombineMode.Intersect);
                // Rounded outer ends darken towards their tips.
                if (leftEnd)
                    using (LinearGradientBrush end = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, leftRadius + 2, r.Height), Color.FromArgb(70, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 0f))
                        g.FillRectangle(end, r.X, r.Y, leftRadius, r.Height);
                if (rightEnd)
                    using (LinearGradientBrush end = new LinearGradientBrush(new RectangleF(r.Right - rightRadius - 1, r.Y, rightRadius + 2, r.Height), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(70, 0, 0, 0), 0f))
                        g.FillRectangle(end, r.Right - rightRadius, r.Y, rightRadius, r.Height);
                // Joints: the faces either side of a gap turn away from the light and darken a little.
                if (!leftEnd)
                    using (LinearGradientBrush joint = new LinearGradientBrush(new RectangleF(r.X - 0.5f, r.Y, 2.5f, r.Height), Color.FromArgb(40, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 0f))
                        g.FillRectangle(joint, r.X, r.Y, 2, r.Height);
                if (!rightEnd)
                    using (LinearGradientBrush joint = new LinearGradientBrush(new RectangleF(r.Right - 2f, r.Y, 2.5f, r.Height), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(40, 0, 0, 0), 0f))
                        g.FillRectangle(joint, r.Right - 2, r.Y, 2, r.Height);
                g.Restore(state);
                // Only the outer silhouette gets the shell's edge line; joints stay dark.
                using (Pen edge = new Pen(Color.FromArgb(IsDark(s.Hinge) ? 90 : 60, 0, 0, 0), 0.9f)) g.DrawPath(edge, path);
            }
        }

        private static void HingeShaft(Graphics g, GraphicsPath outline)
        {
            using (SolidBrush under = new SolidBrush(Color.FromArgb(255, 58, 46, 74))) g.FillPath(under, outline);
            using (LinearGradientBrush rod = Vertical(new RectangleF(20, 514, Width - 40, 30), Color.FromArgb(255, 120, 104, 136), Color.FromArgb(255, 58, 46, 74)))
                g.FillRectangle(rod, 20, 514, Width - 40, 30);
        }

        private static void Lid(Graphics g, DeviceSkin s)
        {
            RectangleF lid = new RectangleF(1, 0, Width - 2, 497);
            using (GraphicsPath path = Ui.Round(lid, 44, 44, 16, 16))
            {
                if (s.Clear) using (SolidBrush under = new SolidBrush(Color.FromArgb(255, 34, 28, 50))) g.FillPath(under, path);
                using (LinearGradientBrush fill = Vertical(lid, Ui.Mix(s.Rim, Color.White, 0.12f), Ui.Mix(s.Rim, s.BodyDark, 0.3f))) g.FillPath(fill, path);
                using (Pen edge = new Pen(s.Outline, 1.6f)) g.DrawPath(edge, path);
            }
            RectangleF glass = new RectangleF(6, 5, Width - 12, 486);
            using (GraphicsPath path = Ui.Round(glass, 40, 40, 12, 12))
            using (LinearGradientBrush fill = Vertical(glass, Ui.Mix(s.Glass, Color.White, 0.06f), s.Glass))
                g.FillPath(fill, path);
            FadingLine(g, 30, Width - 30, 6.5f, Color.FromArgb(34, 255, 255, 255), 1.2f);
        }

        // A thin sheen along a straight edge, faded out towards both ends.
        private static void FadingLine(Graphics g, float x0, float x1, float y, Color color, float width)
        {
            using (LinearGradientBrush sheen = new LinearGradientBrush(new RectangleF(x0 - 1, y - 2, x1 - x0 + 2, 4), Color.Transparent, Color.Transparent, 0f))
            {
                ColorBlend blend = new ColorBlend();
                blend.Colors = new Color[] { Color.FromArgb(0, color), Color.FromArgb(color.A, color), Color.FromArgb(color.A * 6 / 10, color), Color.FromArgb(0, color) };
                blend.Positions = new float[] { 0f, 0.25f, 0.6f, 1f };
                sheen.InterpolationColors = blend;
                using (Pen pen = new Pen(sheen, width)) g.DrawLine(pen, x0, y, x1, y);
            }
        }

        // The barrel belongs to the lid: the lid's bottom edge runs straight down into it, with a
        // small fillet at each end, while the knuckles on the base tuck under the lid's corners.
        private static void LidJoint(Graphics g, DeviceSkin s)
        {
            float bx = 3 + HingeKnuckle + HingeGap, br = Width - 3 - HingeKnuckle - HingeGap;
            Color lidBottom = Ui.Mix(s.Rim, s.BodyDark, 0.3f);
            Color barrelTop = Ui.Mix(s.Hinge, s.HingeDark, 0.3f);
            // The lid's corners overhang the knuckles and shade their tops.
            foreach (RectangleF shade in new RectangleF[] { new RectangleF(10, 497, bx - 12, 7), new RectangleF(br + 2, 497, Width - 12 - br, 7) })
                using (LinearGradientBrush fill = Vertical(shade, Color.FromArgb(IsDark(s.Hinge) ? 110 : 70, 0, 0, 0), Color.FromArgb(0, 0, 0, 0)))
                    g.FillRectangle(fill, shade);
            // The web between lid and barrel: one surface, curving from the lid face into the barrel.
            // Starts on the lid frame just below the screen glass (which ends at 491), never over it.
            RectangleF web = new RectangleF(bx, 492, br - bx, 12);
            if (s.Clear) using (SolidBrush under = new SolidBrush(Color.FromArgb(255, 34, 28, 50))) g.FillRectangle(under, web);
            using (LinearGradientBrush fill = Vertical(web, lidBottom, barrelTop)) g.FillRectangle(fill, web);
            // Fillets where the lid's bottom edge turns down into each end of the barrel.
            const float f = 5f;
            using (GraphicsPath left = new GraphicsPath())
            using (GraphicsPath right = new GraphicsPath())
            {
                left.AddLine(bx - f, 497, bx, 497);
                left.AddLine(bx, 497, bx, 497 + f);
                left.AddArc(bx - 2 * f, 497, 2 * f, 2 * f, 0, -90);
                left.CloseFigure();
                right.AddLine(br, 497, br + f, 497);
                right.AddArc(br, 497, 2 * f, 2 * f, 270, -90);
                right.CloseFigure();
                using (SolidBrush fill = new SolidBrush(Ui.Mix(lidBottom, barrelTop, 0.5f)))
                {
                    g.FillPath(fill, left);
                    g.FillPath(fill, right);
                }
                using (Pen edge = new Pen(Color.FromArgb(IsDark(s.Hinge) ? 90 : 60, 0, 0, 0), 0.9f))
                {
                    g.DrawArc(edge, bx - 2 * f, 497, 2 * f, 2 * f, 0, -90);
                    g.DrawArc(edge, br, 497, 2 * f, 2 * f, 270, -90);
                    g.DrawLine(edge, bx, 497 + f, bx, 506);
                    g.DrawLine(edge, br, 497 + f, br, 506);
                }
            }
        }

        private static void Bezel(Graphics g, DeviceSkin s, RectangleF r, float radius)
        {
            using (GraphicsPath path = Ui.Round(r, radius))
            {
                using (SolidBrush fill = new SolidBrush(s.Glass)) g.FillPath(fill, path);
                using (Pen edge = new Pen(Ui.Alpha(s.BodyDark, 200), 1.4f)) g.DrawPath(edge, path);
            }
        }

        private static void Screen(Graphics g, RectangleF r, Image image, string label)
        {
            if (image == null)
            {
                using (LinearGradientBrush off = Vertical(r, Color.FromArgb(18, 21, 29), Color.FromArgb(9, 11, 15))) g.FillRectangle(off, r);
                using (Pen hint = new Pen(Color.FromArgb(22, 255, 255, 255), 1f)) g.DrawRectangle(hint, r.X, r.Y, r.Width, r.Height);
                if (!string.IsNullOrEmpty(label))
                {
                    string[] lines = label.Split('\n');
                    using (Font big = new Font("Microsoft YaHei UI", r.Height * 0.085f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (Font small = new Font("Microsoft YaHei UI", r.Height * 0.05f, GraphicsUnit.Pixel))
                    using (SolidBrush strong = new SolidBrush(Color.FromArgb(120, 190, 198, 216)))
                    using (SolidBrush faint = new SolidBrush(Color.FromArgb(90, 170, 178, 196)))
                    using (StringFormat center = new StringFormat())
                    {
                        center.Alignment = StringAlignment.Center;
                        center.LineAlignment = StringAlignment.Center;
                        float mid = r.Y + r.Height / 2f;
                        g.DrawString(lines[0], big, strong, new RectangleF(r.X, mid - r.Height * 0.14f, r.Width, r.Height * 0.14f), center);
                        if (lines.Length > 1)
                            g.DrawString(lines[1], small, faint, new RectangleF(r.X, mid + r.Height * 0.02f, r.Width, r.Height * 0.1f), center);
                    }
                }
                return;
            }
            using (SolidBrush black = new SolidBrush(Color.Black)) g.FillRectangle(black, r);
            float scale = Math.Min(r.Width / image.Width, r.Height / image.Height);
            float w = image.Width * scale, h = image.Height * scale;
            float x = r.X + (r.Width - w) / 2f, y = r.Y + (r.Height - h) / 2f;
            InterpolationMode previous = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (ImageAttributes attributes = new ImageAttributes())
            {
                attributes.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the edges
                g.DrawImage(image, new PointF[] { new PointF(x, y), new PointF(x + w, y), new PointF(x, y + h) },
                    new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel, attributes);
            }
            g.InterpolationMode = previous;
        }

        // ---- controls ------------------------------------------------------

        private static void Controls(Graphics g, DeviceSkin s, PadState input)
        {
            for (int i = 0; i < 9; i++) DrawControlPart(g, s, input, i);
        }

        internal static void DrawControlPart(Graphics g, DeviceSkin s, PadState input, int part)
        {
            switch (part)
            {
                case 0: Stick(g, s, 115, 696, input.LX, input.LY, input.Press(PadButton.L3)); return;
                case 1: Dpad(g, s, 123, 862, input); return;
                case 2: AbxyCluster(g, s, 771, 692, input); return;
                case 3: Stick(g, s, 766, 860, input.RX, input.RY, input.Press(PadButton.R3)); return;
                case 4: MiniButton(g, s, 179, 597, 0, input.Press(PadButton.Select)); return;
                case 5: MiniButton(g, s, 699, 597, 1, input.Press(PadButton.Start)); return;
                case 6: MiniButton(g, s, 178, 983, 2, input.Press(PadButton.Home)); return;
                case 7: MiniButton(g, s, 699, 983, 3, input.Press(PadButton.Back)); return;
                case 9: InputIndicators(g, input); return;
            }
            Slot(g, s, 60, 979);
            Slot(g, s, 751, 979);
            // Status LED window: a small dark hole with a lit lower lip.
            using (SolidBrush led = new SolidBrush(Ui.Mix(s.Detail, Color.Black, 0.3f))) Circle(g, led, 175, 772, 3.2f);
            using (Pen lip = new Pen(Glint(s, 90), 0.8f)) g.DrawArc(lip, Disc(175, 772, 3.9f), 20, 140);
        }

        private static void Well(Graphics g, DeviceSkin s, float cx, float cy, float r)
        {
            RectangleF outer = Disc(cx, cy, r);
            using (LinearGradientBrush lip = Vertical(outer, Ui.Mix(s.Face, Color.Black, 0.16f), Ui.Mix(s.Face, Color.White, 0.4f)))
                g.FillEllipse(lip, outer);
            RectangleF inner = Disc(cx, cy, r - 2.6f);
            if (s.Clear)
            {
                using (SolidBrush under = new SolidBrush(Color.FromArgb(255, 46, 40, 60))) g.FillEllipse(under, inner);
                using (Pen ring = new Pen(Color.FromArgb(255, 84, 76, 100), 4f)) g.DrawEllipse(ring, Disc(cx, cy, r * 0.8f));
                using (Pen ring = new Pen(Color.FromArgb(255, 70, 62, 86), 2f)) g.DrawEllipse(ring, Disc(cx, cy, r * 0.55f));
            }
            using (LinearGradientBrush fill = Vertical(inner, Ui.Mix(s.Well, Color.Black, 0.08f), Ui.Mix(s.Well, Color.White, 0.07f)))
                g.FillEllipse(fill, inner);
        }

        // ---- analog stick --------------------------------------------------
        //
        // Matched to close-up photos of a retail unit. Everything in the housing
        // stays put: the cream bowl, the frosted lamp ring, and the dim dome in
        // the middle that the shaft rises from. Only the cap moves, and it moves
        // by tipping about a pivot deep below the housing (see Stick3D), which is
        // why the top travels far while the knurled side wall comes into view.

        internal const float RingOuter = 49f;
        internal const float RingInner = 29f;

        internal static void Stick(Graphics g, DeviceSkin s, float cx, float cy, float x, float y, bool pressed)
        {
            Stick(g, s, cx, cy, x, y, pressed ? 1f : 0f);
        }

        internal static void Stick(Graphics g, DeviceSkin s, float cx, float cy, float x, float y, float pressure)
        {
            Well(g, s, cx, cy, 77);
            Bowl(g, s, cx, cy);
            LampRing(g, s, cx, cy);
            Dome(g, s, cx, cy);
            double capX, capY, capR;
            Stick3D.Footprint(x, y, pressure, out capX, out capY, out capR);
            LampLight.Bowl(g, s, cx, cy, capX, capY, capR);
            Stick3D.Draw(g, s, cx, cy, x, y, pressure);
        }

        // Lamp colour as seen through the diffuser at the current brightness.
        private static Color LampColour(DeviceSkin s)
        {
            Color off = Ui.Mix(s.Skirt, Color.White, 0.25f);
            return Ui.Mix(off, s.Glow, 0.3f + 0.7f * s.GlowLevel);
        }

        // The stick sits in a deep bowl: a flat rim, a moulded step, then a wall that the
        // rim shades along the top and the room light catches along the bottom.
        private static void Bowl(Graphics g, DeviceSkin s, float cx, float cy)
        {
            using (Pen step = new Pen(Ui.Alpha(Ui.Mix(s.Well, Color.Black, 0.35f), 110), 1.1f)) g.DrawEllipse(step, Disc(cx, cy, 70.5f));
            // Gradients span the whole bowl so they never wrap; colour stops set the fades.
            RectangleF span = new RectangleF(cx - 72, cy - 72, 144, 144);
            using (LinearGradientBrush glint = new LinearGradientBrush(span, Color.Transparent, Color.Transparent, 45f))
            {
                ColorBlend blend = new ColorBlend();
                blend.Colors = new Color[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(80, 255, 255, 255) };
                blend.Positions = new float[] { 0f, 0.62f, 1f };
                glint.InterpolationColors = blend;
                using (Pen lit = new Pen(glint, 1f)) g.DrawEllipse(lit, Disc(cx, cy, 69.3f));
            }
            using (GraphicsPath wall = new GraphicsPath(FillMode.Alternate))
            {
                wall.AddEllipse(Disc(cx, cy, 70f));
                wall.AddEllipse(Disc(cx, cy, 57f));
                // Lit from the upper left, the inner wall on that side faces away and sits in soft shade.
                using (LinearGradientBrush shade = new LinearGradientBrush(span, Color.Transparent, Color.Transparent, 45f))
                {
                    ColorBlend blend = new ColorBlend();
                    blend.Colors = new Color[] { Color.FromArgb(34, 0, 0, 0), Color.FromArgb(22, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0, 0, 0, 0) };
                    blend.Positions = new float[] { 0f, 0.25f, 0.55f, 1f };
                    shade.InterpolationColors = blend;
                    g.FillPath(shade, wall);
                }
            }
        }

        private static GraphicsPath Annulus(float cx, float cy, float inner, float outer)
        {
            GraphicsPath path = new GraphicsPath(FillMode.Alternate);
            path.AddEllipse(Disc(cx, cy, outer));
            path.AddEllipse(Disc(cx, cy, inner));
            return path;
        }

        // The lamp ring is a glossy, frosted part with rounded edges. Its outer edge rolls
        // towards the upper-left key light and its inner edge falls away the other way, so
        // their highlights sit on opposite sides; the flat top between them glows evenly
        // with faint hot spots over the LEDs.
        private static void LampRing(Graphics g, DeviceSkin s, float cx, float cy)
        {
            bool lit = !s.Glow.IsEmpty;
            Color lamp = lit ? LampColour(s) : Color.Empty;
            using (GraphicsPath ring = Annulus(cx, cy, RingInner, RingOuter))
            {
                if (!lit)
                {
                    // Unlit, the ring is milky translucent plastic sitting in a shallow channel.
                    using (LinearGradientBrush fill = Vertical(Disc(cx, cy, RingOuter), Ui.Mix(s.Skirt, Color.Black, 0.12f), Ui.Mix(s.Skirt, Color.White, 0.4f)))
                        g.FillPath(fill, ring);
                }
                else
                {
                    // Lit, a thick acrylic ring: saturated through its body, and brightest where
                    // light escapes its edges - a hot line round the inner edge by the shaft hole,
                    // a paler one round the outer edge, and a lighter band over the moulded step.
                    using (GraphicsPath disc = new GraphicsPath())
                    {
                        disc.AddEllipse(Disc(cx, cy, RingOuter));
                        using (PathGradientBrush fill = new PathGradientBrush(disc))
                        {
                            Color deep = Ui.Mix(lamp, Color.Black, 0.05f);
                            ColorBlend blend = new ColorBlend();
                            blend.Colors = new Color[]
                            {
                                Ui.Mix(lamp, Color.White, 0.42f), lamp, deep,
                                Ui.Mix(lamp, Color.White, 0.08f), deep, Ui.Mix(lamp, Color.White, 0.18f),
                                Ui.Mix(lamp, Color.White, 0.58f), Ui.Mix(lamp, Color.White, 0.58f)
                            };
                            // 0 = outer edge (r 49), 1 = centre; inner edge at 0.41, step band near r 40.
                            blend.Positions = new float[] { 0f, 0.025f, 0.1f, 0.18f, 0.27f, 0.385f, 0.408f, 1f };
                            fill.InterpolationColors = blend;
                            fill.CenterPoint = new PointF(cx, cy);
                            g.FillPath(fill, ring);
                        }
                    }
                    // Faint hot spots over the eight LEDs, clipped to the ring.
                    GraphicsState state = g.Save();
                    g.SetClip(ring, CombineMode.Intersect);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = (i + 0.5) * Math.PI / 4;
                        float hx = cx + (float)(39 * Math.Cos(a)), hy = cy + (float)(39 * Math.Sin(a));
                        using (GraphicsPath spot = new GraphicsPath())
                        {
                            spot.AddEllipse(Disc(hx, hy, 10));
                            using (PathGradientBrush fill = new PathGradientBrush(spot))
                            {
                                fill.CenterColor = Color.FromArgb((int)(26 * s.GlowLevel), 255, 255, 255);
                                fill.SurroundColors = new Color[] { Color.FromArgb(0, 255, 255, 255) };
                                g.FillPath(fill, spot);
                            }
                        }
                    }
                    g.Restore(state);
                }
                // Faint moulded steps in the diffuser.
                using (Pen step = new Pen(Color.FromArgb(lit ? 22 : 26, 0, 0, 0), 0.8f))
                {
                    g.DrawEllipse(step, Disc(cx, cy, 43.5f));
                    g.DrawEllipse(step, Disc(cx, cy, 36.5f));
                }
            }
            // A soft reflection of the room on the ring's glossy top, upper left only.
            EdgeLight(g, Disc(cx, cy, RingOuter - 2.2f), Color.FromArgb(lit ? 50 : (IsDark(s.Face) ? 40 : 70), 255, 255, 255), 1.2f, 45f);
            if (lit)
            {
                // The lit edge blooms a little into the air right around it, independent of what the
                // bowl is made of; this is what stops it reading as a painted line on dark shells.
                for (float r = RingOuter; r < RingOuter + 7f; r += 0.8f)
                {
                    int alpha = (int)(70 * s.GlowLevel * Math.Exp(-(r - RingOuter) / 1.8));
                    using (Pen bloom = new Pen(Color.FromArgb(alpha, Ui.Mix(lamp, Color.White, 0.25f)), 1f)) g.DrawEllipse(bloom, Disc(cx, cy, r));
                }
            }
            // The ring sits a little below the bowl: a thin shadowed seam at its outer edge.
            using (Pen seam = new Pen(Ui.Alpha(Ui.Mix(s.Well, Color.Black, 0.4f), 90), 1f)) g.DrawEllipse(seam, Disc(cx, cy, RingOuter + 0.6f));
        }

        // The pivot dome inside the ring. It only rotates, so it is drawn fixed.
        private static void Dome(Graphics g, DeviceSkin s, float cx, float cy)
        {
            bool lit = !s.Glow.IsEmpty;
            Color lamp = lit ? LampColour(s) : Color.Empty;
            using (GraphicsPath dome = new GraphicsPath())
            {
                dome.AddEllipse(Disc(cx, cy, RingInner));
                using (PathGradientBrush fill = new PathGradientBrush(dome))
                {
                    // The shaft hole sits in shadow; lit, only its edge next to the ring picks up colour.
                    Color dark = Ui.Mix(s.CapDark, Color.Black, 0.35f);
                    ColorBlend blend = new ColorBlend();
                    blend.Colors = lit
                        ? new Color[] { Ui.Mix(lamp, dark, 0.45f), Ui.Mix(lamp, dark, 0.82f), dark, dark }
                        : new Color[] { Ui.Mix(s.Skirt, Color.Black, 0.45f), Ui.Mix(s.Skirt, Color.Black, 0.6f), dark, dark };
                    blend.Positions = new float[] { 0f, 0.1f, 0.3f, 1f };
                    fill.InterpolationColors = blend;
                    fill.CenterPoint = new PointF(cx + 2, cy + 3);
                    g.FillPath(fill, dome);
                }
                using (Pen rim = new Pen(Color.FromArgb(lit ? 150 : 90, lit ? Ui.Mix(lamp, Color.Black, 0.55f) : Color.Black), 1.1f))
                    g.DrawPath(rim, dome);
            }
        }

        internal static GraphicsPath Cross(float cx, float cy, float a, float h, float r)
        {
            GraphicsPath p = new GraphicsPath();
            float d = r * 2;
            p.AddArc(cx - h, cy - a, d, d, 180, 90);
            p.AddArc(cx + h - d, cy - a, d, d, 270, 90);
            p.AddLine(cx + h, cy - a + r, cx + h, cy - h);
            p.AddLine(cx + h, cy - h, cx + a - r, cy - h);
            p.AddArc(cx + a - d, cy - h, d, d, 270, 90);
            p.AddArc(cx + a - d, cy + h - d, d, d, 0, 90);
            p.AddLine(cx + a - r, cy + h, cx + h, cy + h);
            p.AddLine(cx + h, cy + h, cx + h, cy + a - r);
            p.AddArc(cx + h - d, cy + a - d, d, d, 0, 90);
            p.AddArc(cx - h, cy + a - d, d, d, 90, 90);
            p.AddLine(cx - h, cy + a - r, cx - h, cy + h);
            p.AddLine(cx - h, cy + h, cx - a + r, cy + h);
            p.AddArc(cx - a, cy + h - d, d, d, 90, 90);
            p.AddArc(cx - a, cy - h, d, d, 180, 90);
            p.AddLine(cx - a + r, cy - h, cx - h, cy - h);
            p.AddLine(cx - h, cy - h, cx - h, cy - a + r);
            p.CloseFigure();
            return p;
        }

        // Round moulded plate the D-pad and ABXY sit on: a matte disc in the shell colour,
        // raised a little, with a softly rounded edge. Returns the centre of its top.
        internal static PointF Plate(Graphics g, DeviceSkin s, float cx, float cy, float r)
        {
            const float lift = 2.4f;
            float baseY = cy + lift / 2, topY = cy - lift / 2;
            using (GraphicsPath shadow = new GraphicsPath())
            {
                shadow.AddEllipse(Disc(cx + 1.6f, baseY + 2f, r + 3.5f));
                using (PathGradientBrush fill = new PathGradientBrush(shadow))
                {
                    ColorBlend blend = new ColorBlend();
                    blend.Colors = new Color[] { Color.FromArgb(0, 0, 0, 0), Color.FromArgb(IsDark(s.Face) ? 70 : 45, 0, 0, 0), Color.FromArgb(IsDark(s.Face) ? 70 : 45, 0, 0, 0) };
                    blend.Positions = new float[] { 0f, 0.08f, 1f };
                    fill.InterpolationColors = blend;
                    g.FillPath(fill, shadow);
                }
            }
            Color side = Ui.Mix(s.Face, s.BodyDark, 0.6f);
            using (GraphicsPath wall = new GraphicsPath())
            {
                wall.FillMode = FillMode.Winding;
                wall.AddEllipse(Disc(cx, baseY, r));
                wall.AddRectangle(new RectangleF(cx - r, topY, r * 2, lift));
                using (LinearGradientBrush fill = new LinearGradientBrush(new RectangleF(cx - r - 0.5f, topY, r * 2 + 1, 1), Ui.Mix(side, Color.White, 0.1f), Ui.Mix(side, Color.Black, 0.2f), 0f))
                    g.FillPath(fill, wall);
            }
            RectangleF top = Disc(cx, topY, r);
            if (s.Clear) using (SolidBrush under = new SolidBrush(Color.FromArgb(255, 46, 40, 60))) g.FillEllipse(under, top);
            using (LinearGradientBrush fill = new LinearGradientBrush(top, Ui.Mix(s.Face, Color.White, 0.06f), Ui.Mix(s.Face, s.BodyDark, 0.12f), 60f))
                g.FillEllipse(fill, top);
            // Rounded edge: lit towards the upper left, a fine shadow line round the lower right.
            EdgeLight(g, Disc(cx, topY, r - 1f), Glint(s, 130), 1.2f, 45f);
            EdgeLight(g, top, Color.FromArgb(70, 0, 0, 0), 0.9f, 225f);
            return new PointF(cx, topY);
        }

        private static void Dpad(Graphics g, DeviceSkin s, float cx, float cy, PadState input)
        {
            PointF c = Plate(g, s, cx, cy, 69);
            Dpad3D.Draw(g, s, c.X, c.Y, input);
        }
        private static void AbxyCluster(Graphics g, DeviceSkin s, float cx, float cy, PadState input)
        {
            PointF top = Plate(g, s, cx, cy, 81);
            cx = top.X;
            cy = top.Y;
            if (s.Clear)
                using (Pen dome = new Pen(Color.FromArgb(90, 40, 34, 52), 2.2f))
                {
                    g.DrawEllipse(dome, Disc(cx, cy - 53, 32));
                    g.DrawEllipse(dome, Disc(cx + 53, cy, 32));
                    g.DrawEllipse(dome, Disc(cx, cy + 53, 32));
                    g.DrawEllipse(dome, Disc(cx - 53, cy, 32));
                }
            string[] letters = { "X", "A", "B", "Y" };
            PointF[] offsets = { new PointF(0, -53), new PointF(53, 0), new PointF(0, 53), new PointF(-53, 0) };
            for (int i = 0; i < 4; i++)
                FaceButton(g, s, cx + offsets[i].X, cy + offsets[i].Y, letters[i], s.Abxy == null ? Color.Empty : s.Abxy[i], input.Press(ShellGeometry.FaceButtons[i]));
        }

        // Face buttons: matte raised cylinders with nearly flat tops and printed letters.
        private static void FaceButton(Graphics g, DeviceSkin s, float cx, float cy, string letter, Color color, float pressure)
        {
            const float r = 25.5f;
            Color material = color.IsEmpty ? s.Button : color;
            Color side = color.IsEmpty ? s.ButtonDark : Ui.Mix(color, Color.Black, 0.38f);
            PointF c = Cylinder(g, s, cx, cy, r, 5.5f, material, side, pressure, 0.45f);
            Color ink = color.IsEmpty ? s.Ink : Color.FromArgb(242, 255, 255, 255);
            using (Font font = new Font("Segoe UI Semibold", 27f, GraphicsUnit.Pixel))
            using (SolidBrush brush = new SolidBrush(ink))
            using (StringFormat center = new StringFormat())
            {
                center.Alignment = StringAlignment.Center;
                center.LineAlignment = StringAlignment.Center;
                g.DrawString(letter, font, brush, new RectangleF(c.X - r, c.Y - r + 1, r * 2, r * 2), center);
            }
        }

        // A short raised cylinder, seen like the rest of the controls from slightly above the
        // front: its top face sits higher on screen than its base, so the lower part of the
        // side wall shows as a band. Returns the centre of the top face.
        private static PointF Cylinder(Graphics g, DeviceSkin s, float cx, float cy, float r, float lift, Color face, Color side, float pressure)
        {
            return Cylinder(g, s, cx, cy, r, lift, face, side, pressure, 1f);
        }

        // relief scales the shading across the top face (1 for the small buttons, lower for matte caps).
        private static PointF Cylinder(Graphics g, DeviceSkin s, float cx, float cy, float r, float lift, Color face, Color side, float pressure, float relief)
        {
            pressure = Math.Max(0, Math.Min(1, pressure));
            lift *= 1 - .75f * pressure;
            float baseY = cy + lift / 2, topY = cy - lift / 2;
            // Narrow gap round the base, and a soft shadow thrown down and to the right.
            using (SolidBrush gap = new SolidBrush(Ui.Alpha(Ui.Mix(s.Face, Color.Black, 0.45f), 150))) Circle(g, gap, cx, baseY, r + 1.2f);
            // The cast shadow scales with height, so low pads barely throw one.
            float height = Math.Min(1f, lift / 4.6f) * (1 - pressure);
            if (height > .001f)
                using (GraphicsPath shadow = new GraphicsPath())
                {
                    shadow.AddEllipse(Disc(cx + 1.2f * height, baseY + 1.4f * height, r + 2.2f * height));
                    using (PathGradientBrush fill = new PathGradientBrush(shadow))
                    {
                        ColorBlend blend = new ColorBlend();
                        blend.Colors = new Color[] { Color.FromArgb(0, 0, 0, 0), Color.FromArgb((int)(60 * height), 0, 0, 0), Color.FromArgb((int)(60 * height), 0, 0, 0) };
                        blend.Positions = new float[] { 0f, 0.18f, 1f };
                        fill.InterpolationColors = blend;
                        g.FillPath(fill, shadow);
                    }
                }
            // Side wall: lit on the left, falling off to the right.
            using (GraphicsPath wall = new GraphicsPath())
            {
                wall.FillMode = FillMode.Winding;
                wall.AddEllipse(Disc(cx, baseY, r));
                wall.AddRectangle(new RectangleF(cx - r, topY, r * 2, baseY - topY));
                using (LinearGradientBrush fill = new LinearGradientBrush(new RectangleF(cx - r - 0.5f, topY, r * 2 + 1, 1), Ui.Mix(side, Color.White, 0.14f), Ui.Mix(side, Color.Black, 0.25f), 0f))
                    g.FillPath(fill, wall);
                using (Pen edge = new Pen(Color.FromArgb(80, 0, 0, 0), 0.8f)) g.DrawArc(edge, Disc(cx, baseY, r), 0, 180);
            }
            // Top face: flat with a satin finish, so only a gentle wash from the upper left.
            // A radial hotspot here reads as a ball, which these buttons are not.
            RectangleF top = Disc(cx, topY, r);
            Color tone = Ui.Mix(face, side, .4f * pressure);
            bool dark = IsDark(face);
            using (LinearGradientBrush fill = new LinearGradientBrush(top, Ui.Mix(tone, Color.White, (dark ? 0.06f : 0.1f) * relief), Ui.Mix(tone, side, (dark ? 0.12f : 0.22f) * relief), 60f))
                g.FillEllipse(fill, top);
            // Small chamfer round the top edge: it catches the light at the upper left and
            // falls into shade at the lower right, with a lit line where it meets the wall.
            float bevel = Math.Max(0.9f, r * 0.07f);
            EdgeLight(g, Disc(cx, topY, r - bevel / 2), Color.FromArgb((int)((dark ? 70 : 150) * (1 - 2 * pressure / 3)), 255, 255, 255), bevel, 45f);
            EdgeLight(g, Disc(cx, topY, r - bevel / 2), Color.FromArgb(dark ? 60 : 40, 0, 0, 0), bevel, 225f);
            EdgeLight(g, Disc(cx, topY, r + 0.1f), Color.FromArgb(dark ? 30 : 80, 255, 255, 255), 0.8f, 270f);
            using (Pen edge = new Pen(Color.FromArgb(dark ? 90 : 55, 0, 0, 0), 0.8f)) g.DrawEllipse(edge, top);
            return new PointF(cx, topY);
        }

        // 0 = select (square), 1 = start (triangle), 2 = home, 3 = back.
        // From photos: a raised round button in the shell colour with a debossed line icon.
        private static void MiniButton(Graphics g, DeviceSkin s, float cx, float cy, int glyph, float pressure)
        {
            PointF c = Cylinder(g, s, cx, cy, 15.5f, 4.6f, s.Button, s.ButtonDark, pressure);
            // Etched icon, cut into the face: the lower-right lip of the cut catches the light,
            // the groove floor is a shade darker, and its upper-left wall is in shadow.
            // On the device the cut is shallow and the same plastic, so it reads by a faint
            // shadow and glint rather than by colour.
            bool dark = IsDark(s.Button);
            Color floor = dark ? Ui.Mix(s.Button, Color.Black, 0.18f) : Ui.Mix(s.Button, Color.Black, 0.06f);
            Color wall = dark ? Ui.Mix(s.Button, Color.Black, 0.42f) : Ui.Mix(s.Button, Color.Black, 0.2f);
            Color lip = Color.FromArgb(dark ? 38 : 120, 255, 255, 255);
            DrawIcon(g, glyph, c.X + 0.55f, c.Y + 0.65f, lip, 1f);
            DrawIcon(g, glyph, c.X, c.Y, floor, 1.7f);
            DrawIcon(g, glyph, c.X - 0.4f, c.Y - 0.45f, wall, 0.7f);
        }

        private static void DrawIcon(Graphics g, int glyph, float cx, float cy, Color color, float width)
        {
            using (Pen pen = new Pen(color, width))
            {
                pen.LineJoin = LineJoin.Round;
                pen.StartCap = pen.EndCap = LineCap.Round;
                switch (glyph)
                {
                    case 0:
                        using (GraphicsPath square = Ui.Round(new RectangleF(cx - 5.2f, cy - 5.2f, 10.4f, 10.4f), 1.6f)) g.DrawPath(pen, square);
                        break;
                    case 1:
                        g.DrawPolygon(pen, new PointF[] { new PointF(cx - 4f, cy - 6.2f), new PointF(cx - 4f, cy + 6.2f), new PointF(cx + 6.2f, cy) });
                        break;
                    case 2:
                        g.DrawLines(pen, new PointF[] { new PointF(cx - 7.2f, cy - 0.4f), new PointF(cx, cy - 7f), new PointF(cx + 7.2f, cy - 0.4f) });
                        g.DrawLines(pen, new PointF[] { new PointF(cx - 5.2f, cy - 2.2f), new PointF(cx - 5.2f, cy + 6.2f), new PointF(cx - 1.7f, cy + 6.2f),
                            new PointF(cx - 1.7f, cy + 2.6f), new PointF(cx + 1.7f, cy + 2.6f), new PointF(cx + 1.7f, cy + 6.2f), new PointF(cx + 5.2f, cy + 6.2f), new PointF(cx + 5.2f, cy - 2.2f) });
                        break;
                    default:
                        // Counter-clockwise arrow: an open circle with its head at the upper left.
                        g.DrawArc(pen, cx - 6.2f, cy - 5.6f, 12.4f, 12.4f, 215, 285);
                        g.DrawLines(pen, new PointF[] { new PointF(cx - 7.6f, cy - 5.6f), new PointF(cx - 5.4f, cy - 1.6f), new PointF(cx - 1.2f, cy - 3.2f) });
                        break;
                }
            }
        }

        // Speaker slot: a dark pill-shaped recess over a fine mesh, shaded under its top edge.
        private static void Slot(Graphics g, DeviceSkin s, float x, float y)
        {
            RectangleF slot = new RectangleF(x, y, 68, 7.5f);
            using (GraphicsPath path = Ui.Round(slot, 3.75f))
            {
                using (LinearGradientBrush fill = Vertical(slot, Ui.Mix(s.Detail, Color.Black, 0.45f), Ui.Mix(s.Detail, Color.White, 0.06f))) g.FillPath(fill, path);
                GraphicsState state = g.Save();
                g.SetClip(path, CombineMode.Intersect);
                using (SolidBrush hole = new SolidBrush(Color.FromArgb(IsDark(s.Face) ? 40 : 55, 255, 255, 255)))
                    for (int row = 0; row < 3; row++)
                        for (float hx = x + 2.5f + (row % 2) * 0.9f; hx < x + 66; hx += 1.8f)
                            g.FillRectangle(hole, hx, y + 1.8f + row * 1.7f, 0.7f, 0.7f);
                g.Restore(state);
                using (Pen edge = new Pen(Color.FromArgb(90, 0, 0, 0), 0.9f)) g.DrawPath(edge, path);
            }
            using (Pen shine = new Pen(Glint(s, 80), 1f))
                g.DrawLine(shine, x + 4, y + 8.6f, x + 64, y + 8.6f);
        }
    }
}
