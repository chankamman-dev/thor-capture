using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace ThorCapture
{
    internal sealed class NativeVideoUploadResult
    {
        public readonly string InstanceId,LibraryId;
        private readonly string video;
        public Dictionary<string,object> Video {get{return (Dictionary<string,object>)NativeEditorJson.Parse(video);}}
        internal NativeVideoUploadResult(NativeEditorInfo info,object value){InstanceId=info.InstanceId;LibraryId=info.LibraryId;video=NativeEditorJson.Encode(value);}
    }
    internal sealed partial class NativeEditorHttpTransport
    {
        internal Task<NativeEditorHttpReply> UploadVideoAsync(string instance,string library,string item,Stream source,long length,CancellationToken token)
        {
            Guid parsed;if(!Guid.TryParseExact(instance,"D",out parsed)||parsed==Guid.Empty||!Guid.TryParseExact(library,"D",out parsed)||parsed==Guid.Empty
                ||!Regex.IsMatch(item??"",@"\A(?:legacy-item|unresolved-native):[1-9][0-9]{0,14}\z")||source==null||!source.CanRead||length<1||length>32L*1024*1024)throw new ArgumentException("Invalid video upload");
            return SendAsync("/api/native-editor/upload-video?instanceId="+instance+"&libraryId="+library+"&itemId="+Uri.EscapeDataString(item),false,source,length,false,true,token,45000);
        }
    }
    internal sealed partial class NativeEditorClient
    {
        public Task<NativeEditorResult> VideosAsync(string itemId,CancellationToken token){return Invoke("videos",Map("itemId",Item(itemId)),token);}
        public async Task<NativeEditorResult> VideoPosterAsync(string itemId,string videoId,int atMs,CancellationToken token)
        {
            if(atMs<0||atMs>=3600000)throw new ArgumentException("Invalid poster timestamp");
            var result=await Invoke("videoPoster",Map("itemId",Item(itemId),"videoId",Id(videoId),"atMs",atMs),token).ConfigureAwait(false);
            if(Text(result.Data["video"],"id")!=videoId||Integer(result.Data["video"],"posterAtMs",0,3599999)!=atMs)throw Invalid();return result;
        }
        public Task<NativeVideoUploadResult> UploadVideoAsync(string itemId,byte[] bytes,CancellationToken token)
        {
            Item(itemId);if(bytes==null||bytes.Length<1||bytes.Length>32*1024*1024)throw new ArgumentException("Invalid video length");
            return UploadVideoBytes(itemId,(byte[])bytes.Clone(),token);
        }
        private async Task<NativeVideoUploadResult> UploadVideoBytes(string itemId,byte[] bytes,CancellationToken token)
        {using(var source=new MemoryStream(bytes,false))return await UploadVideoAsync(itemId,source,bytes.Length,token).ConfigureAwait(false);}
        public async Task<NativeVideoUploadResult> UploadVideoAsync(string itemId,Stream source,long length,CancellationToken token)
        {
            Item(itemId);if(source==null||!source.CanRead||length<1||length>32L*1024*1024)throw new ArgumentException("Invalid video source");
            NativeEditorInfo snapshot;Uri address;Snapshot(out snapshot,out address);var transport=Capture(address);await VerifyContext(transport,snapshot,address,token).ConfigureAwait(false);
            var reply=await transport.UploadVideoAsync(snapshot.InstanceId,snapshot.LibraryId,itemId,source,length,token).ConfigureAwait(false);CheckContext(snapshot,address);ThrowFailure(reply,snapshot);
            var value=Obj(reply.Value,"contract","instanceId","libraryId","deviceWritesEnabled","previewAppliesAutomatically","ok","data");
            if(reply.Status!=201||value.Count!=7||!Flag(value,"ok"))throw Invalid(reply.Status);ValidateBinding(value,snapshot);
            var data=Obj(Json.Get(value,"data"),"itemId","video","pendingChanged");ValidateVideos("videoPoster",data);if(Text(data,"itemId")!=itemId)throw Invalid();
            await VerifyContext(transport,snapshot,address,token).ConfigureAwait(false);return new NativeVideoUploadResult(snapshot,data["video"]);
        }
        private static void ValidateVideo(object value)
        {
            var d=Obj(value,"id","sourceBlobSha256","mime","byteSize","width","height","durationMs","codec","posterBlobSha256","posterAtMs","deviceCompatibility");
            if(d.Count!=11)throw Invalid();Hash(d,"id");Hash(d,"sourceBlobSha256");Hash(d,"posterBlobSha256");
            if(Text(d,"id")!=Text(d,"sourceBlobSha256")||Text(d,"deviceCompatibility")!="UNVERIFIED"||Array.IndexOf(new[]{"video/mp4","video/webm"},Text(d,"mime"))<0
                ||!Regex.IsMatch(Text(d,"codec"),@"\A[a-z0-9_]{1,48}\z"))throw Invalid();
            Integer(d,"byteSize",1,32L*1024*1024);long w=Integer(d,"width",1,8192),h=Integer(d,"height",1,8192),duration=Integer(d,"durationMs",1,3600000);
            if(w*h>16L*1024*1024)throw Invalid();Integer(d,"posterAtMs",0,duration-1);
        }
        private static void ValidateVideos(string operation,Dictionary<string,object> d)
        {
            Obj(d,operation=="videos"?new[]{"itemId","items","pendingChanged"}:new[]{"itemId","video","pendingChanged"});
            if(d.Count!=3||Flag(d,"pendingChanged"))throw Invalid();Item(Text(d,"itemId"));
            if(operation=="videos") {
                var rows=Json.Get(d,"items")as List<object>;if(rows==null||rows.Count>32)throw Invalid();var seen=new HashSet<string>();
                foreach(var row in rows){ValidateVideo(row);if(!seen.Add(Text(row,"id")))throw Invalid();}
            }else ValidateVideo(Json.Get(d,"video"));
        }
    }
}
