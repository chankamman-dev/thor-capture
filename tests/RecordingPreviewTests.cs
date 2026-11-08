using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThorCapture
{
    internal static class RecordingPreviewTests
    {
        private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
        internal static void Run()
        {
            string oldGroup=Environment.GetEnvironmentVariable("THOR_CAPTURE_GROUP_ROOT");
            string group=Path.Combine(Tests.Root,"record-preview-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(group);
            PrepareMovie();
            Environment.SetEnvironmentVariable("THOR_CAPTURE_GROUP_ROOT",group);
            Environment.SetEnvironmentVariable("THOR_RECORD_PREVIEW_TEST",group);
            Tests.FakeMode("paired-running");
            try
            {
                WithUi(delegate(MainForm form)
                {
                    typeof(MainForm).GetField("serial",Fields).SetValue(form,"test");
                    typeof(MainForm).GetField("captureDir",Fields).SetValue(form,group);
                    var screens=(List<DeviceDisplay>)typeof(MainForm).GetField("displays",Fields).GetValue(form);
                    screens.Add(new DeviceDisplay {Width=128,Height=72,PhysicalId="101"});
                    screens.Add(new DeviceDisplay {LogicalId=4,Width=124,Height=108,PhysicalId="202",IsLower=true});
                    ((Segmented)typeof(MainForm).GetField("screenPicker",Fields).GetValue(form)).SelectedIndex=2;
                    typeof(MainForm).GetMethod("ApplyPreviewOptions",Fields).Invoke(form,new object[] {new PreviewOptions(),false});
                    MethodInfo toggle=typeof(MainForm).GetMethod("ToggleRecording",Fields),previewToggle=typeof(MainForm).GetMethod("ToggleLive",Fields);
                    PreviewBox stage=(PreviewBox)typeof(MainForm).GetField("preview",Fields).GetValue(form);
                    toggle.Invoke(form,null);
                    Wait(()=>stage.Current!=null&&stage.HasCompanion,4500);
                    Tests.Check(Recording(form)!=null&&(bool)typeof(MainForm).GetField("busy",Fields).GetValue(form)&&stage.Current!=null&&stage.HasCompanion,"dual recording automatically previews both screens while the device remains busy");
                    int first=stage.Current==null?-1:((Bitmap)stage.Current).GetPixel(0,0).R;
                    Wait(()=>stage.Current!=null&&((Bitmap)stage.Current).GetPixel(0,0).R!=first,2500);
                    Tests.Check(stage.Current!=null&&((Bitmap)stage.Current).GetPixel(0,0).R!=first,"recording preview shows changing frames instead of a frozen poster");
                    typeof(MainForm).GetField("infoDetail",Fields).SetValue(form,"模拟设备 · 本机录屏/取景并行验证，不是 Thor 实机采样");
                    using(Bitmap screenshot=new Bitmap(form.Width,form.Height))
                    { form.DrawToBitmap(screenshot,new Rectangle(0,0,form.Width,form.Height)); screenshot.Save(Path.Combine(Tests.Root,"recording-preview-window.png")); }
                    Tests.Check(typeof(MainForm).GetField("liveStreams",Fields).GetValue(form)==null&&!(bool)typeof(MainForm).GetField("livePreview",Fields).GetValue(form),"recording preview starts no additional H.264 encoders and preserves prior preview intent");
                    previewToggle.Invoke(form,null);
                    Task pending=(Task)typeof(MainForm).GetField("fallbackCapture",Fields).GetValue(form); Wait(()=>pending.IsCompleted,2500);
                    int count=Count(group,"upper"); Pump(250);
                    Tests.Check(!((bool)typeof(MainForm).GetField("recordingPreviewEnabled",Fields).GetValue(form))&&Recording(form)!=null&&Count(group,"upper")==count,"pausing the recording preview stops grabs without stopping either recorder");
                    previewToggle.Invoke(form,null); Wait(()=>Count(group,"upper")>count,2500);
                    Tests.Check(Count(group,"upper")>count&&(bool)typeof(MainForm).GetField("recordingPreviewEnabled",Fields).GetValue(form),"recording preview can resume while recording continues");
                    File.WriteAllText(Path.Combine(group,"preview-fail"),"fail");
                    Wait(()=>!(bool)typeof(MainForm).GetField("recordingPreviewEnabled",Fields).GetValue(form),2500);
                    Tests.Check(Recording(form)!=null&&!(bool)typeof(MainForm).GetField("recordingPreviewEnabled",Fields).GetValue(form),"a preview failure leaves the original recording running and offers retry");
                    File.Delete(Path.Combine(group,"preview-fail")); previewToggle.Invoke(form,null);
                    Wait(()=>Count(group,"upper")>count+1,1500);
                    toggle.Invoke(form,null); Wait(()=>Recording(form)==null&&!(bool)typeof(MainForm).GetField("busy",Fields).GetValue(form),7000);
                    Tests.Check(Recording(form)==null&&Directory.GetFiles(group,"thor-*.mp4").Length==2&&File.Exists(Path.Combine(group,"1001.stop"))&&File.Exists(Path.Combine(group,"1002.stop")),"one stop saves both original MP4s after concurrent preview");
                    count=Count(group,"upper"); Pump(200);
                    Tests.Check(Count(group,"upper")==count&&!(bool)typeof(MainForm).GetField("livePreview",Fields).GetValue(form),"finishing recording drains late grabs and restores previously disabled live preview");
                    form.Close(); Wait(()=>form.IsDisposed,7000);
                    Tests.Check(form.IsDisposed,"recording preview and local playback clean up before window disposal");
                });
            }
            finally
            {
                Environment.SetEnvironmentVariable("THOR_RECORD_PREVIEW_TEST",null);
                Environment.SetEnvironmentVariable("THOR_CAPTURE_GROUP_ROOT",oldGroup); Tests.FakeMode("normal");
            }
        }
        private static RecordingSession Recording(MainForm form) { return (RecordingSession)typeof(MainForm).GetField("recording",Fields).GetValue(form); }
        internal static void RunStream(string ffmpeg)
        {
            string oldGroup=Environment.GetEnvironmentVariable("THOR_CAPTURE_GROUP_ROOT");
            string group=Path.Combine(Tests.Root,"record-preview-live-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(group);
            PrepareMovie();
            Environment.SetEnvironmentVariable("THOR_CAPTURE_GROUP_ROOT",group); Environment.SetEnvironmentVariable("THOR_RECORD_PREVIEW_TEST",group);
            File.WriteAllText(Path.Combine(group,"lower.start"),"single recording fixture barrier");
            Tests.FakeMode("paired-running");
            try
            {
                WithUi(delegate(MainForm form)
                {
                    typeof(MainForm).GetField("serial",Fields).SetValue(form,"test"); typeof(MainForm).GetField("captureDir",Fields).SetValue(form,group);
                    var screens=(List<DeviceDisplay>)typeof(MainForm).GetField("displays",Fields).GetValue(form);
                    screens.Add(new DeviceDisplay {Width=128,Height=72,PhysicalId="101"}); screens.Add(new DeviceDisplay {LogicalId=4,Width=124,Height=108,PhysicalId="202",IsLower=true});
                    typeof(MainForm).GetMethod("ApplyPreviewOptions",Fields).Invoke(form,new object[] {new PreviewOptions(),false});
                    MethodInfo live=typeof(MainForm).GetMethod("ToggleLive",Fields),record=typeof(MainForm).GetMethod("ToggleRecording",Fields);
                    PreviewBox stage=(PreviewBox)typeof(MainForm).GetField("preview",Fields).GetValue(form);
                    live.Invoke(form,null); Wait(()=>stage.Current!=null&&stage.HasCompanion,6000);
                    Tests.Check(typeof(MainForm).GetField("liveStreams",Fields).GetValue(form)!=null&&stage.HasCompanion,"synthetic dual video preview runs before recording");
                    record.Invoke(form,null); Wait(()=>Recording(form)!=null&&stage.HasCompanion&&Count(group,"upper")>0,6000);
                    Tests.Check(Recording(form)!=null&&stage.HasCompanion&&typeof(MainForm).GetField("liveStreams",Fields).GetValue(form)==null,"starting single recording releases video preview and displays continuous dual grabs");
                    typeof(MainForm).GetMethod("ShowSelected",Fields).Invoke(form,null);
                    Tests.Check(stage.Current!=null&&stage.HasCompanion,"gallery refresh cannot overwrite an active recording preview");
                    record.Invoke(form,null); Wait(()=>Recording(form)==null&&!(bool)typeof(MainForm).GetField("busy",Fields).GetValue(form)&&typeof(MainForm).GetField("liveStreams",Fields).GetValue(form)!=null,7000);
                    Tests.Check((bool)typeof(MainForm).GetField("livePreview",Fields).GetValue(form)&&typeof(MainForm).GetField("liveStreams",Fields).GetValue(form)!=null&&Directory.GetFiles(group,"thor-*.mp4").Length==1,"single recording restores prior dual video preview and saves its original");
                    form.Close(); Wait(()=>form.IsDisposed,7000);
                    Tests.Check(form.IsDisposed,"restored video preview cleans up after recording");
                });
            }
            finally { Environment.SetEnvironmentVariable("THOR_RECORD_PREVIEW_TEST",null); Environment.SetEnvironmentVariable("THOR_CAPTURE_GROUP_ROOT",oldGroup); Tests.FakeMode("normal"); }
        }
        private static void WithUi(Action<MainForm> test)
        {
            Exception failure=null;
            using(MainForm form=new MainForm(true) {Opacity=0,ShowInTaskbar=false})
            using(System.Windows.Forms.Timer launch=new System.Windows.Forms.Timer {Interval=1})
            {
                typeof(MainForm).GetField("recordSystemAudio",Fields).SetValue(form,false);
                form.Shown+=delegate { launch.Start(); };
                launch.Tick+=delegate
                {
                    launch.Stop();
                    try { test(form); }
                    catch(Exception error) { failure=error; form.Dispose(); }
                };
                // A real message loop keeps async event continuations on the UI thread.
                Application.Run(form);
            }
            if(failure!=null) throw failure;
        }
        private static int Count(string group,string slug)
        { int count; try { return int.TryParse(File.ReadAllText(Path.Combine(group,slug+".preview-count")),out count)?count:0; } catch { return 0; } }
        private static void Wait(Func<bool> condition,int timeout)
        { Stopwatch timer=Stopwatch.StartNew(); while(!condition()&&timer.ElapsedMilliseconds<timeout) { Application.DoEvents(); Thread.Sleep(10); } }
        private static void Pump(int duration) { Stopwatch timer=Stopwatch.StartNew(); while(timer.ElapsedMilliseconds<duration) { Application.DoEvents(); Thread.Sleep(10); } }
        internal static int FakeAdb(string command,string mode,string dir)
        {
            string group=Environment.GetEnvironmentVariable("THOR_RECORD_PREVIEW_TEST");
            if(string.IsNullOrEmpty(group)) return Tests.NotHandled;
            if(command.Contains(" pull "))
            {
                var match=System.Text.RegularExpressions.Regex.Match(command,@"\spull\s+\S+\s+(.+)$");
                File.Copy(Path.Combine(dir,"record-preview-fixture.mp4"),match.Groups[1].Value.Trim().Trim('"'),false); return 0;
            }
            if(!command.Contains("screencap")) return Tests.NotHandled;
            if(File.Exists(Path.Combine(group,"preview-fail"))) { Console.OpenStandardOutput().WriteByte(0); return 0; }
            bool lower=command.Contains("-d 202"); string slug=lower?"lower":"upper";
            int count=Count(group,slug)+1; File.WriteAllText(Path.Combine(group,slug+".preview-count"),count.ToString());
            int w=lower?124:128,h=lower?108:72; byte red=(byte)(30+count%8*25);
            Thread.Sleep(40);
            if(command.Contains(" -p "))
            {
                using(Bitmap image=new Bitmap(w,h)) using(Graphics g=Graphics.FromImage(image))
                { g.Clear(Color.FromArgb(red,80,lower?180:20)); image.Save(Console.OpenStandardOutput(),System.Drawing.Imaging.ImageFormat.Png); }
            }
            else
            {
                byte[] data=new byte[12+w*h*4]; Buffer.BlockCopy(BitConverter.GetBytes(w),0,data,0,4); Buffer.BlockCopy(BitConverter.GetBytes(h),0,data,4,4); Buffer.BlockCopy(BitConverter.GetBytes(1),0,data,8,4);
                for(int i=12;i<data.Length;i+=4) { data[i]=red; data[i+1]=80; data[i+2]=(byte)(lower?180:20); data[i+3]=255; }
                Console.OpenStandardOutput().Write(data,0,data.Length);
            }
            return 0;
        }
        private static void PrepareMovie()
        { File.Copy(Path.Combine(Path.GetDirectoryName(Adb.AppDir()),"tests","fixtures","recording-preview.mp4"),Path.Combine(Tests.Root,"record-preview-fixture.mp4"),true); }
    }
}
