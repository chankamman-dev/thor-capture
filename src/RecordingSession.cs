using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    // Launches both encoders concurrently, and attempts every stop even if one fails.
    internal sealed class RecordingSession
    {
        internal readonly Recording[] Tracks;
        private readonly string[] saved;
        private readonly Task<string>[] workers;
        private readonly object gate = new object();
        private readonly string serial;
        private readonly bool withAudio;
        private RecordingAudio audio;
        private volatile bool stopRequested;
        internal string Warning;
        internal string TimelineSource { get { return Tracks[0].Destination; } }
        internal string[] Saved { get { lock(gate) return (string[])saved.Clone(); } }

        internal RecordingSession(string serial, IList<DeviceDisplay> displays, string directory)
            : this(serial,displays,directory,false) { }
        internal RecordingSession(string serial, IList<DeviceDisplay> displays, string directory,bool systemAudio)
        {
            this.serial=serial; withAudio=systemAudio;
            if(displays.Count<1 || displays.Count>2) throw new IOException("请选择一块屏幕或 Thor 双屏录制");
            Tracks=new Recording[displays.Count]; saved=new string[displays.Count]; workers=new Task<string>[displays.Count];
            string id=Recording.NewId();
            for(int i=0;i<Tracks.Length;i++) Tracks[i]=new Recording(serial,displays[i],directory,id);
        }
        internal void CheckSupport() { Tracks[0].CheckSupport(); if(withAudio) RecordingAudio.CheckSupport(serial); }

        private void StopTrack(int index)
        {
            // A stop can arrive before adb has written the PID marker.
            Exception last=null;
            for(int attempt=0;attempt<8;attempt++)
            {
                Task<string> worker=workers[index];
                if(worker==null&&stopRequested) return;
                if(worker!=null && worker.IsCompleted) return;
                try { Tracks[index].Stop(); return; }
                catch(Exception failure) { last=failure; }
                Thread.Sleep(150);
            }
            if(workers[index]!=null && workers[index].IsCompleted) return;
            throw new IOException(last==null?"录屏尚未启动":last.Message);
        }
        internal void Stop()
        {
            stopRequested=true;
            bool launching=true; foreach(Task<string> worker in workers) if(worker!=null) launching=false;
            if(launching&&audio!=null) audio.Stop();
            Task[] stops=new Task[Tracks.Length];
            for(int i=0;i<stops.Length;i++) { int index=i; stops[i]=Task.Run(()=>StopTrack(index)); }
            try { Task.WaitAll(stops); }
            catch(AggregateException failure) { throw new IOException("部分屏幕尚未停止，请重试："+failure.Flatten().InnerExceptions[0].Message); }
            if(audio!=null) audio.Stop();
        }
        internal string[] Execute()
        {
            try
            {
                if(stopRequested) throw new IOException("录屏启动已取消");
                if(withAudio)
                {
                    audio=new RecordingAudio(serial,TimelineSource+".system-audio.wav");
                    audio.Start(); audio.MarkVideoStart();
                }
                if(stopRequested) throw new IOException("录屏启动已取消");
                ExecuteVideo();
            }
            finally { if(audio!=null) audio.Dispose(); }
            if(audio!=null)
            {
                if(audio.Error!=null) Warning=audio.Error+"；画面已保留，采集声音保留在 "+audio.PathName;
                else
                {
                    try
                    {
                        foreach(string path in Saved) if(path!=null) MediaAudio.Attach(path,audio.PathName,audio.TrimSeconds);
                        File.Delete(audio.PathName);
                    }
                    catch(Exception failure) { Warning="画面已保存，但声音合成未全部完成："+failure.Message+"；声音原件保留在 "+audio.PathName; }
                }
            }
            return Saved;
        }
        private void ExecuteVideo()
        {
            using(ManualResetEvent launch=new ManualResetEvent(false))
            {
                for(int i=0;i<Tracks.Length;i++)
                {
                    int index=i;
                    workers[i]=Task.Run(delegate
                    {
                        launch.WaitOne();
                        string path=Tracks[index].Execute();
                        lock(gate) saved[index]=path;
                        return path;
                    });
                }
                launch.Set();
                // An encoder failure must not leave the peer recording for three minutes.
                List<Task<string>> pending=new List<Task<string>>(workers);
                while(pending.Count>0)
                {
                    int completed=Task.WaitAny(pending.ToArray());
                    Task<string> task=pending[completed]; pending.RemoveAt(completed);
                    if(task.IsFaulted)
                    {
                        try { Stop(); } catch { /* Preserve each failure below, and all remote originals. */ }
                        break;
                    }
                }
                try { Task.WaitAll(workers); }
                catch(AggregateException failure)
                {
                    string message="录屏未全部完成："+failure.Flatten().InnerExceptions[0].Message;
                    if(Tracks.Length>1) message+="\n若设备无法同时运行两个录屏编码器，请选择上屏或下屏单独录制。";
                    foreach(string path in Saved) if(path!=null) message+="\n已保存："+path;
                    throw new IOException(message);
                }
            }
        }
        internal void CopyTimeline()
        {
            string source=TimelineSource+".input.csv";
            if(!File.Exists(source)) return;
            foreach(string path in Saved)
            {
                if(path==null || path==TimelineSource) continue;
                string target=path+".input.csv",partial=target+".partial";
                File.Copy(source,partial,false);
                File.Move(partial,target);
            }
        }
    }
}
