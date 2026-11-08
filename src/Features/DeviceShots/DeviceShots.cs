using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture
{
    // A picture in the handheld's screenshot folder.
    internal sealed class RemoteShot
    {
        public string Name;
        public long Size;
        public long Mtime; // unix seconds
    }

    internal interface IShotDevice
    {
        string Serial { get; }
        Task<List<RemoteShot>> ListAsync(CancellationToken token);
        Task<byte[]> ReadAsync(RemoteShot shot, CancellationToken token);
        Task DeleteAsync(IList<string> names, CancellationToken token);
    }

    internal sealed class DeviceShotException : Exception
    {
        public readonly string Code;
        public DeviceShotException(string code) : base(code) { Code = code; }

        public static string Describe(Exception error)
        {
            DeviceShotException shot = error as DeviceShotException;
            if (shot == null) return error is OperationCanceledException ? "已取消" : error.Message;
            switch (shot.Code)
            {
                case "SHOTS_ADB_UNAVAILABLE": return "找不到 adb，无法连接掌机";
                case "SHOTS_TIMEOUT": return "掌机没有响应，请检查 USB 连接";
                case "SHOTS_LINK_LOST": return "与掌机的连接中断了";
                case "SHOTS_READ_INCOMPLETE": return "截图没有完整读取，请重试";
                case "SHOTS_DELETE_INCOMPLETE": return "有截图没能从掌机删除";
                default: return "掌机命令失败（" + shot.Code + "）";
            }
        }
    }

    // The handheld's screenshot folder over adb. Only plain file names are ever touched, so
    // nothing in a command needs quoting.
    internal sealed class AdbShotDevice : IShotDevice, IDisposable
    {
        public const string Folder = "/sdcard/Pictures/Screenshots";
        private static readonly Regex Plain = new Regex(@"\A[A-Za-z0-9._-]{1,160}\z");
        private readonly BinaryProcessRunner binary = new BinaryProcessRunner();
        private readonly string adb, serial, folder;

        public AdbShotDevice(string adbPath, string deviceSerial) : this(adbPath, deviceSerial, Folder) { }

        internal AdbShotDevice(string adbPath, string deviceSerial, string folderPath)
        {
            DeviceSessionRegistry.ValidateSerial(deviceSerial);
            if (!Regex.IsMatch(folderPath ?? "", @"\A(/[A-Za-z0-9._-]+)+\z") || folderPath.Contains("..")) throw new ArgumentException("Unsafe device path");
            adb = adbPath; serial = deviceSerial; folder = folderPath;
        }

        public string Serial { get { return serial; } }

        public static bool IsPlainName(string name) { return name != null && Plain.IsMatch(name) && name != "." && name != ".."; }

        private static DeviceShotException Failure(BinaryProcessOutcome outcome)
        {
            return new DeviceShotException(outcome == BinaryProcessOutcome.TimedOut ? "SHOTS_TIMEOUT"
                : outcome == BinaryProcessOutcome.NotFound || outcome == BinaryProcessOutcome.StartDenied ? "SHOTS_ADB_UNAVAILABLE" : "SHOTS_COMMAND_FAILED");
        }

        // exec-out does not report the remote exit status, so the script prints its own last.
        private async Task<string> Shell(string script, int seconds, CancellationToken token)
        {
            string wrapped = "( " + script + " ) 2>&1; rc=$?; echo; echo THOR_RC=$rc";
            BinaryProcessResult result = await binary.RunBytesAsync(new ProcessRequest(adb, "adb.device-shots", new[] { "-s", serial, "exec-out", wrapped }),
                4 * 1024 * 1024, TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
            if (result.Outcome == BinaryProcessOutcome.Cancelled) throw new OperationCanceledException(token);
            if (result.Outcome != BinaryProcessOutcome.Success) throw Failure(result.Outcome);
            string output = Encoding.UTF8.GetString(result.Output).Replace("\r\n", "\n");
            Match status = Regex.Match(output, @"(?:\A|\n)THOR_RC=([0-9]{1,3})\n?\z");
            if (!status.Success) throw new DeviceShotException("SHOTS_LINK_LOST");
            if (status.Groups[1].Value != "0") throw new DeviceShotException("SHOTS_COMMAND_FAILED");
            return output.Substring(0, status.Index);
        }

        public async Task<List<RemoteShot>> ListAsync(CancellationToken token)
        {
            string output = await Shell("cd " + folder + " 2>/dev/null || exit 0; stat -c %s:%Y:%F:%n * 2>/dev/null; true", 30, token).ConfigureAwait(false);
            return Parse(output);
        }

        internal static List<RemoteShot> Parse(string output)
        {
            List<RemoteShot> shots = new List<RemoteShot>();
            foreach (string raw in output.Split('\n'))
            {
                // size:mtime[:type]:name; directories and other non-files are skipped.
                Match line = Regex.Match(raw.Trim(), @"\A([0-9]{1,12}):([0-9]{1,12}):(?:([a-z ]+):)?(.+)\z");
                if (!line.Success || (line.Groups[3].Success && !line.Groups[3].Value.StartsWith("regular"))) continue;
                string name = line.Groups[4].Value;
                if (!IsPlainName(name) || !DeviceShotMirror.IsPicture(name)) continue;
                shots.Add(new RemoteShot { Name = name, Size = long.Parse(line.Groups[1].Value), Mtime = long.Parse(line.Groups[2].Value) });
            }
            return shots;
        }

        public async Task<byte[]> ReadAsync(RemoteShot shot, CancellationToken token)
        {
            if (!IsPlainName(shot.Name) || shot.Size > 96 * 1024 * 1024) throw new ArgumentException("Unsafe screenshot");
            BinaryProcessResult result = await binary.RunBytesAsync(new ProcessRequest(adb, "adb.device-shots", new[] { "-s", serial, "exec-out", "cat " + folder + "/" + shot.Name }),
                (int)shot.Size + 1024, TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
            if (result.Outcome == BinaryProcessOutcome.Cancelled) throw new OperationCanceledException(token);
            if (result.Outcome != BinaryProcessOutcome.Success) throw Failure(result.Outcome);
            if (result.Output.LongLength != shot.Size) throw new DeviceShotException("SHOTS_READ_INCOMPLETE");
            return result.Output;
        }

        public async Task DeleteAsync(IList<string> names, CancellationToken token)
        {
            if (names.Count == 0) return;
            foreach (string name in names) if (!IsPlainName(name)) throw new ArgumentException("Unsafe screenshot name");
            // Batches keep each command line short; the media scan drops the deleted rows from the system gallery.
            for (int start = 0; start < names.Count; start += 40)
            {
                List<string> batch = new List<string>();
                for (int i = start; i < Math.Min(names.Count, start + 40); i++) batch.Add(names[i]);
                string list = string.Join(" ", batch.ToArray());
                await Shell("cd " + folder + " && rm -f " + list + "; for f in " + list + "; do am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d file://" + folder + "/$f >/dev/null 2>&1; done; true", 60, token).ConfigureAwait(false);
            }
        }

        public void Dispose() { binary.Dispose(); }
    }

    internal sealed class ShotSyncProgress
    {
        public int Done, Total;
        public bool Changed; // new local files since the last report
    }

    // Local copy of the handheld's screenshots, named like ThorCapture's own captures so the gallery,
    // pairing, shell preview and export all work unchanged. A stacked "_dual" picture becomes an
    // upper/lower pair; the index remembers which handheld file each local file came from.
    internal sealed class DeviceShotMirror
    {
        public const int UpperWidth = 1920, UpperHeight = 1080, LowerWidth = 1240, LowerHeight = 1080;
        private const string IndexName = ".thor-device-index.txt";
        private readonly string root;
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> byRemote = new Dictionary<string, Entry>(StringComparer.Ordinal);

        private sealed class Entry
        {
            public string Remote;
            public long Size, Mtime;
            public List<string> Locals = new List<string>();
        }

        public DeviceShotMirror(string root)
        {
            this.root = root;
            Directory.CreateDirectory(root);
            Load();
        }

        public string Root { get { return root; } }

        public static string FolderFor(string baseDir, string serial)
        {
            return Path.Combine(baseDir, "device-shots", Regex.Replace(serial ?? "device", @"[^A-Za-z0-9._-]", "_"));
        }

        public static bool IsPicture(string name)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp";
        }

        public int Count { get { lock (gate) return byRemote.Count; } }

        // The handheld file a local picture came from, or null.
        public string RemoteFor(string localPath)
        {
            string name = Path.GetFileName(localPath ?? "");
            lock (gate)
                foreach (Entry entry in byRemote.Values)
                    if (entry.Locals.Contains(name)) return entry.Remote;
            return null;
        }

        // Every local file made from the given handheld file (both halves of a split picture).
        public List<string> LocalsFor(string remote)
        {
            List<string> result = new List<string>();
            lock (gate)
            {
                Entry entry;
                if (byRemote.TryGetValue(remote, out entry)) foreach (string local in entry.Locals) result.Add(Path.Combine(root, local));
            }
            return result;
        }

        public async Task SyncAsync(IShotDevice device, IProgress<ShotSyncProgress> progress, CancellationToken token)
        {
            List<RemoteShot> remote = await device.ListAsync(token).ConfigureAwait(false);
            HashSet<string> present = new HashSet<string>(StringComparer.Ordinal);
            foreach (RemoteShot shot in remote) present.Add(shot.Name);
            // Pictures deleted on the handheld disappear here too.
            List<string> gone = new List<string>();
            lock (gate) foreach (string name in byRemote.Keys) if (!present.Contains(name)) gone.Add(name);
            if (gone.Count > 0) Forget(gone);

            List<RemoteShot> missing = new List<RemoteShot>();
            lock (gate)
                foreach (RemoteShot shot in remote)
                {
                    Entry known;
                    if (!byRemote.TryGetValue(shot.Name, out known) || known.Size != shot.Size || known.Mtime != shot.Mtime || !AllExist(known)) missing.Add(shot);
                }
            // Newest first, so recent shots show up straight away.
            missing.Sort((a, b) => b.Mtime.CompareTo(a.Mtime));
            ShotSyncProgress state = new ShotSyncProgress { Total = missing.Count };
            Report(progress, state);
            try
            {
                // Three reads in flight hide adb's per-command start-up; pictures are stored in order.
                for (int start = 0; start < missing.Count; start += 3)
                {
                    token.ThrowIfCancellationRequested();
                    int count = Math.Min(3, missing.Count - start);
                    Task<byte[]>[] reads = new Task<byte[]>[count];
                    for (int i = 0; i < count; i++) reads[i] = Read(device, missing[start + i], token);
                    for (int i = 0; i < count; i++)
                    {
                        byte[] bytes;
                        try { bytes = await reads[i].ConfigureAwait(false); }
                        catch
                        {
                            // Let the other reads finish before giving up, so nothing is left running.
                            for (int j = i + 1; j < count; j++) try { reads[j].Wait(); } catch (AggregateException) { }
                            throw;
                        }
                        Store(missing[start + i], bytes);
                        state.Done++;
                        state.Changed = true;
                        Report(progress, state);
                        state.Changed = false;
                        if (state.Done % 10 == 0) Save();
                    }
                }
            }
            finally { Save(); }
        }

        // A read that fails straight away still becomes a failed task, so earlier pictures are stored first.
        private static Task<byte[]> Read(IShotDevice device, RemoteShot shot, CancellationToken token)
        {
            try { return device.ReadAsync(shot, token); }
            catch (Exception error)
            {
                TaskCompletionSource<byte[]> failed = new TaskCompletionSource<byte[]>();
                failed.SetException(error);
                return failed.Task;
            }
        }

        private static void Report(IProgress<ShotSyncProgress> progress, ShotSyncProgress state)
        {
            if (progress != null) progress.Report(new ShotSyncProgress { Done = state.Done, Total = state.Total, Changed = state.Changed });
        }

        private bool AllExist(Entry entry)
        {
            if (entry.Locals.Count == 0) return false;
            foreach (string local in entry.Locals) if (!File.Exists(Path.Combine(root, local))) return false;
            return true;
        }

        // Writes the local file(s) for one handheld picture.
        internal void Store(RemoteShot shot, byte[] bytes)
        {
            DateTime time = TimeOf(shot);
            string stem = time.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Tag(shot.Name);
            Entry entry = new Entry { Remote = shot.Name, Size = shot.Size, Mtime = shot.Mtime };
            bool png = Path.GetExtension(shot.Name).Equals(".png", StringComparison.OrdinalIgnoreCase);
            using (MemoryStream memory = new MemoryStream(bytes))
            using (Image picture = Image.FromStream(memory))
            {
                Size size = picture.Size;
                if (size.Width == UpperWidth && size.Height == UpperHeight * 2)
                {
                    // Stacked: the upper screen on top, the narrower lower screen centred below it.
                    entry.Locals.Add(Write("thor-upper-" + stem + ".png", Crop(picture, new Rectangle(0, 0, UpperWidth, UpperHeight)), time));
                    entry.Locals.Add(Write("thor-lower-" + stem + ".png", Crop(picture, new Rectangle((UpperWidth - LowerWidth) / 2, UpperHeight, LowerWidth, LowerHeight)), time));
                }
                else
                {
                    string role = size.Width == LowerWidth && size.Height == LowerHeight ? "lower" : size.Width == UpperWidth && size.Height == UpperHeight ? "upper" : "shot";
                    string name = "thor-" + role + "-" + stem + ".png";
                    entry.Locals.Add(png ? Write(name, bytes, time) : Write(name, Encode(picture), time));
                }
            }
            lock (gate)
            {
                Entry old;
                if (byRemote.TryGetValue(shot.Name, out old))
                    foreach (string local in old.Locals) if (!entry.Locals.Contains(local)) Delete(local);
                byRemote[shot.Name] = entry;
            }
        }

        // Drops handheld files from the mirror (after they were deleted there).
        public void Forget(IEnumerable<string> remotes)
        {
            lock (gate)
                foreach (string remote in remotes)
                {
                    Entry entry;
                    if (!byRemote.TryGetValue(remote, out entry)) continue;
                    foreach (string local in entry.Locals) Delete(local);
                    byRemote.Remove(remote);
                }
            Save();
        }

        private void Delete(string local)
        {
            try { File.Delete(Path.Combine(root, local)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        internal static DateTime TimeOf(RemoteShot shot)
        {
            // Screenshot_20261024-130533.png, Thor_20261107_204811_dual.png; otherwise the file time.
            Match stamp = Regex.Match(shot.Name, @"(20[0-9]{6})[-_]([0-9]{6})");
            DateTime parsed;
            if (stamp.Success && DateTime.TryParseExact(stamp.Groups[1].Value + stamp.Groups[2].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                return parsed;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(shot.Mtime).ToLocalTime();
        }

        // Six stable hex digits from the handheld name: pairs share it, and two shots in the same second differ.
        private static string Tag(string name)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(name));
                return (hash[0].ToString("x2") + hash[1].ToString("x2") + hash[2].ToString("x2"));
            }
        }

        private static byte[] Crop(Image picture, Rectangle area)
        {
            using (Bitmap part = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(part))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.DrawImage(picture, new Rectangle(0, 0, area.Width, area.Height), area, GraphicsUnit.Pixel);
                }
                return Encode(part);
            }
        }

        private static byte[] Encode(Image picture)
        {
            using (MemoryStream memory = new MemoryStream())
            {
                picture.Save(memory, ImageFormat.Png);
                return memory.ToArray();
            }
        }

        private string Write(string name, byte[] bytes, DateTime time)
        {
            string target = Path.Combine(root, name), temporary = target + ".partial";
            File.WriteAllBytes(temporary, bytes);
            File.SetLastWriteTime(temporary, time);
            if (File.Exists(target)) File.Delete(target);
            File.Move(temporary, target);
            return name;
        }

        private void Load()
        {
            string path = Path.Combine(root, IndexName);
            if (!File.Exists(path)) return;
            try
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string[] parts = line.Split('\t');
                    long size, mtime;
                    if (parts.Length < 4 || !AdbShotDevice.IsPlainName(parts[0]) || !long.TryParse(parts[1], out size) || !long.TryParse(parts[2], out mtime)) continue;
                    Entry entry = new Entry { Remote = parts[0], Size = size, Mtime = mtime };
                    for (int i = 3; i < parts.Length; i++) if (AdbShotDevice.IsPlainName(parts[i])) entry.Locals.Add(parts[i]);
                    byRemote[entry.Remote] = entry;
                }
            }
            catch (IOException) { byRemote.Clear(); }
        }

        private void Save()
        {
            StringBuilder text = new StringBuilder();
            lock (gate)
                foreach (Entry entry in byRemote.Values)
                    text.Append(entry.Remote).Append('\t').Append(entry.Size).Append('\t').Append(entry.Mtime).Append('\t').Append(string.Join("\t", entry.Locals.ToArray())).Append('\n');
            string path = Path.Combine(root, IndexName), temporary = path + ".partial";
            File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        // Saves the index now; SyncAsync also saves at the end.
        public void Flush() { Save(); }
    }
}
