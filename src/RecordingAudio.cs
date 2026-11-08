using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    // The pinned Apache-2.0 scrcpy server captures media playback only, never a microphone.
    internal sealed class RecordingAudio : IDisposable
    {
        internal const string Version="3.3.4";
        internal const string ServerName="scrcpy-server-v3.3.4";
        internal const string ServerHash="8588238c9a5a00aa542906b6ec7e6d5541d9ffb9b5d0f6e1bc0e365e2303079e";
        private readonly string serial,path,id,remote;
        private readonly object gate=new object();
        private readonly ManualResetEvent ready=new ManualResetEvent(false);
        private Task work;
        private Process server;
        private TcpClient socket;
        private int port;
        private volatile bool stopping;
        private int disposed;
        private string error;
        private long anchor;
        internal double TrimSeconds;
        internal string Error { get { lock(gate) return error; } }
        internal string PathName { get { return path; } }

        internal RecordingAudio(string device,string wave)
        {
            serial=device; path=wave;
            id=(Guid.NewGuid().GetHashCode()&0x7fffffff).ToString("x8");
            remote="/data/local/tmp/thor-audio-"+id+".jar";
        }
        internal static string LocateServer()
        {
            string path=Path.Combine(Adb.AppDir(),ServerName);
            if(!File.Exists(path)) path=Path.Combine(Adb.AppDir(),"..","third-party","scrcpy",ServerName);
            if(!File.Exists(path)) throw new IOException("系统声音组件缺失：请将 "+ServerName+" 放在程序旁，或选择无声录屏。");
            using(SHA256 hash=SHA256.Create()) using(FileStream file=File.OpenRead(path))
                if(BitConverter.ToString(hash.ComputeHash(file)).Replace("-","").ToLowerInvariant()!=ServerHash)
                    throw new IOException("系统声音组件校验失败，请恢复官方固定版本；未运行设备服务。");
            return System.IO.Path.GetFullPath(path);
        }
        internal static void CheckSupport(string serial)
        {
            LocateServer(); string ffmpeg=VideoExport.Locate();
            if(!File.Exists(Path.Combine(Path.GetDirectoryName(ffmpeg),"ffprobe.exe"))) throw new IOException("声音合成需要 FFmpeg 同目录的 ffprobe.exe");
            AdbResult result=Adb.Run("-s "+serial+" shell getprop ro.build.version.sdk",5000); int api;
            if(result.Code!=0||!int.TryParse(result.Out.Trim(),out api)||api<33)
                throw new IOException("保留实机外放的系统声音录制需要 Android 13 或以上；可选择无声录屏。");
        }
        internal string ServerArguments()
        {
            return "-s "+serial+" shell \"echo $$ > "+remote+".pid; cat /proc/$$/stat > "+remote+".stat; CLASSPATH="+remote
                +" exec app_process / com.genymobile.scrcpy.Server "+Version+" scid="+id
                +" video=false audio=true control=false audio_codec=raw audio_source=playback audio_dup=true"
                +" tunnel_forward=true send_device_meta=false send_dummy_byte=true send_codec_meta=true send_frame_meta=true"
                +" power_on=false cleanup=false log_level=warn\"";
        }
        private AdbResult Run(string args,int timeout)
        { return Adb.Run("-s "+serial+" "+args,timeout); }
        internal void Start()
        {
            try
            {
                AdbResult push=Run("push "+VideoExport.Quote(LocateServer())+" "+remote,15000);
                if(push.Code!=0) throw new IOException("声音组件发送失败："+push.Err);
                AdbResult forward=Run("forward tcp:0 localabstract:scrcpy_"+id,5000);
                if(forward.Code!=0||!int.TryParse(forward.Out.Trim(),out port)||port<1||port>65535) throw new IOException("声音通道创建失败："+forward.Err);
                server=new Process { StartInfo=new ProcessStartInfo(Adb.ExePath,ServerArguments()) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true } };
                Adb.StartBoundProcess(server,serial,10000); server.BeginOutputReadLine(); server.BeginErrorReadLine();
                work=Task.Run((Action)Capture);
                if(!ready.WaitOne(12000)) throw new IOException("设备声音采集启动超时，请解锁设备后重试。");
                if(Error!=null) throw new IOException(Error);
            }
            catch { Dispose(); throw; }
        }
        internal void MarkVideoStart()
        { TrimSeconds=Math.Max(0,(Stopwatch.GetTimestamp()-Interlocked.Read(ref anchor))/(double)Stopwatch.Frequency); }
        internal static void ReadExactly(Stream stream,byte[] bytes)
        {
            int count=0;
            while(count<bytes.Length) { int read=stream.Read(bytes,count,bytes.Length-count); if(read==0) throw new EndOfStreamException("设备声音通道已关闭"); count+=read; }
        }
        internal static long BigEndian(byte[] bytes,int start,int count)
        { long value=0; for(int i=0;i<count;i++) value=(value<<8)|bytes[start+i]; return value; }
        internal static byte[] Packet(Stream stream,out long pts)
        {
            byte[] header=new byte[12]; ReadExactly(stream,header);
            if((header[0]&0x80)!=0) throw new IOException("非 PCM 声音数据包");
            pts=BigEndian(header,0,8)&0x3fffffffffffffffL;
            long size=BigEndian(header,8,4);
            if(size<4||size>65536||size%4!=0) throw new IOException("声音数据包长度无效");
            byte[] data=new byte[(int)size]; ReadExactly(stream,data); return data;
        }
        private void Capture()
        {
            try
            {
                Stopwatch timeout=Stopwatch.StartNew(); Stream stream=null;
                while(!stopping&&timeout.ElapsedMilliseconds<10000)
                {
                    TcpClient client=new TcpClient(); client.NoDelay=true; client.ReceiveTimeout=1500;
                    lock(gate) socket=client;
                    try
                    {
                        client.Connect("127.0.0.1",port); stream=client.GetStream();
                        if(stream.ReadByte()==0) break;
                    }
                    catch(IOException) { } catch(SocketException) { }
                    client.Close(); stream=null; Thread.Sleep(100);
                }
                if(stream==null) throw new IOException("设备声音通道未就绪");
                byte[] codec=new byte[4]; ReadExactly(stream,codec);
                if(BigEndian(codec,0,4)!=0x726177) throw new IOException("设备拒绝系统声音采集；检查系统版本、解锁状态与应用录音限制。");
                using(PcmWave wave=new PcmWave(path))
                {
                    long first=-1;
                    while(!stopping)
                    {
                        long pts; byte[] data=Packet(stream,out pts);
                        if(first<0) { first=pts; Interlocked.Exchange(ref anchor,Stopwatch.GetTimestamp()-(long)(data.Length/192000.0*Stopwatch.Frequency)); }
                        wave.Append(data,pts-first);
                        ready.Set();
                    }
                }
            }
            catch(Exception failure)
            {
                if(!stopping) { lock(gate) error="系统声音采集失败："+failure.Message; Log.Warn("System audio capture failed",failure); }
            }
            finally { ready.Set(); }
        }
        internal void Stop()
        { stopping=true; lock(gate) if(socket!=null) socket.Close(); }
        public void Dispose()
        {
            if(Interlocked.Exchange(ref disposed,1)!=0) return;
            Stop(); if(work!=null&&!work.Wait(4000)) throw new IOException("声音采集结束超时");
            try
            {
                AdbResult marker=Run("shell \"cat "+remote+".pid; cat "+remote+".stat\"",2000);
                string[] rows=marker.Out.Trim().Split('\n');
                if(rows.Length>=2&&Regex.IsMatch(rows[0].Trim(),@"^[1-9][0-9]*$"))
                {
                    string pid=rows[0].Trim(),ticks=LiveVideo.StartTicks(rows[1]);
                    AdbResult current=Run("shell \"cat /proc/"+pid+"/stat; cat /proc/"+pid+"/cmdline\"",2000);
                    int newline=current.Out.IndexOf('\n');
                    if(ticks!=null&&newline>0&&LiveVideo.StartTicks(current.Out.Substring(0,newline))==ticks
                        &&current.Out.Substring(newline+1).Contains("com.genymobile.scrcpy.Server")&&current.Out.Contains("scid="+id))
                        Run("shell kill -15 "+pid,2000);
                }
            }
            finally
            {
                if(port>0) Run("forward --remove tcp:"+port,2000);
                Run("shell rm -f "+remote+" "+remote+".pid "+remote+".stat",2000);
                if(server!=null) { try { if(!server.WaitForExit(1500)) server.Kill(); } catch { } server.Dispose(); server=null; }
                ready.Dispose();
            }
        }
    }

    internal sealed class PcmWave : IDisposable
    {
        private readonly FileStream file;
        private long bytes;
        internal PcmWave(string path) { file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read); file.Write(new byte[44],0,44); }
        internal void Append(byte[] data,long relativeUs)
        {
            long target=(long)Math.Round(relativeUs*48000.0/1000000)*4;
            long gap=target-bytes;
            if(gap>192000*3||gap< -192000/2) throw new IOException("声音时间戳发生跳变，保留已采集声音");
            // Device timestamps repair missing blocks, without converting USB jitter into drift.
            if(gap>192) { byte[] zero=new byte[4096]; while(gap>0) { int n=(int)Math.Min(zero.Length,gap); file.Write(zero,0,n); bytes+=n; gap-=n; } }
            file.Write(data,0,data.Length); bytes+=data.Length;
        }
        public void Dispose()
        {
            try
            {
                file.Position=0;
                using(BinaryWriter writer=new BinaryWriter(file,Encoding.ASCII,true))
                {
                    writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write((uint)(bytes+36)); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                    writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(48000); writer.Write(192000); writer.Write((short)4); writer.Write((short)16);
                    writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write((uint)bytes);
                }
            }
            finally { file.Dispose(); }
        }
    }
}
