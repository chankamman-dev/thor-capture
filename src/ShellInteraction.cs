using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ThorCapture
{
    // Design-space geometry shared by painting, hit testing and dirty rectangles.
    internal static class ShellGeometry
    {
        internal const int PartCount = 10;
        internal const float VisualHeight = DeviceArt.Height + 40;
        internal static readonly RectangleF[] Areas = {
            new RectangleF(34, 615, 162, 162), new RectangleF(48, 787, 150, 150),
            new RectangleF(683, 604, 176, 176), new RectangleF(685, 779, 162, 162),
            new RectangleF(159, 577, 40, 40), new RectangleF(679, 577, 40, 40),
            new RectangleF(158, 963, 40, 40), new RectangleF(679, 963, 40, 40),
            new RectangleF(56, 767, 776, 220), new RectangleF(294, 1051, 292, 30)
        };
        internal static readonly PadButton[] FaceButtons = { PadButton.X, PadButton.A, PadButton.B, PadButton.Y };
        internal static readonly PadButton[] MiniButtons = { PadButton.Select, PadButton.Start, PadButton.Home, PadButton.Back };
        internal static readonly PadButton[] Shoulders = { PadButton.L1, PadButton.L2, PadButton.R2, PadButton.R1 };
        internal static readonly PointF[] FaceCenters = { new PointF(771, 637.8f), new PointF(824, 690.8f), new PointF(771, 743.8f), new PointF(718, 690.8f) };
        internal static readonly PointF[] MiniCenters = { new PointF(179, 597), new PointF(699, 597), new PointF(178, 983), new PointF(699, 983) };

        internal static float Scale(RectangleF bounds) { return Math.Min(bounds.Width / DeviceArt.Width, bounds.Height / DeviceArt.Height); }
        internal static PointF Origin(RectangleF bounds)
        {
            float scale = Scale(bounds);
            return new PointF(bounds.X + (bounds.Width - DeviceArt.Width * scale) / 2, bounds.Y + (bounds.Height - DeviceArt.Height * scale) / 2);
        }
        internal static PointF ToDesign(RectangleF bounds, PointF point)
        {
            float scale = Scale(bounds);
            PointF origin = Origin(bounds);
            return scale <= 0 ? new PointF(-1000, -1000) : new PointF((point.X - origin.X) / scale, (point.Y - origin.Y) / scale);
        }
        internal static Rectangle ToClient(RectangleF bounds, RectangleF area)
        {
            float scale = Scale(bounds);
            PointF origin = Origin(bounds);
            return Rectangle.FromLTRB((int)Math.Floor(origin.X + area.Left * scale) - 2,
                (int)Math.Floor(origin.Y + area.Top * scale) - 2,
                (int)Math.Ceiling(origin.X + area.Right * scale) + 2,
                (int)Math.Ceiling(origin.Y + area.Bottom * scale) + 2);
        }
        private static bool Near(PointF point, PointF center, float radius)
        {
            float x = point.X - center.X, y = point.Y - center.Y;
            return x * x + y * y <= radius * radius;
        }
        internal static int Hit(PointF point, out PadButton button)
        {
            button = 0;
            if (Near(point, new PointF(115, 696), 76)) return 0;
            if (Near(point, new PointF(766, 860), 76)) return 3;
            using (GraphicsPath cross = DeviceArt.Cross(123, 860.8f, 61, 23, 7))
                if (cross.IsVisible(point)) { button = Direction(point); return 1; }
            for (int i = 0; i < FaceCenters.Length; i++)
                if (Near(point, FaceCenters[i], 28)) { button = FaceButtons[i]; return 2; }
            for (int i = 0; i < MiniCenters.Length; i++)
                if (Near(point, MiniCenters[i], 19)) { button = MiniButtons[i]; return 4 + i; }
            for (int i = 0; i < Shoulders.Length; i++)
                if (new RectangleF(296 + i * 74, 1053, 66, 26).Contains(point)) { button = Shoulders[i]; return 9; }
            return -1;
        }
        internal static PadButton Direction(PointF point)
        {
            float x = point.X - 123, y = point.Y - 860.8f;
            if (Math.Abs(x) > 86 || Math.Abs(y) > 86 || (Math.Abs(x) < 10 && Math.Abs(y) < 10)) return 0;
            PadButton value = 0;
            if (Math.Abs(x) >= Math.Abs(y) * .414f) value |= x < 0 ? PadButton.Left : PadButton.Right;
            if (Math.Abs(y) >= Math.Abs(x) * .414f) value |= y < 0 ? PadButton.Up : PadButton.Down;
            return value;
        }
        internal static string Hint(int part, PadButton button)
        {
            if (part == 0 || part == 3) return "拖动演示摇杆 · 右键按住演示" + (part == 0 ? " L3" : " R3");
            if (part == 1) return "按住并拖动演示十字键，可切换斜向";
            return part < 0 ? "" : "按住演示 " + button + " · 仅外壳动画";
        }
    }

    // Purely local demo input. This class has no device or timeline dependency.
    internal sealed class ShellPointer
    {
        internal PadState State = new PadState();
        internal int Part = -1;
        internal MouseButtons Held;
        private PointF anchor;
        private PadButton button;
        internal bool Active { get { return Part >= 0; } }
        internal bool LeftAxis { get { return Part == 0 && Held == MouseButtons.Left; } }
        internal bool RightAxis { get { return Part == 3 && Held == MouseButtons.Left; } }

        internal bool Begin(PointF point, MouseButtons mouse)
        {
            if (Active) return false;
            PadButton hit;
            int part = ShellGeometry.Hit(point, out hit);
            if (part < 0 || (mouse != MouseButtons.Left && !(mouse == MouseButtons.Right && (part == 0 || part == 3)))) return false;
            Part = part; Held = mouse; anchor = point; button = hit;
            State = new PadState();
            if (mouse == MouseButtons.Right) State.Key(part == 0 ? PadButton.L3 : PadButton.R3, true);
            else if (part != 0 && part != 3) State.Buttons = hit;
            return true;
        }
        internal void Move(PointF point)
        {
            if (!Active) return;
            if (LeftAxis || RightAxis)
            {
                float x = (point.X - anchor.X) / 28, y = (point.Y - anchor.Y) / 28;
                float length = (float)Math.Sqrt(x * x + y * y);
                if (length > 1) { x /= length; y /= length; }
                if (LeftAxis) { State.LX = x; State.LY = y; }
                else { State.RX = x; State.RY = y; }
            }
            else if (Part == 1) State.Buttons = ShellGeometry.Direction(point);
            else if (Held == MouseButtons.Left)
            {
                PadButton hit;
                int part = ShellGeometry.Hit(point, out hit);
                State.Buttons = part == Part && hit == button ? button : 0;
            }
        }
        internal bool End(MouseButtons mouse)
        {
            if (!Active || mouse != Held) return false;
            Reset(); return true;
        }
        internal void Reset() { Part = -1; Held = MouseButtons.None; State = new PadState(); }
        internal PadState Merge(PadState physical)
        {
            PadState result = physical == null ? new PadState() : physical.Copy();
            if (Part == 1) result.Buttons &= ~(PadButton.Up | PadButton.Down | PadButton.Left | PadButton.Right);
            result.Buttons |= State.Buttons;
            if (LeftAxis) { result.LX = State.LX; result.LY = State.LY; }
            if (RightAxis) { result.RX = State.RX; result.RY = State.RY; }
            return result;
        }
    }

    internal static class PadMotion
    {
        private static float Approach(float value, float target, double elapsed, double duration)
        {
            float next = value + (target - value) * (float)(1 - Math.Exp(-Math.Max(0, elapsed) / duration));
            return Math.Abs(next - target) < .001f ? target : next;
        }
        internal static PadState Step(PadState current, PadState target, double elapsed)
        {
            PadState next = target.Copy();
            next.Animated = true;
            next.LX = Approach(current.LX, target.LX, elapsed, .028); next.LY = Approach(current.LY, target.LY, elapsed, .028);
            next.RX = Approach(current.RX, target.RX, elapsed, .028); next.RY = Approach(current.RY, target.RY, elapsed, .028);
            next.LT = Approach(current.LT, target.LT, elapsed, .028); next.RT = Approach(current.RT, target.RT, elapsed, .028);
            next.TiltX = Approach(current.Animated ? current.TiltX : current.DirectionX, target.Animated ? target.TiltX : target.DirectionX, elapsed, .030);
            next.TiltY = Approach(current.Animated ? current.TiltY : current.DirectionY, target.Animated ? target.TiltY : target.DirectionY, elapsed, .030);
            next.Pressures = new float[18];
            for (int i = 0; i < next.Pressures.Length; i++)
            {
                PadButton button = (PadButton)(1 << i);
                float press = target.Press(button);
                next.Pressures[i] = Approach(current.Press(button), press, elapsed, press > current.Press(button) ? .022 : .040);
            }
            return next;
        }
    }
}
