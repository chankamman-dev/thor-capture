using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;
using ThorCapture.Media;

namespace ThorCapture.Features.VideoExport
{
    internal static partial class VideoExporter
    {
        static Process ProcessFor(string exe, List<string> args, bool input)
        { ProcessStartInfo info = new ProcessRequest(exe, "video.framed", args.ToArray()).StartInfo(); info.RedirectStandardInput = input; return new Process { StartInfo = info }; }
        static string Drain(TextReader reader)
        { char[] buffer = new char[4096]; int count, total = 0; while ((count = reader.Read(buffer, 0, buffer.Length)) > 0) total = Math.Min(262144, total + count); return total.ToString(); }
        static void KillOwned(Process process)
        { if (process == null) return; try { if (!process.HasExited) process.Kill(); } catch { } }
        static void Copy(byte[] pixels, int rowWidth, int x, Bitmap bitmap)
        {
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(pixels, (y * rowWidth + x) * 4, IntPtr.Add(data.Scan0, y * data.Stride), bitmap.Width * 4); }
            finally { bitmap.UnlockBits(data); }
        }
        static void Framed(ExportPlan plan, PreparedExport work, ExportTools tools, string output, IProgress<int> progress, CancellationToken token)
        {
            string rendered = plan.Format == VideoFormat.Gif ? output + ".rendered.mp4" : output;
            bool intermediate = false;
            try
            {
                if (plan.Format == VideoFormat.Gif) { using (FileStream reserve = File.Open(rendered, FileMode.CreateNew, FileAccess.Write, FileShare.None)) intermediate = true; }
                RenderFrames(plan, work, tools, rendered, progress, token);
                if (plan.Format == VideoFormat.Gif)
                {
                    ExportPlan gif = new ExportPlan(new[] { new ExportTrack(rendered, ScreenRole.Upper, TimeSpan.Zero) }, TimeSpan.Zero, TimeSpan.FromSeconds(work.Duration), VideoLayout.Single, VideoFormat.Gif, plan.OutputSize, plan.Fps, plan.BitRate, plan.Skin, paletteColors: plan.PaletteColors);
                    PreparedExport gifWork = new PreparedExport { Tracks = gif.Tracks.ToArray(), Duration = work.Duration, Frames = work.Frames };
                    string palette = rendered + ".palette.png";
                    bool ownPalette = false;
                    try { using (FileStream reserve = File.Open(palette, FileMode.CreateNew, FileAccess.Write, FileShare.None)) ownPalette = true; Flat(gif, gifWork, tools, output, palette, token); } finally { if (ownPalette) try { File.Delete(palette); } catch { } }
                }
            }
            finally { if (intermediate) { try { File.Delete(rendered); } catch { } } }
        }
        static void RenderFrames(ExportPlan plan, PreparedExport work, ExportTools tools, string output, IProgress<int> progress, CancellationToken token)
        {
            Size[] sizes = work.Info.Select(info => {
                double shrink = Math.Min(1, Math.Min(plan.OutputSize.Width * 850.0 / 900 / info.Width, plan.OutputSize.Height * 600.0 / 1120 / info.Height));
                return new Size(Math.Max(2, (int)(info.Width * shrink) / 2 * 2), Math.Max(2, (int)(info.Height * shrink) / 2 * 2));
            }).ToArray();
            int packedWidth = sizes.Sum(s => s.Width), packedHeight = sizes.Max(s => s.Height);
            List<string> decode = Inputs(tools, work); List<string> filters = new List<string>();
            for (int i = 0; i < sizes.Length; i++) filters.Add(TrackFilter(plan, work, i, sizes[i].Width, sizes[i].Height, "track" + i, plan.Start.TotalSeconds, work.Duration) + ";[track" + i + "]pad=" + sizes[i].Width + ":" + packedHeight + ":0:0:color=black[packed" + i + "]");
            string final = sizes.Length == 1 ? "[packed0]null[video]" : "[packed0][packed1]hstack=inputs=2:shortest=1[video]";
            decode.AddRange(new[] { "-filter_complex", String.Join(";", filters) + ";" + final, "-map", "[video]", "-an", "-frames:v", work.Frames.ToString(), "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1" });
            List<string> encode = new List<string> { "-v", "error", "-threads", "1", "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", plan.OutputSize.Width + "x" + plan.OutputSize.Height, "-framerate", plan.Fps.ToString(), "-i", "pipe:0" };
            bool audio = work.AudioIndex >= 0 && plan.Format != VideoFormat.Gif;
            if (audio) encode.AddRange(new[] { "-i", work.Tracks[work.AudioIndex].Path, "-map", "0:v:0", "-map", "1:a:0", "-af", AudioFilter(plan, work, 1, plan.Start.TotalSeconds, work.Duration, false) }); else encode.AddRange(new[] { "-map", "0:v:0", "-an" });
            EncodingArgs(plan, encode, audio); encode.AddRange(new[] { "-t", ExportTools.Number(work.Duration), "-frames:v", work.Frames.ToString(), "-y", output });
            PadTimeline timeline = plan.TimelinePath == null ? null : PadTimeline.Load(plan.TimelinePath);
            using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (Process decoder = ProcessFor(tools.Ffmpeg, decode, false))
            using (Process encoder = ProcessFor(tools.Ffmpeg, encode, true))
            {
                deadline.CancelAfter(TimeSpan.FromMinutes(30)); Task<string> de = null, ee = null, eo = null;
                try
                {
                    decoder.Start(); encoder.Start(); de = Task.Run(() => Drain(decoder.StandardError)); ee = Task.Run(() => Drain(encoder.StandardError)); eo = Task.Run(() => Drain(encoder.StandardOutput));
                    using (deadline.Token.Register(() => { KillOwned(decoder); KillOwned(encoder); }))
                    using (Bitmap first = new Bitmap(sizes[0].Width, sizes[0].Height, PixelFormat.Format32bppArgb))
                    using (Bitmap second = sizes.Length == 1 ? null : new Bitmap(sizes[1].Width, sizes[1].Height, PixelFormat.Format32bppArgb))
                    using (Bitmap frame = new Bitmap(plan.OutputSize.Width, plan.OutputSize.Height, PixelFormat.Format32bppArgb))
                    using (Graphics graphics = Graphics.FromImage(frame))
                    {
                        byte[] pixels = new byte[packedWidth * packedHeight * 4], bytes = new byte[plan.OutputSize.Width * plan.OutputSize.Height * 4]; int frames = 0;
                        PadState pose = new PadState(); DeviceSkin skin = plan.Skin;
                        while (frames < work.Frames)
                        {
                            deadline.Token.ThrowIfCancellationRequested(); int read = 0;
                            while (read < pixels.Length) { int count = decoder.StandardOutput.BaseStream.Read(pixels, read, pixels.Length - read); if (count == 0) break; read += count; }
                            if (read != pixels.Length) throw new ExportFailure(VideoExportOutcome.FileFailed, "FRAMED_FRAME_TRUNCATED");
                            Copy(pixels, packedWidth, 0, first); if (second != null) Copy(pixels, packedWidth, sizes[0].Width, second);
                            double at = (plan.Start.TotalSeconds + frames / (double)plan.Fps) * 1000 - plan.InputOffsetMs;
                            PadState input = timeline == null ? null : timeline.At(at); if (input != null) { pose = PadState.Blend(pose, input, .8f); input = pose; }
                            graphics.Clear(Ui.Bg0); float scale = Math.Min(plan.OutputSize.Width / 900f, plan.OutputSize.Height / 1120f);
                            RectangleF shell = new RectangleF((plan.OutputSize.Width - 900 * scale) / 2 + 20 * scale, (plan.OutputSize.Height - 1120 * scale) / 2 + 12 * scale, 860 * scale, 1050 * scale);
                            Bitmap upper = work.Tracks[0].Role == ScreenRole.Upper ? first : second, lower = work.Tracks[0].Role == ScreenRole.Lower ? first : second;
                            DeviceArt.Draw(graphics, shell, skin, upper, lower, "上屏\n未录制", "下屏\n未录制", input);
                            if (plan.ShowTouches && timeline != null && timeline.Touches.Count > 0)
                                for (int screen = 0; screen < 2; screen++) { float unit; RectangleF area = TouchFx.ScreenRect(shell, screen, out unit); TouchFx.Draw(graphics, area, unit, timeline.TouchesAt(at), screen); }
                            BitmapData data = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                            try { Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); } finally { frame.UnlockBits(data); }
                            encoder.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); frames++;
                            if (progress != null) progress.Report(Math.Min(90, 10 + frames * 80 / work.Frames));
                        }
                        encoder.StandardInput.Close();
                        if (!decoder.WaitForExit(15000) || !encoder.WaitForExit(30000)) throw new ExportFailure(VideoExportOutcome.TimedOut, "FRAMED_FINALIZE_TIMEOUT");
                        deadline.Token.ThrowIfCancellationRequested(); if (decoder.ExitCode != 0 || encoder.ExitCode != 0) throw new ExportFailure(VideoExportOutcome.FileFailed, "FRAMED_ENCODER_FAILED");
                    }
                }
                catch (Exception)
                { if (deadline.IsCancellationRequested) { if (token.IsCancellationRequested) throw new OperationCanceledException(token); throw new ExportFailure(VideoExportOutcome.TimedOut, "FRAMED_TIMEOUT"); } throw; }
                finally
                {
                    KillOwned(decoder); KillOwned(encoder);
                    try { decoder.WaitForExit(5000); } catch { } try { encoder.WaitForExit(5000); } catch { }
                    if (de != null) try { de.Wait(1000); } catch { } if (ee != null) try { ee.Wait(1000); } catch { } if (eo != null) try { eo.Wait(1000); } catch { }
                }
            }
        }
    }
}
