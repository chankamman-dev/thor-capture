using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace ThorCapture
{
    // Game library front end: JSON reader, game parsing and the owned Cocoon backend process.
    internal static class LibraryTests
    {
        private static bool Pump(Func<bool> done, int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < timeoutMs) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
            return done();
        }

        internal static void Run()
        {
            object parsed = Json.Parse("{\"a\":[1,2.5,-3e2],\"b\":{\"c\":\"x\\\"y\\u4e2d\"},\"t\":true,\"n\":null}");
            List<object> list = (List<object>)Json.Get(parsed, "a");
            Tests.Check(list.Count == 3 && (double)list[2] == -300 && Json.Text(Json.Get(parsed, "b"), "c") == "x\"y中" && Json.Flag(parsed, "t") && Json.Get(parsed, "n") == null,
                "JSON reader handles nesting, numbers, escapes and literals");
            Tests.Reject(() => Json.Parse("{\"a\":1,}"), "JSON reader rejects trailing commas");
            Tests.Reject(() => Json.Parse(new string('[', 100) + new string(']', 100)), "JSON reader bounds nesting depth");
            Tests.Check(Json.Parse(Json.Quote("a\"b\\c\n")) as string == "a\"b\\c\n", "JSON quoting round-trips");

            GameEntry game = GameEntry.From(Json.Parse("{\"id\":7,\"name\":\"测试\",\"platform\":\"snes\",\"fileName\":\"t.sfc\",\"favorite\":true," +
                "\"art\":{\"icon\":{\"uri\":\"/x\",\"source\":\"steam\"},\"logo\":{\"uri\":null},\"hero\":{\"uri\":\"/h\"}}," +
                "\"metadata\":{\"developer\":\"Dev\",\"releaseDate\":1555372800,\"rating\":3.5}}"));
            Tests.Check(game.Id == 7 && game.Favorite && game.HasCover && !game.HasLogo && game.HasHero && game.ArtCount == 2 && game.CoverSource == "steam"
                && game.Developer == "Dev" && game.Released.HasValue && game.Released.Value.Year == 2019 && game.Rating == 3.5 && GameEntry.PlatformName("snes") == "SFC",
                "game entries parse artwork slots, metadata and platform names");

            CocoonBackend missing = new CocoonBackend(null) { BackendDir = Path.Combine(Tests.Root, "no-backend"), DataRoot = Path.Combine(Tests.Root, "no-data") };
            missing.Start();
            Tests.Check(missing.State == BackendState.Failed && missing.FailureCode == "BACKEND_NOT_FOUND", "a missing backend component fails with a clear code");

            string backendDir = Path.GetFullPath(Path.Combine(Adb.AppDir(), Path.Combine("..", Path.Combine("..", "cocoon-art-backend"))));
            string demoScript = Path.GetFullPath(Path.Combine(Adb.AppDir(), Path.Combine("..", Path.Combine("tools", "cocoon-demo.cjs"))));
            if (!File.Exists(Path.Combine(backendDir, "backend.cjs")) || !File.Exists(demoScript)) { Console.WriteLine("SKIP: Cocoon backend component not present"); return; }

            CocoonBackend nodeless = new CocoonBackend(null) { NodePath = "thor-missing-node-" + Guid.NewGuid().ToString("N") + ".exe", BackendDir = backendDir, DataRoot = Path.Combine(Tests.Root, "nodeless") };
            nodeless.Start();
            Tests.Check(nodeless.State == BackendState.Failed && nodeless.FailureCode == "NODE_NOT_FOUND", "a missing Node runtime fails with a clear code");

            // Real backend.cjs against a fresh demo data root.
            string root = Path.Combine(Tests.Root, "cocoon-demo-" + Guid.NewGuid().ToString("N"));
            Process maker = Process.Start(new ProcessStartInfo("node", "\"" + demoScript + "\" \"" + root + "\"") { UseShellExecute = false, CreateNoWindow = true });
            maker.WaitForExit(30000);
            CocoonBackend backend = new CocoonBackend(null) { BackendDir = backendDir, DataRoot = root };
            try
            {
                backend.Start();
                Tests.Check(Pump(() => backend.State == BackendState.Ready || backend.State == BackendState.Failed, 20000) && backend.State == BackendState.Ready
                    && backend.Endpoint != null && backend.Endpoint.Host == "127.0.0.1" && backend.LocalCatalog, "the owned backend becomes ready on a loopback endpoint");
                object games = backend.GetJson("/api/games");
                Tests.Check(games is List<object> && ((List<object>)games).Count == 24, "the library is read through the authenticated API");
                byte[] cover = backend.GetBytes("/api/art?id=1&type=icon");
                Tests.Check(cover != null && cover.Length > 100 && backend.GetBytes("/api/art?id=7&type=icon") == null, "artwork is served for present slots and absent for missing ones");

                // A second owner of the same data root is refused, and our instance is untouched.
                CocoonBackend second = new CocoonBackend(null) { BackendDir = backendDir, DataRoot = root };
                second.Start();
                Tests.Check(Pump(() => second.State == BackendState.Failed || second.State == BackendState.Ready, 20000) && second.State == BackendState.Failed
                    && second.FailureCode == "DATA_ROOT_IN_USE" && backend.State == BackendState.Ready, "a data root in use by another instance is refused");
            }
            finally
            {
                backend.Stop();
                Pump(() => backend.State == BackendState.Stopped, 15000);
            }
            Tests.Check(backend.State == BackendState.Stopped, "stopping through stdin ends the owned backend cleanly");
        }
    }
}
