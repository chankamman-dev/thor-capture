using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal static class VideoExport
    {
        internal static string Quote(string path) { return "\""+path.Replace("\"", "")+"\""; }
        internal static string Locate()
        {
            string[] candidates={Environment.GetEnvironmentVariable("THOR_FFMPEG"),Path.Combine(Adb.AppDir(),"ffmpeg.exe")};
            foreach(string candidate in candidates) if(!string.IsNullOrEmpty(candidate)&&File.Exists(candidate)) return candidate;
            string config=Path.Combine(Adb.AppDir(),"ffmpeg-path.txt");
            if(File.Exists(config)) { string path=File.ReadAllText(config).Trim(); if(File.Exists(path)) return path; }
            throw new IOException("请在程序旁的 ffmpeg-path.txt 中填写 ffmpeg.exe 路径（同目录需有 ffprobe.exe），或设置 THOR_FFMPEG。");
        }
        private static Process Create(string exe,string arguments,bool input)
        {
            return new Process { StartInfo=new ProcessStartInfo(exe,arguments) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardInput=input,RedirectStandardOutput=!input } };
        }
        private static void Kill(Process process) { try { if(process!=null&&!process.HasExited) process.Kill(); } catch { } }

        public static void Export(string source,string destination,DeviceSkin skin,double offsetMs,Action<int> progress,CancellationToken cancel)
        { Export(source,null,destination,skin,offsetMs,progress,cancel); }

        internal static void Describe(string probe,string source,CancellationToken cancel,out int width,out int height,out double duration,bool restrictedVideo=false)
        {
            width=0; height=0; duration=0;
            using(Process inspect=Create(probe,"-v error -select_streams v:0 -show_entries stream=width,height:format=duration -of default=noprint_wrappers=1 "+VideoInputPolicy.Options(source,restrictedVideo)+" "+Quote(source),false))
            {
                inspect.Start();
                using(cancel.Register(()=>Kill(inspect)))
                {
                    Task<string> output=Task.Run(()=>inspect.StandardOutput.ReadToEnd());
                    Task<string> errors=Task.Run(()=>inspect.StandardError.ReadToEnd());
                    if(!inspect.WaitForExit(15000)) { Kill(inspect); throw new IOException("视频信息读取超时"); }
                    cancel.ThrowIfCancellationRequested();
                    if(inspect.ExitCode!=0) throw new IOException("无法读取视频："+errors.Result);
                    foreach(string row in output.Result.Split('\n'))
                    {
                        string[] pair=row.Trim().Split('='); if(pair.Length!=2) continue;
                        if(pair[0]=="width") int.TryParse(pair[1],out width);
                        if(pair[0]=="height") int.TryParse(pair[1],out height);
                        if(pair[0]=="duration") double.TryParse(pair[1],NumberStyles.Float,CultureInfo.InvariantCulture,out duration);
                    }
                }
            }
            if(width<1||height<1||width>8192||height>8192||duration<=0||double.IsNaN(duration)||double.IsInfinity(duration)) throw new IOException("视频尺寸或时长不受支持");
        }

        private static void CopyFrame(byte[] pixels,int rowWidth,int x,Bitmap image)
        {
            BitmapData data=image.LockBits(new Rectangle(0,0,image.Width,image.Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
            try { for(int y=0;y<image.Height;y++) Marshal.Copy(pixels,(y*rowWidth+x)*4,IntPtr.Add(data.Scan0,y*data.Stride),image.Width*4); }
            finally { image.UnlockBits(data); }
        }

        public static void Export(string source,string peer,string destination,DeviceSkin skin,double offsetMs,Action<int> progress,CancellationToken cancel)
        { Export(source,peer,destination,skin,offsetMs,true,progress,cancel); }

        // showTouches: draw recorded touch feedback over the screens (on by default).
        public static void Export(string source,string peer,string destination,DeviceSkin skin,double offsetMs,bool showTouches,Action<int> progress,CancellationToken cancel)
        {
            string ffmpeg=Locate(),probe=Path.Combine(Path.GetDirectoryName(ffmpeg),"ffprobe.exe");
            if(!File.Exists(probe)) throw new IOException("缺少同目录 ffprobe.exe");
            if(string.Equals(Path.GetFullPath(source),Path.GetFullPath(destination),StringComparison.OrdinalIgnoreCase)
                ||(peer!=null&&string.Equals(Path.GetFullPath(peer),Path.GetFullPath(destination),StringComparison.OrdinalIgnoreCase)))
                throw new IOException("带壳视频必须另存，不能覆盖任一屏原片");
            PadTimeline timeline=File.Exists(source+".input.csv")?PadTimeline.Load(source+".input.csv"):null;
            if(timeline==null && peer!=null && File.Exists(peer+".input.csv")) timeline=PadTimeline.Load(peer+".input.csv");
            int width,height,peerWidth=0,peerHeight=0; double duration,peerDuration;
            Describe(probe,source,cancel,out width,out height,out duration);
            // Decode only enough pixels for the output shell; preserve aspect ratio.
            double shrink=Math.Min(1,Math.Min(850.0/width,600.0/height));
            width=Math.Max(2,(int)(width*shrink)/2*2); height=Math.Max(2,(int)(height*shrink)/2*2);
            if(peer!=null)
            {
                Describe(probe,peer,cancel,out peerWidth,out peerHeight,out peerDuration);
                shrink=Math.Min(1,Math.Min(850.0/peerWidth,600.0/peerHeight));
                peerWidth=Math.Max(2,(int)(peerWidth*shrink)/2*2); peerHeight=Math.Max(2,(int)(peerHeight*shrink)/2*2);
                duration=Math.Min(duration,peerDuration);
            }
            int packedWidth=width+peerWidth,packedHeight=Math.Max(height,peerHeight);
            const int outWidth=900,outHeight=1120,fps=30;
            string partial=destination+"."+Guid.NewGuid().ToString("N")+".partial.mp4";
            string audioSource=MediaAudio.Select(ffmpeg,source,peer,cancel);
            bool lower=Path.GetFileName(source).IndexOf("-lower-",StringComparison.OrdinalIgnoreCase)>=0;
            string decodeArgs="-v error -i "+Quote(source);
            if(peer==null) decodeArgs+=" -an -vf fps=30,scale="+width+":"+height;
            else decodeArgs+=" -i "+Quote(peer)+" -filter_complex \"[0:v]fps=30,scale="+width+":"+height+",pad="+width+":"+packedHeight+":0:0[a];[1:v]fps=30,scale="+peerWidth+":"+peerHeight+",pad="+peerWidth+":"+packedHeight+":0:0[b];[a][b]hstack=inputs=2:shortest=1[v]\" -map \"[v]\" -an";
            decodeArgs+=" -pix_fmt bgra -f rawvideo pipe:1";
            using(Process decoder=Create(ffmpeg,decodeArgs,false))
            using(Process encoder=Create(ffmpeg,"-v error -f rawvideo -pixel_format bgra -video_size 900x1120 -framerate 30 -i pipe:0"
                +(audioSource==null?" -map 0:v:0":" -i "+Quote(audioSource)+" -map 0:v:0 -map 1:a:0 -af apad")
                +" -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -c:a aac -t "+duration.ToString("0.000000",CultureInfo.InvariantCulture)+" -movflags +faststart -y "+Quote(partial),true))
            {
                try
                {
                    decoder.Start(); encoder.Start();
                    Task<string> decodeErrors=Task.Run(()=>decoder.StandardError.ReadToEnd());
                    Task<string> encodeErrors=Task.Run(()=>encoder.StandardError.ReadToEnd());
                    using(cancel.Register(delegate { Kill(decoder); Kill(encoder); }))
                    using(Bitmap video=new Bitmap(width,height,PixelFormat.Format32bppArgb))
                    using(Bitmap companion=peer==null?null:new Bitmap(peerWidth,peerHeight,PixelFormat.Format32bppArgb))
                    using(Bitmap frame=new Bitmap(outWidth,outHeight,PixelFormat.Format32bppArgb))
                    using(Graphics graphics=Graphics.FromImage(frame))
                    {
                        byte[] pixels=new byte[packedWidth*packedHeight*4],output=new byte[outWidth*outHeight*4];
                        int frameIndex=0,previous=-1;
                        PadState pose=new PadState();
                        while(true)
                        {
                            cancel.ThrowIfCancellationRequested();
                            int got=0;
                            while(got<pixels.Length) { int count=decoder.StandardOutput.BaseStream.Read(pixels,got,pixels.Length-got); if(count==0) break; got+=count; }
                            if(got==0) break;
                            if(got!=pixels.Length) throw new IOException("视频解码帧不完整");
                            CopyFrame(pixels,packedWidth,0,video);
                            if(companion!=null) CopyFrame(pixels,packedWidth,width,companion);
                            graphics.Clear(Ui.Bg0);
                            PadState input=timeline==null?null:timeline.At(frameIndex*1000.0/fps-offsetMs);
                            if(input!=null) { pose=PadState.Blend(pose,input,.8f); input=pose; }
                            RectangleF shell=new RectangleF(20,12,860,1050);
                            DeviceArt.Draw(graphics,shell,skin,lower?companion:video,lower?video:companion,"上屏\n未录制","下屏\n未录制",input);
                            if(showTouches&&timeline!=null&&timeline.Touches.Count>0)
                            {
                                TouchVisual marks=timeline.TouchesAt(frameIndex*1000.0/fps-offsetMs);
                                for(int screen=0;screen<2;screen++)
                                {
                                    float unit; RectangleF area=TouchFx.ScreenRect(shell,screen,out unit);
                                    TouchFx.Draw(graphics,area,unit,marks,screen);
                                }
                            }
                            BitmapData data=frame.LockBits(new Rectangle(0,0,outWidth,outHeight),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
                            try { Marshal.Copy(data.Scan0,output,0,output.Length); } finally { frame.UnlockBits(data); }
                            encoder.StandardInput.BaseStream.Write(output,0,output.Length); frameIndex++;
                            int percent=Math.Min(99,(int)(frameIndex*100.0/(duration*fps)));
                            if(percent!=previous) { previous=percent; progress(percent); }
                        }
                        encoder.StandardInput.Close();
                        if(!decoder.WaitForExit(15000)||!encoder.WaitForExit(30000)) throw new IOException("视频编码结束超时");
                        cancel.ThrowIfCancellationRequested();
                        if(frameIndex==0||decoder.ExitCode!=0||encoder.ExitCode!=0) throw new IOException("视频合成失败："+decodeErrors.Result+" "+encodeErrors.Result);
                    }
                    Recording.ValidateMp4(partial);
                    // Never overwrite an existing export without the file dialog's approval.
                    if(File.Exists(destination)) File.Replace(partial,destination,null);
                    else File.Move(partial,destination);
                    progress(100);
                }
                finally
                {
                    Kill(decoder); Kill(encoder);
                    try { if(File.Exists(partial)) File.Delete(partial); } catch { }
                }
            }
        }
    }
}
