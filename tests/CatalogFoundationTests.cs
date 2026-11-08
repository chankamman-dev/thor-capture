using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture
{
    internal static class CatalogFoundationTests
    {
        private static string Root(string name) { string path=Path.Combine(Tests.Root,name+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        private static void Png(string path,Color color) { using(Bitmap bitmap=new Bitmap(16,16)) { using(Graphics g=Graphics.FromImage(bitmap)) g.Clear(color); bitmap.Save(path,ImageFormat.Png); } }
        private static bool Until(Func<bool> condition,int milliseconds=5000)
        { System.Diagnostics.Stopwatch timer=System.Diagnostics.Stopwatch.StartNew(); while(timer.ElapsedMilliseconds<milliseconds) { if(condition())return true; Thread.Sleep(20); }return condition(); }
        internal static void Run()
        {
            Task.Run((Func<Task>)Monitor).GetAwaiter().GetResult();
            Task.Run((Func<Task>)Coordinator).GetAwaiter().GetResult();
            Task.Run((Func<Task>)Settings).GetAwaiter().GetResult();
        }
        private static async Task Monitor()
        {
            string first=Root("watch-"),second=Root("switch-"),upper=Path.Combine(first,"thor-upper-fixture.png"),lower=Path.Combine(first,"thor-lower-fixture.png");
            int notifications=0; bool background=true;
            using(GalleryMonitor monitor=new GalleryMonitor(40))
            {
                monitor.Changed+=(s,e)=>{ Interlocked.Increment(ref notifications); background &= Thread.CurrentThread.IsThreadPoolThread; };
                GallerySwitchResult start=await monitor.SwitchAsync(first,CancellationToken.None);
                Tests.Check(start.Outcome==GalleryOutcome.Success && start.Snapshot.Items.Count==0,"gallery watcher starts asynchronously on an empty fixture directory");
                string temp=upper+".partial"; Png(temp,Color.Orange); Thread.Sleep(180);
                Tests.Check(monitor.Snapshot.Items.Count==0,"gallery ignores atomic temporary files before their final rename");
                File.Move(temp,upper);
                Tests.Check(Until(()=>monitor.Snapshot.Items.Count==1),"real FileSystemWatcher creation is debounced into a ready media item");
                GallerySnapshot frozen=monitor.Snapshot;
                Png(lower,Color.Blue);
                Tests.Check(Until(()=>monitor.Snapshot.Items.Count==2 && monitor.Snapshot.Items.All(x=>x.Paired)),"incremental addition recomputes both members of a filename pair");
                Tests.Check(frozen.Items.Count==1 && !frozen.Items[0].Paired,"published gallery snapshots do not mutate when a paired file arrives");
                File.Delete(lower);
                Tests.Check(Until(()=>monitor.Snapshot.Items.Count==1 && !monitor.Snapshot.Items[0].Paired),"real watcher deletion updates the surviving pair without rescanning on the UI thread");
                string slow=Path.Combine(first,"slow.png"),complete=Path.Combine(second,"source.png"); Png(complete,Color.Red); byte[] bytes=File.ReadAllBytes(complete);
                using(FileStream writer=new FileStream(slow,FileMode.Create,FileAccess.Write,FileShare.ReadWrite))
                { writer.Write(bytes,0,bytes.Length/2); writer.Flush(); Thread.Sleep(200); Tests.Check(monitor.Snapshot.Items.All(x=>x.Name!="slow.png"),"open partial PNG is pending rather than exposed as corrupt media"); writer.Write(bytes,bytes.Length/2,bytes.Length-bytes.Length/2); }
                Tests.Check(Until(()=>monitor.Snapshot.Items.Any(x=>x.Name=="slow.png")),"a slow final-name copy becomes visible only after completion and stability");
                new CaptureMetadata {Package="com.fixture.changed",Status="identified"}.Save(upper);
                Tests.Check(Until(()=>monitor.Snapshot.Items.Any(x=>x.Path==upper && x.Package=="com.fixture.changed")),"sidecar-only events refresh associated metadata incrementally");
                long revision=monitor.Snapshot.Revision; monitor.NotifyOverflow();
                Tests.Check(Until(()=>monitor.Snapshot.Revision>revision && monitor.Snapshot.FullRescan),"overflow and explicit refresh use a full reconciliation path");
                GallerySwitchResult switched=await monitor.SwitchAsync(second,CancellationToken.None);
                Tests.Check(switched.Outcome==GalleryOutcome.Success && switched.Snapshot.Generation>frozen.Generation,"directory switch publishes a distinct generation");
                Png(Path.Combine(first,"old.png"),Color.Yellow); Thread.Sleep(180);
                Tests.Check(monitor.Snapshot.Folder==second && monitor.Snapshot.Items.All(x=>x.Name!="old.png"),"retired watcher events cannot contaminate the newly selected directory");
                using(CancellationTokenSource cancelled=new CancellationTokenSource())
                { cancelled.Cancel(); Tests.Check((await monitor.SwitchAsync(first,cancelled.Token)).Outcome==GalleryOutcome.Cancelled,"cancelled directory switch preserves the active directory"); }
                Tests.Check((await monitor.StopAsync(TimeSpan.FromSeconds(2)))==GalleryOutcome.Success,"gallery watcher close awaits owned background cleanup within a bound");
                Tests.Check(background && notifications>0,"gallery events are delivered on a worker and carry immutable snapshots");
                Tests.Check((await monitor.SwitchAsync(first,CancellationToken.None)).Outcome==GalleryOutcome.Closed,"closed gallery monitor rejects fresh directory work");
            }
            using(GalleryMonitor missing=new GalleryMonitor(40))
            {
                string directory=Path.Combine(second,"not-created");
                Tests.Check((await missing.SwitchAsync(directory,CancellationToken.None)).Outcome==GalleryOutcome.MissingDirectory,"missing gallery directory reports a typed error and is not silently replaced");
                Directory.CreateDirectory(directory); missing.Refresh();
                Tests.Check(Until(()=>missing.Snapshot!=null && missing.Snapshot.Outcome==GalleryOutcome.Success),"explicit refresh can attach a watcher after a formerly missing directory appears");
                Png(Path.Combine(directory,"recovered.png"),Color.Green);
                Tests.Check(Until(()=>missing.Snapshot.Items.Count==1),"recovered directory continues receiving real watcher notifications");
            }
            using(GalleryMonitor timeout=new GalleryMonitor(250,100))
            { Tests.Check((await timeout.SwitchAsync(first,CancellationToken.None)).Outcome==GalleryOutcome.TimedOut,"gallery initialization has a bounded timeout and rejects delayed initial publication"); }
            using(CancellationTokenSource cancelled=new CancellationTokenSource())
            { cancelled.Cancel(); Tests.Reject(()=>{ string error; new GalleryIndex().Scan(first,out error,cancelled.Token); },"cancellable GalleryIndex overload rejects retired scan work without changing the legacy interface"); }
            using(GalleryMonitor periodic=new GalleryMonitor(40,10000,1000))
            {
                await periodic.SwitchAsync(second,CancellationToken.None); Until(()=>periodic.Snapshot.Items.Count>0);
                long revision=periodic.Snapshot.Revision;
                Tests.Check(Until(()=>periodic.Snapshot.Revision>revision && periodic.Snapshot.FullRescan),"periodic background reconciliation does not rely exclusively on watcher notification delivery");
            }
        }
        private static async Task Coordinator()
        {
            using(DeviceSessionRegistry registry=new DeviceSessionRegistry())
            using(OperationGate gate=new OperationGate(registry))
            using(AppCoordinator app=new AppCoordinator(gate,registry))
            {
                DeviceSession session=registry.Open("fixture",DeviceCapabilities.Recording|DeviceCapabilities.VideoPreview);
                app.BindSession(session);
                TaskCompletionSource<bool> held=new TaskCompletionSource<bool>();
                AppJob first=app.Submit("local-first",async context=>{ await held.Task; },TimeSpan.FromSeconds(5),CancellationToken.None);
                Tests.Check(Until(()=>app.Snapshot.Active!=null),"app coordinator exposes an active job without UI busy flags");
                AppStateSnapshot frozen=app.Snapshot;
                bool secondRan=false;
                AppJob second=app.Submit("local-second",context=>{ secondRan=true; return Task.FromResult(0); },TimeSpan.FromSeconds(1),CancellationToken.None);
                Tests.Check(app.Snapshot.Queued.Count==1 && frozen.Queued.Count==0,"queue state snapshots remain immutable after later submissions");
                second.Cancel(); Tests.Check((await second.Result).Outcome==AppJobOutcome.Cancelled && !secondRan,"queued cancellation reports promptly and never starts cancelled work");
                held.SetResult(true); Tests.Check((await first.Result).Outcome==AppJobOutcome.Success,"successful local work reports typed completion");
                Tests.Check(Until(()=>app.Snapshot.Active==null),"local cancellation does not strand coordinator busy state");
                OperationLease preview; gate.TryAcquire(session,OperationKind.Preview,out preview);
                bool called=false;
                AppJob leaseBlocked=app.SubmitDevice("record",session,OperationKind.Recording,c=>{called=true;return Task.FromResult(0);},TimeSpan.FromMilliseconds(100),CancellationToken.None);
                Tests.Check((await leaseBlocked.Result).Outcome==AppJobOutcome.TimedOut && !called,"device queue uses existing encoder leases and times out before executing conflicting work"); preview.Dispose();
                TaskCompletionSource<bool> release=new TaskCompletionSource<bool>();
                AppJob timed=app.SubmitDevice("uncooperative",session,OperationKind.Recording,async c=>{await release.Task;},TimeSpan.FromMilliseconds(80),CancellationToken.None);
                Tests.Check((await timed.Result).Outcome==AppJobOutcome.TimedOut && gate.ActiveCount==1,"job timeout retains its device lease while uncooperative work is draining");
                Tests.Check((await app.StopAsync(TimeSpan.FromMilliseconds(80)))==AppJobOutcome.TimedOut && app.Snapshot.Closing,"close is bounded and reports pending owned cleanup instead of killing unrelated work");
                release.SetResult(true);
                Tests.Check((await app.StopAsync(TimeSpan.FromSeconds(2)))==AppJobOutcome.Success && gate.ActiveCount==0,"late owned cleanup releases the lease and finishes an earlier timed-out close");
                AppJob denied=app.Submit("closed",c=>Task.FromResult(0),TimeSpan.FromSeconds(1),CancellationToken.None);
                Tests.Check((await denied.Result).Outcome==AppJobOutcome.Closed,"closed coordinator rejects new local operations");
            }
            using(DeviceSessionRegistry registry=new DeviceSessionRegistry())
            using(OperationGate gate=new OperationGate(registry))
            using(AppCoordinator app=new AppCoordinator(gate,registry))
            {
                DeviceSession session=registry.Open("disconnect-fixture",DeviceCapabilities.Capture); app.BindSession(session);
                AppJob job=app.SubmitDevice("wait-disconnect",session,OperationKind.Capture,c=>Task.Delay(5000,c.Token),TimeSpan.FromSeconds(6),CancellationToken.None);
                Tests.Check(Until(()=>gate.ActiveCount==1),"coordinator acquires an existing device lease before calling its work"); registry.Disconnect(session.Serial);
                Tests.Check((await job.Result).Outcome==AppJobOutcome.Disconnected,"device retirement produces a disconnected result without rebinding work to another session");
                DeviceSession noCapability=registry.Open("unsupported-fixture",DeviceCapabilities.None);
                bool called=false; job=app.SubmitDevice("unsupported",noCapability,OperationKind.Recording,c=>{called=true;return Task.FromResult(0);},TimeSpan.FromSeconds(1),CancellationToken.None);
                Tests.Check((await job.Result).Outcome==AppJobOutcome.Unsupported && !called,"unsupported device jobs perform no device work");
                job=app.Submit("failure",c=>{throw new IOException("fixture");},TimeSpan.FromSeconds(1),CancellationToken.None);
                Tests.Check((await job.Result).Outcome==AppJobOutcome.Failed,"task exceptions become typed failures without leaking filesystem detail");
                TaskCompletionSource<bool> hold=new TaskCompletionSource<bool>();
                AppJob active=app.Submit("capacity-owner",c=>hold.Task,TimeSpan.FromSeconds(4),CancellationToken.None); Until(()=>app.Snapshot.Active!=null);
                List<AppJob> waiting=new List<AppJob>(); for(int i=0;i<64;i++) waiting.Add(app.Submit("queued",c=>Task.FromResult(0),TimeSpan.FromSeconds(1),CancellationToken.None));
                AppJob rejected=app.Submit("over-capacity",c=>Task.FromResult(0),TimeSpan.FromSeconds(1),CancellationToken.None);
                Tests.Check((await rejected.Result).Outcome==AppJobOutcome.QueueFull,"local pending queue is bounded to 64 entries");
                foreach(AppJob item in waiting)item.Cancel(); hold.SetResult(true); await active.Result;
            }
        }
        private static async Task Settings()
        {
            string root=Root("settings-"),legacy=Path.Combine(root,"legacy.txt"),path=Path.Combine(root,"新目录","preferences.txt");
            File.WriteAllText(legacy,"skin=rainbow\nlampColor=46E2F0\ncaptureDir=Q:\\fixture gallery\nunknown.future=保留字段\n"); byte[] original=File.ReadAllBytes(legacy);
            SettingsStore store=new SettingsStore(path);
            SettingsResult loaded=await store.LoadAsync(legacy,CancellationToken.None);
            Tests.Check(loaded.Outcome==SettingsOutcome.Migrated && loaded.Snapshot.Get("unknown.future","")=="保留字段" && original.SequenceEqual(File.ReadAllBytes(legacy)),"legacy settings migration preserves unknown keys, Chinese values, paths and the untouched source");
            Dictionary<string,string> copy=loaded.Snapshot.CopyValues(); copy["skin"]="changed";
            Tests.Check(store.Snapshot.Get("skin","")=="rainbow","settings snapshots cannot be modified through copied dictionaries");
            SettingsStore other=new SettingsStore(path);
            await Task.WhenAll(store.SaveAsync(new Dictionary<string,string>{{"lampLevel","75"}},CancellationToken.None),other.SaveAsync(new Dictionary<string,string>{{"view","2"}},CancellationToken.None));
            loaded=await store.LoadAsync(null,CancellationToken.None);
            Tests.Check(loaded.Snapshot.GetInt("lampLevel",10,10,100)==75 && loaded.Snapshot.Get("view","")=="2" && loaded.Snapshot.Get("unknown.future","")=="保留字段","independent settings writers serialize with a file lock and preserve each other's updates");
            File.WriteAllText(path,"schemaVersion=1\nskin=truncated\n");
            loaded=await store.LoadAsync(null,CancellationToken.None);
            Tests.Check(loaded.Outcome==SettingsOutcome.Recovered && loaded.Snapshot.Get("skin","")=="rainbow","truncated settings are detected by checksum and recover from the previous valid backup");
            loaded=await store.SaveAsync(new Dictionary<string,string>{{"skin","purple"}},CancellationToken.None);
            Tests.Check(loaded.Outcome==SettingsOutcome.Success && Directory.GetFiles(Path.GetDirectoryName(path),"*.corrupt-*").Length==1,"saving recovered settings keeps a separate corrupt original and the valid backup");
            byte[] before=File.ReadAllBytes(path);
            using(CancellationTokenSource cancel=new CancellationTokenSource())
            { cancel.Cancel(); loaded=await store.SaveAsync(new Dictionary<string,string>{{"skin","white"}},cancel.Token); Tests.Check(loaded.Outcome==SettingsOutcome.Cancelled && before.SequenceEqual(File.ReadAllBytes(path)),"cancelled settings save leaves existing bytes intact"); }
            Tests.Check(Directory.GetFiles(Path.GetDirectoryName(path),"*.tmp").Length==0,"atomic settings commits leave no partial output");
            string blocked=Path.Combine(root,"blocked"); File.WriteAllText(blocked,"fixture");
            loaded=await new SettingsStore(Path.Combine(blocked,"settings.txt")).SaveAsync(new Dictionary<string,string>{{"x","y"}},CancellationToken.None);
            Tests.Check(loaded.Outcome==SettingsOutcome.FileFailure,"unwritable settings destination reports failure instead of claiming a saved preference");
            string future=Path.Combine(root,"future.txt"); File.WriteAllText(future,"schemaVersion=99\nformat=unknown\n"); byte[] futureBytes=File.ReadAllBytes(future);
            SettingsStore futureStore=new SettingsStore(future); loaded=await futureStore.LoadAsync(null,CancellationToken.None);
            Tests.Check(loaded.Outcome==SettingsOutcome.UnsupportedSchema,"settings from a future schema are explicitly unsupported instead of reset to defaults");
            loaded=await futureStore.SaveAsync(new Dictionary<string,string>{{"x","y"}},CancellationToken.None);
            Tests.Check(loaded.Outcome==SettingsOutcome.UnsupportedSchema && futureBytes.SequenceEqual(File.ReadAllBytes(future)),"saving refuses to overwrite an unknown future schema");
            using(FileStream held=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            {
                loaded=await store.SaveAsync(new Dictionary<string,string>{{"view","0"}},CancellationToken.None);
                Tests.Check(loaded.Outcome==SettingsOutcome.TimedOut,"settings cross-process writer lock has a bounded timeout");
            }
            DataDirectoryResult data=await DataDirectoryPolicy.ResolveAsync(root,Path.Combine(root,"appdata"),false,CancellationToken.None);
            Tests.Check(data.Outcome==DataDirectoryOutcome.Success && !data.Portable && data.Directory.Contains("appdata"),"default data policy uses the supplied user-writable application-data root");
            data=await DataDirectoryPolicy.ResolveAsync(blocked,root,true,CancellationToken.None);
            Tests.Check(data.Outcome==DataDirectoryOutcome.NotWritable && data.Portable,"explicit portable mode does not silently change a failing storage location");
        }
    }
}
