// Applies reviewed changes to the handheld's Cocoon catalog: stop Cocoon, take a fresh snapshot,
// patch it on this computer, put it back, start Cocoon. Every step is verified by hash, and a
// journal on disk says how far a run got so an interrupted one can be finished or undone.
//
//   frozen      Cocoon is disabled; the catalog on the device has not been touched.
//   installing  the catalog is being (or has been) replaced; its hash decides which way to recover.
//
// Whatever happens after Cocoon was disabled, the run ends by enabling it again or leaves the
// journal behind so Recover() can.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    // One reviewed metadata change for one game; values are the catalog's own (text, number or null).
    internal sealed class DeviceFieldChange
    {
        public long GameId;
        public string FileName, PlatformId; // identity the catalog row must still have; null skips the check
        public Dictionary<string, object> Before = new Dictionary<string, object>(), After = new Dictionary<string, object>();
        // Fields set without a reviewed old value (artwork source markers only; see snapshotPatch.js).
        public HashSet<string> Unguarded = new HashSet<string>();
        // Files the new values refer to, put on the device before the catalog is touched.
        public List<DeviceMediaFile> Media = new List<DeviceMediaFile>();
    }

    internal sealed class DeviceMediaFile
    {
        public string LocalPath, RemotePath, Sha256;
    }

    internal sealed class DeviceApplyResult
    {
        public string Sha256;       // catalog now on the device
        public string SnapshotRoot; // local folder holding _data\cocoon_db identical to it
        public int Games, FieldsWritten, FieldsUnchanged;
        public string BackupDirectory;
    }

    internal sealed class SnapshotPatchResult
    {
        public string Sha256;
        public int Games, FieldsWritten, FieldsUnchanged;
    }

    internal interface ISnapshotPatcher
    {
        // Patches the catalog file in place (merging a -wal beside it) or throws DeviceApplyException.
        Task<SnapshotPatchResult> PatchAsync(string catalogPath, IList<DeviceFieldChange> changes, CancellationToken token);
    }

    internal sealed class DeviceApplyJournal
    {
        public string State, Serial, OriginalSha, OriginalJournalSha, NewSha, StartedUtc;

        public static string PathIn(string directory) { return Path.Combine(directory, "journal.json"); }

        public static DeviceApplyJournal Load(string directory)
        {
            string file = PathIn(directory);
            if (!File.Exists(file)) return null;
            object value;
            try { value = Json.Parse(File.ReadAllText(file, new UTF8Encoding(false, true))); } catch (Exception) { throw new DeviceApplyException("APPLY_JOURNAL_INVALID"); }
            DeviceApplyJournal journal = new DeviceApplyJournal
            {
                State = Json.Text(value, "state"), Serial = Json.Text(value, "serial"), OriginalSha = Json.Text(value, "originalSha256"),
                OriginalJournalSha = Json.Text(value, "originalJournalSha256"), NewSha = Json.Text(value, "newSha256"), StartedUtc = Json.Text(value, "startedUtc")
            };
            if ((journal.State != "frozen" && journal.State != "installing") || string.IsNullOrEmpty(journal.Serial) || !IsHash(journal.OriginalSha)
                || (journal.OriginalJournalSha != null && !IsHash(journal.OriginalJournalSha)) || (journal.State == "installing" && !IsHash(journal.NewSha)))
                throw new DeviceApplyException("APPLY_JOURNAL_INVALID");
            return journal;
        }

        internal static bool IsHash(string value) { return value != null && Regex.IsMatch(value, @"\A[a-f0-9]{64}\z"); }

        public void Save(string directory)
        {
            Directory.CreateDirectory(directory);
            string text = "{\"state\":" + Json.Quote(State) + ",\"serial\":" + Json.Quote(Serial) + ",\"originalSha256\":" + Json.Quote(OriginalSha)
                + ",\"originalJournalSha256\":" + (OriginalJournalSha == null ? "null" : Json.Quote(OriginalJournalSha))
                + ",\"newSha256\":" + (NewSha == null ? "null" : Json.Quote(NewSha)) + ",\"startedUtc\":" + Json.Quote(StartedUtc) + "}";
            string file = PathIn(directory), temporary = file + ".tmp";
            // Flushed to disk before the device step it describes begins.
            using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
        }

        public static void Clear(string directory) { string file = PathIn(directory); if (File.Exists(file)) File.Delete(file); }
    }

    internal sealed class DeviceApplier
    {
        private readonly ICocoonDevice device;
        private readonly ISnapshotPatcher patcher;
        private readonly string stateDirectory, backupRoot;
        public event Action<string> Progress; // stage names for the dialog, raised on a worker thread

        // stateDirectory: where the journal and the working snapshot live. backupRoot: dated copies of what was read.
        public DeviceApplier(ICocoonDevice target, ISnapshotPatcher snapshotPatcher, string stateDirectory, string backupRoot)
        {
            if (target == null || snapshotPatcher == null) throw new ArgumentNullException("target");
            if (!Path.IsPathRooted(stateDirectory ?? "") || !Path.IsPathRooted(backupRoot ?? "")) throw new ArgumentException("Absolute directories required");
            device = target; patcher = snapshotPatcher; this.stateDirectory = stateDirectory; this.backupRoot = backupRoot;
        }

        public string StateDirectory { get { return stateDirectory; } }
        // A run that did not finish; the device may still have Cocoon disabled.
        public DeviceApplyJournal Interrupted() { return DeviceApplyJournal.Load(stateDirectory); }

        private void Stage(string name) { Action<string> handler = Progress; if (handler != null) handler(name); }

        internal static string Sha(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        public async Task<DeviceApplyResult> ApplyAsync(IList<DeviceFieldChange> changes, CancellationToken token)
        {
            if (changes == null || changes.Count == 0 || changes.Count > 100) throw new DeviceApplyException("APPLY_REQUEST_INVALID");
            if (Interrupted() != null) throw new DeviceApplyException("APPLY_RECOVERY_REQUIRED");

            Stage("inspect");
            CocoonDeviceFacts facts = await device.InspectAsync(token).ConfigureAwait(false);
            if (!facts.Enabled) throw new DeviceApplyException("DEVICE_APP_DISABLED"); // not ours to re-enable blindly

            string work = Path.Combine(stateDirectory, "snapshot"), data = Path.Combine(work, "_data"), local = Path.Combine(data, "cocoon_db");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(data);

            DeviceApplyJournal journal = new DeviceApplyJournal { State = "frozen", Serial = device.Serial, StartedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            bool frozen = false, replaced = false;
            string code;
            List<string> pushed = new List<string>();
            try
            {
                // New artwork and videos first, while Cocoon still runs: nothing refers to them yet, so this
                // step alone changes nothing visible. Each file is checked by hash on the device.
                foreach (DeviceFieldChange change in changes)
                    foreach (DeviceMediaFile media in change.Media)
                    {
                        Stage("media");
                        if (!CocoonMedia.IsMediaPath(media.RemotePath) || !File.Exists(media.LocalPath) || Sha(File.ReadAllBytes(media.LocalPath)) != media.Sha256) throw new DeviceApplyException("APPLY_MEDIA_INVALID");
                        if (await device.HashMediaAsync(media.RemotePath, token).ConfigureAwait(false) == media.Sha256) continue; // already there from an earlier attempt
                        pushed.Add(media.RemotePath);
                        await device.PushMediaAsync(media.LocalPath, media.RemotePath, token).ConfigureAwait(false);
                        if (await device.HashMediaAsync(media.RemotePath, token).ConfigureAwait(false) != media.Sha256) throw new DeviceApplyException("DEVICE_PUSH_FAILED");
                    }

                // From here on Cocoon must be enabled again whatever happens; the journal is on disk first.
                Stage("freeze");
                journal.OriginalSha = await device.HashAsync(DeviceFile.Catalog, token).ConfigureAwait(false);
                if (journal.OriginalSha == null) throw new DeviceApplyException("DEVICE_CATALOG_MISSING");
                journal.Save(stateDirectory);
                frozen = true;
                await device.FreezeAsync(token).ConfigureAwait(false);

                // The catalog as Cocoon left it, hashed on the device, backed up there and read here.
                Stage("read");
                journal.OriginalSha = await device.HashAsync(DeviceFile.Catalog, token).ConfigureAwait(false);
                journal.OriginalJournalSha = await device.HashAsync(DeviceFile.CatalogJournal, token).ConfigureAwait(false);
                if (journal.OriginalSha == null) throw new DeviceApplyException("DEVICE_CATALOG_MISSING");
                journal.Save(stateDirectory);
                await device.BackupAsync(token).ConfigureAwait(false);
                if (await device.HashAsync(DeviceFile.BackupCatalog, token).ConfigureAwait(false) != journal.OriginalSha
                    || await device.HashAsync(DeviceFile.BackupJournal, token).ConfigureAwait(false) != journal.OriginalJournalSha) throw new DeviceApplyException("DEVICE_BACKUP_FAILED");
                byte[] catalog = await device.ReadAsync(DeviceFile.Catalog, token).ConfigureAwait(false);
                if (Sha(catalog) != journal.OriginalSha) throw new DeviceApplyException("DEVICE_READ_MISMATCH");
                byte[] wal = null;
                if (journal.OriginalJournalSha != null)
                {
                    wal = await device.ReadAsync(DeviceFile.CatalogJournal, token).ConfigureAwait(false);
                    if (Sha(wal) != journal.OriginalJournalSha) throw new DeviceApplyException("DEVICE_READ_MISMATCH");
                }
                string backup = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + journal.OriginalSha.Substring(0, 8));
                Directory.CreateDirectory(backup);
                File.WriteAllBytes(Path.Combine(backup, "cocoon_db"), catalog);
                if (wal != null && wal.Length > 0) File.WriteAllBytes(Path.Combine(backup, "cocoon_db-wal"), wal);
                File.WriteAllBytes(local, catalog);
                if (wal != null && wal.Length > 0) File.WriteAllBytes(local + "-wal", wal);

                Stage("patch");
                SnapshotPatchResult patched = await patcher.PatchAsync(local, changes, token).ConfigureAwait(false);
                if (!DeviceApplyJournal.IsHash(patched.Sha256) || Sha(File.ReadAllBytes(local)) != patched.Sha256 || File.Exists(local + "-wal")) throw new DeviceApplyException("PATCH_RESULT_CORRUPT");

                Stage("push");
                await device.PushCandidateAsync(local, token).ConfigureAwait(false);
                if (await device.HashAsync(DeviceFile.Candidate, token).ConfigureAwait(false) != patched.Sha256) throw new DeviceApplyException("DEVICE_PUSH_FAILED");

                // Point of no return for cancellation: once the catalog is being rewritten the run goes to the end
                // or to a restore, never stops in between because the user closed a dialog.
                token.ThrowIfCancellationRequested();
                Stage("install");
                journal.State = "installing"; journal.NewSha = patched.Sha256;
                journal.Save(stateDirectory);
                replaced = true;
                await device.InstallAsync(false, CancellationToken.None).ConfigureAwait(false);
                CocoonDeviceFacts after = await device.InspectAsync(CancellationToken.None).ConfigureAwait(false);
                if (await device.HashAsync(DeviceFile.Catalog, CancellationToken.None).ConfigureAwait(false) != patched.Sha256
                    || after.Ownership != facts.Ownership || after.Context != facts.Context) throw new DeviceApplyException("DEVICE_INSTALL_MISMATCH");

                Stage("thaw");
                await device.ThawAsync(CancellationToken.None).ConfigureAwait(false);
                frozen = false;
                DeviceApplyJournal.Clear(stateDirectory);
                try { await device.RemoveCandidateAsync(CancellationToken.None).ConfigureAwait(false); } catch (DeviceApplyException) { } // leftover scratch file only
                return new DeviceApplyResult { Sha256 = patched.Sha256, SnapshotRoot = work, Games = patched.Games, FieldsWritten = patched.FieldsWritten, FieldsUnchanged = patched.FieldsUnchanged, BackupDirectory = backup };
            }
            catch (Exception failure)
            {
                code = failure is DeviceApplyException ? ((DeviceApplyException)failure).Code : failure is OperationCanceledException ? "APPLY_CANCELLED" : "APPLY_FAILED";
            }
            if (!frozen) { DeviceApplyJournal.Clear(stateDirectory); await RemovePushed(pushed).ConfigureAwait(false); throw new DeviceApplyException(code); }
            // Undo on the device. If that cannot be confirmed the journal stays, and the dialog offers recovery.
            bool restored = false;
            try
            {
                Stage("restore");
                if (replaced)
                {
                    await device.InstallAsync(true, CancellationToken.None).ConfigureAwait(false);
                    if (await device.HashAsync(DeviceFile.Catalog, CancellationToken.None).ConfigureAwait(false) != journal.OriginalSha) throw new DeviceApplyException("DEVICE_RESTORE_FAILED");
                }
                await device.ThawAsync(CancellationToken.None).ConfigureAwait(false);
                DeviceApplyJournal.Clear(stateDirectory);
                restored = true;
            }
            catch (Exception) { }
            // Files nothing refers to any more go too, but only once the original catalog is confirmed back.
            if (restored) await RemovePushed(pushed).ConfigureAwait(false);
            throw new DeviceApplyException(restored ? code : "APPLY_RECOVERY_REQUIRED");
        }

        private async Task RemovePushed(List<string> pushed)
        {
            foreach (string path in pushed)
                try { await device.RemoveMediaAsync(path, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
        }

        // Finishes or undoes an interrupted run. Returns "applied" when the new catalog was already in place,
        // "restored" when the original was put back, "unchanged" when the catalog had not been touched.
        public async Task<string> RecoverAsync(CancellationToken token)
        {
            DeviceApplyJournal journal = Interrupted();
            if (journal == null) return "none";
            if (journal.Serial != device.Serial) throw new DeviceApplyException("APPLY_WRONG_DEVICE");
            Stage("inspect");
            await device.InspectAsync(token).ConfigureAwait(false);
            string current = await device.HashAsync(DeviceFile.Catalog, token).ConfigureAwait(false), outcome;
            // "frozen" never got as far as writing the catalog, whatever Cocoon itself wrote before it stopped.
            if (journal.State == "frozen" || current == journal.OriginalSha) outcome = "unchanged";
            else if (current == journal.NewSha) outcome = "applied";
            else
            {
                // Mid-install, or anything unexpected: put back the backup taken on the device, if it is the right one.
                Stage("restore");
                if (await device.HashAsync(DeviceFile.BackupCatalog, token).ConfigureAwait(false) != journal.OriginalSha) throw new DeviceApplyException("DEVICE_BACKUP_MISSING");
                await device.InstallAsync(true, CancellationToken.None).ConfigureAwait(false);
                if (await device.HashAsync(DeviceFile.Catalog, CancellationToken.None).ConfigureAwait(false) != journal.OriginalSha) throw new DeviceApplyException("DEVICE_RESTORE_FAILED");
                outcome = "restored";
            }
            Stage("thaw");
            await device.ThawAsync(CancellationToken.None).ConfigureAwait(false);
            DeviceApplyJournal.Clear(stateDirectory);
            return outcome;
        }
    }

    // Runs the backend component's snapshot-patch tool (cocoon.snapshot-patch/1) as an owned child.
    internal sealed class NodeSnapshotPatcher : ISnapshotPatcher
    {
        private readonly string node, tool;
        public NodeSnapshotPatcher(string nodePath, string backendDirectory)
        {
            node = nodePath;
            tool = Path.Combine(backendDirectory ?? "", Path.Combine("tools", "snapshot-patch.cjs"));
        }

        internal static string Request(string catalogPath, IList<DeviceFieldChange> changes)
        {
            List<object> rows = new List<object>();
            foreach (DeviceFieldChange change in changes)
            {
                Dictionary<string, object> fields = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> pair in change.After)
                {
                    if (change.Unguarded.Contains(pair.Key)) { fields[pair.Key] = NativeEditorClient.Map("after", pair.Value); continue; }
                    object before;
                    if (!change.Before.TryGetValue(pair.Key, out before)) throw new DeviceApplyException("APPLY_REQUEST_INVALID");
                    fields[pair.Key] = NativeEditorClient.Map("before", before, "after", pair.Value);
                }
                Dictionary<string, object> row = NativeEditorClient.Map("gameId", change.GameId, "fields", fields);
                if (change.FileName != null || change.PlatformId != null)
                {
                    Dictionary<string, object> expect = new Dictionary<string, object>();
                    if (change.FileName != null) expect["fileName"] = change.FileName;
                    if (change.PlatformId != null) expect["platformId"] = change.PlatformId;
                    row["expect"] = expect;
                }
                rows.Add(row);
            }
            return NativeEditorJson.Encode(NativeEditorClient.Map("contract", "cocoon.snapshot-patch/1", "database", catalogPath, "changes", rows));
        }

        public Task<SnapshotPatchResult> PatchAsync(string catalogPath, IList<DeviceFieldChange> changes, CancellationToken token)
        {
            string request = Request(catalogPath, changes);
            return Task.Run(delegate
            {
                if (!File.Exists(tool)) throw new DeviceApplyException("PATCH_TOOL_MISSING");
                ProcessStartInfo info = new ProcessStartInfo(node, "\"" + tool + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
                string output;
                int exit;
                try
                {
                    using (Process child = Process.Start(info))
                    using (token.Register(delegate { try { if (!child.HasExited) child.Kill(); } catch (Exception) { } }))
                    {
                        child.ErrorDataReceived += delegate { };
                        child.BeginErrorReadLine();
                        byte[] bytes = new UTF8Encoding(false).GetBytes(request);
                        child.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                        child.StandardInput.BaseStream.Close();
                        Task<string> reading = child.StandardOutput.ReadToEndAsync();
                        if (!child.WaitForExit(120000)) { try { child.Kill(); } catch (Exception) { } throw new DeviceApplyException("PATCH_TIMEOUT"); }
                        output = reading.Result;
                        exit = child.ExitCode;
                    }
                }
                catch (System.ComponentModel.Win32Exception) { throw new DeviceApplyException("PATCH_TOOL_MISSING"); }
                token.ThrowIfCancellationRequested();
                object value;
                try { value = Json.Parse(output.Trim()); } catch (Exception) { throw new DeviceApplyException("PATCH_FAILED"); }
                if (Json.Text(value, "contract") != "cocoon.snapshot-patch/1") throw new DeviceApplyException("PATCH_FAILED");
                if (exit != 0 || !Json.Flag(value, "ok"))
                {
                    string code = Json.Text(value, "code");
                    throw new DeviceApplyException(Regex.IsMatch(code ?? "", @"\APATCH_[A-Z_]{2,48}\z") ? code : "PATCH_FAILED");
                }
                int written = 0;
                List<object> applied = Json.Get(value, "applied") as List<object>;
                if (applied != null) foreach (object row in applied) { List<object> fields = Json.Get(row, "fields") as List<object>; if (fields != null) written += fields.Count; }
                return new SnapshotPatchResult { Sha256 = Json.Text(value, "sha256"), Games = (int)Json.Number(value, "games", 0), FieldsWritten = written, FieldsUnchanged = (int)Json.Number(value, "unchanged", 0) };
            }, token);
        }
    }
}
