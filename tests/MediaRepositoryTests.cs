using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using ThorCapture.Media;

namespace ThorCapture
{
    internal static class MediaRepositoryTests
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr reserved);
        static string Folder(string name) { string path = Path.Combine(Tests.Root, name + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        static string Image(string dir, string name, Color color)
        { string path = Path.Combine(dir, name); using (Bitmap bitmap = new Bitmap(16, 16)) using (Graphics graphics = Graphics.FromImage(bitmap)) { graphics.Clear(color); bitmap.Save(path, ImageFormat.Png); } return path; }
        static string Hash(string path) { using (FileStream file = File.OpenRead(path)) using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""); }
        static MediaRepositoryResult Ok(MediaRepositoryResult result) { if (result.Outcome != MediaOutcome.Success) throw new Exception(result.Code + ": " + result.Reason); return result; }
        internal static void Run()
        {
            string media = Folder("media"), data = Folder("media-data");
            string upper = Image(media, "thor-upper-20260927-120000-000-abc123.png", Color.Red), lower = Image(media, "thor-lower-20260927-120000-000-abc123.png", Color.Blue);
            string other = Image(media, "自定义截图.png", Color.Green), video = Path.Combine(media, "thor-upper-20260927-130000-000-123abc.mp4"); File.WriteAllBytes(video, new byte[] { 0, 1, 2, 3 });
            new CaptureMetadata { Package = "example.game", Status = "identified", CapturedUtc = DateTime.UtcNow, ObservedUtc = DateTime.UtcNow }.Save(upper);
            string upperHash = Hash(upper), lowerHash = Hash(lower), otherHash = Hash(other), videoHash = Hash(video);
            MediaGroup paired; MediaSnapshot old; GameReference game = new GameReference("cocoon", "library-one", "42", "测试游戏");
            using (MediaRepository repo = new MediaRepository(media, data))
            {
                int calledThread = Thread.CurrentThread.ManagedThreadId, eventThread = calledThread; repo.Changed += delegate { eventThread = Thread.CurrentThread.ManagedThreadId; };
                old = Ok(repo.RefreshAsync(CancellationToken.None).Result).Snapshot;
                paired = old.Groups.Single(g => g.Tracks.Count == 2);
                Tests.Check(old.Groups.Count == 3 && paired.Tracks.Select(t => t.Role).Distinct().Count() == 2 && paired.Tracks.All(t => t.State == TrackState.Present), "legacy upper/lower names adapt once into one stable group while custom image and video remain distinct");
                Tests.Check(paired.GroupId.Length == 32 && old.Groups.SelectMany(g => g.Tracks).Select(t => t.TrackId).Distinct().Count() == 4, "group and track identities are distinct stable GUIDs");
                Tests.Check(paired.Tracks.Single(t => t.Role == ScreenRole.Upper).Package == "example.game" && paired.Tracks.All(t => t.Sha256.Length == 64), "existing capture metadata read without changing its schema and full source SHA recorded");
                Tests.Check(eventThread != calledThread, "repository events run on background threads and immutable snapshots do not require disk reads");
                MediaSnapshot again = Ok(repo.RefreshAsync(CancellationToken.None).Result).Snapshot;
                Tests.Check(again.Groups.Single(g => g.GroupId == paired.GroupId).Revision == paired.Revision && again.Groups.SelectMany(g => g.Tracks).Select(t => t.TrackId).OrderBy(x => x).SequenceEqual(old.Groups.SelectMany(g => g.Tracks).Select(t => t.TrackId).OrderBy(x => x)), "repeated scan preserves IDs and does not revise unchanged groups");
                paired = Ok(repo.AssociateGameAsync(paired.GroupId, paired.Revision, game, CancellationToken.None).Result).Group;
                Tests.Check(repo.GetSnapshot().ForGame(game).Count == 1 && repo.GetSnapshot().ForGame(new GameReference("cocoon", "other-library", "42", "")).Count == 0 && old.ForGame(game).Count == 0, "game association belongs to whole group and uses library-qualified identity with immutable old snapshots");
                string[] tags = new[] { " 游戏 ", "WIP", "wip" }; System.Threading.Tasks.Task<MediaRepositoryResult> annotation = repo.SetAnnotationsAsync(paired.GroupId, paired.Revision, true, tags, CancellationToken.None); tags[0] = "mutated";
                paired = Ok(annotation.Result).Group;
                Tests.Check(paired.Favorite && paired.Tags.Count == 2 && paired.Tags.Contains("游戏") && !paired.Tags.Contains("mutated"), "favorites and normalized tags are group-level and input mutation cannot change queued update");
                Tests.Check(repo.AssociateGameAsync(paired.GroupId, paired.Revision - 1, game, CancellationToken.None).Result.Outcome == MediaOutcome.Conflict, "stale group revision rejected without silently replacing another update");
                Tests.Check(repo.SetAnnotationsAsync(paired.GroupId, paired.Revision, false, new[] { "" }, CancellationToken.None).Result.Outcome == MediaOutcome.Invalid && repo.GetSnapshot().FindGroup(paired.GroupId).Favorite, "invalid annotation leaves previous group untouched");
                paired = Ok(repo.ValidateGameLinksAsync("cocoon", "library-one", new string[0], CancellationToken.None).Result).Snapshot.FindGroup(paired.GroupId);
                Tests.Check(paired.GameMissing && paired.Game.Key == game.Key, "deleted game reference is marked missing and retained for recovery rather than silently detached");
                paired = Ok(repo.ValidateGameLinksAsync("cocoon", "different-library", new string[0], CancellationToken.None).Result).Snapshot.FindGroup(paired.GroupId);
                Tests.Check(paired.GameMissing, "scoped validation cannot change another library namespace");
                paired = Ok(repo.ValidateGameLinksAsync("cocoon", "library-one", new[] { "42" }, CancellationToken.None).Result).Snapshot.FindGroup(paired.GroupId);
                Tests.Check(!paired.GameMissing && paired.Game.Key == game.Key, "restored game identity recovers retained association");
                string upperId = paired.Tracks.Single(t => t.Role == ScreenRole.Upper).TrackId;
                string rename = Path.Combine(media, "重命名后完全无屏幕标记.png"); File.Move(upper, rename);
                paired = Ok(repo.ImportPathsAsync(new[] { upper, rename }, CancellationToken.None).Result).Snapshot.FindGroup(paired.GroupId);
                Tests.Check(paired.Tracks.Single(t => t.Role == ScreenRole.Upper).TrackId == upperId && paired.Tracks.Single(t => t.Role == ScreenRole.Upper).RelativePath == Path.GetFileName(rename) && paired.Game.Key == game.Key && paired.Favorite, "arbitrary filename rename retains track role group ID game favorites and tags by file identity");
                Tests.Check(paired.Tracks.Single(t => t.Role == ScreenRole.Upper).Package == "example.game", "rename retains captured package when old sidecar has not yet moved");
                string alias = Path.Combine(media, "a-alias.png"); bool linked = CreateHardLink(alias, rename, IntPtr.Zero);
                Tests.Check(linked && repo.RegisterGroupAsync(new[] { new MediaTrackInput(alias, ScreenRole.Upper) }, CancellationToken.None).Result.Code == "TRACK_SOURCE_ALIAS", "explicit registration rejects physical alias of an already registered original");
                MediaRepositoryResult aliases = Ok(repo.RefreshAsync(CancellationToken.None).Result);
                Tests.Check(linked && aliases.Snapshot.Issues.Any(i => i.Code == "SOURCE_ALIAS") && aliases.Snapshot.FindGroup(paired.GroupId).Tracks.Single(t => t.Role == ScreenRole.Upper).RelativePath == Path.GetFileName(rename), "hard-link alias is reported without stealing the known path or duplicating media IDs"); File.Delete(alias);
                File.Delete(lower); paired = Ok(repo.ImportPathsAsync(new[] { lower }, CancellationToken.None).Result).Snapshot.FindGroup(paired.GroupId);
                Tests.Check(paired.Tracks.Single(t => t.Role == ScreenRole.Lower).State == TrackState.Missing && paired.Tracks.Count == 2 && paired.Game.Key == game.Key, "external track deletion leaves a readable missing track and preserves group association");
                string copiedLower = Path.Combine(media, "复制恢复下屏.png"); using (Bitmap image = new Bitmap(16, 16)) using (Graphics graphics = Graphics.FromImage(image)) { graphics.Clear(Color.Blue); image.Save(copiedLower, ImageFormat.Png); }
                string lowerId = paired.Tracks.Single(t => t.Role == ScreenRole.Lower).TrackId;
                paired = Ok(repo.ImportPathsAsync(new[] { copiedLower }, CancellationToken.None).Result).Snapshot.FindGroup(paired.GroupId);
                Tests.Check(paired.Tracks.Single(t => t.Role == ScreenRole.Lower).TrackId == lowerId && paired.Tracks.Single(t => t.Role == ScreenRole.Lower).State == TrackState.Present, "unique missing source SHA recovers a copied relocation when file identity changed");
                using (FileStream file = File.Open(rename, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) file.WriteByte(0);
                paired = Ok(repo.ImportPathsAsync(new[] { rename }, CancellationToken.None).Result).Snapshot.FindGroup(paired.GroupId);
                Tests.Check(paired.Tracks.Single(t => t.Role == ScreenRole.Upper).TrackId == upperId && paired.Tracks.Single(t => t.Role == ScreenRole.Upper).State == TrackState.Changed, "in-place content change increments record and explicitly flags changed source instead of silently accepting integrity");
                paired = Ok(repo.AcknowledgeSourceChangesAsync(paired.GroupId, paired.Revision, CancellationToken.None).Result).Group;
                Tests.Check(paired.Tracks.All(t => t.State == TrackState.Present), "source-change review explicitly clears changed flag without touching originals");
                paired = Ok(repo.UnlinkGameAsync(paired.GroupId, paired.Revision, CancellationToken.None).Result).Group;
                Tests.Check(paired.Game == null && repo.GetSnapshot().ForGame(game).Count == 0 && paired.Favorite, "explicit group unlink leaves independent favorites tags and track identity intact");
                string currentPath = paired.Tracks.Single(t => t.Role == ScreenRole.Upper).RelativePath; long unlinkRevision = paired.Revision;
                paired = Ok(repo.RestorePreviousAnnotationsAsync(paired.GroupId, paired.Revision, CancellationToken.None).Result).Group;
                Tests.Check(paired.Game.Key == game.Key && paired.Revision > unlinkRevision && paired.Tracks.Single(t => t.Role == ScreenRole.Upper).RelativePath == currentPath, "previous annotation recovery reverses unlink with a new revision and never rewinds source paths or identities");
                paired = Ok(repo.AssociateGameAsync(paired.GroupId, paired.Revision, game, CancellationToken.None).Result).Group;
                Tests.Check(Json.Text(Json.Parse(repo.ExportSnapshotJson(0, 2)), "schema") == "thor.media-snapshot/1" && ((System.Collections.Generic.List<object>)Json.Get(Json.Parse(repo.ExportSnapshotJson(0, 2)), "groups")).Count == 2, "paged versioned JSON snapshot bridges stable IDs and game references without exposing full source paths");
                Tests.Reject(delegate { repo.ExportSnapshotJson(0, 501); }, "snapshot export limits bridge page size");
                Tests.Reject(delegate { ((System.Collections.Generic.IList<string>)paired.Tags).Add("invalid"); }, "published group collections are immutable");
                using (FileStream exclusive = File.Open(rename, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Tests.Check(exclusive.Length > 0, "all repository hashing and metadata reads release original source handles");
                upperHash = Hash(rename); // The test explicitly appended a byte; repository operations do not change this new version.
                lowerHash = Hash(copiedLower);
                string groupFile = Path.Combine(data, "group-" + paired.GroupId + ".thor-group"); string stable = Hash(groupFile);
                using (FileStream held = File.Open(groupFile, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    MediaRepositoryResult unreadable = repo.RefreshAsync(CancellationToken.None).Result;
                    Tests.Check(unreadable.Code == "RECORDS_UNAVAILABLE" && unreadable.Snapshot.Issues.Any(i => i.Code == "SIDECAR_UNREADABLE") && File.Exists(groupFile), "temporarily locked sidecar stays in place and blocks duplicate re-adaptation instead of being mislabeled corrupt");
                }
                File.SetAttributes(groupFile, FileAttributes.ReadOnly);
                try { Tests.Check(repo.SetAnnotationsAsync(paired.GroupId, paired.Revision, false, new[] { "new" }, CancellationToken.None).Result.Outcome == MediaOutcome.Failed && Hash(groupFile) == stable && Directory.GetFiles(data, "*.tmp").Length == 0, "failed atomic metadata replacement leaves previous group and cleans owned temp"); }
                finally { File.SetAttributes(groupFile, FileAttributes.Normal); }
                File.WriteAllText(groupFile, "truncated"); MediaRepositoryResult repaired = Ok(repo.RefreshAsync(CancellationToken.None).Result);
                Tests.Check(repaired.Snapshot.Issues.Any(i => i.Code == "SIDECAR_RECOVERED") && repaired.Snapshot.FindGroup(paired.GroupId).Tracks.Single(t => t.Role == ScreenRole.Upper).TrackId == upperId && Directory.GetFiles(Path.Combine(data, "quarantine")).Length > 0, "corrupt group sidecar is isolated and restored from complete previous revision");
                paired = repaired.Snapshot.FindGroup(paired.GroupId);
                Tests.Check(Hash(rename) == upperHash && Hash(copiedLower) == lowerHash && Hash(other) == otherHash && Hash(video) == videoHash, "metadata imports game annotations recovery and failures leave all original bytes unchanged");
                MediaRepositoryResult outside = repo.ImportPathsAsync(new[] { Path.Combine(Tests.Root, "outside.png") }, CancellationToken.None).Result;
                Tests.Check(outside.Outcome == MediaOutcome.Invalid, "selected-library boundary rejects outside path instead of reading unrelated files");
            }
            using (MediaRepository reopened = new MediaRepository(media, data))
            {
                MediaGroup restored = Ok(reopened.RefreshAsync(CancellationToken.None).Result).Snapshot.FindGroup(paired.GroupId);
                Tests.Check(restored != null && restored.Tracks.Select(t => t.TrackId).SequenceEqual(paired.Tracks.Select(t => t.TrackId)), "derived memory index rebuild from sidecars preserves group and track IDs after process restart");
                string primary = Path.Combine(data, "group-" + paired.GroupId + ".thor-group"); File.Delete(primary);
                MediaRepositoryResult recover = Ok(reopened.RefreshAsync(CancellationToken.None).Result);
                Tests.Check(recover.Snapshot.FindGroup(paired.GroupId) != null && recover.Snapshot.Issues.Any(i => i.Code == "SIDECAR_RECOVERED"), "missing primary sidecar restores previous valid group while retaining originals");
            }
            RegisterAndConcurrency(); BadRecords(); MissingBatchIsolation(); RootBinding(); Measure();
        }
        static void RegisterAndConcurrency()
        {
            string media = Folder("register-media"), data = Folder("register-data"); string a = Image(media, "a.png", Color.Red), b = Image(media, "b.png", Color.Blue);
            using (MediaRepository repo = new MediaRepository(media, data))
            using (MediaRepository other = new MediaRepository(media, data))
            {
                MediaGroup group = Ok(repo.RegisterGroupAsync(new[] { new MediaTrackInput(a, ScreenRole.Upper), new MediaTrackInput(b, ScreenRole.Lower) }, CancellationToken.None).Result).Group;
                Tests.Check(group.Tracks.Count == 2 && group.Revision == 1 && group.LegacyKey.Length == 0, "new captures explicitly register stable groups without relying on filenames");
                Tests.Check(Ok(repo.RegisterGroupAsync(new[] { new MediaTrackInput(a, ScreenRole.Upper), new MediaTrackInput(b, ScreenRole.Lower) }, CancellationToken.None).Result).Group.GroupId == group.GroupId, "registration retry is idempotent for existing matching tracks and roles");
                Tests.Check(repo.RegisterGroupAsync(new[] { new MediaTrackInput(a, ScreenRole.Unknown) }, CancellationToken.None).Result.Outcome == MediaOutcome.Conflict, "already registered track is never silently split into another group");
                Ok(other.RefreshAsync(CancellationToken.None).Result);
                GameReference game = new GameReference("cocoon", "lib", "7", "Game");
                System.Threading.Tasks.Task<MediaRepositoryResult> first = repo.AssociateGameAsync(group.GroupId, 1, game, CancellationToken.None), second = other.SetAnnotationsAsync(group.GroupId, 1, true, new[] { "race" }, CancellationToken.None);
                MediaRepositoryResult[] results = new[] { first.Result, second.Result };
                Tests.Check(results.Count(r => r.Outcome == MediaOutcome.Success) == 1 && results.Count(r => r.Outcome == MediaOutcome.Conflict) == 1, "independent repository instances coordinate file lease and compare-and-swap group revision");
                MediaSnapshot before = repo.GetSnapshot();
                using (FileStream held = File.Open(Path.Combine(data, "repository.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                using (CancellationTokenSource cancelled = new CancellationTokenSource())
                {
                    System.Threading.Tasks.Task<MediaRepositoryResult> blocked = repo.RefreshAsync(CancellationToken.None);
                    Stopwatch timer = Stopwatch.StartNew(); cancelled.CancelAfter(60);
                    Tests.Check(repo.RefreshAsync(cancelled.Token).Result.Outcome == MediaOutcome.Cancelled && timer.ElapsedMilliseconds < 1000, "queued repository operation cancels without waiting for another blocked writer");
                    timer.Restart(); Tests.Check(Object.ReferenceEquals(repo.GetSnapshot(), before) && timer.ElapsedMilliseconds < 100, "in-memory snapshot reads never wait on repository IO or writer lease");
                    Tests.Check(blocked.Result.Outcome == MediaOutcome.TimedOut, "cross-process lease contention has a bounded timeout");
                }
                using (CancellationTokenSource cancelled = new CancellationTokenSource()) { cancelled.Cancel(); Tests.Check(repo.RefreshAsync(cancelled.Token).Result.Outcome == MediaOutcome.Cancelled, "pre-cancelled refresh performs no media scan"); }
                repo.Dispose(); Tests.Check(repo.RefreshAsync(CancellationToken.None).Result.Code == "REPOSITORY_CLOSED", "disposed repository rejects new operations with explicit outcome");
            }
        }
        static void BadRecords()
        {
            string media = Folder("bad-media"), data = Folder("bad-data"); string path = Image(media, "shot.png", Color.Blue);
            using (MediaRepository initialize = new MediaRepository(media, data)) Ok(initialize.ImportPathsAsync(new string[0], CancellationToken.None).Result);
            string unsupported = Path.Combine(data, "group-" + Guid.NewGuid().ToString("N") + ".thor-group"); File.WriteAllText(unsupported, "thor.media-group/2\nfuture");
            using (MediaRepository repo = new MediaRepository(media, data))
            {
                Tests.Check(repo.RefreshAsync(CancellationToken.None).Result.Code == "RECORDS_UNAVAILABLE" && File.Exists(unsupported) && repo.GetSnapshot().Groups.Count == 0, "future sidecar version remains untouched and prevents identity-destroying automatic adaptation");
                File.Delete(unsupported); string corrupt = Path.Combine(data, "group-" + Guid.NewGuid().ToString("N") + ".thor-group"); File.WriteAllText(corrupt, "bad");
                MediaRepositoryResult repaired = Ok(repo.RefreshAsync(CancellationToken.None).Result);
                Tests.Check(repaired.Snapshot.Groups.Count == 1 && repaired.Snapshot.Issues.Any(i => i.Code == "SIDECAR_CORRUPT") && Directory.GetFiles(Path.Combine(data, "quarantine")).Length == 1 && File.Exists(path), "unrecoverable sidecar quarantined with explicit identity-loss warning while unmodified originals remain browseable");
                Tests.Check(repo.RegisterGroupAsync(new[] { new MediaTrackInput(path, ScreenRole.Upper), new MediaTrackInput(path, ScreenRole.Lower) }, CancellationToken.None).Result.Outcome == MediaOutcome.Invalid, "two roles cannot register the same original path as independent tracks");
            }
            string empty = Folder("empty-media"), store = Folder("empty-store"); using (MediaRepository repo = new MediaRepository(empty, store)) Tests.Check(Ok(repo.RefreshAsync(CancellationToken.None).Result).Snapshot.Groups.Count == 0, "empty media library produces an explicit successful empty snapshot");
            Tests.Reject(delegate { new GameReference("cocoon", "a/path", "7", ""); }, "game namespaces are opaque IDs and cannot contain local paths");
        }
        static void RootBinding()
        {
            string first = Folder("bound-first"), second = Folder("bound-second"), data = Folder("bound-data");
            string source = Image(first, "shot.png", Color.Red), copy = Image(second, "shot.png", Color.Blue);
            MediaSnapshot initial;
            using (MediaRepository repo = new MediaRepository(first, data)) initial = Ok(repo.RefreshAsync(CancellationToken.None).Result).Snapshot;
            string manifest = Path.Combine(data, "repository.thor-root"), originalBinding = Hash(manifest);
            using (MediaRepository repo = new MediaRepository(second, data))
            {
                Tests.Check(repo.RefreshAsync(CancellationToken.None).Result.Code == "ROOT_MISMATCH" && Hash(manifest) == originalBinding, "switching media directory cannot reuse another library identity or associations implicitly");
                Tests.Check(repo.RebindRootAsync(CancellationToken.None).Result.Code == "ROOT_SOURCE_MISMATCH" && Hash(manifest) == originalBinding, "explicit root relocation rejects source fingerprint mismatch without changing binding");
                File.Copy(source, copy, true);
                MediaSnapshot moved = Ok(repo.RebindRootAsync(CancellationToken.None).Result).Snapshot;
                Tests.Check(moved.RepositoryId == initial.RepositoryId && moved.Groups[0].GroupId == initial.Groups[0].GroupId && moved.Groups[0].Tracks[0].TrackId == initial.Groups[0].Tracks[0].TrackId && Hash(source) == Hash(copy), "verified whole-library relocation preserves repository group and track identities without writing originals");
            }
            using (MediaRepository repo = new MediaRepository(first, data)) Tests.Check(repo.RefreshAsync(CancellationToken.None).Result.Code == "ROOT_MISMATCH", "old directory cannot update repository after explicit relocation");
            File.Delete(manifest);
            // Backup describes the old root; it must not silently restore over the verified new root.
            using (MediaRepository repo = new MediaRepository(second, data))
            {
                Tests.Check(repo.RefreshAsync(CancellationToken.None).Result.Code == "ROOT_MISMATCH", "missing root manifest does not silently restore a backup bound to another directory");
                Ok(repo.RebindRootAsync(CancellationToken.None).Result);
            }
            File.Delete(manifest); File.Delete(manifest + ".bak");
            using (MediaRepository repo = new MediaRepository(second, data))
            {
                Tests.Check(repo.RefreshAsync(CancellationToken.None).Result.Code == "ROOT_BINDING_MISSING", "lost repository binding requires explicit verified adoption instead of guessing root");
                Tests.Check(Ok(repo.RebindRootAsync(CancellationToken.None).Result).Snapshot.Groups[0].GroupId == initial.Groups[0].GroupId, "explicit recovery with surviving fingerprints retains group ID after lost root binding");
            }
        }
        static void Measure()
        {
            string media = Folder("media-measure"), data = Folder("media-measure-data");
            for (int i = 0; i < 1000; i++) Image(media, "shot-" + i + ".png", Color.FromArgb(255, i % 256, (i / 256) % 256, 100));
            using (MediaRepository repo = new MediaRepository(media, data))
            {
                Stopwatch timer = Stopwatch.StartNew(); MediaRepositoryResult cold = Ok(repo.RefreshAsync(CancellationToken.None).Result); long coldMs = timer.ElapsedMilliseconds;
                timer.Restart(); MediaRepositoryResult warm = Ok(repo.RefreshAsync(CancellationToken.None).Result); long warmMs = timer.ElapsedMilliseconds;
                timer.Restart(); MediaSnapshot snapshot = repo.GetSnapshot(); int groups = snapshot.Groups.Count; long memoryMs = timer.ElapsedMilliseconds;
                File.WriteAllText(Path.Combine(Tests.Root, "media-repository-performance.txt"), "synthetic PNG=1000x16x16; cold adaptation ms=" + coldMs + "; warm verify+reload ms=" + warmMs + "; memory snapshot ms=" + memoryMs + "; groups=" + groups);
                Tests.Check(cold.Snapshot.Groups.Count == 1000 && warm.Snapshot.Groups.Select(g => g.GroupId).SequenceEqual(cold.Snapshot.Groups.Select(g => g.GroupId)), "one-thousand-source adaptation and complete SHA verification preserve all stable IDs");
            }
        }
        static void MissingBatchIsolation()
        {
            string media = Folder("missing-batch"), data = Folder("missing-batch-store");
            string path = Image(media, "thor-upper-20260927-100000-000-aaaaaa.png", Color.Red);
            using (MediaRepository repo = new MediaRepository(media, data))
            {
                MediaGroup original = Ok(repo.RefreshAsync(CancellationToken.None).Result).Snapshot.Groups.Single(); File.Delete(path);
                string fresh = Image(media, "thor-upper-20260927-110000-000-bbbbbb.png", Color.Red);
                MediaSnapshot snapshot = Ok(repo.RefreshAsync(CancellationToken.None).Result).Snapshot;
                Tests.Check(snapshot.Groups.Count == 2 && snapshot.FindGroup(original.GroupId).Tracks.Single().State == TrackState.Missing && snapshot.Groups.Single(g => g.GroupId != original.GroupId).Tracks.Single().RelativePath == Path.GetFileName(fresh), "a fresh capture batch never inherits a missing older group merely because screenshot bytes match");
                string hash = Hash(fresh); File.SetLastWriteTimeUtc(fresh, File.GetLastWriteTimeUtc(fresh).AddSeconds(1));
                MediaRepositoryResult checkedVersion = Ok(repo.RefreshAsync(CancellationToken.None).Result);
                Tests.Check(checkedVersion.Snapshot.Groups.Count == 2 && Hash(fresh) == hash, "mtime-only change refreshes metadata without altering original content or creating duplicate IDs");
            }
            string ambiguous = Folder("ambiguous-hash"), records = Folder("ambiguous-records");
            string a = Image(ambiguous, "a.png", Color.Blue), b = Image(ambiguous, "b.png", Color.Blue);
            using (MediaRepository repo = new MediaRepository(ambiguous, records))
            {
                Ok(repo.RefreshAsync(CancellationToken.None).Result); File.Delete(a); File.Delete(b); Image(ambiguous, "unknown-relocation.png", Color.Blue);
                MediaSnapshot snapshot = Ok(repo.RefreshAsync(CancellationToken.None).Result).Snapshot;
                Tests.Check(snapshot.Groups.Count == 2 && snapshot.Issues.Any(i => i.Code == "SOURCE_ID_AMBIGUOUS") && snapshot.Groups.SelectMany(g => g.Tracks).All(t => t.State == TrackState.Missing), "ambiguous equal-byte missing tracks never auto-rebind a new unknown filename");
            }
        }
    }
}
