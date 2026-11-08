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
    internal sealed class PreparedExport
    {
        internal ExportTrack[] Tracks;
        internal VideoInfo[] Info;
        internal List<SourceStamp> Sources = new List<SourceStamp>();
        internal double Duration;
        internal int Frames, AudioIndex = -1;
    }
    internal static partial class VideoExporter
    {
        public static ExportPlan Legacy900x1120(string source, string peer, DeviceSkin skin, double offsetMs, bool showTouches = true)
        {
            Func<string, ScreenRole> role = path => Path.GetFileName(path).IndexOf("-lower-", StringComparison.OrdinalIgnoreCase) >= 0 ? ScreenRole.Lower : ScreenRole.Upper;
            List<ExportTrack> tracks = new List<ExportTrack> { new ExportTrack(source, role(source), TimeSpan.Zero) };
            if (peer != null) tracks.Add(new ExportTrack(peer, role(source) == ScreenRole.Upper ? ScreenRole.Lower : ScreenRole.Upper, TimeSpan.Zero));
            string timeline = File.Exists(source + ".input.csv") ? source + ".input.csv" : peer != null && File.Exists(peer + ".input.csv") ? peer + ".input.csv" : null;
            return new ExportPlan(tracks, TimeSpan.Zero, TimeSpan.Zero, VideoLayout.Framed, VideoFormat.Mp4, new Size(900, 1120), 30, 4000000, skin, EndPolicy.Shortest, offsetMs, timeline, showTouches, 256, VideoEncoding.AccurateTranscode, true);
        }
        static void ValidateBasic(ExportPlan plan)
        {
            if (plan == null) throw new ExportFailure(VideoExportOutcome.InvalidPlan, "PLAN_REQUIRED");
            if (plan.Encoding != VideoEncoding.AccurateTranscode) throw new ExportFailure(VideoExportOutcome.Unsupported, "KEYFRAME_COPY_UNSUPPORTED");
            if (plan.Format == VideoFormat.Gif && (plan.Fps > 50 || (long)plan.OutputSize.Width * plan.OutputSize.Height > 1024 * 1024 || plan.Duration == TimeSpan.Zero || plan.Duration.TotalSeconds > 30)) throw new ExportFailure(VideoExportOutcome.Unsupported, "GIF_LIMIT_EXCEEDED");
            if (plan.Layout == VideoLayout.Framed && plan.Tracks.Any(t => t.Role == ScreenRole.Unknown)) throw new ExportFailure(VideoExportOutcome.Unsupported, "FRAMED_ROLE_REQUIRED");
        }
        static void CheckLegacyTimeline(ExportPlan plan)
        {
            string first = plan.Tracks[0].Path, peer = plan.Tracks.Count == 1 ? null : plan.Tracks[1].Path;
            string actual = File.Exists(first + ".input.csv") ? first + ".input.csv" : peer != null && File.Exists(peer + ".input.csv") ? peer + ".input.csv" : null;
            if (!String.Equals(actual, plan.TimelinePath, StringComparison.OrdinalIgnoreCase)) throw new ExportFailure(VideoExportOutcome.SourceChanged, "TIMELINE_SELECTION_CHANGED");
        }
        internal static PreparedExport Prepare(ExportPlan plan, ExportTools tools, CancellationToken token)
        {
            ValidateBasic(plan); if (plan.LegacyCompatible) CheckLegacyTimeline(plan);
            PreparedExport work = new PreparedExport { Tracks = plan.Tracks.OrderBy(t => t.Role == ScreenRole.Upper ? 0 : t.Role == ScreenRole.Lower ? 1 : 2).ToArray() };
            work.Info = new VideoInfo[work.Tracks.Length];
            for (int i = 0; i < work.Tracks.Length; i++)
            {
                token.ThrowIfCancellationRequested(); work.Sources.Add(SourceStamp.Read(work.Tracks[i].Path, token)); work.Info[i] = tools.Describe(work.Tracks[i].Path, token);
                if (work.Info[i].Duration + work.Tracks[i].Offset.TotalSeconds <= 0) throw new ExportFailure(VideoExportOutcome.InvalidPlan, "TRACK_EXHAUSTED_BY_OFFSET");
                if (work.AudioIndex < 0 && work.Info[i].Audio) work.AudioIndex = i;
            }
            if (plan.TimelinePath != null) work.Sources.Add(SourceStamp.Read(plan.TimelinePath, token));
            double[] ends = work.Info.Select((v, i) => v.Duration + work.Tracks[i].Offset.TotalSeconds).ToArray();
            double available = (plan.End == EndPolicy.Shortest ? ends.Min() : ends.Max()) - plan.Start.TotalSeconds;
            if (available <= 0) throw new ExportFailure(VideoExportOutcome.InvalidPlan, "CLIP_OUTSIDE_MEDIA");
            double duration = plan.Duration == TimeSpan.Zero ? available : Math.Min(available, plan.Duration.TotalSeconds);
            if (duration > 1800 || duration <= 0) throw new ExportFailure(VideoExportOutcome.Unsupported, "CLIP_LIMIT_EXCEEDED");
            work.Frames = Math.Max(1, (int)Math.Ceiling(duration * plan.Fps - .000001)); work.Duration = work.Frames / (double)plan.Fps;
            return work;
        }
        internal static string TrackFilter(ExportPlan plan, PreparedExport work, int index, int width, int height, string output, double start, double duration)
        {
            double offset = work.Tracks[index].Offset.TotalSeconds;
            return "[" + index + ":v:0]trim=start=" + ExportTools.Number(Math.Max(0, -offset)) + ",setpts=PTS-STARTPTS,fps=" + plan.Fps
                + ",scale=" + width + ":" + height + ":force_original_aspect_ratio=decrease,pad=" + width + ":" + height + ":(ow-iw)/2:(oh-ih)/2:color=black,setsar=1"
                + ",tpad=start_duration=" + ExportTools.Number(Math.Max(0, offset)) + ":start_mode=add:stop_duration=" + ExportTools.Number(start + duration + 1) + ":stop_mode=" + (plan.End == EndPolicy.Freeze ? "clone" : "add") + ":color=black"
                + ",trim=start=" + ExportTools.Number(start) + ":duration=" + ExportTools.Number(duration) + ",setpts=PTS-STARTPTS[" + output + "]";
        }
        internal static string Composition(ExportPlan plan, PreparedExport work, double start, double duration)
        {
            int width = plan.OutputSize.Width, height = plan.OutputSize.Height;
            if (work.Tracks.Length == 1) return TrackFilter(plan, work, 0, width, height, "video", start, duration);
            bool vertical = plan.Layout == VideoLayout.Vertical;
            int firstWidth = vertical ? width : width / 2, firstHeight = vertical ? height / 2 : height;
            int secondWidth = vertical ? width : width - firstWidth, secondHeight = vertical ? height - firstHeight : height;
            return TrackFilter(plan, work, 0, firstWidth, firstHeight, "a", start, duration) + ";" + TrackFilter(plan, work, 1, secondWidth, secondHeight, "b", start, duration)
                + ";[a][b]" + (vertical ? "vstack" : "hstack") + "=inputs=2:shortest=1[video]";
        }
        internal static string AudioFilter(ExportPlan plan, PreparedExport work, int input, double start, double duration, bool labeled)
        {
            double offset = work.Tracks[work.AudioIndex].Offset.TotalSeconds;
            return (labeled ? "[" + input + ":a:0]" : "") + "atrim=start=" + ExportTools.Number(Math.Max(0, -offset)) + ",asetpts=PTS-STARTPTS,adelay=" + Math.Round(Math.Max(0, offset) * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":all=1,apad,atrim=start=" + ExportTools.Number(start) + ":duration=" + ExportTools.Number(duration) + ",asetpts=PTS-STARTPTS" + (labeled ? "[audio]" : "");
        }
        internal static List<string> Inputs(ExportTools tools, PreparedExport work)
        { List<string> args = new List<string> { "-v", "error", "-nostdin", "-threads", "1", "-filter_complex_threads", "1" }; foreach (ExportTrack track in work.Tracks) { args.Add("-i"); args.Add(track.Path); } return args; }
        internal static void EncodingArgs(ExportPlan plan, List<string> args, bool audio)
        {
            args.AddRange(plan.Format == VideoFormat.WebM ? new[] { "-c:v", "libvpx-vp9", "-deadline", "good", "-cpu-used", "4" } : new[] { "-c:v", "libx264", "-preset", "veryfast" });
            args.AddRange(new[] { "-b:v", plan.BitRate.ToString(), "-pix_fmt", "yuv420p" });
            if (audio) args.AddRange(plan.Format == VideoFormat.WebM ? new[] { "-c:a", "libopus", "-b:a", "128k" } : new[] { "-c:a", "aac", "-b:a", "192k" });
            if (plan.Format == VideoFormat.Mp4) args.AddRange(new[] { "-movflags", "+faststart" });
        }
        static void Flat(ExportPlan plan, PreparedExport work, ExportTools tools, string output, string palette, CancellationToken token)
        {
            string filter = Composition(plan, work, plan.Start.TotalSeconds, work.Duration);
            if (plan.Format == VideoFormat.Gif)
            {
                List<string> generate = Inputs(tools, work); generate.AddRange(new[] { "-filter_complex", filter + ";[video]palettegen=max_colors=" + plan.PaletteColors + ":stats_mode=full[palette]", "-map", "[palette]", "-frames:v", "1", "-y", palette }); tools.Run(tools.Ffmpeg, generate, token, 1800);
                List<string> encode = Inputs(tools, work); encode.AddRange(new[] { "-i", palette, "-filter_complex", filter + ";[video][" + work.Tracks.Length + ":v]paletteuse=dither=bayer:bayer_scale=3[gif]", "-map", "[gif]", "-an", "-loop", "0", "-frames:v", work.Frames.ToString(), "-y", output }); tools.Run(tools.Ffmpeg, encode, token, 1800);
            }
            else
            {
                List<string> args = Inputs(tools, work);
                if (work.AudioIndex >= 0) filter += ";" + AudioFilter(plan, work, work.AudioIndex, plan.Start.TotalSeconds, work.Duration, true);
                args.AddRange(new[] { "-filter_complex", filter, "-map", "[video]" }); if (work.AudioIndex >= 0) args.AddRange(new[] { "-map", "[audio]" }); else args.Add("-an");
                EncodingArgs(plan, args, work.AudioIndex >= 0); args.AddRange(new[] { "-frames:v", work.Frames.ToString(), "-t", ExportTools.Number(work.Duration), "-y", output }); tools.Run(tools.Ffmpeg, args, token, 1800);
            }
        }
        public static Task<VideoExportResult> CheckCapabilitiesAsync(ExportPlan plan, CancellationToken token)
        {
            return Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested(); ValidateBasic(plan);
                    using (ExportTools tools = new ExportTools()) tools.Require(plan, token);
                    return new VideoExportResult(VideoExportOutcome.Success, "CAPABILITIES_AVAILABLE");
                }
                catch (OperationCanceledException) { return new VideoExportResult(VideoExportOutcome.Cancelled, "CANCELLED"); }
                catch (ExportFailure failure) { return new VideoExportResult(failure.Outcome, failure.Code); }
                catch (ArgumentException) { return new VideoExportResult(VideoExportOutcome.InvalidPlan, "INVALID_PLAN"); }
                catch (Exception) { return new VideoExportResult(VideoExportOutcome.FileFailed, "CAPABILITY_CHECK_FAILED"); }
            });
        }

        public static Task<VideoExportResult> ExportAsync(ExportPlan plan, string destination, IProgress<int> progress, CancellationToken token)
        {
            return Task.Run(() =>
            {
                string temp = null, palette = null; bool own = false, ownPalette = false;
                try
                {
                    token.ThrowIfCancellationRequested(); ValidateBasic(plan);
                    destination = Path.GetFullPath(destination); ExportTools.SafePath(destination);
                    if (File.Exists(destination)) return new VideoExportResult(VideoExportOutcome.AlreadyExists, "TARGET_EXISTS");
                    if (destination.Length + 96 >= 260) throw new ExportFailure(VideoExportOutcome.Unsupported, "LONG_PATH_UNSUPPORTED");
                    if (!String.Equals(Path.GetExtension(destination), plan.Extension, StringComparison.OrdinalIgnoreCase)) throw new ExportFailure(VideoExportOutcome.InvalidPlan, "TARGET_EXTENSION_MISMATCH");
                    if (progress != null) progress.Report(0);
                    using (ExportTools tools = new ExportTools())
                    {
                        tools.Require(plan, token); PreparedExport work = Prepare(plan, tools, token);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)); temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial" + plan.Extension; palette = temp + ".palette.png";
                        using (FileStream reserve = File.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) own = true;
                        if (plan.Format == VideoFormat.Gif && plan.Layout != VideoLayout.Framed) using (FileStream reserve = File.Open(palette, FileMode.CreateNew, FileAccess.Write, FileShare.None)) ownPalette = true;
                        if (progress != null) progress.Report(10);
                        if (plan.LegacyCompatible)
                        {
                            string first = plan.Tracks[0].Path, peer = plan.Tracks.Count == 1 ? null : plan.Tracks[1].Path;
                            ThorCapture.VideoExport.Export(first, peer, temp, plan.Skin, plan.InputOffsetMs, plan.ShowTouches, value => { if (progress != null) progress.Report(Math.Min(90, 10 + value * 8 / 10)); }, token);
                        }
                        else if (plan.Layout == VideoLayout.Framed) Framed(plan, work, tools, temp, progress, token);
                        else Flat(plan, work, tools, temp, palette, token);
                        if (progress != null) progress.Report(95); tools.VerifyOutput(temp, plan, work.Duration, token, work.AudioIndex >= 0 && plan.Format != VideoFormat.Gif);
                        foreach (SourceStamp source in work.Sources) source.Verify(token);
                        if (plan.LegacyCompatible) CheckLegacyTimeline(plan);
                        token.ThrowIfCancellationRequested(); File.Move(temp, destination); own = false;
                        if (progress != null) { try { progress.Report(100); } catch { } }
                        return new VideoExportResult(VideoExportOutcome.Success, "OK", destination, work.Duration, new FileInfo(destination).Length);
                    }
                }
                catch (OperationCanceledException) { return new VideoExportResult(VideoExportOutcome.Cancelled, "CANCELLED"); }
                catch (ExportFailure error) { return new VideoExportResult(error.Outcome, error.Code); }
                catch (ArgumentException) { return new VideoExportResult(VideoExportOutcome.InvalidPlan, "INVALID_PATH"); }
                catch (IOException) { return new VideoExportResult(File.Exists(destination) ? VideoExportOutcome.AlreadyExists : VideoExportOutcome.FileFailed, File.Exists(destination) ? "TARGET_EXISTS" : "FILE_FAILED"); }
                catch (Exception) { return new VideoExportResult(VideoExportOutcome.FileFailed, "EXPORT_FAILED"); }
                finally { if (own && temp != null) { try { File.Delete(temp); } catch { } } if (ownPalette && palette != null) { try { File.Delete(palette); } catch { } } }
            });
        }
    }
}
