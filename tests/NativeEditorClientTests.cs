using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture
{
    internal static class NativeEditorClientTests
    {
        private sealed class OwnedHttp : IDisposable
        {
            private readonly Process child;
            public readonly Uri Endpoint;
            public bool Current = true;
            public readonly ManualResetEvent FirstHandshake=new ManualResetEvent(false);
            public OwnedHttp(string mode)
            {
                string file=Path.Combine(Tests.Root,"editor-http-"+Guid.NewGuid().ToString("N")+".cjs");
                File.WriteAllText(file,@"const http=require('node:http');const mode=process.argv[2];
const id='11111111-1111-4111-8111-111111111111', library='22222222-2222-4222-8222-222222222222';let handshakes=0,diagnosticsReads=0,archives=0,firstResponse=null,switched=false;
const server=http.createServer((req,res)=>{req.on('error',()=>{});res.on('error',()=>{});let text='',chunks=[];req.on('data',b=>{text+=b;chunks.push(b);});req.on('end',()=>{
 res.setHeader('Content-Type','application/json');
 if(req.headers['x-cocoon-art-token']!=='fixture-token' || req.url.includes('token')){res.statusCode=403;return res.end(JSON.stringify({code:'EDITOR_FORBIDDEN',phase:'NATIVE_EDITOR'}));}
 if(req.url==='/api/native-editor'){handshakes++;const changed=mode==='switch'&&handshakes>1||mode==='connect-race'&&handshakes>1||(mode==='invoke-switch'||mode==='scrape-switch')&&switched;
 const value={contract:'cocoon.native-editor/1',instanceId:changed?'44444444-4444-4444-8444-444444444444':id,libraryId:changed&&mode!=='switch'?'55555555-5555-4555-8555-555555555555':library,deviceWritesEnabled:false,previewAppliesAutomatically:false};
 if(mode==='connect-race'&&handshakes===1){firstResponse={res,value};console.log('FIRST');return;}
 res.end(JSON.stringify(value));if(mode==='connect-race'&&handshakes===2)setTimeout(()=>firstResponse.res.end(JSON.stringify(firstResponse.value)),80);return;}
 const send=value=>mode==='extras-delay'?setTimeout(()=>res.end(JSON.stringify(value)),5000):res.end(JSON.stringify(value));
 if(req.url.startsWith('/api/scrape/jobs')){
   console.log('FIRST');
   const creating=req.url==='/api/scrape/jobs',input=creating?JSON.parse(text):null;
   if(req.method!=='GET'&&!req.headers.origin){res.statusCode=403;return send({code:'MISSING_ORIGIN'});}
   if(creating&&(input.libraryId!==library||input.ids[0]!==1||input.providers[0]!=='steam')){res.statusCode=400;return send({code:'SCRAPE_INPUT_INVALID'});}
   const reply={contract:'cocoon.scrape-jobs/1',jobId:'33333333-3333-4333-8333-333333333333',libraryId:library,state:'queued',
     summary:{total:1,completed:0,identified:0,highConfidence:0,review:0,noResult:0,failed:0},currentGameId:null,createdAt:1,finishedAt:null,code:null,games:[],reviewRequired:true,deviceWritesEnabled:false};
   if(mode==='scrape-switch')switched=true;
   if(mode==='scrape-library')reply.libraryId=id;
   if(mode==='scrape-error'){res.statusCode=409;return send({code:'SCRAPE_BUSY',error:'do not echo fixture secret',phase:'SCRAPE_JOBS'});}
   res.statusCode=creating?202:200;
   if(mode==='scrape-delay')return setTimeout(()=>send(reply),5000);
   return send(reply);
 }
 if(req.url.startsWith('/api/native-editor/upload?')){
   const query=new URL(req.url,'http://127.0.0.1').searchParams;
   if(query.get('instanceId')!==id||query.get('libraryId')!==library||query.get('itemId')!=='legacy-item:1'||query.get('slot')!=='icon'||req.headers['content-type']!=='application/octet-stream'||!req.headers.origin){res.statusCode=409;return send({code:'EDITOR_SESSION_STALE',phase:'NATIVE_EDITOR'});}
   const hash=require('node:crypto').createHash('sha256').update(Buffer.concat(chunks)).digest('hex');
   const reply={contract:'cocoon.native-editor/1',instanceId:id,libraryId:library,deviceWritesEnabled:false,previewAppliesAutomatically:false,ok:true,data:{candidate:{id:hash,sourceBlobSha256:hash,width:16,height:16},pendingChanged:false}};
   if(mode==='upload-library')reply.libraryId='55555555-5555-4555-8555-555555555555';if(mode==='upload-stage')reply.data.pendingChanged=true;
   res.statusCode=201;return send(reply);
 }
 if(req.url==='/api/native-editor/archive-foreign'){
   archives++;const body=JSON.parse(text);
   if(body.instanceId!==id||body.libraryId!==library||body.confirm!==true){res.statusCode=400;return send({code:'EDITOR_CONFIRM_REQUIRED',phase:'NATIVE_EDITOR'});}
   const reply={contract:'cocoon.native-editor/1',instanceId:id,libraryId:library,deviceWritesEnabled:false,previewAppliesAutomatically:false,archived:true,archiveId:'66666666-6666-4666-8666-666666666666',previousRevision:archives,revision:0,entries:[]};
   if(mode==='archive-library')reply.libraryId='55555555-5555-4555-8555-555555555555';if(mode==='archive-entry')reply.entries=[{raw:'SECRET'}];
   return send(reply);
 }
 if(req.url.startsWith('/api/backend/diagnostics?max=')){
   diagnosticsReads++;const row={id:'77777777-7777-4777-8777-777777777777',timestamp:'2026-09-27T00:00:00.000Z',event:'operation.completed',code:'OK',component:'api',durationMs:12,count:1,success:true};
   const reply={contract:'cocoon.diagnostics/1',instanceId:mode==='diag-switch'&&diagnosticsReads>1?'88888888-8888-4888-8888-888888888888':'99999999-9999-4999-8999-999999999999',timestamp:'2026-09-27T00:00:00.001Z',capacity:256,recorded:1,dropped:0,retained:1,events:[row]};
   if(mode==='diag-raw')row.message='SECRET_PATH_TOKEN';if(mode==='diag-code')row.code='SECRET_PATH_TOKEN';if(mode==='diag-time')row.timestamp='yesterday';if(mode==='diag-count')row.count=1.5;if(mode==='diag-uuid')row.id='not-a-uuid';
   return send(reply);
 }
 if(req.url!=='/api/native-editor/invoke'||req.method!=='POST'){res.statusCode=404;return res.end('{}');}
 const body=JSON.parse(text), envelope={contract:'cocoon.native-editor/1',instanceId:id,requestId:'33333333-3333-4333-8333-333333333333',ok:true,data:{itemId:body.input.itemId,slot:'icon',items:[],recommendationOnly:true},error:null,deviceWritesEnabled:false};
 if(mode==='error'){res.statusCode=409;envelope.ok=false;envelope.data=null;envelope.error={code:'EDITOR_REVISION_CONFLICT',phase:'NATIVE_EDITOR',status:409};}
 if(mode==='connect-race')envelope.instanceId='44444444-4444-4444-8444-444444444444';
 if(mode==='invoke-switch')switched=true;
 if(mode==='stale')envelope.instanceId='44444444-4444-4444-8444-444444444444';
 if(mode==='version')envelope.contract='cocoon.native-editor/2';
 if(mode==='write')envelope.deviceWritesEnabled=true;
 if(mode==='mismatch')envelope.data.itemId='legacy-item:999';
 if(mode==='malformed')envelope.data.items='invalid';
 if(mode==='missing')delete envelope.error;
 if(mode==='duplicate')return res.end(JSON.stringify(envelope).replace('{','{'+String.fromCharCode(34)+'ok'+String.fromCharCode(34)+':false,'));
 if(mode==='redirect'){res.statusCode=302;res.setHeader('Location','http://invalid.example/token-capture');return res.end('{}');}
 if(mode==='large'){res.setHeader('Content-Length',2*1024*1024);return res.end('{}');}
 if(mode==='plain'){res.statusCode=409;return res.end(JSON.stringify({code:'EDITOR_SESSION_STALE',phase:'NATIVE_EDITOR'}));}
 if(mode==='delay')return setTimeout(()=>res.end(JSON.stringify(envelope)),5000);
 if(mode==='late'||mode==='switch')return setTimeout(()=>res.end(JSON.stringify(envelope)),200);
 res.end(JSON.stringify(envelope));
 });});server.listen(0,'127.0.0.1',()=>console.log(server.address().port));
process.stdin.resume();process.stdin.on('data',d=>{if(String(d).includes('stop'))server.close(()=>process.exit(0));});
",new UTF8Encoding(false));
                ProcessStartInfo start=new ProcessRequest("node","fixture.editor-http",new[]{file,mode}).StartInfo();start.RedirectStandardInput=true;
                int port=0;ManualResetEvent ready=new ManualResetEvent(false);
                child=new Process{StartInfo=start};child.OutputDataReceived+=delegate(object sender,DataReceivedEventArgs e){int number;if(e.Data=="FIRST")FirstHandshake.Set();if(int.TryParse(e.Data,out number)){port=number;ready.Set();}};
                child.ErrorDataReceived+=delegate { };child.Start();child.BeginOutputReadLine();child.BeginErrorReadLine();
                if(!ready.WaitOne(10000)||port<1){Dispose();throw new Exception("Owned HTTP fixture failed");}ready.Dispose();Endpoint=new Uri("http://127.0.0.1:"+port+"/");
            }
            public NativeEditorClient Client(){return new NativeEditorClient(()=>Endpoint,()=>new NativeEditorHttpTransport(Endpoint,"fixture-token",()=>Current));}
            public void Dispose(){Current=false;try{if(!child.HasExited){child.StandardInput.WriteLine("stop");child.StandardInput.Flush();if(!child.WaitForExit(1000)){child.Kill();child.WaitForExit(1000);}}}finally{child.Dispose();}}
        }
        internal static void Run(){Task.Run((Func<Task>)Checks).GetAwaiter().GetResult();}
        private static async Task Checks()
        {
            Tests.Reject(()=>new NativeEditorHttpTransport(new Uri("https://example.com/"),"fixture",()=>true),"editor authenticated transport rejects external HTTPS endpoint");
            Tests.Reject(()=>new NativeEditorHttpTransport(new Uri("http://127.0.0.1:123/?token=unsafe"),"fixture",()=>true),"editor transport rejects credential/query URL before any HTTP");
            Tests.Reject(()=>NativeEditorJson.Parse("{\"ok\":true,\"ok\":false}"),"editor JSON rejects duplicate keys rather than tolerant overwrite");
            Tests.Reject(()=>NativeEditorJson.Parse("{\"n\":+1}"),"editor JSON rejects nonstandard numeric syntax");
            Tests.Reject(()=>NativeEditorJson.Parse("{\"n\":NaN}"),"editor JSON rejects nonfinite numeric token");
            Tests.Reject(()=>NativeEditorJson.Parse("{\"n\":1} trailing"),"editor JSON rejects trailing data");
            foreach(string mode in new[]{"scrape-library","scrape-switch","scrape-error"})
            using(OwnedHttp server=new OwnedHttp(mode))
            {
                NativeEditorClient client=server.Client();await client.ConnectAsync(CancellationToken.None);bool rejected=false;
                try{await client.StartScrapeAsync(new long[]{1},new[]{"steam"},"balanced","en",false,CancellationToken.None);}
                catch(NativeEditorException error){rejected=error.Code==(mode=="scrape-error"?"SCRAPE_BUSY":mode=="scrape-switch"?"EDITOR_SESSION_STALE":"EDITOR_RESPONSE_INVALID")&&!error.Message.Contains("fixture secret");}
                Tests.Check(rejected,"scrape owned HTTP rejects "+mode+" with a typed failure");
            }
            using(OwnedHttp server=new OwnedHttp("scrape-delay"))
            {
                NativeEditorClient client=server.Client();await client.ConnectAsync(CancellationToken.None);
                using(CancellationTokenSource cancel=new CancellationTokenSource())
                {
                    Task<NativeScrapeJob> pending=client.StartScrapeAsync(new long[]{1},new[]{"steam"},"balanced","en",false,cancel.Token);
                    Tests.Check(server.FirstHandshake.WaitOne(3000),"scrape cancellation fixture received the actual request");
                    cancel.Cancel();bool cancelled=false;try{await pending;}catch(OperationCanceledException){cancelled=true;}
                    Tests.Check(cancelled,"scrape HTTP cancellation aborts an owned in-flight response");
                }
            }
            using(OwnedHttp server=new OwnedHttp("good"))
            {
                NativeEditorClient client=server.Client();bool disconnected=false;try{await client.PendingAsync(CancellationToken.None);}catch(NativeEditorException e){disconnected=e.Code=="EDITOR_NOT_CONNECTED";}
                Tests.Check(disconnected,"editor operations require explicit versioned handshake");
                NativeEditorInfo info=await client.ConnectAsync(CancellationToken.None);
                Tests.Check(info.LibraryId=="22222222-2222-4222-8222-222222222222","editor info binds persisted library UUID and instance");
                NativeEditorResult rows=await client.CandidatesAsync("legacy-item:1",ArtworkSlot.Icon,CancellationToken.None);
                Tests.Check(((List<object>)rows.Data["items"]).Count==0&&rows.Operation=="candidates","fixed invoke request authenticates through header without URL token");
                Dictionary<string,object> data=rows.Data;data["slot"]="mutated";Tests.Check((string)rows.Data["slot"]=="icon","editor response DTO returns immutable snapshot copies");
                Tests.Reject(()=>client.CandidatesAsync("../../private",ArtworkSlot.Icon,CancellationToken.None),"editor input rejects arbitrary item route/path");
                Tests.Reject(()=>client.MetadataPreviewAsync("legacy-item:1",NativeEditorClient.Map("isFavorite","false"),CancellationToken.None),"editor request rejects boolean coercion");
                Tests.Reject(()=>client.MetadataConfirmAsync("valid",new[]{"title","title"},0,CancellationToken.None),"editor request rejects duplicate field confirmations");
            }
            foreach(string mode in new[]{"error","plain","stale","version","write","mismatch","malformed","missing","duplicate","redirect","large"})
            using(OwnedHttp server=new OwnedHttp(mode))
            {
                NativeEditorClient client=server.Client();await client.ConnectAsync(CancellationToken.None);bool rejected=false;
                try{await client.CandidatesAsync("legacy-item:1",ArtworkSlot.Icon,CancellationToken.None);}
                catch(NativeEditorException e){rejected=mode=="error"?e.Code=="EDITOR_REVISION_CONFLICT"&&e.Status==409:e.Code.StartsWith("EDITOR_",StringComparison.Ordinal);}
                Tests.Check(rejected,"editor rejects "+mode+" response with typed private-data-free error");
            }
            using(OwnedHttp server=new OwnedHttp("delay"))
            {
                NativeEditorClient client=server.Client();await client.ConnectAsync(CancellationToken.None);using(CancellationTokenSource cancel=new CancellationTokenSource())
                {cancel.CancelAfter(80);Stopwatch watch=Stopwatch.StartNew();bool cancelled=false;try{await client.CandidatesAsync("legacy-item:1",ArtworkSlot.Icon,cancel.Token);}catch(OperationCanceledException){cancelled=true;}
                    Tests.Check(cancelled&&watch.ElapsedMilliseconds<1500,"editor cancellation aborts in-flight owned HTTP promptly");}
            }
            using(OwnedHttp server=new OwnedHttp("delay"))
            {
                NativeEditorClient client=server.Client();await client.ConnectAsync(CancellationToken.None);using(CancellationTokenSource cancel=new CancellationTokenSource())
                {Task<NativeEditorResult> pending=client.CandidatesAsync("legacy-item:1",ArtworkSlot.Icon,cancel.Token);await Task.Delay(50);server.Current=false;cancel.CancelAfter(100);bool rejected=false;try{await pending;}catch(OperationCanceledException){rejected=true;}catch(NativeEditorException e){rejected=e.Code=="EDITOR_SESSION_STALE";}
                    Tests.Check(rejected,"editor captured backend generation cannot survive shutdown/restart");}
            }
            using(OwnedHttp server=new OwnedHttp("late"))
            {
                NativeEditorClient client=server.Client();await client.ConnectAsync(CancellationToken.None);
                Task<NativeEditorResult> pending=client.CandidatesAsync("legacy-item:1",ArtworkSlot.Icon,CancellationToken.None);await Task.Delay(40);server.Current=false;
                bool stale=false;try{await pending;}catch(NativeEditorException e){stale=e.Code=="EDITOR_SESSION_STALE";}
                Tests.Check(stale,"editor discards an actual late response after captured session changes");
            }
            using(OwnedHttp server=new OwnedHttp("switch"))
            {
                NativeEditorClient client=server.Client();await client.ConnectAsync(CancellationToken.None);
                Task<NativeEditorResult> pending=client.CandidatesAsync("legacy-item:1",ArtworkSlot.Icon,CancellationToken.None);await Task.Delay(40);await client.ConnectAsync(CancellationToken.None);
                bool stale=false;try{await pending;}catch(NativeEditorException e){stale=e.Code=="EDITOR_SESSION_STALE";}
                Tests.Check(stale,"editor handshake replacement rejects old-instance response even at the same endpoint");
            }
            using(OwnedHttp server=new OwnedHttp("connect-race"))
            {
                NativeEditorClient client=server.Client();Task<NativeEditorInfo> old=client.ConnectAsync(CancellationToken.None);
                if(!server.FirstHandshake.WaitOne(2000))throw new Exception("Connect fixture barrier timed out");
                NativeEditorInfo latest=await client.ConnectAsync(CancellationToken.None);bool stale=false;
                try{await old;}catch(NativeEditorException e){stale=e.Code=="EDITOR_SESSION_STALE";}
                NativeEditorResult rows=await client.CandidatesAsync("legacy-item:1",ArtworkSlot.Icon,CancellationToken.None);
                Tests.Check(stale&&latest.LibraryId=="55555555-5555-4555-8555-555555555555"&&rows.InstanceId==latest.InstanceId,"late connect cannot overwrite the newer same-endpoint library binding");
            }
            using(OwnedHttp server=new OwnedHttp("invoke-switch"))
            {
                NativeEditorClient client=server.Client();await client.ConnectAsync(CancellationToken.None);bool stale=false;
                try{await client.CandidatesAsync("legacy-item:1",ArtworkSlot.Icon,CancellationToken.None);}catch(NativeEditorException e){stale=e.Code=="EDITOR_SESSION_STALE";}
                Tests.Check(stale,"invoke success is rejected when the editor library changed before the response is accepted");
            }
            await ExtraChecks();
            await RealBackend();
        }
        private static byte[] ImageBytes()
        {using(System.Drawing.Bitmap image=new System.Drawing.Bitmap(16,16))using(MemoryStream output=new MemoryStream()){image.SetPixel(0,0,System.Drawing.Color.Red);image.Save(output,System.Drawing.Imaging.ImageFormat.Png);return output.ToArray();}}
        private static string Digest(byte[] value)
        {using(System.Security.Cryptography.SHA256 hash=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(hash.ComputeHash(value)).Replace("-","").ToLowerInvariant();}
        private sealed class ControlledUploadStream : Stream
        {
            internal bool ThrowPrivate;
            public override bool CanRead {get{return true;}} public override bool CanWrite {get{return false;}} public override bool CanSeek {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}} public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override async Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)
            {if(ThrowPrivate)throw new Exception("SECRET_PATH_TOKEN_SOURCE");await Task.Delay(2000,token);return 0;}
            public override int Read(byte[] b,int o,int c){throw new NotSupportedException();} public override void Flush(){} public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();} public override void SetLength(long n){throw new NotSupportedException();}public override void Write(byte[] b,int o,int c){throw new NotSupportedException();}
        }
        private static async Task ExtraChecks()
        {
            using(OwnedHttp server=new OwnedHttp("good"))
            {
                NativeEditorClient client=server.Client();await client.ConnectAsync(CancellationToken.None);
                byte[] bytes=Encoding.ASCII.GetBytes("snapshot-before-upload"),before=(byte[])bytes.Clone();Task<NativeEditorUploadResult> upload=client.UploadAsync("legacy-item:1",ArtworkSlot.Icon,bytes,CancellationToken.None);Array.Clear(bytes,0,bytes.Length);
                NativeEditorUploadResult result=await upload;Tests.Check(result.Candidate.SourceBlobSha256==Digest(before)&&result.LibraryId.Length==36,"editor upload snapshots mutable byte input and binds immutable candidate DTO");
                using(MemoryStream source=new MemoryStream(before))
                {NativeEditorUploadResult streamed=await client.UploadAsync("legacy-item:1",ArtworkSlot.Icon,source,before.Length,CancellationToken.None);Tests.Check(streamed.Candidate.Id==result.Candidate.Id&&source.CanRead,"editor bounded stream upload preserves bytes and caller stream ownership");}
                using(MemoryStream source=new MemoryStream(before))
                {Tests.Reject(()=>client.UploadAsync("legacy-item:1",ArtworkSlot.Icon,source,32L*1024*1024+1,CancellationToken.None).GetAwaiter().GetResult(),"editor upload rejects payload budget before HTTP");}
                using(ControlledUploadStream source=new ControlledUploadStream())using(CancellationTokenSource cancel=new CancellationTokenSource())
                {cancel.CancelAfter(80);Stopwatch clock=Stopwatch.StartNew();bool aborted=false;try{await client.UploadAsync("legacy-item:1",ArtworkSlot.Icon,source,16,cancel.Token);}catch(OperationCanceledException){aborted=true;}
                    Tests.Check(aborted&&clock.ElapsedMilliseconds<1500&&source.CanRead,"editor cancellation during upload source read aborts HTTP without disposing borrowed stream");}
                using(ControlledUploadStream source=new ControlledUploadStream{ThrowPrivate=true})
                {bool typed=false;try{await client.UploadAsync("legacy-item:1",ArtworkSlot.Icon,source,16,CancellationToken.None);}catch(NativeEditorException e){typed=e.Code=="EDITOR_UPLOAD_SOURCE_FAILED"&&!e.Message.Contains("SECRET");}
                    Tests.Check(typed,"editor upload source exceptions cannot expose path or credential text");}
                bool confirm=false;try{await client.ArchiveForeignAsync(false,CancellationToken.None);}catch(NativeEditorException e){confirm=e.Code=="EDITOR_CONFIRM_REQUIRED";}
                NativeEditorArchiveResult archive=await client.ArchiveForeignAsync(true,CancellationToken.None);
                Tests.Check(confirm&&archive.PreviousRevision==1&&archive.Revision==0&&archive.EntryCount==0,"editor archive requires explicit confirm with zero false-confirm HTTP writes");
                NativeBackendDiagnostics diagnostic=await client.DiagnosticsAsync(8,CancellationToken.None);
                Tests.Check(diagnostic.Events.Count==1&&diagnostic.Events[0].Code==NativeDiagnosticCode.OK&&diagnostic.Events[0].Event==NativeDiagnosticEvent.OperationCompleted&&diagnostic.Events[0].DurationMs==12,"editor diagnostics projects known enums numeric values and UTC timestamps");
                Tests.Reject(()=>diagnostic.Events.Clear(),"editor diagnostics event collection immutable");
                Tests.Reject(()=>client.DiagnosticsAsync(257,CancellationToken.None).GetAwaiter().GetResult(),"editor diagnostics query bounded to 256 entries");
            }
            foreach(string mode in new[]{"upload-library","upload-stage","archive-library","archive-entry","diag-raw","diag-code","diag-time","diag-count","diag-uuid"})
            using(OwnedHttp server=new OwnedHttp(mode))
            {
                NativeEditorClient client=server.Client();await client.ConnectAsync(CancellationToken.None);bool rejected=false;
                try{if(mode.StartsWith("upload"))await client.UploadAsync("legacy-item:1",ArtworkSlot.Icon,ImageBytes(),CancellationToken.None);else if(mode.StartsWith("archive"))await client.ArchiveForeignAsync(true,CancellationToken.None);else await client.DiagnosticsAsync(8,CancellationToken.None);}
                catch(NativeEditorException e){rejected=e.Code.StartsWith("EDITOR_")&&!e.Message.Contains("SECRET");}
                Tests.Check(rejected,"editor typed extension rejects "+mode+" without accepting raw data");
            }
            using(OwnedHttp server=new OwnedHttp("diag-switch"))
            {
                NativeEditorClient client=server.Client();await client.ConnectAsync(CancellationToken.None);await client.DiagnosticsAsync(8,CancellationToken.None);bool stale=false;
                try{await client.DiagnosticsAsync(8,CancellationToken.None);}catch(NativeEditorException e){stale=e.Code=="EDITOR_SESSION_STALE";}
                Tests.Check(stale,"editor diagnostic instance changes require explicit fresh connection");
            }
            foreach(string operation in new[]{"upload","archive","diagnostics"})
            using(OwnedHttp server=new OwnedHttp("extras-delay"))
            {
                NativeEditorClient client=server.Client();await client.ConnectAsync(CancellationToken.None);
                using(CancellationTokenSource cancel=new CancellationTokenSource())
                {cancel.CancelAfter(100);Stopwatch watch=Stopwatch.StartNew();bool cancelled=false;
                    try{if(operation=="upload")await client.UploadAsync("legacy-item:1",ArtworkSlot.Icon,ImageBytes(),cancel.Token);else if(operation=="archive")await client.ArchiveForeignAsync(true,cancel.Token);else await client.DiagnosticsAsync(8,cancel.Token);}catch(OperationCanceledException){cancelled=true;}
                    Tests.Check(cancelled&&watch.ElapsedMilliseconds<1500,"editor "+operation+" cancellation aborts actual owned HTTP promptly");}
            }
            using(OwnedHttp server=new OwnedHttp("extras-delay"))
            {
                NativeEditorClient client=server.Client();await client.ConnectAsync(CancellationToken.None);Task<NativeEditorUploadResult> pending=client.UploadAsync("legacy-item:1",ArtworkSlot.Icon,ImageBytes(),CancellationToken.None);await Task.Delay(50);server.Current=false;
                // Wait for the actual late reply; no result can survive owner invalidation.
                bool stale=false;try{await pending;}catch(NativeEditorException e){stale=e.Code=="EDITOR_SESSION_STALE";}Tests.Check(stale,"editor upload late response rejected after owner context changes");
            }
        }
        private static async Task Wait(CocoonBackend backend,BackendState state)
        {for(int i=0;i<750&&backend.State!=state;i++)await Task.Delay(20).ConfigureAwait(false);if(backend.State!=state)throw new Exception("Owned editor backend state deadline");}
        private static async Task RealBackend()
        {
            string backendDir=Path.GetFullPath(Path.Combine(Adb.AppDir(),"../../cocoon-art-backend")),demo=Path.GetFullPath(Path.Combine(Adb.AppDir(),"../tools/cocoon-demo.cjs"));
            string root=Path.Combine(Tests.Root,"native-editor-real-"+Guid.NewGuid().ToString("N"));
            using(ProcessRunner runner=new ProcessRunner())
            {ProcessResult made=await runner.RunAsync(new ProcessRequest("node","fixture.editor-demo",new[]{demo,root}),TimeSpan.FromSeconds(30),CancellationToken.None);Tests.Check(made.Outcome==ProcessOutcome.Success&&made.CleanupComplete,"editor real backend gets isolated demo data from owned maker");await runner.StopAllAsync();}
            string db=Path.Combine(root,"_data/cocoon_db");byte[] before=File.ReadAllBytes(db);
            CocoonBackend backend=new CocoonBackend(null){BackendDir=backendDir,DataRoot=root};
            try
            {
                backend.Start();await Wait(backend,BackendState.Ready);
                // Test-only access to the token of this exact owned demo session. No production token is read.
                FieldInfo credential=typeof(CocoonBackend).GetField("token",BindingFlags.NonPublic|BindingFlags.Instance);
                NativeEditorClient client=new NativeEditorClient(backend,delegate{Uri address=backend.Endpoint;string token=(string)credential.GetValue(backend);return new NativeEditorHttpTransport(address,token,()=>backend.State==BackendState.Ready&&address.Equals(backend.Endpoint));});
                NativeEditorInfo info=await client.ConnectAsync(CancellationToken.None);Tests.Check(info.LibraryId.Length==36,"editor authenticates actual owned Node facade with library identity");
                NativeScrapeJob scrape=await client.StartScrapeAsync(new long[]{1},new[]{"steamgriddb"},"balanced","en",false,CancellationToken.None);
                for(int attempt=0;attempt<100&&!scrape.Finished;attempt++){await Task.Delay(20);scrape=await client.ScrapeStatusAsync(scrape,CancellationToken.None);}
                Tests.Check(scrape.State=="completed"&&scrape.Total==1&&scrape.Completed==1&&scrape.LibraryId==info.LibraryId,"scrape typed client creates and polls an actual offline owned backend job");
                NativeScrapeJob cancelled=await client.CancelScrapeAsync(scrape,CancellationToken.None);
                Tests.Check(cancelled.State=="completed"&&cancelled.JobId==scrape.JobId,"scrape cancel is idempotent for a completed review job");
                IList<NativeScrapeJob> recovered=await client.ScrapeJobsAsync(CancellationToken.None);
                Tests.Check(recovered.Count==1&&recovered[0].JobId==scrape.JobId&&!recovered[0].HasDetails,"scrape session list recovers current library job handles without raw provider payloads");
                Tests.Check((await client.ScrapeStatusAsync(recovered[0],CancellationToken.None)).HasDetails,"scrape recovered handle retrieves validated row details");
                Dictionary<string,object> detached=scrape.Data;detached["state"]="corrupted";
                Tests.Check((string)scrape.Data["state"]=="completed","scrape progress DTO returns detached data for UI binding");
                string extensionFile=Path.Combine(root,"_data/cocoon-art.db");byte[] extensionBefore=File.ReadAllBytes(extensionFile);
                NativeMigrationPlan migration=await client.MigrationPlanAsync(CancellationToken.None);
                Tests.Check(migration.CurrentVersion==1&&migration.TargetVersion==2&&!migration.NoChanges&&migration.LibraryId==info.LibraryId,"migration client rehearses actual extension upgrade with library binding");
                Tests.Check(Digest(extensionBefore)==Digest(File.ReadAllBytes(extensionFile)),"migration preview preserves original extension bytes");
                bool confirmationRequired=false;try{await client.ConfirmMigrationAsync(migration,false,CancellationToken.None);}catch(NativeEditorException error){confirmationRequired=error.Code=="MIGRATION_CONFIRM_REQUIRED";}
                Tests.Check(confirmationRequired,"migration client requires reviewed explicit confirmation");
                NativeMigrationRecord migrated=await client.ConfirmMigrationAsync(migration,true,CancellationToken.None);
                Tests.Check(migrated.State=="committed"&&migrated.CurrentVersion==2&&migrated.CanRestore,"migration client publishes rehearsed extension candidate through actual backend");
                Tests.Check((await client.ConfirmMigrationAsync(migration,true,CancellationToken.None)).MigrationId==migrated.MigrationId,"migration client retry recovers the committed transaction");
                IList<NativeMigrationRecord> migrations=await client.MigrationRecordsAsync(CancellationToken.None);
                Tests.Check(migrations.Count==1&&migrations[0].MigrationId==migrated.MigrationId,"migration client lists recoverable owned library records");
                Tests.Check((await client.MigrationStatusAsync(migrated.MigrationId,CancellationToken.None)).CanRestore,"migration client rechecks current restore availability");
                NativeMigrationPlan noop=await client.MigrationPlanAsync(CancellationToken.None);
                Tests.Check(noop.NoChanges&&(await client.ConfirmMigrationAsync(noop,true,CancellationToken.None)).State=="unchanged","migration client handles already-current schema without a second replacement");
                NativeMigrationRecord restoredMigration=await client.RestoreMigrationAsync(migrated,true,CancellationToken.None);
                Tests.Check(restoredMigration.State=="restored"&&!restoredMigration.CanRestore&&Digest(extensionBefore)==Digest(File.ReadAllBytes(extensionFile)),"migration client restores byte-exact original extension without native database writes");
                NativeEditorResult candidates=await client.CandidatesAsync("legacy-item:1",ArtworkSlot.Icon,CancellationToken.None);
                List<object> items=(List<object>)candidates.Data["items"];Tests.Check(items.Count>0,"editor reads actual demo artwork candidate projection");
                string candidate=(string)((Dictionary<string,object>)items[0])["id"];
                NativeEditorResult originals=await client.OriginalsAsync("legacy-item:1",ArtworkSlot.Icon,candidate,CancellationToken.None);
                List<object> originalRows=(List<object>)originals.Data["items"];
                Tests.Check(originalRows.Count>0&&!Json.Flag(originals.Data,"pendingChanged"),"editor actual backend exposes retained original image descriptors without changing drafts");
                string originalHash=(string)((Dictionary<string,object>)originalRows[0])["sha256"];
                NativeOriginalContent originalBytes=await client.DownloadOriginalAsync("legacy-item:1",ArtworkSlot.Icon,originalHash,CancellationToken.None);
                Tests.Check(originalBytes.Sha256==originalHash&&NativeOriginalContent.Digest(originalBytes.Bytes)==originalHash&&originalBytes.LibraryId==info.LibraryId,"editor actual original download verifies complete bytes and library binding");
                ArtworkThumbnail originalPixels=await client.PreviewAsync("legacy-item:1",ArtworkSlot.Icon,originalHash,160,160,ArtworkFit.Contain,null,CancellationToken.None);
                Tests.Check(originalPixels.SourceSha256==originalHash&&originalPixels.Bytes.Length>0,"editor original-byte identity is bound to the actual original preview pixels");
                NativeEditorResult draft=await client.PendingAsync(CancellationToken.None);Tests.Check(Json.Number(draft.Data,"revision",-1)==0,"editor read preview leaves isolated draft unchanged");
                NativeEditorResult layoutPreview=await client.LayoutAsync("legacy-item:1",candidate,new NativeArtworkTarget("fixture-layout",ArtworkSlot.Icon,32,20),
                    new NativeArtworkLayout{Fit=NativeLayoutFit.Contain,FocusX=1,PaddingLeft=4},CancellationToken.None);
                Tests.Check(!Json.Flag(layoutPreview.Data,"pendingChanged")&&layoutPreview.Operation=="layout","editor actual backend creates a layout preview without staging");
                ArtworkThumbnail layoutPixels=await client.PreviewAsync("legacy-item:1",ArtworkSlot.Icon,(string)layoutPreview.Data["blobSha256"],160,160,ArtworkFit.Contain,null,CancellationToken.None);
                Tests.Check(layoutPixels.Bytes.Length>0&&layoutPixels.SourceSha256==(string)layoutPreview.Data["blobSha256"],"editor actual layout preview has retrievable hash-bound pixels");
                NativeEditorUploadResult heroUpload=await client.UploadAsync("legacy-item:1",ArtworkSlot.Hero,ImageBytes(),CancellationToken.None);
                NativeEditorUploadResult logoUpload=await client.UploadAsync("legacy-item:1",ArtworkSlot.Logo,ImageBytes(),CancellationToken.None);
                NativeEditorResult logoScene=await client.LogoPreviewAsync("legacy-item:1",heroUpload.Candidate.Id,logoUpload.Candidate.Id,NativeArtworkScreen.Lower,new NativeLogoPlacement(8,.6,.4),null,CancellationToken.None);
                Tests.Check((string)logoScene.Data["compatibility"]=="EDITOR_APPROXIMATION"&&!Json.Flag(logoScene.Data,"pendingChanged"),"editor actual logo scene returns explicit compatibility boundary and metadata review");
                ArtworkThumbnail logoPixels=await client.PreviewAsync("legacy-item:1",ArtworkSlot.Hero,(string)logoScene.Data["blobSha256"],160,160,ArtworkFit.Contain,null,CancellationToken.None);
                Tests.Check(logoPixels.Bytes.Length>0&&logoPixels.SourceSha256==(string)logoScene.Data["blobSha256"],"editor actual logo scene pixels use the authenticated hero preview route");
                NativeEditorResult logoCroppedScene=await client.LogoPreviewAsync("legacy-item:1",heroUpload.Candidate.Id,logoUpload.Candidate.Id,NativeArtworkScreen.Lower,new NativeLogoPlacement(4,.5,.5),null,
                    new NativeArtworkLayout{CropX=1,CropY=1,CropWidth=8,CropHeight=8},true,CancellationToken.None);
                Tests.Check(!Json.Flag(logoCroppedScene.Data,"pendingChanged"),"editor actual logo crop and staged-selection preview leave draft unchanged");
                NativeEditorResult temporaryLogo=await client.CropAsync("legacy-item:1",ArtworkSlot.Logo,logoUpload.Candidate.Id,1,1,8,8,8,8,CancellationToken.None);
                NativeEditorResult temporaryScene=await client.LogoPreviewVariantsAsync("legacy-item:1",heroUpload.Candidate.Id,logoUpload.Candidate.Id,
                    NativeArtworkScreen.Lower,new NativeLogoPlacement(4,.5,.5),null,(string)temporaryLogo.Data["variantToken"],CancellationToken.None);
                Tests.Check(!Json.Flag(temporaryScene.Data,"pendingChanged"),"editor actual temporary logo token participates in scene without staging");
                ArtworkThumbnail temporaryPixels=await client.PreviewAsync("legacy-item:1",ArtworkSlot.Hero,(string)temporaryScene.Data["blobSha256"],160,160,ArtworkFit.Contain,null,CancellationToken.None);
                Tests.Check(temporaryPixels.Bytes.Length>0,"editor temporary scene pixels remain accessible through owned preview route");
                bool swappedToken=false;try{await client.LogoPreviewVariantsAsync("legacy-item:1",heroUpload.Candidate.Id,logoUpload.Candidate.Id,
                    NativeArtworkScreen.Lower,new NativeLogoPlacement(4,.5,.5),(string)temporaryLogo.Data["variantToken"],null,CancellationToken.None);}
                catch(NativeEditorException error){swappedToken=error.Code=="EDITOR_VARIANT_STALE";}
                Tests.Check(swappedToken,"editor temporary variant from another slot is rejected by actual backend");
                NativeEditorResult logoReset=await client.LogoResetPreviewAsync("legacy-item:1",NativeLogoResetMode.Original,CancellationToken.None);
                Tests.Check(((Dictionary<string,object>)logoReset.Data["changes"]).Count==0,"editor actual original placement reset is empty when no placement override was staged");
                NativeEditorResult cropped=await client.CropAsync("legacy-item:1",ArtworkSlot.Icon,candidate,0,0,16,16,16,16,CancellationToken.None);
                Tests.Check(Json.Flag(cropped.Data,"pendingChanged")==false,"editor crop uses real owned Node worker without automatic stage");
                ArtworkThumbnail pixels=await client.PreviewAsync("legacy-item:1",ArtworkSlot.Icon,candidate,160,160,ArtworkFit.Contain,null,CancellationToken.None);
                using(MemoryStream encoded=new MemoryStream(pixels.Bytes))using(System.Drawing.Bitmap decoded=new System.Drawing.Bitmap(encoded))
                    Tests.Check(decoded.Width==pixels.Width&&pixels.SourceSha256==candidate,"editor actual candidate preview returns decodable pixels bound to its CAS hash");
                ArtworkThumbnail cached=await client.PreviewAsync("legacy-item:1",ArtworkSlot.Icon,candidate,160,160,ArtworkFit.Contain,pixels.ETag,CancellationToken.None);
                Tests.Check(cached.NotModified&&cached.Bytes==null,"editor actual candidate preview supports validated 304 reuse");
                ArtworkThumbnail cropPixels=await client.PreviewAsync("legacy-item:1",ArtworkSlot.Icon,(string)cropped.Data["blobSha256"],160,160,ArtworkFit.Contain,null,CancellationToken.None);
                Tests.Check(cropPixels.Width==16&&cropPixels.Height==16,"editor actual crop preview uses the requested variant without upscaling");
                bool wrongSlot=false;try{await client.PreviewAsync("legacy-item:1",ArtworkSlot.Logo,candidate,160,160,ArtworkFit.Contain,null,CancellationToken.None);}catch(NativeEditorException e){wrongSlot=e.Code=="THUMBNAIL_NOT_FOUND"&&e.Status==404;}
                Tests.Check(wrongSlot,"editor actual preview rejects a blob not owned by the item slot");
                Tests.Check(Json.Number((await client.PendingAsync(CancellationToken.None)).Data,"revision",-1)==0,"editor actual candidate and crop previews never stage changes");
                NativeEditorResult staged=await client.StageAsync("legacy-item:1",ArtworkSlot.Icon,candidate,0,(string)cropped.Data["variantToken"],CancellationToken.None);
                Tests.Check(Json.Number(staged.Data,"revision",-1)==1,"editor stage persists CAS revision through authenticated actual HTTP");
                bool conflict=false;try{await client.StageAsync("legacy-item:1",ArtworkSlot.Icon,candidate,0,null,CancellationToken.None);}catch(NativeEditorException e){conflict=e.Code=="EDITOR_REVISION_CONFLICT"&&e.Status==409;}
                Tests.Check(conflict,"editor actual nested error envelope preserves revision conflict and HTTP409");
                string entry=(string)((Dictionary<string,object>)((List<object>)staged.Data["entries"])[0])["entryId"];
                NativeEditorResult undone=await client.UndoAsync(entry,1,CancellationToken.None);Tests.Check(((List<object>)undone.Data["entries"]).Count==0&&Json.Number(undone.Data,"revision",-1)==2,"editor actual undo removes only explicit pending entry");
                NativeEditorResult preview=await client.MetadataPreviewAsync("legacy-item:1",NativeEditorClient.Map("title","Offline revised title","summary","Unconfirmed summary"),CancellationToken.None);
                NativeEditorResult confirmed=await client.MetadataConfirmAsync((string)preview.Data["previewToken"],new[]{"title"},2,CancellationToken.None);
                Dictionary<string,object> fields=(Dictionary<string,object>)((Dictionary<string,object>)((List<object>)confirmed.Data["entries"])[0])["fields"];
                Tests.Check(fields.Count==1&&(string)fields["title"]=="Offline revised title","editor metadata confirms only reviewed field over actual HTTP");
                bool migrationNeeded=false;try{await client.PreviewChangesAsync(3,CancellationToken.None);}catch(NativeEditorException error){migrationNeeded=error.Code=="EDITOR_MIGRATION_REQUIRED";}
                Tests.Check(migrationNeeded,"native change review requires explicit migration before persistence");
                NativeMigrationPlan upgrade=await client.MigrationPlanAsync(CancellationToken.None);await client.ConfirmMigrationAsync(upgrade,true,CancellationToken.None);
                await client.StageAsync("legacy-item:1",ArtworkSlot.Icon,candidate,3,(string)cropped.Data["variantToken"],CancellationToken.None);
                NativeChangeReview review=await client.PreviewChangesAsync(4,CancellationToken.None);
                Tests.Check(review.IsPreview&&review.ChangeCount==2&&review.Revision==4,"native change client previews metadata and actual cropped CAS bytes");
                bool explicitReview=false;try{await client.ConfirmChangesAsync(review,false,CancellationToken.None);}catch(NativeEditorException error){explicitReview=error.Code=="EDITOR_CONFIRM_REQUIRED";}
                Tests.Check(explicitReview,"native change client requires explicit reviewed confirmation");
                NativeChangeReview submitted=await client.ConfirmChangesAsync(review,true,CancellationToken.None);
                Tests.Check(!submitted.IsPreview&&submitted.ChangeSetId==review.ChangeSetId,"native change client persists the reviewed intent without device execution");
                Tests.Check((await client.ConfirmChangesAsync(review,true,CancellationToken.None)).ReviewId==submitted.ReviewId,"native change client safely retries a committed review");
                IList<NativeChangeReviewHandle> reviews=await client.ChangeReviewsAsync(CancellationToken.None);
                Tests.Check(reviews.Count==1&&(await client.ChangeStatusAsync(reviews[0],CancellationToken.None)).ChangeCount==2,"native change client recovers validated review handles and details");
                NativeChangeReview revoked=await client.RevokeChangesAsync(submitted,true,CancellationToken.None);
                Tests.Check(revoked.State=="REVOKED"&&Json.Number((await client.PendingAsync(CancellationToken.None)).Data,"revision",0)==4,"native change revoke preserves current editor drafts");
                byte[] localImage=ImageBytes();NativeEditorUploadResult uploaded=await client.UploadAsync("legacy-item:1",ArtworkSlot.Icon,localImage,CancellationToken.None);
                Tests.Check(uploaded.Candidate.Width==16&&uploaded.Candidate.Height==16&&uploaded.LibraryId==info.LibraryId,"editor actual owned backend uploads PNG into candidate CAS without applying");
                NativeBackendDiagnostics diagnostics=await client.DiagnosticsAsync(32,CancellationToken.None);Tests.Check(diagnostics.Capacity>=diagnostics.Events.Count&&diagnostics.InstanceId!=Guid.Empty,"editor actual owned backend diagnostics schema matches typed client projection");
                bool sameLibrary=false;try{await client.ArchiveForeignAsync(true,CancellationToken.None);}catch(NativeEditorException e){sameLibrary=e.Code=="EDITOR_DRAFT_SAME_LIBRARY"&&e.Status==409;}
                Tests.Check(sameLibrary,"editor actual archive preserves same-library CAS instead of bypassing revision");
                string payload=NativeEditorJson.Encode(NativeEditorClient.Map("contract","cocoon.native-draft/1","libraryFingerprint",new string('f',64),"revision",9,"entries",new object[0]));
                string foreign=payload.Substring(0,payload.Length-1)+",\"checksum\":\""+Digest(Encoding.UTF8.GetBytes(payload))+"\"}";
                string draftFile=Path.Combine(root,"_staging/native-editor.json");File.WriteAllText(draftFile,foreign,new UTF8Encoding(false));File.WriteAllText(draftFile+".bak",foreign,new UTF8Encoding(false));
                NativeEditorArchiveResult archived=await client.ArchiveForeignAsync(true,CancellationToken.None);
                Tests.Check(archived.PreviousRevision==9&&archived.Revision==0&&archived.LibraryId==info.LibraryId&&File.Exists(Path.Combine(root,"_staging/native-draft-archive",archived.ArchiveId+".json")),"editor actual foreign draft archives owned fixture with preserved original and new revision zero");
                string videoFfmpeg=null;try{videoFfmpeg=VideoExport.Locate();}catch(IOException){}
                if(!string.IsNullOrEmpty(videoFfmpeg)&&File.Exists(videoFfmpeg))
                {
                    string videoFile=Path.Combine(root,"fixture-video.mp4");
                    using(var runner=new ProcessRunner())
                    {var made=await runner.RunAsync(new ProcessRequest(videoFfmpeg,"fixture.video",new[]{"-v","error","-f","lavfi","-i","color=red:size=32x16:rate=10","-t","1","-threads","1","-c:v","libx264",videoFile}),TimeSpan.FromSeconds(15),CancellationToken.None);Tests.Check(made.Outcome==ProcessOutcome.Success&&made.CleanupComplete,"video fixture encoder completes with owned process cleanup");await runner.StopAllAsync();}
                    var videoUpload=await client.UploadVideoAsync("legacy-item:1",File.ReadAllBytes(videoFile),CancellationToken.None);string videoId=(string)videoUpload.Video["id"];
                    Tests.Check(videoUpload.LibraryId==info.LibraryId&&(string)videoUpload.Video["deviceCompatibility"]=="UNVERIFIED","editor actual upload validates original video without device compatibility promotion");
                    var videoList=await client.VideosAsync("legacy-item:1",CancellationToken.None);Tests.Check(((List<object>)videoList.Data["items"]).Count==1,"editor actual video query restores imported video identity");
                    var poster=await client.VideoPosterAsync("legacy-item:1",videoId,500,CancellationToken.None);var posterData=(Dictionary<string,object>)poster.Data["video"];
                    var posterPixels=await client.PreviewAsync("legacy-item:1",ArtworkSlot.Hero,(string)posterData["posterBlobSha256"],160,160,ArtworkFit.Contain,null,CancellationToken.None);
                    Tests.Check(posterPixels.Bytes.Length>0&&Json.Number(posterData,"posterAtMs",-1)==500,"editor actual video seek creates retrievable poster pixels");
                    await client.StageAsync("legacy-item:1",ArtworkSlot.Hero,videoId,0,null,CancellationToken.None);
                    var videoReview=await client.PreviewChangesAsync(1,CancellationToken.None);
                    Tests.Check(((List<object>)videoReview.Data["blockerCodes"]).Contains("VIDEO_PROFILE_UNVERIFIED"),"editor actual video draft review retains codec compatibility blocker");
                    string exported=await client.ExportOriginalAsync("legacy-item:1",ArtworkSlot.Hero,videoId,Path.Combine(root,"exported-video.mp4"),CancellationToken.None);
                    Tests.Check(Digest(File.ReadAllBytes(exported))==videoId,"editor actual video export preserves byte-exact original without a URL token");
                    NativeVideoPlaybackSession playback=await NativeVideoPlaybackSession.StartAsync(client,"legacy-item:1",videoId,root,videoFfmpeg,CancellationToken.None);
                    Exception playbackFailure=null;
                    try
                    {
                        for(int wait=0;wait<200&&!playback.Playback.Ready&&playback.Playback.Error==null;wait++)await Task.Delay(25);
                        Tests.Check(playback.Playback.Ready&&playback.Playback.Error==null,"editor original video initializes existing owned playback engine");
                        playback.Playback.Play();PlaybackFrame frame=null;
                        for(int wait=0;wait<200&&frame==null;wait++){frame=playback.Playback.TakeLatest();if(frame==null)await Task.Delay(25);}
                        using(frame)Tests.Check(frame!=null&&frame.Upper!=null,"editor downloaded video produces an actual decoded playback frame");
                    }catch(Exception error){playbackFailure=error;}
                    await playback.StopAsync();if(playbackFailure!=null)throw playbackFailure;
                    Tests.Check(!File.Exists(playback.LocalPath),"editor playback removes only its verified local source after decoder shutdown");
                }else Console.WriteLine("SKIP: actual video integration requires configured host FFmpeg and FFprobe");
                Tests.Check(Convert.ToBase64String(before)==Convert.ToBase64String(File.ReadAllBytes(db)),"editor isolated workflow preserves original demo Cocoon database bytes");
            }
            finally{backend.Stop();Wait(backend,BackendState.Stopped).GetAwaiter().GetResult();backend.Dispose();}
            Tests.Check(backend.State==BackendState.Stopped,"editor integration closes only the backend instance it owns");
        }
    }
}
