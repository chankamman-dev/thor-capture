using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ThorCapture
{
    internal static class ShellInteractionTests
    {
        private sealed class Stage : PreviewBox
        {
            internal void Down(Point point, MouseButtons button) { OnMouseDown(new MouseEventArgs(button, 1, point.X, point.Y, 0)); }
            internal void Drag(Point point) { OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, 0)); }
            internal void Up(Point point, MouseButtons button) { OnMouseUp(new MouseEventArgs(button, 1, point.X, point.Y, 0)); }
            internal void Render(Graphics graphics) { OnPaint(new PaintEventArgs(graphics, ClientRectangle)); }
            internal Point ClientPoint(PointF design)
            {
                RectangleF bounds = DeviceBounds();
                PointF origin = ShellGeometry.Origin(bounds);
                float scale = ShellGeometry.Scale(bounds);
                return Point.Round(new PointF(origin.X + design.X * scale, origin.Y + design.Y * scale));
            }
        }

        internal static void Run()
        {
            PadButton button;
            Tests.Check(ShellGeometry.Hit(new PointF(400, 200), out button) == -1 && ShellGeometry.Hit(new PointF(430, 750), out button) == -1,
                "screen clicks are not shell button demos");
            bool hits = true;
            for (int i = 0; i < ShellGeometry.FaceCenters.Length; i++)
            {
                hits &= ShellGeometry.Hit(ShellGeometry.FaceCenters[i], out button) == 2 && button == ShellGeometry.FaceButtons[i];
                hits &= ShellGeometry.Hit(ShellGeometry.MiniCenters[i], out button) == 4 + i && button == ShellGeometry.MiniButtons[i];
                hits &= ShellGeometry.Hit(new PointF(329 + i * 74, 1066), out button) == 9 && button == ShellGeometry.Shoulders[i];
            }
            Tests.Check(hits, "ABXY, system buttons and shoulder overlays have correct hit targets");
            Tests.Check(ShellGeometry.Direction(new PointF(123, 811)) == PadButton.Up
                && ShellGeometry.Direction(new PointF(141, 842.8f)) == (PadButton.Up | PadButton.Right)
                && ShellGeometry.Direction(new PointF(123, 860.8f)) == 0, "D-pad supports cardinal, diagonal and neutral positions");

            ShellPointer pointer = new ShellPointer();
            Tests.Check(pointer.Begin(new PointF(115, 696), MouseButtons.Left), "stick drag can start at the cap");
            pointer.Move(new PointF(215, 596));
            Tests.Check(Math.Abs(pointer.State.LX * pointer.State.LX + pointer.State.LY * pointer.State.LY - 1) < .0001
                && pointer.State.LY < 0, "stick demo is radially clamped and follows drag direction");
            Tests.Check(!pointer.Begin(new PointF(824, 691), MouseButtons.Left) && !pointer.End(MouseButtons.Right), "extra mouse buttons cannot steal an active drag");
            pointer.End(MouseButtons.Left);
            Tests.Check(pointer.State.LX == 0 && pointer.State.Buttons == 0 && !pointer.Active, "mouse release returns the local demo to neutral");
            pointer.Begin(new PointF(766, 860), MouseButtons.Right);
            Tests.Check(pointer.State.Down(PadButton.R3) && pointer.State.RX == 0, "right click presses R3 without moving the stick");
            pointer.Reset();
            pointer.Begin(ShellGeometry.FaceCenters[1], MouseButtons.Left);
            pointer.Move(new PointF(500, 500));
            Tests.Check(pointer.State.Buttons == 0, "dragging out of a face button releases its pressure");
            pointer.Move(ShellGeometry.FaceCenters[1]);
            Tests.Check(pointer.State.Down(PadButton.A), "re-entering a held face button restores local pressure");

            PadState physical = new PadState { Buttons = PadButton.B, RX = .65f };
            PadState combined = pointer.Merge(physical);
            Tests.Check(combined.Down(PadButton.A) && combined.Down(PadButton.B) && combined.RX == .65f
                && physical.Buttons == PadButton.B, "demo and physical input merge without modifying the recording source");
            pointer.Reset();
            PadState target = new PadState { Buttons = PadButton.A | PadButton.Up, LX = 1 };
            PadState fast = new PadState(), slow = new PadState();
            for (int i = 0; i < 6; i++) fast = PadMotion.Step(fast, target, .01);
            for (int i = 0; i < 2; i++) slow = PadMotion.Step(slow, target, .03);
            Tests.Check(Math.Abs(fast.LX - slow.LX) < .0001 && Math.Abs(fast.TiltY - slow.TiltY) < .0001
                && Math.Abs(fast.Press(PadButton.A) - slow.Press(PadButton.A)) < .0001, "animation follows elapsed time rather than tick count");
            PadState pressed = PadMotion.Step(new PadState(), target, .015);
            PadState releasing = PadMotion.Step(pressed, new PadState(), .015);
            Tests.Check(pressed.Press(PadButton.A) > 0 && pressed.Press(PadButton.A) < 1
                && releasing.Press(PadButton.A) > 0 && releasing.Press(PadButton.A) < pressed.Press(PadButton.A), "button press and release have continuous travel");
            for (int i = 0; i < 30; i++) releasing = PadMotion.Step(releasing, new PadState(), .015);
            Tests.Check(releasing.Press(PadButton.A) == 0 && releasing.TiltY == 0, "released controls settle exactly without endless invalidation");
            PadState copy = pressed.Copy(); copy.Pressures[0] = 0;
            Tests.Check(pressed.Press(PadButton.A) > 0, "animated snapshots do not share mutable pressure arrays");

            foreach (Size size in new Size[] { new Size(760, 940), new Size(1200, 1440) })
            using (Stage stage = new Stage { Size = size, Skin = DeviceSkin.All[2] })
            using (Bitmap bitmap = new Bitmap(size.Width, size.Height))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                stage.CreateControl(); stage.Render(graphics);
                Point a = stage.ClientPoint(ShellGeometry.FaceCenters[1]);
                stage.Down(a, MouseButtons.Left); stage.AdvanceAnimation(.05);
                Tests.Check(stage.DemoInput.Down(PadButton.A) && stage.VisualInput.Press(PadButton.A) > .5f, "scaled mouse hit drives animated A at " + size.Width);
                stage.Capture = false;
                for (int i = 0; i < 10; i++) stage.AdvanceAnimation(.05);
                Tests.Check(stage.DemoInput.Buttons == 0 && stage.VisualInput.Press(PadButton.A) == 0, "capture loss clears held demo at " + size.Width);
                stage.Render(graphics);
                long left = stage.Layers.RenderCounts[0], right = stage.Layers.RenderCounts[3], dpad = stage.Layers.RenderCounts[1];
                stage.SetInput(new PadState { Buttons = PadButton.Up });
                for (int i = 0; i < 12; i++) { stage.AdvanceAnimation(.015); stage.Render(graphics); }
                Tests.Check(stage.Layers.RenderCounts[0] == left && stage.Layers.RenderCounts[3] == right && stage.Layers.RenderCounts[1] > dpad,
                    "D-pad animation reuses both stick and lamp layers at " + size.Width);
                long[] counts = (long[])stage.Layers.RenderCounts.Clone();
                stage.SetLiveFrame(new Bitmap(16, 16), false); stage.Render(graphics);
                bool unchanged = true;
                for (int i = 0; i < counts.Length; i++) unchanged &= counts[i] == stage.Layers.RenderCounts[i];
                Tests.Check(unchanged, "video frames do not rebuild control layers at " + size.Width);
                stage.Skin = DeviceSkin.All[2].WithLamp(true, Color.Red, .3f); stage.Render(graphics);
                Tests.Check(stage.Layers.RenderCounts[0] > left, "lamp settings invalidate stick layers at " + size.Width);
                Point shoulder = stage.ClientPoint(new PointF(329, 1066));
                Tests.Check(stage.ClientRectangle.Contains(shoulder), "shoulder overlays fit within the preview at " + size.Width);
                stage.Down(a, MouseButtons.Left); stage.Mode = PreviewMode.Fit;
                Tests.Check(stage.DemoInput.Buttons == 0, "changing view releases a held demo at " + size.Width);
                stage.Down(a, MouseButtons.Left);
                Tests.Check(stage.DemoInput.Buttons == 0, "flat image view does not accept shell demos at " + size.Width);
                stage.Mode = PreviewMode.Device; stage.SetInput(null);
                Point stick = stage.ClientPoint(new PointF(115, 696));
                stage.Down(stick, MouseButtons.Left); stage.Drag(stage.ClientPoint(new PointF(143, 680))); stage.AdvanceAnimation(.06);
                Tests.Check(stage.VisualInput.LX > .5f && stage.VisualInput.LY < -.25f, "scaled mouse drag animates a stick at " + size.Width);
                stage.Up(stick, MouseButtons.Left);
                for (int i = 0; i < 10; i++) stage.AdvanceAnimation(.05);
                Tests.Check(stage.VisualInput.LX == 0 && stage.VisualInput.LY == 0, "released stick returns to center at " + size.Width);
                Point rightStick = stage.ClientPoint(new PointF(766, 860));
                stage.Down(rightStick, MouseButtons.Right); stage.AdvanceAnimation(.05);
                Tests.Check(stage.VisualInput.Press(PadButton.R3) > .5f, "scaled right click animates R3 at " + size.Width);
                stage.Enabled = false;
                for (int i = 0; i < 10; i++) stage.AdvanceAnimation(.05);
                Tests.Check(stage.DemoInput.Buttons == 0 && stage.VisualInput.Press(PadButton.R3) == 0, "disabling the preview releases a held stick at " + size.Width);
                stage.Enabled = true;
                int plays = 0; stage.PlayRequested += delegate { plays++; };
                stage.ShowVideo(); stage.Down(new Point(size.Width / 2, size.Height / 2), MouseButtons.Left);
                Tests.Check(plays == 1 && stage.DemoInput.Buttons == 0, "video play click remains separate from shell demos at " + size.Width);
            }
            CheckLayers();
        }

        private static void CheckLayers()
        {
            RectangleF bounds = new RectangleF(20, 10, 600, 715);
            foreach (DeviceSkin skin in new DeviceSkin[] { DeviceSkin.All[2], DeviceSkin.All[3] })
            using (Bitmap direct = new Bitmap(660, 790, PixelFormat.Format32bppPArgb))
            using (Bitmap cached = new Bitmap(660, 790, PixelFormat.Format32bppPArgb))
            using (ControlLayers layers = new ControlLayers())
            {
                PadState pose = new PadState { LX = -.8f, LY = .4f, RX = .6f, RY = -.6f, Buttons = PadButton.Down | PadButton.A | PadButton.R3 };
                using (Graphics g = Graphics.FromImage(direct)) { g.Clear(Ui.Bg0); DeviceArt.DrawControls(g, bounds, skin, pose); }
                using (Graphics g = Graphics.FromImage(cached)) { g.Clear(Ui.Bg0); layers.Paint(g, bounds, skin, pose); }
                double worst = 0;
                for (int part = 0; part < ShellGeometry.PartCount; part++)
                {
                    Rectangle area = Rectangle.Intersect(new Rectangle(Point.Empty, direct.Size), ShellGeometry.ToClient(bounds, ShellGeometry.Areas[part]));
                    long error = 0, samples = 0;
                    for (int y = area.Top; y < area.Bottom; y += 2)
                        for (int x = area.Left; x < area.Right; x += 2)
                        {
                            Color a = direct.GetPixel(x, y), b = cached.GetPixel(x, y);
                            error += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B); samples += 3;
                        }
                    if (samples > 0) worst = Math.Max(worst, error / (double)samples);
                }
                Tests.Check(worst < 3, "cached 3D controls retain shading and lamp detail: " + skin.Key + " (mean RGB error " + worst.ToString("F2") + ")");
            }
        }
    }
}
