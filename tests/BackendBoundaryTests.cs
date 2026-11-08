using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;
using ThorCapture.Features.Capture;
using ThorCapture.Media;

namespace ThorCapture
{
    // Isolated regression fixtures only. No ADB, device, network provider or existing media is used.
    internal static class BackendBoundaryTests
    {
        internal static void Run() { Task.Run((Func<Task>)Checks).GetAwaiter().GetResult(); }
        static string NewRoot() { string root=Path.Combine(Tests.Root,"backend-boundary-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root; }
        static async Task Checks()
        {
            string root=NewRoot(), file=Path.Combine(root,"batch.json"), partial=file+".partial";
            File.WriteAllText(partial,"owned-test-sentinel");
            try { typeof(CaptureService).GetMethod("SaveNew",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{file,new byte[]{1}}); }
            catch(TargetInvocationException) { }
            Tests.Check(File.Exists(partial)&&File.ReadAllText(partial)=="owned-test-sentinel"&&!File.Exists(file),"capture metadata collision preserves a temporary file it did not create");

            string media=Path.Combine(root,"media"),data=Path.Combine(root,"repository"),outside=Path.Combine(root,"junction-target");
            Directory.CreateDirectory(media);Directory.CreateDirectory(data);Directory.CreateDirectory(outside);
            using(MediaRepository repository=new MediaRepository(media,data))
            {
                await repository.RefreshAsync(CancellationToken.None);
                // Both ends are freshly generated beneath this fixture. Never recursively delete a junction.
                string junction=Path.Combine(data,"transactions");
                if(junction.IndexOfAny(new[]{'"','&','|','<','>','^','%'})>=0||outside.IndexOfAny(new[]{'"','&','|','<','>','^','%'})>=0)throw new Exception("Unsafe fixture path");
                using(Process child=Process.Start(new ProcessStartInfo("cmd.exe","/d /c mklink /J \""+junction+"\" \""+outside+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}))
                { string output=child.StandardOutput.ReadToEnd();string error=child.StandardError.ReadToEnd();if(!child.WaitForExit(5000)||child.ExitCode!=0)throw new Exception("Junction fixture creation failed");GC.KeepAlive(output);GC.KeepAlive(error); }
                byte[] png;using(Bitmap image=new Bitmap(4,4))using(MemoryStream memory=new MemoryStream()){image.Save(memory,System.Drawing.Imaging.ImageFormat.Png);png=memory.ToArray();}
                MediaRepositoryResult result=await repository.PublishGroupAsync(repository.GetSnapshot().RepositoryId,new[]{new PublishedPngInput("fixture.png",ScreenRole.Upper,png)},CancellationToken.None);
                Tests.Check(result.Outcome!=MediaOutcome.Success&&Directory.GetFileSystemEntries(outside).Length==0&&!File.Exists(Path.Combine(media,"fixture.png")),"publication rejects a linked transaction directory before writing any journal outside its repository");
                using(DeviceSessionRegistry registry=new DeviceSessionRegistry())using(OperationGate operations=new OperationGate(registry))
                {
                    DeviceSession session=registry.Open("boundary-offline",DeviceCapabilities.Capture,new DisplayTarget(0,"1","upper",4,4));
                    using(BlockSource source=new BlockSource())
                    {
                        CaptureService service=new CaptureService(source,operations,repository);
                        Task<CaptureBatchResult> work=service.RunAsync(new CaptureRequest(session,session.Displays,media,false,TimeSpan.FromSeconds(2),4096),CancellationToken.None);
                        Task<bool> stop=Task.Run(()=>service.StopAsync(TimeSpan.FromMilliseconds(20)));bool timely=false,ack=false;
                        try { if(!source.Entered.WaitOne(2000))throw new Exception("Cancellation fixture did not enter");await Task.Delay(250);timely=stop.IsCompleted;ack=timely&&stop.Status==TaskStatus.RanToCompletion&&stop.Result; }
                        finally { source.Release.Set();source.Completion.TrySetResult(new ScreenBytes(null,"CAPTURE_CANCELLED",true));work.GetAwaiter().GetResult();stop.GetAwaiter().GetResult(); }
                        Tests.Check(timely&&!ack,"capture stop deadline returns pending while an owned cancellation callback is blocked");
                    }
                }
            }
            string backendRoot=Path.Combine(root,"child");Directory.CreateDirectory(backendRoot);
            File.WriteAllText(Path.Combine(backendRoot,"backend.cjs"),"process.stdin.resume();process.stdin.once('data',()=>process.exit(0));process.stdin.once('end',()=>process.exit(0));");
            using(ManualResetEvent entered=new ManualResetEvent(false))using(ManualResetEvent release=new ManualResetEvent(false))
            using(CocoonBackend backend=new CocoonBackend(null,new BackendLifecycleOptions(5000,20,100,0)){BackendDir=backendRoot,DataRoot=Path.Combine(root,"child-data"),AutoRestart=false})
            {
                bool synchronousExitPending=false;
                EventHandler handler=(sender,e)=>{if(backend.State==BackendState.Stopped){entered.Set();release.WaitOne();}};
                backend.StateChanged+=handler;backend.Start();Task<bool> stop=backend.StopAsync(TimeSpan.FromSeconds(3),CancellationToken.None);
                try { if(!entered.WaitOne(5000))throw new Exception("Owned Node exit callback did not enter");await Task.Delay(60);synchronousExitPending=!stop.IsCompleted||stop.Status==TaskStatus.RanToCompletion&&!stop.Result; }
                finally { release.Set();stop.GetAwaiter().GetResult();backend.StateChanged-=handler;backend.StopAsync(TimeSpan.FromSeconds(3),CancellationToken.None).GetAwaiter().GetResult(); }
                Tests.Check(synchronousExitPending,"backend cannot acknowledge cleanup before its owned exit handler and synchronous subscriber drain");
            }
        }
        sealed class BlockSource:IScreenCaptureSource,IDisposable
        {
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            internal readonly TaskCompletionSource<ScreenBytes> Completion=new TaskCompletionSource<ScreenBytes>();
            CancellationTokenRegistration callback;
            public bool SupportsParallel {get{return false;}}
            public Task<ScreenBytes> ReadAsync(DeviceSession session,DisplayTarget display,TimeSpan timeout,CancellationToken token)
            {callback=token.Register(()=>{Entered.Set();Release.WaitOne();});return Completion.Task;}
            public Task<bool> StopAsync(){return Task.FromResult(false);}
            public void Dispose(){Release.Set();callback.Dispose();Entered.Dispose();Release.Dispose();}
        }
    }
}
