// Reading the game library straight from the handheld (cocoon.device-read/1). The runner owns
// the device broker for this window, binds it to our backend child and follows one read job
// until it yields an import plan for review. Reads only: nothing is written to the device, and
// the plan is not applied until the user confirms it in the import dialog.

using System;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture
{
    internal static class PullText
    {
        public static string Error(string code)
        {
            switch (code ?? "")
            {
                case "DEVICE_READ_DENIED": return "掌机拒绝读取 Cocoon 的游戏库文件。普通调试权限读不到应用的私有数据，这里没有尝试提权。";
                case "DEVICE_ROOT_DENIED": return "用 root 也没有读到。请确认掌机已 root，并在掌机上允许 Shell 获取超级用户权限后重试。";                case "DEVICE_DISCONNECTED": case "DEVICE_TARGET_INVALID": return "掌机已断开或重新连接，请确认连接后重试。";
                case "DEVICE_CANCELLED": return "已取消读取。";
                case "DEVICE_READ_TIMEOUT": case "DEVICE_QUEUE_TIMEOUT": return "读取超时。请确认掌机没有休眠，稍后重试。";
                case "DEVICE_ADB_UNAVAILABLE": return "找不到 adb，无法连接掌机。";
                case "DEVICE_FILE_TOO_LARGE": return "掌机上的游戏库超过 64 MB 的读取上限。";
                case "DEVICE_SOURCE_CHANGED": return "读取期间掌机上的游戏库发生了变化。请先在掌机上停止操作 Cocoon，再重试。";
                case "DEVICE_PULL_PENDING_CHANGES": return "本机还有暂存的修改。请先在「暂存的修改」中处理或撤销，再从掌机读取。";
                case "DEVICE_PULL_BUSY": case "LIBRARY_IMPORT_BUSY": return "另一个读取或导入正在进行，请稍后再试。";
                case "DEVICE_CLEANUP_REQUIRED": return "上一次读取还没有完全结束，请稍后重试。";
                case "DEVICE_BROKER_REQUIRED": case "DEVICE_BROKER_INVALID": return "游戏库服务没有接受设备通道，请重试；仍然失败时重新打开游戏库。";
                case "DEVICE_READ_UNSUPPORTED": return "这个连接不支持读取游戏库。";
                case "DEVICE_BUSY": return "正在截图或录屏，结束后再读取。";
                case "DEVICE_NOT_CONNECTED": return "没有连接掌机。请用 USB 连接并在掌机上允许调试。";
                case "IMPORT_LIBRARY_INVALID": case "IMPORT_SOURCE_INVALID": return "读到的文件不是有效的 Cocoon 游戏库。本机的游戏库没有被替换。";
                case "BACKEND_STOPPING": case "BACKEND_NOT_READY": return "游戏库服务未就绪，请稍后重试。";
                default: return "读取没有完成（" + (string.IsNullOrEmpty(code) ? "未知原因" : code) + "）。本机的游戏库没有被替换。";
            }
        }
    }

    internal sealed class DevicePullRunner : IDisposable
    {
        private readonly CocoonBackend backend;
        private readonly SynchronizationContext ui;
        private readonly DevicePullClient client;
        private DeviceSessionRegistry sessions;
        private OperationGate operations;
        private DeviceCommandBroker broker;
        private CancellationTokenSource stop = new CancellationTokenSource();
        private bool disposed;
        private volatile bool cancelRequested;
        // Tests supply a reader over a fixture file; the app reads through adb.
        internal IBrokerFileReader Reader;
        public bool Running { get; private set; }
        public string Phase { get; private set; } // binding / reading / checking while running
        public string Error { get; private set; }
        public bool UsedRoot { get; private set; } // whether the last read was allowed to fall back to su
        public LibraryImportPlan Plan { get; private set; }
        public event EventHandler Changed;

        public DevicePullRunner(CocoonBackend owner, SynchronizationContext context)
        {
            backend = owner;
            ui = context;
            client = new DevicePullClient(owner);
        }

        private void Raise()
        {
            if (ui == null || SynchronizationContext.Current == ui) { if (!disposed && Changed != null) Changed(this, EventArgs.Empty); }
            else ui.Post(delegate { if (!disposed && Changed != null) Changed(this, EventArgs.Empty); }, null);
        }

        // One read of the catalog on `serial`, ending with Plan (for review) or Error. Never throws.
        public Task Start(string serial) { return Start(serial, false); }

        // root: also try the same read through su when the plain one is refused. Only ever passed
        // as true after the user chose it in the dialog.
        public async Task Start(string serial, bool root)
        {
            if (Running || disposed) return;
            Running = true; cancelRequested = false; Error = null; Plan = null; Phase = "binding";
            Raise();
            string code = null;
            DevicePullJob job = null;
            try
            {
                CancellationToken token = stop.Token;
                if (broker == null)
                {
                    sessions = new DeviceSessionRegistry();
                    operations = new OperationGate(sessions);
                    if (Reader == null) Reader = new BrokerFileReader(Adb.ExePath);
                    broker = new DeviceCommandBroker(sessions, operations, Reader);
                }
                BrokerFileReader adbReader = Reader as BrokerFileReader;
                if (adbReader != null) adbReader.AllowRoot = root;
                UsedRoot = root;
                if (!await backend.BindDeviceBrokerAsync(broker.Address).ConfigureAwait(false)) code = "DEVICE_BROKER_REQUIRED";
                else
                {
                    // A fresh session per read: an earlier one for this serial is retired, so a late answer cannot be reused.
                    DeviceSession session = sessions.Open(serial, DeviceCapabilities.CocoonRead);
                    job = await client.StartPlanAsync(session, token).ConfigureAwait(false);
                    while (job.State == "reading" || job.State == "checking")
                    {
                        if (cancelRequested) { cancelRequested = false; job = await client.CancelAsync(job.JobId).ConfigureAwait(false); continue; }
                        if (Phase != job.State) { Phase = job.State; Raise(); }
                        await Task.Delay(300, token).ConfigureAwait(false);
                        job = await client.StatusAsync(job.JobId, token).ConfigureAwait(false);
                    }
                    if (job.State == "planned") Plan = job.Plan;
                    else code = job.State == "cancelled" ? "DEVICE_CANCELLED" : job.Code ?? "DEVICE_PULL_FAILED";
                }
            }
            catch (BackendRequestException failure) { code = failure.Code; }
            catch (OperationCanceledException) { code = "DEVICE_CANCELLED"; }
            catch (ArgumentException) { code = "DEVICE_TARGET_INVALID"; }
            catch (Exception) { code = "DEVICE_PULL_FAILED"; }
            Error = code; Phase = null; Running = false;
            Raise();
        }

        // Asks the backend to abandon the read; Start() then finishes with DEVICE_CANCELLED.
        public void Cancel() { if (Running) cancelRequested = true; }

        // Stops the broker and waits for its reader; false means cleanup is still owed.
        public async Task<bool> StopAsync()
        {
            DeviceCommandBroker owned = broker;
            stop.Cancel();
            if (owned == null) return true;
            bool clean = await owned.StopAsync().ConfigureAwait(false);
            if (clean) { operations.Dispose(); sessions.Dispose(); }
            return clean;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Task pending = StopAsync();
            GC.KeepAlive(pending);
        }
    }
}
