// Touch feedback: reads the touchscreens' multi-touch events alongside the gamepad,
// maps raw panel coordinates onto the upper / lower screen images, keeps a short history
// and turns it into visual marks (a pressed dot, a drag trail, a release ripple, a
// long-press ring). The same history code drives live preview, playback and export, so
// what is recorded is exactly what is drawn. Recordings keep touch samples in the input
// timeline; the original videos are never altered.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ThorCapture
{
    internal sealed class TouchDevice
    {
        public string Path, Name;
        public int MinX, MaxX, MinY, MaxY;
        public override string ToString() { return Name + " · " + Path; }

        // Multi-touch screens from `getevent -lp`: any node reporting ABS_MT_POSITION_X/Y.
        public static List<TouchDevice> Parse(string text)
        {
            List<TouchDevice> result = new List<TouchDevice>();
            TouchDevice current = null;
            bool hasX = false, hasY = false;
            Action close = delegate { if (current != null && hasX && hasY && current.MaxX > current.MinX && current.MaxY > current.MinY) result.Add(current); };
            foreach (string line in text.Split('\n'))
            {
                Match device = Regex.Match(line, @"add device \d+:\s*(/dev/input/event\d+)");
                if (device.Success)
                {
                    close();
                    current = new TouchDevice { Path = device.Groups[1].Value, Name = device.Groups[1].Value };
                    hasX = hasY = false;
                    continue;
                }
                if (current == null) continue;
                Match name = Regex.Match(line, "name:\\s*\"([^\"]+)\"");
                if (name.Success) current.Name = name.Groups[1].Value;
                Match axis = Regex.Match(line, @"(ABS_MT_POSITION_[XY])\s*:\s*value\s+-?\d+,\s*min\s+(-?\d+),\s*max\s+(-?\d+)");
                if (!axis.Success) continue;
                int min = int.Parse(axis.Groups[2].Value, CultureInfo.InvariantCulture), max = int.Parse(axis.Groups[3].Value, CultureInfo.InvariantCulture);
                if (axis.Groups[1].Value.EndsWith("X")) { current.MinX = min; current.MaxX = max; hasX = true; }
                else { current.MinY = min; current.MaxY = max; hasY = true; }
            }
            close();
            return result;
        }
    }

    // Which screen a touch panel belongs to and how its axes turn onto the screen image.
    // Rotation is clockwise degrees from panel axes to the screen image as captured.
    internal sealed class TouchMap
    {
        public int Screen, Rotation;
        public bool Mirror;

        public PointF Map(TouchDevice device, int x, int y)
        {
            float u = Clamp((x - device.MinX) / (float)Math.Max(1, device.MaxX - device.MinX));
            float v = Clamp((y - device.MinY) / (float)Math.Max(1, device.MaxY - device.MinY));
            float sx, sy;
            switch (Rotation)
            {
                case 90: sx = v; sy = 1 - u; break;
                case 180: sx = 1 - u; sy = 1 - v; break;
                case 270: sx = 1 - v; sy = u; break;
                default: sx = u; sy = v; break;
            }
            if (Mirror) sx = 1 - sx;
            return new PointF(sx, sy);
        }

        private static float Clamp(float value) { return Math.Max(0, Math.Min(1, value)); }

        public override string ToString()
        {
            return Screen.ToString(CultureInfo.InvariantCulture) + "," + Rotation.ToString(CultureInfo.InvariantCulture) + "," + (Mirror ? "1" : "0");
        }

        public static TouchMap Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string[] parts = text.Split(',');
            int screen, rotation;
            if (parts.Length != 3 || !int.TryParse(parts[0], out screen) || !int.TryParse(parts[1], out rotation)) return null;
            if ((screen != 0 && screen != 1) || rotation % 90 != 0 || rotation < 0 || rotation > 270) return null;
            return new TouchMap { Screen = screen, Rotation = rotation, Mirror = parts[2] == "1" };
        }

        private static float Aspect(float a, float b) { return Math.Max(a, b) / Math.Max(1, Math.Min(a, b)); }

        // Best guess before any calibration: the two Thor screens have clearly different
        // shapes (16:9 and about 8:7), so each panel goes to the display whose aspect it
        // matches; a panel whose long side disagrees with the image is assumed rotated 90.
        public static List<TouchMap> Guess(List<TouchDevice> panels, List<DeviceDisplay> displays)
        {
            List<TouchMap> maps = new List<TouchMap>();
            List<int> free = new List<int>();
            for (int i = 0; i < displays.Count; i++) free.Add(i);
            foreach (TouchDevice panel in panels)
            {
                float aspect = Aspect(panel.MaxX - panel.MinX, panel.MaxY - panel.MinY);
                int best = -1; float bestCost = float.MaxValue;
                foreach (int i in free)
                {
                    float cost = Math.Abs(Aspect(displays[i].Width, displays[i].Height) - aspect);
                    if (cost < bestCost) { bestCost = cost; best = i; }
                }
                if (best < 0) { maps.Add(null); continue; }
                free.Remove(best);
                DeviceDisplay display = displays[best];
                bool panelWide = panel.MaxX - panel.MinX >= panel.MaxY - panel.MinY, imageWide = display.Width >= display.Height;
                maps.Add(new TouchMap { Screen = display.IsLower ? 1 : 0, Rotation = panelWide == imageWide ? 0 : 90 });
            }
            return maps;
        }
    }

    // One finger on one screen, in screen-image coordinates (0..1).
    internal struct TouchPoint
    {
        public int Screen, Id;
        public float X, Y;
        public TouchPoint(int screen, int id, float x, float y) { Screen = screen; Id = id; X = x; Y = y; }
    }

    // Linux multi-touch decoder for one panel (protocol B slots, with protocol A fallback).
    internal sealed class TouchDecoder
    {
        private sealed class Slot { public int Id = -1, X, Y; }
        private readonly Dictionary<int, Slot> slots = new Dictionary<int, Slot>();
        private readonly List<Slot> reports = new List<Slot>();
        private readonly TouchDevice device;
        private readonly TouchMap map;
        private readonly int idBase;
        private Slot pending = new Slot();
        private int slot;
        private bool protocolA, pendingHas;
        public bool Lost;
        public double DeviceSeconds;

        public TouchDecoder(TouchDevice device, TouchMap map, int panelIndex)
        {
            this.device = device;
            this.map = map;
            idBase = (panelIndex + 1) * 100000;
        }

        public TouchDevice Device { get { return device; } }
        public TouchMap Map { get { return map; } }

        private Slot Current
        {
            get
            {
                Slot value;
                if (!slots.TryGetValue(slot, out value)) { value = new Slot(); slots[slot] = value; }
                return value;
            }
        }

        // Returns true when a complete frame (SYN_REPORT) has been applied.
        public bool Feed(string line)
        {
            Match match = Regex.Match(line, @"\[\s*([0-9]+\.[0-9]+)\]\s*(?:/dev/input/event\d+:\s*)?([0-9a-fA-F]{4})\s+([0-9a-fA-F]{4})\s+([0-9a-fA-F]{8})\s*$");
            if (!match.Success) return false;
            DeviceSeconds = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int type = Convert.ToInt32(match.Groups[2].Value, 16), code = Convert.ToInt32(match.Groups[3].Value, 16);
            int value = unchecked((int)uint.Parse(match.Groups[4].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            if (type == 0 && code == 3) { slots.Clear(); reports.Clear(); Lost = true; return true; } // SYN_DROPPED
            if (type == 0 && code == 2) // SYN_MT_REPORT ends one protocol A contact
            {
                if (!protocolA)
                {
                    // The first contact arrived before we knew this was protocol A.
                    protocolA = true;
                    Slot early;
                    if (!pendingHas && slots.TryGetValue(slot, out early)) { pending = new Slot { Id = -1, X = early.X, Y = early.Y }; pendingHas = true; }
                    slots.Clear();
                }
                if (pendingHas) reports.Add(pending); // a lone SYN_MT_REPORT means no contacts
                pending = new Slot();
                pendingHas = false;
                return false;
            }
            if (type == 0 && code == 0)
            {
                if (protocolA)
                {
                    slots.Clear();
                    for (int i = 0; i < reports.Count; i++) { reports[i].Id = reports[i].Id < 0 ? i : reports[i].Id; slots[i] = reports[i]; }
                    reports.Clear();
                }
                return true;
            }
            if (type == 1 && code == 0x14a && value == 0) { slots.Clear(); return false; } // BTN_TOUCH up: nothing on the glass
            if (type != 3) return false;
            Slot target = protocolA ? pending : Current;
            switch (code)
            {
                case 0x2f: slot = value; break;                                    // ABS_MT_SLOT
                case 0x39: if (protocolA) pending.Id = value; else Current.Id = value; break; // ABS_MT_TRACKING_ID, -1 lifts
                case 0x35: target.X = value; if (protocolA) pendingHas = true; else if (target.Id == -1) target.Id = slot; break;
                case 0x36: target.Y = value; if (protocolA) pendingHas = true; else if (target.Id == -1) target.Id = slot; break;
            }
            return false;
        }

        public void Collect(List<TouchPoint> into)
        {
            if (map == null) return;
            foreach (KeyValuePair<int, Slot> pair in slots)
            {
                if (pair.Value.Id < 0) continue;
                PointF p = map.Map(device, pair.Value.X, pair.Value.Y);
                into.Add(new TouchPoint(map.Screen, idBase + (pair.Value.Id & 0xffff), p.X, p.Y));
            }
        }
    }

    // What to draw at one moment.
    internal sealed class TouchMark
    {
        public int Screen;
        public float X, Y;
        public bool Down;          // finger on the glass; otherwise a release ripple
        public double Age;         // ms since press (down) or since release (ripple)
        public PointF[] Trail;     // recent positions, oldest first (down only)
    }

    internal sealed class TouchVisual
    {
        public static readonly TouchVisual Empty = new TouchVisual();
        public readonly List<TouchMark> Marks = new List<TouchMark>();
        public bool IsEmpty { get { return Marks.Count == 0; } }
        public bool On(int screen) { foreach (TouchMark mark in Marks) if (mark.Screen == screen) return true; return false; }
    }

    // Time-ordered touch frames. Bounded histories back the live view; a complete one is
    // loaded from a recording's timeline for playback and export.
    internal sealed class TouchHistory
    {
        public const double RippleMs = 480, TrailMs = 220, LongPressMs = 480;
        private readonly List<double> times = new List<double>();
        private readonly List<TouchPoint[]> frames = new List<TouchPoint[]>();
        private readonly double keep;

        public TouchHistory(double keepMs) { keep = keepMs; }
        public int Count { get { return times.Count; } }
        public bool HasTouches { get { foreach (TouchPoint[] frame in frames) if (frame.Length > 0) return true; return false; } }

        public void Add(double time, TouchPoint[] points)
        {
            if (times.Count > 0)
            {
                if (time < times[times.Count - 1]) time = times[times.Count - 1];
                if (Same(frames[frames.Count - 1], points)) return;
            }
            times.Add(time);
            frames.Add(points);
            if (keep > 0)
            {
                int drop = 0;
                while (drop < times.Count - 1 && times[drop + 1] < time - keep) drop++;
                if (drop > 0) { times.RemoveRange(0, drop); frames.RemoveRange(0, drop); }
            }
        }

        private static bool Same(TouchPoint[] a, TouchPoint[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].Screen != b[i].Screen || a[i].Id != b[i].Id || a[i].X != b[i].X || a[i].Y != b[i].Y) return false;
            return true;
        }

        private static int Find(TouchPoint[] frame, int screen, int id)
        {
            for (int i = 0; i < frame.Length; i++) if (frame[i].Screen == screen && frame[i].Id == id) return i;
            return -1;
        }

        private int IndexAt(double time)
        {
            int index = times.BinarySearch(time);
            return index < 0 ? ~index - 1 : index;
        }

        public TouchVisual At(double time)
        {
            int now = IndexAt(time);
            if (now < 0) return TouchVisual.Empty;
            TouchVisual visual = new TouchVisual();
            TouchPoint[] current = frames[now];
            foreach (TouchPoint point in current)
            {
                // Walk back to the press; collect recent positions for the trail.
                int start = now;
                List<PointF> trail = new List<PointF>();
                for (int k = now; k >= 0; k--)
                {
                    int at = Find(frames[k], point.Screen, point.Id);
                    if (at < 0) break;
                    start = k;
                    if (time - times[k] <= TrailMs) trail.Insert(0, new PointF(frames[k][at].X, frames[k][at].Y));
                    if (time - times[k] > LongPressMs + 2000) break;
                }
                visual.Marks.Add(new TouchMark { Screen = point.Screen, X = point.X, Y = point.Y, Down = true, Age = time - times[start], Trail = trail.ToArray() });
            }
            // Releases in the ripple window: present in frame k, gone in frame k + 1.
            for (int k = now - 1; k >= 0 && time - times[k + 1] <= RippleMs; k--)
                foreach (TouchPoint point in frames[k])
                    if (Find(frames[k + 1], point.Screen, point.Id) < 0)
                        visual.Marks.Add(new TouchMark { Screen = point.Screen, X = point.X, Y = point.Y, Down = false, Age = time - times[k + 1] });
            return visual;
        }

        // Timeline record: T,<ms>,<screen>:<id>:<x>:<y>|...
        public static string Format(double milliseconds, TouchPoint[] points)
        {
            StringBuilder text = new StringBuilder("T,");
            text.Append(milliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append(',');
            for (int i = 0; i < points.Length; i++)
            {
                if (i > 0) text.Append('|');
                text.Append(points[i].Screen).Append(':').Append(points[i].Id).Append(':')
                    .Append(points[i].X.ToString("0.#####", CultureInfo.InvariantCulture)).Append(':')
                    .Append(points[i].Y.ToString("0.#####", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        public static void ParseRecord(string line, out double milliseconds, out TouchPoint[] points)
        {
            string[] parts = line.Split(new char[] { ',' }, 3);
            if (parts.Length != 3 || parts[0] != "T") throw new System.IO.IOException("触摸时间线格式损坏");
            milliseconds = double.Parse(parts[1], CultureInfo.InvariantCulture);
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0) throw new System.IO.IOException("触摸时间无效");
            if (parts[2].Length == 0) { points = new TouchPoint[0]; return; }
            string[] items = parts[2].Split('|');
            if (items.Length > 20) throw new System.IO.IOException("触摸点过多");
            points = new TouchPoint[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                string[] f = items[i].Split(':');
                if (f.Length != 4) throw new System.IO.IOException("触摸点格式损坏");
                float x = float.Parse(f[2], CultureInfo.InvariantCulture), y = float.Parse(f[3], CultureInfo.InvariantCulture);
                int screen = int.Parse(f[0], CultureInfo.InvariantCulture);
                if ((screen != 0 && screen != 1) || !(x >= 0 && x <= 1) || !(y >= 0 && y <= 1)) throw new System.IO.IOException("触摸坐标无效");
                points[i] = new TouchPoint(screen, int.Parse(f[1], CultureInfo.InvariantCulture), x, y);
            }
        }
    }

    // Draws touch marks over one screen image. `unit` is the screen's design-to-pixel scale,
    // so a fingertip has the same physical size on the large and the small screen.
    internal static class TouchFx
    {
        public static void Draw(Graphics g, RectangleF screen, float unit, TouchVisual visual, int index)
        {
            if (visual == null || visual.IsEmpty || screen.Width <= 0) return;
            GraphicsState state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.SetClip(screen, CombineMode.Intersect);
            float radius = 16f * unit; // a fingertip's contact patch, kept small and quiet
            foreach (TouchMark mark in visual.Marks)
            {
                if (mark.Screen != index) continue;
                PointF at = new PointF(screen.X + mark.X * screen.Width, screen.Y + mark.Y * screen.Height);
                if (mark.Down) Press(g, screen, at, radius, mark);
                else Release(g, at, radius, mark.Age);
            }
            g.Restore(state);
        }

        // A soft halo of stacked rings: keeps a pale mark readable on bright game art
        // without a hard outline.
        private static void Halo(Graphics g, PointF at, float r, float strength)
        {
            for (int i = 3; i >= 1; i--)
            {
                float grow = r * 0.09f * i;
                using (SolidBrush shade = new SolidBrush(Color.FromArgb((int)(14 * strength), 0, 0, 0)))
                    g.FillEllipse(shade, at.X - r - grow, at.Y - r - grow + r * 0.06f, (r + grow) * 2, (r + grow) * 2);
            }
        }

        // Glassy contact mark: almost clear in the middle, brighter towards a fine rim.
        private static void Lens(Graphics g, PointF at, float r, float opacity, float glow)
        {
            if (r <= 0.5f || opacity <= 0.01f) return;
            Halo(g, at, r, opacity);
            using (GraphicsPath disc = new GraphicsPath())
            {
                disc.AddEllipse(at.X - r, at.Y - r, r * 2, r * 2);
                using (PathGradientBrush fill = new PathGradientBrush(disc))
                {
                    fill.CenterPoint = new PointF(at.X - r * 0.18f, at.Y - r * 0.22f);
                    fill.CenterColor = Color.FromArgb((int)((50 + 50 * glow) * opacity), 255, 255, 255);
                    fill.SurroundColors = new Color[] { Color.FromArgb((int)((120 + 40 * glow) * opacity), 255, 255, 255) };
                    g.FillPath(fill, disc);
                }
            }
            using (Pen rim = new Pen(Color.FromArgb((int)(210 * opacity), 255, 255, 255), Math.Max(1f, r * 0.07f)))
                g.DrawEllipse(rim, at.X - r, at.Y - r, r * 2, r * 2);
        }

        private static void Press(Graphics g, RectangleF screen, PointF at, float radius, TouchMark mark)
        {
            // Trail: a smooth curve that thins and fades towards its tail.
            if (mark.Trail != null && mark.Trail.Length > 2)
            {
                PointF[] points = new PointF[mark.Trail.Length];
                for (int i = 0; i < points.Length; i++) points[i] = new PointF(screen.X + mark.Trail[i].X * screen.Width, screen.Y + mark.Trail[i].Y * screen.Height);
                for (int i = 1; i < points.Length; i++)
                {
                    float t = i / (float)(points.Length - 1);
                    using (Pen pen = new Pen(Color.FromArgb((int)(80 * t * t), 255, 255, 255), Math.Max(0.8f, radius * 0.5f * t)))
                    {
                        pen.StartCap = pen.EndCap = LineCap.Round;
                        g.DrawCurve(pen, new PointF[] { points[Math.Max(0, i - 2)], points[i - 1], points[i] }, 0.5f);
                    }
                }
            }
            // Landing: the mark settles from slightly larger and clear onto the fingertip.
            float land = Motion.Apply(Ease.OutCubic, (float)Math.Min(1, mark.Age / 180.0));
            float r = radius * (1.25f - 0.25f * land);
            float hold = mark.Age > TouchHistory.LongPressMs ? (float)Math.Min(1, (mark.Age - TouchHistory.LongPressMs) / 650.0) : 0;
            Lens(g, at, r, land, hold);
            // Long press: a fine arc draws around the rim.
            if (hold > 0)
            {
                float ring = r + radius * 0.28f;
                float sweep = 360 * Motion.Apply(Ease.InOutCubic, hold);
                using (Pen track = new Pen(Color.FromArgb(40, 0, 0, 0), Math.Max(1f, radius * 0.09f)))
                    g.DrawEllipse(track, at.X - ring, at.Y - ring, ring * 2, ring * 2);
                using (Pen arc = new Pen(Color.FromArgb((int)(150 + 90 * hold), 255, 255, 255), Math.Max(1f, radius * 0.09f)))
                {
                    arc.StartCap = arc.EndCap = LineCap.Round;
                    g.DrawArc(arc, at.X - ring, at.Y - ring, ring * 2, ring * 2, -90, Math.Max(1, sweep));
                }
            }
        }

        private static void Release(Graphics g, PointF at, float radius, double age)
        {
            // The mark lifts away (shrinks and fades) while a hairline ring spreads out.
            float lift = (float)Math.Min(1, age / 170.0);
            if (lift < 1) Lens(g, at, radius * (1 - 0.18f * Motion.Apply(Ease.OutCubic, lift)), 1 - lift, 0);
            float t = Motion.Apply(Ease.OutCubic, (float)Math.Min(1, age / TouchHistory.RippleMs));
            float fade = 1 - (float)Math.Min(1, age / TouchHistory.RippleMs);
            float r = radius * (1 + 1.1f * t);
            using (Pen shade = new Pen(Color.FromArgb((int)(35 * fade), 0, 0, 0), Math.Max(1f, radius * 0.1f)))
                g.DrawEllipse(shade, at.X - r, at.Y - r + radius * 0.05f, r * 2, r * 2);
            using (Pen ring = new Pen(Color.FromArgb((int)(190 * fade * fade), 255, 255, 255), Math.Max(0.8f, radius * 0.07f)))
                g.DrawEllipse(ring, at.X - r, at.Y - r, r * 2, r * 2);
        }

        // The exact screen rectangle for a device-art bounds (no hit-test padding).
        public static RectangleF ScreenRect(RectangleF bounds, int index, out float unit)
        {
            unit = ShellGeometry.Scale(bounds);
            PointF origin = ShellGeometry.Origin(bounds);
            RectangleF area = index == 0 ? DeviceArt.TopScreen : DeviceArt.BottomScreen;
            return new RectangleF(origin.X + area.X * unit, origin.Y + area.Y * unit, area.Width * unit, area.Height * unit);
        }
    }
}
