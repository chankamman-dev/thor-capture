using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThorCapture
{
    internal static class PreviewOptionsTests
    {
        internal static void Run()
        {
            DeviceDisplay top=new DeviceDisplay { Width=1920,Height=1080,PhysicalId="101" },bottom=new DeviceDisplay { Width=1240,Height=1080,PhysicalId="202",IsLower=true };
            PreviewOptions defaults=new PreviewOptions();
            double deadline=0; int ticks=0;
            for(int i=0;i<1000;i+=15) if(PreviewPacer.Due(i/1000.0,60,ref deadline)) ticks++;
            Tests.Check(ticks>=59&&ticks<=61,"60 fps presentation stays near its target on a 15 ms UI clock");
            deadline=0; PreviewPacer.Due(0,30,ref deadline); bool afterStall=PreviewPacer.Due(5,30,ref deadline);
            Tests.Check(afterStall&&!PreviewPacer.Due(5,30,ref deadline)&&deadline>5,"a stalled UI skips missed presentation slots instead of replaying frames");
            Tests.Check(defaults.SizeFor(top)==new Size(960,540)&&defaults.SizeFor(bottom)==new Size(620,540),"default preview retains existing per-screen dimensions");
            PreviewOptions native=PreviewOptions.Preset(3);
            Tests.Check(native.SizeFor(top)==new Size(1920,1080)&&native.SizeFor(bottom)==new Size(1240,1080),"native preset retains both screen resolutions");
            PreviewOptions low=PreviewOptions.Preset(0);
            Tests.Check(low.SizeFor(top)==new Size(640,360)&&low.BitRate==2000000,"low latency preset reduces source pixels and bitrate");
            Size odd=defaults.SizeFor(new DeviceDisplay {Width=1241,Height=1081});
            Tests.Check(odd.Width%2==0&&odd.Height%2==0&&odd.Width<=1241,"odd source dimensions produce even encoder sizes");
            Tests.Reject(()=>defaults.SizeFor(new DeviceDisplay {Width=0,Height=1080}),"invalid display cannot allocate preview buffers");
            PreviewOptions invalid=PreviewOptions.Read((key,fallback)=>"-1");
            Tests.Check(invalid.MaxHeight==540&&invalid.BitRate==3000000&&invalid.DisplayFps==60&&invalid.Screens==0&&!invalid.PixelMode,"corrupt saved preview values restore safe defaults");
            PreviewOptions restored=PreviewOptions.Read((key,fallback)=>key=="previewHeight"?"0":key=="previewBitRate"?"12500000":key=="previewFps"?"30":key=="previewScreens"?"2":"1");
            Tests.Check(restored.MaxHeight==0&&restored.BitRate==12500000&&restored.DisplayFps==30&&restored.Screens==2&&restored.PixelMode,"custom preview settings restore without changing unrelated settings");
            Tests.Check(restored.Includes(bottom)&&!restored.Includes(top),"single-screen preview selection uses role rather than logical display ID");
            string command=LiveVideo.SourceArguments("test","202","/data/local/tmp/owned",620,540,12500000);
            Tests.Check(command.Contains("--display-id 202 --size 620x540 --bit-rate 12500000")&&!command.Contains("--frame-rate"),"source receives custom bitrate and physical ID without unsupported fps flags");
            Tests.Check(LiveVideo.DecoderArguments(620,540).Contains("-flush_packets 1")&&!LiveVideo.DecoderArguments(620,540).Contains("nobuffer"),"decoder flushes frames without discarding the initial keyframe");
            using(Bitmap original=new Bitmap(901,551))
            {
                original.SetPixel(900,550,Color.FromArgb(255,17,43,119));
                using(Bitmap copy=LiveFrame.ResizePng(original,0))
                    Tests.Check(copy.Size==original.Size&&copy.GetPixel(900,550)==original.GetPixel(900,550),"native PNG path preserves pixel dimensions and sample colors");
            }
            byte[] raw=new byte[12+902*552*4]; Buffer.BlockCopy(BitConverter.GetBytes(902),0,raw,0,4); Buffer.BlockCopy(BitConverter.GetBytes(552),0,raw,4,4); Buffer.BlockCopy(BitConverter.GetBytes(1),0,raw,8,4);
            raw[12]=17; raw[13]=43; raw[14]=119; raw[15]=255;
            using(Bitmap frame=LiveFrame.Decode(raw,0))
                Tests.Check(frame.Size==new Size(902,552)&&frame.GetPixel(0,0).R==17,"fast raw fallback also respects native dimensions and channel order");
            using(PreviewOptionsDialog dialog=new PreviewOptionsDialog(restored))
                Tests.Check(dialog.Value.PixelMode&&dialog.Value.BitRate==12500000&&dialog.Value.Screens==2,"settings dialog opens with the configured custom values");
            using(PreviewBox stage=new PreviewBox {Size=new Size(760,940)})
            {
                stage.SetLiveFrame(new Bitmap(124,108),true,true); stage.Mode=PreviewMode.Fit;
                Tests.Check(stage.Current!=null&&stage.Current.Width==124&&stage.LowerScreen&&!stage.HasCompanion,"lower-only preview remains visible in single-image mode");
            }
            string settingsPath=Path.Combine(Adb.AppDir(),"thor-capture-settings.txt");
            byte[] savedFile=File.Exists(settingsPath)?File.ReadAllBytes(settingsPath):null;
            var values=(System.Collections.Generic.Dictionary<string,string>)typeof(Settings).GetField("values",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
            var savedValues=new System.Collections.Generic.Dictionary<string,string>(values);
            try
            {
                values.Clear();
                File.WriteAllText(settingsPath,"skin=purple\npreviewHeight=720\npreviewFps=27\npreviewScreens=2\n");
                using(MainForm form=new MainForm(true))
                {
                    PreviewOptions configuration=(PreviewOptions)typeof(MainForm).GetField("previewOptions",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
                    Tests.Check(configuration.MaxHeight==720&&configuration.DisplayFps==27&&configuration.Screens==2,"fresh form loads saved preview settings before constructing its toolbar");
                    configuration.BitRate=12500000;
                    Tests.Check(configuration.Save()&&File.ReadAllText(settingsPath).Contains("previewBitRate=12500000")&&Settings.Get("skin","")=="purple","saving preview parameters preserves unrelated appearance settings");
                }
            }
            finally
            {
                if(savedFile==null) File.Delete(settingsPath); else File.WriteAllBytes(settingsPath,savedFile);
                values.Clear(); foreach(var entry in savedValues) values[entry.Key]=entry.Value;
            }
        }
        internal static void RunStream(string ffmpeg)
        {
            Tests.FakeMode("preview-video");
            DeviceDisplay display=new DeviceDisplay {Width=128,Height=72,PhysicalId="101"};
            PreviewOptions options=new PreviewOptions {MaxHeight=36,BitRate=8000000,DisplayFps=10};
            using(LiveVideo video=new LiveVideo("test",display,ffmpeg,options))
            {
                options.DisplayFps=1; options.MaxHeight=720;
                Stopwatch clock=Stopwatch.StartNew(); long first=-1; int delivered=0; bool sizesValid=true;
                while(clock.ElapsedMilliseconds<2200&&video.Error==null)
                {
                    using(Bitmap frame=video.TakeLatest())
                    { if(frame!=null) { delivered++; if(first<0) first=clock.ElapsedMilliseconds; sizesValid=sizesValid&&frame.Size==new Size(64,36); } }
                    Thread.Sleep(5);
                }
                Tests.Check(sizesValid&&delivered>0,"custom-sized decoded preview frames preserve the configured snapshot");
                Tests.Check(video.Error==null&&video.Frames>=40&&delivered>=5&&delivered<=23,"display fps cap drops old frames while the source continues decoding");
                Tests.Check(video.Replaced>10&&video.Presented==delivered&&video.QueueAge<.15,"one-frame mailbox replaces stale frames without accumulating a queue");
                Tests.Check(first>=0&&first<1500,"custom decoder delivers its first frame while input remains open");
                File.WriteAllText(Path.Combine(Tests.Root,"preview-host-metrics.txt"),"synthetic source, not device latency\nfirstFrameMs="+first+"\ndecoded="+video.Frames+"\npresented="+video.Presented+"\nreplaced="+video.Replaced+"\nlatestMailboxAgeMs="+(video.QueueAge*1000).ToString("0.0"));
            }
            using(MainForm form=new MainForm(true) {Opacity=0,ShowInTaskbar=false})
            {
                BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
                form.Show(); Application.DoEvents();
                typeof(MainForm).GetField("serial",flags).SetValue(form,"test");
                var screens=(System.Collections.Generic.List<DeviceDisplay>)typeof(MainForm).GetField("displays",flags).GetValue(form);
                screens.Add(display); screens.Add(new DeviceDisplay {Width=124,Height=108,PhysicalId="202",IsLower=true});
                MethodInfo apply=typeof(MainForm).GetMethod("ApplyPreviewOptions",flags),toggle=typeof(MainForm).GetMethod("ToggleLive",flags);
                apply.Invoke(form,new object[] {new PreviewOptions {Screens=2,MaxHeight=36,BitRate=5000000,DisplayFps=30},false}); toggle.Invoke(form,null);
                PreviewBox stage=(PreviewBox)typeof(MainForm).GetField("preview",flags).GetValue(form);
                Wait(()=>stage.Current!=null,6000);
                LiveVideo[] streams=(LiveVideo[])typeof(MainForm).GetField("liveStreams",flags).GetValue(form);
                Tests.Check(streams!=null&&streams.Length==1&&streams[0].Display.IsLower&&stage.LowerScreen&&stage.Current!=null,"real form starts only the configured lower stream");
                apply.Invoke(form,new object[] {new PreviewOptions {PixelMode=true},false});
                Wait(()=>stage.HasCompanion&&stage.Current!=null,6000);
                Tests.Check((bool)typeof(MainForm).GetField("liveFallback",flags).GetValue(form)&&stage.HasCompanion,"applying PNG mode restarts preview and rejects the old generation");
                apply.Invoke(form,new object[] {new PreviewOptions(),false});
                Wait(()=>typeof(MainForm).GetField("liveStreams",flags).GetValue(form)!=null&&stage.HasCompanion,6000);
                Tests.Check(!(bool)typeof(MainForm).GetField("liveFallback",flags).GetValue(form)&&stage.HasCompanion,"applying video mode restores dual streaming after PNG cleanup");
                form.Close(); Wait(()=>form.IsDisposed,8000);
                Tests.Check(form.IsDisposed,"custom preview window awaits its owned workers on closure");
            }
        }
        private static void Wait(Func<bool> ready,int timeout)
        { Stopwatch clock=Stopwatch.StartNew(); while(!ready()&&clock.ElapsedMilliseconds<timeout) { Application.DoEvents(); Thread.Sleep(10); } }
    }
}
