using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
namespace ThorCapture
{
    internal static class NativeOriginalFiles
    {
        internal static void Plain(string path)
        {
            string current=Path.GetFullPath(path);
            while(current!=null){if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new NativeEditorException("EDITOR_EXPORT_PATH_UNSAFE",400);current=Path.GetDirectoryName(current);}
        }
        internal static bool Matches(string path,NativeOriginalContent content)
        {
            try{Plain(path);using(var stream=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var hash=SHA256.Create())
                return stream.Length==content.ByteSize&&BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant()==content.Sha256;}
            catch{return false;}
        }
        internal static string Publish(NativeOriginalContent content,string destination,CancellationToken token)
        {
            if(content==null||string.IsNullOrWhiteSpace(destination)||!Path.IsPathRooted(destination))throw new ArgumentException("Invalid export destination");
            string target=Path.GetFullPath(destination),parent=Path.GetDirectoryName(target),temp=null;bool created=false;
            try
            {
                token.ThrowIfCancellationRequested();Plain(target);if(!Directory.Exists(parent))throw new NativeEditorException("EDITOR_EXPORT_DIRECTORY_MISSING",400);
                if(File.Exists(target)||Directory.Exists(target))throw new NativeEditorException("EDITOR_EXPORT_EXISTS",409);
                temp=Path.Combine(parent,".thor-original-"+Guid.NewGuid().ToString("N")+".tmp");
                using(var stream=File.Open(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){created=true;content.Write(stream,token);stream.Flush(true);}
                if(!Matches(temp,content))throw new NativeEditorException("EDITOR_EXPORT_VERIFY_FAILED",409);
                token.ThrowIfCancellationRequested();Plain(target);File.Move(temp,target);created=false;return target;
            }
            catch(OperationCanceledException){throw;}
            catch(NativeEditorException){throw;}
            catch{throw new NativeEditorException("EDITOR_EXPORT_FAILED",500);}
            finally{if(created&&temp!=null){try{if(Matches(temp,content))File.Delete(temp);}catch{}}}
        }
    }
    // Stop the owned decoder before releasing its local source file.
    internal sealed class NativeVideoPlaybackSession
    {
        public readonly VideoPlayback Playback;
        public readonly string LocalPath;
        private readonly NativeOriginalContent content;
        private readonly object sync=new object();private Task stop;
        private NativeVideoPlaybackSession(string path,NativeOriginalContent original,string ffmpeg)
        {LocalPath=path;content=original;Playback=new VideoPlayback(path,null,ffmpeg,true);}
        public static async Task<NativeVideoPlaybackSession> StartAsync(NativeEditorClient client,string itemId,string videoSha256,string cacheDirectory,string ffmpeg,CancellationToken token)
        {
            if(client==null||!Path.IsPathRooted(cacheDirectory??"")||!Directory.Exists(cacheDirectory)||!Path.IsPathRooted(ffmpeg??"")||!File.Exists(ffmpeg))throw new ArgumentException("Invalid playback configuration");
            var original=await client.DownloadOriginalAsync(itemId,ArtworkSlot.Hero,videoSha256,token).ConfigureAwait(false);
            if(original.Mime!="video/mp4"&&original.Mime!="video/webm")throw new NativeEditorException("EDITOR_VIDEO_INVALID",422);
            string path=Path.Combine(Path.GetFullPath(cacheDirectory),"cocoon-video-"+Guid.NewGuid().ToString("N")+"."+original.Extension);
            await Task.Run(()=>NativeOriginalFiles.Publish(original,path,token),token).ConfigureAwait(false);
            try{token.ThrowIfCancellationRequested();return new NativeVideoPlaybackSession(path,original,ffmpeg);}
            catch{if(NativeOriginalFiles.Matches(path,original))File.Delete(path);throw;}
        }
        public Task StopAsync(){lock(sync){if(stop==null)stop=StopCore();return stop;}}
        private async Task StopCore()
        {
            await Playback.StopAsync().ConfigureAwait(false);
            try{if(NativeOriginalFiles.Matches(LocalPath,content))File.Delete(LocalPath);}catch{}
        }
    }
}
