using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Backend
{
    internal enum DiagnosticArea { TextProcess, BinaryProcess, Shutdown }
    internal enum DiagnosticCode { Started, Finished, CancelRequested, CleanupPending, Closed, Exported }
    internal enum DiagnosticState { Success, Failed, Cancelled, TimedOut, Pending, NotFound, StartDenied, LimitExceeded, SinkFailed }
    internal sealed class DiagnosticEntry
    {
        public readonly long Sequence, ElapsedMilliseconds;
        public readonly DateTime Utc;
        public readonly Guid Id;
        public readonly DiagnosticArea Area;
        public readonly DiagnosticCode Code;
        public readonly DiagnosticState State;
        public readonly int ExitCode;
        public readonly bool CleanupComplete;
        internal DiagnosticEntry(long sequence, Guid id, DiagnosticArea area, DiagnosticCode code, DiagnosticState state, long elapsed, int exit, bool cleanup)
        { Sequence = sequence; Utc = DateTime.UtcNow; Id = id; Area = area; Code = code; State = state; ElapsedMilliseconds = elapsed; ExitCode = exit; CleanupComplete = cleanup; }
    }
    internal enum DiagnosticExportOutcome { Success, Cancelled, AlreadyExists, FileFailed }
    // This schema deliberately has NO free-text field: callers cannot submit argv, paths or logs.
    internal sealed class Diagnostics
    {
        public static readonly Diagnostics Shared = new Diagnostics(512);
        private readonly object gate = new object();
        private readonly Queue<DiagnosticEntry> ring = new Queue<DiagnosticEntry>();
        private readonly int capacity;
        private long sequence;
        public Diagnostics(int maxEntries)
        { if (maxEntries < 1 || maxEntries > 4096) throw new ArgumentOutOfRangeException("maxEntries"); capacity = maxEntries; }
        public void Record(Guid id, DiagnosticArea area, DiagnosticCode code, DiagnosticState state, TimeSpan duration, int exitCode, bool cleanup)
        {
            if (!Enum.IsDefined(typeof(DiagnosticArea), area) || !Enum.IsDefined(typeof(DiagnosticCode), code) || !Enum.IsDefined(typeof(DiagnosticState), state)) throw new ArgumentException("Unknown diagnostic schema value");
            lock (gate)
            {
                while (ring.Count >= capacity) ring.Dequeue();
                ring.Enqueue(new DiagnosticEntry(++sequence, id, area, code, state, Math.Max(0, (long)duration.TotalMilliseconds), exitCode, cleanup));
            }
        }
        public DiagnosticEntry[] Snapshot(int maxEntries)
        {
            if (maxEntries < 1 || maxEntries > 4096) throw new ArgumentOutOfRangeException("maxEntries");
            lock (gate)
            {
                DiagnosticEntry[] all = ring.ToArray();
                int count = Math.Min(maxEntries, all.Length);
                DiagnosticEntry[] result = new DiagnosticEntry[count]; Array.Copy(all, all.Length - count, result, 0, count); return result;
            }
        }
        public Task<DiagnosticExportOutcome> ExportAsync(string path, int maxEntries, CancellationToken token)
        {
            DiagnosticEntry[] entries = Snapshot(maxEntries);
            return Task.Run(delegate
            {
                string temporary = null;
                try
                {
                    token.ThrowIfCancellationRequested();
                    string destination = Path.GetFullPath(path);
                    if (File.Exists(destination)) return DiagnosticExportOutcome.AlreadyExists;
                    temporary = Path.Combine(Path.GetDirectoryName(destination), ".thor-diagnostics-" + Guid.NewGuid().ToString("N") + ".tmp");
                    using (FileStream file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        using (StreamWriter writer = new StreamWriter(file, new UTF8Encoding(false), 4096, true))
                        {
                            writer.WriteLine("thor.diagnostics/1\tsequence\tutc\tid\tarea\tcode\tstate\telapsedMs\texit\tcleanup");
                            foreach (DiagnosticEntry entry in entries)
                            {
                                token.ThrowIfCancellationRequested();
                                writer.WriteLine("1\t" + entry.Sequence + "\t" + entry.Utc.ToString("o") + "\t" + entry.Id.ToString("N") + "\t" + entry.Area + "\t" + entry.Code + "\t" + entry.State + "\t" + entry.ElapsedMilliseconds + "\t" + entry.ExitCode + "\t" + entry.CleanupComplete);
                            }
                            writer.Flush();
                        }
                        file.Flush(true);
                    }
                    if (new FileInfo(temporary).Length > 1048576) throw new IOException("Diagnostic schema size exceeded");
                    token.ThrowIfCancellationRequested(); File.Move(temporary, destination); temporary = null;
                    return DiagnosticExportOutcome.Success;
                }
                catch (OperationCanceledException) { return DiagnosticExportOutcome.Cancelled; }
                catch (Exception error)
                {
                    if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is ArgumentException) && !(error is NotSupportedException)) throw;
                    return DiagnosticExportOutcome.FileFailed;
                }
                finally { if (temporary != null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            });
        }
    }
}
