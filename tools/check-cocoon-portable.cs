using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;

namespace ThorCapture
{
    // Fixed entrypoint: production backend code is compiled, but no window or ADB starts.
    internal static class CocoonPortableCheck
    {
        static void Check(bool value, string code)
        { if (!value) throw new InvalidOperationException(code); Console.WriteLine("PASS: " + code); }
        static bool ContainsText(object value, string text)
        {
            string item = value as string; if (item != null) return item.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
            IDictionary map = value as IDictionary; if (map != null) foreach (object child in map.Values) if (ContainsText(child, text)) return true;
            IList list = value as IList; if (list != null) foreach (object child in list) if (ContainsText(child, text)) return true;
            return false;
        }
        static int Main(string[] arguments)
        {
            CocoonBackend backend = null; Process observed = null;
            try
            {
                if (arguments.Length != 2 || !Path.IsPathRooted(arguments[0]) || !Path.IsPathRooted(arguments[1])) throw new InvalidOperationException("PROBE_ARGUMENTS_INVALID");
                string node = Path.GetFullPath(arguments[0]), data = Path.GetFullPath(arguments[1]);
                string app = Path.GetFullPath(Adb.AppDir()), expected = Path.Combine(app, "cocoon");
                Check(File.Exists(node), "external-node-present");
                Check(!File.Exists(data) && !Directory.Exists(data), "new-empty-data-root");
                Check(!File.Exists(Path.Combine(app, "thor-capture-settings.txt")), "no-application-settings");
                string automatic = CocoonBackend.DefaultBackendDir();
                Check(automatic != null && String.Equals(Path.GetFullPath(automatic), expected, StringComparison.OrdinalIgnoreCase), "default-backend-is-beside-exe-cocoon");
                foreach (string key in new string[] { "NODE_OPTIONS", "NODE_PATH", "NODE_REPL_HISTORY", "COCOON_ADB", "COCOON_DEVICE_BROKER", "COCOON_TEST_CONFIGURATION", "THOR_FFMPEG" }) Environment.SetEnvironmentVariable(key, null);
                backend = new CocoonBackend(null) { AutoRestart = false, NodePath = node, BackendDir = automatic, DataRoot = data };
                backend.Start(); Stopwatch readyDeadline = Stopwatch.StartNew();
                while (backend.Endpoint == null && backend.State == BackendState.Starting && readyDeadline.Elapsed.TotalSeconds < 30) Thread.Sleep(20);
                Check(backend.State == BackendState.Ready && backend.Endpoint != null, "production-start-to-ready");
                Check(backend.OwnedProcessId.HasValue, "owned-node-pid-recorded"); observed = Process.GetProcessById(backend.OwnedProcessId.Value);
                DateTime started = observed.StartTime; Check(observed.Handle != IntPtr.Zero && !observed.HasExited && started >= Process.GetCurrentProcess().StartTime, "owned-node-running");
                Uri endpoint = backend.Endpoint;
                HttpWebRequest unauthenticated = (HttpWebRequest)WebRequest.Create(new Uri(endpoint, "/api/games")); unauthenticated.Proxy = null; unauthenticated.Timeout = 5000; unauthenticated.AllowAutoRedirect = false;
                bool denied = false;
                try { using (HttpWebResponse response = (HttpWebResponse)unauthenticated.GetResponse()) { } }
                catch (WebException failure) { using (HttpWebResponse response = failure.Response as HttpWebResponse) denied = response != null && response.StatusCode == HttpStatusCode.Unauthorized; }
                Check(denied, "unauthenticated-games-denied");
                object games = backend.GetJson("/api/games", false); IList list = games as IList;
                Check(list != null && list.Count == 0, "authenticated-games-empty");
                object identity = backend.GetJson("/api/backend/library/identity", false);
                Check(Json.Text(identity, "contract") == "cocoon.library-identity/1" && Json.Text(identity, "provider") == "cocoon" && Json.Get(identity, "libraryId") == null, "persistent-identity-not-imported");
                object info = backend.GetJson("/api/backend/info", false);
                Check(Json.Text(info, "contract") == CocoonBackend.Contract && !Json.Flag(Json.Get(info, "capabilities"), "deviceAccessEnabled") && !Json.Flag(Json.Get(info, "capabilities"), "legacyApplyEnabled"), "managed-device-and-legacy-disabled");
                object diagnostics = backend.GetJson("/api/backend/diagnostics", false);
                Check(Json.Text(diagnostics, "contract") == "cocoon.diagnostics/1" && !ContainsText(diagnostics, data) && !ContainsText(diagnostics, app), "authenticated-diagnostics-redacted");
                bool nativeRequired = false;
                try { backend.GetJson("/api/native-editor", false); }
                catch (BackendRequestException failure) { nativeRequired = failure.Status == 409 && failure.Code == "EDITOR_LIBRARY_REQUIRED"; }
                Check(nativeRequired, "native-editor-empty-library-typed");
                Check(backend.GetJson("/api/backend/library", false) != null, "authenticated-library-state");
                Check(backend.StopAsync(TimeSpan.FromSeconds(15), CancellationToken.None).GetAwaiter().GetResult(), "production-stopasync-completes");
                Check(observed.WaitForExit(5000) && observed.ExitCode == 0, "owned-node-exits-zero-same-process");
                Check(backend.State == BackendState.Stopped && backend.OwnedProcessId == null && backend.Endpoint == null, "stopped-clears-owned-session");
                backend.Dispose(); backend = null;
                Console.WriteLine("PORTABLE_CSHARP_PROBE_PASS"); return 0;
            }
            catch (Exception failure)
            {
                string code = failure is InvalidOperationException ? failure.Message : failure is BackendRequestException ? ((BackendRequestException)failure).Code : "PORTABLE_PROBE_FAILED";
                if (!System.Text.RegularExpressions.Regex.IsMatch(code ?? "", "\\A[A-Za-z0-9_-]{1,96}\\z")) code = "PORTABLE_PROBE_FAILED";
                Console.Error.WriteLine(code); return 1;
            }
            finally
            {
                if (backend != null) { try { backend.StopAsync(TimeSpan.FromSeconds(15), CancellationToken.None).GetAwaiter().GetResult(); backend.Dispose(); } catch { } }
                if (observed != null) observed.Dispose();
            }
        }
    }
}
