using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture.Features.Capture
{
    internal sealed class AdbScreenSource : IScreenCaptureSource
    {
        readonly string executable; readonly BinaryProcessRunner runner = new BinaryProcessRunner();
        readonly HashSet<string> pending = new HashSet<string>();
        public bool SupportsParallel { get { return false; } }
        public AdbScreenSource(string adbPath) { if (String.IsNullOrWhiteSpace(adbPath)) throw new ArgumentException("adbPath"); executable = adbPath; }
        public Task<ScreenBytes> ReadAsync(DeviceSession session, DisplayTarget display, TimeSpan timeout, CancellationToken token)
        {
            if (session == null || !Object.ReferenceEquals(session.Display(display.Role), display)) throw new ArgumentException("Display session mismatch");
            return Task.Run(() => {
                try {
                    return DeviceTransportGate.Execute(session.Serial, timeout, token, () => {
                        BinaryProcessResult result = runner.RunBytesAsync(new ProcessRequest(executable, "adb.capture", new[] { "-s", session.Serial, "exec-out", "screencap", "-p", "-d", display.PhysicalId }),
                            64 * 1024 * 1024, timeout, token).GetAwaiter().GetResult();
                        if (!result.CleanupComplete) { lock (pending) pending.Add(session.Serial); DeviceTransportGate.Block(session.Serial); }
                        return new ScreenBytes(result.Output, result.Outcome == BinaryProcessOutcome.Success ? "OK" : result.Outcome == BinaryProcessOutcome.TimedOut ? "CAPTURE_TIMEOUT" : result.Outcome == BinaryProcessOutcome.Cancelled ? "CAPTURE_CANCELLED" : "CAPTURE_READ_FAILED", result.CleanupComplete);
                    });
                } catch (OperationCanceledException) { return new ScreenBytes(null, "CAPTURE_CANCELLED", true); }
                catch (TimeoutException) { return new ScreenBytes(null, "CAPTURE_QUEUE_TIMEOUT", true); }
                catch (InvalidOperationException) { return new ScreenBytes(null, "CAPTURE_CLEANUP_REQUIRED", false); }
            });
        }
        public async Task<bool> StopAsync() {
            bool clean = await runner.StopAllAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            if (clean) lock (pending) { foreach (string serial in pending) DeviceTransportGate.Unblock(serial); pending.Clear(); }
            return clean;
        }
    }
}
