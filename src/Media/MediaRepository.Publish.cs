using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Media
{
    /// <summary>Fixed bytes only; caller arrays are copied and never exposed.</summary>
    public sealed class PublishedPngInput
    {
        public readonly string RelativeName;
        public readonly ScreenRole Role;
        internal readonly byte[] Png, Metadata, Timeline;
        public PublishedPngInput(string relativeName, ScreenRole role, byte[] pngBytes, byte[] metadataBytes = null, byte[] inputTimelineBytes = null)
        {
            if (pngBytes == null || pngBytes.Length == 0 || pngBytes.Length > 32 * 1024 * 1024 || (metadataBytes != null && metadataBytes.Length > 65536) || (inputTimelineBytes != null && inputTimelineBytes.Length > 4 * 1024 * 1024)) throw new ArgumentException("Publication byte limit");
            RelativeName = relativeName; Role = role; Png = (byte[])pngBytes.Clone(); Metadata = metadataBytes == null ? null : (byte[])metadataBytes.Clone(); Timeline = inputTimelineBytes == null ? null : (byte[])inputTimelineBytes.Clone();
        }
    }
    public sealed partial class MediaRepository
    {
        sealed class PublicationFile { internal string Name, Hash; internal long Length; internal byte[] Bytes; }
        sealed class Publication { internal string Id, Phase; internal MediaGroup Group; internal List<PublicationFile> Files = new List<PublicationFile>(); }
        static readonly uint[] publicationCrcTable = PublicationCrcTable();
        static uint[] PublicationCrcTable() { uint[] table = new uint[256]; for (uint i=0;i<256;i++) { uint value=i; for(int bit=0;bit<8;bit++) value=(value&1)!=0?(value>>1)^0xedb88320:value>>1; table[i]=value; } return table; }
        static bool PublicationLeaf(string name)
        { return !String.IsNullOrWhiteSpace(name) && name.Length <= 180 && Path.GetFileName(name) == name && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith(".") && !name.EndsWith(" ") && !System.Text.RegularExpressions.Regex.IsMatch(name, @"\A(con|prn|aux|nul|com[1-9]|lpt[1-9])(\.|\z)", System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
        static string PublicationHash(byte[] bytes) { using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""); }
        static string PublicationHashFile(string path) { using (FileStream file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", ""); }
        static void PublicationPng(byte[] bytes)
        {
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 }, end = { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 };
            if (bytes.Length < 45 || !bytes.Take(8).SequenceEqual(signature) || !bytes.Skip(bytes.Length - 12).SequenceEqual(end)) throw new ArgumentException("Incomplete PNG");
            int offset = 8; bool idat = false;
            while (offset < bytes.Length)
            {
                long length = ((long)bytes[offset] << 24) | ((long)bytes[offset + 1] << 16) | ((long)bytes[offset + 2] << 8) | bytes[offset + 3];
                if (length > bytes.Length - offset - 12 || (offset == 8 && (length != 13 || Encoding.ASCII.GetString(bytes, offset + 4, 4) != "IHDR"))) throw new ArgumentException("PNG chunk");
                uint crc = 0xffffffff; for (int i = offset + 4; i < offset + 8 + length; i++) crc = publicationCrcTable[(crc ^ bytes[i]) & 255] ^ (crc >> 8); crc ^= 0xffffffff;
                int tail = offset + 8 + (int)length; uint expected = ((uint)bytes[tail] << 24) | ((uint)bytes[tail + 1] << 16) | ((uint)bytes[tail + 2] << 8) | bytes[tail + 3]; if (crc != expected) throw new ArgumentException("PNG checksum");
                string type = Encoding.ASCII.GetString(bytes, offset + 4, 4); if (type == "IDAT") idat = true; if (type == "IEND" && (length != 0 || offset + 12 != bytes.Length)) throw new ArgumentException("PNG end"); offset += (int)length + 12; if (offset < bytes.Length && bytes.Length - offset < 12) throw new ArgumentException("PNG chunk bounds");
            }
            if (!idat) throw new ArgumentException("PNG image data");
            // Read IHDR dimensions before invoking GDI+ to bound decoder allocation.
            long width = ((long)bytes[16] << 24) | ((long)bytes[17] << 16) | ((long)bytes[18] << 8) | bytes[19], height = ((long)bytes[20] << 24) | ((long)bytes[21] << 16) | ((long)bytes[22] << 8) | bytes[23];
            if (width < 1 || height < 1 || width > 8192 || height > 8192 || width * height > 24000000) throw new ArgumentException("PNG dimensions");
            using (MemoryStream stream = new MemoryStream(bytes, false)) using (Image image = Image.FromStream(stream, true, true)) { if (image.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Png.Guid || image.Width != width || image.Height != height) throw new ArgumentException("PNG format"); }
        }
        string PublicationJournal(string id) { string path = Path.Combine(dataRoot, "transactions", MediaCodec.Id(id) + ".thor-publish"); CheckPlainPath(path); CheckPlainPath(path + ".bak"); return path; }
        string PublicationTemp(Publication publication, PublicationFile file) { return Path.Combine(mediaRoot, ".publish-" + publication.Id + "-" + publication.Files.IndexOf(file).ToString(CultureInfo.InvariantCulture) + ".partial"); }
        static string PublicationEncode(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); }
        static string PublicationDecode(string value) { return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value)); }
        void WritePublication(Publication p)
        {
            StringBuilder body = new StringBuilder("thor.publish/1\n"); body.Append(repositoryId).Append('\n').Append(p.Id).Append('\n').Append(p.Phase).Append('\n').Append(PublicationEncode(MediaCodec.Encode(p.Group))).Append('\n');
            foreach (PublicationFile f in p.Files) body.Append(PublicationEncode(f.Name)).Append('|').Append(f.Hash).Append('|').Append(f.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
            string target = PublicationJournal(p.Id); Directory.CreateDirectory(Path.GetDirectoryName(target)); string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (FileStream file = File.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) using (StreamWriter writer = new StreamWriter(file, new UTF8Encoding(false))) { writer.Write(body.ToString() + "sha256=" + Digest(body.ToString()) + "\n"); writer.Flush(); file.Flush(true); }
            if (File.Exists(target)) File.Replace(temp, target, target + ".bak"); else File.Move(temp, target);
        }
        Publication ReadPublication(string id)
        {
            string path = PublicationJournal(id); FileInfo info = new FileInfo(path); if (info.Length > 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            string text = File.ReadAllText(path, new UTF8Encoding(false, true)); int marker = text.LastIndexOf("sha256=", StringComparison.Ordinal); if (marker < 0 || text.Substring(marker) != "sha256=" + Digest(text.Substring(0, marker)) + "\n") throw new InvalidDataException();
            string[] lines = text.Substring(0, marker).Split('\n'); if (lines.Length < 7 || lines.Length > 12 || lines[0] != "thor.publish/1" || lines[1] != repositoryId || lines[2] != MediaCodec.Id(id) || !new[] { "prepared", "staged", "reserved", "publishing", "committed" }.Contains(lines[3])) throw new InvalidDataException();
            Publication p = new Publication { Id = lines[2], Phase = lines[3], Group = MediaCodec.Decode(PublicationDecode(lines[4])) };
            if (p.Group.LegacyKey != "publish:" + p.Id || p.Group.Tracks.Count < 1 || p.Group.Tracks.Count > 2) throw new InvalidDataException();
            for (int i = 5; i < lines.Length - 1; i++) { string[] parts = lines[i].Split('|'); long length; if (parts.Length != 3 || !PublicationLeaf(PublicationDecode(parts[0])) || !System.Text.RegularExpressions.Regex.IsMatch(parts[1], "^[A-F0-9]{64}$") || !Int64.TryParse(parts[2], out length) || length < 0 || length > 32 * 1024 * 1024) throw new InvalidDataException(); p.Files.Add(new PublicationFile { Name = PublicationDecode(parts[0]), Hash = parts[1], Length = length }); }
            List<string> legal = p.Group.Tracks.SelectMany(t => new[] { t.RelativePath, t.RelativePath + ".thor-meta.txt", t.RelativePath + ".input.csv" }).ToList();
            if (p.Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Files.Count || p.Files.Any(f => !legal.Contains(f.Name)) || p.Group.Tracks.Any(t => t.Kind != MediaKind.Image || !PublicationLeaf(t.RelativePath) || !t.RelativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !p.Files.Any(f => f.Name == t.RelativePath && f.Hash == t.Sha256 && f.Length == t.Length))) throw new InvalidDataException();
            return p;
        }
        bool PublicationMatches(string path, PublicationFile file)
        { FileInfo info = new FileInfo(path); return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0 && info.Length == file.Length && PublicationHashFile(path) == file.Hash; }
        /// <summary>Stages one or two PNGs and fixed sidecars before publishing under the repository lock.</summary>
        public Task<MediaRepositoryResult> PublishGroupAsync(string expectedRepositoryId, IEnumerable<PublishedPngInput> inputs, CancellationToken token)
        { return PublishGroupAsync(expectedRepositoryId, inputs, token, null); }
        /// <summary>Progress counts completed primary PNG publications; never supplies paths or write callbacks.</summary>
        public Task<MediaRepositoryResult> PublishGroupAsync(string expectedRepositoryId, IEnumerable<PublishedPngInput> inputs, CancellationToken token, IProgress<int> progress)
        {
            PublishedPngInput[] frozen = inputs == null ? new PublishedPngInput[0] : inputs.Take(3).ToArray();
            return Run(t =>
            {
                if (expectedRepositoryId != repositoryId) return Result(MediaOutcome.Conflict, "PUBLICATION_ROOT_CHANGED", "媒体库已切换", null);
                if (!Directory.Exists(mediaRoot) || (File.GetAttributes(mediaRoot) & FileAttributes.ReparsePoint) != 0) return Result(MediaOutcome.Invalid, "PUBLICATION_ROOT_UNAVAILABLE", "图库目录无效", null);
                if (frozen.Length < 1 || frozen.Length > 2 || frozen.Any(x => x == null || !PublicationLeaf(x.RelativeName) || !x.RelativeName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !Enum.IsDefined(typeof(ScreenRole), x.Role)) || frozen.Select(x => x.RelativeName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != frozen.Length || (frozen.Length == 2 && (frozen.Select(x => x.Role).Distinct().Count() != 2 || frozen.Any(x => x.Role == ScreenRole.Unknown)))) return Result(MediaOutcome.Invalid, "INVALID_PUBLICATION", "截图轨道参数无效", null);
                List<MediaIssue> issues = new List<MediaIssue>(); List<MediaGroup> groups = Load(issues, t); Publication p = new Publication { Id = Guid.NewGuid().ToString("N"), Phase = "prepared" }; List<MediaTrack> tracks = new List<MediaTrack>(); DateTime now = DateTime.UtcNow;
                foreach (PublishedPngInput input in frozen)
                {
                    PublicationPng(input.Png); string hash = PublicationHash(input.Png); tracks.Add(new MediaTrack(Guid.NewGuid().ToString("N"), input.RelativeName, hash, "", "", input.Role, MediaKind.Image, TrackState.Missing, input.Png.Length, now));
                    p.Files.Add(new PublicationFile { Name = input.RelativeName, Hash = hash, Length = input.Png.Length, Bytes = input.Png });
                    if (input.Metadata != null) p.Files.Add(new PublicationFile { Name = input.RelativeName + ".thor-meta.txt", Hash = PublicationHash(input.Metadata), Length = input.Metadata.Length, Bytes = input.Metadata });
                    if (input.Timeline != null) p.Files.Add(new PublicationFile { Name = input.RelativeName + ".input.csv", Hash = PublicationHash(input.Timeline), Length = input.Timeline.Length, Bytes = input.Timeline });
                }
                p.Group = new MediaGroup(Guid.NewGuid().ToString("N"), 1, now, "publish:" + p.Id, tracks, null, false, false, new string[0]);
                if (p.Files.Any(f => File.Exists(Path.Combine(mediaRoot, f.Name)) || Directory.Exists(Path.Combine(mediaRoot, f.Name))) || groups.SelectMany(g => g.Tracks).Any(track => p.Files.Any(f => String.Equals(f.Name, track.RelativePath, StringComparison.OrdinalIgnoreCase)))) return Result(MediaOutcome.Conflict, "PUBLICATION_NAME_EXISTS", "目标名称已存在，原文件未覆盖", null);
                WritePublication(p);
                return ApplyPublication(p, groups, issues, t, progress);
            }, token);
        }
        public Task<MediaRepositoryResult> RecoverPublicationAsync(string transactionId, CancellationToken token)
        {
            return Run(t => { Publication p; try { p = ReadPublication(transactionId); } catch { return Result(MediaOutcome.Conflict, "PUBLICATION_JOURNAL_INVALID", "恢复日志无效，文件保留", null); } List<MediaIssue> issues = new List<MediaIssue>(); return ApplyPublication(p, Load(issues, t), issues, t, null); }, token);
        }
        MediaRepositoryResult ApplyPublication(Publication p, List<MediaGroup> groups, List<MediaIssue> issues, CancellationToken token, IProgress<int> progress)
        {
            MediaOutcome outcome = MediaOutcome.Success; string code = "OK"; bool reserved = groups.Any(g => g.GroupId == p.Group.GroupId); int count = 0;
            try
            {
                if ((File.GetAttributes(mediaRoot) & FileAttributes.ReparsePoint) != 0 || (Directory.Exists(Path.Combine(dataRoot, "transactions")) && (File.GetAttributes(Path.Combine(dataRoot, "transactions")) & FileAttributes.ReparsePoint) != 0)) throw new InvalidDataException();
                if (reserved && !groups.Single(g => g.GroupId == p.Group.GroupId).Tracks.Select(x => x.TrackId).SequenceEqual(p.Group.Tracks.Select(x => x.TrackId))) throw new InvalidDataException();
                if (groups.Any(g => g.GroupId != p.Group.GroupId && g.Tracks.Any(t => p.Group.Tracks.Any(x => String.Equals(x.RelativePath, t.RelativePath, StringComparison.OrdinalIgnoreCase))))) throw new InvalidDataException();
                // Stage all bytes first. Until a durable reservation exists, final names are never touched.
                foreach (PublicationFile f in p.Files)
                {
                    token.ThrowIfCancellationRequested(); string final = Path.Combine(mediaRoot, f.Name), temp = PublicationTemp(p, f);
                    if (File.Exists(final)) { if (!reserved || !PublicationMatches(final, f)) throw new InvalidDataException(); continue; }
                    if (File.Exists(temp)) { if (!PublicationMatches(temp, f)) throw new InvalidDataException(); }
                    else { if (f.Bytes == null) throw new EndOfStreamException(); using (FileStream file = File.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(f.Bytes, 0, f.Bytes.Length); file.Flush(true); } }
                }
                token.ThrowIfCancellationRequested(); p.Phase = "staged"; WritePublication(p);
                if (!reserved) { Store(p.Group); groups.Add(p.Group); reserved = true; } p.Phase = "reserved"; WritePublication(p);
                // Sidecars are published before their PNG, so watchers see a complete metadata set.
                foreach (MediaTrack track in p.Group.Tracks)
                {
                    foreach (PublicationFile f in p.Files.Where(f => f.Name == track.RelativePath + ".thor-meta.txt" || f.Name == track.RelativePath + ".input.csv").Concat(p.Files.Where(f => f.Name == track.RelativePath)))
                    {
                        token.ThrowIfCancellationRequested(); string final = Path.Combine(mediaRoot, f.Name), temp = PublicationTemp(p, f);
                        p.Phase = "publishing"; WritePublication(p);
                        if (File.Exists(final)) { if (!PublicationMatches(final, f) || File.Exists(temp)) throw new InvalidDataException(); } else File.Move(temp, final);
                        WritePublication(p);
                    }
                    count++; if (progress != null) progress.Report(count);
                }
                token.ThrowIfCancellationRequested(); p.Phase = "committed";
            }
            catch (OperationCanceledException) { outcome = MediaOutcome.Cancelled; code = "PUBLICATION_CANCELLED"; }
            catch (EndOfStreamException) { outcome = MediaOutcome.Conflict; code = "PUBLICATION_INCOMPLETE"; }
            catch (InvalidDataException) { outcome = MediaOutcome.Conflict; code = "PUBLICATION_INTEGRITY_CONFLICT"; }
            catch { outcome = MediaOutcome.Failed; code = "PUBLICATION_WRITE_FAILED"; }
            MediaGroup actual = null;
            if (reserved)
            {
                try
                {
                    MediaGroup current = groups.Single(g => g.GroupId == p.Group.GroupId); List<MediaTrack> actualTracks = new List<MediaTrack>(); int present = 0;
                    foreach (MediaTrack planned in p.Group.Tracks)
                    {
                        PublicationFile file = p.Files.Single(f => f.Name == planned.RelativePath); string final = Path.Combine(mediaRoot, file.Name);
                        if (PublicationMatches(final, file) && !File.Exists(PublicationTemp(p, file))) { actualTracks.Add(MediaSource.Read(final, CancellationToken.None).Track(planned.TrackId, planned.Role, false)); present++; } else actualTracks.Add(planned.Status(TrackState.Missing));
                    }
                    MediaGroup updated = current.Replace(actualTracks, current.Game, current.GameMissing, current.Favorite, current.Tags); Store(updated); groups[groups.IndexOf(current)] = updated; if (present > 0) actual = updated;
                    WritePublication(p); Publish(groups, issues, actual);
                }
                catch { outcome = MediaOutcome.Failed; code = "PUBLICATION_INDEX_PENDING"; actual = null; }
            }
            return Result(outcome, code, outcome == MediaOutcome.Success ? "" : "已完成的文件与恢复日志保留，未覆盖原文件", actual);
        }
    }
}
