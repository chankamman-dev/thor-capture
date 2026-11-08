using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture.Media
{
    internal enum MediaLibraryOutcome { Success, Cancelled, Closed, TimedOut, MissingDirectory, FileFailure, RepositoryFailure, Busy }
    internal sealed class MediaLibrarySnapshot
    {
        public readonly string MediaRoot;
        public readonly long Generation, Revision;
        public readonly MediaLibraryOutcome Outcome;
        public readonly string Code;
        public readonly int PendingFiles;
        public readonly MediaSnapshot Media;
        internal MediaLibrarySnapshot(string root,long generation,long revision,MediaLibraryOutcome outcome,string code,int pending,MediaSnapshot media)
        { MediaRoot=root;Generation=generation;Revision=revision;Outcome=outcome;Code=code;PendingFiles=pending;Media=media; }
    }
    internal sealed class MediaLibraryEventArgs : EventArgs
    {
        public readonly MediaLibrarySnapshot Snapshot;
        internal MediaLibraryEventArgs(MediaLibrarySnapshot snapshot) { Snapshot=snapshot; }
    }
    internal sealed class MediaLibraryResult
    {
        public readonly MediaLibraryOutcome Outcome;
        public readonly MediaLibrarySnapshot Snapshot;
        internal MediaLibraryResult(MediaLibraryOutcome outcome,MediaLibrarySnapshot snapshot) { Outcome=outcome;Snapshot=snapshot; }
    }
    // The monitor accepts complete/stable files; only the repository assigns persistent media identities.
    internal sealed class MediaLibraryService : IDisposable
    {
        private sealed class Binding
        {
            internal readonly object Gate=new object();
            internal readonly string Root;
            internal readonly long Generation;
            internal readonly CancellationTokenSource Cancellation=new CancellationTokenSource();
            internal readonly SemaphoreSlim Wake=new SemaphoreSlim(0,1);
            internal readonly GalleryMonitor Monitor;
            internal readonly MediaRepository Repository;
            internal readonly TaskCompletionSource<MediaLibraryResult> Initial=new TaskCompletionSource<MediaLibraryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal GallerySnapshot Latest;
            internal readonly HashSet<string> Dirty=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal bool Full=true;
            internal Task Work;
            internal Binding(string root,string data,long generation,int debounce,int reconciliation)
            { Root=root;Generation=generation;Repository=new MediaRepository(root,data);Monitor=new GalleryMonitor(debounce,10000,reconciliation); }
            internal void Signal() { try { Wake.Release(); } catch(SemaphoreFullException) { } }
        }
        private readonly object gate=new object();
        private readonly List<Binding> bindings=new List<Binding>();
        private readonly int debounce,reconciliation;
        private Binding current;
        private long generation,revision;
        private bool closed;
        private MediaLibrarySnapshot snapshot;
        public event EventHandler<MediaLibraryEventArgs> Changed;
        internal MediaLibraryService(int debounceMilliseconds=150,int reconciliationMilliseconds=30000)
        {
            if(debounceMilliseconds<20||debounceMilliseconds>2000||reconciliationMilliseconds<1000||reconciliationMilliseconds>120000)throw new ArgumentOutOfRangeException("timing");
            debounce=debounceMilliseconds;reconciliation=reconciliationMilliseconds;
        }
        public MediaLibrarySnapshot Snapshot { get { lock(gate)return snapshot; } }
        public Task<MediaLibraryResult> SwitchAsync(string mediaRoot,string repositoryRoot,CancellationToken token)
        {
            Binding next;
            try
            {
                string root=Path.GetFullPath(mediaRoot),data=Path.GetFullPath(repositoryRoot);
                lock(gate)
                {
                    if(closed)return Task.FromResult(new MediaLibraryResult(MediaLibraryOutcome.Closed,snapshot));
                    if(token.IsCancellationRequested)return Task.FromResult(new MediaLibraryResult(MediaLibraryOutcome.Cancelled,snapshot));
                    bindings.RemoveAll(x=>x.Work!=null&&x.Work.IsCompleted);
                    if(bindings.Count>=8)return Task.FromResult(new MediaLibraryResult(MediaLibraryOutcome.Busy,snapshot));
                    next=new Binding(root,data,generation+1,debounce,reconciliation);generation=next.Generation;
                    if(current!=null)Retire(current);
                    current=next;bindings.Add(next);
                    // Immediately replace the visible root with an empty immutable generation, no disk reads.
                    snapshot=new MediaLibrarySnapshot(root,next.Generation,++revision,MediaLibraryOutcome.Success,"INITIALIZING",0,next.Repository.GetSnapshot());
                    next.Monitor.Changed+=(s,e)=>{
                        lock(next.Gate){
                            next.Latest=e.Snapshot;next.Full|=e.Snapshot.FullRescan;
                            foreach(string path in e.Snapshot.ChangedPaths){string media=MediaPath(path);if(Supported(media))next.Dirty.Add(media);}
                            if(next.Dirty.Count>4096){next.Dirty.Clear();next.Full=true;}
                        }next.Signal();
                    };
                    next.Work=Task.Run(()=>Run(next));
                }
            }
            catch(ArgumentException){return Task.FromResult(new MediaLibraryResult(MediaLibraryOutcome.FileFailure,null));}
            catch(NotSupportedException){return Task.FromResult(new MediaLibraryResult(MediaLibraryOutcome.FileFailure,null));}
            catch(PathTooLongException){return Task.FromResult(new MediaLibraryResult(MediaLibraryOutcome.FileFailure,null));}
            return AwaitInitial(next,token);
        }
        private static void Retire(Binding binding)
        { binding.Initial.TrySetResult(new MediaLibraryResult(MediaLibraryOutcome.Cancelled,null));binding.Cancellation.Cancel();binding.Signal(); }
        private async Task<MediaLibraryResult> AwaitInitial(Binding binding,CancellationToken token)
        {
            using(token.Register(delegate{ lock(gate){if(binding.Initial.Task.IsCompleted)return;if(current==binding){current=null;generation++;}Retire(binding);} }))
            {
                if(await Task.WhenAny(binding.Initial.Task,Task.Delay(15000)).ConfigureAwait(false)!=binding.Initial.Task)
                {
                    lock(gate){if(!binding.Initial.Task.IsCompleted){if(current==binding){current=null;generation++;}binding.Initial.TrySetResult(new MediaLibraryResult(MediaLibraryOutcome.TimedOut,null));binding.Cancellation.Cancel();binding.Signal();}}
                }
                return await binding.Initial.Task.ConfigureAwait(false);
            }
        }
        private async Task Run(Binding binding)
        {
            try
            {
                GallerySwitchResult first=await binding.Monitor.SwitchAsync(binding.Root,binding.Cancellation.Token).ConfigureAwait(false);
                if(first.Snapshot!=null){lock(binding.Gate){if(binding.Latest==null||binding.Latest.Revision<=first.Snapshot.Revision)binding.Latest=first.Snapshot;binding.Full=true;}binding.Signal();}
                else { Complete(binding,first.Outcome==GalleryOutcome.Cancelled?MediaLibraryOutcome.Cancelled:MediaLibraryOutcome.FileFailure,"MONITOR_START_FAILED",null);throw new OperationCanceledException(); }
                while(true)
                {
                    await binding.Wake.WaitAsync(binding.Cancellation.Token).ConfigureAwait(false);
                    await Task.Delay(debounce,binding.Cancellation.Token).ConfigureAwait(false);
                    GallerySnapshot gallery;bool full;string[] dirty;
                    lock(binding.Gate){gallery=binding.Latest;full=binding.Full&&gallery!=null&&gallery.PendingFiles==0;if(full)binding.Full=false;dirty=binding.Dirty.ToArray();binding.Dirty.Clear();}
                    if(gallery==null)continue;
                    binding.Cancellation.Token.ThrowIfCancellationRequested();
                    if(gallery.Outcome!=GalleryOutcome.Success)
                    { Complete(binding,gallery.Outcome==GalleryOutcome.MissingDirectory?MediaLibraryOutcome.MissingDirectory:MediaLibraryOutcome.FileFailure,"GALLERY_"+gallery.Outcome.ToString().ToUpperInvariant(),gallery);continue; }
                    MediaRepositoryResult result;
                    if(full)result=await binding.Repository.RefreshAsync(binding.Cancellation.Token).ConfigureAwait(false);
                    else
                    {
                        // Deletions must touch their old repository paths, but pending writers must never be hashed.
                        HashSet<string> stable=new HashSet<string>(gallery.Items.Select(x=>x.Path),StringComparer.OrdinalIgnoreCase);
                        IEnumerable<string> paths=dirty.Where(x=>stable.Contains(x)||gallery.PendingFiles==0);
                        if(gallery.PendingFiles>0) paths=paths.Concat(stable); // Deferred full scans can still accept completed peers.
                        if(gallery.PendingFiles==0) paths=paths.Concat(binding.Repository.GetSnapshot().Groups.SelectMany(x=>x.Tracks).Select(x=>Path.Combine(binding.Root,x.RelativePath)).Where(x=>!stable.Contains(x)));
                        result=await binding.Repository.ImportPathsAsync(paths.Distinct(StringComparer.OrdinalIgnoreCase),binding.Cancellation.Token).ConfigureAwait(false);
                    }
                    binding.Cancellation.Token.ThrowIfCancellationRequested();
                    Complete(binding,result.Outcome==MediaOutcome.Success?MediaLibraryOutcome.Success:result.Outcome==MediaOutcome.Cancelled?MediaLibraryOutcome.Cancelled:MediaLibraryOutcome.RepositoryFailure,result.Code,gallery);
                }
            }
            catch(OperationCanceledException){binding.Initial.TrySetResult(new MediaLibraryResult(MediaLibraryOutcome.Cancelled,null));}
            catch(Exception){Complete(binding,MediaLibraryOutcome.FileFailure,"LIBRARY_WORK_FAILED",null);}
            {
                binding.Cancellation.Cancel();binding.Repository.Dispose();
                // Do not forget monitor ownership after a bounded stop reports timeout.
                while(await binding.Monitor.StopAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false)==GalleryOutcome.TimedOut) { }
            }
        }
        private static string MediaPath(string path)
        {foreach(string suffix in new[]{".thor-meta.txt",".input.csv"})if(path.EndsWith(suffix,StringComparison.OrdinalIgnoreCase))return path.Substring(0,path.Length-suffix.Length);return path;}
        private static bool Supported(string path)
        {string ext=Path.GetExtension(path);return ext.Equals(".png",StringComparison.OrdinalIgnoreCase)||ext.Equals(".mp4",StringComparison.OrdinalIgnoreCase);}
        private void Complete(Binding binding,MediaLibraryOutcome outcome,string code,GallerySnapshot gallery)
        {
            MediaLibrarySnapshot value;
            lock(gate)
            {
                if(closed||current!=binding||binding.Generation!=generation||binding.Cancellation.IsCancellationRequested)return;
                value=new MediaLibrarySnapshot(binding.Root,binding.Generation,++revision,outcome,code,gallery==null?0:gallery.PendingFiles,binding.Repository.GetSnapshot());snapshot=value;
            }
            lock(gate){if(current!=binding||closed)return;binding.Initial.TrySetResult(new MediaLibraryResult(outcome,value));}
            EventHandler<MediaLibraryEventArgs> handlers=Changed;
            if(handlers!=null)foreach(EventHandler<MediaLibraryEventArgs> handler in handlers.GetInvocationList())
                try{handler(this,new MediaLibraryEventArgs(value));}catch{Log.Warn("Media library subscriber failed");}
        }
        public void Refresh(){Binding selected;lock(gate)selected=closed?null:current;if(selected!=null)selected.Monitor.Refresh();}
        internal void NotifyOverflow(){Refresh();}
        public async Task<MediaLibraryOutcome> StopAsync(TimeSpan timeout)
        {
            if(timeout<=TimeSpan.Zero||timeout>TimeSpan.FromMinutes(1))throw new ArgumentOutOfRangeException("timeout");
            Task[] work;lock(gate){closed=true;generation++;current=null;foreach(Binding binding in bindings)Retire(binding);work=bindings.Select(x=>x.Work).Where(x=>x!=null).ToArray();}
            Task all=Task.WhenAll(work);if(await Task.WhenAny(all,Task.Delay(timeout)).ConfigureAwait(false)!=all)return MediaLibraryOutcome.TimedOut;
            try{await all.ConfigureAwait(false);return MediaLibraryOutcome.Success;}catch{return MediaLibraryOutcome.FileFailure;}
        }
        public void Dispose(){StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();}
    }
}
