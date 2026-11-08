// Preview stage. Three modes: the screenshot inside the Thor shell, the single
// image fitted to the window, and 1:1 with drag-to-pan. Fit mode upscales in
// whole steps with nearest neighbour, because everything on this device is
// pixel art.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ThorCapture
{
    // Values are stored in the settings file; Dual came later and so comes last.
    internal enum PreviewMode { Device, Fit, Actual, Dual }

    internal class PreviewBox : Control
    {
        // Export scale puts the top screen at its native 1920 px width.
        private const float ExportScale = 1920f / 790f;
        private const float ExportMargin = 44f;

        private Image image;
        private Image companion;
        private Image poster;
        private bool video;
        private PreviewMode mode = PreviewMode.Device;
        private DeviceSkin skin = DeviceSkin.All[1];
        private Bitmap backdrop;
        private Bitmap deviceCache;
        // Display-only scaled copies of the media; originals stay untouched for export.
        private readonly ScaledImage topScreen = new ScaledImage(), bottomScreen = new ScaledImage(), fitted = new ScaledImage(), posterFit = new ScaledImage();
        private readonly ControlLayers controls = new ControlLayers();
        private readonly ShellPointer pointer = new ShellPointer();
        private readonly Timer animationTimer = new Timer();
        private readonly Stopwatch animationClock = new Stopwatch();
        private double animationTime;
        private readonly ToolTip demoTip = new ToolTip();
        private string demoHint = "";
        private bool disposingControls;
        private PointF pan;
        private Point dragFrom;
        private PointF panFrom;
        private bool dragging;
        private bool playHover;
        private string toast;
        private Color toastColor;
        private DateTime toastUntil;
        private readonly Timer toastTimer = new Timer();
        public bool LowerScreen;
        public string EmptyHint = "";
        private MascotClock face;
        private bool sleepy;

        // No device connected: the mascot naps on the empty screen.
        public bool Sleepy { get { return sleepy; } set { if (sleepy != value) { sleepy = value; Invalidate(); } } }
        public event EventHandler PlayRequested;

        // Touch feedback drawn over the screens. Only the screens that show marks (now or in
        // the previous frame) are repainted.
        private TouchVisual touches = TouchVisual.Empty;
        internal TouchVisual Touches { get { return touches; } }
        internal void SetTouches(TouchVisual value)
        {
            if (value == null) value = TouchVisual.Empty;
            if (value.IsEmpty && touches.IsEmpty) return;
            TouchVisual previous = touches;
            touches = value;
            if (mode != PreviewMode.Device || video) { Invalidate(); return; }
            RectangleF bounds = DeviceBounds();
            for (int i = 0; i < 2; i++)
                if (previous.On(i) || value.On(i))
                    Invalidate(ShellGeometry.ToClient(bounds, i == 0 ? DeviceArt.TopScreen : DeviceArt.BottomScreen));
        }
        private PadState inputState = new PadState();
        private PadState physicalInput;
        internal PadState VisualInput { get { return inputState.Copy(); } }
        internal PadState DemoInput { get { return pointer.State.Copy(); } }
        internal ControlLayers Layers { get { return controls; } }
        public void SetInput(PadState value)
        {
            if (physicalInput == null && value == null) return;
            bool same = physicalInput != null && value != null && physicalInput.Buttons == value.Buttons
                && physicalInput.LT == value.LT && physicalInput.RT == value.RT;
            for (int i = 0; same && i < ShellGeometry.PartCount; i++) same = ControlLayers.SamePart(physicalInput, value, i);
            if (same) return;
            physicalInput = value == null ? null : value.Copy();
            WakeAnimation();
        }

        private void WakeAnimation()
        {
            if (IsDisposed || disposingControls || mode != PreviewMode.Device || video) return;
            if (!animationTimer.Enabled)
            {
                animationClock.Restart(); animationTime = 0; animationTimer.Start();
            }
        }

        internal void AdvanceAnimation(double elapsed)
        {
            PadState target = pointer.Merge(physicalInput);
            PadState next = PadMotion.Step(inputState, target, Math.Min(.25, elapsed));
            RectangleF bounds = DeviceBounds();
            bool settled = true;
            for (int i = 0; i < ShellGeometry.PartCount; i++)
            {
                if (!ControlLayers.SamePart(inputState, next, i)) Invalidate(ShellGeometry.ToClient(bounds, ShellGeometry.Areas[i]));
                if (!ControlLayers.SamePart(next, target, i)) settled = false;
            }
            inputState = next;
            if (settled) { animationTimer.Stop(); animationClock.Stop(); }
        }

        internal RectangleF DeviceBounds()
        {
            float margin = Ui.S(26), top = Ui.S(12);
            float width = Math.Max(0, ClientSize.Width - 2 * margin), height = Math.Max(0, ClientSize.Height - top - margin);
            float scale = Math.Min(width / DeviceArt.Width, height / ShellGeometry.VisualHeight);
            return new RectangleF((ClientSize.Width - DeviceArt.Width * scale) / 2,
                top + (height - ShellGeometry.VisualHeight * scale) / 2, DeviceArt.Width * scale, DeviceArt.Height * scale);
        }

        private void ReleaseDemo()
        {
            if (pointer == null || !pointer.Active) return;
            pointer.Reset();
            if (Capture) Capture = false;
            WakeAnimation();
        }

        public PreviewBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg0;
            Font = Ui.Body;
            animationTimer.Interval = 15;
            animationTimer.Tick += delegate
            {
                double now = animationClock.Elapsed.TotalSeconds;
                AdvanceAnimation(now - animationTime); animationTime = now;
            };
            face = new MascotClock(this);
            toastIn = new Anim(this, 0, 520, Ease.Spring);
            toastIn.Area = ToastArea;
            flashA = new Anim(this, 0, 480, Ease.OutCubic);
            toastTimer.Interval = 16;
            toastTimer.Tick += delegate
            {
                // Expiry starts the exit animation; the toast is dropped once it has faded out.
                if (toast != null && DateTime.UtcNow > toastUntil && toastIn.Target != 0) { toastIn.Curve = Ease.OutCubic; toastIn.To(0, 260); }
                if (toast != null && toastIn.Target == 0 && toastIn.Value <= 0.001f) { toast = null; toastTimer.Stop(); }
                if (toast != null) Invalidate(ToastArea());
            };
        }

        public static Size ExportSize
        {
            get
            {
                return new Size((int)Math.Round((DeviceArt.Width + ExportMargin * 2) * ExportScale),
                                (int)Math.Round((DeviceArt.Height + ExportMargin * 2.6f) * ExportScale));
            }
        }

        public Image Current { get { return image; } }
        public bool HasCompanion { get { return companion != null; } }

        public PreviewMode Mode
        {
            get { return mode; }
            set { ReleaseDemo(); mode = value; pan = PointF.Empty; animationTimer.Stop(); inputState = pointer.Merge(physicalInput); Dirty(); }
        }

        public DeviceSkin Skin
        {
            get { return skin; }
            set
            {
                // The stage aura follows the shell colour, so the backdrop is rebuilt too.
                skin = value;
                if (backdrop != null) { backdrop.Dispose(); backdrop = null; }
                Dirty();
            }
        }

        public void SetImage(Image value)
        {
            SetImages(value, null, LowerScreen);
        }

        public void SetCompanion(Image value)
        {
            if (companion != null) companion.Dispose();
            companion = value;
            ContentChanged();
        }

        // Swaps both screens at once: one repaint, no flash of the placeholder in between,
        // and the shell and control caches survive because only the pictures changed.
        public void SetImages(Image value, Image peer, bool lower)
        {
            if (image != null && !ReferenceEquals(image, value)) image.Dispose();
            if (companion != null && !ReferenceEquals(companion, peer)) companion.Dispose();
            image = value;
            companion = peer;
            LowerScreen = lower;
            if (poster != null) { poster.Dispose(); poster = null; }
            bool wasVideo = video;
            video = false;
            pan = PointF.Empty;
            if (wasVideo) Dirty(); else ContentChanged();
        }

        public void SetLiveFrame(Image value,bool lower)
        { SetLiveFrame(value,lower,false); }
        internal void SetLiveFrame(Image value,bool lower,bool lowerOnly)
        {
            LowerScreen=lowerOnly;
            video=false;
            if(poster!=null) { poster.Dispose(); poster=null; }
            if(lower&&!lowerOnly) { if(companion!=null) companion.Dispose(); companion=value; }
            else { if(image!=null) image.Dispose(); image=value; }
            // Screen updates do not rebuild the shell, lights or stationary controls.
            if (mode == PreviewMode.Device) Invalidate(ShellGeometry.ToClient(DeviceBounds(), lower ? DeviceArt.BottomScreen : DeviceArt.TopScreen));
            else Invalidate();
        }

        internal void SetPlaybackFrames(Bitmap upper,Bitmap lower,bool selectedLower)
        {
            video=false;
            if(poster!=null) { poster.Dispose(); poster=null; }
            if(image!=null) image.Dispose(); if(companion!=null) companion.Dispose();
            image=selectedLower?lower:upper; companion=selectedLower?upper:lower; LowerScreen=selectedLower;
            if(mode==PreviewMode.Device)
            {
                Invalidate(ShellGeometry.ToClient(DeviceBounds(),DeviceArt.TopScreen));
                Invalidate(ShellGeometry.ToClient(DeviceBounds(),DeviceArt.BottomScreen));
            }
            else Invalidate();
        }

        public void ShowVideo()
        {
            ReleaseDemo();
            SetImage(null);
            video = true;
            animationTimer.Stop();
            Invalidate();
        }

        public void SetPoster(Image value)
        {
            if (poster != null) poster.Dispose();
            poster = value;
            ContentChanged();
        }

        private readonly Anim toastIn, flashA;
        private DateTime toastShown;

        public void Toast(string text, Color color)
        {
            bool replacing = toast != null && toastIn.Value > 0.5f;
            toast = text;
            toastColor = color;
            toastShown = DateTime.UtcNow;
            toastUntil = toastShown.AddMilliseconds(2600);
            // Slide in with a slight overshoot; a replacement message just swaps in place.
            toastIn.Curve = Ease.Spring;
            if (replacing) toastIn.To(1, 200); else toastIn.Play(0, 1);
            toastTimer.Start();
            Invalidate(ToastArea());
        }

        // Shutter flash over the preview when a capture is saved.
        public void Flash()
        {
            flashA.Play(1, 0);
        }

        // Media changed but the shell did not: keep the shell and control caches.
        private void ContentChanged()
        {
            if (animationTimer != null && (mode != PreviewMode.Device || video)) animationTimer.Stop();
            Invalidate();
        }

        // Appearance changed (skin, size, mode): rebuild the shell and control layers.
        private void Dirty()
        {
            if (deviceCache != null) { deviceCache.Dispose(); deviceCache = null; }
            if (controls != null) controls.Clear();
            if (animationTimer != null)
            {
                if (mode == PreviewMode.Device && !video) WakeAnimation();
                else animationTimer.Stop();
            }
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                disposingControls = true;
                ReleaseDemo();
                if (image != null) { image.Dispose(); image = null; }
                if (companion != null) { companion.Dispose(); companion = null; }
                if (poster != null) { poster.Dispose(); poster = null; }
                if (backdrop != null) { backdrop.Dispose(); backdrop = null; }
                if (deviceCache != null) { deviceCache.Dispose(); deviceCache = null; }
                topScreen.Dispose(); bottomScreen.Dispose(); fitted.Dispose(); posterFit.Dispose();
                animationTimer.Stop(); animationTimer.Dispose(); demoTip.Dispose(); controls.Dispose();
                toastTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnResize(EventArgs e)
        {
            ReleaseDemo();
            if (backdrop != null) { backdrop.Dispose(); backdrop = null; }
            Dirty();
            base.OnResize(e);
        }

        internal Image TopImage { get { return LowerScreen ? companion : image; } }
        internal Image BottomImage { get { return LowerScreen ? image : companion; } }

        private void PaintBackdrop(Graphics g)
        {
            Size size = ClientSize;
            if (size.Width <= 0 || size.Height <= 0) return;
            if (backdrop == null || backdrop.Size != size)
            {
                if (backdrop != null) backdrop.Dispose();
                backdrop = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                using (Graphics b = Graphics.FromImage(backdrop))
                {
                    // Plain warm grey: the device and the screenshots carry the colour.
                    b.Clear(Ui.Bg0);
                }
            }
            g.DrawImageUnscaled(backdrop, 0, 0);
        }

        private static void Pool(Graphics g, RectangleF area, Color color, int alpha)
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(area);
                using (PathGradientBrush brush = new PathGradientBrush(path))
                {
                    brush.CenterColor = Ui.Alpha(color, alpha);
                    brush.SurroundColors = new Color[] { Ui.Alpha(color, 0) };
                    g.FillPath(brush, path);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            face.Run((mode == PreviewMode.Device ? TopImage == null || BottomImage == null : image == null) && !video && Visible);
            if (mode == PreviewMode.Device && !video) PaintDevice(g);
            else
            {
                PaintBackdrop(g);
                if (video) PaintVideo(g);
                else if (mode == PreviewMode.Dual && showcase && (TopImage != null || BottomImage != null)) PaintShowcase(g);
                else if (mode == PreviewMode.Dual && (TopImage != null || BottomImage != null)) PaintDual(g);
                else if (image == null) PaintEmpty(g);
                else PaintImage(g);
            }
            PaintFlash(g);
            PaintToast(g);
        }

        private void PaintDevice(Graphics g)
        {
            Size size = ClientSize;
            if (size.Width <= 0 || size.Height <= 0) return;
            RectangleF bounds = DeviceBounds();
            if (deviceCache == null || deviceCache.Size != size)
            {
                if (deviceCache != null) deviceCache.Dispose();
                deviceCache = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                using (Graphics c = Graphics.FromImage(deviceCache))
                {
                    PaintBackdrop(c);
                    DeviceArt.Draw(c, bounds, skin, null, null, null, null, null,false);
                }
            }
            g.DrawImageUnscaled(deviceCache, 0, 0);
            PaintScreen(g, bounds, true, TopImage);
            PaintScreen(g, bounds, false, BottomImage);
            PaintTouches(g, bounds);
            if (TopImage == null || BottomImage == null)
            {
                // An empty screen shows the mascot instead of a caption: on the upper screen when
                // nothing is selected, otherwise on whichever screen has no paired capture.
                // It naps while no Thor is connected.
                Rectangle screen = ShellGeometry.ToClient(bounds, TopImage == null ? DeviceArt.TopScreen : DeviceArt.BottomScreen);
                face.Area = screen;
                Mascot.DrawFace(g, screen, sleepy ? MascotMood.Sleep : MascotMood.Idle, face.Time, Color.FromArgb(150, 214, 212, 208));
            }
            controls.Paint(g, bounds, skin, inputState);
        }

        private void PaintTouches(Graphics g, RectangleF bounds)
        {
            if (touches.IsEmpty) return;
            for (int i = 0; i < 2; i++)
            {
                if (!touches.On(i)) continue;
                float unit;
                RectangleF screen = TouchFx.ScreenRect(bounds, i, out unit);
                TouchFx.Draw(g, screen, unit, touches, i);
            }
        }

        // Screens are drawn from a cached copy scaled to the current screen rectangle; the
        // expensive resample only happens when the picture or the rectangle changes.
        private void PaintScreen(Graphics g, RectangleF bounds, bool top, Image picture)
        {
            Rectangle rect = ShellGeometry.ToClient(bounds, top ? DeviceArt.TopScreen : DeviceArt.BottomScreen);
            if (!g.IsVisible(rect)) return;
            if (picture == null) { DeviceArt.DrawScreen(g, bounds, top, null); return; }
            ScaledImage cache = top ? topScreen : bottomScreen;
            Bitmap scaled = cache.Get(picture, rect.Size, delegate(Graphics c)
            {
                c.TranslateTransform(-rect.X, -rect.Y);
                DeviceArt.DrawScreen(c, bounds, top, picture);
            });
            g.DrawImageUnscaled(scaled, rect.Location);
        }

        private void PaintEmpty(Graphics g)
        {
            Rectangle r = ClientRectangle;
            int d = Ui.S(120), mid = r.Height / 2;
            face.Area = new Rectangle(r.Width / 2 - d / 2 - Ui.S(20), mid - d - Ui.S(24), d + Ui.S(50), d + Ui.S(30));
            Mascot.Draw(g, new RectangleF(r.Width / 2 - d / 2, mid - d, d, d), sleepy ? MascotMood.Sleep : MascotMood.Idle, face.Time);
            Ui.Write(g, sleepy ? "等待连接 Thor" : "没有选中的截图", Ui.Heading, new Rectangle(0, mid + Ui.S(14), r.Width, Ui.S(28)), Ui.TextDim, TextFormatFlags.HorizontalCenter);
        }

        private float FitScale()
        {
            int margin = Ui.S(28);
            float scale = Math.Min((float)(ClientSize.Width - margin * 2) / image.Width, (float)(ClientSize.Height - margin * 2) / image.Height);
            if (scale > 1f) scale = (float)Math.Floor(scale);
            return scale;
        }

        private PointF ClampPan(PointF value)
        {
            if (image == null) return PointF.Empty;
            float spareX = Math.Max(0, (image.Width - ClientSize.Width) / 2f + Ui.S(20));
            float spareY = Math.Max(0, (image.Height - ClientSize.Height) / 2f + Ui.S(20));
            if (image.Width <= ClientSize.Width) spareX = 0;
            if (image.Height <= ClientSize.Height) spareY = 0;
            return new PointF(Math.Max(-spareX, Math.Min(spareX, value.X)), Math.Max(-spareY, Math.Min(spareY, value.Y)));
        }

        private bool Pannable
        {
            get { return mode == PreviewMode.Actual && image != null && !video && (image.Width > ClientSize.Width || image.Height > ClientSize.Height); }
        }

        private void PaintImage(Graphics g)
        {
            float scale = mode == PreviewMode.Actual ? 1f : FitScale();
            if (scale <= 0) return;
            float w = image.Width * scale, h = image.Height * scale;
            float x = (ClientSize.Width - w) / 2f, y = (ClientSize.Height - h) / 2f;
            if (mode == PreviewMode.Actual) { x += pan.X; y += pan.Y; }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Ui.Shadow(g, new RectangleF(x, y, w, h), Ui.S(4), Ui.S(18), 60, Ui.S(8), Ui.Ink);
            g.SmoothingMode = SmoothingMode.None;
            if (scale < 1f && mode == PreviewMode.Fit)
            {
                // Downscaled fit: resample once per picture and size, then blit.
                Size size = new Size(Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
                Image source = image;
                Bitmap scaled = fitted.Get(source, size, delegate(Graphics c)
                {
                    c.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    c.PixelOffsetMode = PixelOffsetMode.Half;
                    c.DrawImage(source, new Rectangle(0, 0, size.Width, size.Height));
                });
                g.DrawImageUnscaled(scaled, (int)Math.Round(x), (int)Math.Round(y));
            }
            else
            {
                g.InterpolationMode = scale >= 1f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(image, new RectangleF(x, y, w, h));
                g.PixelOffsetMode = PixelOffsetMode.Default;
            }
            using (Pen edge = new Pen(Ui.Wash(0.08f))) g.DrawRectangle(edge, x - 1, y - 1, w + 1, h + 1);

            if (!touches.IsEmpty)
            {
                int shown = LowerScreen ? 1 : 0;
                TouchFx.Draw(g, new RectangleF(x, y, w, h), w / (shown == 0 ? DeviceArt.TopScreen.Width : DeviceArt.BottomScreen.Width), touches, shown);
            }
            string chip = Math.Round(scale * 100) + "%" + (Pannable ? "  ·  拖动平移" : "");
            int cw = Ui.Measure(chip, Ui.Small) + Ui.S(20);
            Rectangle pill = new Rectangle(ClientSize.Width - cw - Ui.S(14), ClientSize.Height - Ui.S(38), cw, Ui.S(24));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Ui.Shadow(g, pill, Ui.S(12), Ui.S(6), 30, Ui.S(2), Ui.Ink);
            Ui.Fill(g, pill, Ui.S(12), Ui.Alpha(Ui.Surface, 235));
            Ui.Write(g, chip, Ui.Small, pill, Ui.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private Rectangle PlayButton()
        {
            int d = Ui.S(76);
            return new Rectangle((ClientSize.Width - d) / 2, (ClientSize.Height - d) / 2, d, d);
        }

        private void PaintVideo(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int margin = Ui.S(40);
            if (poster != null)
            {
                float scale = Math.Min((float)(ClientSize.Width - margin * 2) / poster.Width, (float)(ClientSize.Height - margin * 2) / poster.Height);
                float w = poster.Width * scale, h = poster.Height * scale;
                RectangleF r = new RectangleF((ClientSize.Width - w) / 2f, (ClientSize.Height - h) / 2f, w, h);
                Ui.Shadow(g, r, Ui.S(10), Ui.S(20), 60, Ui.S(8), Ui.Ink);
                Size size = new Size(Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
                Image source = poster;
                Bitmap scaled = posterFit.Get(source, size, delegate(Graphics c)
                {
                    c.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    c.DrawImage(source, new Rectangle(0, 0, size.Width, size.Height));
                    using (SolidBrush dim = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) c.FillRectangle(dim, 0, 0, size.Width, size.Height);
                });
                using (GraphicsPath clip = Ui.Round(new RectangleF((int)Math.Round(r.X), (int)Math.Round(r.Y), size.Width, size.Height), Ui.S(10)))
                {
                    Region saved = g.Clip;
                    g.SetClip(clip, CombineMode.Intersect);
                    g.DrawImageUnscaled(scaled, (int)Math.Round(r.X), (int)Math.Round(r.Y));
                    g.Clip = saved;
                    saved.Dispose();
                }
            }
            else
            {
                RectangleF r = new RectangleF(ClientSize.Width / 2f - Ui.S(200), ClientSize.Height / 2f - Ui.S(120), Ui.S(400), Ui.S(240));
                Ui.Fill(g, r, Ui.S(14), Ui.Bg2);
                Ui.Stroke(g, r, Ui.S(14), Ui.Line, 1f);
            }
            Rectangle play = PlayButton();
            Ui.Shadow(g, play, play.Width / 2f, Ui.S(12), playHover ? 90 : 50, Ui.S(4), playHover ? Ui.AccentDeep : Ui.Ink);
            if (playHover) using (LinearGradientBrush disc = Ui.AccentBrush(play, 0)) g.FillEllipse(disc, play);
            else using (SolidBrush disc = new SolidBrush(Color.FromArgb(200, 22, 26, 44))) g.FillEllipse(disc, play);
            using (Pen ring = new Pen(Color.FromArgb(150, 255, 255, 255), 1.5f)) g.DrawEllipse(ring, play);
            float cx = play.X + play.Width / 2f + Ui.S(3), cy = play.Y + play.Height / 2f, s = Ui.S(15);
            using (SolidBrush arrow = new SolidBrush(Color.White))
                g.FillPolygon(arrow, new PointF[] { new PointF(cx - s * 0.7f, cy - s), new PointF(cx - s * 0.7f, cy + s), new PointF(cx + s, cy) });
            Ui.Write(g, "点击用系统播放器打开", Ui.Body, new Rectangle(0, play.Bottom + Ui.S(14), ClientSize.Width, Ui.S(24)),
                Ui.Text, TextFormatFlags.HorizontalCenter);
        }

        private Rectangle ToastArea()
        {
            return new Rectangle(0, 0, ClientSize.Width, Ui.S(76));
        }

        private void PaintFlash(Graphics g)
        {
            float f = flashA.Value;
            if (f <= 0.001f) return;
            using (SolidBrush white = new SolidBrush(Color.FromArgb((int)(120 * f), 255, 255, 255))) g.FillRectangle(white, ClientRectangle);
        }

        private void PaintToast(Graphics g)
        {
            if (toast == null) return;
            float v = Math.Max(0, toastIn.Value);
            int alpha = (int)(255 * Math.Min(1, v * 1.4f));
            if (alpha <= 0) return;
            // Dark pill that drops in on a spring, stretching tall as it falls and wide as it lands.
            bool cheer = toastColor.ToArgb() == Ui.Ok.ToArgb();
            int lead = cheer ? Ui.S(46) : Ui.S(34);
            int w = Ui.Measure(toast, Ui.Body) + lead + Ui.S(22);
            float squash = Math.Max(-0.12f, Math.Min(0.12f, (v - 1) * 1.2f));
            float rw = w * (1 - squash * 0.5f), rh = Ui.S(40) * (1 + squash);
            RectangleF r = new RectangleF((ClientSize.Width - rw) / 2, Ui.S(18) - (1 - Math.Min(1, v)) * Ui.S(36), rw, rh);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Ui.Fill(g, r, r.Height / 2, Ui.Alpha(Ui.AccentDeep, alpha * 240 / 255));
            if (cheer)
                // A tiny mascot hops inside a success message.
                Mascot.Draw(g, new RectangleF(r.X + Ui.S(10), r.Y + Ui.S(3), Ui.S(32), Ui.S(32)), MascotMood.Happy, (DateTime.UtcNow - toastShown).TotalMilliseconds);
            else
                using (SolidBrush dot = new SolidBrush(Ui.Alpha(toastColor, alpha)))
                    g.FillEllipse(dot, r.X + Ui.S(18), r.Y + r.Height / 2 - Ui.S(4), Ui.S(8), Ui.S(8));
            Ui.Write(g, toast, Ui.Body, new Rectangle((int)r.X + lead, (int)r.Y, (int)r.Width - lead - Ui.S(10), (int)r.Height),
                Ui.Alpha(Color.White, alpha), TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (mode == PreviewMode.Device && !video && Enabled && pointer.Begin(ShellGeometry.ToDesign(DeviceBounds(), e.Location), e.Button))
            {
                Focus(); Capture = true; WakeAnimation();
                base.OnMouseDown(e); return;
            }
            if (e.Button == MouseButtons.Left && video && PlayButton().Contains(e.Location))
            {
                if (PlayRequested != null) PlayRequested(this, EventArgs.Empty);
            }
            else if (e.Button == MouseButtons.Left && Pannable)
            {
                dragging = true;
                dragFrom = e.Location;
                panFrom = pan;
                Capture = true;
                Cursor = Cursors.SizeAll;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (pointer.Active)
            {
                pointer.Move(ShellGeometry.ToDesign(DeviceBounds(), e.Location)); WakeAnimation();
            }
            else if (mode == PreviewMode.Device && !video)
            {
                PadButton button;
                int part = ShellGeometry.Hit(ShellGeometry.ToDesign(DeviceBounds(), e.Location), out button);
                Cursor = part >= 0 ? Cursors.Hand : Cursors.Default;
                string hint = ShellGeometry.Hint(part, button);
                if (hint != demoHint) { demoHint = hint; demoTip.SetToolTip(this, hint); }
            }
            else if (dragging)
            {
                pan = ClampPan(new PointF(panFrom.X + e.X - dragFrom.X, panFrom.Y + e.Y - dragFrom.Y));
                Invalidate();
            }
            else if (video)
            {
                bool hot = PlayButton().Contains(e.Location);
                if (hot != playHover) { playHover = hot; Invalidate(PlayButton()); }
                Cursor = hot ? Cursors.Hand : Cursors.Default;
            }
            else Cursor = Pannable ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (pointer.End(e.Button)) { if (Capture) Capture = false; WakeAnimation(); }
            if (dragging) { dragging = false; Capture = false; Cursor = Pannable ? Cursors.Hand : Cursors.Default; }
            base.OnMouseUp(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture) { dragging = false; ReleaseDemo(); }
            base.OnMouseCaptureChanged(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            if (!Enabled) ReleaseDemo();
            base.OnEnabledChanged(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            if (!Visible)
            {
                ReleaseDemo();
                if (animationTimer != null) animationTimer.Stop();
            }
            else if (animationTimer != null) WakeAnimation();
            base.OnVisibleChanged(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            ReleaseDemo();
            base.OnLostFocus(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Pannable)
            {
                float step = e.Delta * Ui.S(90) / 120f;
                pan = ClampPan((ModifierKeys & Keys.Shift) != 0 ? new PointF(pan.X + step, pan.Y) : new PointF(pan.X, pan.Y + step));
                Invalidate();
            }
            base.OnMouseWheel(e);
        }

        // ---- both screens without the shell ------------------------------------------
        // The two screens at their own pixel sizes, the lower one centred under the upper one with a
        // small gap. A missing screen keeps its place at the usual size so the layout never jumps.
        private const int DualGap = 48;
        private static Size ScreenSize(Image picture, bool top) { return picture != null ? picture.Size : top ? new Size(1920, 1080) : new Size(1240, 1080); }

        internal static Rectangle[] DualLayout(Size top, Size bottom, out Size total)
        {
            int width = Math.Max(top.Width, bottom.Width);
            total = new Size(width, top.Height + DualGap + bottom.Height);
            return new[] { new Rectangle((width - top.Width) / 2, 0, top.Width, top.Height), new Rectangle((width - bottom.Width) / 2, top.Height + DualGap, bottom.Width, bottom.Height) };
        }

        private void PaintDual(Graphics g)
        {
            Size total;
            Rectangle[] parts = DualLayout(ScreenSize(TopImage, true), ScreenSize(BottomImage, false), out total);
            int margin = Ui.S(28);
            float scale = Math.Min(1f, Math.Min((ClientSize.Width - margin * 2f) / total.Width, (ClientSize.Height - margin * 2f) / total.Height));
            if (scale <= 0) return;
            float ox = (ClientSize.Width - total.Width * scale) / 2f, oy = (ClientSize.Height - total.Height * scale) / 2f;
            Image[] pictures = { TopImage, BottomImage };
            ScaledImage[] caches = { topScreen, bottomScreen };
            for (int i = 0; i < 2; i++)
            {
                RectangleF r = new RectangleF(ox + parts[i].X * scale, oy + parts[i].Y * scale, parts[i].Width * scale, parts[i].Height * scale);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (pictures[i] == null)
                {
                    Ui.Fill(g, r, Ui.S(6), Ui.Alpha(Ui.Bg3, 160));
                    Ui.Write(g, i == 0 ? "没有上屏画面" : "没有配对的下屏", Ui.Small, Rectangle.Round(r), Ui.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    continue;
                }
                Ui.Shadow(g, r, Ui.S(4), Ui.S(16), 55, Ui.S(6), Ui.Ink);
                g.SmoothingMode = SmoothingMode.None;
                Size size = new Size(Math.Max(1, (int)Math.Round(r.Width)), Math.Max(1, (int)Math.Round(r.Height)));
                Image source = pictures[i];
                if (scale < 1f)
                {
                    // Resampled once per picture and size; live frames replace the source and so the cache entry.
                    Bitmap scaled = caches[i].Get(source, size, delegate(Graphics c)
                    {
                        c.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        c.PixelOffsetMode = PixelOffsetMode.Half;
                        c.DrawImage(source, new Rectangle(0, 0, size.Width, size.Height));
                    });
                    g.DrawImageUnscaled(scaled, (int)Math.Round(r.X), (int)Math.Round(r.Y));
                }
                else g.DrawImage(source, new Rectangle((int)Math.Round(r.X), (int)Math.Round(r.Y), size.Width, size.Height));
                using (Pen edge = new Pen(Ui.Wash(0.08f))) g.DrawRectangle(edge, r.X - 1, r.Y - 1, r.Width + 1, r.Height + 1);
                if (!touches.IsEmpty) TouchFx.Draw(g, r, r.Width / (i == 0 ? DeviceArt.TopScreen.Width : DeviceArt.BottomScreen.Width), touches, i);
            }
            string chip = "双屏  ·  " + Math.Round(scale * 100) + "%";
            int cw = Ui.Measure(chip, Ui.Small) + Ui.S(20);
            Rectangle pill = new Rectangle(ClientSize.Width - cw - Ui.S(14), ClientSize.Height - Ui.S(38), cw, Ui.S(24));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Ui.Shadow(g, pill, Ui.S(12), Ui.S(6), 30, Ui.S(2), Ui.Ink);
            Ui.Fill(g, pill, Ui.S(12), Ui.Alpha(Ui.Surface, 235));
            Ui.Write(g, chip, Ui.Small, pill, Ui.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ---- showcase: a captioned presentation of both screens ----------------------
        // Dark page 1920 wide: a title line, the upper screen edge to edge, a caption for the lower
        // screen, then the lower screen scaled to (nearly) the same width. An empty title leaves the band
        // out entirely; an empty caption leaves only a narrow gap between the screens.
        private bool showcase;
        private string showcaseTitle = "", showcaseLabel = "";
        internal const int ShowWidth = 1920, ShowTitle = 84, ShowLabel = 70, ShowGap = 20, ShowInset = 28, ShowFoot = 28;
        private static readonly Color ShowPage = Color.FromArgb(16, 16, 17), ShowTitleBand = Color.FromArgb(24, 24, 26),
            ShowTitleInk = Color.FromArgb(236, 236, 236), ShowLabelInk = Color.FromArgb(214, 198, 168);

        public bool Showcase { get { return showcase; } set { if (showcase == value) return; showcase = value; Invalidate(); } }
        public string ShowcaseTitle { get { return showcaseTitle; } set { showcaseTitle = value ?? ""; if (showcase) Invalidate(); } }
        public string ShowcaseLabel { get { return showcaseLabel; } set { showcaseLabel = value ?? ""; if (showcase) Invalidate(); } }

        internal static Rectangle[] ShowcaseLayout(Size top, Size bottom, bool titled, bool captioned, out Size total)
        {
            int topHeight = (int)Math.Round(ShowWidth * (double)top.Height / top.Width);
            int bottomWidth = ShowWidth - ShowInset * 2, bottomHeight = (int)Math.Round(bottomWidth * (double)bottom.Height / bottom.Width);
            int band = titled ? ShowTitle : 0;
            Rectangle upper = new Rectangle(0, band, ShowWidth, topHeight);
            Rectangle label = new Rectangle(0, upper.Bottom, ShowWidth, captioned ? ShowLabel : ShowGap);
            Rectangle lower = new Rectangle(ShowInset, label.Bottom, bottomWidth, bottomHeight);
            total = new Size(ShowWidth, lower.Bottom + ShowFoot);
            return new[] { new Rectangle(0, 0, ShowWidth, band), upper, label, lower };
        }

        // Draws the page in its own 1920-wide units; callers scale the Graphics for the screen.
        internal static void DrawShowcase(Graphics g, Image top, Image bottom, string title, string label)
        {
            Size total;
            bool titled = !string.IsNullOrWhiteSpace(title), captioned = !string.IsNullOrWhiteSpace(label);
            Rectangle[] parts = ShowcaseLayout(ScreenSize(top, true), ScreenSize(bottom, false), titled, captioned, out total);
            using (SolidBrush page = new SolidBrush(ShowPage)) g.FillRectangle(page, 0, 0, total.Width, total.Height);
            if (titled) using (SolidBrush band = new SolidBrush(ShowTitleBand)) g.FillRectangle(band, parts[0]);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            Image[] pictures = { top, bottom };
            Rectangle[] places = { parts[1], parts[3] };
            for (int i = 0; i < 2; i++)
            {
                if (pictures[i] != null) { g.DrawImage(pictures[i], places[i]); continue; }
                using (SolidBrush empty = new SolidBrush(Color.FromArgb(32, 32, 34))) g.FillRectangle(empty, places[i]);
            }
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (StringFormat line = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
            {
                using (Font font = new Font("Microsoft YaHei UI", 34f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush ink = new SolidBrush(ShowTitleInk))
                    g.DrawString(title ?? "", font, ink, new RectangleF(32, parts[0].Y, ShowWidth - 64, parts[0].Height), line);
                using (Font font = new Font("Microsoft YaHei UI", 29f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush ink = new SolidBrush(ShowLabelInk))
                    g.DrawString(label ?? "", font, ink, new RectangleF(ShowInset + 4, parts[2].Y, ShowWidth - ShowInset * 2, parts[2].Height), line);
            }
        }

        private void PaintShowcase(Graphics g)
        {
            Size total;
            ShowcaseLayout(ScreenSize(TopImage, true), ScreenSize(BottomImage, false), !string.IsNullOrWhiteSpace(showcaseTitle), !string.IsNullOrWhiteSpace(showcaseLabel), out total);
            int margin = Ui.S(24);
            float scale = Math.Min(1f, Math.Min((ClientSize.Width - margin * 2f) / total.Width, (ClientSize.Height - margin * 2f) / total.Height));
            if (scale <= 0) return;
            RectangleF page = new RectangleF((ClientSize.Width - total.Width * scale) / 2f, (ClientSize.Height - total.Height * scale) / 2f, total.Width * scale, total.Height * scale);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Ui.Shadow(g, page, Ui.S(6), Ui.S(18), 60, Ui.S(8), Ui.Ink);
            GraphicsState state = g.Save();
            g.TranslateTransform(page.X, page.Y);
            g.ScaleTransform(scale, scale);
            DrawShowcase(g, TopImage, BottomImage, showcaseTitle, showcaseLabel);
            g.Restore(state);
            string chip = "展示  ·  " + Math.Round(scale * 100) + "%";
            int cw = Ui.Measure(chip, Ui.Small) + Ui.S(20);
            Rectangle pill = new Rectangle(ClientSize.Width - cw - Ui.S(14), ClientSize.Height - Ui.S(38), cw, Ui.S(24));
            Ui.Shadow(g, pill, Ui.S(12), Ui.S(6), 30, Ui.S(2), Ui.Ink);
            Ui.Fill(g, pill, Ui.S(12), Ui.Alpha(Ui.Surface, 235));
            Ui.Write(g, chip, Ui.Small, pill, Ui.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // Both screens at full resolution, no shell; null when this capture has no paired screen.
        // transparent: the gap and the margins beside the narrower screen stay clear (PNG); otherwise black.
        // In the showcase style the result is the captioned page instead.
        public Bitmap ExportDual(bool transparent)
        {
            Image top = TopImage, bottom = BottomImage;
            if (top == null || bottom == null) return null;
            if (showcase)
            {
                Size page;
                ShowcaseLayout(top.Size, bottom.Size, !string.IsNullOrWhiteSpace(showcaseTitle), !string.IsNullOrWhiteSpace(showcaseLabel), out page);
                Bitmap sheet = new Bitmap(page.Width, page.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(sheet)) DrawShowcase(g, top, bottom, showcaseTitle, showcaseLabel);
                return sheet;
            }
            Size total;
            Rectangle[] parts = DualLayout(top.Size, bottom.Size, out total);
            Bitmap result = new Bitmap(total.Width, total.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.Clear(transparent ? Color.Transparent : Color.Black);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(top, parts[0]);
                g.DrawImage(bottom, parts[1]);
            }
            return result;
        }

        // Transparent PNG of the shell with both screens; the originals are untouched.
        public Bitmap ExportDevice()
        {
            return ExportDevice(true);
        }

        public Bitmap ExportDevice(bool transparent)
        {
            Size size = ExportSize;
            Bitmap result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.Clear(transparent ? Color.Transparent : Color.FromArgb(236, 237, 241));
                float m = ExportMargin * ExportScale;
                DeviceArt.Draw(g, new RectangleF(m, m * 0.6f, DeviceArt.Width * ExportScale, DeviceArt.Height * ExportScale),
                    skin, TopImage, BottomImage, null, null);
            }
            return result;
        }
    }
}
