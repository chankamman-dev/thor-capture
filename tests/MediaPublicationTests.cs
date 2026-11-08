using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Media;

namespace ThorCapture
{
    internal static class MediaPublicationTests
    {
        sealed class Step : IProgress<int> { readonly Action<int> action; internal Step(Action<int> action) { this.action = action; } public void Report(int value) { action(value); } }
        static string Folder() { string path = Path.Combine(Tests.Root, "publish-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        static byte[] Png(Color color) { using (Bitmap image = new Bitmap(20, 20)) using (Graphics g = Graphics.FromImage(image)) using (MemoryStream s = new MemoryStream()) { g.Clear(color); image.Save(s, ImageFormat.Png); return s.ToArray(); } }
        internal static void Run() { Task.Run((Func<Task>)RunAsync).GetAwaiter().GetResult(); }
        static async Task RunAsync()
        {
            string root = Folder(), data = Folder(); using (MediaRepository repo = new MediaRepository(root, data))
            {
                await repo.RefreshAsync(CancellationToken.None); string id = repo.GetSnapshot().RepositoryId; byte[] bytes = Png(Color.Red); string metaFixture = Path.Combine(Folder(), "meta.png"); new CaptureMetadata { Package = "fixture.game", Status = "identified", CapturedUtc = DateTime.UtcNow, ObservedUtc = DateTime.UtcNow }.Save(metaFixture); PublishedPngInput upper = new PublishedPngInput("thor-upper-fixture.png", ScreenRole.Upper, bytes, File.ReadAllBytes(metaFixture + ".thor-meta.txt")); bytes[0] = 0;
                MediaRepositoryResult result = await repo.PublishGroupAsync(id, new[] { upper, new PublishedPngInput("thor-lower-fixture.png", ScreenRole.Lower, Png(Color.Blue)) }, CancellationToken.None);
                Tests.Check(result.Outcome == MediaOutcome.Success && result.Group.Tracks.Count == 2 && result.Group.Tracks.All(x => x.State == TrackState.Present), "fixed-byte publication writes paired PNGs into a single stable group");
                Tests.Check(result.Group.Tracks.Single(x => x.Role == ScreenRole.Upper).Package == "fixture.game", "PNG metadata publishes before its primary frame and survives source inspection");
                Tests.Check(File.ReadAllBytes(Path.Combine(root, "thor-upper-fixture.png"))[0] == 137, "publication input defensively copies caller bytes");
                string group = result.Group.GroupId; await repo.RefreshAsync(CancellationToken.None);
                Tests.Check(repo.GetSnapshot().Groups.Single().GroupId == group, "watcher reconciliation preserves publication group identity");
                result = await repo.PublishGroupAsync(id, new[] { upper }, CancellationToken.None);
                Tests.Check(result.Code == "PUBLICATION_NAME_EXISTS" && result.Group == null, "preexisting target never appears as newly published success");
                result = await repo.PublishGroupAsync("wrong", new[] { new PublishedPngInput("wrong.png", ScreenRole.Unknown, Png(Color.Blue)) }, CancellationToken.None);
                Tests.Check(result.Code == "PUBLICATION_ROOT_CHANGED" && !File.Exists(Path.Combine(root, "wrong.png")), "publication refuses stale repository scope");
                result = await repo.PublishGroupAsync(id, new[] { new PublishedPngInput("../escape.png", ScreenRole.Upper, Png(Color.Red)) }, CancellationToken.None);
                Tests.Check(result.Code == "INVALID_PUBLICATION", "publication accepts only flat leaf names");
                result = await repo.PublishGroupAsync(id, new[] { new PublishedPngInput("one.png", ScreenRole.Upper, Png(Color.Red)), new PublishedPngInput("two.png", ScreenRole.Upper, Png(Color.Blue)) }, CancellationToken.None);
                Tests.Check(result.Code == "INVALID_PUBLICATION", "duplicate dual-screen roles fail before filesystem changes");
                byte[] corrupt = Png(Color.Red); corrupt[50] ^= 1; result = await repo.PublishGroupAsync(id, new[] { new PublishedPngInput("bad.png", ScreenRole.Unknown, corrupt) }, CancellationToken.None);
                Tests.Check(result.Outcome != MediaOutcome.Success && !File.Exists(Path.Combine(root, "bad.png")), "PNG chunk checksums reject corrupt fixed bytes before publication");
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    result = await repo.PublishGroupAsync(id, new[] { new PublishedPngInput("partial-upper.png", ScreenRole.Upper, Png(Color.Green)), new PublishedPngInput("partial-lower.png", ScreenRole.Lower, Png(Color.Yellow)) }, cancel.Token, new Step(n => { if (n == 1) cancel.Cancel(); }));
                    Tests.Check(result.Code == "PUBLICATION_CANCELLED" && result.Group != null && result.Group.Tracks.Count(x => x.State == TrackState.Present) == 1 && !File.Exists(Path.Combine(root, "partial-lower.png")), "cancellation returns only the genuinely published PNG with recoverable missing peer");
                    string partialId = result.Group.GroupId, tx = result.Group.LegacyKey.Substring(8), trackId = result.Group.Tracks.Single(x => x.Role == ScreenRole.Lower).TrackId;
                    await repo.RefreshAsync(CancellationToken.None); Tests.Check(repo.GetSnapshot().FindGroup(partialId).Tracks.Count == 2, "partial publication reservation prevents watcher from creating a competing single-screen group");
                    MediaGroup current = repo.GetSnapshot().FindGroup(partialId); await repo.SetAnnotationsAsync(partialId, current.Revision, true, new[] { "kept" }, CancellationToken.None);
                    using (MediaRepository reopened = new MediaRepository(root, data))
                    {
                        await reopened.RefreshAsync(CancellationToken.None); result = await reopened.RecoverPublicationAsync(tx, CancellationToken.None);
                        Tests.Check(result.Outcome == MediaOutcome.Success && result.Group.GroupId == partialId && result.Group.Tracks.Single(x => x.Role == ScreenRole.Lower).TrackId == trackId, "restart recovery publishes staged peer with the original reserved UUIDs");
                        Tests.Check(result.Group.Favorite && result.Group.Tags.Single() == "kept", "publication recovery preserves annotations edited after partial completion");
                        result = await reopened.RecoverPublicationAsync(tx, CancellationToken.None); Tests.Check(result.Outcome == MediaOutcome.Success && reopened.GetSnapshot().Groups.Count == 2, "committed publication recovery is idempotent without duplicate groups");
                        string journal = Path.Combine(data, "transactions", tx + ".thor-publish"); File.AppendAllText(journal, "tamper"); result = await reopened.RecoverPublicationAsync(tx, CancellationToken.None);
                        Tests.Check(result.Code == "PUBLICATION_JOURNAL_INVALID", "tampered recovery journal fails closed");
                    }
                }
                using (CancellationTokenSource cancel = new CancellationTokenSource()) { cancel.Cancel(); result = await repo.PublishGroupAsync(id, new[] { new PublishedPngInput("cancelled.png", ScreenRole.Unknown, Png(Color.Red)) }, cancel.Token); Tests.Check(result.Outcome == MediaOutcome.Cancelled && result.Group == null && !File.Exists(Path.Combine(root, "cancelled.png")), "pre-cancelled publication touches no final filename"); }
            }
        }
    }
}
