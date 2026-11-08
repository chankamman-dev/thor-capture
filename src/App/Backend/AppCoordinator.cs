using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Backend
{
    internal enum AppJobOutcome { Success, Cancelled, TimedOut, Failed, Disconnected, Unsupported, Closed, QueueFull, Stale }
    internal enum AppJobPhase { Queued, WaitingForLease, Running, Draining, Finished }
    internal sealed class AppJobResult
    {
        public readonly Guid JobId;
        public readonly AppJobOutcome Outcome;
        public readonly string Code;
        public AppJobResult(Guid id, AppJobOutcome outcome, string code) { JobId=id; Outcome=outcome; Code=code; }
    }
    internal sealed class AppJobSnapshot
    {
        public readonly Guid Id;
        public readonly string Name;
        public readonly AppJobPhase Phase;
        public readonly Guid? SessionId;
        public readonly OperationKind Kind;
        public readonly bool DeviceWork;
        internal AppJobSnapshot(AppJob job) { Id=job.Id; Name=job.Name; Phase=job.Phase; SessionId=job.Session==null?(Guid?)null:job.Session.Id; Kind=job.Kind; DeviceWork=job.Session!=null; }
    }
    internal sealed class AppStateSnapshot
    {
        public readonly long Revision;
        public readonly bool Closing;
        public readonly Guid? SessionId;
        public readonly AppJobSnapshot Active;
        public readonly ReadOnlyCollection<AppJobSnapshot> Queued;
        internal AppStateSnapshot(long revision,bool closing,DeviceSession session,AppJob active,IEnumerable<AppJob> queued)
        {
            Revision=revision; Closing=closing; SessionId=session==null?(Guid?)null:session.Id;
            Active=active==null?null:new AppJobSnapshot(active);
            Queued=new ReadOnlyCollection<AppJobSnapshot>(queued.Select(x=>new AppJobSnapshot(x)).ToArray());
        }
    }
    internal sealed class AppStateEventArgs : EventArgs
    {
        public readonly AppStateSnapshot State;
        public AppStateEventArgs(AppStateSnapshot state) { State=state; }
    }
    internal sealed class AppJobContext
    {
        public readonly Guid JobId;
        public readonly DeviceSession Session;
        public readonly OperationLease Lease;
        public readonly CancellationToken Token;
        internal AppJobContext(AppJob job,OperationLease lease,CancellationToken token) { JobId=job.Id; Session=job.Session; Lease=lease; Token=token; }
    }
    internal sealed class AppJob
    {
        internal readonly Guid Id=Guid.NewGuid();
        internal readonly string Name;
        internal readonly DeviceSession Session;
        internal readonly OperationKind Kind;
        internal readonly TimeSpan Timeout;
        internal readonly Func<AppJobContext,Task> Work;
        internal readonly CancellationTokenSource Cancellation;
        internal readonly TaskCompletionSource<AppJobResult> Completion=new TaskCompletionSource<AppJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal AppJobPhase Phase;
        public Task<AppJobResult> Result { get { return Completion.Task; } }
        internal AppJob(string name,DeviceSession session,OperationKind kind,TimeSpan timeout,Func<AppJobContext,Task> work,CancellationToken token,CancellationToken lifetime)
        {
            Name=name; Session=session; Kind=kind; Timeout=timeout; Work=work;
            Cancellation=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime); Phase=AppJobPhase.Queued;
        }
        internal void RegisterCancellation()
        { try { Cancellation.Token.Register(delegate { Completion.TrySetResult(new AppJobResult(Id,AppJobOutcome.Cancelled,"CANCELLED")); }); } catch(ObjectDisposedException) { } }
        public void Cancel() { try { Cancellation.Cancel(); } catch(AggregateException) { Log.Warn("App job cancellation callback failed"); } catch(ObjectDisposedException) { } }
    }

    // One local queue. Device exclusivity remains owned by the existing OperationGate.
    // Cancellation/timeout reports are bounded; uncooperative work keeps its lease until actual cleanup.
    internal sealed class AppCoordinator : IDisposable
    {
        private readonly object sync=new object();
        private readonly Queue<AppJob> queue=new Queue<AppJob>();
        private readonly OperationGate operationGate;
        private readonly DeviceSessionRegistry registry;
        private readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        private readonly SemaphoreSlim wake=new SemaphoreSlim(0,1);
        private readonly SemaphoreSlim notifications=new SemaphoreSlim(0,1);
        private readonly Task worker;
        private readonly Task eventWorker;
        private AppJob active;
        private DeviceSession session;
        private bool closed;
        private long revision;
        /// <summary>Raised serially on a background dispatcher; subscribers marshal to their UI context.</summary>
        public event EventHandler<AppStateEventArgs> Changed;
        public AppCoordinator(OperationGate operationGate,DeviceSessionRegistry registry)
        {
            if(operationGate==null || registry==null) throw new ArgumentNullException("operationGate");
            this.operationGate=operationGate; this.registry=registry; worker=Task.Run((Func<Task>)RunAsync);
            eventWorker=Task.Run((Func<Task>)EventsAsync); worker.ContinueWith(t=>Notify(),TaskScheduler.Default);
        }
        public AppStateSnapshot Snapshot { get { lock(sync) return State(); } }
        private AppStateSnapshot State() { return new AppStateSnapshot(revision,closed,session,active,queue); }
        public void BindSession(DeviceSession value)
        {
            AppJob[] retired;
            lock(sync)
            {
                if(closed) return;
                if(value!=null && !registry.IsCurrent(value)) throw new ArgumentException("Session is not current","value");
                session=value; revision++;
                retired=queue.Concat(active==null?new AppJob[0]:new AppJob[]{active}).Where(x=>x.Session!=null && !ReferenceEquals(x.Session,value)).ToArray();
            }
            foreach(AppJob job in retired) job.Cancel(); Signal();
        }
        public AppJob Submit(string name,Func<AppJobContext,Task> work,TimeSpan timeout,CancellationToken token)
        { return Enqueue(name,null,OperationKind.Inspect,work,timeout,token); }
        public AppJob SubmitDevice(string name,DeviceSession boundSession,OperationKind kind,Func<AppJobContext,Task> work,TimeSpan timeout,CancellationToken token)
        {
            if(boundSession==null) throw new ArgumentNullException("boundSession");
            OperationGate.Required(kind); return Enqueue(name,boundSession,kind,work,timeout,token);
        }
        private AppJob Enqueue(string name,DeviceSession boundSession,OperationKind kind,Func<AppJobContext,Task> work,TimeSpan timeout,CancellationToken token)
        {
            if(string.IsNullOrWhiteSpace(name)||name.Length>80||name.IndexOfAny(new char[]{'\r','\n'})>=0||work==null) throw new ArgumentException("Invalid job");
            if(timeout<=TimeSpan.Zero || timeout>TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException("timeout");
            AppJob job=new AppJob(name,boundSession,kind,timeout,work,token,lifetime.Token);
            lock(sync)
            {
                if(closed || queue.Count>=64)
                {
                    Finish(job,closed?AppJobOutcome.Closed:AppJobOutcome.QueueFull,closed?"CLOSED":"QUEUE_FULL");
                    job.Cancellation.Dispose(); return job;
                }
                queue.Enqueue(job); revision++;
            }
            job.RegisterCancellation(); Signal(); return job;
        }
        private void Signal() { try { wake.Release(); } catch(SemaphoreFullException) { } Notify(); }
        private void Notify()
        {
            try { notifications.Release(); } catch(SemaphoreFullException) { }
        }
        private async Task EventsAsync()
        {
            while(true)
            {
            await notifications.WaitAsync().ConfigureAwait(false);
            AppStateSnapshot snapshot; lock(sync) snapshot=State();
            EventHandler<AppStateEventArgs> handlers=Changed;
            if(handlers!=null) foreach(EventHandler<AppStateEventArgs> handler in handlers.GetInvocationList())
                try { handler(this,new AppStateEventArgs(snapshot)); } catch { Log.Warn("App state subscriber failed"); }
            if(snapshot.Closing && worker.IsCompleted) return;
            }
        }
        private static void Finish(AppJob job,AppJobOutcome outcome,string code)
        { job.Completion.TrySetResult(new AppJobResult(job.Id,outcome,code)); }
        private async Task RunAsync()
        {
            while(true)
            {
                await wake.WaitAsync().ConfigureAwait(false); Notify();
                while(true)
                {
                    AppJob job;
                    lock(sync)
                    {
                        if(queue.Count==0) { if(closed) return; break; }
                        job=queue.Dequeue(); active=job; job.Phase=AppJobPhase.Running; revision++;
                    }
                    Notify();
                    await Execute(job).ConfigureAwait(false);
                    lock(sync) { job.Phase=AppJobPhase.Finished; active=null; revision++; }
                    job.Cancellation.Dispose(); Notify();
                }
            }
        }
        private async Task Execute(AppJob job)
        {
            OperationLease lease=null;
            CancellationTokenSource linked=null;
            Stopwatch elapsed=Stopwatch.StartNew();
            try
            {
                job.Cancellation.Token.ThrowIfCancellationRequested();
                if(job.Session!=null)
                {
                    lock(sync) { job.Phase=AppJobPhase.WaitingForLease; revision++; } Notify();
                    while(true)
                    {
                        LeaseOutcome outcome=operationGate.TryAcquire(job.Session,job.Kind,out lease);
                        if(outcome==LeaseOutcome.Acquired) break;
                        if(outcome!=LeaseOutcome.Busy)
                        {
                            Finish(job,outcome==LeaseOutcome.Unsupported?AppJobOutcome.Unsupported:outcome==LeaseOutcome.Closed?AppJobOutcome.Closed:AppJobOutcome.Disconnected,"LEASE_"+outcome.ToString().ToUpperInvariant()); return;
                        }
                        if(elapsed.Elapsed>=job.Timeout) { Finish(job,AppJobOutcome.TimedOut,"LEASE_TIMEOUT"); return; }
                        await Task.Delay(30,job.Cancellation.Token).ConfigureAwait(false);
                    }
                    lock(sync) { job.Phase=AppJobPhase.Running; revision++; } Notify();
                }
                linked=lease==null?CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token):CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token,lease.Token);
                AppJobContext context=new AppJobContext(job,lease,linked.Token);
                Task work=Task.Run(()=>job.Work(context));
                TimeSpan remaining=job.Timeout-elapsed.Elapsed;
                if(remaining<TimeSpan.Zero) remaining=TimeSpan.Zero;
                Task timer=Task.Delay(remaining,linked.Token);
                Task completed=await Task.WhenAny(work,timer).ConfigureAwait(false);
                if(completed!=work)
                {
                    bool cancelled=linked.IsCancellationRequested;
                    try { linked.Cancel(); } catch(AggregateException) { Log.Warn("App task cancellation callback failed"); }
                    lock(sync) { job.Phase=AppJobPhase.Draining; revision++; }
                    AppJobOutcome outcome=cancelled?(job.Session!=null&&!registry.IsCurrent(job.Session)?AppJobOutcome.Disconnected:AppJobOutcome.Cancelled):AppJobOutcome.TimedOut;
                    Finish(job,outcome,cancelled?"CANCELLED":"TIMEOUT"); Notify();
                    // Never release an encoder/device lease while its owner is still executing.
                    try { await work.ConfigureAwait(false); } catch { }
                    return;
                }
                await work.ConfigureAwait(false);
                if(linked.IsCancellationRequested) Finish(job,AppJobOutcome.Cancelled,"CANCELLED");
                else if(lease!=null && !lease.IsCurrent) Finish(job,AppJobOutcome.Stale,"STALE_SESSION");
                else { if(lease!=null) { lease.Dispose(); lease=null; } Finish(job,AppJobOutcome.Success,"OK"); }
            }
            catch(OperationCanceledException) { Finish(job,job.Session!=null&&!registry.IsCurrent(job.Session)?AppJobOutcome.Disconnected:AppJobOutcome.Cancelled,"CANCELLED"); }
            catch { Log.Warn("App task failed"); Finish(job,AppJobOutcome.Failed,"TASK_FAILED"); }
            finally
            {
                if(linked!=null) { try { linked.Cancel(); } catch(AggregateException) { Log.Warn("App completion callback failed"); } linked.Dispose(); }
                if(lease!=null) lease.Dispose();
            }
        }
        public async Task<AppJobOutcome> StopAsync(TimeSpan timeout)
        {
            if(timeout<=TimeSpan.Zero||timeout>TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException("timeout");
            lock(sync) { closed=true; revision++; }
            try { lifetime.Cancel(); } catch(AggregateException) { Log.Warn("App close cancellation callback failed"); }
            Signal(); Task stopped=Task.WhenAll(worker,eventWorker); Task finished=await Task.WhenAny(stopped,Task.Delay(timeout)).ConfigureAwait(false);
            return finished==stopped?AppJobOutcome.Success:AppJobOutcome.TimedOut;
        }
        public void Dispose() { StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
    }
}
