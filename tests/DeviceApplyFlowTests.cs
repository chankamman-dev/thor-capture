using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThorCapture
{
    // The whole "apply to the handheld" flow through the page and its dialog, against the real backend
    // and the real patch tool, with a directory standing in for the device.
    internal static class DeviceApplyFlowTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        private static bool Pump(Func<bool> done, int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < timeoutMs) { Application.DoEvents(); Thread.Sleep(5); }
            return done();
        }

        private static string Developer(string catalog)
        {
            return DeviceApplyTests.Node("const {DatabaseSync}=require('node:sqlite');const d=new DatabaseSync(process.argv[1],{readOnly:true});console.log(d.prepare('select developer from games where id=1').get().developer)", catalog);
        }

        // Stages one field for game 1 through the editor, the way the metadata dialog does.
        private static void Stage(LibraryPage page, string developer)
        {
            GameEditSession edits = page.Edits;
            bool done = false; string failure = null;
            Func<Task> work = async delegate
            {
                try
                {
                    NativeEditorClient editor = await edits.Editor();
                    string item = await edits.ItemIdAsync(1);
                    Dictionary<string, object> fields = new Dictionary<string, object>(); fields["developer"] = developer;
                    NativeEditorResult preview = await editor.MetadataPreviewAsync(item, fields, edits.Token);
                    edits.Apply(await editor.MetadataConfirmAsync(Json.Text(preview.Data, "previewToken"), new[] { "developer" }, edits.Revision, edits.Token));
                }
                catch (Exception error) { failure = error.Message; }
                done = true;
            };
            work();
            Tests.Check(Pump(() => done, 30000) && failure == null && edits.Count == 1, "a change to game 1 is staged in the local draft [" + failure + "]");
        }

        internal static void Run()
        {
            string backendDir = Path.GetFullPath(Path.Combine(Adb.AppDir(), Path.Combine("..", Path.Combine("..", "cocoon-art-backend"))));
            string demoScript = Path.GetFullPath(Path.Combine(Adb.AppDir(), Path.Combine("..", Path.Combine("tools", "cocoon-demo.cjs"))));
            if (!File.Exists(Path.Combine(backendDir, Path.Combine("tools", "snapshot-patch.cjs"))) || !File.Exists(demoScript)) { Console.WriteLine("SKIP: snapshot-patch tool not present"); return; }

            string fixture = DeviceApplyTests.Fixture(demoScript);
            DeviceApplyTests.FakeDevice device = new DeviceApplyTests.FakeDevice(Path.Combine(Tests.Root, "flow-device-" + Guid.NewGuid().ToString("N")));
            File.Copy(fixture, device.File(DeviceFile.Catalog)); File.Copy(fixture + "-wal", device.File(DeviceFile.CatalogJournal));
            // The local library starts as a read of that same device.
            string pulled = Path.Combine(Tests.Root, "flow-pulled-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(pulled, "_data"));
            File.Copy(fixture, Path.Combine(Path.Combine(pulled, "_data"), "cocoon_db")); File.Copy(fixture + "-wal", Path.Combine(Path.Combine(pulled, "_data"), "cocoon_db-wal"));
            string target = Path.Combine(Path.Combine(Tests.Root, "flow-local-" + Guid.NewGuid().ToString("N")), "cocoon");

            string previousRoot = Environment.GetEnvironmentVariable("THOR_COCOON_DATA_ROOT");
            Environment.SetEnvironmentVariable("THOR_COCOON_DATA_ROOT", target);
            bool autoInstall = WindowsFormsSynchronizationContext.AutoInstall;
            SynchronizationContext previousContext = SynchronizationContext.Current;
            WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Form host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0), Size = new Size(1400, 900) };
            LibraryPage page = null;
            try
            {
                page = new LibraryPage { Dock = DockStyle.Fill, Visible = false };
                page.Backend.BackendDir = backendDir;
                page.Backend.DataRoot = target;
                host.Controls.Add(page);
                host.Show();
                page.Visible = true;
                Tests.Check(Pump(() => page.Current == LibraryPage.Phase.NotImported, 25000), "the local library starts empty [" + page.Describe + "]");
                ImportRunner importer = (ImportRunner)typeof(LibraryPage).GetProperty("Importer", Flags).GetValue(page, null);
                LibraryImportPlan plan = null;
                importer.Client.PlanAsync(pulled, false, false).ContinueWith(t => { if (!t.IsFaulted) plan = t.Result; });
                Tests.Check(Pump(() => plan != null, 20000), "the device's catalog can be imported as the local library");
                importer.Execute(plan);
                Tests.Check(Pump(() => page.Current == LibraryPage.Phase.Ready && page.GamesLoaded == 24 && page.Edits.Loaded, 60000) && page.Edits.Error == null, "the page shows the library read from the device [" + page.Describe + " " + page.Edits.Error + "]");
                Func<int, GameEntry> lookup = delegate(int id) { return (GameEntry)typeof(LibraryPage).GetMethod("GameById", Flags).Invoke(page, new object[] { id }); };
                Tests.Check(lookup(1) != null && lookup(1).Developer == "Fixture Studio", "game 1 shows the developer stored on the device");
                MethodInfo primary = typeof(ApplyDialog).GetMethod("Primary", Flags);

                // 1. Stage, review, apply.
                Stage(page, "Flow Studio");
                string beforeApply = DeviceApplier.Sha(File.ReadAllBytes(device.File(DeviceFile.Catalog)));
                using (ApplyDialog dialog = new ApplyDialog(page.Edits, page.NewApplier(device), lookup, page.RefreshAfterApply) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0) })
                {
                    dialog.Show(host);
                    Tests.Check(Pump(() => !dialog.Describe.StartsWith("Loading", StringComparison.Ordinal), 30000), "the dialog finishes preparing the review [" + dialog.Describe + "]");
                    if (dialog.Describe.StartsWith("Migration", StringComparison.Ordinal))
                    {
                        Tests.Check(device.Calls.Count == 0, "the local data upgrade is offered before anything touches the device");
                        primary.Invoke(dialog, null);
                        Tests.Check(Pump(() => dialog.Describe.StartsWith("Review", StringComparison.Ordinal) || dialog.Describe.StartsWith("Failed", StringComparison.Ordinal), 60000), "after the upgrade the review is prepared [" + dialog.Describe + "]");
                    }
                    ReviewList list = (ReviewList)typeof(ApplyDialog).GetField("list", Flags).GetValue(dialog);
                    Tests.Check(dialog.Describe.StartsWith("Review", StringComparison.Ordinal) && list.Count == 1 && list[0].Game == lookup(1).Name && list[0].Field == "开发商"
                        && list[0].Before == "Fixture Studio" && list[0].After == "Flow Studio", "the review shows the game, the field and both values [" + dialog.Describe + "]");
                    Tests.Check(device.Calls.Count == 0 && DeviceApplier.Sha(File.ReadAllBytes(device.File(DeviceFile.Catalog))) == beforeApply, "reviewing sends nothing to the device");
                    primary.Invoke(dialog, null);
                    Tests.Check(Pump(() => dialog.Describe.StartsWith("Done", StringComparison.Ordinal) || dialog.Describe.StartsWith("Failed", StringComparison.Ordinal) || dialog.Describe.StartsWith("Recovery", StringComparison.Ordinal), 120000)
                        && dialog.Describe.StartsWith("Done", StringComparison.Ordinal) && dialog.Applied && dialog.Describe.EndsWith("notice=", StringComparison.Ordinal), "confirming applies the change and refreshes the local copy [" + dialog.Describe + "]");
                }
                Tests.Check(Developer(device.File(DeviceFile.Catalog)) == "Flow Studio" && device.Enabled, "the device catalog holds the new value and Cocoon is enabled");
                Tests.Check(Pump(() => page.Current == LibraryPage.Phase.Ready && page.Edits.Loaded && lookup(1) != null && lookup(1).Developer == "Flow Studio", 60000), "the page shows the applied value from the refreshed local copy [" + page.Describe + "]");
                Tests.Check(page.Edits.Count == 0 && page.Edits.Error == null, "the applied change is no longer in the draft [" + page.Edits.Count + " " + page.Edits.Error + "]");

                // 2. The device changed behind our back: nothing is written.
                Stage(page, "Second Studio");
                DeviceApplyTests.Node("const {DatabaseSync}=require('node:sqlite');const d=new DatabaseSync(process.argv[1]);d.prepare('update games set developer=? where id=1').run('Changed On Device');d.exec('PRAGMA wal_checkpoint(TRUNCATE)');d.close()", device.File(DeviceFile.Catalog));
                using (ApplyDialog dialog = new ApplyDialog(page.Edits, page.NewApplier(device), lookup, page.RefreshAfterApply) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0) })
                {
                    dialog.Show(host);
                    Pump(() => !dialog.Describe.StartsWith("Loading", StringComparison.Ordinal), 30000);
                    // Replacing the local copy brought back the older local data layout, so the upgrade is offered again.
                    if (dialog.Describe.StartsWith("Migration", StringComparison.Ordinal)) primary.Invoke(dialog, null);
                    Tests.Check(Pump(() => dialog.Describe.StartsWith("Review", StringComparison.Ordinal), 60000), "the second review is prepared [" + dialog.Describe + "]");
                    primary.Invoke(dialog, null);
                    Tests.Check(Pump(() => dialog.Describe.StartsWith("Failed", StringComparison.Ordinal), 120000) && dialog.Describe.Contains("不一样") && !dialog.Applied, "a value changed on the device is reported as a conflict [" + dialog.Describe + "]");
                }
                Tests.Check(Developer(device.File(DeviceFile.Catalog)) == "Changed On Device" && device.Enabled && page.Edits.Count == 1, "the device keeps its own value, Cocoon is enabled and the draft is kept");

                // 3. An interrupted run is offered for recovery before anything else.
                device.Enabled = false; device.Running = false;
                new DeviceApplyJournal { State = "frozen", Serial = device.Serial, OriginalSha = DeviceApplier.Sha(File.ReadAllBytes(device.File(DeviceFile.Catalog))), StartedUtc = "t" }.Save(Path.Combine(page.ApplyRoot, "state"));
                using (ApplyDialog dialog = new ApplyDialog(page.Edits, page.NewApplier(device), lookup, page.RefreshAfterApply) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0) })
                {
                    dialog.Show(host);
                    Tests.Check(dialog.Describe.StartsWith("Recovery", StringComparison.Ordinal), "an unfinished run opens the dialog in recovery [" + dialog.Describe + "]");
                    primary.Invoke(dialog, null);
                    Tests.Check(Pump(() => dialog.Describe.StartsWith("Done", StringComparison.Ordinal), 60000) && device.Enabled && !dialog.Applied, "recovery enables Cocoon again [" + dialog.Describe + "]");
                }
                Tests.Check(!File.Exists(DeviceApplyJournal.PathIn(Path.Combine(page.ApplyRoot, "state"))), "the journal is cleared after recovery");

                // 4. A new cover: staged through the editor as a real image, applied as a file plus a new reference.
                bool cleared = false;
                Func<Task> clear = async delegate { foreach (StagedMetadata entry in new List<StagedMetadata>(page.Edits.Metadata)) await page.Edits.Undo(entry.EntryId); cleared = true; };
                clear();
                Tests.Check(Pump(() => cleared && page.Edits.Count == 0, 30000), "the conflicting draft entry is undone");
                byte[] png;
                using (Bitmap picture = new Bitmap(60, 90))
                {
                    using (Graphics g = Graphics.FromImage(picture)) { g.Clear(Color.SeaGreen); g.FillEllipse(Brushes.Gold, 10, 20, 40, 40); }
                    using (MemoryStream stream = new MemoryStream()) { picture.Save(stream, System.Drawing.Imaging.ImageFormat.Png); png = stream.ToArray(); }
                }
                bool staged = false; string stageError = null;
                Func<Task> stage = async delegate
                {
                    try
                    {
                        NativeEditorClient editor = await page.Edits.Editor();
                        string item = await page.Edits.ItemIdAsync(1);
                        NativeEditorUploadResult upload = await editor.UploadAsync(item, ArtworkSlot.Icon, png, page.Edits.Token);
                        page.Edits.Apply(await editor.StageAsync(item, ArtworkSlot.Icon, upload.Candidate.Id, page.Edits.Revision, null, page.Edits.Token));
                    }
                    catch (Exception error) { stageError = error.Message; }
                    staged = true;
                };
                stage();
                Tests.Check(Pump(() => staged, 30000) && stageError == null && page.Edits.AssetFor(1, ArtworkSlot.Icon) != null, "a new cover is staged in the draft [" + stageError + "]");
                using (ApplyDialog dialog = new ApplyDialog(page.Edits, page.NewApplier(device), lookup, page.RefreshAfterApply) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0) })
                {
                    dialog.Show(host);
                    Pump(() => !dialog.Describe.StartsWith("Loading", StringComparison.Ordinal), 30000);
                    if (dialog.Describe.StartsWith("Migration", StringComparison.Ordinal)) primary.Invoke(dialog, null);
                    Tests.Check(Pump(() => dialog.Describe.StartsWith("Review", StringComparison.Ordinal), 60000), "the artwork review is prepared [" + dialog.Describe + "]");
                    ReviewList list = (ReviewList)typeof(ApplyDialog).GetField("list", Flags).GetValue(dialog);
                    Tests.Check(list.Count == 1 && list[0].Field == "封面" && list[0].After.StartsWith("新图片 · 60×90", StringComparison.Ordinal), "the review names the slot and the new image [" + (list.Count > 0 ? list[0].Field + " " + list[0].Before + " -> " + list[0].After : "") + "]");
                    primary.Invoke(dialog, null);
                    Tests.Check(Pump(() => dialog.Describe.StartsWith("Done", StringComparison.Ordinal) || dialog.Describe.StartsWith("Failed", StringComparison.Ordinal), 120000) && dialog.Applied, "the new cover is applied [" + dialog.Describe + "]");
                    Console.WriteLine("   after apply: " + dialog.Describe);
                }
                string reference = DeviceApplyTests.Node("const {DatabaseSync}=require('node:sqlite');const d=new DatabaseSync(process.argv[1],{readOnly:true});const g=d.prepare('select boxArtLocal,boxArtSource from games where id=1').get();console.log(g.boxArtLocal+'|'+g.boxArtSource)", device.File(DeviceFile.Catalog));
                string mediaFolder = Path.Combine(Path.Combine(Path.Combine(device.Dir, "media"), lookup(1) == null ? "?" : "x"), "");
                string[] pushedFiles = Directory.Exists(Path.Combine(device.Dir, "media")) ? Directory.GetFiles(Path.Combine(device.Dir, "media"), "*", SearchOption.AllDirectories) : new string[0];
                Tests.Check(pushedFiles.Length == 1 && pushedFiles[0].Replace('\\', '/').Contains("/icon/cocoon-1-icon-") && reference.EndsWith("|user", StringComparison.Ordinal)
                    && reference.StartsWith("content://com.android.externalstorage.documents/tree/primary%3Acocoon/document/primary%3Acocoon%2Fdownloaded_media%2F", StringComparison.Ordinal)
                    && reference.Contains(Uri.EscapeDataString(Path.GetFileName(pushedFiles[0]))), "the device has the image in the platform's icon folder and the catalog refers to it [" + reference + "]");
                Tests.Check(Pump(() => page.Edits.Loaded && page.Edits.Count == 0, 30000) && device.Enabled, "the applied cover leaves the draft and Cocoon is running [count=" + page.Edits.Count + " error=" + page.Edits.Error + " enabled=" + device.Enabled + " assets=" + page.Edits.Assets.Count + "]");

                // 5. A background video: uploaded as the video itself, applied as an MP4 in the platform's hero folder.
                string ffmpeg = null;
                try { ffmpeg = VideoExport.Locate(); } catch (Exception) { }
                if (ffmpeg == null || !File.Exists(Path.Combine(Path.GetDirectoryName(ffmpeg), "ffprobe.exe"))) { Console.WriteLine("SKIP: background video needs FFmpeg and FFprobe"); return; }
                string clip = Path.Combine(Tests.Root, "flow-hero-" + Guid.NewGuid().ToString("N") + ".mp4");
                using (Process maker = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error -f lavfi -i testsrc=duration=2:size=320x180:rate=15 -pix_fmt yuv420p -c:v libx264 -y \"" + clip + "\"") { UseShellExecute = false, CreateNoWindow = true })) maker.WaitForExit(60000);
                Tests.Check(File.Exists(clip) && new FileInfo(clip).Length > 0, "a short test clip is made");
                // The previous apply reloads the library in the background, which starts a fresh editor session; let it settle.
                Pump(() => false, 1500);
                Tests.Check(Pump(() => importer.Job != null && importer.Job.Finished && page.Current == LibraryPage.Phase.Ready && page.Edits.Loaded, 60000), "the library has reloaded after the cover was applied");
                bool videoStaged = false; string videoError = null;
                Func<Task> stageVideo = async delegate
                {
                    try
                    {
                        NativeEditorClient editor = await page.Edits.Editor();
                        string item = await page.Edits.ItemIdAsync(1);
                        NativeVideoUploadResult upload = await editor.UploadVideoAsync(item, File.ReadAllBytes(clip), page.Edits.Token);
                        string video = (string)upload.Video["id"];
                        page.Edits.Apply(await editor.StageAsync(item, ArtworkSlot.Hero, video, page.Edits.Revision, null, page.Edits.Token));
                    }
                    catch (Exception error) { videoError = error.Message; }
                    videoStaged = true;
                };
                stageVideo();
                Tests.Check(Pump(() => videoStaged, 60000) && videoError == null && page.Edits.AssetFor(1, ArtworkSlot.Hero) != null, "a background video is staged [" + videoError + "]");
                using (ApplyDialog dialog = new ApplyDialog(page.Edits, page.NewApplier(device), lookup, page.RefreshAfterApply) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0) })
                {
                    dialog.Show(host);
                    Pump(() => !dialog.Describe.StartsWith("Loading", StringComparison.Ordinal), 30000);
                    if (dialog.Describe.StartsWith("Migration", StringComparison.Ordinal)) primary.Invoke(dialog, null);
                    Tests.Check(Pump(() => dialog.Describe.StartsWith("Review", StringComparison.Ordinal), 60000), "the video review is prepared [" + dialog.Describe + "]");
                    ReviewList list = (ReviewList)typeof(ApplyDialog).GetField("list", Flags).GetValue(dialog);
                    Tests.Check(list.Count == 1 && list[0].Field == "背景" && list[0].After.StartsWith("新视频 · 320×180 MP4", StringComparison.Ordinal), "the review names the background and the new video [" + (list.Count > 0 ? list[0].After : "") + "]");
                    primary.Invoke(dialog, null);
                    Tests.Check(Pump(() => dialog.Describe.StartsWith("Done", StringComparison.Ordinal) || dialog.Describe.StartsWith("Failed", StringComparison.Ordinal), 120000) && dialog.Applied, "the video is applied [" + dialog.Describe + "]");
                }
                string heroReference = DeviceApplyTests.Node("const {DatabaseSync}=require('node:sqlite');const d=new DatabaseSync(process.argv[1],{readOnly:true});const g=d.prepare('select heroLocal,heroSource from games where id=1').get();console.log(g.heroLocal+'|'+g.heroSource)", device.File(DeviceFile.Catalog));
                string[] heroFiles = Directory.GetFiles(Path.Combine(device.Dir, "media"), "*.mp4", SearchOption.AllDirectories);
                Tests.Check(heroFiles.Length == 1 && heroFiles[0].Replace('\\', '/').Contains("/hero/cocoon-1-hero-") && DeviceApplier.Sha(File.ReadAllBytes(heroFiles[0])) == DeviceApplier.Sha(File.ReadAllBytes(clip))
                    && heroReference.EndsWith(".mp4|user", StringComparison.Ordinal), "the original clip is on the device in the hero folder and the catalog plays it as the background [" + heroReference + "]");
            }
            finally
            {
                Environment.SetEnvironmentVariable("THOR_COCOON_DATA_ROOT", previousRoot);
                if (page != null)
                {
                    page.Backend.Stop();
                    Pump(() => page.Backend.State == BackendState.Stopped, 15000);
                }
                host.Close();
                host.Dispose();
                SynchronizationContext.SetSynchronizationContext(previousContext);
                WindowsFormsSynchronizationContext.AutoInstall = autoInstall;
            }
        }
    }
}
