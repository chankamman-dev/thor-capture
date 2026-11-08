using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal static class MediaAudio
    {
        internal static string Run(string exe,string arguments,int timeout,CancellationToken cancel)
        {
            using(Process process=new Process { StartInfo=new ProcessStartInfo(exe,arguments) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true } })
            {
                process.Start();
                using(cancel.Register(delegate { try { if(!process.HasExited) process.Kill(); } catch { } }))
                {
                    Task<string> output=Task.Run(()=>process.StandardOutput.ReadToEnd()),errors=Task.Run(()=>process.StandardError.ReadToEnd());
                    if(!process.WaitForExit(timeout)) { try { process.Kill(); } catch { } throw new IOException("声音处理超时"); }
                    cancel.ThrowIfCancellationRequested();
                    if(process.ExitCode!=0) throw new IOException("声音处理失败："+errors.Result);
                    return output.Result;
                }
            }
        }
        internal static bool HasAudio(string ffmpeg,string source,CancellationToken cancel,bool restrictedVideo=false)
        {
            string output=Run(Path.Combine(Path.GetDirectoryName(ffmpeg),"ffprobe.exe"),"-v error -select_streams a:0 -show_entries stream=codec_type -of csv=p=0 "+VideoInputPolicy.Options(source,restrictedVideo)+" "+VideoExport.Quote(source),15000,cancel);
            return output.Trim()=="audio";
        }
        internal static string Select(string ffmpeg,string source,string peer,CancellationToken cancel,bool restrictedVideo=false)
        {
            if(HasAudio(ffmpeg,source,cancel,restrictedVideo)) return source;
            return peer!=null&&HasAudio(ffmpeg,peer,cancel,restrictedVideo)?peer:null;
        }
        internal static void Attach(string video,string wave,double trim)
        {
            string ffmpeg=VideoExport.Locate(); int w,h; double duration;
            VideoExport.Describe(Path.Combine(Path.GetDirectoryName(ffmpeg),"ffprobe.exe"),video,CancellationToken.None,out w,out h,out duration);
            string partial=video+".audio-"+Guid.NewGuid().ToString("N")+".partial.mp4";
            try
            {
                // Copy video packets unchanged. Pad the common audio to each track's own duration.
                Run(ffmpeg,"-v error -i "+VideoExport.Quote(video)+" -ss "+trim.ToString("0.000000",CultureInfo.InvariantCulture)+" -i "+VideoExport.Quote(wave)
                    +" -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 192k -af apad -t "+duration.ToString("0.000000",CultureInfo.InvariantCulture)
                    +" -movflags +faststart -y "+VideoExport.Quote(partial),60000,CancellationToken.None);
                Recording.ValidateMp4(partial);
                if(!HasAudio(ffmpeg,partial,CancellationToken.None)) throw new IOException("合成文件缺少音轨");
                File.Replace(partial,video,null);
            }
            finally { if(File.Exists(partial)) File.Delete(partial); }
        }
    }
}
