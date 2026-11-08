using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture.Features.VideoExport
{
    internal sealed class ExportFailure : Exception
    {
        internal readonly VideoExportOutcome Outcome; internal readonly string Code;
        internal ExportFailure(VideoExportOutcome outcome, string code) { Outcome = outcome; Code = code; }
    }
    internal sealed class SourceStamp
    {
        internal string Path, Sha;
        internal long Length; internal DateTime Write;
        internal static SourceStamp Read(string path, CancellationToken token)
        {
            ExportTools.SafePath(path); FileInfo before = new FileInfo(path);
            if (!before.Exists || before.Length > 8L * 1024 * 1024 * 1024) throw new ExportFailure(VideoExportOutcome.FileFailed, "SOURCE_UNAVAILABLE");
            string hash;
            using (FileStream file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[65536]; int count;
                while ((count = file.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, count, null, 0); }
                sha.TransformFinalBlock(buffer, 0, 0); hash = BitConverter.ToString(sha.Hash).Replace("-", "");
            }
            FileInfo after = new FileInfo(path);
            if (!after.Exists || before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc) throw new ExportFailure(VideoExportOutcome.SourceChanged, "SOURCE_CHANGED");
            return new SourceStamp { Path = path, Sha = hash, Length = after.Length, Write = after.LastWriteTimeUtc };
        }
        internal void Verify(CancellationToken token)
        { SourceStamp current = Read(Path, token); if (Sha != current.Sha || Length != current.Length || Write != current.Write) throw new ExportFailure(VideoExportOutcome.SourceChanged, "SOURCE_CHANGED"); }
    }
    internal sealed class VideoInfo { internal int Width, Height; internal double Duration, Fps; internal bool Audio; }
    internal sealed class ExportTools : IDisposable
    {
        internal readonly string Ffmpeg, Probe;
        readonly ProcessRunner runner = new ProcessRunner();
        readonly BinaryProcessRunner longRunner = new BinaryProcessRunner();
        internal ExportTools()
        {
            try { Ffmpeg = ThorCapture.VideoExport.Locate(); }
            catch (IOException) { throw new ExportFailure(VideoExportOutcome.Unsupported, "FFMPEG_MISSING"); }
            Probe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Ffmpeg), "ffprobe.exe");
            if (!File.Exists(Probe)) throw new ExportFailure(VideoExportOutcome.Unsupported, "FFPROBE_MISSING");
        }
        internal static string Number(double value) { return value.ToString("0.000000", CultureInfo.InvariantCulture); }
        internal static void SafePath(string path)
        {
            string full = System.IO.Path.GetFullPath(path);
            if (full.IndexOf(':', 2) >= 0) throw new ExportFailure(VideoExportOutcome.InvalidPlan, "PATH_UNSAFE");
            string current = full;
            while (!String.IsNullOrEmpty(current))
            {
                if (File.Exists(current) || Directory.Exists(current)) { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ExportFailure(VideoExportOutcome.InvalidPlan, "PATH_UNSAFE"); }
                string parent = System.IO.Path.GetDirectoryName(current); if (parent == current) break; current = parent;
            }
        }
        internal string Run(string executable, IEnumerable<string> args, CancellationToken token, int seconds = 30)
        {
            if (seconds > 300)
            {
                BinaryProcessResult binary = longRunner.RunBytesAsync(new ProcessRequest(executable, "video.export", args.ToArray()), 262144, TimeSpan.FromSeconds(seconds), token).GetAwaiter().GetResult();
                if (binary.Outcome == BinaryProcessOutcome.Cancelled) throw new OperationCanceledException(token);
                if (binary.Outcome == BinaryProcessOutcome.TimedOut) throw new ExportFailure(VideoExportOutcome.TimedOut, "PROCESS_TIMEOUT");
                if (binary.Outcome != BinaryProcessOutcome.Success || !binary.CleanupComplete) throw new ExportFailure(VideoExportOutcome.FileFailed, "FFMPEG_FAILED");
                return Encoding.UTF8.GetString(binary.Output);
            }
            ProcessResult result = runner.RunAsync(new ProcessRequest(executable, "video.export", args.ToArray()), TimeSpan.FromSeconds(seconds), token).GetAwaiter().GetResult();
            if (result.Outcome == ProcessOutcome.Cancelled) throw new OperationCanceledException(token);
            if (result.Outcome == ProcessOutcome.TimedOut) throw new ExportFailure(VideoExportOutcome.TimedOut, "PROCESS_TIMEOUT");
            if (result.Outcome != ProcessOutcome.Success || !result.CleanupComplete) throw new ExportFailure(VideoExportOutcome.FileFailed, "FFMPEG_FAILED");
            return result.Output;
        }
        internal VideoInfo Describe(string file, CancellationToken token)
        {
            object node = Json.Parse(Run(Probe, new[] { "-v", "error", "-show_entries", "stream=codec_type,width,height,duration,avg_frame_rate:format=duration", "-of", "json", file }, token));
            List<object> streams = Json.Get(node, "streams") as List<object>; if (streams == null) throw new ExportFailure(VideoExportOutcome.InvalidPlan, "VIDEO_INVALID");
            object video = streams.FirstOrDefault(s => Json.Text(s, "codec_type") == "video"); double duration;
            string value = Json.Text(Json.Get(node, "format"), "duration") ?? Json.Text(video, "duration");
            if (video == null || !Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out duration) || Double.IsNaN(duration) || Double.IsInfinity(duration) || duration <= 0) throw new ExportFailure(VideoExportOutcome.InvalidPlan, "VIDEO_INVALID");
            int width = (int)Json.Number(video, "width", 0), height = (int)Json.Number(video, "height", 0);
            if (width < 1 || height < 1 || width > 8192 || height > 8192) throw new ExportFailure(VideoExportOutcome.InvalidPlan, "VIDEO_SIZE_UNSUPPORTED");
            string[] rate = (Json.Text(video, "avg_frame_rate") ?? "0/1").Split('/'); double numerator, denominator, fps = 0;
            if (rate.Length == 2 && Double.TryParse(rate[0], NumberStyles.Float, CultureInfo.InvariantCulture, out numerator) && Double.TryParse(rate[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator) && denominator > 0) fps = numerator / denominator;
            return new VideoInfo { Width = width, Height = height, Duration = duration, Fps = fps, Audio = streams.Any(s => Json.Text(s, "codec_type") == "audio") };
        }
        internal void Require(ExportPlan plan, CancellationToken token)
        {
            string encoders = Run(Ffmpeg, new[] { "-hide_banner", "-encoders" }, token);
            string[] needed = plan.Format == VideoFormat.Gif ? new[] { "gif", "png" } : plan.Format == VideoFormat.WebM ? new[] { "libvpx-vp9", "libopus" } : new[] { "libx264", "aac" };
            foreach (string codec in needed.Concat(plan.Layout == VideoLayout.Framed && plan.Format == VideoFormat.Gif ? new[] { "libx264" } : new string[0])) if (!encoders.Split('\n').Any(line => line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(1).Contains(codec))) throw new ExportFailure(VideoExportOutcome.Unsupported, "ENCODER_MISSING_" + codec.ToUpperInvariant().Replace('-', '_'));
            string filters = Run(Ffmpeg, new[] { "-hide_banner", "-filters" }, token);
            foreach (string name in new[] { "trim", "tpad", "fps", "scale", "pad", "hstack", "vstack", "atrim", "adelay", "apad" }.Concat(plan.Format == VideoFormat.Gif ? new[] { "palettegen", "paletteuse" } : new string[0]))
                if (!filters.Split('\n').Any(line => line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(1).Contains(name))) throw new ExportFailure(VideoExportOutcome.Unsupported, "FILTER_MISSING_" + name.ToUpperInvariant());
        }
        internal void VerifyOutput(string file, ExportPlan plan, double duration, CancellationToken token, bool audioExpected = false)
        {
            VideoInfo output = Describe(file, token);
            if (output.Width != plan.OutputSize.Width || output.Height != plan.OutputSize.Height || Math.Abs(output.Duration - duration) > Math.Max(.15, 2.0 / plan.Fps)) throw new ExportFailure(VideoExportOutcome.FileFailed, "OUTPUT_VALIDATION_FAILED");
            if (plan.Format != VideoFormat.Gif && Math.Abs(output.Fps - plan.Fps) > .01) throw new ExportFailure(VideoExportOutcome.FileFailed, "OUTPUT_FPS_INVALID");
            if (audioExpected && !output.Audio) throw new ExportFailure(VideoExportOutcome.FileFailed, "OUTPUT_AUDIO_MISSING");
            Run(Ffmpeg, new[] { "-v", "error", "-i", file, "-map", "0:v:0", "-map", "0:a?", "-f", "null", "-" }, token, 1800);
        }
        public void Dispose() { runner.Dispose(); longRunner.Dispose(); }
    }
}
