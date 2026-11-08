using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    // Continuously drains the decoder; the UI owns at most one pending frame per screen.
    internal sealed class LiveVideo : IDisposable
    {
        internal readonly DeviceDisplay Display;
        internal readonly int Width,Height;
        private readonly string serial,ffmpeg,marker;
        private readonly PreviewOptions options;
        private readonly object gate=new object();
        private readonly Stopwatch clock=Stopwatch.StartNew();
        private readonly Thread worker;
        private Process source,decoder;
        private Bitmap latest;
        private volatile bool stopping;
        private string error;
        private long frames,windowFrames;
        private double received=-1,windowStart,fps,nextPresentation,queueAge,presentationStart,presentationRate,lastPresentation=-1;
        private long presentationWindow;
        private long replaced,presented;
        internal string Error { get { lock(gate) return error; } }
        internal long Frames { get { lock(gate) return frames; } }
        internal double Fps { get { lock(gate) return clock.Elapsed.TotalSeconds-received>2?0:fps; } }
        internal double FrameAge { get { lock(gate) return received<0?-1:clock.Elapsed.TotalSeconds-received; } }
        internal long Replaced { get { lock(gate) return replaced; } }
        internal long Presented { get { lock(gate) return presented; } }
        internal double QueueAge { get { lock(gate) return queueAge; } }
        internal double PresentationFps { get { lock(gate) return lastPresentation<0||clock.Elapsed.TotalSeconds-lastPresentation>2?0:presentationRate; } }

        internal LiveVideo(string deviceSerial,DeviceDisplay display,string decoderPath)
            : this(deviceSerial,display,decoderPath,new PreviewOptions()) { }
        internal LiveVideo(string deviceSerial,DeviceDisplay display,string decoderPath,PreviewOptions configuration)
        {
            serial=deviceSerial; Display=display; ffmpeg=decoderPath;
            options=configuration.Copy();
            Size size=options.SizeFor(display); Width=size.Width; Height=size.Height;
            marker="/data/local/tmp/thor-preview-"+Guid.NewGuid().ToString("N");
            worker=new Thread(Work) { IsBackground=true,Name="Thor video "+display.Slug }; worker.Start();
        }
        internal Bitmap TakeLatest()
        {
            lock(gate)
            {
                double now=clock.Elapsed.TotalSeconds;
                if(latest==null||!PreviewPacer.Due(now,options.DisplayFps,ref nextPresentation)) return null;
                Bitmap frame=latest; latest=null; queueAge=Math.Max(0,now-received); presented++;
                presentationWindow++; lastPresentation=now;
                if(now-presentationStart>=1) { presentationRate=presentationWindow/(now-presentationStart); presentationWindow=0; presentationStart=now; }
                return frame;
            }
        }
        internal static string DecoderArguments(int width,int height)
        {
            // Flush the tail of each raw frame even when the source becomes static.
            // nobuffer may drop a probed keyframe and delay startup until the next one.
            return "-v error -threads 1 -flags low_delay -probesize 32 -analyzeduration 0 -fpsprobesize 0 -f h264 -blocksize 4096 -i pipe:0 -an -vf scale="
                +width+":"+height+" -vsync 0 -threads 1 -pix_fmt bgra -f rawvideo -flush_packets 1 -blocksize 4096 pipe:1";
        }
        internal static string SourceArguments(string serial,string physicalId,string marker,int width,int height,int bitRate)
        {
            return "-s "+serial+" exec-out \"echo $$ > "+marker+".pid; cat /proc/$$/stat > "+marker
                +".stat; exec screenrecord --display-id "+physicalId+" --size "+width+"x"+height
                +" --bit-rate "+bitRate+" --time-limit 180 --output-format=h264 -\"";
        }
        private static Process Create(string exe,string arguments,bool input)
        {
            return new Process { StartInfo=new ProcessStartInfo(exe,arguments) { UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=input } };
        }
        private static void Kill(Process process)
        { try { if(process!=null&&!process.HasExited) process.Kill(); } catch { } }
        private static Task<string> Errors(Process process)
        {
            // Bound diagnostic storage even if a failing codec writes indefinitely.
            return Task.Run(delegate
            {
                char[] buffer=new char[1024]; StringBuilder text=new StringBuilder(); int count;
                while((count=process.StandardError.Read(buffer,0,buffer.Length))>0)
                    if(text.Length<8192) text.Append(buffer,0,Math.Min(count,8192-text.Length));
                return text.ToString();
            });
        }
        private void Work()
        {
            try
            {
                while(!stopping)
                {
                    double started=clock.Elapsed.TotalSeconds;
                    long startingFrames=Frames;
                    string command=SourceArguments(serial,Display.PhysicalId,marker,Width,Height,options.BitRate);
                    using(Process adb=Create(Adb.ExePath,command,false))
                    using(Process decode=Create(ffmpeg,DecoderArguments(Width,Height),true))
                    {
                        try
                        {
                        lock(gate)
                        {
                            if(stopping) return;
                            source=adb; decoder=decode; decode.Start(); Adb.StartBoundProcess(adb,serial,10000);
                        }
                        Task<string> adbErrors=Errors(adb),decodeErrors=Errors(decode);
                        Task transfer=Task.Run(delegate
                        {
                            try
                            {
                                byte[] packet=new byte[4096]; int count;
                                while((count=adb.StandardOutput.BaseStream.Read(packet,0,packet.Length))>0)
                                { decode.StandardInput.BaseStream.Write(packet,0,count); decode.StandardInput.BaseStream.Flush(); }
                            }
                            finally { try { decode.StandardInput.Close(); } catch { } }
                        });
                        // A stalled startup must not occupy encoders forever. Static scenes after
                        // the first frame are valid: Android emits only changed frames.
                        using(System.Threading.Timer watchdog=new System.Threading.Timer(delegate
                        {
                            if(Frames==startingFrames && clock.Elapsed.TotalSeconds-started>12)
                            { lock(gate) error="视频流启动超时；请检查连接与设备编码器"; Kill(adb); Kill(decode); }
                        },null,1000,1000))
                        {
                            byte[] pixels=new byte[Width*Height*4];
                            while(!stopping)
                            {
                                int got=0;
                                while(got<pixels.Length)
                                { int count=decode.StandardOutput.BaseStream.Read(pixels,got,pixels.Length-got); if(count==0) break; got+=count; }
                                if(got==0) break;
                                if(got!=pixels.Length) throw new IOException("预览解码帧不完整");
                                Bitmap frame=new Bitmap(Width,Height,PixelFormat.Format32bppArgb);
                                BitmapData data=frame.LockBits(new Rectangle(0,0,Width,Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
                                try { Marshal.Copy(pixels,0,data.Scan0,pixels.Length); } finally { frame.UnlockBits(data); }
                                lock(gate)
                                {
                                    if(stopping) { frame.Dispose(); break; }
                                    if(latest!=null) { latest.Dispose(); replaced++; } latest=frame; frames++; windowFrames++;
                                    received=clock.Elapsed.TotalSeconds;
                                    double span=received-windowStart;
                                    if(span>=1) { fps=windowFrames/span; windowFrames=0; windowStart=received; }
                                }
                            }
                        }
                        if(stopping) return;
                        if(!adb.WaitForExit(2000)||!decode.WaitForExit(2000)) throw new IOException("预览视频流中断");
                        transfer.GetAwaiter().GetResult();
                        if(adb.ExitCode!=0||decode.ExitCode!=0||clock.Elapsed.TotalSeconds-started<160)
                            throw new IOException("设备未能持续输出视频流："+adbErrors.Result+" "+decodeErrors.Result);
                        // Older Android limits screenrecord to 180 seconds; renew only normal expiry.
                        }
                        finally { Kill(adb); Kill(decode); }
                    }
                }
            }
            catch(Exception failure)
            {
                if(!stopping)
                {
                    lock(gate) { if(error==null) error=failure.Message; }
                    Log.Warn("Preview stream failed: "+Display.Slug,failure);
                }
            }
            finally
            {
                lock(gate) { Kill(source); Kill(decoder); source=null; decoder=null; }
                StopRemote();
            }
        }
        internal static string StartTicks(string stat)
        {
            int end=stat.LastIndexOf(')');
            if(end<0) return null;
            string[] fields=stat.Substring(end+1).Split(new char[] {' '},StringSplitOptions.RemoveEmptyEntries);
            return fields.Length>19 && Regex.IsMatch(fields[19],@"^\d+$")?fields[19]:null;
        }
        private void StopRemote()
        {
            try
            {
                AdbResult identity=Adb.Run("-s "+serial+" shell \"cat "+marker+".pid; cat "+marker+".stat\"",3000);
                string[] rows=identity.Out.Trim().Split('\n');
                if(rows.Length<2||!Regex.IsMatch(rows[0].Trim(),@"^[1-9][0-9]*$")) return;
                string pid=rows[0].Trim(),oldTicks=StartTicks(rows[1]);
                AdbResult current=Adb.Run("-s "+serial+" shell \"cat /proc/"+pid+"/stat; cat /proc/"+pid+"/cmdline\"",3000);
                int newline=current.Out.IndexOf('\n');
                if(newline<0) return;
                string ticks=StartTicks(current.Out.Substring(0,newline)),cmd=current.Out.Substring(newline+1);
                if(oldTicks==null||ticks!=oldTicks||!cmd.Contains("screenrecord")||!cmd.Contains("--output-format=h264")
                    ||!cmd.Contains(Display.PhysicalId)) return;
                AdbResult stopped=Adb.Run("-s "+serial+" shell kill -2 "+pid,3000);
                if(stopped.Code==0) Adb.Run("-s "+serial+" shell rm -f "+marker+".pid "+marker+".stat",3000);
            }
            catch(Exception failure) { Log.Warn("Preview process cleanup failed: "+Display.Slug,failure); }
            finally
            {
                try { Adb.Run("-s "+serial+" shell rm -f "+marker+".pid "+marker+".stat",1500); }
                catch { /* The unique markers may remain on a disconnected device. */ }
            }
        }
        public void Dispose()
        {
            stopping=true;
            StopRemote();
            lock(gate) { Kill(source); Kill(decoder); if(latest!=null) { latest.Dispose(); latest=null; } }
            worker.Join(5000);
        }
    }
}
