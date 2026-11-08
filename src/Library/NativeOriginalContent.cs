using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal sealed class NativeOriginalContent
    {
        private readonly byte[] bytes;
        public readonly string Sha256,Mime,InstanceId,LibraryId;
        public long ByteSize {get{return bytes.Length;}}
        public byte[] Bytes {get{return (byte[])bytes.Clone();}}
        private NativeOriginalContent(byte[] value,string hash,string mime,string instance,string library)
        {bytes=value;Sha256=hash;Mime=mime;InstanceId=instance;LibraryId=library;}
        internal static string Digest(byte[] value)
        {using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(value)).Replace("-","").ToLowerInvariant();}
        internal static NativeOriginalContent Read(Stream stream,long length,string hash,string mime,string instance,string library,CancellationToken token)
        {
            if(length<1||length>32L*1024*1024||!Regex.IsMatch(hash??"",@"\A[a-f0-9]{64}\z")
                ||Array.IndexOf(new[]{"image/png","image/jpeg","image/webp","image/gif","video/mp4","video/webm"},mime)<0)throw NativeEditorClient.Invalid();
            byte[] data=new byte[(int)length];int offset=0;
            while(offset<data.Length){token.ThrowIfCancellationRequested();int read=stream.Read(data,offset,Math.Min(1024*1024,data.Length-offset));if(read==0)throw NativeEditorClient.Invalid();offset+=read;}
            token.ThrowIfCancellationRequested();if(stream.ReadByte()!=-1||Digest(data)!=hash)throw NativeEditorClient.Invalid();
            return new NativeOriginalContent(data,hash,mime,instance,library);
        }
        internal void Write(Stream stream,CancellationToken token)
        {for(int offset=0;offset<bytes.Length;offset+=1024*1024){token.ThrowIfCancellationRequested();stream.Write(bytes,offset,Math.Min(1024*1024,bytes.Length-offset));}}
        internal string Extension {get{return Mime=="image/jpeg"?"jpg":Mime.Substring(Mime.IndexOf('/')+1);}}
    }
    internal sealed partial class NativeEditorHttpTransport
    {
        internal Task<NativeOriginalContent> OriginalAsync(string path,string hash,string instance,string library,CancellationToken cancellation)
        {
            Uri target;if(path==null||!path.StartsWith("/api/native-editor/content?",StringComparison.Ordinal)||!Uri.TryCreate(root,path,out target)
                ||target.Authority!=root.Authority||target.Scheme!=root.Scheme||target.Fragment.Length!=0)throw new ArgumentException("Invalid original route");
            return Task.Run(delegate
            {
                using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    deadline.CancelAfter(30000);CheckThumbnailContext(cancellation,deadline.Token);
                    var request=(HttpWebRequest)WebRequest.Create(target);request.Method="GET";request.Timeout=request.ReadWriteTimeout=30000;
                    request.AllowAutoRedirect=false;request.Proxy=null;request.ServicePoint.ConnectionLimit=4;request.Headers["X-Cocoon-Art-Token"]=token;
                    using(deadline.Token.Register(request.Abort))
                    {
                        try
                        {
                            NativeOriginalContent result;
                            using(var response=(HttpWebResponse)request.GetResponse())
                            {
                                if(response.StatusCode!=HttpStatusCode.OK||response.Headers["X-Cocoon-Original-Contract"]!="cocoon.original-content/1"
                                    ||response.Headers["X-Original-SHA256"]!=hash||response.Headers["X-Cocoon-Editor-Instance"]!=instance||response.Headers["X-Cocoon-Library-Id"]!=library
                                    ||response.Headers["Content-Encoding"]!=null||response.Headers["Content-Range"]!=null)throw NativeEditorClient.Invalid();
                                using(var stream=response.GetResponseStream())result=NativeOriginalContent.Read(stream,response.ContentLength,hash,response.ContentType,instance,library,deadline.Token);
                            }
                            CheckThumbnailContext(cancellation,deadline.Token);return result;
                        }
                        catch(WebException failure){CheckThumbnailContext(cancellation,deadline.Token);throw CocoonBackend.RequestFailure(failure);}
                        catch(Exception failure){CheckThumbnailContext(cancellation,deadline.Token);if(failure is IOException||failure is ObjectDisposedException)throw new BackendRequestException("BACKEND_REQUEST_FAILED",0);throw;}
                    }
                }
            },cancellation);
        }
    }
    internal sealed partial class NativeEditorClient
    {
        public async Task<NativeOriginalContent> DownloadOriginalAsync(string itemId,ArtworkSlot slot,string sha256,CancellationToken token)
        {
            string item=Item(itemId),name=Slot(slot);if(!Regex.IsMatch(sha256??"",@"\A[a-f0-9]{64}\z"))throw new ArgumentException("Invalid original hash");
            NativeEditorInfo snapshot;Uri address;Snapshot(out snapshot,out address);var transport=Capture(address);await VerifyContext(transport,snapshot,address,token).ConfigureAwait(false);
            string path="/api/native-editor/content?instanceId="+snapshot.InstanceId+"&libraryId="+snapshot.LibraryId+"&itemId="+Uri.EscapeDataString(item)+"&slot="+name+"&blob="+sha256;
            NativeOriginalContent result;
            try{result=await transport.OriginalAsync(path,sha256,snapshot.InstanceId,snapshot.LibraryId,token).ConfigureAwait(false);}
            catch(BackendRequestException failure){throw new NativeEditorException(failure.Code=="BACKEND_SESSION_STALE"?"EDITOR_SESSION_STALE":failure.Code=="BACKEND_TIMEOUT"?"EDITOR_TIMEOUT":failure.Code,failure.Status);}
            CheckContext(snapshot,address);await VerifyContext(transport,snapshot,address,token).ConfigureAwait(false);return result;
        }
        public async Task<string> ExportOriginalAsync(string itemId,ArtworkSlot slot,string sha256,string destination,CancellationToken token)
        {
            var content=await DownloadOriginalAsync(itemId,slot,sha256,token).ConfigureAwait(false);
            return await Task.Run(()=>NativeOriginalFiles.Publish(content,destination,token),token).ConfigureAwait(false);
        }
    }
}
