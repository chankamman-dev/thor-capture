using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Media
{
    public sealed partial class MediaRepository : IMediaRepository
    {
        readonly object snapshotGate = new object();
        readonly SemaphoreSlim workGate = new SemaphoreSlim(1, 1);
        readonly string mediaRoot, dataRoot;
        readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        string repositoryId = "";
        MediaSnapshot snapshot = new MediaSnapshot("", 0, new MediaGroup[0], new MediaIssue[0]);
        bool disposed;
        public string MediaRoot { get { return mediaRoot; } }
        public string DataRoot { get { return dataRoot; } }
        /// <summary>Raised on a background thread after publishing an immutable snapshot. Marshal to the UI thread.</summary>
        public event EventHandler<MediaSnapshotEventArgs> Changed;
        public MediaRepository(string mediaDirectory, string repositoryDirectory)
        { mediaRoot = Root(mediaDirectory); dataRoot = Root(repositoryDirectory); if (String.Equals(mediaRoot, dataRoot, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Repository must have a separate directory"); }
        static string Root(string path) { string full = Path.GetFullPath(path); return full.Length == Path.GetPathRoot(full).Length ? full : full.TrimEnd(Path.DirectorySeparatorChar); }
        public MediaSnapshot GetSnapshot() { lock (snapshotGate) return snapshot; }
        public string ResolvePath(MediaTrack track) { if (track == null) throw new ArgumentNullException("track"); return SafePath(Path.Combine(mediaRoot, track.RelativePath)); }
        string SafePath(string path)
        {
            string full = Path.GetFullPath(path);
            if (!String.Equals(Path.GetDirectoryName(full), mediaRoot, StringComparison.OrdinalIgnoreCase) || full.IndexOf(':', 2) >= 0 || Path.GetFileName(full).Length > 255 || !MediaSource.Supported(full)) throw new ArgumentException("Media path outside the selected flat library");
            return full;
        }
        FileStream Acquire(CancellationToken token)
        {
            CheckPlainPath(mediaRoot); CheckPlainPath(dataRoot); CheckPlainPath(Path.Combine(dataRoot, "transactions"));
            Directory.CreateDirectory(dataRoot); Stopwatch time = Stopwatch.StartNew();
            for (;;)
            {
                token.ThrowIfCancellationRequested();
                try { return File.Open(Path.Combine(dataRoot, "repository.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { if (time.ElapsedMilliseconds >= 3000) throw new TimeoutException("Repository busy"); Thread.Sleep(25); }
            }
        }
        internal static void CheckPlainPath(string path)
        {
            for (string current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked repository path");
        }
        Task<MediaRepositoryResult> Run(Func<CancellationToken, MediaRepositoryResult> action, CancellationToken token, bool allowRebind = false)
        {
            CancellationToken linkedShutdown;
            lock (snapshotGate) { if (disposed) return Task.FromResult(Result(MediaOutcome.Failed, "REPOSITORY_CLOSED", "媒体库已关闭", null)); linkedShutdown = shutdown.Token; }
            return Task.Run(async () =>
            {
                MediaRepositoryResult result;
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, linkedShutdown))
                {
                    try
                    {
                        await workGate.WaitAsync(linked.Token);
                        try { linked.Token.ThrowIfCancellationRequested(); using (FileStream lease = Acquire(linked.Token)) { if (!allowRebind) EnsureRoot(); result = action(linked.Token); } }
                        finally { workGate.Release(); }
                    }
                    catch (OperationCanceledException) { return Result(MediaOutcome.Cancelled, "CANCELLED", "媒体操作已取消，已提交的单组变更仍可恢复", null); }
                    catch (TimeoutException) { return Result(MediaOutcome.TimedOut, "REPOSITORY_BUSY", "另一程序正在更新媒体库，请稍后重试", null); }
                    catch (MediaRootConflict ex) { return Result(MediaOutcome.Conflict, ex.Code, "图库目录与仓库绑定不一致，请使用独立仓库或显式验证重定位", null); }
                    catch (ArgumentException) { return Result(MediaOutcome.Invalid, "INVALID_REQUEST", "媒体参数或路径无效", null); }
                    catch (Exception ex) { Log.Warn("Media repository failure: " + ex.GetType().Name); return Result(MediaOutcome.Failed, "FILE_FAILURE", "媒体库读写失败，原片未修改", null); }
                }
                if (result.Outcome == MediaOutcome.Success)
                {
                    EventHandler<MediaSnapshotEventArgs> handlers = Changed;
                    if (handlers != null) foreach (EventHandler<MediaSnapshotEventArgs> handler in handlers.GetInvocationList())
                        try { handler(this, new MediaSnapshotEventArgs(result.Snapshot)); } catch { Log.Warn("Media snapshot subscriber failed"); }
                }
                return result;
            });
        }
        MediaRepositoryResult Result(MediaOutcome outcome, string code, string reason, MediaGroup group)
        { return new MediaRepositoryResult(outcome, code, reason, GetSnapshot(), group); }
        MediaRepositoryResult Publish(List<MediaGroup> groups, List<MediaIssue> issues, MediaGroup group)
        { lock (snapshotGate) { snapshot = new MediaSnapshot(repositoryId, snapshot.Generation + 1, groups, issues); } return Result(MediaOutcome.Success, "OK", "", group); }
        string GroupPath(string id) { return Path.Combine(dataRoot, "group-" + MediaCodec.Id(id) + ".thor-group"); }
        static MediaGroup Read(string path)
        { FileInfo file = new FileInfo(path); if (file.Length > 1024 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Media sidecar exceeds limit or is a reparse point"); return MediaCodec.Decode(File.ReadAllText(path, new UTF8Encoding(false, true))); }
        static string ReadHeader(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return "";
                using (StreamReader reader = new StreamReader(path))
                { StringBuilder header = new StringBuilder(); for (int i = 0; i < 64; i++) { int ch = reader.Read(); if (ch < 0 || ch == '\n') break; header.Append((char)ch); } return header.ToString(); }
            }
            catch { return ""; }
        }
        void Store(MediaGroup group)
        {
            string target = GroupPath(group.GroupId), temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp"; bool own = false;
            try
            {
                using (FileStream file = File.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (StreamWriter writer = new StreamWriter(file, new UTF8Encoding(false))) { own = true; writer.Write(MediaCodec.Encode(group)); writer.Flush(); file.Flush(true); }
                MediaGroup check = Read(temp); if (check.GroupId != group.GroupId || check.Revision != group.Revision) throw new InvalidDataException("Media write validation failed");
                if (File.Exists(target)) File.Replace(temp, target, target + ".bak"); else File.Move(temp, target);
            }
            finally { if (own && File.Exists(temp)) File.Delete(temp); }
        }
        List<MediaGroup> Load(List<MediaIssue> issues, CancellationToken token)
        {
            List<MediaGroup> groups = new List<MediaGroup>();
            string[] files = Directory.GetFiles(dataRoot, "group-*.thor-group");
            if (files.Length > 20000) throw new InvalidDataException("Media group limit exceeded");
            // Missing primaries can be recovered from the previous complete group revision.
            foreach (string backup in Directory.GetFiles(dataRoot, "group-*.thor-group.bak"))
            {
                string primary = backup.Substring(0, backup.Length - 4);
                if (!File.Exists(primary))
                {
                    token.ThrowIfCancellationRequested();
                    try { MediaGroup recover = Read(backup); if (primary != GroupPath(recover.GroupId)) throw new InvalidDataException(); Store(recover); issues.Add(new MediaIssue("SIDECAR_RECOVERED", recover.GroupId, "从上一完整版本恢复缺失记录")); }
                    catch (Exception ex) { if (ex is OperationCanceledException) throw; issues.Add(new MediaIssue("BACKUP_INVALID", Path.GetFileName(backup), "备份不可用，原片仍保留")); }
                }
            }
            foreach (string path in Directory.GetFiles(dataRoot, "group-*.thor-group"))
            {
                token.ThrowIfCancellationRequested();
                try { MediaGroup group = Read(path); if (!String.Equals(path, GroupPath(group.GroupId), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ID and filename mismatch"); groups.Add(group); }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException) throw;
                    if (ex is UnauthorizedAccessException || ex is IOException)
                    { issues.Add(new MediaIssue("SIDECAR_UNREADABLE", Path.GetFileName(path), "记录暂时无法读取，保留原处并停止自动适配")); continue; }
                    if (ex is OutOfMemoryException) throw;
                    string header = ReadHeader(path);
                    if (header.StartsWith("thor.media-group/", StringComparison.Ordinal) && header != MediaCodec.Schema) { issues.Add(new MediaIssue("SCHEMA_UNSUPPORTED", Path.GetFileName(path), "记录版本高于当前软件，保留且停止自动适配")); continue; }
                    try
                    {
                        MediaGroup previous = Read(path + ".bak"); if (!String.Equals(GroupPath(previous.GroupId), path, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                        Quarantine(path); Store(previous); groups.Add(previous); issues.Add(new MediaIssue("SIDECAR_RECOVERED", previous.GroupId, "损坏记录已隔离并从上一版本恢复"));
                    }
                    catch (Exception failure)
                    {
                        if (failure is OperationCanceledException) throw;
                        bool isolated = false; try { Quarantine(path); isolated = true; } catch { }
                        issues.Add(new MediaIssue(isolated ? "SIDECAR_CORRUPT" : "SIDECAR_UNREADABLE", Path.GetFileName(path), isolated ? "损坏记录无法恢复，已隔离；原片不变，可能需要人工找回关联" : "记录无法隔离，原处保留并停止自动适配"));
                    }
                }
            }
            HashSet<string> trackIds = new HashSet<string>();
            foreach (MediaGroup group in groups.ToArray())
                if (group.Tracks.Any(t => !trackIds.Add(t.TrackId))) { groups.Remove(group); issues.Add(new MediaIssue("TRACK_ID_CONFLICT", group.GroupId, "轨道ID冲突，未发布该组")); }
            return groups;
        }
        void Quarantine(string path)
        { string folder = Path.Combine(dataRoot, "quarantine"); Directory.CreateDirectory(folder); File.Move(path, Path.Combine(folder, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".bad")); }
        public Task<MediaRepositoryResult> RefreshAsync(CancellationToken token) { return Run(t => Refresh(null, t), token); }
        public Task<MediaRepositoryResult> ImportPathsAsync(IEnumerable<string> paths, CancellationToken token)
        { if (paths == null) throw new ArgumentNullException("paths"); string[] copy = paths.ToArray(); return Run(t => Refresh(copy, t), token); }
        MediaRepositoryResult Refresh(string[] requested, CancellationToken token)
        {
            List<MediaIssue> issues = new List<MediaIssue>(); List<MediaGroup> groups = Load(issues, token);
            if (issues.Any(i => i.Code == "SCHEMA_UNSUPPORTED" || i.Code == "SIDECAR_UNREADABLE")) { Publish(groups, issues, null); return Result(MediaOutcome.Invalid, "RECORDS_UNAVAILABLE", "存在未来版本或不可读记录，未自动导入未知素材", null); }
            if (!Directory.Exists(mediaRoot)) { issues.Add(new MediaIssue("MEDIA_ROOT_MISSING", "", "素材目录不存在，保留已知缺轨记录")); }
            string[] paths = requested == null ? Directory.Exists(mediaRoot) ? Directory.GetFiles(mediaRoot).Where(MediaSource.Supported).ToArray() : new string[0] : requested.Select(SafePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length > 20000) throw new InvalidDataException("Media scan limit exceeded");
            HashSet<string> matched = new HashSet<string>(); Dictionary<string, MediaGroup> originalGroups = groups.ToDictionary(g => g.GroupId);
            List<KeyValuePair<MediaGroup, MediaTrack>> known = groups.SelectMany(g => g.Tracks.Select(track => new KeyValuePair<MediaGroup, MediaTrack>(g, track))).ToList();
            Dictionary<string, List<KeyValuePair<MediaGroup, MediaTrack>>> byIdentity = known.Where(p => p.Value.FileIdentity.Length > 0).GroupBy(p => p.Value.FileIdentity).ToDictionary(g => g.Key, g => g.ToList());
            Dictionary<string, List<KeyValuePair<MediaGroup, MediaTrack>>> byPath = known.GroupBy(p => p.Value.RelativePath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<KeyValuePair<MediaGroup, MediaTrack>>> missingByHash = known.Where(p => !File.Exists(Path.Combine(mediaRoot, p.Value.RelativePath))).GroupBy(p => p.Value.Sha256).ToDictionary(g => g.Key, g => g.ToList());
            Dictionary<string, int> positions = groups.Select((g, i) => new { g.GroupId, Position = i }).ToDictionary(p => p.GroupId, p => p.Position);
            Dictionary<string, MediaGroup> legacyGroups = groups.Where(g => g.LegacyKey.Length > 0).GroupBy(g => g.LegacyKey).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
            HashSet<string> seenIdentities = new HashSet<string>();
            foreach (string path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested(); string safe = SafePath(path);
                if (!File.Exists(safe)) continue;
                MediaSource source;
                try { source = MediaSource.Read(safe, token); }
                catch (OperationCanceledException) { throw; }
                catch { issues.Add(new MediaIssue("SOURCE_UNREADABLE", Path.GetFileName(safe), "源无法读取或扫描时变化，未接受新指纹")); continue; }
                List<KeyValuePair<MediaGroup, MediaTrack>> pool;
                List<KeyValuePair<MediaGroup, MediaTrack>> candidates = source.Identity.Length > 0 && byIdentity.TryGetValue(source.Identity, out pool) ? pool.Where(p => !matched.Contains(p.Value.TrackId)).ToList() : new List<KeyValuePair<MediaGroup, MediaTrack>>();
                if (candidates.Count == 1 && !candidates[0].Value.RelativePath.Equals(source.Name, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(mediaRoot, candidates[0].Value.RelativePath))) { issues.Add(new MediaIssue("SOURCE_ALIAS", source.Name, "存在另一有效原路径，不把硬链接别名当重命名")); continue; }
                if (source.Identity.Length > 0 && !seenIdentities.Add(source.Identity)) { issues.Add(new MediaIssue("SOURCE_ALIAS", source.Name, "同一文件身份出现多个路径，未重复导入")); continue; }
                if (candidates.Count == 0 && byPath.TryGetValue(source.Name, out pool)) candidates = pool.Where(p => p.Value.Sha256 == source.Sha256 && !matched.Contains(p.Value.TrackId)).ToList();
                if (candidates.Count == 0 && missingByHash.TryGetValue(source.Sha256, out pool)) candidates = pool.Where(p => p.Value.Kind == source.Kind && (source.LegacyKey.Length == 0 || source.LegacyKey == p.Key.LegacyKey) && !matched.Contains(p.Value.TrackId)).ToList();
                if (candidates.Count > 1) { issues.Add(new MediaIssue("SOURCE_ID_AMBIGUOUS", source.Name, "文件身份或缺轨指纹不唯一，未自动改绑")); continue; }
                if (candidates.Count == 1)
                {
                    MediaGroup group = groups[positions[candidates[0].Key.GroupId]]; MediaTrack previous = candidates[0].Value;
                    MediaTrack replacement = source.Track(previous.TrackId, previous.Role, previous.Sha256 != source.Sha256 || previous.State == TrackState.Changed);
                    if (!source.HasMetadata) replacement = new MediaTrack(replacement.TrackId, replacement.RelativePath, replacement.Sha256, replacement.FileIdentity, previous.Package, replacement.Role, replacement.Kind, replacement.State, replacement.Length, replacement.WriteUtc);
                    groups[positions[group.GroupId]] = new MediaGroup(group.GroupId, group.Revision, group.CreatedUtc, group.LegacyKey, group.Tracks.Select(track => track.TrackId == previous.TrackId ? replacement : track), group.Game, group.GameMissing, group.Favorite, group.Tags);
                    matched.Add(previous.TrackId);
                }
                else
                {
                    // Only first adaptation uses filename pairing; identities are persisted from here on.
                    MediaGroup legacyGroup;
                    MediaGroup group = source.LegacyKey.Length > 0 && legacyGroups.TryGetValue(source.LegacyKey, out legacyGroup) ? groups[positions[legacyGroup.GroupId]] : null;
                    if (group != null && group.Tracks.Any(track => track.Role == source.Role)) group = null;
                    MediaTrack createdTrack = source.Track(Guid.NewGuid().ToString("N"), source.Role, false); matched.Add(createdTrack.TrackId);
                    if (group == null)
                    {
                        group = new MediaGroup(Guid.NewGuid().ToString("N"), 1, source.WriteUtc, source.LegacyKey, new[] { createdTrack }, null, false, false, new string[0]); positions.Add(group.GroupId, groups.Count); groups.Add(group);
                        if (source.LegacyKey.Length > 0 && !legacyGroups.ContainsKey(source.LegacyKey)) legacyGroups.Add(source.LegacyKey, group);
                    }
                    else groups[positions[group.GroupId]] = new MediaGroup(group.GroupId, group.Revision, group.CreatedUtc, group.LegacyKey, group.Tracks.Concat(new[] { createdTrack }), group.Game, group.GameMissing, group.Favorite, group.Tags);
                }
            }
            HashSet<string> touched = requested == null ? null : new HashSet<string>(paths.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < groups.Count; i++)
            {
                token.ThrowIfCancellationRequested(); MediaGroup group = groups[i];
                MediaTrack[] tracks = group.Tracks.Select(track => matched.Contains(track.TrackId) || (touched != null && !touched.Contains(track.RelativePath)) ? track : track.Status(File.Exists(Path.Combine(mediaRoot, track.RelativePath)) ? TrackState.Unreadable : TrackState.Missing)).ToArray();
                group = new MediaGroup(group.GroupId, group.Revision, group.CreatedUtc, group.LegacyKey, tracks, group.Game, group.GameMissing, group.Favorite, group.Tags);
                MediaGroup previous;
                if (!originalGroups.TryGetValue(group.GroupId, out previous)) Store(group);
                else if (MediaCodec.Payload(group) != MediaCodec.Payload(previous)) { group = previous.Replace(group.Tracks, group.Game, group.GameMissing, group.Favorite, group.Tags); Store(group); }
                groups[i] = group;
            }
            return Publish(groups, issues, null);
        }
        public Task<MediaRepositoryResult> RegisterGroupAsync(IEnumerable<MediaTrackInput> tracks, CancellationToken token)
        {
            if (tracks == null) throw new ArgumentNullException("tracks"); MediaTrackInput[] copy = tracks.ToArray();
            return Run(t =>
            {
                if (copy.Length < 1 || copy.Length > 2 || copy.Any(input => input == null || !Enum.IsDefined(typeof(ScreenRole), input.Role)) || copy.Select(input => input.Role).Distinct().Count() != copy.Length) return Result(MediaOutcome.Invalid, "INVALID_TRACKS", "新采集组需一至两条不同屏幕轨道", null);
                List<MediaIssue> issues = new List<MediaIssue>(); List<MediaGroup> groups = Load(issues, t);
                if (issues.Any(i => i.Code == "SCHEMA_UNSUPPORTED" || i.Code == "SIDECAR_UNREADABLE")) return Result(MediaOutcome.Invalid, "RECORDS_UNAVAILABLE", "存在未来版本或不可读记录，未写入", null);
                MediaSource[] sources = copy.Select(input => MediaSource.Read(SafePath(input.Path), t)).ToArray();
                if (sources.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length || sources.Select(s => s.Kind).Distinct().Count() != 1 || sources.Where(s => s.Identity.Length > 0).Select(s => s.Identity).Distinct().Count() != sources.Count(s => s.Identity.Length > 0)) return Result(MediaOutcome.Invalid, "INVALID_TRACKS", "轨道重复、同文件别名或媒体类型不一致", null);
                MediaGroup[] present = groups.Where(g => g.Tracks.Any(track => sources.Any(s => s.Name.Equals(track.RelativePath, StringComparison.OrdinalIgnoreCase)))).ToArray();
                if (groups.Any(g => g.Tracks.Any(track => sources.Any(s => s.Identity.Length > 0 && s.Identity == track.FileIdentity && !s.Name.Equals(track.RelativePath, StringComparison.OrdinalIgnoreCase))))) return Result(MediaOutcome.Conflict, "TRACK_SOURCE_ALIAS", "同一原片已注册，别名不能创建第二套身份", null);
                if (present.Length > 0)
                {
                    if (present.Length == 1 && sources.All(s => present[0].Tracks.Any(track => track.RelativePath.Equals(s.Name, StringComparison.OrdinalIgnoreCase) && track.Sha256 == s.Sha256)) && copy.All(input => present[0].Tracks.Any(track => track.RelativePath.Equals(Path.GetFileName(input.Path), StringComparison.OrdinalIgnoreCase) && track.Role == input.Role))) return Publish(groups, issues, present[0]);
                    return Result(MediaOutcome.Conflict, "TRACK_ALREADY_REGISTERED", "轨道已有组记录，不自动拆组或合并", null);
                }
                MediaGroup group = new MediaGroup(Guid.NewGuid().ToString("N"), 1, sources.Min(s => s.WriteUtc), "", sources.Select((s, i) => s.Track(Guid.NewGuid().ToString("N"), copy[i].Role, false)), null, false, false, new string[0]);
                t.ThrowIfCancellationRequested(); Store(group); groups.Add(group); return Publish(groups, issues, group);
            }, token);
        }
        Task<MediaRepositoryResult> Modify(string id, long revision, Func<MediaGroup, MediaGroup> edit, CancellationToken token)
        {
            return Run(t =>
            {
                MediaCodec.Id(id); List<MediaIssue> issues = new List<MediaIssue>(); List<MediaGroup> groups = Load(issues, t); MediaGroup previous = groups.FirstOrDefault(g => g.GroupId == id);
                if (previous == null) return Result(MediaOutcome.NotFound, "GROUP_NOT_FOUND", "媒体组不存在或记录不可恢复", null);
                if (previous.Revision != revision) return Result(MediaOutcome.Conflict, "REVISION_CONFLICT", "组已被另一操作更新，请重新读取并审阅", previous);
                MediaGroup changed = edit(previous); t.ThrowIfCancellationRequested(); Store(changed); groups[groups.IndexOf(previous)] = changed; return Publish(groups, issues, changed);
            }, token);
        }
        public Task<MediaRepositoryResult> AssociateGameAsync(string groupId, long expectedRevision, GameReference game, CancellationToken token)
        { if (game == null) throw new ArgumentNullException("game"); return Modify(groupId, expectedRevision, g => g.Replace(g.Tracks, game, false, g.Favorite, g.Tags), token); }
        public Task<MediaRepositoryResult> UnlinkGameAsync(string groupId, long expectedRevision, CancellationToken token)
        { return Modify(groupId, expectedRevision, g => g.Replace(g.Tracks, null, false, g.Favorite, g.Tags), token); }
        public Task<MediaRepositoryResult> SetAnnotationsAsync(string groupId, long expectedRevision, bool favorite, IEnumerable<string> tags, CancellationToken token)
        {
            if (tags == null) throw new ArgumentNullException("tags"); string[] copy = tags.Select(tag => tag == null ? "" : tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return Modify(groupId, expectedRevision, g => { if (copy.Length > 64 || copy.Any(tag => tag.Length == 0 || tag.Length > 128)) throw new ArgumentException("Invalid tags"); return g.Replace(g.Tracks, g.Game, g.GameMissing, favorite, copy); }, token);
        }
        public Task<MediaRepositoryResult> ValidateGameLinksAsync(IEnumerable<GameReference> available, CancellationToken token)
        {
            if (available == null) throw new ArgumentNullException("available"); HashSet<string> keys = new HashSet<string>(available.Select(game => game.Key), StringComparer.Ordinal);
            return Run(t =>
            {
                List<MediaIssue> issues = new List<MediaIssue>(); List<MediaGroup> groups = Load(issues, t);
                for (int i = 0; i < groups.Count; i++)
                { t.ThrowIfCancellationRequested(); MediaGroup group = groups[i]; bool missing = group.Game != null && !keys.Contains(group.Game.Key); if (missing != group.GameMissing) { group = group.Replace(group.Tracks, group.Game, missing, group.Favorite, group.Tags); Store(group); groups[i] = group; } }
                return Publish(groups, issues, null);
            }, token);
        }
        public Task<MediaRepositoryResult> ValidateGameLinksAsync(string provider, string libraryId, IEnumerable<string> availableIds, CancellationToken token)
        {
            if (availableIds == null) throw new ArgumentNullException("availableIds"); GameReference scope = new GameReference(provider, libraryId, "scope", "");
            HashSet<string> keys = new HashSet<string>(availableIds.Select(id => new GameReference(scope.Provider, scope.LibraryId, id, "").Key), StringComparer.Ordinal);
            return Run(t =>
            {
                List<MediaIssue> issues = new List<MediaIssue>(); List<MediaGroup> groups = Load(issues, t);
                for (int i = 0; i < groups.Count; i++)
                {
                    t.ThrowIfCancellationRequested(); MediaGroup group = groups[i];
                    if (group.Game == null || group.Game.Provider != scope.Provider || group.Game.LibraryId != scope.LibraryId) continue;
                    bool missing = !keys.Contains(group.Game.Key);
                    if (missing != group.GameMissing) { group = group.Replace(group.Tracks, group.Game, missing, group.Favorite, group.Tags); Store(group); groups[i] = group; }
                }
                return Publish(groups, issues, null);
            }, token);
        }
        public Task<MediaRepositoryResult> AcknowledgeSourceChangesAsync(string groupId, long expectedRevision, CancellationToken token)
        { return Modify(groupId, expectedRevision, g => g.Replace(g.Tracks.Select(track => track.State == TrackState.Changed ? track.Status(TrackState.Present) : track), g.Game, g.GameMissing, g.Favorite, g.Tags), token); }
        public Task<MediaRepositoryResult> RestorePreviousAnnotationsAsync(string groupId, long expectedRevision, CancellationToken token)
        {
            return Modify(groupId, expectedRevision, group =>
            {
                MediaGroup previous = Read(GroupPath(group.GroupId) + ".bak");
                if (previous.GroupId != group.GroupId || previous.Revision >= group.Revision) throw new InvalidDataException("Invalid previous group revision");
                // Restore only group annotations. Current source paths, identities and fingerprints stay intact.
                return group.Replace(group.Tracks, previous.Game, previous.GameMissing, previous.Favorite, previous.Tags);
            }, token);
        }
        public string ExportSnapshotJson() { return ExportSnapshotJson(0, 500); }
        public string ExportSnapshotJson(int offset, int count)
        {
            if (offset < 0 || count < 1 || count > 500) throw new ArgumentOutOfRangeException("count");
            MediaSnapshot current = GetSnapshot();
            return "{\"schema\":\"thor.media-snapshot/1\",\"repositoryId\":" + MediaCodec.Quote(current.RepositoryId) + ",\"generation\":" + MediaCodec.Quote(current.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ",\"totalGroups\":" + current.Groups.Count + ",\"totalIssues\":" + current.Issues.Count + ",\"offset\":" + offset + ",\"groups\":[" + String.Join(",", current.Groups.Skip(offset).Take(count).Select(MediaCodec.Payload))
                + "],\"issues\":[" + String.Join(",", current.Issues.Take(200).Select(issue => "{\"code\":" + MediaCodec.Quote(issue.Code) + ",\"reference\":" + MediaCodec.Quote(issue.Reference) + ",\"reason\":" + MediaCodec.Quote(issue.Reason) + "}")) + "]}";
        }
        public void Dispose() { lock (snapshotGate) { if (disposed) return; disposed = true; shutdown.Cancel(); } }
    }
}
