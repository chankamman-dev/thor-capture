using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ThorCapture
{
    // Touch feedback: panel discovery, screen mapping, multi-touch decoding, history and drawing.
    internal static class TouchTests
    {
        // A gamepad plus two portrait/landscape panels, as `getevent -lp` prints them.
        internal const string Fixture =
            "add device 1: /dev/input/event7\n  name: \"Test controller\"\n  events:\n    KEY (0001): BTN_A BTN_B BTN_X BTN_Y\n" +
            "    ABS (0003): ABS_X : value 32768, min 0, max 65535, fuzz 0, flat 1024\n                ABS_Y : value 32768, min 0, max 65535, fuzz 0, flat 1024\n" +
            "add device 2: /dev/input/event2\n  name: \"upper_ts\"\n  events:\n    ABS (0003): ABS_MT_SLOT : value 0, min 0, max 9, fuzz 0, flat 0, resolution 0\n" +
            "                ABS_MT_POSITION_X     : value 0, min 0, max 1079, fuzz 0, flat 0, resolution 0\n" +
            "                ABS_MT_POSITION_Y     : value 0, min 0, max 1919, fuzz 0, flat 0, resolution 0\n" +
            "                ABS_MT_TRACKING_ID    : value 0, min 0, max 65535, fuzz 0, flat 0, resolution 0\n" +
            "add device 3: /dev/input/event3\n  name: \"lower_ts\"\n  events:\n" +
            "    ABS (0003): ABS_MT_POSITION_X     : value 0, min 0, max 1239, fuzz 0, flat 0, resolution 0\n" +
            "                ABS_MT_POSITION_Y     : value 0, min 0, max 1079, fuzz 0, flat 0, resolution 0\n";

        private static string Ev(double t, int type, int code, int value)
        {
            return string.Format("[ {0,10:F6}] {1:x4} {2:x4} {3:x8}", t, type, code, unchecked((uint)value));
        }

        private static void Feed(TouchDecoder decoder, params string[] lines) { foreach (string line in lines) decoder.Feed(line); }

        private static List<TouchPoint> Points(TouchDecoder decoder) { List<TouchPoint> p = new List<TouchPoint>(); decoder.Collect(p); return p; }

        internal static void Run()
        {
            List<TouchDevice> panels = TouchDevice.Parse(Fixture);
            Tests.Check(panels.Count == 2 && panels[0].Path == "/dev/input/event2" && panels[0].MaxY == 1919 && panels[1].MaxX == 1239, "touch panels are found with their coordinate ranges");
            Tests.Check(InputDevice.Parse(Fixture).Count == 1, "touch panels are not mistaken for the gamepad");

            List<DeviceDisplay> displays = new List<DeviceDisplay> {
                new DeviceDisplay { LogicalId = 0, Width = 1920, Height = 1080 },
                new DeviceDisplay { LogicalId = 4, Width = 1240, Height = 1080, IsLower = true } };
            List<TouchMap> maps = TouchMap.Guess(panels, displays);
            Tests.Check(maps[0].Screen == 0 && maps[0].Rotation == 90 && maps[1].Screen == 1 && maps[1].Rotation == 0, "panels map to screens by shape; a portrait panel is treated as rotated");
            PointF corner = maps[0].Map(panels[0], 0, 1919), centre = maps[1].Map(panels[1], 620, 540);
            Tests.Check(Math.Abs(corner.X - 1) < 1e-3 && Math.Abs(corner.Y - 1) < 1e-3 && Math.Abs(centre.X - 0.5f) < 0.01f && Math.Abs(centre.Y - 0.5f) < 0.01f, "raw panel coordinates land on the right point of the screen image");
            TouchMap saved = TouchMap.Parse(new TouchMap { Screen = 1, Rotation = 270, Mirror = true }.ToString());
            Tests.Check(saved != null && saved.Screen == 1 && saved.Rotation == 270 && saved.Mirror && TouchMap.Parse("2,45,0") == null, "calibration round-trips and rejects invalid values");

            // Protocol B: two fingers, one moves, one lifts.
            TouchDecoder b = new TouchDecoder(panels[1], maps[1], 1);
            Feed(b, Ev(1, 3, 0x2f, 0), Ev(1, 3, 0x39, 7), Ev(1, 3, 0x35, 100), Ev(1, 3, 0x36, 200),
                    Ev(1, 3, 0x2f, 1), Ev(1, 3, 0x39, 8), Ev(1, 3, 0x35, 1000), Ev(1, 3, 0x36, 900));
            bool frame = b.Feed(Ev(1, 0, 0, 0));
            List<TouchPoint> two = Points(b);
            Feed(b, Ev(1.1, 3, 0x2f, 0), Ev(1.1, 3, 0x35, 150), Ev(1.1, 3, 0x2f, 1), Ev(1.1, 3, 0x39, -1), Ev(1.1, 0, 0, 0));
            List<TouchPoint> one = Points(b);
            Tests.Check(frame && two.Count == 2 && one.Count == 1 && one[0].Screen == 1 && Math.Abs(one[0].X - 150f / 1239) < 1e-3, "multi-touch slots track moves and lifts");
            b.Feed(Ev(1.2, 0, 3, 0));
            Tests.Check(Points(b).Count == 0, "dropped events clear touches instead of leaving a stuck finger");

            // Protocol A: contacts separated by SYN_MT_REPORT.
            TouchDecoder a = new TouchDecoder(panels[1], maps[1], 1);
            Feed(a, Ev(1, 3, 0x35, 10), Ev(1, 3, 0x36, 20), Ev(1, 0, 2, 0), Ev(1, 3, 0x35, 30), Ev(1, 3, 0x36, 40), Ev(1, 0, 2, 0), Ev(1, 0, 0, 0));
            List<TouchPoint> legacy = Points(a);
            Feed(a, Ev(1.1, 0, 2, 0), Ev(1.1, 0, 0, 0));
            Tests.Check(legacy.Count == 2 && Points(a).Count == 0, "protocol A contacts appear and clear");

            // History: tap -> dot -> ripple -> nothing; drag leaves a trail; long press grows a ring.
            TouchHistory history = new TouchHistory(0);
            TouchPoint down = new TouchPoint(0, 1, 0.5f, 0.5f);
            history.Add(0, new TouchPoint[] { down });
            history.Add(100, new TouchPoint[0]);
            TouchVisual pressed = history.At(50), rippled = history.At(200), gone = history.At(100 + TouchHistory.RippleMs + 50);
            Tests.Check(pressed.Marks.Count == 1 && pressed.Marks[0].Down && Math.Abs(pressed.Marks[0].Age - 50) < 1e-6
                && rippled.Marks.Count == 1 && !rippled.Marks[0].Down && Math.Abs(rippled.Marks[0].Age - 100) < 1e-6 && gone.IsEmpty,
                "a tap shows a dot while held, then a ripple that fades out");
            TouchHistory drag = new TouchHistory(0);
            for (int i = 0; i <= 10; i++) drag.Add(i * 16, new TouchPoint[] { new TouchPoint(1, 2, 0.1f + i * 0.05f, 0.5f) });
            TouchVisual moving = drag.At(160);
            Tests.Check(moving.Marks.Count == 1 && moving.Marks[0].Trail.Length > 5 && moving.Marks[0].Trail[0].X < moving.Marks[0].X, "a drag carries a trail behind the finger");
            TouchHistory hold = new TouchHistory(0);
            hold.Add(0, new TouchPoint[] { down });
            Tests.Check(hold.At(900).Marks[0].Age > TouchHistory.LongPressMs, "holding still reports a long press");
            TouchHistory bounded = new TouchHistory(1500);
            for (int i = 0; i < 1000; i++) bounded.Add(i * 16, new TouchPoint[] { new TouchPoint(0, 1, (i % 50) / 50f, 0.5f) });
            Tests.Check(bounded.Count < 120, "the live history keeps only recent frames");

            // Timeline round trip alongside gamepad records.
            string path = Path.Combine(Tests.Root, "touch-" + Guid.NewGuid().ToString("N") + ".input.csv");
            File.WriteAllLines(path, new string[] {
                "#thor-input-v1",
                PadTimeline.Format(0, 1, new PadState()),
                TouchHistory.Format(10, new TouchPoint[] { down, new TouchPoint(1, 5, 0.25f, 0.75f) }),
                TouchHistory.Format(90, new TouchPoint[0]),
                PadTimeline.Format(100, 1.1, new PadState { Buttons = PadButton.A }) });
            PadTimeline loaded = PadTimeline.Load(path);
            TouchVisual replay = loaded.TouchesAt(50);
            Tests.Check(loaded.Times.Count == 2 && replay.Marks.Count == 2 && replay.On(0) && replay.On(1) && loaded.At(120).Down(PadButton.A), "touch records load alongside button records");
            File.WriteAllLines(path, new string[] { PadTimeline.Format(0, 1, new PadState()), "T,5,3:1:0.5:0.5" });
            Tests.Reject(() => PadTimeline.Load(path), "a touch record for an unknown screen is rejected");

            Drawing(pressed, rippled);
            Live();
        }

        private sealed class Stage : PreviewBox
        {
            public void Render(Graphics g) { OnPaint(new PaintEventArgs(g, ClientRectangle)); }
        }

        private static void Drawing(TouchVisual pressed, TouchVisual rippled)
        {
            bool motion = Motion.Enabled;
            Motion.Enabled = false;
            try
            {
                using (Stage stage = new Stage { Size = new Size(760, 940) })
                using (Bitmap canvas = new Bitmap(760, 940))
                using (Graphics g = Graphics.FromImage(canvas))
                {
                    Bitmap black = new Bitmap(192, 108);
                    using (Graphics b = Graphics.FromImage(black)) b.Clear(Color.Black);
                    stage.SetImages(black, null, false);
                    float unit;
                    RectangleF top = TouchFx.ScreenRect(stage.DeviceBounds(), 0, out unit);
                    Point centre = Point.Round(new PointF(top.X + top.Width / 2, top.Y + top.Height / 2));
                    Func<int> brightest = delegate
                    {
                        // The mark is a clear lens with a bright rim, so look for the rim around the centre.
                        int best = 0, reach = (int)(20 * unit);
                        for (int y = centre.Y - reach; y <= centre.Y + reach; y++)
                            for (int x = centre.X - reach; x <= centre.X + reach; x++) best = Math.Max(best, canvas.GetPixel(x, y).R);
                        return best;
                    };
                    stage.Render(g);
                    int before = brightest();
                    stage.SetTouches(pressed);
                    stage.Render(g);
                    int dot = brightest();
                    stage.SetTouches(null);
                    stage.Render(g);
                    int after = brightest();
                    Tests.Check(before < 20 && dot > 90 && after < 20, "the preview draws a touch dot on the right screen and clears it");

                    stage.CreateControl();
                    List<Rectangle> dirty = new List<Rectangle>();
                    stage.Invalidated += delegate(object sender, InvalidateEventArgs e) { dirty.Add(e.InvalidRect); };
                    stage.SetTouches(rippled);
                    Rectangle screen = ShellGeometry.ToClient(stage.DeviceBounds(), DeviceArt.TopScreen);
                    Tests.Check(dirty.Count > 0 && dirty.TrueForAll(r => screen.Contains(r)), "touch feedback repaints only the touched screen");
                }
            }
            finally { Motion.Enabled = motion; }
        }

        // Real InputMonitor against the fake adb: the upper panel reports a tap.
        private static void Live()
        {
            Tests.FakeMode("touch");
            try
            {
                List<TouchDevice> panels;
                List<InputDevice> pads = InputDevice.Discover("test", out panels);
                List<DeviceDisplay> displays = new List<DeviceDisplay> {
                    new DeviceDisplay { LogicalId = 0, Width = 1920, Height = 1080 },
                    new DeviceDisplay { LogicalId = 4, Width = 1240, Height = 1080, IsLower = true } };
                List<TouchMap> maps = TouchMap.Guess(panels, displays);
                using (InputMonitor monitor = new InputMonitor(pads[0], new InputProfile(pads[0])))
                {
                    monitor.Start("test", pads[0].Path);
                    monitor.StartTouch("test", new List<TouchDecoder> { new TouchDecoder(panels[0], maps[0], 0), new TouchDecoder(panels[1], maps[1], 1) });
                    Stopwatch clock = Stopwatch.StartNew();
                    TouchVisual seen = TouchVisual.Empty;
                    while (clock.ElapsedMilliseconds < 5000 && !seen.On(0)) { Thread.Sleep(20); seen = monitor.Touches; }
                    Tests.Check(seen.On(0) && !seen.On(1) && monitor.TouchPanels == 2, "live touch events reach the feedback for the upper screen");
                }
            }
            finally { Tests.FakeMode("normal"); }
        }

        internal static int FakeAdb(string command, string mode, string dir)
        {
            if (mode != "touch" || !command.Contains("getevent")) return Tests.NotHandled;
            if (command.Contains("-lp")) { Console.WriteLine(Fixture); return 0; }
            if (command.Contains("/dev/input/event2"))
            {
                Console.WriteLine(Ev(5, 3, 0x2f, 0) + "\n" + Ev(5, 3, 0x39, 3) + "\n" + Ev(5, 3, 0x35, 540) + "\n" + Ev(5, 3, 0x36, 960) + "\n" + Ev(5, 0, 0, 0));
                Console.Out.Flush();
            }
            Thread.Sleep(10000);
            return 0;
        }
    }
}
