using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace ThorCapture
{
    // Scraping. The offline part checks how a chosen candidate becomes catalog values. The live part
    // (real sources, real credentials) only runs when THOR_SCRAPE_CONFIG and THOR_SCRAPE_CATALOG point
    // at a config.json with keys and a catalog file; it works in its own data root.
    internal static class ScrapeTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        private static bool Pump(Func<bool> done, int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < timeoutMs) { Application.DoEvents(); Thread.Sleep(5); }
            return done();
        }

        internal static void Run()
        {
            // Shapes as the backend returns them from /api/scrape/select (merge.selected).
            Dictionary<string, object> selected = (Dictionary<string, object>)Json.Parse("{"
                + "\"canonicalTitle\":{\"value\":\"Ever17: The Out of Infinity\",\"nativeField\":\"title\"},"
                + "\"localizedTitles\":{\"value\":{\"zhHans\":\"\",\"zhHant\":\"時空輪迴\",\"en\":\"x\"},\"nativeField\":\"displayName\"},"
                + "\"summary\":{\"value\":\"  A tale.  \",\"nativeField\":\"summary\"},"
                + "\"developer\":{\"value\":[\"KID Corp.\",\" 5pb \"],\"nativeField\":\"developer\"},"
                + "\"publisher\":{\"value\":[],\"nativeField\":\"publisher\"},"
                + "\"genres\":{\"value\":[\"Adventure\"],\"nativeField\":\"genres\"},"
                + "\"releaseDates\":{\"value\":[{\"region\":null,\"date\":\"2009-03-12\",\"precision\":\"day\"}],\"nativeField\":\"releaseDate\"},"
                + "\"rating\":{\"value\":{\"value\":77,\"scale\":100,\"count\":3},\"nativeField\":\"rating\"},"
                + "\"players\":{\"value\":{\"offlineMin\":1,\"offlineMax\":2,\"onlineMax\":0},\"nativeField\":\"players\"},"
                + "\"region\":{\"value\":[\"JP\"],\"nativeField\":\"region\"},"
                + "\"franchises\":{\"value\":[\"Infinity\"]}}");
            Dictionary<string, object> native = ScrapeClient.NativeFields(selected, null);
            Tests.Check((string)native["title"] == "Ever17: The Out of Infinity" && (string)native["displayName"] == "時空輪迴" && (string)native["summary"] == "A tale.",
                "titles and summary become catalog text; a Chinese name is preferred for the display name");
            Tests.Check((string)native["developer"] == "KID Corp., 5pb" && (string)native["genres"] == "Adventure" && (string)native["region"] == "JP" && !native.ContainsKey("publisher"),
                "lists are joined, and an empty list offers nothing");
            Tests.Check((double)native["releaseDate"] == 1236816000000d && (double)native["rating"] == 77 && (string)native["players"] == "{\"offlineMin\":1,\"offlineMax\":2,\"onlineMax\":0}",
                "the release day becomes the catalog's millisecond timestamp; rating and player counts keep the catalog's form");
            Tests.Check(native.Count == 9, "fields the catalog has no column for are left out [" + native.Count + "]");
            Dictionary<string, object> locks = (Dictionary<string, object>)Json.Parse("{\"summary\":true,\"releaseDates\":true}");
            Dictionary<string, object> locked = ScrapeClient.NativeFields(selected, locks);
            Tests.Check(!locked.ContainsKey("summary") && !locked.ContainsKey("releaseDate") && locked.ContainsKey("title"), "fields the user locked are not offered");
            Dictionary<string, object> odd = (Dictionary<string, object>)Json.Parse("{\"releaseDates\":{\"value\":[{\"date\":\"1998\"}],\"nativeField\":\"releaseDate\"},\"rating\":{\"value\":{\"value\":0},\"nativeField\":\"rating\"},"
                + "\"players\":{\"value\":{\"offlineMin\":0,\"offlineMax\":0,\"onlineMax\":0},\"nativeField\":\"players\"}}");
            Dictionary<string, object> sparse = ScrapeClient.NativeFields(odd, null);
            Tests.Check((double)sparse["releaseDate"] == 883612800000d && sparse.Count == 1, "a year-only date is kept; a zero rating and empty player counts are not offered");
            Tests.Check(ScrapeClient.NativeFields(null, null).Count == 0, "no selection offers nothing");

            RunLive();
        }

        private static void RunLive()
        {
            string config = Environment.GetEnvironmentVariable("THOR_SCRAPE_CONFIG"), catalog = Environment.GetEnvironmentVariable("THOR_SCRAPE_CATALOG");
            string backendDir = Path.GetFullPath(Path.Combine(Adb.AppDir(), Path.Combine("..", Path.Combine("..", "cocoon-art-backend"))));
            if (string.IsNullOrEmpty(config) || string.IsNullOrEmpty(catalog) || !File.Exists(config) || !File.Exists(catalog) || !File.Exists(Path.Combine(backendDir, "backend.cjs")))
            { Console.WriteLine("SKIP: live scraping needs THOR_SCRAPE_CONFIG and THOR_SCRAPE_CATALOG"); return; }

            string pulled = Path.Combine(Tests.Root, "scrape-source-" + Guid.NewGuid().ToString("N")), target = Path.Combine(Path.Combine(Tests.Root, "scrape-local-" + Guid.NewGuid().ToString("N")), "cocoon");
            Directory.CreateDirectory(Path.Combine(pulled, "_data")); Directory.CreateDirectory(target);
            File.Copy(catalog, Path.Combine(Path.Combine(pulled, "_data"), "cocoon_db"));
            if (File.Exists(catalog + "-wal")) File.Copy(catalog + "-wal", Path.Combine(Path.Combine(pulled, "_data"), "cocoon_db-wal"));
            File.Copy(config, Path.Combine(target, "config.json")); // the backend reads its sources' credentials from its own data root
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
                page.Backend.BackendDir = backendDir; page.Backend.DataRoot = target;
                host.Controls.Add(page); host.Show(); page.Visible = true;
                Tests.Check(Pump(() => page.Current == LibraryPage.Phase.NotImported, 25000), "live: the isolated library starts empty [" + page.Describe + "]");
                ImportRunner importer = (ImportRunner)typeof(LibraryPage).GetProperty("Importer", Flags).GetValue(page, null);
                LibraryImportPlan plan = null;
                importer.Client.PlanAsync(pulled, false, false).ContinueWith(t => { if (!t.IsFaulted) plan = t.Result; });
                Tests.Check(Pump(() => plan != null, 30000), "live: the catalog copy is accepted");
                importer.Execute(plan);
                Tests.Check(Pump(() => page.Current == LibraryPage.Phase.Ready && page.Edits.Loaded, 90000), "live: the library is ready [" + page.Describe + "]");
                List<GameEntry> all = (List<GameEntry>)typeof(LibraryPage).GetField("all", Flags).GetValue(page);

                // Sources: the configured ones answer a connection test.
                using (ScrapeSettingsDialog settings = new ScrapeSettingsDialog(page.Backend) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0) })
                {
                    settings.Show(host);
                    SourceList sources = (SourceList)typeof(ScrapeSettingsDialog).GetField("list", Flags).GetValue(settings);
                    Tests.Check(Pump(() => sources.Count == 6, 20000), "live: six sources are listed [" + settings.Describe + "]");
                    sources.RaiseTest("thegamesdb");
                    System.Collections.IList rows = (System.Collections.IList)typeof(SourceList).GetField("rows", Flags).GetValue(sources);
                    Func<string> verdict = delegate { foreach (object row in rows) { ScrapeProvider p = (ScrapeProvider)row.GetType().GetField("Source").GetValue(row); if (p.Id == "thegamesdb") return (string)row.GetType().GetField("Result").GetValue(row) + "|" + row.GetType().GetField("Passed").GetValue(row); } return null; };
                    Tests.Check(Pump(() => verdict() != null && !verdict().StartsWith("正在", StringComparison.Ordinal), 60000) && verdict().EndsWith("|True", StringComparison.Ordinal), "live: TheGamesDB accepts the stored key [" + verdict() + "]");
                }

                // One game: search, pick, review, stage.
                GameEntry game = all.Find(entry => entry.Name.StartsWith("Ever17", StringComparison.OrdinalIgnoreCase)) ?? all.Find(entry => entry.Platform == "gba");
                MethodInfo primary = typeof(ScrapeDialog).GetMethod("Primary", Flags);
                using (ScrapeDialog dialog = new ScrapeDialog(page.Edits, game) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0) })
                {
                    dialog.Show(host);
                    CandidateList candidates = (CandidateList)typeof(ScrapeDialog).GetField("list", Flags).GetValue(dialog);
                    Tests.Check(Pump(() => dialog.Describe.StartsWith("Results", StringComparison.Ordinal), 180000) && candidates.Count > 0, "live: searching \"" + game.Name + "\" returns candidates [" + dialog.Describe + "]");
                    List<ScrapeCandidate> rows = (List<ScrapeCandidate>)typeof(CandidateList).GetField("rows", Flags).GetValue(candidates);
                    int metadata = rows.FindIndex(row => row.Provider == "thegamesdb");
                    Tests.Check(metadata >= 0, "live: a TheGamesDB entry is among them [" + rows.Count + "]");
                    candidates.Select(metadata);
                    primary.Invoke(dialog, null);
                    Tests.Check(Pump(() => dialog.Describe.StartsWith("Review", StringComparison.Ordinal) || (dialog.Describe.StartsWith("Results", StringComparison.Ordinal) && !dialog.Describe.Contains("note= ")), 180000)
                        && dialog.Describe.StartsWith("Review", StringComparison.Ordinal), "live: the chosen entry's fields are compared with the library [" + dialog.Describe + "]");
                    ChangeList changes = (ChangeList)typeof(ScrapeDialog).GetField("changes", Flags).GetValue(dialog);
                    List<ChangeList.Row> fields = (List<ChangeList.Row>)typeof(ChangeList).GetField("rows", Flags).GetValue(changes);
                    Console.WriteLine("   review: " + string.Join(" ; ", fields.ConvertAll(row => row.Field + (row.Picked ? "*" : "") + "=" + (row.After.Length > 40 ? row.After.Substring(0, 40) + "…" : row.After)).ToArray()));
                    Tests.Check(page.Edits.Count == 0, "live: reviewing stages nothing");
                    fields[0].Picked = true;
                    primary.Invoke(dialog, null);
                    Tests.Check(Pump(() => page.Edits.Count == 1, 60000), "live: confirming stages the ticked fields in the draft [" + dialog.Describe + "]");
                }
                StagedMetadata staged = page.Edits.MetadataFor(game.Id);
                Tests.Check(staged != null && staged.Fields.Count >= 1, "live: the draft holds the scraped fields for that game");

                // Artwork from the sources: browse covers and logos, stage one of each.
                using (ArtworkDialog artwork = new ArtworkDialog(page.Edits, game, ArtworkSlot.Icon) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0) })
                {
                    artwork.Show(host);
                    AssetGrid assets = (AssetGrid)typeof(ArtworkDialog).GetField("grid", Flags).GetValue(artwork);
                    MethodInfo use = typeof(ArtworkDialog).GetMethod("UseSelected", Flags);
                    foreach (ArtworkSlot slot in new[] { ArtworkSlot.Icon, ArtworkSlot.Logo })
                    {
                        if (slot != ArtworkSlot.Icon) ((Segmented)typeof(ArtworkDialog).GetField("slots", Flags).GetValue(artwork)).SelectedIndex = (int)slot;
                        Tests.Check(Pump(() => artwork.Describe.StartsWith("slot=" + slot + " busy=False", StringComparison.Ordinal) && assets.Count > 0, 180000), "live: the sources offer " + slot + " artwork [" + artwork.Describe + "]");
                        if (slot == ArtworkSlot.Icon)
                        {
                            List<FlatButton> chips = (List<FlatButton>)typeof(ArtworkDialog).GetField("chips", Flags).GetValue(artwork);
                            FlatButton sgdb = chips.Find(chip => (string)chip.Tag == "steamgriddb");
                            Console.WriteLine("   sources: " + string.Join(" | ", chips.ConvertAll(chip => chip.Text + (chip.Enabled ? "" : "(未设置)")).ToArray()));
                            Tests.Check(chips.Count >= 3 && sgdb != null && sgdb.Enabled && sgdb.Text.StartsWith("SteamGridDB ", StringComparison.Ordinal), "live: each artwork source has a toggle with its count");
                            int before = assets.Count;
                            sgdb.Checked = false; typeof(ArtworkDialog).GetMethod("Filter", Flags).Invoke(artwork, null);
                            int without = assets.Count;
                            sgdb.Checked = true; typeof(ArtworkDialog).GetMethod("Filter", Flags).Invoke(artwork, null);
                            Tests.Check(without < before && assets.Count == before, "live: unticking a source hides its images and ticking brings them back [" + before + " -> " + without + "]");
                        }
                        assets.Select(0);
                        use.Invoke(artwork, null);
                        Tests.Check(Pump(() => artwork.Describe.Contains("busy=False") && page.Edits.AssetFor(game.Id, slot) != null, 120000), "live: the chosen " + slot + " is downloaded and staged [" + artwork.Describe + "]");
                    }
                }

                // Steam's official artwork for a non-PC entry, found through SteamGridDB when the store is unreachable.
                GameEntry steamGame = all.Find(entry => entry.Name.StartsWith("A Monster's Expedition", StringComparison.OrdinalIgnoreCase));
                if (steamGame != null)
                    using (ArtworkDialog artwork = new ArtworkDialog(page.Edits, steamGame, ArtworkSlot.Icon) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0) })
                    {
                        artwork.Show(host);
                        AssetGrid assets = (AssetGrid)typeof(ArtworkDialog).GetField("grid", Flags).GetValue(artwork);
                        Tests.Check(Pump(() => artwork.Describe.StartsWith("slot=Icon busy=False", StringComparison.Ordinal) && assets.Count > 0, 180000), "live: covers are offered for " + steamGame.Name + " [" + artwork.Describe + "]");
                        List<FlatButton> chips = (List<FlatButton>)typeof(ArtworkDialog).GetField("chips", Flags).GetValue(artwork);
                        FlatButton steam = chips.Find(chip => (string)chip.Tag == "steam");
                        Console.WriteLine("   sources: " + string.Join(" | ", chips.ConvertAll(chip => chip.Text + (chip.Enabled ? "" : "(未设置)")).ToArray()));
                        List<ScrapeAsset> shown = (List<ScrapeAsset>)typeof(AssetGrid).GetField("assets", Flags).GetValue(assets);
                        Tests.Check(steam != null && steam.Enabled && shown.Exists(asset => asset.Provider == "steam" && asset.Url.Contains("steamstatic.com")), "live: Steam's official covers are offered for an Android entry");
                    }

                // A small batch.
                List<GameEntry> some = all.FindAll(entry => entry.Platform == "gba").GetRange(0, 3);
                using (BatchScrapeDialog batch = new BatchScrapeDialog(page.Edits, some, some, all) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0) })
                {
                    batch.Show(host);
                    List<FlatButton> chips = (List<FlatButton>)typeof(BatchScrapeDialog).GetField("chips", Flags).GetValue(batch);
                    Tests.Check(Pump(() => chips.Count > 0, 20000), "live: the batch offers the configured sources");
                    foreach (FlatButton chip in chips) chip.Checked = (string)chip.Tag == "thegamesdb";
                    typeof(BatchScrapeDialog).GetMethod("Run", Flags).Invoke(batch, null);
                    Tests.Check(Pump(() => batch.Describe.StartsWith("Done", StringComparison.Ordinal), 240000) && batch.Describe.Contains(" 3/3 ") && batch.Describe.EndsWith("rows=3", StringComparison.Ordinal),
                        "live: the batch runs to the end and lists every game [" + batch.Describe + "]");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("THOR_COCOON_DATA_ROOT", previousRoot);
                if (page != null) { page.Backend.Stop(); Pump(() => page.Backend.State == BackendState.Stopped, 15000); }
                host.Close(); host.Dispose();
                SynchronizationContext.SetSynchronizationContext(previousContext);
                WindowsFormsSynchronizationContext.AutoInstall = autoInstall;
            }
        }
    }
}
