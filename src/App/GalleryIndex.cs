using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ThorCapture
{
    // One file in the capture folder, as indexed off the UI thread. Immutable once published.
    internal sealed class IndexEntry
    {
        public string Path, Name, DateText;
        public DateTime Time, WriteUtc;
        public long Size;
        public bool IsVideo, Paired;
        public int Screen;
        public CaptureMetadata Metadata;
    }

    // Builds snapshots of the capture folder on a worker thread. Sidecar metadata is cached
    // by sidecar write time, so a rescan only re-reads sidecars that changed. Searching and
    // filtering work on the published snapshot in memory; they never touch the disk.
    internal sealed class GalleryIndex
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, KeyValuePair<DateTime, CaptureMetadata>> sidecars =
            new Dictionary<string, KeyValuePair<DateTime, CaptureMetadata>>(StringComparer.OrdinalIgnoreCase);

        internal static int ScreenOf(string name)
        {
            if (name.IndexOf("-upper-", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (name.IndexOf("-lower-", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return -1;
        }

        internal static string PeerName(string name)
        {
            int marker = name.IndexOf("-lower-", StringComparison.OrdinalIgnoreCase);
            string replacement = "-upper-";
            if (marker < 0) { marker = name.IndexOf("-upper-", StringComparison.OrdinalIgnoreCase); replacement = "-lower-"; }
            return marker < 0 ? null : name.Substring(0, marker) + replacement + name.Substring(marker + 7);
        }

        // Worker thread. Scans are serialised so the sidecar cache is never shared concurrently.
        public List<IndexEntry> Scan(string folder, out string error)
        { return Scan(folder,out error,CancellationToken.None); }

        public List<IndexEntry> Scan(string folder, out string error, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            error = null;
            List<IndexEntry> entries = new List<IndexEntry>();
            lock (gate)
            {
                List<FileInfo> files = new List<FileInfo>();
                Dictionary<string, FileInfo> meta = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    DirectoryInfo dir = new DirectoryInfo(folder);
                    if (dir.Exists)
                    {
                        files.AddRange(dir.GetFiles("*.png"));
                        token.ThrowIfCancellationRequested();
                        files.AddRange(dir.GetFiles("*.mp4"));
                        token.ThrowIfCancellationRequested();
                        foreach (FileInfo sidecar in dir.GetFiles("*.thor-meta.txt")) { token.ThrowIfCancellationRequested(); meta[sidecar.Name] = sidecar; }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception failure) { error = failure.Message; }
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FileInfo file in files) names.Add(file.Name);
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FileInfo file in files)
                {
                    token.ThrowIfCancellationRequested();
                    string peer = PeerName(file.Name);
                    IndexEntry entry = new IndexEntry
                    {
                        Path = file.FullName,
                        Name = file.Name,
                        Time = file.LastWriteTime,
                        WriteUtc = file.LastWriteTimeUtc,
                        DateText = file.LastWriteTime.ToString("yyyyMMdd HH:mm"),
                        Size = file.Length,
                        IsVideo = file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase),
                        Screen = ScreenOf(file.Name),
                        Paired = peer != null && names.Contains(peer)
                    };
                    FileInfo sidecar;
                    if (meta.TryGetValue(file.Name + ".thor-meta.txt", out sidecar))
                    {
                        seen.Add(sidecar.FullName);
                        KeyValuePair<DateTime, CaptureMetadata> cached;
                        if (sidecars.TryGetValue(sidecar.FullName, out cached) && cached.Key == sidecar.LastWriteTimeUtc) entry.Metadata = cached.Value;
                        else
                        {
                            entry.Metadata = CaptureMetadata.Load(file.FullName);
                            sidecars[sidecar.FullName] = new KeyValuePair<DateTime, CaptureMetadata>(sidecar.LastWriteTimeUtc, entry.Metadata);
                        }
                    }
                    entries.Add(entry);
                }
                List<string> stale = new List<string>();
                foreach (string key in sidecars.Keys) if (!seen.Contains(key)) stale.Add(key);
                foreach (string key in stale) sidecars.Remove(key);
            }
            entries.Sort(delegate(IndexEntry a, IndexEntry b) { return b.Time.CompareTo(a.Time); });
            return entries;
        }

        // UI thread; in memory only. filter: 0 all, 1 upper, 2 lower, 3 videos.
        public static List<IndexEntry> Filter(List<IndexEntry> all, int filter, string query)
        {
            List<IndexEntry> result = new List<IndexEntry>();
            foreach (IndexEntry entry in all)
            {
                if (filter == 1 && entry.Screen != 0) continue;
                if (filter == 2 && entry.Screen != 1) continue;
                if (filter == 3 && !entry.IsVideo) continue;
                if (query.Length > 0 && entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                    && entry.DateText.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                    && (entry.Metadata == null || !entry.Metadata.Matches(query))) continue;
                result.Add(entry);
            }
            return result;
        }
    }
}
