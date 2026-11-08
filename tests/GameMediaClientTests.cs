using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using ThorCapture.Media;

namespace ThorCapture
{
    internal static class GameMediaClientTests
    {
        sealed class Fake
        {
            internal Uri Endpoint = new Uri("http://127.0.0.1:43210/");
            internal long Version;
            internal string Library = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
            internal bool Missing, BadIdentity, BadGame;
            internal int Calls, IdentityCalls;
            internal Action<string, int> During;
            internal object Get(string path, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); int call = ++Calls;
                object value;
                if (path == "/api/backend/library/identity") { IdentityCalls++; value = Json.Parse(BadIdentity ? "{\"contract\":\"cocoon.library-identity/1\",\"provider\":\"cocoon\"}" : "{\"contract\":\"cocoon.library-identity/1\",\"provider\":\"cocoon\",\"libraryId\":" + (Library == null ? "null" : Json.Quote(Library)) + "}"); }
                else {
                    if (path != "/api/v2/catalog/items/legacy-item%3A42") throw new Exception("Unexpected route");
                    value = Missing ? null : Json.Parse("{\"id\":\"unresolved-native:42\",\"nativeGameId\":" + (BadGame ? "43" : "42") + ",\"displayName\":\"中文游戏\"}");
                }
                if (During != null) During(path, call); return value;
            }
            internal GameMediaClient Client(IMediaRepository repo) { return new GameMediaClient(repo, () => Endpoint, () => Version, Get); }
        }
        internal static void Run()
        {
            string dir = Path.Combine(Tests.Root, "game-media-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "shot.png"); using (Bitmap bitmap = new Bitmap(8, 8)) { bitmap.SetPixel(1, 1, Color.Red); bitmap.Save(path, ImageFormat.Png); }
            byte[] original = File.ReadAllBytes(path);
            using (MediaRepository repo = new MediaRepository(dir, dir + "-records"))
            {
                MediaGroup group = repo.RefreshAsync(CancellationToken.None).Result.Snapshot.Groups.Single(); Fake fake = new Fake();
                using (GameMediaClient client = fake.Client(repo))
                {
                    Tests.Check(GameMediaClient.NormalizeGameId("00042") == "native:42" && GameMediaClient.NormalizeGameId("legacy-item:42") == "native:42" && GameMediaClient.NormalizeGameId("unresolved-native:42") == "native:42" && GameMediaClient.NormalizeGameId("native:42") == "native:42", "integer legacy and unresolved game IDs share one canonical native namespace");
                    Tests.Reject(() => GameMediaClient.NormalizeGameId("42/../../"), "game ID path injection rejected before transport");
                    Tests.Reject(() => GameMediaClient.NormalizeGameId("9007199254740992"), "game ID beyond exact JS integer range rejected");
                    GameMediaResult resolved = client.ResolveGameAsync(42, CancellationToken.None).Result;
                    Tests.Check(resolved.Outcome == GameMediaOutcome.Success && resolved.Game.GameId == "native:42" && resolved.Game.DisplayName == "中文游戏" && fake.IdentityCalls == 2, "game resolution verifies single-item V2 response and persistent identity before and after without depending on game pagination");
                    fake.Library = null; Tests.Check(client.ResolveGameAsync("42", CancellationToken.None).Result.Outcome == GameMediaOutcome.EmptyLibrary && repo.GetSnapshot().FindGroup(group.GroupId).Game == null, "unimported library cannot create a temporary game namespace or media association"); fake.Library = resolved.Game.LibraryId;
                    fake.BadIdentity = true; Tests.Check(client.ResolveGameAsync("42", CancellationToken.None).Result.Code == "LIBRARY_IDENTITY_INVALID", "missing identity field is invalid rather than confused with explicit not-imported null"); fake.BadIdentity = false;
                    fake.BadGame = true; Tests.Check(client.AssociateAsync(group.GroupId, group.Revision, "42", CancellationToken.None).Result.Code == "GAME_RESPONSE_INVALID" && repo.GetSnapshot().FindGroup(group.GroupId).Game == null, "mismatching native game response cannot write a media association"); fake.BadGame = false;
                    fake.Missing = true; Tests.Check(client.AssociateAsync(group.GroupId, group.Revision, "42", CancellationToken.None).Result.Outcome == GameMediaOutcome.NotFound && repo.GetSnapshot().FindGroup(group.GroupId).Game == null, "missing game validation returns not-found and does not link group"); fake.Missing = false;
                    GameMediaResult linked = client.AssociateAsync(group.GroupId, group.Revision, "legacy-item:42", CancellationToken.None).Result;
                    group = linked.RepositoryResult.Group; GameReference reference = group.Game;
                    Tests.Check(linked.Outcome == GameMediaOutcome.Success && linked.Groups.Count == 1 && client.QueryByGameAsync("unresolved-native:42", CancellationToken.None).Result.Groups.Count == 1, "validated group-level association and canonical game query share the persistent reference");
                    Tests.Check(client.AssociateAsync(group.GroupId, group.Revision - 1, "42", CancellationToken.None).Result.Outcome == GameMediaOutcome.RepositoryFailure && repo.GetSnapshot().FindGroup(group.GroupId).Revision == group.Revision, "client preserves repository compare-and-swap conflict without unconditional retries");
                    EventHandler<MediaSnapshotEventArgs> swap = delegate { fake.Library = "dddddddd-dddd-dddd-dddd-dddddddddddd"; };
                    repo.Changed += swap; GameMediaResult changed;
                    try { changed = client.AssociateAsync(group.GroupId, group.Revision, "42", CancellationToken.None).Result; } finally { repo.Changed -= swap; }
                    Tests.Check(changed.Outcome == GameMediaOutcome.ContextChanged && changed.RepositoryResult.Outcome == MediaOutcome.Success && changed.RepositoryResult.Group.Game.Key == reference.Key, "library replacement after local commit exposes committed old-namespace reference instead of claiming atomic rollback");
                    group = changed.RepositoryResult.Group; fake.Library = reference.LibraryId;
                    fake.IdentityCalls = 0; fake.During = (route, call) => { if (fake.IdentityCalls == 3) throw new BackendRequestException("BACKEND_TIMEOUT", 0); };
                    GameMediaResult timeout = client.AssociateAsync(group.GroupId, group.Revision, "42", CancellationToken.None).Result; fake.During = null;
                    Tests.Check(timeout.Outcome == GameMediaOutcome.Failed && timeout.Code == "BACKEND_TIMEOUT" && timeout.RepositoryResult.Outcome == MediaOutcome.Success, "post-commit HTTP failure retains explicit committed repository result for safe review");
                    group = timeout.RepositoryResult.Group;
                    fake.Missing = true; GameMediaResult missing = client.CheckReferenceAsync(reference, CancellationToken.None).Result;
                    Tests.Check(missing.Outcome == GameMediaOutcome.NotFound && missing.Groups.Count == 1 && repo.GetSnapshot().FindGroup(group.GroupId).Game.Key == reference.Key, "deleted game remains queryable as an explicit invalid reference without silently unlinking stored media"); fake.Missing = false;
                    fake.Library = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
                    Tests.Check(client.QueryByGameAsync("42", CancellationToken.None).Result.Groups.Count == 0 && client.CheckReferenceAsync(reference, CancellationToken.None).Result.Outcome == GameMediaOutcome.ContextChanged && client.QueryStored(reference).Count == 1, "same game number in a replacement library cannot inherit another library media while old references remain recoverable"); fake.Library = reference.LibraryId;
                    fake.Endpoint = new Uri("http://127.0.0.1:43211/"); fake.Version++;
                    Tests.Check(client.QueryByGameAsync("42", CancellationToken.None).Result.Groups.Count == 1, "backend process restart with new endpoint preserves media through persistent library UUID");
                    fake.During = (route, call) => { if (route.StartsWith("/api/v2/")) fake.Endpoint = new Uri("http://127.0.0.1:43212/"); };
                    Tests.Check(client.ResolveGameAsync("42", CancellationToken.None).Result.Outcome == GameMediaOutcome.ContextChanged, "late game response from previous endpoint is rejected"); fake.During = null;
                    fake.During = (route, call) => { if (route.StartsWith("/api/v2/")) fake.Version++; };
                    Tests.Check(client.ResolveGameAsync("42", CancellationToken.None).Result.Outcome == GameMediaOutcome.ContextChanged, "same endpoint reused by a different backend generation still rejects late response"); fake.During = null;
                    fake.During = (route, call) => { if (route.StartsWith("/api/v2/")) fake.Library = "cccccccc-cccc-cccc-cccc-cccccccccccc"; };
                    Tests.Check(client.AssociateAsync(group.GroupId, group.Revision, "42", CancellationToken.None).Result.Code == "LIBRARY_REPLACED" && repo.GetSnapshot().FindGroup(group.GroupId).Game.Key == reference.Key, "in-place library replacement during validation cannot overwrite prior media association"); fake.During = null; fake.Library = reference.LibraryId;
                    using (CancellationTokenSource cancellation = new CancellationTokenSource())
                    { cancellation.Cancel(); int calls = fake.Calls; Tests.Check(client.ResolveGameAsync("42", cancellation.Token).Result.Outcome == GameMediaOutcome.Cancelled && calls == fake.Calls, "pre-cancelled game media request launches no HTTP calls"); }
                    using (CancellationTokenSource cancellation = new CancellationTokenSource())
                    { fake.During = (route, call) => cancellation.Cancel(); Tests.Check(client.ResolveGameAsync("42", cancellation.Token).Result.Outcome == GameMediaOutcome.Cancelled, "inflight cancellation rejects a returned response without changing associations"); fake.During = null; }
                    fake.Endpoint = null;
                    Tests.Check(client.ResolveGameAsync("42", CancellationToken.None).Result.Code == "BACKEND_NOT_READY", "unready backend has a typed failure rather than invented empty library");
                    Tests.Check(client.UnlinkAsync(group.GroupId, group.Revision, CancellationToken.None).Result.Outcome == GameMediaOutcome.Success && repo.GetSnapshot().FindGroup(group.GroupId).Game == null, "explicit unlink works offline using group revision without mutating original media");
                    client.Dispose(); Tests.Check(client.ResolveGameAsync("42", CancellationToken.None).Result.Outcome == GameMediaOutcome.Cancelled, "closed client rejects new requests through cancellation");
                    Tests.Check(File.ReadAllBytes(path).SequenceEqual(original), "game resolution query linkage conflicts cancellation and unlink leave original bytes unchanged");
                }
            }
        }
    }
}
