using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Media;

namespace ThorCapture.Features.VideoExport
{
    internal static partial class VideoExporter
    {
        public static Task<VideoPreviewResult> RenderPreviewAsync(ExportPlan plan, TimeSpan localPosition, CancellationToken token)
        {
            return Task.Run(() =>
            {
                string folder = null; List<string> files = new List<string>(); Bitmap frame = null;
                try
                {
                    token.ThrowIfCancellationRequested(); if (plan == null) throw new ExportFailure(VideoExportOutcome.InvalidPlan, "PLAN_REQUIRED");
                    if (plan.LegacyCompatible) throw new ExportFailure(VideoExportOutcome.Unsupported, "LEGACY_PREVIEW_USE_EXISTING_PLAYBACK");
                    using (ExportTools tools = new ExportTools())
                    {
                        PreparedExport work = Prepare(plan, tools, token);
                        if (localPosition.TotalSeconds < 0 || localPosition.TotalSeconds >= work.Duration) throw new ExportFailure(VideoExportOutcome.InvalidPlan, "PREVIEW_OUTSIDE_CLIP");
                        int localFrame = (int)Math.Floor(localPosition.TotalSeconds * plan.Fps + .000001);
                        double start = plan.Start.TotalSeconds + localFrame / (double)plan.Fps;
                        folder = Path.Combine(Path.GetTempPath(), "thor-video-preview-" + Guid.NewGuid().ToString("N")); if (Directory.Exists(folder)) throw new IOException(); Directory.CreateDirectory(folder);
                        if (plan.Layout != VideoLayout.Framed)
                        {
                            string png = Path.Combine(folder, "preview.png"); using (FileStream reserve = File.Open(png, FileMode.CreateNew, FileAccess.Write, FileShare.None)) files.Add(png);
                            List<string> args = Inputs(tools, work); args.AddRange(new[] { "-filter_complex", Composition(plan, work, start, 1.0 / plan.Fps), "-map", "[video]", "-an", "-frames:v", "1", "-y", png }); tools.Run(tools.Ffmpeg, args, token);
                            using (Image image = Image.FromFile(png)) frame = new Bitmap(image);
                        }
                        else
                        {
                            Bitmap[] screens = new Bitmap[work.Tracks.Length];
                            try
                            {
                                for (int i = 0; i < screens.Length; i++)
                                {
                                    VideoInfo info = work.Info[i]; double shrink = Math.Min(1, Math.Min(plan.OutputSize.Width * 850.0 / 900 / info.Width, plan.OutputSize.Height * 600.0 / 1120 / info.Height));
                                    int width = Math.Max(2, (int)(info.Width * shrink) / 2 * 2), height = Math.Max(2, (int)(info.Height * shrink) / 2 * 2);
                                    string png = Path.Combine(folder, "screen-" + i + ".png"); using (FileStream reserve = File.Open(png, FileMode.CreateNew, FileAccess.Write, FileShare.None)) files.Add(png);
                                    List<string> args = Inputs(tools, work); args.AddRange(new[] { "-filter_complex", TrackFilter(plan, work, i, width, height, "video", start, 1.0 / plan.Fps), "-map", "[video]", "-an", "-frames:v", "1", "-y", png }); tools.Run(tools.Ffmpeg, args, token);
                                    using (Image image = Image.FromFile(png)) screens[i] = new Bitmap(image);
                                }
                                frame = new Bitmap(plan.OutputSize.Width, plan.OutputSize.Height, PixelFormat.Format32bppArgb);
                                PadTimeline timeline = plan.TimelinePath == null ? null : PadTimeline.Load(plan.TimelinePath); PadState pose = null;
                                if (timeline != null) { pose = new PadState(); for (int i = 0; i <= localFrame; i++) { token.ThrowIfCancellationRequested(); pose = PadState.Blend(pose, timeline.At((plan.Start.TotalSeconds + i / (double)plan.Fps) * 1000 - plan.InputOffsetMs), .8f); } }
                                using (Graphics graphics = Graphics.FromImage(frame))
                                {
                                    graphics.Clear(Ui.Bg0); float scale = Math.Min(plan.OutputSize.Width / 900f, plan.OutputSize.Height / 1120f);
                                    RectangleF shell = new RectangleF((plan.OutputSize.Width - 900 * scale) / 2 + 20 * scale, (plan.OutputSize.Height - 1120 * scale) / 2 + 12 * scale, 860 * scale, 1050 * scale);
                                    Bitmap second = screens.Length == 1 ? null : screens[1];
                                    DeviceArt.Draw(graphics, shell, plan.Skin, work.Tracks[0].Role == ScreenRole.Upper ? screens[0] : second, work.Tracks[0].Role == ScreenRole.Lower ? screens[0] : second, "上屏\n未录制", "下屏\n未录制", pose);
                                    if (timeline != null && plan.ShowTouches && timeline.Touches.Count > 0) for (int screen = 0; screen < 2; screen++) { float unit; RectangleF area = TouchFx.ScreenRect(shell, screen, out unit); TouchFx.Draw(graphics, area, unit, timeline.TouchesAt(start * 1000 - plan.InputOffsetMs), screen); }
                                }
                            }
                            finally { foreach (Bitmap image in screens) if (image != null) image.Dispose(); }
                        }
                        foreach (SourceStamp source in work.Sources) source.Verify(token); token.ThrowIfCancellationRequested();
                        Bitmap result = frame; frame = null; return new VideoPreviewResult(VideoExportOutcome.Success, "OK", result);
                    }
                }
                catch (OperationCanceledException) { return new VideoPreviewResult(VideoExportOutcome.Cancelled, "CANCELLED"); }
                catch (ExportFailure error) { return new VideoPreviewResult(error.Outcome, error.Code); }
                catch (Exception) { return new VideoPreviewResult(VideoExportOutcome.FileFailed, "PREVIEW_FAILED"); }
                finally { if (frame != null) frame.Dispose(); foreach (string file in files) try { File.Delete(file); } catch { } if (folder != null) try { Directory.Delete(folder, false); } catch { } }
            });
        }
    }
}
