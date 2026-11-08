using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Media;

namespace ThorCapture.Features.Library
{
    internal enum RecoveryKind { ScreenshotPublication, GroupRename, GroupCopy, DeviceImport }
    internal enum RecoveryState { Prepared, Staged, Reserved, Publishing, Applying, RollingBack, Copying, Copied, Verified, Stored, Corrupt, Unreadable }
    [Flags] internal enum RecoveryDirection { None = 0, Continue = 1, Rollback = 2 }
    internal enum RecoveryCatalogOutcome { Success, Partial, Cancelled, Conflict, Failed, TimedOut }
    internal sealed class RecoveryEntry
    {
        public const string Schema = "thor.recovery-catalog/1";
        public readonly string TransactionId, PeerRepositoryId, ProviderId, Code;
        public readonly RecoveryKind Kind;
        public readonly RecoveryState State;
        public readonly RecoveryDirection Directions;
        public readonly bool RequiresConfirmation = true, RequiresRevalidation = true;
        internal RecoveryEntry(string transactionId, RecoveryKind kind, RecoveryState state, RecoveryDirection directions, string code, string peer = "", string provider = "")
        { TransactionId = transactionId; Kind = kind; State = state; Directions = directions; Code = code; PeerRepositoryId = peer; ProviderId = provider; }
    }
    internal sealed class RecoveryCatalogResult
    {
        public const string Schema = RecoveryEntry.Schema;
        public readonly RecoveryCatalogOutcome Outcome;
        public readonly string Code, RepositoryId;
        public readonly ReadOnlyCollection<RecoveryEntry> Entries;
        public readonly int InvalidNames, CompletedCount, InspectedCount;
        public readonly bool Truncated;
        internal RecoveryCatalogResult(RecoveryCatalogOutcome outcome, string code, string repository, IEnumerable<RecoveryEntry> entries, int invalidNames = 0, int completed = 0, int inspected = 0, bool truncated = false)
        { Outcome = outcome; Code = code; RepositoryId = repository; Entries = new ReadOnlyCollection<RecoveryEntry>(entries.OrderBy(x => x.Kind).ThenBy(x => x.TransactionId, StringComparer.Ordinal).ToArray()); InvalidNames = invalidNames; CompletedCount = completed; InspectedCount = inspected; Truncated = truncated; }
    }
    /// <summary>Read-only startup discovery. It never executes recovery, touches media, or calls a provider.</summary>
    internal static class RecoveryCatalog
    {
        const int MaxFiles = 200, BudgetMs = 5000;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        static string Hash(string value) { using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Utf8.GetBytes(value))).Replace("-", ""); }
        static string Decode(string value) { return Utf8.GetString(Convert.FromBase64String(value)); }
        static bool Sha(string value) { return System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-F0-9]{64}$"); }
        static void Demand(bool value) { if (!value) throw new InvalidDataException(); }
        static string ReadBounded(string path, int maximum, CancellationToken token)
        {
            LibraryPathGuard.NoLinks(path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            using (FileStream file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) using (MemoryStream bytes = new MemoryStream())
            {
                byte[] buffer = new byte[8192]; int count;
                while ((count = file.Read(buffer, 0, Math.Min(buffer.Length, maximum + 1 - (int)bytes.Length))) > 0) { token.ThrowIfCancellationRequested(); bytes.Write(buffer, 0, count); if (bytes.Length > maximum) throw new InvalidDataException(); }
                token.ThrowIfCancellationRequested(); return Utf8.GetString(bytes.ToArray());
            }
        }
        static string[] Checksummed(string text, bool marked)
        {
            int marker = marked ? text.LastIndexOf("sha256=", StringComparison.Ordinal) : text.LastIndexOf('\n') + 1;
            Demand(marker > 0); string body = text.Substring(0, marker); Demand(text.Substring(marker) == (marked ? "sha256=" : "") + Hash(body) + (marked ? "\n" : "")); return body.TrimEnd('\n').Split('\n');
        }
        static bool KindOf(string extension, out RecoveryKind kind)
        {
            kind = RecoveryKind.ScreenshotPublication;
            if (extension == ".thor-publish") return true;
            if (extension == ".thor-rename") { kind = RecoveryKind.GroupRename; return true; }
            if (extension == ".thor-copy") { kind = RecoveryKind.GroupCopy; return true; }
            if (extension == ".thor-import") { kind = RecoveryKind.DeviceImport; return true; } return false;
        }
        public static async Task<RecoveryCatalogResult> ScanAsync(MediaRepository repository, string expectedRepositoryId, CancellationToken token)
        {
            if (repository == null) throw new ArgumentNullException("repository");
            if (token.IsCancellationRequested) return Empty(RecoveryCatalogOutcome.Cancelled, "RECOVERY_CANCELLED", "");
            string repositoryId = repository.GetSnapshot().RepositoryId;
            if (repositoryId != expectedRepositoryId || repositoryId.Length != 32) return Empty(RecoveryCatalogOutcome.Conflict, "RECOVERY_SCOPE_CHANGED", "");
            using (CancellationTokenSource owned = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task<RecoveryCatalogResult> work = Task.Run(() => Scan(repository, repositoryId, owned.Token));
                Task timer = Task.Delay(BudgetMs, owned.Token);
                if (await Task.WhenAny(work, timer).ConfigureAwait(false) != work)
                {
                    owned.Cancel(); Task observe = work.ContinueWith(task => { if (task.IsFaulted) { var ignored = task.Exception; } }, TaskScheduler.Default); GC.KeepAlive(observe);
                    return Empty(token.IsCancellationRequested ? RecoveryCatalogOutcome.Cancelled : RecoveryCatalogOutcome.TimedOut, token.IsCancellationRequested ? "RECOVERY_CANCELLED" : "RECOVERY_SCAN_TIMEOUT", repositoryId);
                }
                RecoveryCatalogResult result = await work.ConfigureAwait(false); owned.Cancel(); return result;
            }
        }
        static RecoveryCatalogResult Empty(RecoveryCatalogOutcome outcome, string code, string id) { return new RecoveryCatalogResult(outcome, code, id, new RecoveryEntry[0]); }
        static RecoveryCatalogResult Scan(MediaRepository repository, string repositoryId, CancellationToken token)
        {
            List<RecoveryEntry> entries = new List<RecoveryEntry>(); int invalid = 0, completed = 0, inspected = 0; bool truncated = false; Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                token.ThrowIfCancellationRequested(); LibraryPathGuard.NoLinks(repository.DataRoot); Demand((File.GetAttributes(repository.DataRoot) & FileAttributes.ReparsePoint) == 0);
                string binding = ReadBounded(Path.Combine(repository.DataRoot, "repository.thor-root"), 16384, token); string[] bind = binding.Split('\n');
                Demand(bind.Length == 4 && bind[0] == "thor.media-repository/1" && bind[1] == repositoryId && Decode(bind[2]) == repository.MediaRoot && Hash(String.Join("\n", bind, 0, 3)) == bind[3]);
                string folder = Path.Combine(repository.DataRoot, "transactions");
                LibraryPathGuard.NoLinks(folder);
                if (!Directory.Exists(folder)) return Empty(RecoveryCatalogOutcome.Success, "OK", repositoryId);
                Demand((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0);
                // Top-level discovery only: no media path, remote directory, or peer repository is read.
                foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
                {
                    token.ThrowIfCancellationRequested(); if (elapsed.ElapsedMilliseconds >= BudgetMs) throw new TimeoutException();
                    if (++inspected > MaxFiles) { inspected = MaxFiles; truncated = true; break; }
                    RecoveryKind kind; if (!KindOf(Path.GetExtension(path), out kind)) continue;
                    string id = Path.GetFileNameWithoutExtension(path); try { if (MediaCodec.Id(id) != id) throw new InvalidDataException(); } catch { invalid++; continue; }
                    try
                    {
                        RecoveryEntry entry = Inspect(repository, repositoryId, id, kind, ReadBounded(path, kind == RecoveryKind.DeviceImport ? 16384 : 1024 * 1024, token));
                        if (entry == null) completed++; else entries.Add(entry);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (InvalidDataException) { entries.Add(new RecoveryEntry(id, kind, RecoveryState.Corrupt, RecoveryDirection.None, "RECOVERY_LOG_INVALID")); }
                    catch (IOException) { entries.Add(new RecoveryEntry(id, kind, RecoveryState.Unreadable, RecoveryDirection.None, "RECOVERY_LOG_UNREADABLE")); }
                    catch (UnauthorizedAccessException) { entries.Add(new RecoveryEntry(id, kind, RecoveryState.Unreadable, RecoveryDirection.None, "RECOVERY_LOG_UNREADABLE")); }
                    catch { entries.Add(new RecoveryEntry(id, kind, RecoveryState.Corrupt, RecoveryDirection.None, "RECOVERY_LOG_INVALID")); }
                }
                if (repository.GetSnapshot().RepositoryId != repositoryId) return Empty(RecoveryCatalogOutcome.Conflict, "RECOVERY_SCOPE_CHANGED", repositoryId);
                bool issues = invalid > 0 || truncated || entries.Any(x => x.Directions == RecoveryDirection.None);
                return new RecoveryCatalogResult(issues ? RecoveryCatalogOutcome.Partial : RecoveryCatalogOutcome.Success, truncated ? "RECOVERY_FILE_LIMIT" : issues ? "RECOVERY_REVIEW_REQUIRED" : "OK", repositoryId, entries, invalid, completed, inspected, truncated);
            }
            catch (OperationCanceledException) { return Empty(RecoveryCatalogOutcome.Cancelled, "RECOVERY_CANCELLED", repositoryId); }
            catch (TimeoutException) { return Empty(RecoveryCatalogOutcome.TimedOut, "RECOVERY_SCAN_TIMEOUT", repositoryId); }
            catch (InvalidDataException) { return Empty(RecoveryCatalogOutcome.Conflict, "RECOVERY_ROOT_INVALID", repositoryId); }
            catch { return Empty(RecoveryCatalogOutcome.Failed, "RECOVERY_READ_FAILED", repositoryId); }
        }
        static RecoveryEntry Inspect(MediaRepository repository, string repositoryId, string id, RecoveryKind kind, string text)
        {
            string[] lines = Checksummed(text, kind == RecoveryKind.ScreenshotPublication); string phase, peer = "", provider = ""; RecoveryDirection directions = RecoveryDirection.Continue;
            if (kind == RecoveryKind.ScreenshotPublication)
            {
                Demand(lines.Length >= 6 && lines.Length <= 11 && lines[0] == "thor.publish/1" && lines[1] == repositoryId && lines[2] == id); phase = lines[3]; Demand(new[] { "prepared", "staged", "reserved", "publishing", "committed" }.Contains(phase));
                MediaGroup group = MediaCodec.Decode(Decode(lines[4])); Demand(group.LegacyKey == "publish:" + id && group.Tracks.Count >= 1 && group.Tracks.Count <= 2 && (group.Tracks.Count == 1 || (group.Tracks.All(t => t.Role != ScreenRole.Unknown) && group.Tracks.Select(t => t.Role).Distinct().Count() == 2)));
                List<string> names = new List<string>(); List<string> primaries = new List<string>();
                for (int i = 5; i < lines.Length; i++) { string[] fields = lines[i].Split('|'); long length; string name = fields.Length == 3 ? Decode(fields[0]) : ""; Demand(fields.Length == 3 && GroupOperations.Leaf(name) && Sha(fields[1]) && Int64.TryParse(fields[2], out length) && length >= 0 && length <= 32 * 1024 * 1024); names.Add(name); Demand(group.Tracks.Any(t => name == t.RelativePath || name == t.RelativePath + ".thor-meta.txt" || name == t.RelativePath + ".input.csv")); if (name.EndsWith(".thor-meta.txt", StringComparison.Ordinal)) Demand(Int64.Parse(fields[2], CultureInfo.InvariantCulture) <= 65536); if (name.EndsWith(".input.csv", StringComparison.Ordinal)) Demand(Int64.Parse(fields[2], CultureInfo.InvariantCulture) <= 4 * 1024 * 1024); if (group.Tracks.Any(t => t.RelativePath == name && t.Sha256 == fields[1] && t.Length.ToString(CultureInfo.InvariantCulture) == fields[2])) primaries.Add(name); }
                Demand(names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count && primaries.Count == group.Tracks.Count && group.Tracks.All(t => t.Kind == MediaKind.Image && t.RelativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)));
            }
            else if (kind == RecoveryKind.GroupRename)
            {
                Demand(lines.Length >= 8 && lines.Length <= 23 && lines[0] == "thor.group-rename/1" && lines[1] == id && lines[2] == repositoryId); MediaCodec.Id(lines[3]); phase = lines[5]; Demand(new[] { "prepared", "applying", "rolling-back", "committed", "rolled-back" }.Contains(phase));
                MediaGroup group = MediaCodec.Decode(Decode(lines[6])); Demand(group.GroupId == lines[3] && group.Revision.ToString(CultureInfo.InvariantCulture) == lines[4]); ValidateMoves(lines.Skip(7), group, false, repository.MediaRoot); directions |= RecoveryDirection.Rollback;
            }
            else if (kind == RecoveryKind.GroupCopy)
            {
                Demand(lines.Length >= 9 && lines.Length <= 24 && lines[0] == "thor.group-copy/1" && lines[1] == id && lines[2] == repositoryId); peer = MediaCodec.Id(lines[3]); Demand(peer != repositoryId); phase = lines[6]; Demand(new[] { "prepared", "copying", "copied", "committed" }.Contains(phase));
                MediaGroup group = MediaCodec.Decode(Decode(lines[7])); Demand(group.GroupId == lines[4] && group.Revision.ToString(CultureInfo.InvariantCulture) == lines[5]); ValidateMoves(lines.Skip(8), group, true, repository.MediaRoot);
            }
            else
            {
                Demand(lines.Length == 12 && lines[0] == "thor.device-import/1" && lines[1] == id && lines[2] == repositoryId); Guid fixedProvider; Demand(Guid.TryParseExact(lines[3], "D", out fixedProvider) && fixedProvider != Guid.Empty); provider = fixedProvider.ToString("D"); string remote = Decode(lines[4]), root = Decode(lines[5]), target = Decode(lines[6]); int slash = remote.LastIndexOf('/');
                long length, bytes; DateTime modified; Demand(slash > 0 && DeviceImport.DirectoryAllowed(remote.Substring(0, slash)) && GroupOperations.Leaf(remote.Substring(slash + 1)) && MediaSource.Supported(remote) && root == repository.MediaRoot && GroupOperations.Leaf(target) && MediaSource.Supported(target) && Path.GetExtension(target).Equals(Path.GetExtension(remote), StringComparison.OrdinalIgnoreCase) && Sha(lines[7]) && Int64.TryParse(lines[8], out length) && length > 0 && length <= 1024L * 1024 * 1024 && Int64.TryParse(lines[10], out bytes) && bytes >= 0 && bytes <= length && DateTime.TryParseExact(lines[9], "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out modified) && modified.Kind == DateTimeKind.Utc); phase = lines[11]; Demand(new[] { "prepared", "copying", "verified", "stored", "committed" }.Contains(phase));
            }
            if (phase == "committed" || phase == "rolled-back") return null;
            RecoveryState state; Demand(Enum.TryParse<RecoveryState>(phase, true, out state)); return new RecoveryEntry(id, kind, state, directions, kind == RecoveryKind.GroupCopy ? "RECOVERY_REQUIRES_PEER" : kind == RecoveryKind.DeviceImport ? "RECOVERY_REQUIRES_PROVIDER" : "RECOVERY_REVALIDATE", peer, provider);
        }
        static void ValidateMoves(IEnumerable<string> sourceLines, MediaGroup group, bool absolute, string mediaRoot)
        {
            string[] lines = sourceLines.ToArray(); Demand(lines.Length >= 1 && lines.Length <= 16); List<string> trackIds = new List<string>(), targets = new List<string>(), sources = new List<string>(); string peerRoot = "";
            foreach (string line in lines)
            {
                string[] fields = line.Split('|'); long length; Demand(fields.Length == 6); string source = Decode(fields[0]), target = Decode(fields[1]);
                Demand(Sha(fields[2]) && Int64.TryParse(fields[3], out length) && length >= 0 && (fields[5] == "0" || fields[5] == "1"));
                if (absolute) { Demand(Path.IsPathRooted(source) && Path.IsPathRooted(target) && Path.GetDirectoryName(source) == mediaRoot && GroupOperations.Leaf(Path.GetFileName(source)) && GroupOperations.Leaf(Path.GetFileName(target))); string parent = Path.GetDirectoryName(target); Demand(parent != mediaRoot && Path.GetFullPath(target) == target); if (peerRoot.Length == 0) peerRoot = parent; Demand(peerRoot == parent); }
                else Demand(GroupOperations.Leaf(source) && GroupOperations.Leaf(target));
                sources.Add(source); targets.Add(target); if (fields[4].Length > 0) { MediaCodec.Id(fields[4]); Demand(group.Tracks.Any(t => t.TrackId == fields[4] && t.Sha256 == fields[2] && t.Length.ToString(CultureInfo.InvariantCulture) == fields[3] && t.RelativePath == Path.GetFileName(source))); trackIds.Add(fields[4]); }
            }
            Demand(trackIds.Count == group.Tracks.Count && trackIds.Distinct().Count() == trackIds.Count && sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() == sources.Count && targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() == targets.Count);
            // Derived files must belong to an explicitly listed primary track; no arbitrary sidecar moves.
            string[][] fieldsByMove = lines.Select(x => x.Split('|')).ToArray();
            foreach (string[] fields in fieldsByMove.Where(x => x[4].Length == 0))
            {
                string source = Decode(fields[0]), target = Decode(fields[1]);
                Demand(fieldsByMove.Where(x => x[4].Length > 0).Any(primary =>
                {
                    string a = Decode(primary[0]), b = Decode(primary[1]);
                    return (source == a + ".thor-meta.txt" && target == b + ".thor-meta.txt") || (source == a + ".input.csv" && target == b + ".input.csv") || (source == Path.ChangeExtension(a, ".wav") && target == Path.ChangeExtension(b, ".wav"));
                }));
            }
        }
    }
}
