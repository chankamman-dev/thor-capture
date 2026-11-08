using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace ThorCapture
{
    // Metadata editing through the page's own draft session against the real backend and demo
    // data: form -> review -> stage -> undo. The library database must not change.
    internal static class GameEditingTests
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

            string root = Path.Combine(Tests.Root, "edit-demo-" + Guid.NewGuid().ToString("N"));
            Process maker = Process.Start(new ProcessStartInfo("node", "\"" + demoScript + "\" \"" + root + "\"") { UseShellExecute = false, CreateNoWindow = true });
            maker.WaitForExit(30000);
            string database = Path.Combine(Path.Combine(root, "_data"), "cocoon_db");
            byte[] before = File.ReadAllBytes(database);
            string previousRoot = Environment.GetEnvironmentVariable("THOR_COCOON_DATA_ROOT");
            Environment.SetEnvironmentVariable("THOR_COCOON_DATA_ROOT", root);
            // The dialogs await on the UI context like the app does under Application.Run. Here DoEvents is the
            // outermost loop, and WinForms uninstalls an auto-installed context each time one ends, which would
            // send continuations to the thread pool. Pin one context for the whole suite instead.
            bool autoInstall = WindowsFormsSynchronizationContext.AutoInstall;
            System.Threading.SynchronizationContext previousContext = System.Threading.SynchronizationContext.Current;
            WindowsFormsSynchronizationContext.AutoInstall = false;
            System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Form host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0), Size = new Size(1400, 900) };
            LibraryPage page = null;
            try
            {
                page = new LibraryPage { Dock = DockStyle.Fill, Visible = false };
                page.Backend.BackendDir = backendDir;
                page.Backend.DataRoot = root;
                host.Controls.Add(page);
                host.Show();
                page.Visible = true;
                Tests.Check(Pump(() => page.Current == LibraryPage.Phase.Ready && page.GamesLoaded == 24, 30000), "the demo library opens for editing [" + page.Describe + "]");
                GameEditSession edits = page.Edits;
                Tests.Check(Pump(() => edits.Loaded, 20000) && edits.Error == null && edits.Revision == 0 && edits.Count == 0, "the page reads an empty draft after loading the library [" + edits.Error + "]");
                FlatButton pendingButton = (FlatButton)typeof(LibraryPage).GetField("pendingButton", Flags).GetValue(page);
                Tests.Check(!pendingButton.Visible, "the draft entry stays out of the bar while nothing is staged");

                GameEntry game = ((GameGridView)typeof(LibraryPage).GetField("grid", Flags).GetValue(page)).Selected;
                Tests.Check(game != null, "a game is selected for editing");
                using (MetadataDialog dialog = new MetadataDialog(edits, game) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0) })
                {
                    dialog.Show(host);
                    Tests.Check(Pump(() => dialog.Describe.StartsWith("Edit", StringComparison.Ordinal), 20000) && dialog.Describe.Contains("error= "), "the form loads the library facts [" + dialog.Describe + "]");
                    MethodInfo primary = typeof(MetadataDialog).GetMethod("Primary", Flags);
                    primary.Invoke(dialog, null);
                    Pump(() => false, 300);
                    Tests.Check(dialog.Describe.StartsWith("Edit", StringComparison.Ordinal) && edits.Count == 0, "an untouched form stages nothing [" + dialog.Describe + "]");

                    Dictionary<string, TextField> texts = (Dictionary<string, TextField>)typeof(MetadataDialog).GetField("texts", Flags).GetValue(dialog);
                    texts["developer"].Text = "Thor Test Studio";
                    texts["releaseDate"].Text = "not a date";
                    primary.Invoke(dialog, null);
                    Pump(() => false, 100);
                    Tests.Check(dialog.Describe.StartsWith("Edit", StringComparison.Ordinal) && !dialog.Describe.Contains("error= "), "a malformed date is refused before any request [" + dialog.Describe + "]");
                    texts["releaseDate"].Text = "2024-03-15";
                    primary.Invoke(dialog, null);
                    Tests.Check(Pump(() => dialog.Describe.StartsWith("Review", StringComparison.Ordinal), 20000) && dialog.Describe.EndsWith("changes=2", StringComparison.Ordinal), "the review lists exactly the changed fields [" + dialog.Describe + "]");
                    Tests.Check(edits.Count == 0, "reviewing does not stage anything");

                    // Keep only the developer; the date stays out of the draft.
                    ChangeList changes = (ChangeList)typeof(MetadataDialog).GetField("changes", Flags).GetValue(dialog);
                    List<ChangeList.Row> rows = (List<ChangeList.Row>)typeof(ChangeList).GetField("rows", Flags).GetValue(changes);
                    foreach (ChangeList.Row row in rows) row.Picked = row.Field == "developer";
                    primary.Invoke(dialog, null);
                    Tests.Check(Pump(() => edits.Count == 1, 20000) && edits.Revision == 1, "confirming stages the ticked fields as one draft entry [" + dialog.Describe + "]");
                }
                StagedMetadata staged = edits.MetadataFor(game.Id);
                Tests.Check(staged != null && staged.Fields.Count == 1 && (string)staged.Fields["developer"] == "Thor Test Studio", "only the ticked field is in the draft, and the detail view can find it");
                Tests.Check(pendingButton.Visible && pendingButton.Hint == "1", "the bar shows the draft entry with its count [visible=" + pendingButton.Visible + " hint=" + pendingButton.Hint + " count=" + edits.Count + " loaded=" + edits.Loaded + " " + page.Describe + "]");

                using (PendingDialog pending = new PendingDialog(edits, delegate(int id) { return id == game.Id ? game.Name : "?"; }) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0) })
                {
                    pending.Show(host);
                    PendingList list = (PendingList)typeof(PendingDialog).GetField("list", Flags).GetValue(pending);
                    Tests.Check(list.Count == 1 && list[0].Title == game.Name && list[0].Detail.Contains("开发商"), "the draft list names the game and the staged field");
                    list.RaiseUndo(0);
                    Tests.Check(Pump(() => edits.Count == 0 && pending.Describe.StartsWith("rows=0", StringComparison.Ordinal) && pending.Describe.Contains("busy=False"), 20000) && edits.Revision == 2, "undo removes the entry and the list follows [" + pending.Describe + "]");
                }
                Tests.Check(!pendingButton.Visible, "the draft entry leaves the bar once the draft is empty");
                byte[] after = File.ReadAllBytes(database);
                bool same = before.Length == after.Length;
                for (int i = 0; same && i < before.Length; i++) same = before[i] == after[i];
                Tests.Check(same, "staging and undoing leave the library database byte-identical");
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
                System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
                WindowsFormsSynchronizationContext.AutoInstall = autoInstall;
            }
        }
    }
}
