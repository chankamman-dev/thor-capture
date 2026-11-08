using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal static class RecordingAudioTests
    {
        internal static void Run()
        {
            byte[] header=new byte[12]; header[7]=123; header[11]=4;
            using(MemoryStream data=new MemoryStream())
            {
                data.Write(header,0,12); data.Write(new byte[] {1,2,3,4},0,4); data.Position=0;
                long pts; Tests.Check(RecordingAudio.Packet(data,out pts)[3]==4&&pts==123,"PCM packets preserve big-endian timestamps and sample payloads");
            }
            Tests.Reject(()=>{long pts; RecordingAudio.Packet(new MemoryStream(new byte[3]),out pts);},"truncated audio packets are rejected");
            header[11]=3;
            Tests.Reject(()=>{long pts; RecordingAudio.Packet(new MemoryStream(header),out pts);},"unaligned PCM packet length is rejected");
            string wave=Path.Combine(Tests.Root,"audio-gap-"+Guid.NewGuid().ToString("N")+".wav");
            using(PcmWave output=new PcmWave(wave))
            { output.Append(new byte[3840],0); output.Append(new byte[3840],40000); Tests.Reject(()=>output.Append(new byte[3840],10000000),"large device timestamp jumps stop audio rather than introducing silent drift"); }
            byte[] bytes=File.ReadAllBytes(wave);
            Tests.Check(Encoding.ASCII.GetString(bytes,0,4)=="RIFF"&&BitConverter.ToInt32(bytes,40)==11520&&bytes.Length==11564,"PCM WAV is finalized and missing timestamp blocks are padded");
            using(RecordingAudio capture=new RecordingAudio("test",wave))
            {
                string args=capture.ServerArguments();
                Tests.Check(args.Contains("audio_source=playback audio_dup=true")&&args.Contains("video=false")&&args.Contains("control=false")&&!args.Contains("mic"),"audio capture keeps Thor speakers on and never enables microphone or device control");
            }
            Tests.Check(File.Exists(RecordingAudio.LocateServer()),"pinned audio server matches its official SHA256");
            RecordingSession cancelled=new RecordingSession("test",new List<DeviceDisplay> {new DeviceDisplay {PhysicalId="101"}},Tests.Root,true);
            cancelled.Stop(); Tests.Reject(()=>cancelled.Execute(),"stopping before launch prevents future audio or video capture from starting");
        }
        private sealed class Sink : IPcmOutput
        {
            internal static int Blocks,Resets,Pauses,Closed;
            internal static long Energy;
            private volatile bool paused;
            public void Write(byte[] samples,Func<bool> active)
            {
                while(paused&&active()) Thread.Sleep(2);
                if(!active()) return;
                long energy=0; for(int i=0;i+1<samples.Length;i+=2) energy+=Math.Abs((int)BitConverter.ToInt16(samples,i));
                Interlocked.Add(ref Energy,energy); Interlocked.Increment(ref Blocks); Thread.Sleep(10);
            }
            public void Pause(bool value) { paused=value; if(value) Interlocked.Increment(ref Pauses); }
            public void Reset() { Interlocked.Increment(ref Resets); }
            public void Dispose() { Interlocked.Increment(ref Closed); }
        }
        private static void Wait(Func<bool> predicate,string message)
        {
            Stopwatch watch=Stopwatch.StartNew(); while(!predicate()&&watch.ElapsedMilliseconds<10000) Thread.Sleep(10);
            if(!predicate()) throw new Exception(message);
        }
        internal static void RunStream(string ffmpeg)
        {
            string folder=Path.Combine(Tests.Root,"audio-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            string video=Path.Combine(folder,"fixture.mp4");
            MediaAudio.Run(ffmpeg,"-v error -f lavfi -i color=c=orange:s=128x72:r=30 -t 2 -c:v libx264 -pix_fmt yuv420p -y "+VideoExport.Quote(video),15000,CancellationToken.None);
            string old=Environment.GetEnvironmentVariable("THOR_CAPTURE_AUDIO_TEST");
            Environment.SetEnvironmentVariable("THOR_CAPTURE_AUDIO_TEST",folder);
            try
            {
                RecordingAudio.CheckSupport("test");
                File.WriteAllText(Path.Combine(folder,"old-api"),"32");
                Tests.Reject(()=>RecordingAudio.CheckSupport("test"),"unsupported device audio fails preflight instead of producing a silent success"); File.Delete(Path.Combine(folder,"old-api"));
                List<DeviceDisplay> displays=new List<DeviceDisplay> {new DeviceDisplay {PhysicalId="101",Width=128,Height=72},new DeviceDisplay {PhysicalId="202",LogicalId=4,IsLower=true,Width=128,Height=72}};
                RecordingSession session=new RecordingSession("test",displays,folder,true);
                Task<string[]> work=Task.Run(()=>session.Execute());
                Wait(()=>File.Exists(Path.Combine(folder,"lower.start")),"audio dual recording failed to start"); Thread.Sleep(750); session.Stop();
                Tests.Check(work.Wait(15000)&&session.Warning==null,"dual recording stops the shared sound capture and finalizes both sound-bearing MP4s");
                string[] saved=session.Saved;
                Tests.Check(MediaAudio.HasAudio(ffmpeg,saved[0],CancellationToken.None)&&MediaAudio.HasAudio(ffmpeg,saved[1],CancellationToken.None),"both screen originals contain a real AAC audio stream");
                string decoded=Path.Combine(folder,"decoded.pcm");
                MediaAudio.Run(ffmpeg,"-v error -i "+VideoExport.Quote(saved[0])+" -map 0:a:0 -f s16le -y "+VideoExport.Quote(decoded),10000,CancellationToken.None);
                byte[] samples=File.ReadAllBytes(decoded); long energy=0; for(int i=0;i+1<samples.Length;i+=2) energy+=Math.Abs((int)BitConverter.ToInt16(samples,i));
                Tests.Check(energy>100000&&samples.Length>100000,"recorded audio decodes into non-silent samples, rather than an empty audio track");
                Tests.Check(Directory.GetFiles(folder,"*.system-audio.wav").Length==0&&File.Exists(Path.Combine(folder,"forward-removed")),"successful mux removes temporary sound and only its own ADB forward");
                string before=Path.Combine(folder,"before.h264"),after=Path.Combine(folder,"after.h264");
                MediaAudio.Run(ffmpeg,"-v error -i "+VideoExport.Quote(video)+" -map 0:v:0 -c copy -f h264 -y "+VideoExport.Quote(before),10000,CancellationToken.None);
                MediaAudio.Run(ffmpeg,"-v error -i "+VideoExport.Quote(saved[0])+" -map 0:v:0 -c copy -f h264 -y "+VideoExport.Quote(after),10000,CancellationToken.None);
                Tests.Check(Convert.ToBase64String(File.ReadAllBytes(before))==Convert.ToBase64String(File.ReadAllBytes(after)),"adding audio copies original H264 packets without re-encoding the screen");
                byte[] intact=File.ReadAllBytes(video);
                Tests.Reject(()=>MediaAudio.Attach(video,Path.Combine(folder,"missing.wav"),0),"audio mux failure is visible");
                Tests.Check(Convert.ToBase64String(intact)==Convert.ToBase64String(File.ReadAllBytes(video))&&Directory.GetFiles(folder,"*.partial.mp4").Length==0,"audio mux failure leaves the original MP4 intact and removes incomplete output");
                string export=Path.Combine(folder,"dual-with-sound.mp4");
                VideoExport.Export(saved[1],saved[0],export,DeviceSkin.All[1],0,p=>{},CancellationToken.None);
                Tests.Check(MediaAudio.HasAudio(ffmpeg,export,CancellationToken.None),"lower-first dual shell export retains system sound");
                string streams=MediaAudio.Run(Path.Combine(Path.GetDirectoryName(ffmpeg),"ffprobe.exe"),"-v error -select_streams a -show_entries stream=codec_type -of csv=p=0 "+VideoExport.Quote(export),10000,CancellationToken.None);
                Tests.Check(streams.Trim()=="audio","dual shell export uses one audio track and never doubles the system sound");
                MediaAudio.Run(ffmpeg,"-v error -i "+VideoExport.Quote(export)+" -f null -",15000,CancellationToken.None);
                Tests.Check(MediaAudio.Select(ffmpeg,video,saved[0],CancellationToken.None)==saved[0],"sound selection falls back to the companion when the selected source is silent");
                Func<IPcmOutput> original=VideoPlayback.OutputFactory; VideoPlayback.OutputFactory=()=>new Sink();
                try
                {
                    using(VideoPlayback player=new VideoPlayback(saved[1],saved[0],ffmpeg))
                    {
                        Wait(()=>player.Ready||player.Error!=null,"audio player not ready");
                        Tests.Check(player.Ready&&player.HasAudio&&!player.Playing,"internal sound playback opens paused on the shared dual-screen clock");
                        player.Play(); Wait(()=>Sink.Blocks>5,"audio decoder produced no blocks"); player.Pause(); Thread.Sleep(70); int count=Sink.Blocks; Thread.Sleep(100);
                        Tests.Check(Sink.Blocks==count&&Sink.Pauses>0&&Sink.Energy>0,"pausing internal playback pauses non-silent audio along with the picture");
                        player.Seek(.5); player.Seek(1); player.Play(); Wait(()=>Sink.Blocks>count+3,"seek did not resume audio");
                        Tests.Check(Sink.Resets>0,"seeking flushes old audio buffers before resuming the new position");
                        Tests.Check(player.StopAsync().Wait(5000)&&Sink.Closed>0,"stopping internal audio playback releases decoder and output without hanging");
                    }
                    using(VideoPlayback player=new VideoPlayback(saved[0],null,ffmpeg))
                    { Wait(()=>player.Ready,"paused audio not ready"); Thread.Sleep(120); Tests.Check(player.StopAsync().Wait(5000),"closing a paused sound-bearing video releases both waiting decoders"); }
                }
                finally { VideoPlayback.OutputFactory=original; }
                File.WriteAllText(Path.Combine(folder,"deny"),"1");
                using(RecordingAudio audio=new RecordingAudio("test",Path.Combine(folder,"denied.wav"))) Tests.Reject(()=>audio.Start(),"device capture denial is surfaced before recording silently");
                File.Delete(Path.Combine(folder,"deny"));
                File.WriteAllText(Path.Combine(folder,"drop"),"1");
                string dropped=Path.Combine(folder,"dropped.wav");
                using(RecordingAudio audio=new RecordingAudio("test",dropped)) { audio.Start(); Wait(()=>audio.Error!=null,"audio disconnect not detected"); }
                Tests.Check(new FileInfo(dropped).Length>44,"disconnect preserves finalized captured sound for recovery");
                using(PcmOutput output=new PcmOutput())
                {
                    output.Pause(true); Task block=Task.Run(()=>output.Write(new byte[3840],()=>true)); Thread.Sleep(50);
                    Tests.Check(!block.IsCompleted,"Windows audio output keeps a submitted silent buffer paused");
                    output.Pause(false); Tests.Check(block.Wait(3000),"Windows audio output resumes and releases a native PCM buffer"); output.Reset();
                }
            }
            finally { Environment.SetEnvironmentVariable("THOR_CAPTURE_AUDIO_TEST",old); }
        }
        internal static int FakeAdb(string command,string mode,string dir)
        {
            string folder=Environment.GetEnvironmentVariable("THOR_CAPTURE_AUDIO_TEST"); if(folder==null) return Tests.NotHandled;
            if(command.Contains("getprop ro.build.version.sdk")) { Console.Write(File.Exists(Path.Combine(folder,"old-api"))?"32":"33"); return 0; }
            if(command.Contains(" forward --remove")) { File.WriteAllText(Path.Combine(folder,"forward-removed"),"1"); return 0; }
            if(command.Contains(" forward tcp:0"))
            {
                TcpListener find=new TcpListener(IPAddress.Loopback,0); find.Start(); int port=((IPEndPoint)find.LocalEndpoint).Port; find.Stop();
                File.WriteAllText(Path.Combine(folder,"port"),port.ToString()); Console.Write(port); return 0;
            }
            if(command.Contains("com.genymobile.scrcpy.Server"))
            {
                File.WriteAllText(Path.Combine(folder,"server-command"),command);
                TcpListener listener=new TcpListener(IPAddress.Loopback,int.Parse(File.ReadAllText(Path.Combine(folder,"port")))); listener.Start();
                try
                {
                    using(TcpClient client=listener.AcceptTcpClient()) using(Stream stream=client.GetStream())
                    {
                        stream.WriteByte(0); bool denied=File.Exists(Path.Combine(folder,"deny"));
                        stream.Write(denied?new byte[4]:new byte[] {0,0x72,0x61,0x77},0,4); if(denied) return 0;
                        for(int index=0;index<1000;index++)
                        {
                            byte[] packet=new byte[12],data=new byte[3840]; long pts=1000000+index*20000;
                            for(int b=7;b>=0;b--) { packet[b]=(byte)pts; pts>>=8; } packet[10]=15;
                            for(int frame=0;frame<960;frame++) { short sample=(short)(Math.Sin((index*960+frame)*2*Math.PI*440/48000)*12000); byte[] pair=BitConverter.GetBytes(sample); for(int channel=0;channel<2;channel++) { data[frame*4+channel*2]=pair[0]; data[frame*4+channel*2+1]=pair[1]; } }
                            stream.Write(packet,0,12); stream.Write(data,0,data.Length); stream.Flush(); Thread.Sleep(20);
                            if(index==4&&File.Exists(Path.Combine(folder,"drop"))) return 0;
                        }
                    }
                }
                catch(IOException) { } catch(SocketException) { }
                finally { listener.Stop(); }
                return 0;
            }
            if(command.Contains("thor-audio-")&&command.Contains(".stat")) return 0; // No remote process identity in this socket fixture.
            if(command.Contains("exec screenrecord"))
            {
                string slug=command.Contains("-lower-")?"lower":"upper",pid=slug=="upper"?"1001":"1002";
                File.WriteAllText(Path.Combine(folder,pid+".command"),command); File.WriteAllText(Path.Combine(folder,slug+".start"),"1");
                while(!File.Exists(Path.Combine(folder,pid+".stop"))) Thread.Sleep(20); return 0;
            }
            if(command.Contains("shell cat ")&&command.EndsWith(".pid")) { Console.Write(command.Contains("-lower-")?"1002":"1001"); return 0; }
            if(command.Contains("/cmdline")) { Console.Write(File.ReadAllText(Path.Combine(folder,command.Contains("1002")?"1002.command":"1001.command"))); return 0; }
            if(command.Contains("kill -2 ")) { File.WriteAllText(Path.Combine(folder,command.Contains("1002")?"1002.stop":"1001.stop"),"1"); return 0; }
            if(command.Contains(" pull ")) { string rest=command.Substring(command.IndexOf(" pull ")+6); string target=rest.Substring(rest.IndexOf(' ')+1).Trim('"'); File.Copy(Path.Combine(folder,"fixture.mp4"),target,false); return 0; }
            return Tests.NotHandled;
        }
    }
}
