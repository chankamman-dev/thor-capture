using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using ThorCapture.Media;

namespace ThorCapture.Features.VideoExport
{
    internal enum VideoLayout { Single, Vertical, Horizontal, Framed }
    internal enum VideoFormat { Mp4, WebM, Gif }
    internal enum EndPolicy { Shortest, Freeze, Blank }
    internal enum VideoEncoding { AccurateTranscode, KeyframeCopy }
    internal enum VideoExportOutcome { Success, Cancelled, Unsupported, InvalidPlan, AlreadyExists, SourceChanged, FileFailed, TimedOut }
    internal sealed class ExportTrack
    {
        public readonly string Path;
        public readonly ScreenRole Role;
        public readonly TimeSpan Offset;
        public ExportTrack(string path, ScreenRole role, TimeSpan offset)
        {
            if (String.IsNullOrWhiteSpace(path) || !Enum.IsDefined(typeof(ScreenRole), role) || Math.Abs(offset.TotalSeconds) > 3600) throw new ArgumentException("Invalid export track");
            Path = System.IO.Path.GetFullPath(path); Role = role; Offset = offset;
        }
        // Positive offset means this track begins later in the shared timeline.
        public TimeSpan SourceTime(TimeSpan globalTime) { return globalTime - Offset; }
    }
    internal sealed class ExportPlan
    {
        readonly DeviceSkin skin;
        public readonly ReadOnlyCollection<ExportTrack> Tracks;
        public readonly TimeSpan Start, Duration;
        public readonly VideoLayout Layout;
        public readonly VideoFormat Format;
        public readonly VideoEncoding Encoding;
        public readonly EndPolicy End;
        public readonly Size OutputSize;
        public readonly int Fps, BitRate, PaletteColors;
        public readonly double InputOffsetMs;
        public readonly string TimelinePath;
        public readonly bool ShowTouches, LegacyCompatible;
        public DeviceSkin Skin { get { return ImageExportPlan.CopySkin(skin); } }
        public ExportPlan(IEnumerable<ExportTrack> tracks, TimeSpan start, TimeSpan duration, VideoLayout layout,
            VideoFormat format, Size size, int fps, int bitRate, DeviceSkin skin, EndPolicy end = EndPolicy.Shortest,
            double inputOffsetMs = 0, string timelinePath = null, bool showTouches = true, int paletteColors = 256,
            VideoEncoding encoding = VideoEncoding.AccurateTranscode)
            : this(tracks, start, duration, layout, format, size, fps, bitRate, skin, end, inputOffsetMs, timelinePath, showTouches, paletteColors, encoding, false) { }
        internal ExportPlan(IEnumerable<ExportTrack> tracks, TimeSpan start, TimeSpan duration, VideoLayout layout,
            VideoFormat format, Size size, int fps, int bitRate, DeviceSkin skin, EndPolicy end,
            double inputOffsetMs, string timelinePath, bool showTouches, int paletteColors, VideoEncoding encoding, bool legacy)
        {
            if (tracks == null || skin == null) throw new ArgumentNullException();
            ExportTrack[] copy = tracks.ToArray();
            if (copy.Length < 1 || copy.Length > 2 || copy.Any(t => t == null) || copy.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != copy.Length || copy.Select(t => t.Role).Distinct().Count() != copy.Length) throw new ArgumentException("Invalid tracks");
            if (!Enum.IsDefined(typeof(VideoLayout), layout) || !Enum.IsDefined(typeof(VideoFormat), format) || !Enum.IsDefined(typeof(EndPolicy), end) || !Enum.IsDefined(typeof(VideoEncoding), encoding)) throw new ArgumentException("Invalid options");
            if (start.TotalSeconds < 0 || start.TotalHours > 24 || duration.TotalSeconds < 0 || duration.TotalMinutes > 30 || size.Width < 16 || size.Height < 16 || size.Width > 4096 || size.Height > 4096 || (long)size.Width * size.Height > 4 * 1024 * 1024 || fps < 1 || fps > 120 || bitRate < 64000 || bitRate > 100000000 || paletteColors < 16 || paletteColors > 256 || Double.IsNaN(inputOffsetMs) || Double.IsInfinity(inputOffsetMs) || Math.Abs(inputOffsetMs) > 3600000) throw new ArgumentException("Export exceeds limits");
            if (format != VideoFormat.Gif && (size.Width % 2 != 0 || size.Height % 2 != 0)) throw new ArgumentException("Video output dimensions must be even");
            if (layout == VideoLayout.Single && copy.Length != 1) throw new ArgumentException("Single layout needs one track");
            if (skin.Abxy != null && skin.Abxy.Length != 4 || Single.IsNaN(skin.GlowLevel) || Single.IsInfinity(skin.GlowLevel) || skin.GlowLevel < .1f || skin.GlowLevel > 1f) throw new ArgumentException("Invalid skin");
            Tracks = new ReadOnlyCollection<ExportTrack>(copy); Start = start; Duration = duration; Layout = layout; Format = format; OutputSize = size;
            Fps = fps; BitRate = bitRate; this.skin = ImageExportPlan.CopySkin(skin); End = end; InputOffsetMs = inputOffsetMs;
            TimelinePath = timelinePath == null ? null : System.IO.Path.GetFullPath(timelinePath); ShowTouches = showTouches; PaletteColors = paletteColors; Encoding = encoding; LegacyCompatible = legacy;
        }
        public string Extension { get { return Format == VideoFormat.Gif ? ".gif" : Format == VideoFormat.WebM ? ".webm" : ".mp4"; } }
        public string Describe()
        { return OutputSize.Width + "×" + OutputSize.Height + " · " + Fps + " fps · " + Format + " · " + Layout + " · " + (Duration == TimeSpan.Zero ? "至轨道结束" : Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " 秒") + " · " + (Encoding == VideoEncoding.KeyframeCopy ? "关键帧复制（本版不支持）" : "精确片段需转码") + (LegacyCompatible ? " · 旧版兼容" : ""); }
        public SizeEstimate Estimate()
        {
            double seconds = Duration == TimeSpan.Zero ? 1800 : Duration.TotalSeconds;
            long upper = Format == VideoFormat.Gif ? (long)Math.Min(Int64.MaxValue / 2.0, (double)OutputSize.Width * OutputSize.Height * Fps * seconds * 1.5 + 65536) : (long)(seconds * (BitRate + 192000) / 8 * 1.8 + 65536);
            long lower = Format == VideoFormat.Gif ? (long)(seconds * Fps * 64 + 1024) : (long)(seconds * BitRate / 8 * .3);
            return new SizeEstimate(lower, upper, Duration == TimeSpan.Zero);
        }
    }
    internal sealed class SizeEstimate
    {
        public readonly long MinimumBytes, MaximumBytes;
        public readonly bool DurationUnknown;
        internal SizeEstimate(long minimum, long maximum, bool unknown) { MinimumBytes = minimum; MaximumBytes = maximum; DurationUnknown = unknown; }
    }
    internal sealed class VideoExportResult
    {
        public readonly VideoExportOutcome Outcome;
        public readonly string Code, Destination;
        public readonly double Duration;
        public readonly long Bytes;
        internal VideoExportResult(VideoExportOutcome outcome, string code, string destination = null, double duration = 0, long bytes = 0)
        { Outcome = outcome; Code = code; Destination = destination; Duration = duration; Bytes = bytes; }
    }
    internal sealed class VideoPreviewResult : IDisposable
    {
        public readonly VideoExportOutcome Outcome;
        public readonly string Code;
        public Bitmap Frame { get; private set; }
        internal VideoPreviewResult(VideoExportOutcome outcome, string code, Bitmap frame = null) { Outcome = outcome; Code = code; Frame = frame; }
        public void Dispose() { if (Frame != null) Frame.Dispose(); Frame = null; }
    }
}
