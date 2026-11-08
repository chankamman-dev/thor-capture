using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Media;

namespace ThorCapture
{
    internal static class MediaLibraryServiceTests
    {
        private static string Folder(){string dir=Path.Combine(Tests.Root,"media-service-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);return dir;}
        private static byte[] Png(Color color){using(Bitmap bmp=new Bitmap(16,16))using(Graphics draw=Graphics.FromImage(bmp))using(MemoryStream stream=new MemoryStream()){draw.Clear(color);bmp.Save(stream,ImageFormat.Png);return stream.ToArray();}}
        private static async Task Until(Func<bool> predicate){Stopwatch watch=Stopwatch.StartNew();while(!predicate()){if(watch.ElapsedMilliseconds>8000)throw new Exception("Media service fixture timed out");await Task.Delay(15).ConfigureAwait(false);}}
        private static MediaTrack Track(MediaLibraryService service,string name){MediaLibrarySnapshot snapshot=service.Snapshot;return snapshot==null?null:snapshot.Media.Groups.SelectMany(x=>x.Tracks).FirstOrDefault(x=>x.RelativePath==name);}
        internal static void Run(){Task.Run((Func<Task>)RunAsync).GetAwaiter().GetResult();}
        private static async Task RunAsync()
        {
            string root=Folder(),data=Folder();MediaLibraryService service=new MediaLibraryService(30,1000);
            int callbacks=0;bool background=true;service.Changed+=(s,e)=>{Interlocked.Increment(ref callbacks);background&=Thread.CurrentThread.IsThreadPoolThread;};
            try
            {
                MediaLibraryResult initial=await service.SwitchAsync(root,data,CancellationToken.None);
                Tests.Check(initial.Outcome==MediaLibraryOutcome.Success && initial.Snapshot.Media.Groups.Count==0,"media service asynchronously initializes repository and watcher");
                string upper="thor-upper-20260927-010101-000-aabbcc.png",lower=upper.Replace("-upper-","-lower-");
                File.WriteAllBytes(Path.Combine(root,upper),Png(Color.Red));
                await Until(()=>Track(service,upper)!=null);string group=service.Snapshot.Media.Groups.Single().GroupId,track=Track(service,upper).TrackId;
                MediaLibrarySnapshot old=service.Snapshot;
                Tests.Check(Guid.Parse(group)!=Guid.Empty && Guid.Parse(track)!=Guid.Empty,"external completed PNG receives repository-owned stable UUIDs");
                File.WriteAllBytes(Path.Combine(root,lower),Png(Color.Blue));await Until(()=>service.Snapshot.Media.Groups.Any(x=>x.Tracks.Count==2));
                Tests.Check(service.Snapshot.Media.Groups.Single(x=>x.Tracks.Count==2).GroupId==group && Track(service,upper).TrackId==track,"external lower PNG automatically joins existing paired group without changing UUIDs");
                Tests.Check(old.Media.Groups.Single().Tracks.Count==1,"media service published snapshots remain immutable after pairing");
                new CaptureMetadata{Package="fixture.game",Status="identified",CapturedUtc=DateTime.UtcNow,ObservedUtc=DateTime.UtcNow}.Save(Path.Combine(root,upper));
                await Until(()=>Track(service,upper).Package=="fixture.game");
                Tests.Check(Track(service,upper).TrackId==track,"sidecar-only watcher change refreshes metadata without replacing track identity");
                string renamed="renamed-upper.png";File.Move(Path.Combine(root,upper),Path.Combine(root,renamed));
                await Until(()=>Track(service,renamed)!=null);
                Tests.Check(Track(service,renamed).TrackId==track && service.Snapshot.Media.Groups.Single(x=>x.GroupId==group).Tracks.Count==2,"real watcher rename updates relative path while preserving paired identities");
                File.Delete(Path.Combine(root,lower));await Until(()=>Track(service,lower).State==TrackState.Missing);
                Tests.Check(Track(service,lower).TrackId!=null && service.Snapshot.Media.Groups.Single(x=>x.GroupId==group).Tracks.Count==2,"external deletion preserves known missing track instead of dropping its group");
                string half="slow.png";byte[] bytes=Png(Color.Green);
                using(FileStream writer=File.Open(Path.Combine(root,half),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                {
                    writer.Write(bytes,0,bytes.Length/2);writer.Flush();service.NotifyOverflow();await Task.Delay(300);
                    Tests.Check(Track(service,half)==null && service.Snapshot.PendingFiles>0,"overflow reconcile defers half-written PNG instead of hashing it into repository");
                    writer.Write(bytes,bytes.Length/2,bytes.Length-bytes.Length/2);writer.Flush();
                }
                await Until(()=>Track(service,half)!=null);
                Tests.Check(Track(service,half).State==TrackState.Present,"late writer completion automatically imports stable PNG");
                string partial=Path.Combine(root,"atomic.png.partial"),atomic=Path.Combine(root,"atomic.png");File.WriteAllBytes(partial,Png(Color.Yellow));await Task.Delay(100);
                Tests.Check(Track(service,"atomic.png")==null,"media watcher does not import partial temporary filenames");File.Move(partial,atomic);await Until(()=>Track(service,"atomic.png")!=null);
                Tests.Check(Track(service,"atomic.png").State==TrackState.Present,"external atomic rename becomes available without manual refresh");
                long before=service.Snapshot.Revision;await Until(()=>service.Snapshot.Revision>before);
                Tests.Check(service.Snapshot.Media.FindGroup(group)!=null,"periodic monitor reconciliation reaches repository service");
                string other=Folder(),otherData=Folder();File.WriteAllBytes(Path.Combine(other,"other.png"),Png(Color.Purple));
                MediaLibraryResult selected=await service.SwitchAsync(other,otherData,CancellationToken.None);await Until(()=>Track(service,"other.png")!=null);
                long generation=service.Snapshot.Generation;File.WriteAllBytes(Path.Combine(root,"old-root.png"),Png(Color.White));await Task.Delay(200);
                Tests.Check(selected.Outcome==MediaLibraryOutcome.Success && service.Snapshot.Generation==generation && service.Snapshot.Media.Groups.SelectMany(x=>x.Tracks).All(x=>x.RelativePath=="other.png"),"directory switch rejects late old-root watcher groups and events");
                Tests.Check((await service.SwitchAsync(other,other,CancellationToken.None)).Outcome==MediaLibraryOutcome.FileFailure && service.Snapshot.Generation==generation,"invalid same-directory repository switch preserves the current root and generation");
                string stuck=Folder(),stuckData=Folder();File.WriteAllBytes(Path.Combine(stuck,"stuck.png"),Png(Color.Black));
                using(FileStream locked=File.Open(Path.Combine(stuckData,"repository.lock"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
                {
                    Task<MediaLibraryResult> oldSwitch=service.SwitchAsync(stuck,stuckData,CancellationToken.None);await Task.Delay(150);
                    MediaLibraryResult resumed=await service.SwitchAsync(other,otherData,CancellationToken.None);
                    Tests.Check((await oldSwitch).Outcome==MediaLibraryOutcome.Cancelled && resumed.Outcome==MediaLibraryOutcome.Success,"new root cancels an old queued repository lock wait and keeps its own snapshot");
                }
                before=service.Snapshot.Revision;for(int i=0;i<30;i++)service.Refresh();await Until(()=>service.Snapshot.Revision>before);
                Tests.Check(background && callbacks>0,"coalesced media changes publish only on background workers");
                Stopwatch read=Stopwatch.StartNew();for(int i=0;i<10000;i++){MediaLibrarySnapshot current=service.Snapshot;if(current==null)throw new Exception();}
                Tests.Check(read.ElapsedMilliseconds<1000,"static media snapshot reads require no disk enumeration");
                service.Refresh();Tests.Check(await service.StopAsync(TimeSpan.FromSeconds(3))==MediaLibraryOutcome.Success,"service stop cancels pending refresh and awaits all owned workers and watchers");
                before=service.Snapshot.Revision;File.WriteAllBytes(Path.Combine(other,"after-close.png"),Png(Color.White));await Task.Delay(150);
                Tests.Check(service.Snapshot.Revision==before && (await service.SwitchAsync(root,data,CancellationToken.None)).Outcome==MediaLibraryOutcome.Closed,"closed media service rejects switches and publishes no late snapshots");
            }
            finally{if(service.StopAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult()!=MediaLibraryOutcome.Success)throw new Exception("Fixture service still owns work");}
            using(MediaLibraryService missing=new MediaLibraryService(30,1000))
            {
                MediaLibraryResult result=await missing.SwitchAsync(Path.Combine(Folder(),"missing"),Folder(),CancellationToken.None);
                Tests.Check(result.Outcome==MediaLibraryOutcome.MissingDirectory,"missing media roots report typed failure without scanning production locations");
            }
            using(MediaLibraryService cancelled=new MediaLibraryService(30,1000))using(CancellationTokenSource token=new CancellationTokenSource())
            {token.Cancel();Tests.Check((await cancelled.SwitchAsync(root,data,token.Token)).Outcome==MediaLibraryOutcome.Cancelled,"pre-cancelled root switch never starts new disk work");}
            using(MediaLibraryService delayed=new MediaLibraryService(30,1000))using(ManualResetEventSlim entered=new ManualResetEventSlim())using(ManualResetEventSlim release=new ManualResetEventSlim())
            {
                delayed.Changed+=(s,e)=>{entered.Set();release.Wait();};
                await delayed.SwitchAsync(Folder(),Folder(),CancellationToken.None);await Until(()=>entered.IsSet);
                Tests.Check(await delayed.StopAsync(TimeSpan.FromMilliseconds(60))==MediaLibraryOutcome.TimedOut,"service reports bounded timeout while a subscriber still owns its worker");
                release.Set();Tests.Check(await delayed.StopAsync(TimeSpan.FromSeconds(3))==MediaLibraryOutcome.Success,"service can await actual owned cleanup after timed-out stop");
            }
        }
    }
}
