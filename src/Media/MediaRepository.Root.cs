using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Security.Cryptography;

namespace ThorCapture.Media
{
    internal sealed class MediaRootConflict : Exception
    {
        internal readonly string Code;
        internal MediaRootConflict(string code) { Code = code; }
    }
    public sealed partial class MediaRepository
    {
        sealed class RootBinding { internal string Id, Root; }
        string BindingPath { get { return Path.Combine(dataRoot, "repository.thor-root"); } }
        static string Digest(string text)
        { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", ""); }
        static RootBinding ReadBinding(string path)
        {
            FileInfo file = new FileInfo(path);
            if (file.Length > 16384 || (file.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Invalid repository binding");
            string[] lines = File.ReadAllText(path, new UTF8Encoding(false, true)).Split('\n');
            if (lines.Length != 4 || lines[0] != "thor.media-repository/1" || Digest(String.Join("\n", lines, 0, 3)) != lines[3]) throw new InvalidDataException("Invalid repository binding");
            string root = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(lines[2]));
            if (!Path.IsPathRooted(root) || Root(root) != root) throw new InvalidDataException("Invalid repository root");
            return new RootBinding { Id = MediaCodec.Id(lines[1]), Root = root };
        }
        void WriteBinding(RootBinding binding)
        {
            string body = "thor.media-repository/1\n" + binding.Id + "\n" + Convert.ToBase64String(Encoding.UTF8.GetBytes(binding.Root));
            string temp = BindingPath + "." + Guid.NewGuid().ToString("N") + ".tmp"; bool own = false;
            try
            {
                using (FileStream file = File.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (StreamWriter writer = new StreamWriter(file, new UTF8Encoding(false))) { own = true; writer.Write(body + "\n" + Digest(body)); writer.Flush(); file.Flush(true); }
                ReadBinding(temp);
                if (File.Exists(BindingPath)) File.Replace(temp, BindingPath, BindingPath + ".bak"); else File.Move(temp, BindingPath);
            }
            finally { if (own && File.Exists(temp)) File.Delete(temp); }
        }
        void EnsureRoot()
        {
            RootBinding binding;
            if (File.Exists(BindingPath)) binding = ReadBinding(BindingPath);
            else if (File.Exists(BindingPath + ".bak"))
            {
                binding = ReadBinding(BindingPath + ".bak");
                if (!String.Equals(binding.Root, mediaRoot, StringComparison.OrdinalIgnoreCase)) throw new MediaRootConflict("ROOT_MISMATCH");
                WriteBinding(binding);
            }
            else
            {
                if (Directory.GetFiles(dataRoot, "group-*.thor-group*").Length != 0) throw new MediaRootConflict("ROOT_BINDING_MISSING");
                binding = new RootBinding { Id = Guid.NewGuid().ToString("N"), Root = mediaRoot }; WriteBinding(binding);
            }
            if (!String.Equals(binding.Root, mediaRoot, StringComparison.OrdinalIgnoreCase)) throw new MediaRootConflict("ROOT_MISMATCH");
            repositoryId = binding.Id;
        }
        /// <summary>Explicitly verifies surviving sources before rebinding an entire library. Never moves media files.</summary>
        public Task<MediaRepositoryResult> RebindRootAsync(CancellationToken token)
        {
            return Run(cancel =>
            {
                if (!Directory.Exists(mediaRoot)) return Result(MediaOutcome.Invalid, "ROOT_UNAVAILABLE", "新图库目录不存在", null);
                RootBinding previous = File.Exists(BindingPath) ? ReadBinding(BindingPath) : File.Exists(BindingPath + ".bak") ? ReadBinding(BindingPath + ".bak") : null;
                List<MediaIssue> issues = new List<MediaIssue>(); List<MediaGroup> groups = Load(issues, cancel);
                if (issues.Count != 0) return Result(MediaOutcome.Conflict, "RECORDS_NEED_REVIEW", "请先审阅媒体记录恢复结果，再重定位", null);
                int verified = 0;
                foreach (MediaGroup group in groups) foreach (MediaTrack track in group.Tracks)
                {
                    cancel.ThrowIfCancellationRequested(); string path = SafePath(Path.Combine(mediaRoot, track.RelativePath));
                    if (!File.Exists(path)) continue;
                    MediaSource source = MediaSource.Read(path, cancel);
                    if (source.Sha256 != track.Sha256) return Result(MediaOutcome.Conflict, "ROOT_SOURCE_MISMATCH", "新目录原片与保存的指纹不一致", null);
                    verified++;
                }
                if (groups.Count > 0 && verified == 0) return Result(MediaOutcome.Conflict, "ROOT_UNVERIFIED", "新目录没有可验证的原片", null);
                cancel.ThrowIfCancellationRequested();
                RootBinding binding = new RootBinding { Id = previous == null ? Guid.NewGuid().ToString("N") : previous.Id, Root = mediaRoot };
                WriteBinding(binding); repositoryId = binding.Id;
                return Refresh(null, cancel);
            }, token, true);
        }
    }
}
