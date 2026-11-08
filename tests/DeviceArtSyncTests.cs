using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    // Copying artwork from the handheld into the local cache: path mapping, cache names, and what gets written.
    internal static class DeviceArtSyncTests
    {
        private sealed class Reader : IDeviceArtReader
        {
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
            public readonly List<string> Asked = new List<string>();
            public Task<byte[]> ReadAsync(string devicePath, CancellationToken token)
            {
                lock (Asked) Asked.Add(devicePath);
                byte[] bytes;
                return Task.FromResult(Files.TryGetValue(devicePath, out bytes) ? bytes : null);
            }
        }

        private static byte[] Png(int size) { byte[] bytes = new byte[size]; Array.Copy(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes, 8); return bytes; }

        internal static void Run()
        {
            const string tree = "content://com.android.externalstorage.documents/tree/primary%3Acocoon/document/";
            Tests.Check(DeviceArtSync.DevicePath(tree + "primary%3Acocoon%2Fdownloaded_media%2Fpsp%2Ficon%2FEver17.jpg") == "/storage/emulated/0/cocoon/downloaded_media/psp/icon/Ever17.jpg",
                "a document URI on primary storage maps to its path under /storage/emulated/0");
            Tests.Check(DeviceArtSync.DevicePath(tree + "primary%3AESDE%2Fnds%2Fcovers%2FASH%20%E8%BF%9C%E5%8F%A4.png") == "/storage/emulated/0/ESDE/nds/covers/ASH 远古.png", "spaces and non-ASCII names are decoded");
            Tests.Check(DeviceArtSync.DevicePath("content://x/tree/1A2B-3C4D%3Aroms/document/1A2B-3C4D%3Aart%2Fa.png") == "/storage/1A2B-3C4D/art/a.png", "a card volume maps to /storage/<volume>");
            Tests.Check(DeviceArtSync.DevicePath("/data/user/0/rip.moth.cocoonshell/files/android_app_icons/a.b.png") == "/data/user/0/rip.moth.cocoonshell/files/android_app_icons/a.b.png", "a plain path stays as it is");
            Tests.Check(DeviceArtSync.DevicePath(tree + "primary%3Acocoon%2F..%2F..%2Fsecret.png") == null && DeviceArtSync.DevicePath("content://no-document-part") == null && DeviceArtSync.DevicePath(null) == null,
                "references that climb out of their folder or cannot be mapped are ignored");
            // Same name the backend computes: sha256(path) + extension (".bin" when there is none).
            Tests.Check(DeviceArtSync.CacheName("/storage/emulated/0/cocoon/downloaded_media/psp/icon/Ever17.jpg") == DeviceApplier.Sha(Encoding.UTF8.GetBytes("/storage/emulated/0/cocoon/downloaded_media/psp/icon/Ever17.jpg")) + ".jpg"
                && DeviceArtSync.CacheName("/storage/emulated/0/a/.hidden").EndsWith(".bin", StringComparison.Ordinal) && DeviceArtSync.CacheName("/storage/emulated/0/a.b/noext").EndsWith(".bin", StringComparison.Ordinal),
                "cache names are the path's hash plus the file's own extension");

            string root = Path.Combine(Tests.Root, "art-sync-" + Guid.NewGuid().ToString("N"));
            Reader reader = new Reader();
            reader.Files["/storage/emulated/0/c/a.png"] = Png(64);
            reader.Files["/storage/emulated/0/c/b 封面.png"] = Png(128);
            reader.Files["/storage/emulated/0/c/denied.jpg"] = Encoding.ASCII.GetBytes("cat: /storage/emulated/0/c/denied.jpg: Permission denied\n");
            reader.Files["/storage/emulated/0/c/movie.mp4"] = Png(64);
            List<string> uris = new List<string> { "/storage/emulated/0/c/a.png", "/storage/emulated/0/c/b 封面.png", "/storage/emulated/0/c/denied.jpg", "/storage/emulated/0/c/movie.mp4",
                "/storage/emulated/0/c/gone.png", "/storage/emulated/0/c/a.png", null };
            DeviceArtSync sync = new DeviceArtSync(reader, root);
            sync.RunAsync(uris, CancellationToken.None).GetAwaiter().GetResult();
            string media = Path.Combine(Path.Combine(root, "_cache"), "media");
            Tests.Check(sync.Total == 4 && sync.Done == 4 && sync.Fetched == 2 && sync.Unreadable == 2, "each distinct image is tried once; videos and empty references are left out [" + sync.Total + " " + sync.Done + " " + sync.Fetched + " " + sync.Unreadable + "]");
            Tests.Check(File.ReadAllBytes(Path.Combine(media, DeviceArtSync.CacheName("/storage/emulated/0/c/a.png"))).Length == 64
                && File.ReadAllBytes(Path.Combine(media, DeviceArtSync.CacheName("/storage/emulated/0/c/b 封面.png"))).Length == 128, "images land in the cache under the backend's names");
            Tests.Check(Directory.GetFiles(media).Length == 2 && !reader.Asked.Contains("/storage/emulated/0/c/movie.mp4"), "error text returned in place of an image is not cached, and videos are never requested");
            reader.Asked.Clear();
            reader.Files["/storage/emulated/0/c/gone.png"] = Png(32);
            DeviceArtSync again = new DeviceArtSync(reader, root);
            again.RunAsync(uris, CancellationToken.None).GetAwaiter().GetResult();
            Tests.Check(again.Fetched == 1 && !reader.Asked.Contains("/storage/emulated/0/c/a.png") && reader.Asked.Contains("/storage/emulated/0/c/gone.png"), "a second pass only asks for what is still missing");
            using (CancellationTokenSource stopped = new CancellationTokenSource())
            {
                stopped.Cancel(); reader.Asked.Clear();
                new DeviceArtSync(reader, Path.Combine(Tests.Root, "art-sync-cancel-" + Guid.NewGuid().ToString("N"))).RunAsync(uris, stopped.Token).GetAwaiter().GetResult();
                Tests.Check(reader.Asked.Count == 0, "a cancelled sync reads nothing");
            }
        }
    }
}
