using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture
{
    internal static class LibraryThumbnailTests
    {
        internal static void Run() { Task.Run(() => Workflow()).GetAwaiter().GetResult(); }
        private static async Task WaitFor(CocoonBackend backend, BackendState state)
        {
            DateTime limit = DateTime.UtcNow.AddSeconds(15);
            while (backend.State != state && DateTime.UtcNow < limit) await Task.Delay(20);
            if (backend.State != state) throw new Exception("Owned library backend state deadline");
        }
        private static async Task Workflow()
        {
            string backendDir = Path.GetFullPath(Path.Combine(Adb.AppDir(), "../../cocoon-art-backend"));
            string script = Path.GetFullPath(Path.Combine(Adb.AppDir(), "../tools/cocoon-demo.cjs"));
            string root = Path.Combine(Tests.Root, "thumbnail 中文 " + Guid.NewGuid().ToString("N"));
            using (ProcessRunner runner = new ProcessRunner())
            {
                ProcessResult fixture = await runner.RunAsync(new ProcessRequest("node", "fixture.thumbnails", new[] { script, root }), TimeSpan.FromSeconds(30), CancellationToken.None);
                Tests.Check(fixture.Outcome == ProcessOutcome.Success && fixture.CleanupComplete, "library thumbnail fixture uses only an owned offline child");
                await runner.StopAllAsync();
            }
            CocoonBackend backend = new CocoonBackend(null) { BackendDir = backendDir, DataRoot = root };
            try
            {
                backend.Start(); await WaitFor(backend, BackendState.Ready);
                ArtworkThumbnailClient client = new ArtworkThumbnailClient(backend);
                byte[] original = backend.GetBytes("/api/art?id=1&type=icon");
                ArtworkThumbnail cover = await client.GetAsync(1, ArtworkSlot.Icon, 160, 160);
                Tests.Check(!cover.NotModified && cover.Width == 160 && cover.Height == 160 && cover.Bytes.Length > 0 && cover.ETag.StartsWith("\"cth1-") && cover.SourceSha256.Length == 64,
                    "native thumbnail response carries validated PNG bytes, dimensions and content validator");
                using (MemoryStream bytes = new MemoryStream(cover.Bytes))
                using (Image decoded = Image.FromStream(bytes)) Tests.Check(decoded.Width == 160 && decoded.Height == 160, "native thumbnail PNG is actually decodable");
                ArtworkThumbnail unchanged = await client.GetAsync(1, ArtworkSlot.Icon, 160, 160, ArtworkFit.Contain, cover.ETag);
                Tests.Check(unchanged.NotModified && unchanged.Bytes == null && unchanged.ETag == cover.ETag && unchanged.Width == 160,
                    "native conditional GET accepts 304 without decoding or downloading the image again");
                ArtworkThumbnail hero = await client.GetAsync(1, ArtworkSlot.Hero, 160, 160);
                Tests.Check(hero.Width == 160 && hero.Height == 90, "native hero thumbnail retains its aspect ratio");
                ArtworkThumbnail cropped = await client.GetAsync(1, ArtworkSlot.Hero, 160, 160, ArtworkFit.Cover);
                Tests.Check(cropped.Width == 160 && cropped.Height == 160 && cropped.ETag != hero.ETag, "cover crop receives a separate thumbnail validator");
                Tests.Check(Convert.ToBase64String(original) == Convert.ToBase64String(backend.GetBytes("/api/art?id=1&type=icon")), "native thumbnail reads leave the original artwork unchanged");
                bool missing = false;
                try { await client.GetAsync(7, ArtworkSlot.Icon, 160, 160); }
                catch (BackendRequestException failure) { missing = failure.Code == "THUMBNAIL_NOT_FOUND" && failure.Status == 404; }
                Tests.Check(missing, "native missing thumbnail retains its typed local-cache error");
                Tests.Reject(() => client.GetAsync(1, ArtworkSlot.Icon, 4096, 160), "native thumbnail dimensions are rejected before HTTP");
                using (CancellationTokenSource cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel(); bool observed = false;
                    try { await client.GetAsync(1, ArtworkSlot.Icon, 160, 160, ArtworkFit.Contain, null, cancelled.Token); }
                    catch (OperationCanceledException) { observed = true; }
                    Tests.Check(observed, "native thumbnail cancellation avoids starting a request");
                }
                string stage = Path.Combine(root, "_staging/1-logo.png");
                using (Bitmap transparent = new Bitmap(64, 32, PixelFormat.Format32bppArgb))
                {
                    using (Graphics paint = Graphics.FromImage(transparent)) { paint.Clear(Color.Transparent); using (Brush red = new SolidBrush(Color.Red)) paint.FillRectangle(red, 0, 0, 32, 32); }
                    transparent.Save(stage, ImageFormat.Png);
                }
                ArtworkThumbnail logo = await client.GetStagedAsync(1, ArtworkSlot.Logo, "png", 32, 32);
                using (MemoryStream bytes = new MemoryStream(logo.Bytes))
                using (Bitmap decoded = new Bitmap(bytes)) Tests.Check(logo.Width == 32 && logo.Height == 16 && decoded.GetPixel(31, 0).A == 0 && decoded.GetPixel(0, 0).R > 240,
                    "native staged logo retains transparent background and colours");
                using (Bitmap changed = new Bitmap(64, 32)) { using (Graphics paint = Graphics.FromImage(changed)) paint.Clear(Color.Blue); changed.Save(stage, ImageFormat.Png); }
                ArtworkThumbnail updated = await client.GetStagedAsync(1, ArtworkSlot.Logo, "png", 32, 32, ArtworkFit.Contain, logo.ETag);
                Tests.Check(!updated.NotModified && updated.ETag != logo.ETag && updated.SourceSha256 != logo.SourceSha256,
                    "native source edit invalidates the prior staged-image validator");
            }
            finally { backend.Stop(); WaitFor(backend, BackendState.Stopped).GetAwaiter().GetResult(); }
            Tests.Check(backend.State == BackendState.Stopped, "native thumbnail owner closes its backend cleanly");
        }
    }
}
