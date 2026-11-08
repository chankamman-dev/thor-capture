using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Features.Library;
using ThorCapture.Media;

namespace ThorCapture
{
    internal static class RecoveryCatalogTests
    {
        sealed class Step : IProgress<int> { readonly CancellationTokenSource cancel; internal Step(CancellationTokenSource cancel) { this.cancel = cancel; } public void Report(int value) { cancel.Cancel(); } }
        sealed class ByteStep : IProgress<long> { readonly CancellationTokenSource cancel; internal ByteStep(CancellationTokenSource cancel) { this.cancel = cancel; } public void Report(long value) { cancel.Cancel(); } }
        sealed class Provider : IReadOnlyMediaProvider
        {
            internal readonly Guid Id = Guid.NewGuid(); internal byte[] Bytes; internal RemoteMedia Item;
            public Guid InstanceId { get { return Id; } }
            public Task<ReadOnlyCollection<RemoteMedia>> ListAsync(string directory, CancellationToken token) { return Task.FromResult(new ReadOnlyCollection<RemoteMedia>(new[] { Item })); }
            public Task<Stream> OpenReadAsync(RemoteMedia item, long offset, CancellationToken token) { Stream stream = new MemoryStream(Bytes, false); stream.Position = offset; return Task.FromResult(stream); }
        }
        static string Folder() { string path = Path.Combine(Tests.Root, "catalog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        static byte[] Png(Color color) { using (Bitmap image = new Bitmap(15, 15)) using (Graphics draw = Graphics.FromImage(image)) using (MemoryStream stream = new MemoryStream()) { draw.Clear(color); image.Save(stream, ImageFormat.Png); return stream.ToArray(); } }
        static string Hash(byte[] bytes) { using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""); }
        static void Rehash(string path) { string text = File.ReadAllText(path); int last = text.LastIndexOf('\n'); string body = text.Substring(0, last + 1); File.WriteAllText(path, body + Hash(Encoding.UTF8.GetBytes(body)), new UTF8Encoding(false)); }
        internal static void Run() { Task.Run((Func<Task>)RunAsync).GetAwaiter().GetResult(); }
        static async Task RunAsync()
        {
            string root = Folder(), data = Folder(), peerRoot = Folder(), peerData = Folder(); using (MediaRepository repo = new MediaRepository(root, data)) using (MediaRepository peer = new MediaRepository(peerRoot, peerData))
            {
                await repo.RefreshAsync(CancellationToken.None); await peer.RefreshAsync(CancellationToken.None); string repoId = repo.GetSnapshot().RepositoryId;
                RecoveryCatalogResult catalog = await RecoveryCatalog.ScanAsync(repo, repoId, CancellationToken.None);
                Tests.Check(catalog.Outcome == RecoveryCatalogOutcome.Success && catalog.Entries.Count == 0 && !Directory.Exists(Path.Combine(data, "transactions")), "recovery catalog on an empty repository creates no files or directories");
                catalog = await RecoveryCatalog.ScanAsync(repo, "stale", CancellationToken.None); Tests.Check(catalog.Code == "RECOVERY_SCOPE_CHANGED", "read-only recovery scan refuses stale repository scope");
                using (CancellationTokenSource cancel = new CancellationTokenSource()) { cancel.Cancel(); catalog = await RecoveryCatalog.ScanAsync(repo, repoId, cancel.Token); Tests.Check(catalog.Outcome == RecoveryCatalogOutcome.Cancelled, "pre-cancelled recovery catalog performs no discovery"); }
                MediaRepositoryResult result;
                using (CancellationTokenSource cancel = new CancellationTokenSource()) result = await repo.PublishGroupAsync(repoId, new[] { new PublishedPngInput("publish-upper.png", ScreenRole.Upper, Png(Color.Red)), new PublishedPngInput("publish-lower.png", ScreenRole.Lower, Png(Color.Blue)) }, cancel.Token, new Step(cancel));
                string publishId = result.Group.LegacyKey.Substring(8);
                MediaGroup renameGroup = (await repo.PublishGroupAsync(repoId, new[] { new PublishedPngInput("rename.png", ScreenRole.Unknown, Png(Color.Green)) }, CancellationToken.None)).Group;
                GroupOperationPlan rename = await GroupOperations.PlanRenameAsync(repo, renameGroup.GroupId, "renamed", CancellationToken.None); using (CancellationTokenSource cancel = new CancellationTokenSource()) await GroupOperations.ExecuteAsync(repo, rename, true, cancel.Token, new Step(cancel));
                MediaGroup copyGroup = (await repo.PublishGroupAsync(repoId, new[] { new PublishedPngInput("copy-upper.png", ScreenRole.Upper, Png(Color.Gold)), new PublishedPngInput("copy-lower.png", ScreenRole.Lower, Png(Color.Purple)) }, CancellationToken.None)).Group;
                GroupOperationPlan copy = await GroupOperations.PlanCopyAsync(repo, peer, copyGroup.GroupId, CancellationToken.None); using (CancellationTokenSource cancel = new CancellationTokenSource()) await GroupOperations.ExecuteCopyAsync(repo, peer, copy, true, cancel.Token, new Step(cancel));
                Provider provider = new Provider { Bytes = Png(Color.Cyan) }; provider.Item = new RemoteMedia("/sdcard/Pictures/Selected", "import.png", provider.Bytes.Length, DateTime.UtcNow, Hash(provider.Bytes)); ImportScan scan = await DeviceImport.ScanAsync(provider, new[] { provider.Item.Directory }, CancellationToken.None); DeviceImportPlan import = DeviceImport.Plan(scan, provider.Item, repo); using (CancellationTokenSource cancel = new CancellationTokenSource()) await DeviceImport.ImportAsync(provider, repo, import, true, cancel.Token, new ByteStep(cancel));
                string folder = Path.Combine(data, "transactions"); string[] beforePaths = Directory.GetFiles(folder); byte[][] beforeBytes = beforePaths.Select(File.ReadAllBytes).ToArray(); MediaSnapshot snapshot = repo.GetSnapshot();
                catalog = await RecoveryCatalog.ScanAsync(repo, repoId, CancellationToken.None);
                Tests.Check(catalog.Outcome == RecoveryCatalogOutcome.Success && catalog.Entries.Count == 4, "catalog discovers all four genuine unfinished transaction types from real operation logs");
                RecoveryEntry publication = catalog.Entries.Single(x => x.TransactionId == publishId); Tests.Check(publication.Kind == RecoveryKind.ScreenshotPublication && publication.State == RecoveryState.Publishing && publication.Directions == RecoveryDirection.Continue, "partial publication offers only explicit continuation");
                RecoveryEntry renaming = catalog.Entries.Single(x => x.TransactionId == rename.TransactionId); Tests.Check(renaming.Kind == RecoveryKind.GroupRename && renaming.State == RecoveryState.Applying && renaming.Directions == (RecoveryDirection.Continue | RecoveryDirection.Rollback), "unfinished rename offers continue and rollback directions");
                RecoveryEntry copying = catalog.Entries.Single(x => x.TransactionId == copy.TransactionId); Tests.Check(copying.Kind == RecoveryKind.GroupCopy && copying.PeerRepositoryId == peer.GetSnapshot().RepositoryId && copying.Code == "RECOVERY_REQUIRES_PEER", "copy catalog returns only the peer UUID and a fixed prerequisite code");
                RecoveryEntry importing = catalog.Entries.Single(x => x.TransactionId == import.TransactionId); Tests.Check(importing.Kind == RecoveryKind.DeviceImport && importing.ProviderId == provider.Id.ToString("D") && importing.Code == "RECOVERY_REQUIRES_PROVIDER", "import catalog requires same fixed provider without opening a device or remote directory");
                Tests.Check(ReferenceEquals(snapshot, repo.GetSnapshot()) && beforePaths.SequenceEqual(Directory.GetFiles(folder)) && beforePaths.Select((path, i) => File.ReadAllBytes(path).SequenceEqual(beforeBytes[i])).All(x => x), "catalog is strictly read-only and neither recovers files nor changes the media snapshot");
                Tests.Check(catalog.CompletedCount == 2 && catalog.Entries.All(x => x.RequiresConfirmation && x.RequiresRevalidation), "completed transaction logs are omitted and every suggested direction requires explicit validation and confirmation");
                Tests.Check(typeof(RecoveryEntry).GetFields(BindingFlags.Instance | BindingFlags.Public).Where(x => x.FieldType == typeof(string)).Select(x => x.Name).OrderBy(x => x).SequenceEqual(new[] { "Code", "PeerRepositoryId", "ProviderId", "TransactionId" }), "catalog schema contains no free text paths names raw logs or device serial fields");
                RecoveryCatalogResult old = catalog; await repo.RecoverPublicationAsync(publishId, CancellationToken.None); catalog = await RecoveryCatalog.ScanAsync(repo, repoId, CancellationToken.None); Tests.Check(catalog.Entries.Count == 3 && old.Entries.Count == 4, "immutable catalog result remains frozen after subsequent recovery");
                string corruptId = Guid.NewGuid().ToString("N"), lockedId = Guid.NewGuid().ToString("N"), oversizedId = Guid.NewGuid().ToString("N"), wrongId = Guid.NewGuid().ToString("N"); File.WriteAllText(Path.Combine(folder, corruptId + ".thor-import"), "private path and token must never appear in results"); File.WriteAllText(Path.Combine(folder, "private-device-serial.thor-copy"), "secret"); File.WriteAllBytes(Path.Combine(folder, oversizedId + ".thor-rename"), new byte[1024 * 1024 + 1]); string locked = Path.Combine(folder, lockedId + ".thor-copy"); File.WriteAllText(locked, "locked");
                using (FileStream lease = File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None)) { catalog = await RecoveryCatalog.ScanAsync(repo, repoId, CancellationToken.None); Tests.Check(catalog.Outcome == RecoveryCatalogOutcome.Partial && catalog.Entries.Single(x => x.TransactionId == lockedId).State == RecoveryState.Unreadable, "unreadable transaction returns typed disabled entry without leaking exception text"); }
                Tests.Check(catalog.Entries.Single(x => x.TransactionId == corruptId).State == RecoveryState.Corrupt && catalog.Entries.Single(x => x.TransactionId == oversizedId).Directions == RecoveryDirection.None && catalog.InvalidNames == 1, "malformed oversize and non-UUID logs fail closed within fixed bounds");
                string importPath = Path.Combine(folder, import.TransactionId + ".thor-import"), foreign = Path.Combine(folder, wrongId + ".thor-import"); string foreignText = File.ReadAllText(importPath).Replace(import.TransactionId, wrongId).Replace(repoId, Guid.NewGuid().ToString("N")); File.WriteAllText(foreign, foreignText); Rehash(foreign); catalog = await RecoveryCatalog.ScanAsync(repo, repoId, CancellationToken.None); Tests.Check(catalog.Entries.Single(x => x.TransactionId == wrongId).Directions == RecoveryDirection.None, "foreign repository journal remains non-actionable even with a valid checksum");
                string[] foreignLines = File.ReadAllText(foreign).Split('\n'); foreignLines[2] = repoId; foreignLines[11] = "execute-shell"; File.WriteAllText(foreign, String.Join("\n", foreignLines)); Rehash(foreign); catalog = await RecoveryCatalog.ScanAsync(repo, repoId, CancellationToken.None); Tests.Check(catalog.Entries.Single(x => x.TransactionId == wrongId).State == RecoveryState.Corrupt, "unknown recovery phases cannot invent executable directions");
                for (int i = 0; i < 205; i++) File.WriteAllText(Path.Combine(folder, "ignored-" + i + ".tmp"), "not a journal"); catalog = await RecoveryCatalog.ScanAsync(repo, repoId, CancellationToken.None); Tests.Check(catalog.Truncated && catalog.InspectedCount == 200 && catalog.Code == "RECOVERY_FILE_LIMIT", "transaction discovery has a strict total file-count budget including ignored names");
            }
        }
    }
}
