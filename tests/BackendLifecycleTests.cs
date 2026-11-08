using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal static class BackendLifecycleTests
    {
        private const string Fixture = @"'use strict';
const fs=require('node:fs'),path=require('node:path'),http=require('node:http'),crypto=require('node:crypto');
const root=process.env.COCOON_DATA_ROOT, cfg=JSON.parse(fs.readFileSync(path.join(root,'fixture.json'),'utf8'));
const log=path.join(root,'launches.txt');fs.appendFileSync(log,process.pid+'\n');
const count=fs.readFileSync(log,'utf8').trim().split('\n').length;
const id=cfg.mode==='reuse-instance'?'cccccccc-cccc-4ccc-8ccc-cccccccccccc':crypto.randomUUID();
const token=cfg.mode==='reuse-token'?'fixture-stable-token':crypto.randomUUID();
let stopping=false;
const stop=()=>{if(stopping)return;stopping=true;setTimeout(()=>server.close(()=>process.exit(0)),cfg.stopDelay||0);};
const server=http.createServer((req,res)=>{
 if(req.url==='/'){setTimeout(()=>res.end(cfg.mode==='missing-token'?'fixture-empty':'<meta name='+String.fromCharCode(34)+'cocoon-art-token'+String.fromCharCode(34)+' content='+String.fromCharCode(34)+token+String.fromCharCode(34)+'>'),cfg.tokenDelay||0);}
 else{res.setHeader('content-type','application/json');res.end(JSON.stringify({id,authorized:req.headers['x-cocoon-art-token']===token}));}
});
const emit=()=>process.stdout.write('COCOON_BACKEND_EVENT '+JSON.stringify({event:'ready',product:'cocoon-art',contract:'cocoon.backend/1',instanceId:id,version:'fixture',endpoint:'http://127.0.0.1:'+server.address().port+'/',capabilities:{localCatalog:true}})+'\n');
server.listen(0,'127.0.0.1',()=>{if(cfg.mode==='exit-start'){process.exit(9);return;}setTimeout(()=>{emit(); if(cfg.mode==='crash'||cfg.mode==='crash-once'&&count===1)setTimeout(()=>process.exit(7),150);},cfg.readyDelay||0);});
let pending='';process.stdin.on('data',data=>{pending+=data;let end;while((end=pending.indexOf('\n'))>=0){const line=pending.slice(0,end);pending=pending.slice(end+1);try{const value=JSON.parse(line);if(value.command==='shutdown'&&value.instanceId===id)stop();}catch{}}});
process.stdin.on('end',stop);";
        private static async Task Until(Func<bool> predicate, int timeout=8000)
        {
            Stopwatch watch=Stopwatch.StartNew();
            while(!predicate()) { if(watch.ElapsedMilliseconds>timeout) throw new Exception("Lifecycle fixture condition timed out"); await Task.Delay(10).ConfigureAwait(false); }
        }
        private sealed class Setup : IDisposable
        {
            public readonly string Root;
            public readonly CocoonBackend Backend;
            public Setup(string mode,int ready=0,int stop=0,int token=0,int startBudget=2000,int restart=80,int limit=3)
            {
                Root=Path.Combine(Tests.Root,"lifecycle-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Root);
                File.WriteAllText(Path.Combine(Root,"backend.cjs"),Fixture);
                File.WriteAllText(Path.Combine(Root,"fixture.json"),"{\"mode\":"+Json.Quote(mode)+",\"readyDelay\":"+ready+",\"stopDelay\":"+stop+",\"tokenDelay\":"+token+"}");
                Backend=new CocoonBackend(null,new BackendLifecycleOptions(startBudget,restart,60000,limit)){BackendDir=Root,DataRoot=Root};
            }
            public int Launches
            {
                get
                {
                    string file=Path.Combine(Root,"launches.txt");if(!File.Exists(file))return 0;
                    // The fixture appends while Until polls; permit its writer and count only complete records.
                    using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
                    using(var reader=new StreamReader(stream))return reader.ReadToEnd().Count(c=>c=='\n');
                }
            }
            public void Dispose() { if(!Backend.StopAsync(TimeSpan.FromSeconds(8),CancellationToken.None).GetAwaiter().GetResult()) throw new Exception("Owned fixture did not exit; never killed"); }
        }
        internal static void Run() { Task.Run((Func<Task>)RunAsync).GetAwaiter().GetResult(); }
        private static async Task RunAsync()
        {
            Tests.Reject(()=>new BackendLifecycleOptions(49),"backend lifecycle fixture budgets reject invalid ranges");
            using(Setup fixture=new Setup("normal"))
            {
                await Task.WhenAll(Enumerable.Range(0,12).Select(x=>Task.Run(()=>fixture.Backend.Start())));
                await Until(()=>fixture.Backend.State==BackendState.Ready);
                int pid=fixture.Backend.OwnedProcessId.Value;string id=fixture.Backend.InstanceId;
                Tests.Check(fixture.Launches==1 && fixture.Backend.Owns(pid),"concurrent backend Start owns exactly one fixture child");
                object probe=fixture.Backend.GetJson("/api/probe");
                Tests.Check(Json.Flag(probe,"authorized") && Json.Text(probe,"id")==id,"backend token stays private and authenticates its current fixture");
                Tests.Check(await fixture.Backend.StopAsync(TimeSpan.FromSeconds(3),CancellationToken.None),"backend normal StopAsync awaits actual child exit");
                fixture.Backend.Start();await Until(()=>fixture.Backend.State==BackendState.Ready);
                Tests.Check(fixture.Backend.InstanceId!=id && fixture.Backend.OwnedProcessId!=pid && Json.Flag(fixture.Backend.GetJson("/api/probe"),"authorized"),"backend manual restart has fresh instance and token binding");
            }
            using(Setup fixture=new Setup("normal",stop:700))
            {
                fixture.Backend.Start();await Until(()=>fixture.Backend.State==BackendState.Ready);
                int pid=fixture.Backend.OwnedProcessId.Value;Stopwatch elapsed=Stopwatch.StartNew();
                bool closed=await fixture.Backend.StopAsync(TimeSpan.FromMilliseconds(80),CancellationToken.None);
                Tests.Check(!closed && elapsed.ElapsedMilliseconds<500 && fixture.Backend.Owns(pid) && fixture.Backend.FailureCode=="BACKEND_STOP_TIMEOUT","backend stop timeout is bounded and retains exact process ownership");
                Tests.Check(fixture.Backend.Endpoint==null,"backend shutdown immediately revokes API endpoint and token access");
                await Until(()=>!fixture.Backend.OwnedProcessId.HasValue);
                Tests.Check(fixture.Launches==1 && await fixture.Backend.StopAsync(TimeSpan.FromSeconds(1),CancellationToken.None),"backend timed out fixture can exit naturally and be awaited again");
            }
            using(Setup fixture=new Setup("normal",stop:500))
            {
                fixture.Backend.Start();await Until(()=>fixture.Backend.State==BackendState.Ready);
                CancellationTokenSource cancellation=new CancellationTokenSource();cancellation.Cancel();
                bool closed=await fixture.Backend.StopAsync(TimeSpan.FromSeconds(1),cancellation.Token);
                Tests.Check(!closed && fixture.Backend.OwnedProcessId.HasValue && fixture.Backend.FailureCode=="BACKEND_STOP_CANCELLED","backend cancelled stop retains owned process with distinct failure code");cancellation.Dispose();
            }
            using(Setup fixture=new Setup("normal",stop:500))
            {
                fixture.Backend.Start();await Until(()=>fixture.Backend.State==BackendState.Ready);
                await fixture.Backend.StopAsync(TimeSpan.FromMilliseconds(50),CancellationToken.None);
                fixture.Backend.Start();fixture.Backend.Stop();await Until(()=>!fixture.Backend.OwnedProcessId.HasValue);await Task.Delay(200);
                Tests.Check(fixture.Launches==1,"explicit stop cancels a pending manual retry after stop timeout");
            }
            using(Setup fixture=new Setup("exit-start"))
            {
                fixture.Backend.Start();await Until(()=>fixture.Backend.State==BackendState.Failed);await Task.Delay(200);
                Tests.Check(fixture.Backend.FailureCode=="BACKEND_EXITED" && fixture.Launches==1,"pre-ready backend exit is typed and does not enter automatic restart loop");
            }
            using(Setup fixture=new Setup("missing-token"))
            {
                fixture.Backend.Start();await Until(()=>fixture.Backend.State==BackendState.Failed);
                Tests.Check(fixture.Backend.FailureCode=="TOKEN_UNAVAILABLE" && fixture.Backend.Endpoint==null && fixture.Backend.OwnedProcessId.HasValue,"invalid token page fails closed while retaining the child for orderly shutdown");
            }
            using(Setup fixture=new Setup("normal",ready:350,stop:600,startBudget:150))
            {
                int readyEvents=0;fixture.Backend.StateChanged+=(s,e)=>{if(fixture.Backend.State==BackendState.Ready)Interlocked.Increment(ref readyEvents);};
                fixture.Backend.Start();await Until(()=>fixture.Backend.FailureCode=="BACKEND_START_TIMEOUT");
                await Task.Delay(450);
                Tests.Check(readyEvents==0 && fixture.Backend.State!=BackendState.Ready && fixture.Backend.Endpoint==null,"late backend ready cannot revive a start-timed-out generation");
            }
            using(Setup fixture=new Setup("normal",token:500,stop:700))
            {
                fixture.Backend.Start();await Until(()=>fixture.Launches==1);await Task.Delay(120);fixture.Backend.Stop();await Task.Delay(550);
                Tests.Check(fixture.Backend.State!=BackendState.Ready && fixture.Backend.Endpoint==null,"late token page completion cannot revive an explicitly stopped backend");
            }
            using(Setup fixture=new Setup("crash-once"))
            {
                fixture.Backend.Start();await Until(()=>fixture.Backend.State==BackendState.Ready);string first=fixture.Backend.InstanceId;
                await Until(()=>fixture.Launches==2 && fixture.Backend.State==BackendState.Ready);
                Tests.Check(fixture.Backend.InstanceId!=first && Json.Flag(fixture.Backend.GetJson("/api/probe"),"authorized"),"unexpected ready child crash automatically restarts with fresh credentials");
            }
            using(Setup fixture=new Setup("crash",limit:2))
            {
                fixture.Backend.Start();await Until(()=>fixture.Backend.FailureCode=="BACKEND_RESTART_LIMIT");await Task.Delay(300);
                Tests.Check(fixture.Launches==3 && !fixture.Backend.OwnedProcessId.HasValue,"backend crash burst obeys bounded automatic restart limit");
            }
            using(Setup fixture=new Setup("crash",restart:500))
            {
                fixture.Backend.Start();await Until(()=>fixture.Backend.FailureCode=="BACKEND_CRASHED");fixture.Backend.Stop();await Task.Delay(750);
                Tests.Check(fixture.Launches==1 && fixture.Backend.State==BackendState.Stopped,"explicit stop suppresses pending delayed automatic restart");
            }
            foreach(string mode in new[]{"reuse-instance","reuse-token"}) using(Setup fixture=new Setup(mode))
            {
                fixture.Backend.Start();await Until(()=>fixture.Backend.State==BackendState.Ready);
                await fixture.Backend.StopAsync(TimeSpan.FromSeconds(2),CancellationToken.None);
                fixture.Backend.Start();await Until(()=>fixture.Backend.State==BackendState.Failed);
                Tests.Check(fixture.Backend.FailureCode==(mode=="reuse-instance"?"INSTANCE_REUSED":"TOKEN_REUSED") && fixture.Backend.Endpoint==null,"backend rejects repeated launch "+mode+" identity");
            }
        }
    }
}
