using System;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Backend
{
    internal sealed class BrokerFileReader : IBrokerFileReader
    {
        private readonly BinaryProcessRunner runner = new BinaryProcessRunner();
        private readonly string executable;
        public BrokerFileReader(string adbPath) { executable = adbPath; }
        public async Task<BrokerFileResult> ReadAsync(DeviceSession session, string file, CancellationToken cancellation)
        {
            if (file != "catalog" && file != "catalog-wal") throw new ArgumentException("Unknown read scope");
            const string catalog = "/data/data/rip.moth.cocoonshell/databases/cocoon_db";
            string path = catalog + (file == "catalog-wal" ? "-wal" : "");
            string command = "cat '" + path + "'";
            if (file == "catalog-wal") command = "if [ -f '" + path + "' ]; then " + command + "; else exit 44; fi";
            // No device-side temporary copy, launcher stop, remote write or arbitrary path. The plain read
            // comes first; root is used only for the same fixed read, and only when the user allowed it.
            BrokerFileResult plain = await Run(session, file, command, cancellation).ConfigureAwait(false);
            if (plain.Code != "DEVICE_READ_DENIED" || !AllowRoot || !plain.CleanupComplete) return plain;
            return await Run(session, file, "su -c \"" + command + "\"", cancellation).ConfigureAwait(false);
        }

        // Set after the user explicitly chose a root read for this device's private catalog.
        public volatile bool AllowRoot;

        private async Task<BrokerFileResult> Run(DeviceSession session, string file, string command, CancellationToken cancellation)
        {
            BinaryProcessResult result = await runner.RunBytesAsync(new ProcessRequest(executable, "adb.catalog-read",
                new[] { "-s", session.Serial, "exec-out", command }), 64 * 1024 * 1024, TimeSpan.FromSeconds(15), cancellation).ConfigureAwait(false);
            string code = result.Outcome == BinaryProcessOutcome.Success ? "OK"
                : result.ExitCode == 44 && file == "catalog-wal" ? "DEVICE_FILE_MISSING"
                : result.Outcome == BinaryProcessOutcome.Cancelled ? "DEVICE_CANCELLED"
                : result.Outcome == BinaryProcessOutcome.TimedOut ? "DEVICE_READ_TIMEOUT"
                : result.Outcome == BinaryProcessOutcome.OutputLimit ? "DEVICE_FILE_TOO_LARGE"
                : result.Outcome == BinaryProcessOutcome.NotFound || result.Outcome == BinaryProcessOutcome.StartDenied ? "DEVICE_ADB_UNAVAILABLE"
                : "DEVICE_READ_DENIED";
            if (code == "OK") code = Classify(file, result.Output);
            return new BrokerFileResult { Code = code, Bytes = code == "OK" ? result.Output : new byte[0], CleanupComplete = result.CleanupComplete };
        }
        // exec-out reports exit 0 even when the remote command failed, and puts its error text
        // ("cat: ...: Permission denied") where the file should be. Only bytes that carry the
        // file's own signature count as a read; anything else is a refusal, not a catalog.
        internal static string Classify(string file, byte[] bytes)
        {
            if (bytes == null) return "DEVICE_READ_DENIED";
            if (file == "catalog-wal")
            {
                if (bytes.Length == 0) return "DEVICE_FILE_MISSING";
                return bytes.Length >= 32 && bytes[0] == 0x37 && bytes[1] == 0x7F && bytes[2] == 0x06 && (bytes[3] == 0x82 || bytes[3] == 0x83) ? "OK" : "DEVICE_READ_DENIED";
            }
            byte[] magic = System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");
            if (bytes.Length < 100) return "DEVICE_READ_DENIED";
            for (int i = 0; i < magic.Length; i++) if (bytes[i] != magic[i]) return "DEVICE_READ_DENIED";
            return "OK";
        }

        public Task<bool> StopAsync() { return runner.StopAllAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
    }
}
