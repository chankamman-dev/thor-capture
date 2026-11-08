// Brings the artwork a library read from the handheld refers to into the local cache, so covers,
// logos and backgrounds show up here. The catalog only stores where each image lives on the
// device; the backend looks for them in <data root>\_cache\media and never reads the device itself.
//
// Read-only: one "cat" per image. Files that are not images (videos, error text) are not cached.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture
{
    internal interface IDeviceArtReader
    {
        // Bytes of one file on the device, or null when it cannot be read.
        Task<byte[]> ReadAsync(string devicePath, CancellationToken token);
    }

    internal sealed class AdbArtReader : IDeviceArtReader, IDisposable
    {
        private readonly BinaryProcessRunner runner = new BinaryProcessRunner();
        private readonly string adb, serial;
        private readonly bool root;
        public AdbArtReader(string adbPath, string deviceSerial, bool allowRoot)
        {
            DeviceSessionRegistry.ValidateSerial(deviceSerial);
            adb = adbPath; serial = deviceSerial; root = allowRoot;
        }

        public async Task<byte[]> ReadAsync(string devicePath, CancellationToken token)
        {
            string command;
            if (devicePath.StartsWith("/storage/", StringComparison.Ordinal))
                command = "cat '" + devicePath.Replace("'", "'\\''") + "'"; // shared storage: readable by the shell user
            else if (root && Regex.IsMatch(devicePath, @"\A/data/(user/0|data)/rip\.moth\.cocoonshell/files/[A-Za-z0-9._/-]+\z") && !devicePath.Contains(".."))
                command = "su -c 'cat " + devicePath + "'"; // Cocoon's own icon files; only plain names, only when root reads were allowed
            else return null;
            BinaryProcessResult result = await runner.RunBytesAsync(new ProcessRequest(adb, "adb.art-read", new[] { "-s", serial, "exec-out", command }),
                DeviceArtSync.MaxBytes, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result.Outcome == BinaryProcessOutcome.Success ? result.Output : null;
        }

        public void Dispose() { runner.Dispose(); }
    }

    internal sealed class DeviceArtSync
    {
        public const int MaxBytes = 32 * 1024 * 1024;
        private readonly IDeviceArtReader reader;
        private readonly string cache;
        public int Total { get; private set; }
        public int Done { get; private set; }      // looked at, whatever the outcome
        public int Fetched { get; private set; }   // newly written to the cache
        public int Unreadable { get; private set; }
        public event EventHandler Progress;        // raised on worker threads

        public DeviceArtSync(IDeviceArtReader source, string dataRoot)
        {
            if (source == null) throw new ArgumentNullException("source");
            reader = source;
            cache = Path.Combine(Path.Combine(dataRoot, "_cache"), "media");
        }

        // Where the image behind a catalog reference lives on the device: a plain path, or a storage-access
        // document URI such as content://…/document/primary%3Acocoon%2Fdownloaded_media%2F… (as lib/adb.js uriToPath).
        internal static string DevicePath(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return null;
            string path;
            if (uri[0] == '/') path = uri;
            else
            {
                string decoded;
                try { decoded = Uri.UnescapeDataString(uri); } catch (UriFormatException) { return null; }
                string[] parts = decoded.Split(new[] { "/document/" }, StringSplitOptions.None);
                if (parts.Length < 2) return null;
                int colon = parts[1].IndexOf(':');
                if (colon < 1) return null;
                string volume = parts[1].Substring(0, colon), relative = parts[1].Substring(colon + 1);
                path = (volume == "primary" ? "/storage/emulated/0/" : "/storage/" + volume + "/") + relative;
            }
            if (path.Length > 1024 || path.IndexOf('\0') >= 0 || Regex.IsMatch(path, @"[\x00-\x1f]") || Array.IndexOf(path.Split('/'), "..") >= 0) return null;
            return path;
        }

        // The cache name the backend derives from a device path: SHA-256 of the path plus its extension.
        internal static string CacheName(string devicePath)
        {
            string name = devicePath.Substring(devicePath.LastIndexOfAny(new[] { '/', '\\' }) + 1);
            int dot = name.LastIndexOf('.');
            string extension = dot > 0 ? name.Substring(dot) : ".bin";
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(devicePath))).Replace("-", "").ToLowerInvariant() + extension;
        }

        internal static bool IsImagePath(string devicePath)
        {
            return Regex.IsMatch(devicePath, @"\.(png|jpe?g|webp|gif)\z", RegexOptions.IgnoreCase);
        }

        // PNG, JPEG, GIF or WebP by signature. A refused read comes back as error text with a success status.
        internal static bool LooksLikeImage(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 16) return false;
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return true;
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return true;
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38) return true;
            return bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50;
        }

        // Fetches every image in `uris` (in the given order) that is not cached yet, a few at a time.
        public async Task RunAsync(IList<string> uris, CancellationToken token)
        {
            List<string> paths = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string uri in uris)
            {
                string path = DevicePath(uri);
                if (path != null && IsImagePath(path) && seen.Add(path)) paths.Add(path);
            }
            Total = paths.Count; Done = Fetched = Unreadable = 0;
            Directory.CreateDirectory(cache);
            int next = -1;
            object gate = new object();
            Func<Task> worker = async delegate
            {
                for (;;)
                {
                    int index = Interlocked.Increment(ref next);
                    if (index >= paths.Count || token.IsCancellationRequested) return;
                    string target = Path.Combine(cache, CacheName(paths[index]));
                    bool fetched = false, unreadable = false;
                    if (!File.Exists(target) || new FileInfo(target).Length == 0)
                    {
                        byte[] bytes = null;
                        try { bytes = await reader.ReadAsync(paths[index], token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        catch (Exception) { }
                        if (LooksLikeImage(bytes))
                        {
                            // Complete file or nothing: the backend may read the cache at any moment.
                            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                            try { File.WriteAllBytes(temporary, bytes); if (File.Exists(target)) File.Delete(target); File.Move(temporary, target); fetched = true; }
                            catch (IOException) { try { File.Delete(temporary); } catch (IOException) { } unreadable = true; }
                        }
                        else unreadable = true;
                    }
                    lock (gate) { Done++; if (fetched) Fetched++; if (unreadable) Unreadable++; }
                    EventHandler handler = Progress;
                    if (handler != null) handler(this, EventArgs.Empty);
                }
            };
            await Task.WhenAll(Task.Run(worker), Task.Run(worker), Task.Run(worker)).ConfigureAwait(false);
        }
    }
}
