using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Features.VideoExport
{
    internal enum ConflictPolicy { Skip, Rename, Fail }
    internal enum BatchItemOutcome { Success, Skipped, Unsupported, Failed, Cancelled, NotStarted }
    internal sealed class NameTemplate
    {
        public readonly string Pattern;
        public NameTemplate(string pattern)
        {
            if (String.IsNullOrWhiteSpace(pattern) || pattern.Length > 160 || pattern.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || pattern.Contains("..") || Regex.Replace(pattern, "\\{(name|index|id)\\}", "").IndexOfAny(new[] { '{', '}' }) >= 0) throw new ArgumentException("Invalid filename template");
            Pattern = pattern;
        }
        public string Expand(BatchJob job, int index)
        {
            string value = Pattern.Replace("{name}", Path.GetFileNameWithoutExtension(job.FirstPath)).Replace("{index}", (index + 1).ToString("D3", System.Globalization.CultureInfo.InvariantCulture)).Replace("{id}", job.SourceMediaId);
            foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_'); value = value.Trim().TrimEnd('.');
            if (value.Length > 120) value = value.Substring(0, 120).TrimEnd('.', ' ');
            if (value.Length == 0 || Regex.IsMatch(value, "\\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase)) value = "media-" + (index + 1);
            return value;
        }
    }
    internal sealed class BatchJob
    {
        public readonly string SourceMediaId, TopPath, BottomPath;
        public readonly ExportPlan Video;
        public readonly ImageExportPlan Image;
        public BatchJob(string id, ExportPlan plan)
        { if (plan == null || String.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException(); SourceMediaId = id; Video = plan; }
        public BatchJob(string id, ImageExportPlan plan, string top, string bottom)
        { if (plan == null || (top == null && bottom == null) || String.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException(); SourceMediaId = id; Image = plan; TopPath = top == null ? null : Path.GetFullPath(top); BottomPath = bottom == null ? null : Path.GetFullPath(bottom); }
        public string FirstPath { get { return Video != null ? Video.Tracks[0].Path : TopPath ?? BottomPath; } }
        public string Extension { get { return Video != null ? Video.Extension : Image.Format.Guid == ImageFormat.Png.Guid ? ".png" : ".jpg"; } }
    }
    internal sealed class BatchProgress
    {
        public readonly int Index, Count, Percent;
        public readonly bool Saved;
        internal BatchProgress(int index, int count, int percent, bool saved) { Index = index; Count = count; Percent = percent; Saved = saved; }
    }
    internal sealed class BatchItemResult
    {
        public readonly BatchJob Job;
        public readonly string Destination, Code;
        public readonly BatchItemOutcome Outcome;
        internal BatchItemResult(BatchJob job, string path, BatchItemOutcome outcome, string code) { Job = job; Destination = path; Outcome = outcome; Code = code; }
    }
    internal sealed class BatchReport
    {
        public readonly ReadOnlyCollection<BatchItemResult> Items;
        public readonly bool Cancelled;
        internal BatchReport(IEnumerable<BatchItemResult> items, bool cancelled) { Items = new ReadOnlyCollection<BatchItemResult>(items.ToList()); Cancelled = cancelled; }
        public ReadOnlyCollection<BatchJob> RetryableJobs { get { return new ReadOnlyCollection<BatchJob>(Items.Where(i => i.Outcome == BatchItemOutcome.Failed || i.Outcome == BatchItemOutcome.Cancelled || i.Outcome == BatchItemOutcome.NotStarted).Select(i => i.Job).ToList()); } }
    }
    internal sealed class InlineProgress : IProgress<int>
    {
        readonly Action<int> callback;
        internal InlineProgress(Action<int> callback) { this.callback = callback; }
        public void Report(int value) { callback(value); }
    }
    internal static class BatchExporter
    {
        static Bitmap ReadImage(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); SourceStamp stamp = SourceStamp.Read(path, token);
            if (stamp.Length > 64 * 1024 * 1024) throw new ExportFailure(VideoExportOutcome.InvalidPlan, "IMAGE_LIMIT_EXCEEDED");
            using (FileStream file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (Image image = System.Drawing.Image.FromStream(file, false, true))
            { if ((long)image.Width * image.Height > 16 * 1024 * 1024) throw new ExportFailure(VideoExportOutcome.InvalidPlan, "IMAGE_LIMIT_EXCEEDED"); return new Bitmap(image); }
        }
        static async Task<VideoExportResult> SaveImage(BatchJob job, string destination, IProgress<int> progress, CancellationToken token)
        {
            string temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial" + job.Extension;
            bool own = false;
            try
            {
                if (destination.Length + 60 >= 260) return new VideoExportResult(VideoExportOutcome.Unsupported, "LONG_PATH_UNSUPPORTED");
                List<SourceStamp> stamps = new List<SourceStamp>(); if (job.TopPath != null) stamps.Add(SourceStamp.Read(job.TopPath, token)); if (job.BottomPath != null) stamps.Add(SourceStamp.Read(job.BottomPath, token));
                using (Bitmap top = job.TopPath == null ? null : ReadImage(job.TopPath, token))
                using (Bitmap bottom = job.BottomPath == null ? null : ReadImage(job.BottomPath, token))
                {
                    ExportResult result = await ImageExporter.SaveAsync(job.Image, top, bottom, temp, token, new InlineProgress(value => { if (progress != null) progress.Report(Math.Min(95, value)); })).ConfigureAwait(false);
                    if (!result.Success) return new VideoExportResult(result.Outcome == ImageExportOutcome.Cancelled ? VideoExportOutcome.Cancelled : VideoExportOutcome.FileFailed, result.Reason);
                    own = true;
                    foreach (SourceStamp stamp in stamps) stamp.Verify(token); token.ThrowIfCancellationRequested(); File.Move(temp, destination);
                    return new VideoExportResult(VideoExportOutcome.Success, "OK", destination, 0, new FileInfo(destination).Length);
                }
            }
            catch (OperationCanceledException) { return new VideoExportResult(VideoExportOutcome.Cancelled, "CANCELLED"); }
            catch (ExportFailure error) { return new VideoExportResult(error.Outcome, error.Code); }
            catch (Exception) { return new VideoExportResult(VideoExportOutcome.FileFailed, "IMAGE_EXPORT_FAILED"); }
            finally { if (own) try { File.Delete(temp); } catch { } }
        }
        static void Notify(IProgress<BatchProgress> progress, int index, int count, int percent, bool saved)
        { if (progress != null) try { progress.Report(new BatchProgress(index, count, percent, saved)); } catch { } }
        public static Task<BatchReport> RunAsync(IList<BatchJob> jobs, string directory, NameTemplate template, ConflictPolicy conflicts, IProgress<BatchProgress> progress, CancellationToken token, Func<string, long> availableSpace = null)
        {
            if (jobs == null || template == null || jobs.Count > 1000 || jobs.Any(j => j == null) || !Enum.IsDefined(typeof(ConflictPolicy), conflicts)) throw new ArgumentException("Invalid batch");
            BatchJob[] copy = jobs.ToArray(); directory = Path.GetFullPath(directory);
            return Task.Run(async () =>
            {
                List<BatchItemResult> results = new List<BatchItemResult>(); bool cancelled = false; string failure = null;
                if (!token.IsCancellationRequested)
                {
                    string probe = Path.Combine(directory, ".thor-write-probe-" + Guid.NewGuid().ToString("N")); bool own = false;
                    try { ExportTools.SafePath(directory); Directory.CreateDirectory(directory); using (FileStream file = File.Open(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { own = true; file.WriteByte(0); file.Flush(true); } }
                    catch (Exception) { failure = "DIRECTORY_UNWRITABLE"; }
                    finally { if (own) try { File.Delete(probe); } catch { } }
                    if (failure == null) try {
                        long free = availableSpace == null ? new DriveInfo(Path.GetPathRoot(directory)).AvailableFreeSpace : availableSpace(directory);
                        long minimum = copy.Sum(job => job.Video == null ? 1024L : job.Video.Duration == TimeSpan.Zero ? 1024L : job.Video.Estimate().MinimumBytes);
                        if (free < minimum) failure = "DISK_SPACE_LOW";
                    } catch { failure = "DISK_SPACE_UNKNOWN"; }
                }
                for (int index = 0; index < copy.Length; index++)
                {
                    BatchJob job = copy[index]; string destination = Path.Combine(directory, template.Expand(job, index) + job.Extension);
                    if (cancelled || token.IsCancellationRequested) { cancelled = true; results.Add(new BatchItemResult(job, destination, BatchItemOutcome.NotStarted, "CANCELLED")); continue; }
                    if (failure != null) { results.Add(new BatchItemResult(job, destination, BatchItemOutcome.Failed, failure)); continue; }
                    if (File.Exists(destination))
                    {
                        if (conflicts != ConflictPolicy.Rename) { results.Add(new BatchItemResult(job, destination, conflicts == ConflictPolicy.Skip ? BatchItemOutcome.Skipped : BatchItemOutcome.Failed, "TARGET_EXISTS")); continue; }
                        string basis = Path.Combine(directory, template.Expand(job, index)); int suffix = 2;
                        while (File.Exists(destination) && suffix <= 10000) destination = basis + "-" + suffix++ + job.Extension;
                        if (File.Exists(destination)) { results.Add(new BatchItemResult(job, destination, BatchItemOutcome.Failed, "NAME_LIMIT_EXCEEDED")); continue; }
                    }
                    Notify(progress, index, copy.Length, 0, false);
                    IProgress<int> itemProgress = new InlineProgress(value => Notify(progress, index, copy.Length, Math.Min(99, value), false));
                    VideoExportResult result = job.Video == null ? await SaveImage(job, destination, itemProgress, token).ConfigureAwait(false) : await VideoExporter.ExportAsync(job.Video, destination, itemProgress, token).ConfigureAwait(false);
                    BatchItemOutcome outcome = result.Outcome == VideoExportOutcome.Success ? BatchItemOutcome.Success : result.Outcome == VideoExportOutcome.Unsupported ? BatchItemOutcome.Unsupported : result.Outcome == VideoExportOutcome.Cancelled ? BatchItemOutcome.Cancelled : BatchItemOutcome.Failed;
                    results.Add(new BatchItemResult(job, destination, outcome, result.Code));
                    if (outcome == BatchItemOutcome.Success) Notify(progress, index, copy.Length, 100, true);
                    if (outcome == BatchItemOutcome.Cancelled) cancelled = true;
                }
                return new BatchReport(results, cancelled);
            });
        }
    }
}
