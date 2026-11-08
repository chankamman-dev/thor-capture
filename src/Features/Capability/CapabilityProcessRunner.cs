using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Features.Capability
{
    // Owns only the short-lived child it starts, never a server or unrelated process.
    internal sealed class CapabilityProcessRunner : ICapabilityRunner
    {
        private readonly string adb, ffmpeg;
        private const int OutputLimit = 262144;
        public CapabilityProcessRunner(string adbPath, string ffmpegPath) { adb = adbPath; ffmpeg = ffmpegPath; }
        internal static string Arguments(string serial, ProbeCommand command)
        {
            switch (command)
            {
                case ProbeCommand.AdbVersion: return "version";
                case ProbeCommand.FfmpegVersion: return "-version";
                case ProbeCommand.FfmpegDecoders: return "-hide_banner -decoders";
                case ProbeCommand.FfmpegEncoders: return "-hide_banner -encoders";
            }
            if (string.IsNullOrEmpty(serial) || !Regex.IsMatch(serial, @"\A[A-Za-z0-9_.:\-\[\]]{1,256}\z")) throw new ArgumentException("Invalid device identity", "serial");
            string prefix = "-s \"" + serial + "\" shell ";
            switch (command)
            {
                case ProbeCommand.Properties: return prefix + "getprop";
                case ProbeCommand.Displays: return prefix + "dumpsys display";
                case ProbeCommand.ScreenCaptureHelp: return prefix + "screencap --help";
                case ProbeCommand.ScreenRecordHelp: return prefix + "screenrecord --help";
                case ProbeCommand.InputNodes: return prefix + "getevent -lp";
                case ProbeCommand.DeviceCodecs: return prefix + "dumpsys media.codec";
                default: throw new ArgumentOutOfRangeException("command");
            }
        }
        public Task<ProbeReply> RunAsync(string serial, ProbeCommand command, TimeSpan timeout, CancellationToken token)
        {
            string args = Arguments(serial, command);
            if (timeout.TotalMilliseconds < 1 || timeout.TotalMilliseconds > 60000) throw new ArgumentOutOfRangeException("timeout");
            string executable = command >= ProbeCommand.FfmpegVersion ? ffmpeg : adb;
            return Task.Run(() => Run(executable, args, command, timeout, token));
        }
        private static ProbeReply Run(string executable, string arguments, ProbeCommand command, TimeSpan timeout, CancellationToken token)
        {
            Stopwatch watch = Stopwatch.StartNew();
            ProbeReply reply = new ProbeReply { Outcome = ProbeOutcome.Failed, ExitCode = -1 };
            if (token.IsCancellationRequested) { reply.Outcome = ProbeOutcome.Cancelled; return Finish(reply, command, watch); }
            if (string.IsNullOrWhiteSpace(executable)) { reply.Outcome = ProbeOutcome.NotFound; return Finish(reply, command, watch); }
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                    process.Start();
                    BoundedText output = new BoundedText(), error = new BoundedText();
                    Task stdout = Task.Run(() => output.Drain(process.StandardOutput));
                    Task stderr = Task.Run(() => error.Drain(process.StandardError));
                    bool aborted = false;
                    while (!process.WaitForExit(25))
                    {
                        if (token.IsCancellationRequested || watch.Elapsed >= timeout)
                        {
                            reply.Outcome = token.IsCancellationRequested ? ProbeOutcome.Cancelled : ProbeOutcome.TimedOut;
                            aborted = true;
                            try { process.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                            process.WaitForExit(2000);
                            break;
                        }
                    }
                    bool drained = Task.WaitAll(new[] { stdout, stderr }, 2000);
                    reply.Output = output.Text;
                    reply.Error = error.Text;
                    reply.Truncated = output.Truncated || error.Truncated || !drained;
                    if (!aborted)
                    {
                        reply.ExitCode = process.ExitCode;
                        reply.Outcome = token.IsCancellationRequested ? ProbeOutcome.Cancelled : reply.ExitCode == 0 ? ProbeOutcome.Success : ProbeOutcome.Failed;
                        if (Regex.IsMatch(reply.Error, "device .*not found|device offline|device unauthorized|no devices/emulators found", RegexOptions.IgnoreCase)) reply.Outcome = ProbeOutcome.Disconnected;
                    }
                }
            }
            catch (Win32Exception failure) { reply.Outcome = failure.NativeErrorCode == 2 || failure.NativeErrorCode == 3 ? ProbeOutcome.NotFound : ProbeOutcome.Failed; }
            catch { reply.Outcome = token.IsCancellationRequested ? ProbeOutcome.Cancelled : ProbeOutcome.Failed; }
            return Finish(reply, command, watch);
        }
        private static ProbeReply Finish(ProbeReply reply, ProbeCommand command, Stopwatch watch)
        {
            reply.Duration = watch.Elapsed;
            // No argv, output, exceptions, serials, pairing codes or executable paths enter logs.
            Log.Info("Capability command=" + command + " outcome=" + reply.Outcome + " exit=" + reply.ExitCode + " elapsedMs=" + watch.ElapsedMilliseconds);
            return reply;
        }
        private sealed class BoundedText
        {
            private readonly object gate = new object();
            private readonly StringBuilder text = new StringBuilder();
            private bool truncated;
            public string Text { get { lock (gate) return text.ToString(); } }
            public bool Truncated { get { lock (gate) return truncated; } }
            public void Drain(StreamReader reader)
            {
                char[] buffer = new char[2048];
                try
                {
                    int count;
                    while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        lock (gate)
                        {
                            int take = Math.Min(count, OutputLimit - text.Length);
                            text.Append(buffer, 0, take);
                            if (take < count) truncated = true;
                        }
                    }
                }
                catch (IOException) { lock (gate) truncated = true; }
                catch (ObjectDisposedException) { lock (gate) truncated = true; }
            }
        }
    }
}
