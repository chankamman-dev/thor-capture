using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture
{
    internal static class ThumbnailSessionTests
    {
        private static readonly string Blob = new string('a', 64);
        private static readonly string ETag = "\"cth1-" + new string('b', 64) + "\"";
        private const string Route = "/api/art/thumbnail?id=1&type=icon&width=16&height=16&fit=contain&source=current&ext=png";

        private sealed class Fixture : IDisposable
        {
            private readonly Process child;
            private int current = 1;
            public readonly Uri Endpoint;
            public readonly ManualResetEvent Received = new ManualResetEvent(false);
            public bool Current { get { return Interlocked.CompareExchange(ref current, 0, 0) == 1; } set { Interlocked.Exchange(ref current, value ? 1 : 0); } }
            public Fixture(string mode)
            {
                string dir = Path.Combine(Tests.Root, "thumbnail-session-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
                int size = mode == "oversize" ? 32 : 16;
                using (Bitmap png = new Bitmap(size, size)) { png.SetPixel(0, 0, Color.Red); png.Save(Path.Combine(dir, "image.png"), ImageFormat.Png); }
                string script = Path.Combine(dir, "fixture.cjs");
                File.WriteAllText(script, @"const http=require('node:http'),fs=require('node:fs'),path=require('node:path');
const mode=process.argv[2],png=fs.readFileSync(path.join(__dirname,'image.png'));
const id='11111111-1111-4111-8111-111111111111',library='22222222-2222-4222-8222-222222222222';
let pending=null,switched=false,stopping=false;
const server=http.createServer((req,res)=>{
 req.on('error',()=>{});res.on('error',()=>{});
 const json=(value,status=200)=>{res.writeHead(status,{'Content-Type':'application/json'});res.end(JSON.stringify(value));};
 if(req.headers['x-cocoon-art-token']!=='fixture-token'||req.url.includes('token'))return json({code:'FORBIDDEN'},403);
 if(req.url==='/api/native-editor')return json({contract:'cocoon.native-editor/1',instanceId:switched?'44444444-4444-4444-8444-444444444444':id,libraryId:switched?'55555555-5555-4555-8555-555555555555':library,deviceWritesEnabled:false,previewAppliesAutomatically:false});
 const query=new URL(req.url,'http://127.0.0.1').searchParams;
 if(req.url.startsWith('/api/native-editor/preview?')&&(query.get('instanceId')!==id||query.get('itemId')!=='legacy-item:1'||query.get('slot')!=='icon'||query.get('blob')!=='a'.repeat(64)))return json({code:'EDITOR_PARAMETERS_INVALID'},400);
 const send=()=>{
   if(mode==='hold-error')return json({code:'THUMBNAIL_NOT_FOUND'},404);
   if(mode==='redirect'){res.writeHead(302,{Location:'http://invalid.example/secret'});return res.end();}
   const size=mode==='oversize'?32:16;
   const headers={'Content-Type':'image/png','X-Cocoon-Thumbnail-Contract':'cocoon.thumbnail/1','X-Thumbnail-Width':size,'X-Thumbnail-Height':size,'X-Thumbnail-Source-SHA256':(mode.startsWith('hash')?'c':'a').repeat(64),'X-Thumbnail-Cache':'memory',ETag:'""cth1-'+'b'.repeat(64)+'""'};
   if(mode==='switch')switched=true;
   if(req.headers['if-none-match']){res.writeHead(304,headers);return res.end();}
   res.writeHead(200,{...headers,'Content-Length':png.length});
   if(mode==='body'){res.write(png.subarray(0,33));console.log('RECEIVED');return;}
   res.end(png);
 };
 if(mode.startsWith('hold')){pending=send;console.log('RECEIVED');}else send();
});
server.listen(0,'127.0.0.1',()=>console.log(server.address().port));
require('node:readline').createInterface({input:process.stdin}).on('line',raw=>{const line=raw.replace(/^﻿/,'').trim();
 if(line==='release'&&pending){const send=pending;pending=null;send();}
 if(line==='stop'&&!stopping){stopping=true;server.close(()=>process.exit(0));server.closeAllConnections();}
});
", new UTF8Encoding(false));
                ProcessStartInfo start = new ProcessRequest("node", "fixture.thumbnail-session", new[] { script, mode }).StartInfo(); start.RedirectStandardInput = true;
                int port = 0;
                using (ManualResetEvent ready = new ManualResetEvent(false))
                {
                    child = new Process { StartInfo = start };
                    child.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args) { int number; if (args.Data == "RECEIVED") Received.Set(); if (int.TryParse(args.Data, out number)) { port = number; ready.Set(); } };
                    child.ErrorDataReceived += delegate { };
                    child.Start(); child.BeginOutputReadLine(); child.BeginErrorReadLine();
                    if (!ready.WaitOne(10000) || port == 0) throw new Exception("Thumbnail HTTP fixture failed to start");
                }
                Endpoint = new Uri("http://127.0.0.1:" + port + "/");
            }
            public NativeEditorHttpTransport Transport() { return new NativeEditorHttpTransport(Endpoint, "fixture-token", () => Current); }
            public NativeEditorClient Client() { return new NativeEditorClient(() => Endpoint, Transport); }
            public void Release() { child.StandardInput.WriteLine("release"); child.StandardInput.Flush(); }
            public void AwaitRequest() { if (!Received.WaitOne(5000)) throw new Exception("Thumbnail request did not reach barrier"); }
            public void Dispose()
            {
                Current = false;
                try
                {
                    if (!child.HasExited) { child.StandardInput.WriteLine("stop"); child.StandardInput.Flush(); if (!child.WaitForExit(5000)) { child.Kill(); child.WaitForExit(); throw new Exception("Owned thumbnail fixture failed graceful shutdown"); } }
                    child.WaitForExit();
                }
                finally { child.Dispose(); Received.Dispose(); }
            }
        }

        internal static void Run() { Task.Run((Func<Task>)Checks).GetAwaiter().GetResult(); }
        private static Task<ArtworkThumbnail> Preview(NativeEditorClient client, CancellationToken token, string etag = null)
        { return client.PreviewAsync("legacy-item:1", ArtworkSlot.Icon, Blob, 16, 16, ArtworkFit.Contain, etag, token); }
        private static async Task Checks()
        {
            using (Fixture fixture = new Fixture("good"))
            {
                NativeEditorHttpTransport transport = fixture.Transport();
                Tests.Reject(() => transport.ThumbnailAsync("https://invalid.example/", null, CancellationToken.None), "thumbnail transport rejects external routes before HTTP");
                Tests.Reject(() => transport.ThumbnailAsync(Route, "bad", CancellationToken.None), "thumbnail transport rejects malformed validators before HTTP");
                ArtworkThumbnail image = await transport.ThumbnailAsync(Route, null, CancellationToken.None);
                using (MemoryStream bytes = new MemoryStream(image.Bytes)) using (Bitmap decoded = new Bitmap(bytes))
                    Tests.Check(decoded.Width == 16 && decoded.GetPixel(0, 0).R == 255, "captured thumbnail transport authenticates and returns actual PNG pixels");
                ArtworkThumbnail same = await transport.ThumbnailAsync(Route, image.ETag, CancellationToken.None);
                Tests.Check(same.NotModified && same.Bytes == null && same.ETag == image.ETag, "captured thumbnail transport preserves valid 304 cache reuse");
                fixture.Current = false; bool stale = false;
                try { await transport.ThumbnailAsync(Route, null, CancellationToken.None); } catch (BackendRequestException failure) { stale = failure.Code == "BACKEND_SESSION_STALE"; }
                Tests.Check(stale, "queued thumbnail transport cannot retarget an invalidated owner");
            }
            foreach (string mode in new[] { "hold", "hold304", "hold-error" })
            using (Fixture fixture = new Fixture(mode))
            {
                Task<ArtworkThumbnail> pending = fixture.Transport().ThumbnailAsync(Route, mode == "hold304" ? ETag : null, CancellationToken.None);
                fixture.AwaitRequest(); fixture.Current = false; fixture.Release(); bool stale = false;
                try { await pending; } catch (BackendRequestException failure) { stale = failure.Code == "BACKEND_SESSION_STALE" && failure.Status == 409; }
                Tests.Check(stale, "late thumbnail " + mode + " response is rejected after owner invalidation");
            }
            using (Fixture fixture = new Fixture("body"))
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                Task<ArtworkThumbnail> pending = fixture.Transport().ThumbnailAsync(Route, null, cancel.Token); fixture.AwaitRequest();
                Stopwatch elapsed = Stopwatch.StartNew(); cancel.Cancel(); bool observed = false;
                try { await pending; } catch (OperationCanceledException) { observed = true; }
                Tests.Check(observed && elapsed.ElapsedMilliseconds < 1500, "thumbnail cancellation interrupts an incomplete PNG body with a cancellation result");
            }
            using (Fixture fixture = new Fixture("body"))
            {
                Stopwatch elapsed = Stopwatch.StartNew(); bool timeout = false;
                try { await fixture.Transport().ThumbnailAsync(Route, null, CancellationToken.None); } catch (BackendRequestException failure) { timeout = failure.Code == "BACKEND_TIMEOUT"; }
                Tests.Check(timeout && elapsed.ElapsedMilliseconds < 20000, "thumbnail whole-request deadline interrupts an incomplete PNG body");
            }
            using (Fixture fixture = new Fixture("good"))
            {
                NativeEditorClient client = fixture.Client(); bool disconnected = false;
                try { await Preview(client, CancellationToken.None); } catch (NativeEditorException failure) { disconnected = failure.Code == "EDITOR_NOT_CONNECTED"; }
                Tests.Check(disconnected, "native preview requires the explicit editor handshake");
                await client.ConnectAsync(CancellationToken.None);
                ArtworkThumbnail image = await Preview(client, CancellationToken.None);
                Tests.Check(image.SourceSha256 == Blob && image.Width == 16, "native preview binds item slot source hash and editor session");
                Tests.Check((await Preview(client, CancellationToken.None, image.ETag)).NotModified, "native preview accepts matching conditional cache response");
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    cancel.Cancel(); bool observed = false;
                    try { await Preview(client, cancel.Token); } catch (OperationCanceledException) { observed = true; }
                    Tests.Check(observed, "native preview respects cancellation before HTTP");
                }
            }
            foreach (string mode in new[] { "switch", "hash", "hash304", "oversize", "redirect" })
            using (Fixture fixture = new Fixture(mode))
            {
                NativeEditorClient client = fixture.Client(); await client.ConnectAsync(CancellationToken.None); bool rejected = false;
                try { await Preview(client, CancellationToken.None, mode == "hash304" ? ETag : null); }
                catch (NativeEditorException failure) { rejected = failure.Code == (mode == "switch" ? "EDITOR_SESSION_STALE" : mode == "redirect" ? "BACKEND_RESPONSE_INVALID" : "EDITOR_RESPONSE_INVALID"); }
                Tests.Check(rejected, "native preview rejects " + mode + " response before returning pixels");
            }
            using (Fixture fixture = new Fixture("hold"))
            {
                NativeEditorClient client = fixture.Client(); await client.ConnectAsync(CancellationToken.None);
                Task<ArtworkThumbnail> pending = Preview(client, CancellationToken.None); fixture.AwaitRequest();
                await client.ConnectAsync(CancellationToken.None); fixture.Release(); bool stale = false;
                try { await pending; } catch (NativeEditorException failure) { stale = failure.Code == "EDITOR_SESSION_STALE"; }
                Tests.Check(stale, "reconnecting the editor rejects a preview from its prior binding even at the same endpoint");
            }
        }
    }
}
