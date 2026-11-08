using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ThorCapture.Backend
{
    // Windows named mutexes share the short-command queue with other Capture processes.
    // Node never owns this mutex: its device requests are executed by the owning broker.
    internal static class DeviceTransportGate
    {
        private static readonly object sync = new object();
        private static readonly HashSet<string> blocked = new HashSet<string>();
        private static readonly Dictionary<string, FileStream> retained = new Dictionary<string, FileStream>();
        internal static void Block(string serial) { lock (sync) blocked.Add(serial); }
        internal static void Unblock(string serial) { lock (sync) { FileStream reservation;
            if (retained.TryGetValue(serial, out reservation)) { reservation.Dispose(); retained.Remove(serial); } blocked.Remove(serial); } }
        private static void Check(string serial) { lock (sync) if (blocked.Contains(serial)) throw new InvalidOperationException("Device cleanup remains pending"); }
        internal static string SerialOf(string arguments)
        {
            Match match = Regex.Match(arguments ?? "", @"\A\s*-s\s+(?:""([A-Za-z0-9_.:\-\[\]]+)""|([A-Za-z0-9_.:\-\[\]]+))\s+");
            if (match.Success) {
                string serial = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value; DeviceSessionRegistry.ValidateSerial(serial);
                if (!Regex.IsMatch((arguments ?? "").Substring(match.Length), @"\A[a-z][a-z-]*(?:\s|$)")) throw new ArgumentException("Multiple device selectors are forbidden");
                return serial;
            }
            if (Regex.IsMatch(arguments ?? "", @"\A\s*(?:devices(?:\s+-l)?|version|help|hang|flood|infra-ping)\s*\z")) return null;
            throw new ArgumentException("Device operation requires an explicit serial");
        }
        internal static T Execute<T>(string serial, TimeSpan timeout, CancellationToken cancellation, Func<T> command)
        {
            if (serial == null) { cancellation.ThrowIfCancellationRequested(); return command(); }
            DeviceSessionRegistry.ValidateSerial(serial);
            Check(serial);
            string key;
            using (SHA256 hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(serial))).Replace("-", "");
            using (Mutex mutex = new Mutex(false, "Local\\ThorCapture.Device." + key))
            {
                bool acquired = false; FileStream reservation = null; Stopwatch queue = Stopwatch.StartNew();
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    try { acquired = WaitHandle.WaitAny(new WaitHandle[] { mutex, cancellation.WaitHandle }, timeout) == 0; }
                    catch (AbandonedMutexException) { acquired = true; }
                    cancellation.ThrowIfCancellationRequested();
                    if (!acquired) throw new TimeoutException("Device queue deadline");
                    Check(serial);
                    // File ownership survives a returned cleanup-pending result and is not thread-affine.
                    // Another Capture process must also reserve this file before emitting a device call.
                    string directory = Path.Combine(Path.GetTempPath(), "ThorCapture.DeviceLocks");
                    Directory.CreateDirectory(directory);
                    for (string current = directory; current != null; current = Path.GetDirectoryName(current))
                        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Device reservation path unsafe");
                    string file = Path.Combine(directory, key + ".lock");
                    if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Device reservation file unsafe");
                    for (;;) {
                        cancellation.ThrowIfCancellationRequested();
                        try { reservation = File.Open(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
                        catch (IOException) { if (queue.Elapsed >= timeout) throw new TimeoutException("Device reservation pending cleanup"); Thread.Sleep(10); }
                    }
                    return command();
                }
                finally {
                    if (reservation != null) {
                        lock (sync) { if (blocked.Contains(serial)) { if (!retained.ContainsKey(serial)) retained.Add(serial, reservation); else reservation.Dispose(); }
                            else reservation.Dispose(); }
                    }
                    if (acquired) mutex.ReleaseMutex();
                }
            }
        }
    }
}
