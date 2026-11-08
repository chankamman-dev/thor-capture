using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace ThorCapture
{
    // Library page against the real backend: not-imported state, import, thumbnails, undo.
    internal static class LibraryPageTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        private static bool Pump(Func<bool> done, int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < timeoutMs) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
            return done();
        }

        internal static void Run()
        {
            string backendDir = Path.GetFullPath(Path.Combine(Adb.AppDir(), Path.Combine("..", Path.Combine("..", "cocoon-art-backend"))));
            string demoScript = Path.GetFullPath(Path.Combine(Adb.AppDir(), Path.Combine("..", Path.Combine("tools", "cocoon-demo.cjs"))));
            if (!File.Exists(Path.Combine(backendDir, "backend.cjs")) || !File.Exists(demoScript)) { Console.WriteLine("SKIP: Cocoon backend component not present"); return; }

            string source = Path.Combine(Tests.Root, "import-source-" + Guid.NewGuid().ToString("N"));
            string target = Path.Combine(Tests.Root, "import-target-" + Guid.NewGuid().ToString("N"));
            Process maker = Process.Start(new ProcessStartInfo("node", "\"" + demoScript + "\" \"" + source + "\"") { UseShellExecute = false, CreateNoWindow = true });
            maker.WaitForExit(30000);
            string previousRoot = Environment.GetEnvironmentVariable("THOR_COCOON_DATA_ROOT");
            Environment.SetEnvironmentVariable("THOR_COCOON_DATA_ROOT", target);
            // The app creates the page on its UI thread with the WinForms context installed; do the same.
            if (!(System.Threading.SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Form host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0), Size = new Size(1400, 900) };
            LibraryPage page = null;
            try
            {
                page = new LibraryPage { Dock = DockStyle.Fill, Visible = false };
                typeof(LibraryPage).GetField("backend", Flags).SetValue(page, NewBackend(page, backendDir, target));
                host.Controls.Add(page);
                host.Show();
                page.Visible = true;
                Tests.Check(Pump(() => page.Current == LibraryPage.Phase.NotImported || page.Current == LibraryPage.Phase.Failed, 25000) && page.Current == LibraryPage.Phase.NotImported,
                    "an empty data root shows the not-imported page, not an empty library");

                ImportRunner runner = (ImportRunner)typeof(LibraryPage).GetProperty("Importer", Flags).GetValue(page, null);
                LibraryImportPlan plan = null;
                runner.Client.PlanAsync(source, true, true).ContinueWith(t => { if (!t.IsFaulted) plan = t.Result; });
                Tests.Check(Pump(() => plan != null, 20000) && plan.TotalGames == 24 && plan.Files.Length > 0 && !plan.ReplacesExistingLibrary, "the import plan summarises the source for review");
                bool started = false;
                runner.Execute(plan).ContinueWith(t => started = true);
                Tests.Check(Pump(() => started && runner.Job != null && runner.Job.Finished, 60000) && runner.Job.State == "completed" && runner.Job.CanRestore,
                    "the confirmed import runs to completion under the page's runner" + (runner.Error != null ? " [" + runner.Error + "]" : runner.Job == null ? " [no job]" : " [" + runner.Job.State + " polls=" + runner.Polls + "]"));
                Tests.Check(Pump(() => page.Current == LibraryPage.Phase.Ready && page.GamesLoaded == 24, 20000), "the page reloads the imported library by itself [" + page.Describe + "]");

                GameArtCache art = (GameArtCache)typeof(LibraryPage).GetField("art", Flags).GetValue(page);
                Bitmap cover = null;
                Tests.Check(Pump(() => (cover = art.Get(1, GameArtCache.Kind.Cover, 200)) != null, 20000) && cover.Width == 256 && cover.Height == 256,
                    "covers arrive as thumbnails sized to the drawn card");
                int revalidated = art.Revalidated;
                art.Invalidate();
                art.Get(1, GameArtCache.Kind.Cover, 200);
                Tests.Check(Pump(() => art.Revalidated > revalidated, 20000) && art.Get(1, GameArtCache.Kind.Cover, 200) != null, "after a library change unchanged covers revalidate with 304 and stay");

                bool undone = false;
                runner.Restore().ContinueWith(t => undone = t.Result);
                Tests.Check(Pump(() => undone && page.Current == LibraryPage.Phase.NotImported, 30000), "undoing the import returns to the not-imported page");
                Tests.Check(File.Exists(Path.Combine(Path.Combine(source, "_data"), "cocoon_db")), "the import source is left in place");
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
            }
        }

        private static CocoonBackend NewBackend(LibraryPage page, string backendDir, string root)
        {
            // The page already built its backend from defaults; point it explicitly at this test's roots.
            CocoonBackend backend = page.Backend;
            backend.BackendDir = backendDir;
            backend.DataRoot = root;
            return backend;
        }
    }
}
