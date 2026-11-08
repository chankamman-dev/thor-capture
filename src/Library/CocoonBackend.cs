// Front-end side of the Cocoon backend contract (cocoon.backend/1): starts and owns one
// Node child running backend.cjs, reads its JSONL events, binds to the reported local
// endpoint, keeps the per-launch token in memory only, and shuts it down through stdin.
// It never attaches to, or stops, a Cocoon/Node process it did not start itself.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal enum BackendState { Stopped, Starting, Ready, Stopping, Failed }

    internal sealed class BackendLifecycleOptions
    {
        public readonly int StartTimeoutMs, RestartDelayMs, RestartWindowMs, RestartLimit;
        public BackendLifecycleOptions(int startTimeoutMs = 30000, int restartDelayMs = 500, int restartWindowMs = 60000, int restartLimit = 3)
        {
            if (startTimeoutMs < 50 || startTimeoutMs > 300000 || restartDelayMs < 20 || restartDelayMs > 60000
                || restartWindowMs < 100 || restartWindowMs > 3600000 || restartLimit < 0 || restartLimit > 10) throw new ArgumentOutOfRangeException("lifecycle");
            StartTimeoutMs = startTimeoutMs; RestartDelayMs = restartDelayMs; RestartWindowMs = restartWindowMs; RestartLimit = restartLimit;
        }
    }

    internal sealed class CocoonBackend : IDisposable
    {
        public const string Contract = "cocoon.backend/1";
        private const string EventPrefix = "COCOON_BACKEND_EVENT ";
        private readonly object gate = new object();
        private readonly SynchronizationContext ui;
        private Process process;
        private string token, instanceId;
        private Uri endpoint;
        private readonly List<string> log = new List<string>();
        private readonly Queue<long> crashes = new Queue<long>();
        private readonly Stopwatch lifecycleClock = Stopwatch.StartNew();
        private readonly BackendLifecycleOptions lifecycle;
        private string previousInstance, previousToken;
        private long launchRevision;
        private bool stopRequested;
        private int exitCallbacks, eventCallbacks;
        private Backend.DeviceBrokerAddress deviceBroker;
        public bool AutoRestart = true;

        public BackendState State { get; private set; }
        public string FailureCode { get; private set; }
        public string FailureDetail { get; private set; }
        public string Version { get; private set; }
        public bool LocalCatalog { get; private set; }
        public Uri Endpoint { get { lock (gate) return endpoint; } }
        public string InstanceId { get { lock (gate) return instanceId; } }
        public int? OwnedProcessId { get { lock (gate) { try { return process == null ? (int?)null : process.Id; } catch { return null; } } } }
        public event EventHandler StateChanged;

        // Where things live; filled from settings or sensible defaults by the caller.
        public string NodePath = "node", BackendDir, DataRoot;

        public CocoonBackend(SynchronizationContext context) : this(context, new BackendLifecycleOptions()) { }
        internal CocoonBackend(SynchronizationContext context, BackendLifecycleOptions options) { if (options == null) throw new ArgumentNullException("options"); lifecycle = options; ui = context; State = BackendState.Stopped; }

        public static string DefaultBackendDir()
        {
            string configured = Settings.Get("cocoon.backendDir", "");
            if (configured.Length > 0) return configured;
            foreach (string candidate in new string[] {
                Path.Combine(Adb.AppDir(), "cocoon"),
                Path.GetFullPath(Path.Combine(Adb.AppDir(), Path.Combine("..", Path.Combine("..", "cocoon-art-backend")))) })
                if (File.Exists(Path.Combine(candidate, "backend.cjs"))) return candidate;
            return null;
        }

        public static string DefaultDataRoot()
        {
            string forced = Environment.GetEnvironmentVariable("THOR_COCOON_DATA_ROOT"); // tools and tests only
            if (!string.IsNullOrEmpty(forced)) return forced;
            string configured = Settings.Get("cocoon.dataRoot", "");
            return configured.Length > 0 ? configured : Path.Combine(Path.Combine(Adb.AppDir(), "data"), "cocoon");
        }

        private void Raise()
        {
            EventHandler handler = StateChanged;
            if (handler == null) return;
            SendOrPostCallback dispatch = delegate {
                foreach (EventHandler subscriber in handler.GetInvocationList())
                    try { subscriber(this, EventArgs.Empty); } catch { Note("observer failure"); }
            };
            if (ui != null) ui.Post(dispatch, null);
            else {
                lock (gate) eventCallbacks++;
                Task notification = Task.Run(delegate { try { dispatch(null); } finally { lock (gate) eventCallbacks--; } });
                GC.KeepAlive(notification);
            }
        }

        private bool restartPending;

        public void Start()
        {
            bool retry; long startingRevision;
            lock (gate)
            {
                if (State == BackendState.Starting || State == BackendState.Ready || State == BackendState.Stopping || exitCallbacks != 0) return;
                // A failed launch may still have its process running: stop it, start again on exit.
                retry = process != null;
                if (retry) restartPending = true;
                else {
                    State = BackendState.Starting; FailureCode = FailureDetail = null; token = null; endpoint = null; instanceId = null;
                    stopRequested = false; launchRevision++;
                }
                startingRevision = launchRevision;
            }
            if (retry) { RequestStop(true); return; }
            Raise();
            if (string.IsNullOrEmpty(BackendDir) || !File.Exists(Path.Combine(BackendDir, "backend.cjs"))) { FailStarting(startingRevision, "BACKEND_NOT_FOUND"); return; }
            if (string.IsNullOrEmpty(DataRoot) || !Path.IsPathRooted(DataRoot)) { FailStarting(startingRevision, "DATA_ROOT_INVALID"); return; }
            try { Directory.CreateDirectory(DataRoot); } catch (Exception) { FailStarting(startingRevision, "DATA_ROOT_INVALID"); return; }
            Dictionary<string, string> environment = new Dictionary<string, string> { { "COCOON_DATA_ROOT", DataRoot }, { "PORT", "0" }, { "COCOON_THUMBNAIL_FFMPEG", null } };
            try { environment["COCOON_THUMBNAIL_FFMPEG"] = Path.GetFullPath(VideoExport.Locate()); }
            catch (IOException) { } // PNG previews remain available without an external decoder.
            catch (UnauthorizedAccessException) { }
            ProcessStartInfo info = new Backend.ProcessRequest(NodePath, "cocoon.backend",
                new[] { Path.GetFullPath(Path.Combine(BackendDir, "backend.cjs")) },
                environment).StartInfo();
            info.WorkingDirectory = BackendDir; info.RedirectStandardInput = true;
            Process child = new Process { StartInfo = info, EnableRaisingEvents = true };
            child.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) OnLine(child, e.Data); };
            child.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) Note(e.Data); };
            child.Exited += delegate { OnExit(child); };
            try {
                lock (gate) {
                    if (stopRequested || startingRevision != launchRevision) { child.Dispose(); return; }
                    process = child;
                    child.Start();
                    child.BeginOutputReadLine(); child.BeginErrorReadLine();
                }
            }
            catch (System.ComponentModel.Win32Exception error) { lock (gate) if (process == child) process = null; child.Dispose(); FailStarting(startingRevision, error.NativeErrorCode == 2 || error.NativeErrorCode == 3 ? "NODE_NOT_FOUND" : "BACKEND_START_DENIED"); return; }
            long revision; lock (gate) revision = launchRevision;
            Task.Run(async delegate {
                await Task.Delay(lifecycle.StartTimeoutMs).ConfigureAwait(false);
                lock (gate) {
                    if (revision != launchRevision || process != child || State != BackendState.Starting) return;
                    State = BackendState.Failed; FailureCode = "BACKEND_START_TIMEOUT"; FailureDetail = null;
                }
                Raise(); Stop();
            });
        }

        private void FailStarting(long revision, string code)
        {
            lock (gate) { if (launchRevision != revision || stopRequested || State != BackendState.Starting) return; State = BackendState.Failed; FailureCode = code; FailureDetail = null; }
            Raise();
        }

        private void Note(string line)
        {
            // Arbitrary child output may contain paths, providers or secrets. Keep only a fixed marker.
            lock (gate) { if (log.Count == 0 || log[log.Count - 1] != "BACKEND_OUTPUT_OMITTED") log.Add("BACKEND_OUTPUT_OMITTED"); if (log.Count > 200) log.RemoveAt(0); }
        }

        public string[] RecentLog { get { lock (gate) return log.ToArray(); } }

        private void OnLine(Process child, string line)
        {
            lock (gate) if (process != child) return;
            if (!line.StartsWith(EventPrefix, StringComparison.Ordinal)) { Note(line); return; }
            object message;
            try { message = Json.Parse(line.Substring(EventPrefix.Length)); } catch { Note(line); return; }
            if (Json.Text(message, "product") != "cocoon-art" || Json.Text(message, "contract") != Contract) return;
            string kind = Json.Text(message, "event");
            if (kind == "ready") Ready(child, message);
            else if (kind == "failed") {
                string code = Json.Text(message, "code");
                if (!Regex.IsMatch(code ?? "", @"\A[A-Z0-9_]{1,64}\z")) code = "BACKEND_START_FAILED";
                lock (gate) { if (process != child || stopRequested) return; State = BackendState.Failed; FailureCode = code; FailureDetail = null; }
                Raise();
            }
            else if (kind == "stopping") { lock (gate) if (process == child) State = BackendState.Stopping; Raise(); }
        }

        // Bind only to what this launch reported: a loopback endpoint and a fresh identity.
        private void Ready(Process child, object message)
        {
            lock (gate) if (process != child || State != BackendState.Starting || stopRequested) return;
            Uri address;
            string id = Json.Text(message, "instanceId");
            Guid identity;
            if (!Uri.TryCreate(Json.Text(message, "endpoint") ?? "", UriKind.Absolute, out address) || address.Host != "127.0.0.1" || address.Scheme != "http"
                || address.UserInfo.Length > 0 || address.Query.Length > 0 || address.Fragment.Length > 0 || address.AbsolutePath != "/" || !Guid.TryParse(id, out identity) || identity == Guid.Empty)
            { FailOwned(child, "ENDPOINT_REJECTED"); return; }
            bool repeated; lock (gate) repeated = previousInstance != null && identity == Guid.Parse(previousInstance);
            if (repeated) { FailOwned(child, "INSTANCE_REUSED"); return; }
            object capabilities = Json.Get(message, "capabilities");
            Task.Run(delegate
            {
                // The token comes only from this launch's entry page and stays in memory.
                string page;
                try { page = Download(address, "/", null, 8000); }
                catch (Exception) { FailOwned(child, "TOKEN_UNAVAILABLE"); return; }
                Match meta = Regex.Match(page ?? "", "<meta\\s+name=\"cocoon-art-token\"\\s+content=\"([^\"]+)\"");
                if (!meta.Success || !Regex.IsMatch(meta.Groups[1].Value, @"\A[A-Za-z0-9_-]{16,256}\z")) { FailOwned(child, "TOKEN_UNAVAILABLE"); return; }
                lock (gate)
                {
                    if (process != child || State != BackendState.Starting || stopRequested) return;
                    if (meta.Groups[1].Value == previousToken) { State = BackendState.Failed; FailureCode = "TOKEN_REUSED"; FailureDetail = null; }
                    else {
                    endpoint = address; instanceId = id; token = meta.Groups[1].Value;
                    previousInstance = id; previousToken = token;
                    Version = Json.Text(message, "version");
                    LocalCatalog = Json.Flag(capabilities, "localCatalog");
                    State = BackendState.Ready;
                    }
                }
                Raise();
                Backend.DeviceBrokerAddress broker; lock (gate) broker = deviceBroker;
                if (broker != null) { try { BindDeviceBrokerAsync(broker).GetAwaiter().GetResult(); } catch (BackendRequestException) { } }
            });
        }

        private void FailOwned(Process child, string code)
        {
            lock (gate) { if (process != child || State != BackendState.Starting || stopRequested) return; State = BackendState.Failed; FailureCode = code; FailureDetail = null; }
            Raise();
        }

        private void OnExit(Process child)
        {
            bool raise = false, restart, recover = false; long revision;
            lock (gate)
            {
                if (process != child) return;
                exitCallbacks++;
                process = null; token = null; endpoint = null;
                restart = restartPending; restartPending = false;
                revision = launchRevision;
                if (!stopRequested && State == BackendState.Ready)
                {
                    State = BackendState.Failed; FailureCode = "BACKEND_CRASHED"; raise = true;
                    long now = lifecycleClock.ElapsedMilliseconds;
                    while (crashes.Count > 0 && now - crashes.Peek() > lifecycle.RestartWindowMs) crashes.Dequeue();
                    if (AutoRestart && crashes.Count < lifecycle.RestartLimit) { crashes.Enqueue(now); recover = true; }
                    else if (AutoRestart) FailureCode = "BACKEND_RESTART_LIMIT";
                }
                else if (State == BackendState.Starting) { State = BackendState.Failed; FailureCode = FailureCode ?? "BACKEND_EXITED"; raise = true; }
                else if (State != BackendState.Failed || restart) { State = BackendState.Stopped; raise = true; }
            }
            try { child.Dispose(); if (raise) Raise(); }
            finally { lock (gate) exitCallbacks--; }
            if (restart) { lock (gate) { if (revision == launchRevision && process == null && stopRequested) Start(); } }
            else if (recover) Task.Run(async delegate {
                await Task.Delay(lifecycle.RestartDelayMs).ConfigureAwait(false);
                lock (gate) { if (stopRequested || process != null || revision != launchRevision) return; Start(); }
            });
        }

        private static string Download(Uri root, string path, string tokenValue, int timeout)
        {
            byte[] bytes = Fetch(root, path, tokenValue, timeout);
            return bytes == null ? null : Encoding.UTF8.GetString(bytes);
        }

        // GET with the session token; 404 returns null. Never follows redirects off the endpoint.
        private static byte[] Fetch(Uri root, string path, string tokenValue, int timeout)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(new Uri(root, path));
            request.Method = "GET";
            request.Timeout = request.ReadWriteTimeout = timeout;
            request.AllowAutoRedirect = false;
            request.Proxy = null;
            if (tokenValue != null) request.Headers["X-Cocoon-Art-Token"] = tokenValue;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (MemoryStream copy = new MemoryStream())
                {
                    if ((int)response.StatusCode >= 300) return null;
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        copy.Write(buffer, 0, read);
                        if (copy.Length > 64L * 1024 * 1024) throw new IOException("响应过大");
                    }
                    return copy.ToArray();
                }
            }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) { response.Close(); return null; }
                if (response != null) response.Close();
                throw;
            }
        }

        private void Snapshot(out Uri root, out string tokenValue)
        {
            lock (gate)
            {
                if (State != BackendState.Ready || endpoint == null) throw new BackendRequestException("BACKEND_NOT_READY", 0);
                root = endpoint; tokenValue = token;
            }
        }

        // Worker-thread helpers for the library page.
        internal NativeEditorHttpTransport CaptureNativeEditorTransport()
        {
            lock (gate)
            {
                if (State != BackendState.Ready || endpoint == null) throw new BackendRequestException("BACKEND_NOT_READY", 0);
                Uri root = endpoint; string id = instanceId;
                return new NativeEditorHttpTransport(root, token, delegate {
                    lock (gate) return State == BackendState.Ready && endpoint == root && instanceId == id;
                });
            }
        }
        public object GetJson(string path, bool allowMissing = true)
        { return GetJson(path, CancellationToken.None, allowMissing); }
        public object GetJson(string path, CancellationToken cancellation, bool allowMissing = true)
        {
            cancellation.ThrowIfCancellationRequested();
            Uri root; string tokenValue; Snapshot(out root, out tokenValue);
            Uri url = OwnedApi(root, path);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET"; request.Timeout = request.ReadWriteTimeout = 15000;
            request.AllowAutoRedirect = false; request.Proxy = null;
            request.Headers["X-Cocoon-Art-Token"] = tokenValue;
            using (CancellationTokenRegistration registration = cancellation.Register(() => request.Abort()))
            {
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) return ReadJsonResponse(response);
            }
            catch (WebException failure)
            {
                if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                HttpWebResponse missing = failure.Response as HttpWebResponse;
                if (allowMissing && missing != null && missing.StatusCode == HttpStatusCode.NotFound) { missing.Close(); return null; }
                throw RequestFailure(failure);
            }
            }
        }

        private static Uri OwnedApi(Uri root, string path)
        {
            Uri url;
            if (path == null || !path.StartsWith("/api/", StringComparison.Ordinal) || !Uri.TryCreate(root, path, out url)
                || url.Scheme != root.Scheme || url.Host != root.Host || url.Port != root.Port || !url.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
                throw new BackendRequestException("ENDPOINT_REJECTED", 0);
            return url;
        }

        // Call on a worker thread. Only the captured owned loopback session receives the token.
        public object PostJson(string path, string json, int timeout = 15000)
        { return PostJson(path, json, timeout, CancellationToken.None); }
        public object PostJson(string path, string json, int timeout, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Uri root; string tokenValue; Snapshot(out root, out tokenValue);
            Uri url = OwnedApi(root, path);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST"; request.Timeout = request.ReadWriteTimeout = timeout;
            request.AllowAutoRedirect = false; request.Proxy = null; request.ContentType = "application/json; charset=utf-8";
            request.Headers["X-Cocoon-Art-Token"] = tokenValue;
            request.Headers["Origin"] = root.GetLeftPart(UriPartial.Authority);
            byte[] bytes = Encoding.UTF8.GetBytes(json); request.ContentLength = bytes.Length;
            using (CancellationTokenRegistration registration = cancellation.Register(() => request.Abort()))
            {
            try
            {
                using (Stream output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    return ReadJsonResponse(response);
            }
            catch (WebException failure)
            {
                if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                throw RequestFailure(failure);
            }
            }
        }

        internal static BackendRequestException RequestFailure(WebException failure)
        {
            using (HttpWebResponse response = failure.Response as HttpWebResponse)
            {
                string code = failure.Status == WebExceptionStatus.Timeout ? "BACKEND_TIMEOUT" : "BACKEND_REQUEST_FAILED";
                if (response != null)
                {
                    try
                    {
                        object error = ReadJsonResponse(response);
                        string received = Json.Text(error, "code") ?? Json.Text(Json.Get(error, "error"), "code");
                        if (Regex.IsMatch(received ?? "", @"\A[A-Z0-9_]{1,64}\z")) code = received;
                    }
                    catch (Exception) { } // Never echo raw error bodies, paths or credentials.
                }
                return new BackendRequestException(code, response == null ? 0 : (int)response.StatusCode);
            }
        }

        private static object ReadJsonResponse(HttpWebResponse response)
        {
            using (Stream input = response.GetResponseStream())
            using (MemoryStream copy = new MemoryStream())
            {
                byte[] buffer = new byte[81920]; int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    copy.Write(buffer, 0, read);
                    if (copy.Length > 32L * 1024 * 1024) throw new BackendRequestException("BACKEND_RESPONSE_TOO_LARGE", (int)response.StatusCode);
                }
                try { return Json.Parse(Encoding.UTF8.GetString(copy.ToArray())); }
                catch (Exception) { throw new BackendRequestException("BACKEND_RESPONSE_INVALID", (int)response.StatusCode); }
            }
        }

        public byte[] GetBytes(string path)
        {
            Uri root; string tokenValue; Snapshot(out root, out tokenValue);
            OwnedApi(root, path);
            return Fetch(root, path, tokenValue, 15000);
        }

        internal ArtworkThumbnail GetThumbnail(string path, string etag, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return CaptureNativeEditorTransport().ThumbnailAsync(path, etag, cancellation).GetAwaiter().GetResult();
        }

        // Asks our own child to stop; stdin close is the fallback signal. Does not wait.
        public void Stop() { RequestStop(false); }
        private void RequestStop(bool restart)
        {
            Process child; string id;
            lock (gate)
            {
                child = process; id = instanceId;
                stopRequested = true;
                if (!restart) launchRevision++;
                restartPending = restart; token = null; endpoint = null;
                if (child == null) { State = BackendState.Stopped; return; }
                if (State != BackendState.Failed) State = BackendState.Stopping;
            }
            Raise();
            Task.Run(delegate {
                try { lock (child) {
                    if (id != null) Send(child, "{\"contract\":\"" + Contract + "\",\"instanceId\":" + Json.Quote(id) + ",\"command\":\"shutdown\"}");
                    child.StandardInput.BaseStream.Close();
                } } catch (Exception) { } // already exiting
            });
        }

        // One command line to our child, as raw UTF-8. Process.StandardInput takes the console (or system)
        // code page, and under UTF-8 the framework writes a BOM into the pipe as soon as the child starts.
        // Glued to the first command it makes that line unparseable and the command is dropped, so every
        // command begins on a fresh line: the child ignores the blank or BOM-only line before it.
        private static void Send(Process child, string line)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes("\n" + line + "\n");
            Stream input = child.StandardInput.BaseStream;
            input.Write(bytes, 0, bytes.Length);
            input.Flush();
        }

        public async Task<bool> StopAsync(TimeSpan timeout, CancellationToken cancellation)
        {
            if (timeout.TotalMilliseconds < 1 || timeout.TotalMinutes > 5) throw new ArgumentOutOfRangeException("timeout");
            Stop(); Stopwatch deadline = Stopwatch.StartNew();
            for (;;) {
                lock (gate) if (process == null && exitCallbacks == 0 && eventCallbacks == 0) return true;
                if (cancellation.IsCancellationRequested || deadline.Elapsed >= timeout) {
                    lock (gate) { if (process == null && exitCallbacks == 0 && eventCallbacks == 0) return true; State = BackendState.Failed; FailureCode = cancellation.IsCancellationRequested ? "BACKEND_STOP_CANCELLED" : "BACKEND_STOP_TIMEOUT"; FailureDetail = null; }
                    Raise(); return false;
                }
                await Task.Delay(20).ConfigureAwait(false);
            }
        }

        // Parent-only stdin conveys this capability; the token never enters URLs, environment or logs.
        public Task<bool> BindDeviceBrokerAsync(Backend.DeviceBrokerAddress broker)
        {
            if (broker == null) throw new ArgumentNullException("broker");
            lock (gate) deviceBroker = broker;
            return Task.Run(delegate {
                Process child; string id;
                lock (gate) { if (State != BackendState.Ready || process == null) throw new BackendRequestException("BACKEND_NOT_READY", 0); child = process; id = instanceId; }
                string message = "{\"contract\":\"" + Contract + "\",\"instanceId\":" + Json.Quote(id) + ",\"command\":\"bind-device-broker\",\"broker\":{\"contract\":\""
                    + Backend.DeviceBrokerAddress.Contract + "\",\"host\":\"127.0.0.1\",\"port\":" + broker.Port + ",\"instanceId\":" + Json.Quote(broker.InstanceId) + ",\"token\":" + Json.Quote(broker.Token) + "}}";
                try { lock (child) Send(child, message); }
                catch (Exception) { throw new BackendRequestException("BACKEND_NOT_READY", 0); }
                for (int attempt = 0; attempt < 20; attempt++) { if (Json.Flag(GetJson("/api/backend/device/read", false), "brokerConfigured")) return true; Thread.Sleep(25); }
                return false;
            });
        }

        public bool Owns(int processId) { lock (gate) { try { return process != null && process.Id == processId; } catch { return false; } } }

        public void Dispose() { Stop(); }
    }

    internal sealed class BackendRequestException : Exception
    {
        public readonly string Code;
        public readonly int Status;
        public BackendRequestException(string code, int status) : base("Backend request failed: " + code) { Code = code; Status = status; }
    }
}
