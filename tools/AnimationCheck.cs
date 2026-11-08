using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace ThorCapture
{
    // Repeatable CPU paint measurements, with no ADB connection or frame pacing claims.
    internal static class AnimationCheck
    {
        private sealed class Stage : PreviewBox
        {
            public void Render(Graphics graphics) { base.OnPaint(new PaintEventArgs(graphics, ClientRectangle)); }
        }

        private static readonly StringBuilder report = new StringBuilder();

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                report.AppendLine("Offscreen CPU painting only; not device FPS, frame pacing or end-to-end latency.");
                using (Bitmap bitmap = new Bitmap(900, 1100))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    Measure("full-controls / both sticks", delegate(int i)
                    {
                        DeviceArt.DrawControls(graphics, new RectangleF(0, 0, 900, 1080), DeviceSkin.All[2], Pose(i, true));
                    });
                    Measure("lamp bowl only", delegate(int i)
                    {
                        double x, y, r;
                        Stick3D.Footprint((float)Math.Sin(i * .08), (float)Math.Cos(i * .08), false, out x, out y, out r);
                        LampLight.Bowl(graphics, DeviceSkin.All[2], 100, 100, x, y, r);
                    });
                    Measure("stick cap only", delegate(int i)
                    {
                        Stick3D.Draw(graphics, DeviceSkin.All[2], 100, 100, (float)Math.Sin(i * .08), (float)Math.Cos(i * .08), false);
                    });
                }
                foreach (Size size in new Size[] { new Size(760, 940), new Size(1200, 1440) })
                using (Stage stage = new Stage { Size = size, Skin = DeviceSkin.All[2] })
                using (Bitmap bitmap = new Bitmap(size.Width, size.Height))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    MethodInfo advance = typeof(PreviewBox).GetMethod("AdvanceAnimation", BindingFlags.Instance | BindingFlags.NonPublic);
                    foreach (bool sticks in new bool[] { false, true })
                    {
                        Measure("preview " + size.Width + "x" + size.Height + " / " + (sticks ? "both sticks" : "D-pad only"), delegate(int i)
                        {
                            stage.SetInput(Pose(i, sticks));
                            if (advance != null) advance.Invoke(stage, new object[] { 1.0 / 60 });
                            stage.Render(graphics);
                        });
                    }
                }
                string path = args.Length == 0 ? "animation-timing.txt" : args[0];
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllText(path, report.ToString(), Encoding.UTF8);
                Console.Write(report.ToString());
                return 0;
            }
            catch (Exception failure) { Console.Error.WriteLine(failure); return 1; }
        }

        private static PadState Pose(int i, bool sticks)
        {
            float a = i * .08f;
            return new PadState { Buttons = (i / 20) % 2 == 0 ? PadButton.Up : PadButton.Right,
                Animated = true, TiltX = (float)Math.Sin(a), TiltY = (float)Math.Cos(a),
                LX = sticks ? (float)Math.Sin(a) : 0, LY = sticks ? (float)Math.Cos(a) : 0,
                RX = sticks ? -(float)Math.Sin(a) : 0, RY = sticks ? -(float)Math.Cos(a) : 0 };
        }

        private static void Measure(string name, Action<int> draw)
        {
            for (int i = 0; i < 8; i++) draw(i);
            double[] times = new double[90];
            double total = 0;
            Stopwatch watch = new Stopwatch();
            for (int i = 0; i < times.Length; i++)
            {
                watch.Restart(); draw(i); watch.Stop();
                total += times[i] = watch.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times);
            report.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}: mean {1:F2} ms, median {2:F2} ms, P95 {3:F2} ms", name, total / times.Length, times[45], times[85]));
        }
    }
}
