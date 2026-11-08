using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ThorCapture.Backend;

namespace ThorCapture
{
    // "Read from the handheld" through the page's own runner and the import dialog, against the real
    // backend. The device is a fixture reader over a demo database; no adb is involved.
    internal static class DevicePullTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        private sealed class FixtureReader : IBrokerFileReader
        {
            public byte[] Catalog;
            public string Refuse;
            public int Calls;
            public Task<BrokerFileResult> ReadAsync(DeviceSession session, string file, CancellationToken cancellation)
            {
                Interlocked.Increment(ref Calls);
                if (Refuse != null) return Task.FromResult(new BrokerFileResult { Code = Refuse });
                if (file == "catalog-wal") return Task.FromResult(new BrokerFileResult { Code = "DEVICE_FILE_MISSING" });
                return Task.FromResult(new BrokerFileResult { Bytes = (byte[])Catalog.Clone() });
            }
            public Task<bool> StopAsync() { return Task.FromResult(true); }
        }

        private static bool Pump(Func<bool> done, int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < timeoutMs) { Application.DoEvents(); Thread.Sleep(5); }
            return done();
        }

        internal static void Run()
        {
            string backendDir = Path.GetFullPath(Path.Combine(Adb.AppDir(), Path.Combine("..", Path.Combine("..", "cocoon-art-backend"))));
            string demoScript = Path.GetFullPath(Path.Combine(Adb.AppDir(), Path.Combine("..", Path.Combine("tools", "cocoon-demo.cjs"))));
            if (!File.Exists(Path.Combine(backendDir, "backend.cjs")) || !File.Exists(demoScript)) { Console.WriteLine("SKIP: Cocoon backend component not present"); return; }

            string device = Path.Combine(Tests.Root, "pull-device-" + Guid.NewGuid().ToString("N"));
            string target = Path.Combine(Tests.Root, "pull-local-" + Guid.NewGuid().ToString("N"));
            Process maker = Process.Start(new ProcessStartInfo("node", "\"" + demoScript + "\" \"" + device + "\"") { UseShellExecute = false, CreateNoWindow = true });
            maker.WaitForExit(30000);
            FixtureReader reader = new FixtureReader { Catalog = File.ReadAllBytes(Path.Combine(Path.Combine(device, "_data"), "cocoon_db")) };
            // What a real handheld answered without root: exit 0 and the error text in place of the file.
            byte[] refusal = System.Text.Encoding.ASCII.GetBytes("cat: /data/data/rip.moth.cocoonshell/databases/cocoon_db: Permission denied\n");
            Tests.Check(BrokerFileReader.Classify("catalog", refusal) == "DEVICE_READ_DENIED" && BrokerFileReader.Classify("catalog-wal", refusal) == "DEVICE_READ_DENIED",
                "error text returned in place of the file is a refused read, not a catalog");
            Tests.Check(BrokerFileReader.Classify("catalog", reader.Catalog) == "OK" && BrokerFileReader.Classify("catalog-wal", new byte[0]) == "DEVICE_FILE_MISSING"
                && BrokerFileReader.Classify("catalog-wal", new byte[] { 0x37, 0x7F, 0x06, 0x82, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }) == "OK",
                "a real catalog, an absent journal and a journal header are told apart by signature");
            string previousRoot = Environment.GetEnvironmentVariable("THOR_COCOON_DATA_ROOT");
            Environment.SetEnvironmentVariable("THOR_COCOON_DATA_ROOT", target);
            Settings.Set("cocoon.rootRead", "0");
            // See GameEditingTests: pin one UI context, DoEvents would otherwise uninstall it.
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
                Tests.Check(Pump(() => page.Current == LibraryPage.Phase.NotImported, 25000), "an empty data root offers reading from the handheld [" + page.Describe + "]");
                page.DevicePull.Reader = reader;
                ImportRunner importer = (ImportRunner)typeof(LibraryPage).GetProperty("Importer", Flags).GetValue(page, null);
                MethodInfo primary = typeof(ImportDialog).GetMethod("Primary", Flags), secondary = typeof(ImportDialog).GetMethod("Secondary", Flags);

                using (ImportDialog dialog = new ImportDialog(importer, page.DevicePull, "offline-thor") { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0) })
                {
                    dialog.Show(host);
                    Pump(() => false, 200);
                    Tests.Check(dialog.Describe.StartsWith("Source", StringComparison.Ordinal) && reader.Calls == 0, "opening the dialog sends nothing to the device");

                    reader.Refuse = "DEVICE_READ_DENIED";
                    primary.Invoke(dialog, null);
                    Tests.Check(Pump(() => dialog.Describe.StartsWith("Source", StringComparison.Ordinal) && !page.DevicePull.Running && page.DevicePull.Error != null, 20000)
                        && page.DevicePull.Error == "DEVICE_READ_DENIED" && dialog.Describe.Contains("没有尝试提权"), "a refused read is reported as refused, without a plan [" + dialog.Describe + "]");
                    Tests.Check(!page.DevicePull.UsedRoot && dialog.Describe.Contains("root=True") && Settings.Get("cocoon.rootRead", "0") == "0", "the first read never uses root; a root read is only offered after the refusal");
                    primary.Invoke(dialog, null);
                    Tests.Check(Pump(() => dialog.Describe.StartsWith("Source", StringComparison.Ordinal) && !page.DevicePull.Running, 20000) && page.DevicePull.UsedRoot
                        && Settings.Get("cocoon.rootRead", "0") == "1" && dialog.Describe.Contains("用 root 也没有读到") && dialog.Describe.Contains("root=False"),
                        "choosing root is remembered, and a refusal under root says so [" + dialog.Describe + "]");
                    Tests.Check(page.Current == LibraryPage.Phase.NotImported && !File.Exists(Path.Combine(Path.Combine(target, "_data"), "cocoon_db")), "a refused read leaves the local library untouched");

                    reader.Refuse = null;
                    int before = reader.Calls;
                    primary.Invoke(dialog, null);
                    Tests.Check(Pump(() => dialog.Describe.StartsWith("Review", StringComparison.Ordinal), 30000) && dialog.Describe.EndsWith("plan=24", StringComparison.Ordinal),
                        "retrying reads the catalog and shows its plan for review [" + dialog.Describe + " " + page.DevicePull.Error + "]");
                    Tests.Check(reader.Calls - before == 4, "the catalog and its journal are each read twice and compared");
                    Tests.Check(page.Current == LibraryPage.Phase.NotImported, "the reviewed plan is not applied by itself");

                    primary.Invoke(dialog, null);
                    Tests.Check(Pump(() => dialog.Describe.StartsWith("Done", StringComparison.Ordinal), 60000) && importer.Error == null && importer.Job.State == "completed", "confirming imports the plan [" + dialog.Describe + " " + importer.Error + "]");
                }
                Tests.Check(Pump(() => page.Current == LibraryPage.Phase.Ready && page.GamesLoaded == 24, 20000), "the page shows the library read from the device [" + page.Describe + "]");
                byte[] after = File.ReadAllBytes(Path.Combine(Path.Combine(device, "_data"), "cocoon_db"));
                Tests.Check(Convert.ToBase64String(after) == Convert.ToBase64String(reader.Catalog), "the device fixture is byte-identical afterwards");

                // A read in flight is abandoned when the dialog is cancelled.
                using (ImportDialog dialog = new ImportDialog(importer, page.DevicePull, "offline-thor") { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0) })
                {
                    dialog.Show(host);
                    primary.Invoke(dialog, null);
                    secondary.Invoke(dialog, null);
                    Tests.Check(Pump(() => !page.DevicePull.Running, 20000) && (page.DevicePull.Error == "DEVICE_CANCELLED" || page.DevicePull.Plan != null), "cancelling ends the read without leaving it running [" + page.DevicePull.Error + "]");
                }
                Tests.Check(page.DevicePull.StopAsync().GetAwaiter().GetResult(), "the device broker stops cleanly");
            }
            finally
            {
                Environment.SetEnvironmentVariable("THOR_COCOON_DATA_ROOT", previousRoot);
                Settings.Set("cocoon.rootRead", "0");
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
