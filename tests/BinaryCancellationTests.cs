using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;
namespace ThorCapture
{
    internal static class BinaryCancellationTests
    {
        internal static void Run(){Task.Run((Func<Task>)Checks).GetAwaiter().GetResult();}
        static ProcessRequest Request(string command){return new ProcessRequest(typeof(Tests).Assembly.Location,"fixture.binary-cancel",new[]{command});}
        static async Task Checks()
        {
            await Blocked(false);await Blocked(true);
            using(BinaryProcessRunner runner=new BinaryProcessRunner())using(CancellationTokenSource parent=new CancellationTokenSource())
            {
                parent.Cancel();BinaryProcessResult result=await runner.RunBytesAsync(Request("bc-bytes"),1024,TimeSpan.FromSeconds(2),parent.Token);
                Tests.Check(result.Outcome==BinaryProcessOutcome.Cancelled&&result.CleanupComplete&&runner.ActiveCount==0,"pre-cancelled binary parent has tracked cleanup without launching producer");
            }
        }
        static async Task Blocked(bool parentCancellation)
        {
            using(BinaryProcessRunner runner=new BinaryProcessRunner())using(CancellationTokenSource parent=new CancellationTokenSource())
            using(ControlledSink sink=new ControlledSink())using(Process foreign=new Process{StartInfo=Request("bp-hang").StartInfo()})
            {
                foreign.Start();OwnedBinaryJob job=null;CancellationTokenRegistration callback=default(CancellationTokenRegistration);
                try
                {
                    job=runner.Start(Request("bc-bytes"),sink,1024,TimeSpan.FromSeconds(5),parent.Token);
                    if(!sink.WriteEntered.WaitOne(2000))throw new Exception("Owned sink fixture not reached");
                    int callbacks=0;callback=job.Cancellation.Token.Register(()=>{Interlocked.Increment(ref callbacks);sink.CancelEntered.Set();sink.CancelRelease.WaitOne();});
                    Stopwatch clock=Stopwatch.StartNew();
                    Task<bool> stop;
                    if(parentCancellation){Task cancellation=Task.Run(()=>parent.Cancel());if(await Task.WhenAny(cancellation,Task.Delay(500))!=cancellation)throw new Exception("Parent cancellation blocked");await cancellation;stop=Task.Run(()=>runner.StopAllAsync(TimeSpan.FromMilliseconds(20),CancellationToken.None));}
                    else stop=Task.Run(()=>runner.StopAllAsync(TimeSpan.FromMilliseconds(20),CancellationToken.None));
                    if(!sink.CancelEntered.WaitOne(2000))throw new Exception("Owned cancellation callback not reached");
                    sink.WriteRelease.Set();bool timely=await Task.WhenAny(stop,Task.Delay(500))==stop;bool clean=timely&&await stop;
                    Tests.Check(timely&&!clean&&clock.ElapsedMilliseconds<800,"binary "+(parentCancellation?"parent cancellation":"stop")+" remains bounded while callback blocks");
                    BinaryProcessResult result=await job.Completion;
                    Tests.Check(!result.CleanupComplete&&!job.Cleanup.IsCompleted&&runner.ActiveCount==1&&!foreign.HasExited,"binary process exit and drained pipes cannot acknowledge an unfinished cancellation callback");
                    Tests.Check(!await runner.StopAllAsync(TimeSpan.FromMilliseconds(20),CancellationToken.None)&&callbacks==1,"binary stop retry retains the same blocked callback without rescheduling cancellation");
                    sink.CancelRelease.Set();
                    Tests.Check(await runner.AwaitCleanupAsync(job.Id,TimeSpan.FromSeconds(2),CancellationToken.None)&&await runner.StopAllAsync(TimeSpan.FromSeconds(2),CancellationToken.None)&&runner.ActiveCount==0,"binary callback completion permits later cleanup acknowledgement and retry");
                }
                finally
                {
                    sink.WriteRelease.Set();sink.CancelRelease.Set();callback.Dispose();
                    if(job!=null){job.RequestStop();awaitSafe(job.Completion);}
                    runner.StopAllAsync(TimeSpan.FromSeconds(3),CancellationToken.None).GetAwaiter().GetResult();
                    if(!foreign.HasExited){foreign.Kill();foreign.WaitForExit(2000);}
                }
            }
        }
        static void awaitSafe(Task task){try{task.GetAwaiter().GetResult();}catch{}}
        internal static int FakeAdb(string command,string mode,string directory)
        {
            if(!command.StartsWith("bc-bytes",StringComparison.Ordinal))return Tests.NotHandled;
            using(Stream output=Console.OpenStandardOutput()){byte[] bytes=new byte[1024];output.Write(bytes,0,bytes.Length);output.Flush();}return 0;
        }
        sealed class ControlledSink:Stream
        {
            internal readonly ManualResetEvent WriteEntered=new ManualResetEvent(false),WriteRelease=new ManualResetEvent(false),CancelEntered=new ManualResetEvent(false),CancelRelease=new ManualResetEvent(false);
            public override bool CanRead{get{return false;}}public override bool CanWrite{get{return true;}}public override bool CanSeek{get{return false;}}
            public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override void Write(byte[] bytes,int offset,int count){WriteEntered.Set();WriteRelease.WaitOne();}
            protected override void Dispose(bool disposing){WriteRelease.Set();CancelRelease.Set();if(disposing){WriteEntered.Dispose();WriteRelease.Dispose();CancelEntered.Dispose();CancelRelease.Dispose();}base.Dispose(disposing);}
            public override void Flush(){}public override int Read(byte[] bytes,int offset,int count){throw new NotSupportedException();}public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long length){throw new NotSupportedException();}
        }
    }
}
