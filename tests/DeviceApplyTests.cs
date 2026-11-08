using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    // "Apply to device" against a directory that stands in for the handheld, with the real
    // snapshot-patch tool. Covers the order of steps, hash verification, undo and recovery.
    internal static class DeviceApplyTests
    {
        internal sealed class FakeDevice : ICocoonDevice
        {
            public readonly string Dir;
            public bool Enabled = true, Running = true, Root = true;
            public string Ownership = "u0_a143:u0_a143:660", Context = "u:object_r:app_data_file:s0:c143,c256";
            public readonly List<string> Calls = new List<string>();
            public string FailOn;          // operation that throws
            public int FailTimes;          // how many times it still throws
            public bool CorruptInstall;    // install writes something other than the candidate
            public bool HalfInstall;       // install writes half the file and then fails
            public Action<string> Before;  // hook for cancellation tests

            public FakeDevice(string directory) { Dir = directory; Directory.CreateDirectory(directory); }
            public string Serial { get { return "fake-thor"; } }
            public string File(DeviceFile file)
            {
                return Path.Combine(Dir, file == DeviceFile.Catalog ? "cocoon_db" : file == DeviceFile.CatalogJournal ? "cocoon_db-wal" : file == DeviceFile.BackupCatalog ? "backup_db"
                    : file == DeviceFile.BackupJournal ? "backup_db-wal" : "new_db");
            }
            private void Enter(string name)
            {
                Calls.Add(name);
                if (Before != null) Before(name);
                if (FailOn == name && FailTimes > 0) { FailTimes--; throw new DeviceApplyException("DEVICE_COMMAND_FAILED"); }
            }
            public Task<CocoonDeviceFacts> InspectAsync(CancellationToken token)
            {
                Enter("inspect");
                if (!Root) throw new DeviceApplyException("DEVICE_ROOT_REQUIRED");
                return Task.FromResult(new CocoonDeviceFacts { Enabled = Enabled, Running = Running, Ownership = Ownership, Context = Context });
            }
            public Task FreezeAsync(CancellationToken token) { Enter("freeze"); Enabled = false; Running = false; return Task.FromResult(0); }
            public Task ThawAsync(CancellationToken token) { Enter("thaw"); Enabled = true; Running = true; return Task.FromResult(0); }
            public Task<string> HashAsync(DeviceFile file, CancellationToken token)
            {
                Enter("hash:" + file);
                string path = File(file);
                return Task.FromResult(System.IO.File.Exists(path) ? DeviceApplier.Sha(System.IO.File.ReadAllBytes(path)) : null);
            }
            public Task<byte[]> ReadAsync(DeviceFile file, CancellationToken token) { Enter("read:" + file); return Task.FromResult(System.IO.File.ReadAllBytes(File(file))); }
            public Task BackupAsync(CancellationToken token)
            {
                Enter("backup");
                System.IO.File.Copy(File(DeviceFile.Catalog), File(DeviceFile.BackupCatalog), true);
                if (System.IO.File.Exists(File(DeviceFile.BackupJournal))) System.IO.File.Delete(File(DeviceFile.BackupJournal));
                if (System.IO.File.Exists(File(DeviceFile.CatalogJournal))) System.IO.File.Copy(File(DeviceFile.CatalogJournal), File(DeviceFile.BackupJournal));
                return Task.FromResult(0);
            }
            public Task PushCandidateAsync(string localPath, CancellationToken token) { Enter("push"); System.IO.File.Copy(localPath, File(DeviceFile.Candidate), true); return Task.FromResult(0); }
            public Task InstallAsync(bool fromBackup, CancellationToken token)
            {
                Enter(fromBackup ? "install:backup" : "install:candidate");
                if (Running) throw new DeviceApplyException("DEVICE_COMMAND_FAILED"); // the catalog must never be rewritten under a running Cocoon
                byte[] bytes = System.IO.File.ReadAllBytes(File(fromBackup ? DeviceFile.BackupCatalog : DeviceFile.Candidate));
                if (HalfInstall && !fromBackup) { System.IO.File.WriteAllBytes(File(DeviceFile.Catalog), new byte[bytes.Length / 2]); throw new DeviceApplyException("DEVICE_LINK_LOST"); }
                if (CorruptInstall && !fromBackup) bytes[bytes.Length - 1] ^= 0xFF;
                System.IO.File.WriteAllBytes(File(DeviceFile.Catalog), bytes);
                string wal = File(DeviceFile.CatalogJournal);
                if (System.IO.File.Exists(wal))
                {
                    if (fromBackup && System.IO.File.Exists(File(DeviceFile.BackupJournal))) System.IO.File.Copy(File(DeviceFile.BackupJournal), wal, true);
                    else System.IO.File.WriteAllBytes(wal, new byte[0]);
                }
                return Task.FromResult(0);
            }
            public Task RemoveCandidateAsync(CancellationToken token) { Enter("remove"); System.IO.File.Delete(File(DeviceFile.Candidate)); return Task.FromResult(0); }

            // Shared media folder, mirrored under <Dir>/media.
            public string Media(string remotePath)
            {
                if (!CocoonMedia.IsMediaPath(remotePath)) throw new DeviceApplyException("DEVICE_MEDIA_PATH_INVALID");
                return Path.Combine(Path.Combine(Dir, "media"), remotePath.Substring(CocoonMedia.Root.Length).Replace('/', Path.DirectorySeparatorChar));
            }
            public Task PushMediaAsync(string localPath, string remotePath, CancellationToken token)
            {
                Enter("push-media");
                string target = Media(remotePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                System.IO.File.Copy(localPath, target, true);
                return Task.FromResult(0);
            }
            public Task<string> HashMediaAsync(string remotePath, CancellationToken token)
            {
                string target = Media(remotePath);
                return Task.FromResult(System.IO.File.Exists(target) ? DeviceApplier.Sha(System.IO.File.ReadAllBytes(target)) : null);
            }
            public Task RemoveMediaAsync(string remotePath, CancellationToken token) { Enter("remove-media"); System.IO.File.Delete(Media(remotePath)); return Task.FromResult(0); }
        }

        // A catalog like the device's: demo games plus Room's identity table, in WAL mode with an unmerged
        // journal beside it. Game 1 gets a known developer and is not a favourite.
        internal static string Fixture(string demoScript)
        {
            string source = Path.Combine(Tests.Root, "apply-source-" + Guid.NewGuid().ToString("N"));
            using (Process maker = Process.Start(new ProcessStartInfo("node", "\"" + demoScript + "\" \"" + source + "\"") { UseShellExecute = false, CreateNoWindow = true })) maker.WaitForExit(30000);
            string seed = Path.Combine(Path.Combine(source, "_data"), "cocoon_db"), fixture = Path.Combine(source, "fixture_db");
            Node("const {DatabaseSync}=require('node:sqlite'),fs=require('fs');const [seed,out]=process.argv.slice(1);const d=new DatabaseSync(seed);"
                + "d.exec('PRAGMA journal_mode=WAL;PRAGMA wal_autocheckpoint=0;PRAGMA user_version=47;CREATE TABLE room_master_table (id INTEGER PRIMARY KEY, identity_hash TEXT);');"
                + "d.exec('INSERT INTO room_master_table VALUES (42, \\'ee7e0f3846e5e3b3d5127426d5ba15d0\\')');d.exec('PRAGMA wal_checkpoint(TRUNCATE)');"
                + "d.prepare('UPDATE games SET developer=?, isFavorite=0 WHERE id=1').run('Fixture Studio');"
                + "fs.copyFileSync(seed,out);fs.copyFileSync(seed+'-wal',out+'-wal');d.close();", seed, fixture);
            return fixture;
        }

        internal static string Node(string script, params string[] arguments)
        {
            string quoted = "";
            foreach (string argument in arguments) quoted += " \"" + argument + "\"";
            ProcessStartInfo info = new ProcessStartInfo("node", "-e \"" + script + "\"" + quoted) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process child = Process.Start(info))
            {
                string output = child.StandardOutput.ReadToEnd(), error = child.StandardError.ReadToEnd();
                child.WaitForExit(30000);
                if (child.ExitCode != 0) throw new Exception("fixture script failed: " + error);
                return output.Trim();
            }
        }

        private static string Code(Func<Task> action)
        {
            try { action().GetAwaiter().GetResult(); return null; }
            catch (DeviceApplyException failure) { return failure.Code; }
        }

        private static DeviceFieldChange Change(string before, string after)
        {
            DeviceFieldChange change = new DeviceFieldChange { GameId = 1 };
            change.Before["developer"] = before; change.After["developer"] = after;
            change.Before["isFavorite"] = 0d; change.After["isFavorite"] = 1d;
            return change;
        }

        internal static void Run()
        {
            string backendDir = Path.GetFullPath(Path.Combine(Adb.AppDir(), Path.Combine("..", Path.Combine("..", "cocoon-art-backend"))));
            string demoScript = Path.GetFullPath(Path.Combine(Adb.AppDir(), Path.Combine("..", Path.Combine("tools", "cocoon-demo.cjs"))));
            if (!File.Exists(Path.Combine(backendDir, Path.Combine("tools", "snapshot-patch.cjs"))) || !File.Exists(demoScript)) { Console.WriteLine("SKIP: snapshot-patch tool not present"); return; }

            string fixture = Fixture(demoScript);
            Tests.Check(File.Exists(fixture) && new FileInfo(fixture + "-wal").Length > 0, "the device fixture has a catalog and an unmerged journal");
            NodeSnapshotPatcher patcher = new NodeSnapshotPatcher("node", backendDir);

            Func<string, FakeDevice> device = delegate(string name)
            {
                FakeDevice fake = new FakeDevice(Path.Combine(Tests.Root, "apply-device-" + name + "-" + Guid.NewGuid().ToString("N")));
                File.Copy(fixture, fake.File(DeviceFile.Catalog)); File.Copy(fixture + "-wal", fake.File(DeviceFile.CatalogJournal));
                return fake;
            };
            Func<FakeDevice, DeviceApplier> applier = delegate(FakeDevice fake) { return new DeviceApplier(fake, patcher, Path.Combine(fake.Dir, "state"), Path.Combine(fake.Dir, "pc-backups")); };
            byte[] originalCatalog = File.ReadAllBytes(fixture), originalJournal = File.ReadAllBytes(fixture + "-wal");
            Func<FakeDevice, bool> untouched = delegate(FakeDevice fake)
            {
                return DeviceApplier.Sha(File.ReadAllBytes(fake.File(DeviceFile.Catalog))) == DeviceApplier.Sha(originalCatalog)
                    && DeviceApplier.Sha(File.ReadAllBytes(fake.File(DeviceFile.CatalogJournal))) == DeviceApplier.Sha(originalJournal);
            };

            // 1. The whole run.
            FakeDevice good = device("good");
            DeviceApplier run = applier(good);
            List<string> stages = new List<string>();
            run.Progress += stages.Add;
            DeviceApplyResult result = run.ApplyAsync(new[] { Change("Fixture Studio", "Applied Studio") }, CancellationToken.None).GetAwaiter().GetResult();
            Tests.Check(string.Join(",", stages.ToArray()) == "inspect,freeze,read,patch,push,install,thaw", "the run goes through its stages in order [" + string.Join(",", stages.ToArray()) + "]");
            Tests.Check(good.Calls.IndexOf("freeze") < good.Calls.IndexOf("backup") && good.Calls.IndexOf("backup") < good.Calls.IndexOf("read:Catalog")
                && good.Calls.IndexOf("push") < good.Calls.IndexOf("install:candidate") && good.Calls.IndexOf("install:candidate") < good.Calls.LastIndexOf("thaw") && !good.Calls.Contains("install:backup"),
                "Cocoon is stopped before anything is read, and started only after the catalog is in place");
            Tests.Check(result.Sha256 == DeviceApplier.Sha(File.ReadAllBytes(good.File(DeviceFile.Catalog))) && result.FieldsWritten == 2 && result.Games == 24,
                "the catalog on the device is exactly the patched snapshot");
            Tests.Check(new FileInfo(good.File(DeviceFile.CatalogJournal)).Length == 0, "the stale journal is emptied so it cannot be replayed onto the new catalog");
            Tests.Check(good.Enabled && run.Interrupted() == null && !File.Exists(good.File(DeviceFile.Candidate)), "Cocoon is enabled again and no journal or scratch file is left");
            Tests.Check(File.Exists(Path.Combine(Path.Combine(result.SnapshotRoot, "_data"), "cocoon_db"))
                && DeviceApplier.Sha(File.ReadAllBytes(Path.Combine(Path.Combine(result.SnapshotRoot, "_data"), "cocoon_db"))) == result.Sha256, "a local copy identical to the device is kept for refreshing the library");
            Tests.Check(DeviceApplier.Sha(File.ReadAllBytes(Path.Combine(result.BackupDirectory, "cocoon_db"))) == DeviceApplier.Sha(originalCatalog)
                && DeviceApplier.Sha(File.ReadAllBytes(good.File(DeviceFile.BackupCatalog))) == DeviceApplier.Sha(originalCatalog), "the original catalog is backed up on this computer and on the device");
            Tests.Check(Node("const {DatabaseSync}=require('node:sqlite');const d=new DatabaseSync(process.argv[1],{readOnly:true});const g=d.prepare('select developer,isFavorite from games where id=1').get();console.log(g.developer+'|'+g.isFavorite+'|'+d.prepare('pragma user_version').get().user_version)",
                good.File(DeviceFile.Catalog)) == "Applied Studio|1|47", "the reviewed fields hold their new values and the schema version is unchanged");

            // 2. The device no longer holds what was reviewed.
            string beforeConflict = DeviceApplier.Sha(File.ReadAllBytes(good.File(DeviceFile.Catalog)));
            Tests.Check(Code(() => run.ApplyAsync(new[] { Change("Fixture Studio", "Other Studio") }, CancellationToken.None)) == "PATCH_CONFLICT"
                && DeviceApplier.Sha(File.ReadAllBytes(good.File(DeviceFile.Catalog))) == beforeConflict && good.Enabled && run.Interrupted() == null,
                "a value that changed on the device stops the run before anything is written, and Cocoon is started again");

            // 3. Preconditions that must not even stop Cocoon.
            FakeDevice disabled = device("disabled"); disabled.Enabled = false;
            Tests.Check(Code(() => applier(disabled).ApplyAsync(new[] { Change("Fixture Studio", "X") }, CancellationToken.None)) == "DEVICE_APP_DISABLED" && !disabled.Calls.Contains("freeze") && !disabled.Enabled,
                "a Cocoon that was already disabled is left as it was found");
            FakeDevice rootless = device("rootless"); rootless.Root = false;
            Tests.Check(Code(() => applier(rootless).ApplyAsync(new[] { Change("Fixture Studio", "X") }, CancellationToken.None)) == "DEVICE_ROOT_REQUIRED" && !rootless.Calls.Contains("freeze"), "without root nothing is attempted");

            // 4. Failures after Cocoon was stopped are undone.
            FakeDevice half = device("half"); half.HalfInstall = true;
            Tests.Check(Code(() => applier(half).ApplyAsync(new[] { Change("Fixture Studio", "X") }, CancellationToken.None)) == "DEVICE_LINK_LOST" && untouched(half) && half.Enabled && applier(half).Interrupted() == null,
                "a write that breaks half-way is undone from the device backup, journal included");
            FakeDevice corrupt = device("corrupt"); corrupt.CorruptInstall = true;
            Tests.Check(Code(() => applier(corrupt).ApplyAsync(new[] { Change("Fixture Studio", "X") }, CancellationToken.None)) == "DEVICE_INSTALL_MISMATCH" && untouched(corrupt) && corrupt.Enabled,
                "a catalog that does not hash to the patched snapshot is rejected and the original restored");
            FakeDevice relabelled = device("relabelled");
            relabelled.Before = delegate(string name) { if (name == "install:candidate") relabelled.Context = "u:object_r:unlabeled:s0"; };
            Tests.Check(Code(() => applier(relabelled).ApplyAsync(new[] { Change("Fixture Studio", "X") }, CancellationToken.None)) == "DEVICE_INSTALL_MISMATCH" && untouched(relabelled) && relabelled.Enabled,
                "a catalog whose owner or label changed is not left in place");
            FakeDevice pushFails = device("push"); pushFails.FailOn = "push"; pushFails.FailTimes = 1;
            Tests.Check(Code(() => applier(pushFails).ApplyAsync(new[] { Change("Fixture Studio", "X") }, CancellationToken.None)) == "DEVICE_COMMAND_FAILED" && untouched(pushFails) && pushFails.Enabled
                && !pushFails.Calls.Contains("install:candidate") && !pushFails.Calls.Contains("install:backup"), "a failed transfer never reaches the catalog");

            // 5. Cancelling is honoured up to the write, and Cocoon comes back.
            FakeDevice cancelled = device("cancel");
            using (CancellationTokenSource source0 = new CancellationTokenSource())
            {
                cancelled.Before = delegate(string name) { if (name == "push") source0.Cancel(); };
                Tests.Check(Code(() => applier(cancelled).ApplyAsync(new[] { Change("Fixture Studio", "X") }, source0.Token)) == "APPLY_CANCELLED" && untouched(cancelled) && cancelled.Enabled
                    && !cancelled.Calls.Contains("install:candidate"), "cancelling before the write leaves the device as it was");
            }

            // 6. When the undo cannot be confirmed, the journal stays and recovery finishes the job later.
            FakeDevice stuck = device("stuck"); stuck.HalfInstall = true; stuck.FailOn = "install:backup"; stuck.FailTimes = 1;
            DeviceApplier stuckRun = applier(stuck);
            Tests.Check(Code(() => stuckRun.ApplyAsync(new[] { Change("Fixture Studio", "X") }, CancellationToken.None)) == "APPLY_RECOVERY_REQUIRED" && !stuck.Enabled
                && stuckRun.Interrupted() != null && stuckRun.Interrupted().State == "installing", "an undo that fails leaves a journal saying the catalog was being replaced");
            Tests.Check(Code(() => stuckRun.ApplyAsync(new[] { Change("Fixture Studio", "X") }, CancellationToken.None)) == "APPLY_RECOVERY_REQUIRED", "no new run starts while one is unresolved");
            stuck.HalfInstall = false;
            Tests.Check(applier(stuck).RecoverAsync(CancellationToken.None).GetAwaiter().GetResult() == "restored" && untouched(stuck) && stuck.Enabled && stuckRun.Interrupted() == null,
                "recovery restores the original catalog from the device backup and starts Cocoon");

            // 7. Recovery of runs cut off at other points (as after a crash of this program).
            FakeDevice early = device("early"); early.Enabled = false; early.Running = false;
            new DeviceApplyJournal { State = "frozen", Serial = early.Serial, OriginalSha = DeviceApplier.Sha(originalCatalog), StartedUtc = "t" }.Save(Path.Combine(early.Dir, "state"));
            Tests.Check(applier(early).RecoverAsync(CancellationToken.None).GetAwaiter().GetResult() == "unchanged" && untouched(early) && early.Enabled && !early.Calls.Contains("install:backup"),
                "a run cut off before the write only needs Cocoon enabled again");
            FakeDevice late = device("late");
            DeviceApplyResult done = applier(late).ApplyAsync(new[] { Change("Fixture Studio", "Late Studio") }, CancellationToken.None).GetAwaiter().GetResult();
            late.Enabled = false; late.Running = false;
            new DeviceApplyJournal { State = "installing", Serial = late.Serial, OriginalSha = DeviceApplier.Sha(originalCatalog), NewSha = done.Sha256, StartedUtc = "t" }.Save(Path.Combine(late.Dir, "state"));
            Tests.Check(applier(late).RecoverAsync(CancellationToken.None).GetAwaiter().GetResult() == "applied" && late.Enabled
                && DeviceApplier.Sha(File.ReadAllBytes(late.File(DeviceFile.Catalog))) == done.Sha256, "a run cut off after the write is recognised as applied and kept");
            FakeDevice other = device("other"); other.Enabled = false; other.Running = false;
            new DeviceApplyJournal { State = "frozen", Serial = "another-device", OriginalSha = DeviceApplier.Sha(originalCatalog), StartedUtc = "t" }.Save(Path.Combine(other.Dir, "state"));
            Tests.Check(Code(() => applier(other).RecoverAsync(CancellationToken.None)) == "APPLY_WRONG_DEVICE" && !other.Enabled, "recovery refuses to act on a different device than the journal names");
            File.WriteAllText(DeviceApplyJournal.PathIn(Path.Combine(other.Dir, "state")), "{\"state\":\"installing\"}");
            Tests.Check(Code(() => { applier(other).Interrupted(); return Task.FromResult(0); }) == "APPLY_JOURNAL_INVALID", "a damaged journal is reported, not ignored");

            // 8. Artwork: the file goes into Cocoon's media folder first, then the catalog points at it.
            string artFile = Path.Combine(Tests.Root, "apply-art-" + Guid.NewGuid().ToString("N") + ".png");
            byte[] artBytes = new byte[4096]; new Random(7).NextBytes(artBytes); Array.Copy(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, artBytes, 4);
            File.WriteAllBytes(artFile, artBytes);
            string remote = CocoonMedia.Root + "switch/icon/cocoon-1-icon-" + DeviceApplier.Sha(artBytes) + ".png";
            Func<string, DeviceFieldChange> artChange = delegate(string before)
            {
                DeviceFieldChange change = new DeviceFieldChange { GameId = 1 };
                change.Before["boxArtLocal"] = before; change.After["boxArtLocal"] = CocoonMedia.UriFor(remote);
                change.After["boxArtSource"] = "user"; change.Unguarded.Add("boxArtSource");
                change.Media.Add(new DeviceMediaFile { LocalPath = artFile, RemotePath = remote, Sha256 = DeviceApplier.Sha(artBytes) });
                return change;
            };
            string currentCover = Node("const {DatabaseSync}=require('node:sqlite');const d=new DatabaseSync(process.argv[1],{readOnly:true});console.log(d.prepare('select boxArtLocal from games where id=1').get().boxArtLocal||'')", fixture);
            FakeDevice art = device("art");
            DeviceApplyResult artDone = applier(art).ApplyAsync(new[] { artChange(currentCover.Length == 0 ? null : currentCover) }, CancellationToken.None).GetAwaiter().GetResult();
            Tests.Check(art.Calls.IndexOf("push-media") >= 0 && art.Calls.IndexOf("push-media") < art.Calls.IndexOf("freeze"), "the new file is put on the device before Cocoon is stopped");
            Tests.Check(DeviceApplier.Sha(File.ReadAllBytes(art.Media(remote))) == DeviceApplier.Sha(artBytes), "the file lands in Cocoon's media folder for that platform and slot");
            Tests.Check(Node("const {DatabaseSync}=require('node:sqlite');const d=new DatabaseSync(process.argv[1],{readOnly:true});const g=d.prepare('select boxArtLocal,boxArtSource from games where id=1').get();console.log(g.boxArtLocal+'|'+g.boxArtSource)", art.File(DeviceFile.Catalog))
                == CocoonMedia.UriFor(remote) + "|user" && artDone.FieldsWritten == 2, "the catalog now refers to the new file the way Cocoon writes it, marked as the user's");
            Tests.Check(CocoonMedia.UriFor(CocoonMedia.Root + "psp/icon/Ever17.jpg") == "content://com.android.externalstorage.documents/tree/primary%3Acocoon/document/primary%3Acocoon%2Fdownloaded_media%2Fpsp%2Ficon%2FEver17.jpg",
                "media references match the form already in the catalog");
            FakeDevice artConflict = device("art-conflict");
            Tests.Check(Code(() => applier(artConflict).ApplyAsync(new[] { artChange("content://something/else.png") }, CancellationToken.None)) == "PATCH_CONFLICT"
                && !File.Exists(artConflict.Media(remote)) && artConflict.Calls.Contains("remove-media") && untouched(artConflict), "if the catalog cannot be updated, the pushed file is removed again");
            Tests.Check(!CocoonMedia.IsMediaPath(CocoonMedia.Root + "switch/icon/../../evil.png") && !CocoonMedia.IsMediaPath("/data/data/x/icon/a.png") && !CocoonMedia.IsMediaPath(CocoonMedia.Root + "switch/roms/a.nsp"),
                "only files inside a platform's icon, logo or hero folder can be written");

            // 9. The request sent to the patch tool.
            DeviceFieldChange typed = new DeviceFieldChange { GameId = 7, FileName = "A \"B\".nsp", PlatformId = "switch" };
            typed.Before["title"] = null; typed.After["title"] = "T"; typed.Before["rating"] = 3.5d; typed.After["rating"] = 4d;
            object request = Json.Parse(NodeSnapshotPatcher.Request("C:\\work\\cocoon_db", new[] { typed }));
            object row = ((List<object>)Json.Get(request, "changes"))[0];
            Tests.Check(Json.Text(request, "contract") == "cocoon.snapshot-patch/1" && Json.Text(request, "database") == "C:\\work\\cocoon_db" && Json.Number(row, "gameId", 0) == 7
                && Json.Text(Json.Get(row, "expect"), "fileName") == "A \"B\".nsp" && Json.Get(Json.Get(Json.Get(row, "fields"), "title"), "before") == null
                && Json.Number(Json.Get(Json.Get(row, "fields"), "rating"), "after", 0) == 4, "the patch request carries each field's reviewed before and after values and the game's identity");
        }
    }
}
