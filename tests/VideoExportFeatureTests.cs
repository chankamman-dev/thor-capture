using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using ThorCapture.Features.VideoExport;
using ThorCapture.Media;
using ThorCapture.Backend;

namespace ThorCapture
{
    internal static class VideoExportFeatureTests
    {
        sealed class BatchObserver : IProgress<BatchProgress>
        {
            internal readonly List<BatchProgress> Items = new List<BatchProgress>();
            internal Action<BatchProgress> Action;
            public void Report(BatchProgress value) { Items.Add(value); if (Action != null) Action(value); }
        }
        static string Folder(string name) { string dir = Path.Combine(Tests.Root, name + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
        static string Png(string dir, string name)
        { string path = Path.Combine(dir, name); using (Bitmap bitmap = new Bitmap(16, 16)) using (Graphics graphics = Graphics.FromImage(bitmap)) { graphics.Clear(Color.Red); bitmap.Save(path, ImageFormat.Png); } return path; }
        static byte[] ImageBytes(Bitmap image) { using (MemoryStream bytes = new MemoryStream()) { image.Save(bytes, ImageFormat.Png); return bytes.ToArray(); } }
        static ExportPlan Plan(string source, string lower, VideoLayout layout, VideoFormat format = VideoFormat.Mp4, EndPolicy end = EndPolicy.Shortest, double start = .13, double duration = .55, double firstOffset = 0, double secondOffset = 0)
        {
            List<ExportTrack> tracks = new List<ExportTrack> { new ExportTrack(source, ScreenRole.Upper, TimeSpan.FromSeconds(firstOffset)) };
            if (lower != null) tracks.Add(new ExportTrack(lower, ScreenRole.Lower, TimeSpan.FromSeconds(secondOffset)));
            return new ExportPlan(tracks, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(duration), layout, format, new Size(160, 120), 12, 1000000, DeviceSkin.All[1], end);
        }
        internal static void Run()
        {
            string dir = Folder("video-plan"), source = Png(dir, "原片.png"); DeviceSkin skin = ImageExportPlan.CopySkin(DeviceSkin.All[2]);
            List<ExportTrack> tracks = new List<ExportTrack> { new ExportTrack(source, ScreenRole.Upper, TimeSpan.FromMilliseconds(100)) };
            ExportPlan immutable = new ExportPlan(tracks, TimeSpan.Zero, TimeSpan.FromSeconds(1), VideoLayout.Single, VideoFormat.Mp4, new Size(160, 120), 12, 1000000, skin);
            Color original = immutable.Skin.Body; skin.Body = Color.Magenta; tracks.Clear(); DeviceSkin returned = immutable.Skin; returned.Body = Color.Black;
            Tests.Check(immutable.Tracks.Count == 1 && immutable.Skin.Body == original && immutable.Tracks[0].SourceTime(TimeSpan.FromMilliseconds(300)) == TimeSpan.FromMilliseconds(200), "video plan snapshots skin and track collection with explicit positive-offset timeline mapping");
            Tests.Reject(() => new ExportPlan(immutable.Tracks, TimeSpan.Zero, TimeSpan.FromSeconds(1), VideoLayout.Single, VideoFormat.Mp4, new Size(161, 120), 12, 1000000, skin), "video plan refuses odd YUV dimensions");
            Tests.Reject(() => new ExportPlan(immutable.Tracks, TimeSpan.Zero, TimeSpan.FromSeconds(1), VideoLayout.Single, VideoFormat.Gif, new Size(4096, 4096), 12, 1000000, skin), "video plan enforces output pixel budget before allocating bitmap");
            Tests.Check(immutable.Estimate().MinimumBytes < immutable.Estimate().MaximumBytes && !immutable.Estimate().DurationUnknown && immutable.Describe().Contains("转码"), "plan describes accurate transcoding and exposes an explicit size-estimate range");
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            { cancel.Cancel(); Tests.Check(VideoExporter.ExportAsync(immutable, Path.Combine(dir, "cancel.mp4"), null, cancel.Token).Result.Outcome == VideoExportOutcome.Cancelled && !File.Exists(Path.Combine(dir, "cancel.mp4")), "pre-cancelled export publishes no target and launches no media process"); }
            string existing = Path.Combine(dir, "existing.mp4"); File.WriteAllText(existing, "sentinel");
            Tests.Check(VideoExporter.ExportAsync(immutable, existing, null, CancellationToken.None).Result.Outcome == VideoExportOutcome.AlreadyExists && File.ReadAllText(existing) == "sentinel", "new video exporter never overwrites an existing destination or original alias");
            string longTarget = Path.Combine(dir, new string('a', Math.Max(1, 210 - dir.Length - 5)) + ".mp4");
            Tests.Check(VideoExporter.ExportAsync(immutable, longTarget, null, CancellationToken.None).Result.Code == "LONG_PATH_UNSUPPORTED", "legacy Framework long-path limit is explicit before tool launch or media work");
            ImageExportPlan image = new ImageExportPlan(ExportLayout.Single, skin, ExportBackground.Transparent, Color.Black, Color.Black, null, 1, 0, Color.Black, 0, ImageFormat.Png, 90);
            BatchJob job = new BatchJob("group-one", image, source, null), missing = new BatchJob("group-missing", image, Path.Combine(dir, "missing.png"), null);
            string output = Path.Combine(dir, "batch"); BatchObserver observer = new BatchObserver();
            BatchReport report = BatchExporter.RunAsync(new[] { job, missing }, output, new NameTemplate("{index}-{name}"), ConflictPolicy.Fail, observer, CancellationToken.None).Result;
            Tests.Check(report.Items[0].Outcome == BatchItemOutcome.Success && report.Items[1].Outcome == BatchItemOutcome.Failed && report.RetryableJobs.Count == 1 && File.Exists(report.Items[0].Destination), "batch image export retains successes and reports failed missing source with retryable jobs");
            Tests.Check(observer.Items.Where(p => p.Saved).All(p => p.Percent == 100) && observer.Items.Where(p => !p.Saved).All(p => p.Percent < 100), "batch progress never labels encoding progress as saved before atomic publication");
            BatchReport skipped = BatchExporter.RunAsync(new[] { job }, output, new NameTemplate("001-原片"), ConflictPolicy.Skip, null, CancellationToken.None).Result;
            Tests.Check(skipped.Items[0].Outcome == BatchItemOutcome.Skipped, "batch conflict skip preserves existing item");
            BatchReport renamed = BatchExporter.RunAsync(new[] { job }, output, new NameTemplate("001-原片"), ConflictPolicy.Rename, null, CancellationToken.None).Result;
            Tests.Check(renamed.Items[0].Outcome == BatchItemOutcome.Success && renamed.Items[0].Destination.EndsWith("-2.png"), "batch rename policy chooses a separate non-overwriting destination");
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                BatchObserver stopped = new BatchObserver { Action = p => { if (p.Saved) cancel.Cancel(); } };
                BatchReport partial = BatchExporter.RunAsync(new[] { job, job }, output, new NameTemplate("stop-{index}"), ConflictPolicy.Fail, stopped, cancel.Token).Result;
                Tests.Check(partial.Cancelled && partial.Items[0].Outcome == BatchItemOutcome.Success && partial.Items[1].Outcome == BatchItemOutcome.NotStarted && File.Exists(partial.Items[0].Destination), "batch cancellation retains completed output and marks remaining items not started");
            }
            BatchReport quota = BatchExporter.RunAsync(new[] { job }, output, new NameTemplate("quota"), ConflictPolicy.Fail, null, CancellationToken.None, path => 0).Result;
            Tests.Check(quota.Items[0].Code == "DISK_SPACE_LOW" && !File.Exists(quota.Items[0].Destination), "simulated exhausted disk quota fails before encoding without publishing output");
            BatchReport blocked = BatchExporter.RunAsync(new[] { job }, source, new NameTemplate("blocked"), ConflictPolicy.Fail, null, CancellationToken.None).Result;
            Tests.Check(blocked.Items[0].Code == "DIRECTORY_UNWRITABLE", "batch rejects an unwritable directory fixture before starting image work");
            Tests.Reject(() => new NameTemplate("../{name}"), "batch template cannot escape selected output directory");
            Tests.Check(new NameTemplate("CON").Expand(job, 0) == "media-1", "batch names normalize Windows reserved device names");
            Tests.Check(!Directory.GetFiles(output).Any(p => p.Contains(".partial") || p.Contains(".thor-write-probe")), "image batch cleans owned temporary and write-probe files");
        }
        internal static void RunStream(string ffmpeg)
        {
            string dir = Folder("video-export-stream"); Environment.SetEnvironmentVariable("THOR_FFMPEG", ffmpeg);
            string upper = Path.Combine(dir, "thor-upper-fixture.mp4"), lower = Path.Combine(dir, "thor-lower-fixture.mp4");
            using (ExportTools tools = new ExportTools())
            {
                tools.Run(ffmpeg, new[] { "-v", "error", "-f", "lavfi", "-i", "color=red:s=64x48:r=12:d=1.6", "-f", "lavfi", "-i", "sine=frequency=440:duration=1.6", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-y", upper }, CancellationToken.None);
                tools.Run(ffmpeg, new[] { "-v", "error", "-f", "lavfi", "-i", "color=blue:s=64x48:r=12:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-y", lower }, CancellationToken.None);
                SourceStamp topStamp = SourceStamp.Read(upper, CancellationToken.None), lowerStamp = SourceStamp.Read(lower, CancellationToken.None);
                ExportPlan clip = Plan(upper, lower, VideoLayout.Vertical); string stacked = Path.Combine(dir, "stacked.mp4");
                VideoExportResult first = VideoExporter.ExportAsync(clip, stacked, null, CancellationToken.None).Result;
                if (first.Outcome != VideoExportOutcome.Success) throw new Exception("clip export " + first.Outcome + ": " + first.Code);
                Tests.Check(first.Outcome == VideoExportOutcome.Success && Math.Abs(first.Duration - 7.0 / 12) < .001 && tools.Describe(stacked, CancellationToken.None).Audio, "non-integer dual-screen clip transcodes to exact output frame grid and retains shared audio");
                using (BinaryProcessRunner audio = new BinaryProcessRunner())
                {
                    BinaryProcessResult pcm = audio.RunBytesAsync(new ProcessRequest(ffmpeg, "test.audio", new[] { "-v", "error", "-i", stacked, "-vn", "-ac", "1", "-ar", "48000", "-f", "s16le", "pipe:1" }), 1024 * 1024, TimeSpan.FromSeconds(30), CancellationToken.None).Result;
                    long amplitude = 0; for (int i = 0; i + 1 < pcm.Output.Length; i += 2) amplitude += Math.Abs((int)BitConverter.ToInt16(pcm.Output, i));
                    Tests.Check(pcm.Outcome == BinaryProcessOutcome.Success && pcm.Output.Length > 1000 && amplitude / (pcm.Output.Length / 2) > 1000, "exported clip audio fully decodes to the synthetic tone rather than a silent placeholder track");
                }
                using (VideoPreviewResult preview = VideoExporter.RenderPreviewAsync(clip, TimeSpan.Zero, CancellationToken.None).Result)
                { Tests.Check(preview.Outcome == VideoExportOutcome.Success && preview.Frame.GetPixel(80, 30).R > 180 && preview.Frame.GetPixel(80, 90).B > 180, "shared export plan preview assigns upper red and lower blue to vertical layout"); }
                ExportPlan delayed = Plan(upper, lower, VideoLayout.Horizontal, secondOffset: .5, start: 0, duration: .8);
                using (VideoPreviewResult before = VideoExporter.RenderPreviewAsync(delayed, TimeSpan.Zero, CancellationToken.None).Result)
                using (VideoPreviewResult after = VideoExporter.RenderPreviewAsync(delayed, TimeSpan.FromSeconds(.6), CancellationToken.None).Result)
                { Tests.Check(before.Outcome == VideoExportOutcome.Success && after.Outcome == VideoExportOutcome.Success && before.Frame.GetPixel(120, 60).B < 20 && after.Frame.GetPixel(120, 60).B > 180, "positive track offset uses black pre-roll and later starts actual source in shared preview"); }
                ExportPlan negative = Plan(upper, lower, VideoLayout.Horizontal, firstOffset: -.3, duration: .4);
                Tests.Check(VideoExporter.ExportAsync(negative, Path.Combine(dir, "negative.mp4"), null, CancellationToken.None).Result.Outcome == VideoExportOutcome.Success, "negative track offset trims source before shared timeline without rewriting original");
                ExportPlan shortest = Plan(upper, lower, VideoLayout.Vertical, start: .8, duration: .7);
                Tests.Check(VideoExporter.ExportAsync(shortest, Path.Combine(dir, "shortest.mp4"), null, CancellationToken.None).Result.Duration < .3, "shortest end policy clips at aligned shorter source");
                ExportPlan freeze = Plan(upper, lower, VideoLayout.Vertical, end: EndPolicy.Freeze, start: 1.2, duration: .2);
                ExportPlan blank = Plan(upper, lower, VideoLayout.Vertical, end: EndPolicy.Blank, start: 1.2, duration: .2);
                using (VideoPreviewResult frozen = VideoExporter.RenderPreviewAsync(freeze, TimeSpan.Zero, CancellationToken.None).Result)
                using (VideoPreviewResult empty = VideoExporter.RenderPreviewAsync(blank, TimeSpan.Zero, CancellationToken.None).Result)
                { Tests.Check(frozen.Outcome == VideoExportOutcome.Success && empty.Outcome == VideoExportOutcome.Success && frozen.Frame.GetPixel(80, 90).B > 180 && empty.Frame.GetPixel(80, 90).B < 20, "longer-end policies explicitly freeze or blank exhausted companion track"); }
                ExportPlan gif = Plan(upper, lower, VideoLayout.Horizontal, VideoFormat.Gif); string gifFile = Path.Combine(dir, "clip.gif");
                VideoExportResult gifResult = VideoExporter.ExportAsync(gif, gifFile, null, CancellationToken.None).Result;
                Tests.Check(gifResult.Outcome == VideoExportOutcome.Success && File.ReadAllBytes(gifFile).Take(3).SequenceEqual(new byte[] { 71, 73, 70 }), "dual-screen GIF uses validated two-pass palette generation and preserves clip size");
                File.WriteAllText(Path.Combine(dir, "gif-size.txt"), "static estimate=" + gif.Estimate().MinimumBytes + ".." + gif.Estimate().MaximumBytes + "; actual=" + new FileInfo(gifFile).Length);
                string changing = Path.Combine(dir, "changing.mp4"); tools.Run(ffmpeg, new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=64x48:r=12:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-y", changing }, CancellationToken.None);
                ExportPlan changingGif = Plan(changing, null, VideoLayout.Single, VideoFormat.Gif); string changingOutput = Path.Combine(dir, "changing.gif");
                Tests.Check(VideoExporter.ExportAsync(changingGif, changingOutput, null, CancellationToken.None).Result.Outcome == VideoExportOutcome.Success && new FileInfo(changingOutput).Length > new FileInfo(gifFile).Length, "high-variation GIF is fully decoded and measured separately from static palette output");
                File.AppendAllText(Path.Combine(dir, "gif-size.txt"), "\nhigh variation estimate=" + changingGif.Estimate().MinimumBytes + ".." + changingGif.Estimate().MaximumBytes + "; actual=" + new FileInfo(changingOutput).Length);
                Tests.Check(VideoExporter.ExportAsync(Plan(upper, null, VideoLayout.Single, VideoFormat.WebM), Path.Combine(dir, "single.webm"), null, CancellationToken.None).Result.Outcome == VideoExportOutcome.Success, "single-screen WebM verifies actual VP9 Opus codecs and fully decodes output");
                ExportPlan framed = new ExportPlan(clip.Tracks, TimeSpan.FromSeconds(.2), TimeSpan.FromSeconds(.25), VideoLayout.Framed, VideoFormat.Mp4, new Size(240, 300), 12, 1000000, DeviceSkin.All[1]);
                string framedFile = Path.Combine(dir, "framed.mp4"); Tests.Check(VideoExporter.ExportAsync(framed, framedFile, null, CancellationToken.None).Result.Outcome == VideoExportOutcome.Success, "new framed renderer supports precise clip custom size and shared audio without changing DeviceArt");
                using (VideoPreviewResult preview = VideoExporter.RenderPreviewAsync(framed, TimeSpan.Zero, CancellationToken.None).Result) Tests.Check(preview.Outcome == VideoExportOutcome.Success && preview.Frame.Size == framed.OutputSize, "framed preview samples the same plan and caller owns disposable bitmap");
                ExportPlan framedGif = new ExportPlan(clip.Tracks, framed.Start, framed.Duration, VideoLayout.Framed, VideoFormat.Gif, framed.OutputSize, framed.Fps, framed.BitRate, framed.Skin);
                Tests.Check(VideoExporter.ExportAsync(framedGif, Path.Combine(dir, "framed.gif"), null, CancellationToken.None).Result.Outcome == VideoExportOutcome.Success, "framed GIF encodes shared shell through an owned intermediate and palette workflow");
                string timeline = upper + ".input.csv"; File.WriteAllText(timeline, PadTimeline.Format(0, 0, new PadState()) + "\n" + PadTimeline.Format(500, .5, new PadState { Buttons = PadButton.A }) + "\n");
                ExportPlan animated = new ExportPlan(clip.Tracks, TimeSpan.FromSeconds(.2), TimeSpan.FromSeconds(.6), VideoLayout.Framed, VideoFormat.Mp4, framed.OutputSize, 12, 1000000, framed.Skin, timelinePath: timeline);
                using (VideoPreviewResult early = VideoExporter.RenderPreviewAsync(animated, TimeSpan.Zero, CancellationToken.None).Result)
                using (VideoPreviewResult later = VideoExporter.RenderPreviewAsync(animated, TimeSpan.FromSeconds(.4), CancellationToken.None).Result)
                { Tests.Check(early.Outcome == VideoExportOutcome.Success && later.Outcome == VideoExportOutcome.Success && !ImageBytes(early.Frame).SequenceEqual(ImageBytes(later.Frame)), "framed plan preview reconstructs input history at clip-relative time instead of showing a neutral placeholder"); }
                File.Delete(timeline);
                ExportPlan legacy = VideoExporter.Legacy900x1120(upper, lower, DeviceSkin.All[1], 0); string legacyOut = Path.Combine(dir, "legacy-new.mp4"), old = Path.Combine(dir, "legacy-old.mp4");
                Tests.Check(VideoExporter.ExportAsync(legacy, legacyOut, null, CancellationToken.None).Result.Outcome == VideoExportOutcome.Success, "legacy preset preserves 900 by 1120 thirty-fps whole-clip export");
                ThorCapture.VideoExport.Export(upper, lower, old, DeviceSkin.All[1], 0, delegate { }, CancellationToken.None);
                string newPixels = tools.Run(ffmpeg, new[] { "-v", "error", "-i", legacyOut, "-map", "0:v:0", "-frames:v", "1", "-f", "framemd5", "-" }, CancellationToken.None), oldPixels = tools.Run(ffmpeg, new[] { "-v", "error", "-i", old, "-map", "0:v:0", "-frames:v", "1", "-f", "framemd5", "-" }, CancellationToken.None);
                Tests.Check(newPixels == oldPixels, "compatibility preset delegates existing renderer and decoded first-frame pixels match old export");
                ExportPlan legacyStale = VideoExporter.Legacy900x1120(upper, lower, DeviceSkin.All[1], 0); File.WriteAllText(upper + ".input.csv", PadTimeline.Format(0, 0, new PadState()) + "\n");
                Tests.Check(VideoExporter.ExportAsync(legacyStale, Path.Combine(dir, "stale-timeline.mp4"), null, CancellationToken.None).Result.Outcome == VideoExportOutcome.SourceChanged, "legacy plan refuses timeline selection changes rather than silently reading a newly appeared sidecar"); File.Delete(upper + ".input.csv");
                string changedSource = Path.Combine(dir, "changed-source.mp4"); File.Copy(upper, changedSource); string rejected = Path.Combine(dir, "rejected-changed.mp4");
                VideoExportResult changedResult = VideoExporter.ExportAsync(Plan(changedSource, null, VideoLayout.Single), rejected, new InlineProgress(value => { if (value == 95) File.AppendAllText(changedSource, "fixture mutation"); }), CancellationToken.None).Result;
                Tests.Check(changedResult.Outcome == VideoExportOutcome.SourceChanged && !File.Exists(rejected), "source mutation before publication rejects validated output and cleans owned temporary");
                ImageExportPlan imagePlan = ImageExportPlan.Default(DeviceSkin.All[1]); string screenshot = Png(dir, "batch-image.png");
                BatchReport mixed = BatchExporter.RunAsync(new[] { new BatchJob("image", imagePlan, screenshot, null), new BatchJob("clip", clip), new BatchJob("missing", Plan(Path.Combine(dir, "absent.mp4"), null, VideoLayout.Single)) }, Path.Combine(dir, "mixed-batch"), new NameTemplate("{index}-{id}"), ConflictPolicy.Fail, null, CancellationToken.None).Result;
                Tests.Check(mixed.Items.Count == 3 && mixed.Items.Take(2).All(item => item.Outcome == BatchItemOutcome.Success) && mixed.Items[2].Outcome == BatchItemOutcome.Failed, "mixed image and video batch serially retains both successful outputs while reporting a missing video");
                ExportPlan copy = new ExportPlan(clip.Tracks, clip.Start, clip.Duration, clip.Layout, clip.Format, clip.OutputSize, clip.Fps, clip.BitRate, clip.Skin, encoding: VideoEncoding.KeyframeCopy);
                Tests.Check(VideoExporter.ExportAsync(copy, Path.Combine(dir, "copy.mp4"), null, CancellationToken.None).Result.Outcome == VideoExportOutcome.Unsupported, "keyframe-copy mode is explicitly unsupported rather than pretending precise copied clipping");
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    VideoExportResult cancelled = VideoExporter.ExportAsync(framed, Path.Combine(dir, "cancel-frame.mp4"), new InlineProgress(value => { if (value >= 10) cancel.Cancel(); }), cancel.Token).Result;
                    Tests.Check(cancelled.Outcome == VideoExportOutcome.Cancelled && !File.Exists(Path.Combine(dir, "cancel-frame.mp4")), "inflight owned export cancellation cleans partial output and publishes no target");
                }
                topStamp.Verify(CancellationToken.None); lowerStamp.Verify(CancellationToken.None);
                Tests.Check(!Directory.GetFiles(dir).Any(p => p.Contains(".partial") || p.Contains(".palette")), "clips gifs previews compatibility and cancellation preserve originals and clean owned intermediates");
                string mockDir = Path.Combine(dir, "mock-codec"); Directory.CreateDirectory(mockDir); string mockSource = Path.Combine(mockDir, "mock.cs"), mockExe = Path.Combine(mockDir, "ffmpeg.exe");
                File.WriteAllText(mockSource, "using System; class Mock { static int Main(string[] args) { if(Array.IndexOf(args,\"-encoders\")>=0) { Console.WriteLine(\" V..... png\"); return 0; } return 1; } }");
                tools.Run(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe", new[] { "/nologo", "/target:exe", "/out:" + mockExe, mockSource }, CancellationToken.None);
                try
                {
                    Environment.SetEnvironmentVariable("THOR_FFMPEG", mockExe);
                    Tests.Check(VideoExporter.CheckCapabilitiesAsync(clip, CancellationToken.None).Result.Code == "FFPROBE_MISSING" && VideoExporter.ExportAsync(clip, Path.Combine(dir, "no-probe.mp4"), null, CancellationToken.None).Result.Code == "FFPROBE_MISSING", "missing companion probe disables export before source decoding");
                    File.Copy(mockExe, Path.Combine(mockDir, "ffprobe.exe"));
                    Tests.Check(VideoExporter.CheckCapabilitiesAsync(clip, CancellationToken.None).Result.Code == "ENCODER_MISSING_LIBX264" && VideoExporter.ExportAsync(clip, Path.Combine(dir, "no-encoder.mp4"), null, CancellationToken.None).Result.Code == "ENCODER_MISSING_LIBX264" && !File.Exists(Path.Combine(dir, "no-encoder.mp4")), "missing required encoder is typed unsupported before expensive source hashing or render jobs");
                }
                finally { Environment.SetEnvironmentVariable("THOR_FFMPEG", ffmpeg); }
            }
        }
    }
}
