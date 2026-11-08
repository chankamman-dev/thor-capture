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
    internal sealed class PlaybackFrame : IDisposable
    {
        internal Bitmap Upper, Lower;
        internal double Time;
        public void Dispose() { if(Upper!=null) Upper.Dispose(); if(Lower!=null) Lower.Dispose(); Upper=Lower=null; }
    }

    // One packed decoder and clock for both tracks. No device or mouse input is recorded.
    internal sealed class VideoPlayback : IDisposable
    {
        internal const int Fps=30;
        private readonly string source,peer,ffmpeg;
        private readonly bool sourceLower,restrictedVideo;
        private readonly object gate=new object();
        private readonly AutoResetEvent changed=new AutoResetEvent(false);
        private readonly CancellationTokenSource cancel=new CancellationTokenSource();
        private readonly Stopwatch clock=new Stopwatch();
        private readonly Task completion;
        private Task release;
        private Process decoder;
        private Process audioDecoder;
        private IPcmOutput audioOutput;
        private string audioSource;
        internal static Func<IPcmOutput> OutputFactory=delegate { return new PcmOutput(); };
        internal bool HasAudio { get { lock(gate) return audioSource!=null; } }
        private PlaybackFrame latest;
        private PadTimeline timeline;
        private int revision,width,height,peerWidth,peerHeight;
        private bool stopping,ready,playing,ended;
        private double origin,duration;
        private string error,warning;
        internal bool Ready { get { lock(gate) return ready; } }
        internal bool Playing { get { lock(gate) return playing; } }
        internal bool Ended { get { lock(gate) return ended; } }
        internal bool Paired { get { return peer!=null; } }
        internal double Duration { get { lock(gate) return duration; } }
        internal double Position { get { lock(gate) return PositionLocked(); } }
        internal string Error { get { lock(gate) return error; } }
        internal string Warning { get { lock(gate) return warning; } }
        internal bool HasTimeline { get { lock(gate) return timeline!=null; } }
        internal Task Completion { get { return completion; } }

        internal VideoPlayback(string path,string companion,string decoderPath,bool restrictVideo=false)
        {
            source=path; peer=companion; ffmpeg=decoderPath; restrictedVideo=restrictVideo;
            sourceLower=Path.GetFileName(source).IndexOf("-lower-",StringComparison.OrdinalIgnoreCase)>=0;
            completion=Task.Run((Action)Work);
        }
        internal static string ExistingPeer(string path)
        {
            string name=Path.GetFileName(path);
            int marker=name.IndexOf("-upper-",StringComparison.OrdinalIgnoreCase);
            string replacement="-lower-";
            if(marker<0) { marker=name.IndexOf("-lower-",StringComparison.OrdinalIgnoreCase); replacement="-upper-"; }
            if(marker<0) return null;
            string candidate=Path.Combine(Path.GetDirectoryName(path),name.Substring(0,marker)+replacement+name.Substring(marker+7));
            return File.Exists(candidate)?candidate:null;
        }
        private double PositionLocked() { return Math.Min(duration,origin+(playing?clock.Elapsed.TotalSeconds:0)); }
        internal void Play()
        {
            lock(gate)
            {
                if(stopping||error!=null) return;
                if(ended) { origin=0; revision++; ended=false; if(latest!=null) { latest.Dispose(); latest=null; } }
                if(!playing) { playing=true; clock.Restart(); }
                if(audioOutput!=null) audioOutput.Pause(false);
                changed.Set();
            }
        }
        internal void Pause()
        { lock(gate) { if(stopping) return; origin=PositionLocked(); playing=false; clock.Reset(); if(audioOutput!=null) audioOutput.Pause(true); changed.Set(); } }
        internal void Seek(double seconds)
        {
            lock(gate)
            {
                if(stopping||!ready||double.IsNaN(seconds)||double.IsInfinity(seconds)) return;
                origin=Math.Max(0,Math.Min(Math.Max(0,duration-1.0/Fps),seconds));
                clock.Restart(); if(!playing) clock.Stop();
                ended=false; revision++;
                if(latest!=null) { latest.Dispose(); latest=null; }
                Kill(decoder);
                Kill(audioDecoder); if(audioOutput!=null) audioOutput.Reset();
                changed.Set();
            }
        }
        internal PlaybackFrame TakeLatest() { lock(gate) { PlaybackFrame value=latest; latest=null; return value; } }
        internal PadState InputAt(double seconds) { lock(gate) return timeline==null?null:timeline.At(seconds*1000); }
        internal TouchVisual TouchAt(double seconds) { lock(gate) return timeline==null?TouchVisual.Empty:timeline.TouchesAt(seconds*1000); }
        internal Task StopAsync()
        {
            lock(gate)
            {
                if(release!=null) return release;
                stopping=true; playing=false; Kill(decoder); Kill(audioDecoder); if(audioOutput!=null) audioOutput.Reset(); cancel.Cancel(); changed.Set();
                release=completion.ContinueWith(delegate { lock(gate) { cancel.Dispose(); changed.Dispose(); } });
                return release;
            }
        }
        private static void Kill(Process process) { try { if(process!=null&&!process.HasExited) process.Kill(); } catch { } }
        private static void Scale(ref int w,ref int h)
        {
            double scale=Math.Min(1,Math.Min(960.0/w,540.0/h));
            w=Math.Max(2,(int)(w*scale)/2*2); h=Math.Max(2,(int)(h*scale)/2*2);
        }
        private void Work()
        {
            try
            {
                string probe=Path.Combine(Path.GetDirectoryName(ffmpeg),"ffprobe.exe");
                if(!File.Exists(probe)) throw new IOException("内置播放需要同目录的 ffprobe.exe");
                double length,otherLength;
                VideoExport.Describe(probe,source,cancel.Token,out width,out height,out length,restrictedVideo); Scale(ref width,ref height);
                if(peer!=null)
                {
                    VideoExport.Describe(probe,peer,cancel.Token,out peerWidth,out peerHeight,out otherLength,restrictedVideo);
                    Scale(ref peerWidth,ref peerHeight); length=Math.Min(length,otherLength);
                }
                string csv=File.Exists(source+".input.csv")?source+".input.csv":peer!=null&&File.Exists(peer+".input.csv")?peer+".input.csv":null;
                string selectedAudio=MediaAudio.Select(ffmpeg,source,peer,cancel.Token,restrictedVideo);
                PadTimeline loaded=null; string note=null;
                if(csv!=null)
                {
                    try { loaded=PadTimeline.Load(csv); if(loaded.Interrupted) note="按键时间线中途断开"; }
                    catch(Exception failure) { note="时间线无法读取，仅播放画面："+failure.Message; }
                }
                lock(gate) { audioSource=selectedAudio; duration=length; timeline=loaded; warning=note; ready=true; clock.Restart(); if(!playing) clock.Stop(); }
                while(true)
                {
                    int version; double start;
                    lock(gate) { if(stopping) break; version=revision; start=PositionLocked(); }
                    bool finished=Decode(version,start);
                    lock(gate)
                    {
                        if(stopping) break;
                        if(revision!=version) continue;
                        if(finished) { origin=duration; playing=false; clock.Reset(); ended=true; }
                    }
                    while(true)
                    {
                        lock(gate) { if(stopping||revision!=version) break; }
                        changed.WaitOne(100);
                    }
                }
            }
            catch(Exception failure)
            { lock(gate) { if(!stopping) error="内置播放失败："+failure.Message; playing=false; } }
            finally
            {
                lock(gate) { Kill(decoder); decoder=null; if(latest!=null) { latest.Dispose(); latest=null; } }
            }
        }
        private bool WaitFrame(int version,double time,bool first)
        {
            while(true)
            {
                lock(gate)
                {
                    if(stopping||revision!=version) return false;
                    if(first||(playing&&PositionLocked()+.002>=time)) return true;
                }
                changed.WaitOne(8);
            }
        }
        private bool Decode(int version,double start)
        {
            using(CancellationTokenSource soundStop=new CancellationTokenSource())
            {
                Task sound=audioSource==null?Task.FromResult(0):Task.Run(()=>DecodeAudio(version,start,soundStop.Token));
                try { return DecodeVideo(version,start); }
                finally
                {
                    soundStop.Cancel();
                    lock(gate) { Kill(audioDecoder); if(audioOutput!=null) audioOutput.Reset(); }
                    sound.Wait();
                }
            }
        }
        private bool AudioActive(int version)
        { lock(gate) return !stopping&&revision==version; }
        private void DecodeAudio(int version,double start,CancellationToken stop)
        {
            try
            {
                if(stop.IsCancellationRequested||!AudioActive(version)) return;
                using(IPcmOutput output=OutputFactory())
                using(Process process=new Process { StartInfo=new ProcessStartInfo(ffmpeg,"-v error -ss "+start.ToString("0.000000",CultureInfo.InvariantCulture)
                    +VideoInputPolicy.Options(audioSource,restrictedVideo)+" -i "+VideoExport.Quote(audioSource)+" -map 0:a:0 -vn -ac 2 -ar 48000 -f s16le pipe:1")
                    { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true } })
                {
                    lock(gate) { if(stop.IsCancellationRequested||stopping||revision!=version) return; process.Start(); audioDecoder=process; audioOutput=output; output.Pause(!playing); }
                    Task<string> errors=Task.Run(()=>process.StandardError.ReadToEnd());
                    try
                    {
                        int index=0; byte[] samples=new byte[3840];
                        while(!stop.IsCancellationRequested&&AudioActive(version))
                        {
                            int count=0;
                            while(count<samples.Length) { int n=process.StandardOutput.BaseStream.Read(samples,count,samples.Length-count); if(n==0) break; count+=n; }
                            if(count==0) break;
                            double time=start+index*.02; index++;
                            while(true)
                            {
                                lock(gate) { if(stop.IsCancellationRequested||stopping||revision!=version) return; if(playing&&PositionLocked()+.002>=time) break; }
                                Thread.Sleep(4);
                            }
                            lock(gate) { if(time>=duration) return; if(PositionLocked()-time>.15) continue; }
                            byte[] block=samples;
                            if(count<samples.Length) { block=new byte[count-count%4]; Array.Copy(samples,block,block.Length); }
                            output.Write(block,()=>!stop.IsCancellationRequested&&AudioActive(version));
                        }
                        if(!stop.IsCancellationRequested&&AudioActive(version))
                        {
                            if(!process.WaitForExit(5000)) throw new IOException("声音解码结束超时");
                            if(process.ExitCode!=0) throw new IOException(errors.Result);
                        }
                    }
                    finally
                    {
                        lock(gate) { Kill(process); if(audioDecoder==process) { audioDecoder=null; audioOutput=null; } }
                        try { process.WaitForExit(2000); errors.Wait(2000); } catch { }
                    }
                }
            }
            catch(Exception failure) { lock(gate) if(!stop.IsCancellationRequested&&!stopping&&revision==version) warning="声音播放失败："+failure.Message; }
        }
        private bool DecodeVideo(int version,double start)
        {
            int packedWidth=width+peerWidth,packedHeight=Math.Max(height,peerHeight);
            string stamp=start.ToString("0.000000",CultureInfo.InvariantCulture);
            string args="-v error -threads 1 -ss "+stamp+VideoInputPolicy.Options(source,restrictedVideo)+" -i "+VideoExport.Quote(source);
            if(peer==null) args+=" -an -vf fps=30,scale="+width+":"+height;
            else args+=" -threads 1 -ss "+stamp+VideoInputPolicy.Options(peer,restrictedVideo)+" -i "+VideoExport.Quote(peer)+" -filter_complex_threads 1 -filter_complex \"[0:v]setpts=PTS-STARTPTS,fps=30,scale="+width+":"+height+",pad="+width+":"+packedHeight+":0:0[a];[1:v]setpts=PTS-STARTPTS,fps=30,scale="+peerWidth+":"+peerHeight+",pad="+peerWidth+":"+packedHeight+":0:0[b];[a][b]hstack=inputs=2:shortest=1[v]\" -map \"[v]\" -an";
            args+=" -threads 1 -pix_fmt bgra -f rawvideo pipe:1";
            using(Process process=new Process { StartInfo=new ProcessStartInfo(ffmpeg,args) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true } })
            {
                lock(gate) { if(stopping||revision!=version) return false; process.Start(); decoder=process; }
                Task<string> errors=Task.Run(delegate
                {
                    StringBuilder text=new StringBuilder(); char[] chars=new char[1024]; int read;
                    while((read=process.StandardError.Read(chars,0,chars.Length))>0) if(text.Length<8192) text.Append(chars,0,Math.Min(read,8192-text.Length));
                    return text.ToString();
                });
                try
                {
                    long readStarted=0;
                    using(System.Threading.Timer guard=new System.Threading.Timer(delegate
                    {
                        long ticks=Interlocked.Read(ref readStarted);
                        if(ticks>0&&(Stopwatch.GetTimestamp()-ticks)/(double)Stopwatch.Frequency>15) Kill(process);
                    },null,1000,1000))
                    {
                    byte[] pixels=new byte[packedWidth*packedHeight*4]; int index=0;
                    while(WaitFrame(version,start+index/(double)Fps,index==0))
                    {
                        int got=0;
                        Interlocked.Exchange(ref readStarted,Stopwatch.GetTimestamp());
                        while(got<pixels.Length) { int n=process.StandardOutput.BaseStream.Read(pixels,got,pixels.Length-got); if(n==0) break; got+=n; }
                        Interlocked.Exchange(ref readStarted,0);
                        lock(gate) { if(stopping||revision!=version) return false; }
                        if(got==0) break;
                        if(got!=pixels.Length) throw new IOException("解码帧不完整");
                        PlaybackFrame frame=new PlaybackFrame { Time=start+index/(double)Fps };
                        try
                        {
                            Bitmap main=Image(pixels,packedWidth,0,width,height);
                            if(sourceLower) frame.Lower=main; else frame.Upper=main;
                            if(peer!=null) { Bitmap second=Image(pixels,packedWidth,width,peerWidth,peerHeight); if(sourceLower) frame.Upper=second; else frame.Lower=second; }
                            lock(gate)
                            {
                                if(stopping||revision!=version) return false;
                                if(latest!=null) latest.Dispose(); latest=frame; frame=null;
                            }
                        }
                        finally { if(frame!=null) frame.Dispose(); }
                        index++;
                    }
                    lock(gate) { if(stopping||revision!=version) return false; }
                    if(!process.WaitForExit(5000)) throw new IOException("解码结束超时");
                    if(process.ExitCode!=0||index==0) throw new IOException(errors.Result.Length>0?errors.Result:"没有可播放的视频帧");
                    return true;
                    }
                }
                catch
                {
                    lock(gate) { if(stopping||revision!=version) return false; }
                    throw;
                }
                finally
                {
                    lock(gate) { Kill(process); if(decoder==process) decoder=null; }
                    try { process.WaitForExit(2000); } catch { }
                    try { errors.Wait(2000); } catch { }
                }
            }
        }
        private static Bitmap Image(byte[] pixels,int packedWidth,int x,int w,int h)
        {
            Bitmap bitmap=new Bitmap(w,h,PixelFormat.Format32bppArgb);
            try
            {
                BitmapData bits=bitmap.LockBits(new Rectangle(0,0,w,h),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
                try { for(int y=0;y<h;y++) Marshal.Copy(pixels,(y*packedWidth+x)*4,IntPtr.Add(bits.Scan0,y*bits.Stride),w*4); }
                finally { bitmap.UnlockBits(bits); }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
        public void Dispose() { StopAsync().Wait(6000); }
    }
}
