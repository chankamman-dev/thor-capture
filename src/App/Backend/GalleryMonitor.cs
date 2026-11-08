using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Backend
{
    internal enum GalleryOutcome { Success, Cancelled, Closed, MissingDirectory, FileFailure, TimedOut }
    internal sealed class GalleryItemSnapshot
    {
        public readonly string Path, Name, DateText, Package, MetadataStatus;
        public readonly DateTime Time, WriteUtc;
        public readonly long Size;
        public readonly bool IsVideo, Paired;
        public readonly int Screen;
        internal GalleryItemSnapshot(IndexEntry entry, bool paired)
        {
            Path = entry.Path; Name = entry.Name; DateText = entry.DateText; Time = entry.Time;
            WriteUtc = entry.WriteUtc; Size = entry.Size; IsVideo = entry.IsVideo; Paired = paired;
            Screen = entry.Screen; Package = entry.Metadata == null ? "" : entry.Metadata.Package;
            MetadataStatus = entry.Metadata == null ? "unknown" : entry.Metadata.Status;
        }
    }
    internal sealed class GallerySnapshot
    {
        public readonly long Generation, Revision;
        public readonly string Folder;
        public readonly GalleryOutcome Outcome;
        public readonly int PendingFiles;
        public readonly bool FullRescan;
        public readonly ReadOnlyCollection<GalleryItemSnapshot> Items;
        public readonly ReadOnlyCollection<string> ChangedPaths;
        internal GallerySnapshot(long generation, long revision, string folder, GalleryOutcome outcome,
            IEnumerable<GalleryItemSnapshot> items, int pending, bool full, IEnumerable<string> changed)
        {
            Generation = generation; Revision = revision; Folder = folder; Outcome = outcome;
            Items = new ReadOnlyCollection<GalleryItemSnapshot>(items.ToArray()); PendingFiles = pending;
            FullRescan = full; ChangedPaths = new ReadOnlyCollection<string>(changed.ToArray());
        }
    }
    internal sealed class GallerySwitchResult
    {
        public readonly GalleryOutcome Outcome;
        public readonly GallerySnapshot Snapshot;
        public GallerySwitchResult(GalleryOutcome outcome, GallerySnapshot snapshot) { Outcome = outcome; Snapshot = snapshot; }
    }

    // Every disk enumeration/read and snapshot event runs on a background thread. No media IDs are assigned here.
    internal sealed class GalleryMonitor : IDisposable
    {
        private sealed class Request
        {
            public long Generation; public string Folder;
            public TaskCompletionSource<GallerySwitchResult> Completion = new TaskCompletionSource<GallerySwitchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly CancellationTokenSource Cancellation;
            public readonly CancellationToken Token;
            public Request(CancellationToken lifetime) { Cancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime); Token=Cancellation.Token; }
            public void Cancel() { try { Cancellation.Cancel(); } catch(ObjectDisposedException) { } finally { Cancellation.Dispose(); } }
        }
        private sealed class Pending { public long Size; public DateTime Write; public int Attempts; }
        private readonly object gate = new object();
        private readonly GalleryIndex index = new GalleryIndex();
        private readonly SemaphoreSlim wake = new SemaphoreSlim(0, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly HashSet<string> changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IndexEntry> entries = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);
        private readonly Task worker;
        private readonly int debounceMilliseconds;
        private readonly int initializationTimeoutMilliseconds;
        private readonly int reconciliationMilliseconds;
        private Request request;
        private FileSystemWatcher watcher;
        private GallerySnapshot snapshot;
        private long generation, revision, workerGeneration;
        private bool fullRescan, closed;
        /// <summary>Raised on a background thread. Subscribers must marshal to their own UI context.</summary>
        public event EventHandler<GallerySnapshotEventArgs> Changed;
        public GalleryMonitor(int debounceMilliseconds = 150, int initializationTimeoutMilliseconds = 10000, int reconciliationMilliseconds = 30000)
        {
            if (debounceMilliseconds < 20 || debounceMilliseconds > 2000) throw new ArgumentOutOfRangeException("debounceMilliseconds");
            if(initializationTimeoutMilliseconds<100 || initializationTimeoutMilliseconds>60000) throw new ArgumentOutOfRangeException("initializationTimeoutMilliseconds");
            if(reconciliationMilliseconds<1000||reconciliationMilliseconds>120000) throw new ArgumentOutOfRangeException("reconciliationMilliseconds");
            this.debounceMilliseconds = debounceMilliseconds; this.initializationTimeoutMilliseconds=initializationTimeoutMilliseconds;
            this.reconciliationMilliseconds=reconciliationMilliseconds; worker = Task.Run((Func<Task>)RunAsync);
        }
        public GallerySnapshot Snapshot { get { lock (gate) return snapshot; } }
        public Task<GallerySwitchResult> SwitchAsync(string folder, CancellationToken token)
        {
            string normalized;
            try { normalized = System.IO.Path.GetFullPath(folder); }
            catch (Exception e) { if (!(e is ArgumentException || e is NotSupportedException || e is PathTooLongException)) throw;
                return Task.FromResult(new GallerySwitchResult(GalleryOutcome.FileFailure, null)); }
            Request next;
            lock (gate)
            {
                if (closed) return Task.FromResult(new GallerySwitchResult(GalleryOutcome.Closed, snapshot));
                if (token.IsCancellationRequested) return Task.FromResult(new GallerySwitchResult(GalleryOutcome.Cancelled, snapshot));
                if (request != null) { request.Completion.TrySetResult(new GallerySwitchResult(GalleryOutcome.Cancelled, null)); request.Cancel(); }
                next = new Request(lifetime.Token) { Folder = normalized, Generation = ++generation }; request = next;
                changed.Clear(); fullRescan = true;
            }
            return AwaitSwitch(next, token);
        }
        private async Task<GallerySwitchResult> AwaitSwitch(Request next, CancellationToken token)
        {
            using (token.Register(delegate {
                lock (gate) { if (ReferenceEquals(request, next)) { request = null; generation++; changed.Clear(); } }
                next.Completion.TrySetResult(new GallerySwitchResult(GalleryOutcome.Cancelled, null)); Signal();
                next.Cancel();
            }))
            {
                Signal(); Task timeout=Task.Delay(initializationTimeoutMilliseconds);
                if(await Task.WhenAny(next.Completion.Task,timeout).ConfigureAwait(false)!=next.Completion.Task)
                {
                    lock(gate) { if(request==next) { request=null; generation++; changed.Clear(); } }
                    next.Completion.TrySetResult(new GallerySwitchResult(GalleryOutcome.TimedOut,null)); next.Cancel(); Signal();
                }
                return await next.Completion.Task.ConfigureAwait(false);
            }
        }
        public void Refresh() { lock (gate) { if (closed || request == null) return; fullRescan = true; } Signal(); }
        // Same recovery path used by FileSystemWatcher.Error. Deterministic overflow injection needs no real overflowing directory.
        internal void NotifyOverflow() { Refresh(); }
        private void Signal() { try { wake.Release(); } catch (SemaphoreFullException) { } }
        private void OnChange(long id, string path)
        {
            lock (gate)
            {
                if (closed || request == null || request.Generation != id) return;
                if (changed.Count >= 4096) { fullRescan = true; changed.Clear(); }
                else changed.Add(path);
            }
            Signal();
        }
        private async Task RunAsync()
        {
            try
            {
                while (true)
                {
                    if(!await wake.WaitAsync(reconciliationMilliseconds,lifetime.Token).ConfigureAwait(false)) lock(gate) fullRescan=true;
                    await Task.Delay(debounceMilliseconds, lifetime.Token).ConfigureAwait(false);
                    Request current; bool full; string[] paths;
                    lock (gate) { current = request; full = fullRescan; fullRescan = false; paths = changed.ToArray(); changed.Clear(); }
                    if (current == null) { DisposeWatcher(); entries.Clear(); pending.Clear(); continue; }
                    try
                    {
                        if (workerGeneration != current.Generation || watcher == null)
                        {
                            DisposeWatcher(); entries.Clear(); pending.Clear(); workerGeneration = current.Generation;
                            if (!Directory.Exists(current.Folder))
                            {
                                try { if((File.GetAttributes(current.Folder)&FileAttributes.Directory)==0) { Publish(current,GalleryOutcome.FileFailure,true,paths); continue; } }
                                catch(DirectoryNotFoundException) { Publish(current,GalleryOutcome.MissingDirectory,true,paths); continue; }
                                catch(FileNotFoundException) { Publish(current,GalleryOutcome.MissingDirectory,true,paths); continue; }
                            }
                            FileSystemWatcher next = new FileSystemWatcher(current.Folder) {
                                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                                IncludeSubdirectories = false, InternalBufferSize = 16384
                            };
                            next.Changed += (s,e) => OnChange(current.Generation,e.FullPath);
                            next.Created += (s,e) => OnChange(current.Generation,e.FullPath);
                            next.Deleted += (s,e) => OnChange(current.Generation,e.FullPath);
                            next.Renamed += (s,e) => { OnChange(current.Generation,e.OldFullPath); OnChange(current.Generation,e.FullPath); };
                            next.Error += (s,e) => { lock (gate) { if (request != current) return; fullRescan = true; } Signal(); };
                            watcher = next; next.EnableRaisingEvents = true; full = true;
                        }
                        current.Token.ThrowIfCancellationRequested();
                        if (full)
                        {
                            string error; List<IndexEntry> candidates = index.Scan(current.Folder, out error,current.Token);
                            if (error != null) { Publish(current, GalleryOutcome.FileFailure, true, paths); continue; }
                            HashSet<string> seen = new HashSet<string>(candidates.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
                            foreach (string path in entries.Keys.Where(x => !seen.Contains(x)).ToArray()) entries.Remove(path);
                            foreach (string path in pending.Keys.Where(x => !seen.Contains(x)).ToArray()) pending.Remove(path);
                            foreach (IndexEntry item in candidates) { current.Token.ThrowIfCancellationRequested(); Inspect(item.Path); }
                        }
                        else foreach (string path in paths) { current.Token.ThrowIfCancellationRequested(); Inspect(MediaPath(path)); }
                        Publish(current, GalleryOutcome.Success, full, paths);
                        string[] retry = pending.Where(x => x.Value.Attempts < 40).Select(x => x.Key).ToArray();
                        if (retry.Length > 0)
                        {
                            lock (gate) { if (request == current) foreach (string path in retry) changed.Add(path); }
                            await Task.Delay(debounceMilliseconds, lifetime.Token).ConfigureAwait(false); Signal();
                        }
                    }
                    catch (OperationCanceledException) { if (lifetime.IsCancellationRequested) break; }
                    catch (Exception error)
                    {
                        if (!(error is IOException || error is UnauthorizedAccessException || error is ArgumentException)) Log.Warn("Gallery monitor scan failed");
                        Publish(current, GalleryOutcome.FileFailure, full, paths);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally { DisposeWatcher(); }
        }
        private static string MediaPath(string path)
        {
            foreach (string suffix in new string[] { ".thor-meta.txt", ".input.csv" })
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return path.Substring(0,path.Length-suffix.Length);
            return path;
        }
        private void Inspect(string path)
        {
            string extension = System.IO.Path.GetExtension(path);
            if (!extension.Equals(".png",StringComparison.OrdinalIgnoreCase) && !extension.Equals(".mp4",StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(path)) { entries.Remove(path); pending.Remove(path); return; }
            try
            {
                FileInfo file = new FileInfo(path); Pending prior;
                pending.TryGetValue(path,out prior);
                bool stable = prior != null && prior.Size == file.Length && prior.Write == file.LastWriteTimeUtc;
                IndexEntry existing;
                stable |= entries.TryGetValue(path,out existing) && existing.Size==file.Length && existing.WriteUtc==file.LastWriteTimeUtc;
                pending[path] = new Pending { Size=file.Length, Write=file.LastWriteTimeUtc, Attempts=stable && prior!=null ? prior.Attempts+1 : 1 };
                if (!stable || !Complete(path,extension)) { entries.Remove(path); return; }
                entries[path] = new IndexEntry { Path=path, Name=file.Name, Time=file.LastWriteTime, WriteUtc=file.LastWriteTimeUtc,
                    DateText=file.LastWriteTime.ToString("yyyyMMdd HH:mm"), Size=file.Length,
                    IsVideo=extension.Equals(".mp4",StringComparison.OrdinalIgnoreCase), Screen=GalleryIndex.ScreenOf(file.Name), Metadata=CaptureMetadata.Load(path) };
                pending.Remove(path);
            }
            catch (IOException) { if (!pending.ContainsKey(path)) pending[path]=new Pending(); }
            catch (UnauthorizedAccessException) { if (!pending.ContainsKey(path)) pending[path]=new Pending(); }
        }
        private static bool Complete(string path, string extension)
        {
            using (FileStream file = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if (extension.Equals(".png",StringComparison.OrdinalIgnoreCase))
                {
                    if (file.Length < 20) return false;
                    byte[] signature = new byte[8]; file.Read(signature,0,8);
                    if (!signature.SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) return false;
                    file.Position=file.Length-12; byte[] tail=new byte[12]; file.Read(tail,0,12);
                    return tail[4]==73 && tail[5]==69 && tail[6]==78 && tail[7]==68;
                }
            }
            try { Recording.ValidateMp4(path); return true; } catch (IOException) { return false; }
        }
        private void Publish(Request current, GalleryOutcome outcome, bool full, string[] paths)
        {
            GallerySnapshot next;
            HashSet<string> names=new HashSet<string>(entries.Values.Select(x=>x.Name),StringComparer.OrdinalIgnoreCase);
            GalleryItemSnapshot[] items=entries.Values.OrderByDescending(x=>x.Time).ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase)
                .Select(x=>new GalleryItemSnapshot(x,GalleryIndex.PeerName(x.Name)!=null && names.Contains(GalleryIndex.PeerName(x.Name)))).ToArray();
            lock (gate)
            {
                if (closed || request != current || generation != current.Generation) return;
                next=new GallerySnapshot(current.Generation,++revision,current.Folder,outcome,items,pending.Count,full,paths); snapshot=next;
            }
            current.Completion.TrySetResult(new GallerySwitchResult(outcome,next));
            EventHandler<GallerySnapshotEventArgs> handlers=Changed;
            if (handlers != null) foreach(EventHandler<GallerySnapshotEventArgs> handler in handlers.GetInvocationList())
                try { handler(this,new GallerySnapshotEventArgs(next)); } catch { Log.Warn("Gallery snapshot subscriber failed"); }
        }
        private void DisposeWatcher() { if (watcher!=null) { watcher.Dispose(); watcher=null; } }
        public async Task<GalleryOutcome> StopAsync(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException("timeout");
            lock(gate) { if(!closed) { closed=true; generation++; if(request!=null) { request.Completion.TrySetResult(new GallerySwitchResult(GalleryOutcome.Closed,null)); request.Cancel(); } request=null; } }
            try { lifetime.Cancel(); } catch (AggregateException) { Log.Warn("Gallery cancellation subscriber failed"); }
            Task finished=await Task.WhenAny(worker,Task.Delay(timeout)).ConfigureAwait(false);
            return finished==worker ? GalleryOutcome.Success : GalleryOutcome.TimedOut;
        }
        public void Dispose() { StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
    }
    internal sealed class GallerySnapshotEventArgs : EventArgs
    {
        public readonly GallerySnapshot Snapshot;
        public GallerySnapshotEventArgs(GallerySnapshot snapshot) { Snapshot=snapshot; }
    }
}
