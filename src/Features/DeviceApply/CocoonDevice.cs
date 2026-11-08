// The handheld side of "apply to device": a small, fixed set of operations on Cocoon's catalog
// file and package state. DeviceApplier decides the order and verifies every step; this file
// only knows how each operation is carried out over adb with root.
//
// Nothing here takes a path, package or command from a caller: the catalog location and the
// work directory are constants, and scripts contain no quoting that input could break out of.

using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture
{
    internal sealed class DeviceApplyException : Exception
    {
        public readonly string Code;
        public DeviceApplyException(string code) : base(code) { Code = code; }
    }

    internal enum DeviceFile { Catalog, CatalogJournal, BackupCatalog, BackupJournal, Candidate }

    internal sealed class CocoonDeviceFacts
    {
        public bool Enabled, Running;
        public string Ownership; // "owner:group:mode" of the catalog file
        public string Context;   // its SELinux label
    }

    internal interface ICocoonDevice
    {
        string Serial { get; }
        // Root, package state and the catalog file's attributes. Changes nothing.
        Task<CocoonDeviceFacts> InspectAsync(CancellationToken token);
        // Disables Cocoon and waits until its process is gone, so nothing else writes the catalog.
        Task FreezeAsync(CancellationToken token);
        // Enables Cocoon again and returns to the home screen.
        Task ThawAsync(CancellationToken token);
        // SHA-256 (lower hex) of a file, or null when it does not exist.
        Task<string> HashAsync(DeviceFile file, CancellationToken token);
        Task<byte[]> ReadAsync(DeviceFile file, CancellationToken token);
        // Copies the catalog (and its journal, when present) to the device-side backup.
        Task BackupAsync(CancellationToken token);
        Task PushCandidateAsync(string localPath, CancellationToken token);
        // Writes the candidate (or the backup) over the catalog in place, keeping the file's owner,
        // mode and label, and empties the journal files so a stale journal cannot be replayed.
        Task InstallAsync(bool fromBackup, CancellationToken token);
        Task RemoveCandidateAsync(CancellationToken token);
        // Artwork and video files in Cocoon's shared media folder (see MediaPath). Written next to the
        // final name and moved into place, so Cocoon never sees half a file.
        Task PushMediaAsync(string localPath, string remotePath, CancellationToken token);
        Task<string> HashMediaAsync(string remotePath, CancellationToken token); // null when missing
        Task RemoveMediaAsync(string remotePath, CancellationToken token);
    }

    internal static class CocoonMedia
    {
        public const string Root = "/storage/emulated/0/cocoon/downloaded_media/";

        // Only files directly inside <root>/<platform>/<icon|logo|hero>/ with plain names are ever written.
        public static bool IsMediaPath(string path)
        {
            return path != null && path.StartsWith(Root, StringComparison.Ordinal)
                && Regex.IsMatch(path.Substring(Root.Length), @"\A[A-Za-z0-9_.-]{1,64}/(icon|logo|hero)/[A-Za-z0-9_.-]{1,200}\z") && !path.Contains("..");
        }

        // The storage-access URI Cocoon stores for a file under Root (as lib/cocoondb.js uriFor builds it).
        public static string UriFor(string path)
        {
            if (!IsMediaPath(path)) throw new ArgumentException("Not a Cocoon media path");
            string relative = "primary:cocoon/downloaded_media/" + path.Substring(Root.Length);
            return "content://com.android.externalstorage.documents/tree/" + Uri.EscapeDataString("primary:cocoon") + "/document/" + Uri.EscapeDataString(relative);
        }
    }

    internal sealed class AdbCocoonDevice : ICocoonDevice, IDisposable
    {
        private const string Package = "rip.moth.cocoonshell";
        private readonly BinaryProcessRunner binary = new BinaryProcessRunner();
        private readonly ProcessRunner text = new ProcessRunner();
        private readonly string adb, serial, catalog, work;
        private readonly bool managePackage;
        public string Serial { get { return serial; } }

        public AdbCocoonDevice(string adbPath, string deviceSerial)
            : this(adbPath, deviceSerial, "/data/data/" + Package + "/databases/cocoon_db", "/data/local/tmp/thor-capture", true) { }

        // Rehearsal form: the same commands against a stand-in file in a scratch directory, with the
        // package left alone. Used to check the scripts on a real handheld without touching Cocoon.
        internal AdbCocoonDevice(string adbPath, string deviceSerial, string catalogPath, string workDirectory, bool controlsPackage)
        {
            DeviceSessionRegistry.ValidateSerial(deviceSerial);
            foreach (string path in new[] { catalogPath, workDirectory })
                if (!Regex.IsMatch(path ?? "", @"\A(/[A-Za-z0-9._-]+)+\z") || path.Contains("..")) throw new ArgumentException("Unsafe device path");
            adb = adbPath; serial = deviceSerial; catalog = catalogPath; work = workDirectory; managePackage = controlsPackage;
        }

        private string Path(DeviceFile file)
        {
            switch (file)
            {
                case DeviceFile.Catalog: return catalog;
                case DeviceFile.CatalogJournal: return catalog + "-wal";
                case DeviceFile.BackupCatalog: return work + "/backup_db";
                case DeviceFile.BackupJournal: return work + "/backup_db-wal";
                case DeviceFile.Candidate: return work + "/new_db";
                default: throw new ArgumentOutOfRangeException("file");
            }
        }

        private static DeviceApplyException Failure(BinaryProcessOutcome outcome)
        {
            return new DeviceApplyException(outcome == BinaryProcessOutcome.Cancelled ? "APPLY_CANCELLED" : outcome == BinaryProcessOutcome.TimedOut ? "DEVICE_TIMEOUT"
                : outcome == BinaryProcessOutcome.NotFound || outcome == BinaryProcessOutcome.StartDenied ? "DEVICE_ADB_UNAVAILABLE" : "DEVICE_COMMAND_FAILED");
        }

        // Runs a fixed script and returns its output. exec-out does not report the remote exit status,
        // so the script reports its own on a final line; a missing line means the link broke mid-way.
        private async Task<string> Shell(string script, bool root, int seconds, CancellationToken token)
        {
            if (script.IndexOf('\'') >= 0) throw new ArgumentException("Script must not contain single quotes");
            string wrapped = "( set -e; " + script + " ) 2>&1; rc=$?; echo; echo THOR_RC=$rc";
            string command = root ? "su -c '" + wrapped + "'" : wrapped;
            BinaryProcessResult result = await binary.RunBytesAsync(new ProcessRequest(adb, "adb.device-apply", new[] { "-s", serial, "exec-out", command }),
                1024 * 1024, TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
            if (result.Outcome != BinaryProcessOutcome.Success) throw Failure(result.Outcome);
            string output = Encoding.UTF8.GetString(result.Output).Replace("\r\n", "\n");
            Match status = Regex.Match(output, @"(?:\A|\n)THOR_RC=([0-9]{1,3})\n?\z");
            if (!status.Success) throw new DeviceApplyException("DEVICE_LINK_LOST");
            if (status.Groups[1].Value != "0") throw new DeviceApplyException(root && Regex.IsMatch(output, "(?i)permission denied|not found|inaccessible") ? "DEVICE_ROOT_REQUIRED" : "DEVICE_COMMAND_FAILED");
            return output.Substring(0, status.Index).Trim();
        }

        public async Task<CocoonDeviceFacts> InspectAsync(CancellationToken token)
        {
            string output = await Shell("id -u; stat -c %U:%G:%a " + catalog + "; ls -Z " + catalog, true, 15, token).ConfigureAwait(false);
            string[] lines = output.Split('\n');
            if (lines.Length != 3 || lines[0].Trim() != "0") throw new DeviceApplyException("DEVICE_ROOT_REQUIRED");
            Match label = Regex.Match(lines[2].Trim(), @"\A([A-Za-z0-9_:.,-]+)\s+" + Regex.Escape(catalog) + @"\z");
            if (!Regex.IsMatch(lines[1].Trim(), @"\A[A-Za-z0-9_]+:[A-Za-z0-9_]+:[0-7]{3,4}\z") || !label.Success) throw new DeviceApplyException("DEVICE_CATALOG_MISSING");
            CocoonDeviceFacts facts = new CocoonDeviceFacts { Ownership = lines[1].Trim(), Context = label.Groups[1].Value, Enabled = true };
            if (!managePackage) return facts;
            string state = await Shell("pm list packages -d " + Package + "; echo --; pidof " + Package + " || true", true, 15, token).ConfigureAwait(false);
            string[] parts = state.Split(new[] { "--" }, 2, StringSplitOptions.None);
            facts.Enabled = !Regex.IsMatch(parts[0], @"(?m)^package:" + Regex.Escape(Package) + @"\s*$");
            facts.Running = parts.Length > 1 && parts[1].Trim().Length > 0;
            return facts;
        }

        public async Task FreezeAsync(CancellationToken token)
        {
            if (!managePackage) return;
            await Shell("pm disable-user --user 0 " + Package + "; am force-stop " + Package, true, 20, token).ConfigureAwait(false);
            // A launcher is restarted by the system unless it is disabled; make sure it is really gone.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                string alive = await Shell("pidof " + Package + " || true", true, 10, token).ConfigureAwait(false);
                if (alive.Length == 0) return;
                await Task.Delay(250, token).ConfigureAwait(false);
            }
            throw new DeviceApplyException("DEVICE_APP_STILL_RUNNING");
        }

        public async Task ThawAsync(CancellationToken token)
        {
            if (!managePackage) return;
            await Shell("pm enable " + Package, true, 20, token).ConfigureAwait(false);
            string disabled = await Shell("pm list packages -d " + Package, true, 15, token).ConfigureAwait(false);
            if (Regex.IsMatch(disabled, @"(?m)^package:" + Regex.Escape(Package) + @"\s*$")) throw new DeviceApplyException("DEVICE_ENABLE_FAILED");
            // Bring the launcher back; failing to do so is cosmetic, the home button does the same.
            try { await Shell("am start -a android.intent.action.MAIN -c android.intent.category.HOME", false, 15, token).ConfigureAwait(false); }
            catch (DeviceApplyException) { }
        }

        public async Task<string> HashAsync(DeviceFile file, CancellationToken token)
        {
            string path = Path(file);
            string output = await Shell("if [ -f " + path + " ]; then sha256sum " + path + "; else echo MISSING; fi", true, 60, token).ConfigureAwait(false);
            if (output == "MISSING") return null;
            Match hash = Regex.Match(output, @"\A([a-f0-9]{64})\s");
            if (!hash.Success) throw new DeviceApplyException("DEVICE_COMMAND_FAILED");
            return hash.Groups[1].Value;
        }

        public async Task<byte[]> ReadAsync(DeviceFile file, CancellationToken token)
        {
            // Raw bytes, so no status line can be appended; the caller compares the hash with HashAsync.
            BinaryProcessResult result = await binary.RunBytesAsync(new ProcessRequest(adb, "adb.device-apply",
                new[] { "-s", serial, "exec-out", "su -c 'cat " + Path(file) + "'" }), 64 * 1024 * 1024, TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
            if (result.Outcome != BinaryProcessOutcome.Success) throw Failure(result.Outcome);
            return result.Output;
        }

        public async Task BackupAsync(CancellationToken token)
        {
            // The directory belongs to the shell user so a later push can write into it; root fills in the backup.
            await Shell("mkdir -p " + work, false, 15, token).ConfigureAwait(false);
            await Shell("rm -f " + Path(DeviceFile.BackupCatalog) + " " + Path(DeviceFile.BackupJournal) + "; cp " + catalog + " " + Path(DeviceFile.BackupCatalog)
                + "; if [ -f " + catalog + "-wal ]; then cp " + catalog + "-wal " + Path(DeviceFile.BackupJournal) + "; fi; sync", true, 60, token).ConfigureAwait(false);
        }

        public async Task PushCandidateAsync(string localPath, CancellationToken token)
        {
            await Shell("mkdir -p " + work + "; rm -f " + Path(DeviceFile.Candidate), false, 15, token).ConfigureAwait(false);
            ProcessResult result = await text.RunAsync(new ProcessRequest(adb, "adb.device-apply", new[] { "-s", serial, "push", localPath, Path(DeviceFile.Candidate) }),
                TimeSpan.FromSeconds(120), token).ConfigureAwait(false);
            if (result.Outcome == ProcessOutcome.Cancelled) throw new DeviceApplyException("APPLY_CANCELLED");
            if (result.Outcome != ProcessOutcome.Success) throw new DeviceApplyException(result.Outcome == ProcessOutcome.TimedOut ? "DEVICE_TIMEOUT" : "DEVICE_PUSH_FAILED");
        }

        public Task InstallAsync(bool fromBackup, CancellationToken token)
        {
            // "cat > file" rewrites the existing file, so owner, mode and SELinux label stay exactly as the
            // app created them. The journal files are emptied the same way rather than deleted.
            string wal = catalog + "-wal", shm = catalog + "-shm", source = Path(fromBackup ? DeviceFile.BackupCatalog : DeviceFile.Candidate);
            string journal = fromBackup
                ? "if [ -f " + wal + " ]; then if [ -f " + Path(DeviceFile.BackupJournal) + " ]; then cat " + Path(DeviceFile.BackupJournal) + " > " + wal + "; else : > " + wal + "; fi; fi"
                : "if [ -f " + wal + " ]; then : > " + wal + "; fi";
            return Shell("cat " + source + " > " + catalog + "; " + journal + "; if [ -f " + shm + " ]; then : > " + shm + "; fi; sync", true, 120, token);
        }

        public Task RemoveCandidateAsync(CancellationToken token) { return Shell("rm -f " + Path(DeviceFile.Candidate), true, 15, token); }

        private static string Media(string remotePath)
        {
            if (!CocoonMedia.IsMediaPath(remotePath)) throw new DeviceApplyException("DEVICE_MEDIA_PATH_INVALID");
            return remotePath;
        }

        public async Task PushMediaAsync(string localPath, string remotePath, CancellationToken token)
        {
            string target = Media(remotePath), partial = target + ".thor-partial", folder = target.Substring(0, target.LastIndexOf('/'));
            // Shared storage belongs to the shell user's media group; no root needed to write here.
            await Shell("mkdir -p " + folder + "; rm -f " + partial, false, 15, token).ConfigureAwait(false);
            ProcessResult result = await text.RunAsync(new ProcessRequest(adb, "adb.device-apply", new[] { "-s", serial, "push", localPath, partial }),
                TimeSpan.FromSeconds(300), token).ConfigureAwait(false);
            if (result.Outcome == ProcessOutcome.Cancelled) throw new DeviceApplyException("APPLY_CANCELLED");
            if (result.Outcome != ProcessOutcome.Success) throw new DeviceApplyException(result.Outcome == ProcessOutcome.TimedOut ? "DEVICE_TIMEOUT" : "DEVICE_PUSH_FAILED");
            await Shell("mv -f " + partial + " " + target, false, 15, token).ConfigureAwait(false);
        }

        public async Task<string> HashMediaAsync(string remotePath, CancellationToken token)
        {
            string target = Media(remotePath);
            string output = await Shell("if [ -f " + target + " ]; then sha256sum " + target + "; else echo MISSING; fi", false, 120, token).ConfigureAwait(false);
            if (output == "MISSING") return null;
            Match hash = Regex.Match(output, @"\A([a-f0-9]{64})\s");
            if (!hash.Success) throw new DeviceApplyException("DEVICE_COMMAND_FAILED");
            return hash.Groups[1].Value;
        }

        public Task RemoveMediaAsync(string remotePath, CancellationToken token) { return Shell("rm -f " + Media(remotePath), false, 15, token); }

        public void Dispose() { binary.Dispose(); text.Dispose(); }
    }
}
