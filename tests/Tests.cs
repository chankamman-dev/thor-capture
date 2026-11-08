using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    // Feature packages add their own suites without editing this file:
    //   internal static class <Name>Tests
    //   {
    //       internal static void Run() { ... Tests.Check(...) ... }       // required
    //       internal static void RunLive() { ... }                           // optional, only with -Live
    //       internal static int FakeAdb(string command, string mode, string dir)
    //       { ... return Tests.NotHandled; }                                 // optional fake adb answers
    //   }
    // Suites run after the core checks, in name order, while Adb.ExePath points at this test
    // exe (the fake adb). THOR_TEST_FILTER (set by test-capture-tool.ps1 -Filter) limits which
    // suites run; the core checks always run.
    internal static class Tests
    {
        internal const int NotHandled = int.MinValue;
        private static int passed;
        private static string root;

        // Scratch directory for test files; also where the fake adb reads fixtures from.
        internal static string Root { get { return root; } }

        internal static void Check(bool value, string label)
        {
            if (!value) throw new Exception("FAIL: " + label);
            Console.WriteLine("PASS: " + label); passed++;
        }
        internal static void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); } catch { rejected = true; }
            Check(rejected, label);
        }

        // Selects how the fake adb answers the next commands.
        internal static void FakeMode(string mode)
        {
            Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE", mode);
        }

        private static List<Type> Suites()
        {
            List<Type> suites = new List<Type>();
            foreach (Type type in typeof(Tests).Assembly.GetTypes())
                if (type != typeof(Tests) && type.IsAbstract && type.IsSealed && type.Name.EndsWith("Tests", StringComparison.Ordinal))
                    suites.Add(type);
            suites.Sort(delegate(Type a, Type b) { return string.CompareOrdinal(a.Name, b.Name); });
            return suites;
        }

        private static MethodInfo SuiteMethod(Type suite, string name, Type[] parameters)
        {
            return suite.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, parameters, null);
        }

        private static void RunSuites(string method)
        {
            string filter = Environment.GetEnvironmentVariable("THOR_TEST_FILTER");
            foreach (Type suite in Suites())
            {
                if (!string.IsNullOrEmpty(filter) && suite.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                MethodInfo run = SuiteMethod(suite, method, Type.EmptyTypes);
                if (run == null) continue;
                Console.WriteLine("== " + suite.Name + "." + method);
                FakeMode("normal");
                int before = passed;
                Stopwatch timer = Stopwatch.StartNew();
                try { run.Invoke(null, null); }
                catch (TargetInvocationException failure) { throw new Exception(suite.Name + "." + method + " failed", failure.InnerException); }
                Console.WriteLine("   " + (passed - before) + " passed in " + timer.ElapsedMilliseconds + " ms");
            }
            FakeMode("normal");
        }
        [STAThread]
        private static int Main(string[] args)
        {
            System.Windows.Forms.Application.ThreadException+=delegate(object sender,ThreadExceptionEventArgs e)
            {
                Console.Error.WriteLine("UI failure "+e.Exception.GetType().FullName+": "+e.Exception.Message);
                Console.Error.WriteLine(e.Exception.StackTrace); Environment.Exit(2);
            };
            AppDomain.CurrentDomain.UnhandledException+=delegate(object sender,UnhandledExceptionEventArgs e)
            {
                Exception failure=e.ExceptionObject as Exception;
                if(failure!=null) { Console.Error.WriteLine("Unhandled "+failure.GetType().FullName+": "+failure.Message); Console.Error.WriteLine(failure.StackTrace); }
            };
            if (args.Length > 0 && args[0] != "--live" && args[0] != "--export-check" && args[0] != "--stream-check") return Fake(args);
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "thor-capture-tests");
            Directory.CreateDirectory(root);
            string realAdb = Adb.ExePath;
            string adbConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "artifacts", "adb-path.txt");
            if (realAdb == "adb" && File.Exists(adbConfig)) realAdb = File.ReadAllText(adbConfig).Trim();
            try
            {
                string viewport = "DisplayViewport{displayId=4, uniqueId='local:4630946482288158084', deviceWidth=1240, deviceHeight=1080}";
                var displays = Adb.ParseDisplays(viewport + "DisplayViewport{displayId=0, uniqueId='local:4630946441858561667', deviceWidth=1920, deviceHeight=1080}" + viewport);
                Check(displays.Count == 2 && displays[1].Label == "下屏" && displays[1].Slug == "lower", "logical id 4 maps to lower, duplicate viewports removed");
                Check(displays[0].PhysicalId == "4630946441858561667", "64-bit physical id preserved");
                Check(Adb.ParseDisplays("invalid").Count == 0, "unknown display output fails closed");

                Adb.ExePath = Assembly.GetExecutingAssembly().Location;
                Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE", "normal");
                Stopwatch timer = Stopwatch.StartNew();
                var result = Adb.Run("hang", 200);
                Check(result.Code == -1 && timer.ElapsedMilliseconds < 3500, "text timeout terminates hung process");
                result = Adb.Run("flood", 5000);
                Check(result.Code == 0 && result.Out.Length > 100000 && result.Err.Length > 100000, "stdout/stderr drained without deadlock");
                Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE", "unauthorized");
                Reject(() => Adb.Devices(), "unauthorized devices produce actionable error");

                MethodInfo binary = typeof(Adb).GetMethod("RunBinary", BindingFlags.NonPublic | BindingFlags.Static);
                object[] binaryArgs = { "hang", 200, null };
                timer.Restart();
                Check(binary.Invoke(null, binaryArgs) == null && timer.ElapsedMilliseconds < 3500, "binary timeout terminates hung process");
                byte[] png;
                using (Bitmap b = new Bitmap(24, 16))
                using (MemoryStream ms = new MemoryStream()) { b.Save(ms, System.Drawing.Imaging.ImageFormat.Png); png = ms.ToArray(); }
                File.WriteAllBytes(Path.Combine(root, "fixture.png"), png);
                Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_ROOT", root);
                MetadataAndInputTests(displays);
                byte[] raw=new byte[24];
                Buffer.BlockCopy(BitConverter.GetBytes(2),0,raw,0,4); Buffer.BlockCopy(BitConverter.GetBytes(1),0,raw,4,4);
                Buffer.BlockCopy(BitConverter.GetBytes(1),0,raw,8,4); Buffer.BlockCopy(BitConverter.GetBytes(1),0,raw,12,4);
                raw[16]=255; raw[19]=255; raw[21]=255; raw[23]=255;
                using(Bitmap decoded=LiveFrame.Decode(raw)) Check(decoded.GetPixel(0,0).R==255&&decoded.GetPixel(1,0).G==255,"raw preview decodes packed RGBA with colorspace header");
                byte[] legacy=new byte[20]; Buffer.BlockCopy(raw,0,legacy,0,12); Buffer.BlockCopy(raw,16,legacy,12,8);
                using(Bitmap decoded=LiveFrame.Decode(legacy)) Check(decoded.Width==2,"legacy raw preview header supported");
                Reject(()=>LiveFrame.Decode(new byte[13]),"malformed raw frame rejected");
                raw[12]=2; Reject(()=>LiveFrame.Decode(raw),"wide gamut raw frame falls back instead of guessing colors");
                bool usedRaw;
                using(Bitmap decoded=LiveFrame.Capture("test","123",true,out usedRaw)) Check(!usedRaw&&decoded.Width==24,"unsupported raw capture falls back to PNG");
                PadState pose=PadState.Blend(new PadState(),new PadState { Buttons=PadButton.Up|PadButton.Right },.55f);
                Check(pose.TiltX>0&&pose.TiltX<1&&pose.TiltY<0,"diagonal dpad tilt follows both axes");
                for(int i=0;i<15;i++) pose=PadState.Blend(pose,new PadState(),.55f);
                Check(Math.Abs(pose.TiltX)<.001f&&Math.Abs(pose.TiltY)<.001f,"dpad release settles without stuck tilt");
                using(Bitmap rest=new Bitmap(160,160))
                using(Bitmap left=new Bitmap(160,160))
                using(Bitmap down=new Bitmap(160,160))
                {
                    using(Graphics g=Graphics.FromImage(rest)) DeviceArt.Stick(g,DeviceSkin.All[2],80,80,0,0,false);
                    using(Graphics g=Graphics.FromImage(left)) DeviceArt.Stick(g,DeviceSkin.All[2],80,80,-1,0,false);
                    using(Graphics g=Graphics.FromImage(down)) DeviceArt.Stick(g,DeviceSkin.All[2],80,80,0,0,true);
                    bool fixedRing=true;
                    for(int yy=75;yy<=85;yy++)
                    { fixedRing &= rest.GetPixel(130,yy)==left.GetPixel(130,yy); fixedRing &= rest.GetPixel(130,yy)==down.GetPixel(130,yy); }
                    Check(fixedRing,"unoccluded lamp rim stays fixed during stick travel and L3 press");
                    Check(left.GetPixel(120,80).B>rest.GetPixel(120,80).B,"stick travel reveals the fixed lamp behind the cap");
                    using(Bitmap unlit=new Bitmap(160,160))
                    {
                        using(Graphics g=Graphics.FromImage(unlit)) DeviceArt.Stick(g,DeviceSkin.All[2].WithGlow(Color.Empty),80,80,0,0,false);
                        // r=60: beyond the ring (49) and its edge bloom (56); the wash fades out towards the rim.
                        Color on=rest.GetPixel(80,140),off=unlit.GetPixel(80,140);
                        Check((on.B-off.B)-(on.R-off.R)>3,"lamp casts coloured reflection onto bowl beyond the emitter");
                    }
                    Check(DeviceSkin.All[1].WithLamp(false,Color.Red,1f).Glow.IsEmpty,"lamp switch turns the ring light off");
                    DeviceSkin auto=DeviceSkin.All[1].WithLamp(true,Color.Empty,.5f);
                    Check(auto.Glow==DeviceSkin.All[1].DefaultGlow&&Math.Abs(auto.GlowLevel-.5f)<1e-6,"lamp follows the colourway colour unless one is picked");
                    Check(DeviceSkin.All[1].WithLamp(true,Color.Red,5f).GlowLevel==1f&&DeviceSkin.All[1].WithLamp(true,Color.Red,0f).GlowLevel>0,"lamp brightness stays within range");
                    using(Bitmap dim=new Bitmap(160,160))
                    {
                        using(Graphics g=Graphics.FromImage(dim)) DeviceArt.Stick(g,DeviceSkin.All[2].WithLamp(true,Color.Empty,.15f),80,80,-1,0,false);
                        Color bright=left.GetPixel(120,80),low=dim.GetPixel(120,80);
                        Check((bright.B-bright.R)>(low.B-low.R)+20,"lower lamp brightness renders a fainter ring");
                    }
                }
                Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE", "normal");
                string error;
                Check(Adb.Capture("test", "123", out error) != null, "valid exec-out capture");
                Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE", "invalid-stream");
                Check(Adb.Capture("test", "123", out error) != null, "corrupt stream uses validated pull fallback");
                Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE", "corrupt-all");
                Check(Adb.Capture("test", "123", out error) == null, "truncated PNG rejected in both paths");
                Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE", "nonzero-stream");
                Check(Adb.Capture("test", "123", out error) == null, "valid PNG with failed exit status not accepted");

                Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE", "normal");
                var recording = new Recording("test", displays[1], root);
                recording.CheckSupport();
                string saved = recording.Execute();
                Check(File.Exists(saved) && !File.Exists(saved + ".partial"), "recording saved after MP4 structural validation");
                File.WriteAllBytes(Path.Combine(root, "bad.mp4"), new byte[] { 0, 0, 0, 40, 102, 116, 121, 112 });
                Reject(() => Recording.ValidateMp4(Path.Combine(root, "bad.mp4")), "truncated MP4 rejected");
                Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE", "wrong-pid");
                Reject(() => recording.Stop(), "stop refuses a process belonging to another recording");
                SessionTests(displays);
                using (PreviewBox preview = new PreviewBox())
                {
                    preview.SetImage(new Bitmap(192, 108));
                    preview.SetCompanion(new Bitmap(124, 108));
                    using (Bitmap frame = preview.ExportDevice())
                    {
                        frame.Save(Path.Combine(root, "frame-test.png"));
                        Check(frame.Size == PreviewBox.ExportSize && frame.Width > 1920, "device frame export at native top-screen scale");
                        Check(frame.GetPixel(2, 2).A == 0, "device frame export has transparent background");
                    }
                    foreach (DeviceSkin skin in DeviceSkin.All)
                    {
                        preview.Skin = skin;
                        using (Bitmap frame = preview.ExportDevice(false)) frame.Save(Path.Combine(root, "frame-" + skin.Key + ".png"));
                    }
                }
                RunSuites("Run");
                Adb.ExePath = realAdb;
                if(args.Length==2&&args[0]=="--stream-check")
                {
                    Adb.ExePath=Assembly.GetExecutingAssembly().Location;
                    StreamTests(args[1],displays);
                    foreach(Type suite in Suites())
                    {
                        string streamFilter=Environment.GetEnvironmentVariable("THOR_TEST_FILTER");
                        if(!string.IsNullOrEmpty(streamFilter)&&suite.Name.IndexOf(streamFilter,StringComparison.OrdinalIgnoreCase)<0) continue;
                        MethodInfo run=SuiteMethod(suite,"RunStream",new Type[] { typeof(string) });
                        if(run!=null) run.Invoke(null,new object[] { args[1] });
                    }
                    Adb.ExePath=realAdb;
                }
                if (args.Length == 1 && args[0] == "--live") { Live(); RunSuites("RunLive"); }
                if (args.Length == 3 && args[0] == "--export-check")
                {
                    string source=args[1],destination=args[2];
                    File.WriteAllLines(source+".input.csv",new string[] { "#thor-input-v1", PadTimeline.Format(0,100,new PadState()), PadTimeline.Format(400,100.4,new PadState { Buttons=PadButton.A|PadButton.Up|PadButton.L1,LX=0.8f,LY=-0.7f,RX=-0.6f,RY=0.4f }), PadTimeline.Format(1400,101.4,new PadState { Buttons=PadButton.Down|PadButton.Right }), PadTimeline.Format(2400,102.4,new PadState()) });
                    VideoExport.Export(source,destination,DeviceSkin.All[1],0,p=>Console.WriteLine("EXPORT: "+p+"%"),CancellationToken.None);
                    Check(File.Exists(destination),"framed export completed");
                    using(CancellationTokenSource cancelled=new CancellationTokenSource())
                    {
                        cancelled.Cancel();
                        string cancelledPath=Path.Combine(root,"cancelled-export.mp4");
                        Reject(()=>VideoExport.Export(source,cancelledPath,DeviceSkin.All[0],0,p=>{},cancelled.Token),"cancelled export stops external processes");
                        Check(!File.Exists(cancelledPath),"cancel does not publish partial video");
                    }
                }
                Console.WriteLine("TOTAL PASS: " + passed);
                return 0;
            }
            catch (Exception failure)
            {
                // Keep diagnostics usable if an exception's ToString implementation fails.
                for(int depth=0;failure!=null&&depth<8;depth++,failure=failure.InnerException)
                { Console.Error.WriteLine(failure.GetType().FullName+": "+failure.Message); Console.Error.WriteLine(failure.StackTrace); }
                return 1;
            }
            finally { Adb.ExePath = realAdb; }
        }

        private static void SessionTests(System.Collections.Generic.List<DeviceDisplay> displays)
        {
            string group=Path.Combine(root,"paired-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(group);
            Environment.SetEnvironmentVariable("THOR_CAPTURE_GROUP_ROOT",group);
            Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE","paired-auto");
            var session=new RecordingSession("test",displays,group);
            Stopwatch timer=Stopwatch.StartNew(); string[] files=session.Execute();
            Check(File.Exists(files[0])&&File.Exists(files[1])&&timer.ElapsedMilliseconds<4000,"dual encoders launch concurrently through a shared barrier");
            Check(Path.GetFileName(files[0]).Replace("-upper-","-lower-")==Path.GetFileName(files[1])&&session.Tracks[0].Remote!=session.Tracks[1].Remote,"dual recordings pair by name with distinct remote files");
            File.WriteAllText(session.TimelineSource+".input.csv","#shared timeline"); session.CopyTimeline();
            Check(File.ReadAllText(files[1]+".input.csv")==File.ReadAllText(files[0]+".input.csv"),"paired videos share the same input timeline origin");

            group=Path.Combine(root,"paired-stop-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(group);
            Environment.SetEnvironmentVariable("THOR_CAPTURE_GROUP_ROOT",group);
            Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE","paired-running");
            session=new RecordingSession("test",displays,group);
            Task<string[]> run=Task.Run(()=>session.Execute());
            for(int i=0;i<100&&!File.Exists(Path.Combine(group,"lower.start"));i++) Thread.Sleep(20);
            timer.Restart(); session.Stop();
            Check(run.Wait(5000)&&File.Exists(Path.Combine(group,"1001.stop"))&&File.Exists(Path.Combine(group,"1002.stop")),"one stop finalizes and downloads both encoders");
            Check(timer.ElapsedMilliseconds<5000,"paired stop is bounded and parallel");

            group=Path.Combine(root,"paired-fail-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(group);
            Environment.SetEnvironmentVariable("THOR_CAPTURE_GROUP_ROOT",group);
            Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE","paired-fail");
            session=new RecordingSession("test",displays,group); timer.Restart();
            Reject(()=>session.Execute(),"dual encoder failure is visible instead of claiming complete recording");
            Check(timer.ElapsedMilliseconds<5000&&session.Saved[0]!=null&&File.Exists(Path.Combine(group,"1001.stop")),"failed encoder stops its peer and preserves the successful original");

            group=Path.Combine(root,"paired-pull-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(group);
            Environment.SetEnvironmentVariable("THOR_CAPTURE_GROUP_ROOT",group);
            Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE","paired-pull-fail");
            session=new RecordingSession("test",displays,group);
            Reject(()=>session.Execute(),"a failed screen download cannot publish a paired success");
            Check(session.Saved[0]!=null&&File.Exists(session.Tracks[1].Destination+".partial")&&!File.Exists(session.Tracks[1].Destination),"paired download failure preserves the other original and incomplete evidence");
            Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE","normal");
        }

        private static void Ffmpeg(string exe,string arguments)
        {
            using(Process process=new Process { StartInfo=new ProcessStartInfo(exe,"-v error -y "+arguments) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true } })
            {
                process.Start(); Task<string> errors=Task.Run(()=>process.StandardError.ReadToEnd());
                if(!process.WaitForExit(20000)) { process.Kill(); throw new IOException("fixture command timed out"); }
                if(process.ExitCode!=0) throw new IOException(errors.Result);
            }
        }
        private static void StreamTests(string ffmpeg,System.Collections.Generic.List<DeviceDisplay> displays)
        {
            Environment.SetEnvironmentVariable("THOR_FFMPEG",ffmpeg);
            string h264=Path.Combine(root,"stream.h264");
            Ffmpeg(ffmpeg,"-f lavfi -i testsrc2=size=128x72:rate=30 -t 3 -c:v libx264 -preset ultrafast -tune zerolatency -bf 0 -g 30 -x264-params aud=1 -f h264 "+VideoExport.Quote(h264));
            Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE","preview-video");
            var top=new DeviceDisplay { LogicalId=0,PhysicalId=displays[0].PhysicalId,Width=128,Height=72 };
            var bottom=new DeviceDisplay { LogicalId=4,PhysicalId=displays[1].PhysicalId,Width=124,Height=108,IsLower=true };
            using(LiveVideo upper=new LiveVideo("test",top,ffmpeg))
            using(LiveVideo lower=new LiveVideo("test",bottom,ffmpeg))
            {
                Stopwatch wait=Stopwatch.StartNew();
                while(wait.ElapsedMilliseconds<8000&&(upper.Frames<45||lower.Frames<45)&&upper.Error==null&&lower.Error==null) Thread.Sleep(20);
                Check(upper.Frames>=45&&lower.Frames>=45,"two real H.264 decoders deliver frames before the input closes");
                using(Bitmap a=upper.TakeLatest()) using(Bitmap b=lower.TakeLatest())
                    Check(a!=null&&b!=null&&a.Width==128&&b.Height==108,"dual streams decode independently with the expected dimensions");
                long consumedAt=upper.Frames;
                using(Bitmap pending=upper.TakeLatest()) Check(pending==null||upper.Frames>consumedAt,"slow consumers do not retain a queue of historical frames");
                using(PreviewBox preview=new PreviewBox())
                {
                    preview.Size=new Size(900,1120); preview.ShowVideo();
                    preview.SetLiveFrame(new Bitmap(128,72),false); preview.SetLiveFrame(new Bitmap(124,108),true);
                    Bitmap red=new Bitmap(128,72); using(Graphics g=Graphics.FromImage(red)) g.Clear(Color.Red);
                    preview.SetLiveFrame(red,false);
                    Check(preview.HasCompanion,"updating the upper stream retains the lower frame");
                    using(Bitmap drawn=new Bitmap(900,1120))
                    { preview.DrawToBitmap(drawn,new Rectangle(0,0,900,1120)); Check(drawn.GetPixel(450,240).R>200,"live frame resumes device rendering after the gallery shows a video"); }
                }
            }
            Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE","preview-hang");
            var stalled=new LiveVideo("test",top,ffmpeg); Thread.Sleep(200); Stopwatch stop=Stopwatch.StartNew(); stalled.Dispose();
            Check(stop.ElapsedMilliseconds<7000,"stopping a stalled preview terminates both local pipe processes");
            string stat="1001 (name with spaces) S "+new string('0',1);
            for(int i=0;i<17;i++) stat+=" 0"; stat+=" 9876 0";
            Check(LiveVideo.StartTicks(stat)=="9876"&&LiveVideo.StartTicks("broken")==null,"preview process identity parses start ticks safely even with spaces");
            Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE","preview-rejected");
            using(LiveVideo rejected=new LiveVideo("test",top,ffmpeg))
            {
                for(int i=0;i<200&&rejected.Error==null;i++) Thread.Sleep(20);
                Check(rejected.Error!=null&&rejected.Error.Contains("unsupported codec"),"unsupported stream reports the actual device error");
            }
            using(MainForm form=new MainForm(true) { Opacity=0,ShowInTaskbar=false })
            {
                form.Show(); System.Windows.Forms.Application.DoEvents();
                BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
                typeof(MainForm).GetField("serial",flags).SetValue(form,"test");
                var screens=(System.Collections.Generic.List<DeviceDisplay>)typeof(MainForm).GetField("displays",flags).GetValue(form);
                screens.Add(top); screens.Add(bottom);
                MethodInfo toggle=typeof(MainForm).GetMethod("ToggleLive",flags);
                toggle.Invoke(form,null);
                Stopwatch wait=Stopwatch.StartNew();
                PreviewBox preview=(PreviewBox)typeof(MainForm).GetField("preview",flags).GetValue(form);
                while(wait.ElapsedMilliseconds<8000&&(!preview.HasCompanion||preview.Current==null))
                { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10); }
                Check((bool)typeof(MainForm).GetField("liveFallback",flags).GetValue(form)&&preview.HasCompanion&&preview.Current!=null,"unsupported dual streaming falls back to both screenshot previews in the real form");
                toggle.Invoke(form,null);
                Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE","preview-video"); toggle.Invoke(form,null);
                wait.Restart();
                while(wait.ElapsedMilliseconds<8000&&(!preview.HasCompanion||preview.Current==null))
                { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10); }
                Check(!(bool)typeof(MainForm).GetField("liveFallback",flags).GetValue(form)&&preview.HasCompanion,"restart reconnects both video streams without retaining fallback mode");
                form.Close(); wait.Restart();
                while(!form.IsDisposed&&wait.ElapsedMilliseconds<8000) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10); }
                Check(form.IsDisposed,"closing the live form waits for stream cleanup before disposal");
            }
            string upperFile=Path.Combine(root,"thor-upper-export-fixture.mp4"),lowerFile=Path.Combine(root,"thor-lower-export-fixture.mp4");
            Ffmpeg(ffmpeg,"-f lavfi -i color=red:size=128x72:rate=30 -t 0.4 -c:v libx264 -pix_fmt yuv420p "+VideoExport.Quote(upperFile));
            Ffmpeg(ffmpeg,"-f lavfi -i color=blue:size=124x108:rate=30 -t 0.3 -c:v libx264 -pix_fmt yuv420p "+VideoExport.Quote(lowerFile));
            string output=Path.Combine(root,"dual-framed.mp4");
            VideoExport.Export(lowerFile,upperFile,output,DeviceSkin.All[1],0,p=>{},CancellationToken.None);
            string png=Path.Combine(root,"dual-framed.png");
            Ffmpeg(ffmpeg,"-i "+VideoExport.Quote(output)+" -frames:v 1 "+VideoExport.Quote(png));
            using(Bitmap rendered=new Bitmap(png))
            { Color a=rendered.GetPixel(450,240),b=rendered.GetPixel(450,760); Check(a.R>150&&a.B<50&&b.B>150&&b.R<50,"paired framed export places the correct source in both screens"); }
            Ffmpeg(ffmpeg,"-i "+VideoExport.Quote(output)+" -f null -");
            Check(File.Exists(output),"paired framed MP4 passes full FFmpeg decode");
            Reject(()=>VideoExport.Export(upperFile,lowerFile,lowerFile,DeviceSkin.All[1],0,p=>{},CancellationToken.None),"paired export refuses to overwrite either original");
            using(var cancel=new CancellationTokenSource())
            {
                cancel.Cancel(); string target=Path.Combine(root,"cancelled-paired.mp4");
                Reject(()=>VideoExport.Export(upperFile,lowerFile,target,DeviceSkin.All[1],0,p=>{},cancel.Token),"paired export cancellation stops decoder and encoder");
                Check(!File.Exists(target),"cancelled paired export publishes no video");
            }
            Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE","normal");
        }

        private static void Live()
        {
            var devices = Adb.Devices();
            Check(devices.Count == 1, "live: exactly one authorized device");
            var screens = Adb.Displays(devices[0]);
            Check(screens.Count == 2, "live: two physical displays");
            foreach (var screen in screens)
            {
                string error;
                byte[] data = Adb.Capture(devices[0], screen.PhysicalId, out error);
                Check(data != null, "live: " + screen.Label + " PNG " + error);
                File.WriteAllBytes(Path.Combine(root, "live-" + screen.Slug + ".png"), data);
                var recording = new Recording(devices[0], screen, root);
                recording.CheckSupport();
                Task<string> task = Task.Run(() => recording.Execute());
                Thread.Sleep(3500);
                recording.Stop();
                Check(task.Wait(80000), "live: stop/download completed");
                Console.WriteLine("VIDEO: " + task.Result);
                Check(new FileInfo(task.Result).Length > 1000, "live: " + screen.Label + " finalized MP4");
            }
            string config=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","artifacts","ffmpeg-path.txt");
            if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("THOR_FFMPEG"))&&File.Exists(config))
                Environment.SetEnvironmentVariable("THOR_FFMPEG",File.ReadAllText(config).Trim());
            string ffmpeg=VideoExport.Locate();
            var session=new RecordingSession(devices[0],screens,root); session.CheckSupport();
            Task<string[]> dual=Task.Run(()=>session.Execute()); Thread.Sleep(3500); session.Stop();
            Check(dual.Wait(80000),"live: dual recording stop and download complete");
            foreach(string path in dual.Result)
            {
                Ffmpeg(ffmpeg,"-i "+VideoExport.Quote(path)+" -f null -");
                Check(new FileInfo(path).Length>1000,"live: paired original fully decodes: "+Path.GetFileName(path));
            }
            using(LiveVideo upper=new LiveVideo(devices[0],screens[0],ffmpeg))
            using(LiveVideo lower=new LiveVideo(devices[0],screens[1],ffmpeg))
            {
                Stopwatch sample=Stopwatch.StartNew();
                while(sample.ElapsedMilliseconds<12000&&upper.Error==null&&lower.Error==null) Thread.Sleep(50);
                Check(upper.Error==null&&lower.Error==null,"live: both preview streams remain active: "+upper.Error+" "+lower.Error);
                Check(upper.Frames>0&&lower.Frames>0,"live: both preview streams deliver decoded frames");
                string metrics="durationSeconds="+sample.Elapsed.TotalSeconds.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)
                    +"\nupperFrames="+upper.Frames+"\nlowerFrames="+lower.Frames
                    +"\nupperReceivedFps="+upper.Fps.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture)
                    +"\nlowerReceivedFps="+lower.Fps.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture)
                    +"\nNote: a moving scene is needed for FPS; these are decoder receipt rates, not end-to-end latency or rendered FPS.\n";
                File.WriteAllText(Path.Combine(root,"live-stream-metrics.txt"),metrics); Console.WriteLine(metrics);
            }
            using (PreviewBox preview = new PreviewBox())
            {
                preview.SetImage(new Bitmap(Path.Combine(root, "live-upper.png")));
                preview.SetCompanion(new Bitmap(Path.Combine(root, "live-lower.png")));
                using (Bitmap frame = preview.ExportDevice()) frame.Save(Path.Combine(root, "live-device-frame.png"));
            }
        }

        private const string InputFixture="add device 1: /dev/input/event7\n  name: \"Test controller\"\n  events:\n    KEY (0001): BTN_A BTN_B BTN_X BTN_Y\n    ABS (0003): ABS_X : value 32768, min 0, max 65535, fuzz 0, flat 1024\n                ABS_Y : value 32768, min 0, max 65535, fuzz 0, flat 1024\n                ABS_RX : value 0, min -32768, max 32767, fuzz 0, flat 1024\n                ABS_RY : value 0, min -32768, max 32767, fuzz 0, flat 1024\n                ABS_HAT0X : value 0, min -1, max 1\nadd device 2: /dev/input/event9\n  name: \"Touchscreen\"\n  ABS_MT_POSITION_X : value 0, min 0, max 1920\n";
        private static void MetadataAndInputTests(System.Collections.Generic.List<DeviceDisplay> displays)
        {
            Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE","normal");
            ForegroundSnapshot snapshot=ForegroundSnapshot.Read("test");
            Check(snapshot.Packages[0]=="com.game.upper" && snapshot.Packages[4]=="com.ayn.panel","foreground packages stay associated with each display");
            snapshot=ForegroundSnapshot.Parse("Display #0:\n  mResumedActivity: ActivityRecord{x u0 com.background/.M t1}\n  topResumedActivity=ActivityRecord{x u0 com.game/.M t2}\nDisplay #4:\n  topResumedActivity=ActivityRecord{x u0 com.panel/.M t3}\nmTopResumedActivity=ActivityRecord{x u0 com.global/.M t2}");
            Check(snapshot.Packages[0]=="com.game"&&snapshot.Packages[4]=="com.panel","foreground priority and global summary isolation");
            Check(ForegroundSnapshot.Parse("topResumedActivity=ActivityRecord{x u0 com.global/.M t2}").Packages.Count==0,"unscoped activity is never assigned to a screen");
            Check(ForegroundSnapshot.Parse("Display #0:\n topResumedActivity=ActivityRecord{x u0 com.one/.M t1}\n topResumedActivity=ActivityRecord{x u0 com.two/.M t2}").Packages[0]=="","ambiguous resumed apps remain unknown");
            Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE","foreground-failed");
            Check(ForegroundSnapshot.Read("test").QueryFailed,"foreground query failure is nonfatal and explicit");
            string metadataImage=Path.Combine(root,Guid.NewGuid().ToString("N")+".png");
            CaptureMetadata metadata=CaptureMetadata.Create(snapshot,"test",displays[1],DateTime.UtcNow); metadata.Save(metadataImage);
            Check(CaptureMetadata.Load(metadataImage).Package=="com.panel","metadata sidecar round trip");
            Settings.Set("appAlias.com.panel","面板测试");
            Check(metadata.Matches("面板测试")&&metadata.Matches("com.PANEL"),"gallery search matches aliases and package names");
            File.WriteAllText(CaptureMetadata.Sidecar(metadataImage),"broken");
            Check(CaptureMetadata.Load(metadataImage)==null,"damaged sidecar does not break gallery");
            var devices=InputDevice.Parse(InputFixture);
            Check(devices.Count==1&&devices[0].Path=="/dev/input/event7","input discovery excludes touchscreens");
            var profile=new InputProfile(devices[0]); var decoder=new PadDecoder(devices[0],profile);
            Check(decoder.Current.LX==0&&decoder.Current.RX==0,"axis centers respect signed and unsigned ranges");
            decoder.Feed("[ 10.01] 0001 0130 00000001");
            Check(decoder.Current.Down(PadButton.A),"button press state");
            decoder.Feed("[ 10.02] 0001 0130 00000002"); decoder.Feed("[ 10.03] 0001 0130 00000000");
            Check(!decoder.Current.Down(PadButton.A)&&decoder.KeySequence==1,"key repeat and release do not create extra presses");
            decoder.Feed("[ 10.04] 0003 0010 ffffffff");
            Check(decoder.Current.Down(PadButton.Left)&&!decoder.Current.Down(PadButton.Right),"signed hat axis maps to left");
            decoder.Feed("[ 10.05] /dev/input/event7: 0003 0000 0000ffff");
            Check(decoder.Current.LX==1,"unsigned maximum normalizes to full stick travel");
            profile.Bind(PadButton.B,0x130); decoder.Feed("[ 10.06] 0001 0130 00000001");
            Check(decoder.Current.Down(PadButton.B)&&!decoder.Current.Down(PadButton.A),"calibration remaps physical button");
            Check(!decoder.Feed("unrecognized output"),"malformed input line ignored");
            decoder.Feed("[ 10.07] 0000 0003 00000000");
            Check(decoder.Lost&&decoder.Current.Buttons==0&&decoder.Current.LX==0,"SYN_DROPPED clears stale held controls");
            decoder.Feed("[ 10.08] 0001 0130 00000001");
            Check(decoder.Current.Buttons==0,"input loss requires reconnect instead of guessing state");
            Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE","normal");
            string recorded=Path.Combine(root,"input-test-"+Guid.NewGuid().ToString("N")+".mp4");
            using(InputMonitor monitor=new InputMonitor(devices[0],new InputProfile(devices[0])))
            {
                monitor.Start("test",devices[0].Path); monitor.BeginTimeline(recorded);
                Thread.Sleep(850); monitor.EndTimeline();
            }
            PadTimeline timeline=PadTimeline.Load(recorded+".input.csv");
            Check(timeline.States[0].Down(PadButton.A)&&timeline.At(2000).Buttons==0,"recording timeline contains initial held state and release");
            Check(timeline.At(-100).Buttons==0,"positive export offset leaves pre-event frames neutral");
            string invalid=Path.Combine(root,"invalid.input.csv");
            File.WriteAllText(invalid,"#thor-input-v1\n100,0,0,0,0,0,0,0,0\n20,0,0,0,0,0,0,0,0");
            Reject(()=>PadTimeline.Load(invalid),"out-of-order timeline rejected");
            File.WriteAllText(invalid,"#thor-input-v1\n0,0,0,0,0,0,0,0,0\n#warning=input-disconnected-or-lost");
            Check(PadTimeline.Load(invalid).Interrupted,"interrupted timeline remains explicitly marked");
            profile.Bind(PadButton.A,-1);
            Check(!profile.Keys.ContainsValue(PadButton.A),"cleared button binding stays disabled");
            Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE","input-denied");
            Reject(()=>InputDevice.Discover("test"),"input permission denial remains visible");
            using(InputMonitor denied=new InputMonitor(devices[0],new InputProfile(devices[0]))) Reject(()=>denied.Start("test",devices[0].Path),"stream startup failure detected");
            Environment.SetEnvironmentVariable("THOR_CAPTURE_TEST_MODE","normal");
        }

        private static int Fake(string[] args)
        {
            string command = string.Join(" ", args);
            string mode = Environment.GetEnvironmentVariable("THOR_CAPTURE_TEST_MODE");
            string dir = Environment.GetEnvironmentVariable("THOR_CAPTURE_TEST_ROOT");
            string group=Environment.GetEnvironmentVariable("THOR_CAPTURE_GROUP_ROOT");
            if(mode!=null&&mode.StartsWith("paired-"))
            {
                if(command.Contains("exec screenrecord")&&!command.Contains("--output-format=h264"))
                {
                    bool lower=command.Contains("thor-record-lower-"); string slug=lower?"lower":"upper",pid=lower?"1002":"1001";
                    string remote=System.Text.RegularExpressions.Regex.Match(command,@"/data/local/tmp/thor-record-[^\s"";]+\.mp4").Value;
                    File.WriteAllText(Path.Combine(group,pid+".cmd"),"screenrecord "+remote);
                    File.WriteAllText(Path.Combine(group,slug+".start"),"started");
                    Stopwatch wait=Stopwatch.StartNew();
                    while(!File.Exists(Path.Combine(group,(lower?"upper":"lower")+".start"))&&wait.ElapsedMilliseconds<3000) Thread.Sleep(10);
                    if(wait.ElapsedMilliseconds>=3000) { Console.Error.Write("peer never launched"); return 1; }
                    if(mode=="paired-fail"&&lower) { Console.Error.Write("second encoder unavailable"); return 1; }
                    if(mode=="paired-running"||mode=="paired-fail")
                        while(!File.Exists(Path.Combine(group,pid+".stop"))&&wait.ElapsedMilliseconds<6000) Thread.Sleep(10);
                    else Thread.Sleep(120);
                    return 0;
                }
                if(command.Contains("shell cat")&&command.EndsWith(".pid"))
                { string pid=command.Contains("-lower-")?"1002":"1001"; if(!File.Exists(Path.Combine(group,pid+".cmd"))) return 1; Console.Write(pid); return 0; }
                if(command.Contains("/cmdline")) { Console.Write(File.ReadAllText(Path.Combine(group,command.Contains("1002")?"1002.cmd":"1001.cmd"))); return 0; }
                if(command.Contains("kill -2")) { File.WriteAllText(Path.Combine(group,command.Contains("1002")?"1002.stop":"1001.stop"),"stopped"); return 0; }
            }
            if(command.Contains("--output-format=h264"))
            {
                if(mode=="preview-rejected") { Console.Error.Write("unsupported codec"); return 1; }
                if(mode=="preview-hang") { Thread.Sleep(20000); return 0; }
                byte[] data=File.ReadAllBytes(Path.Combine(dir,"stream.h264"));
                Stream output=Console.OpenStandardOutput();
                int start=0;
                for(int i=4;i<data.Length-5;i++)
                    if(data[i]==0&&data[i+1]==0&&data[i+2]==0&&data[i+3]==1&&(data[i+4]&31)==9)
                    { output.Write(data,start,i-start); output.Flush(); start=i; Thread.Sleep(33); }
                output.Write(data,start,data.Length-start); output.Flush(); Thread.Sleep(10000); return 0;
            }
            Type[] signature = { typeof(string), typeof(string), typeof(string) };
            foreach (Type suite in Suites())
            {
                MethodInfo fake = SuiteMethod(suite, "FakeAdb", signature);
                if (fake == null) continue;
                int code = (int)fake.Invoke(null, new object[] { command, mode, dir });
                if (code != NotHandled) return code;
            }
            if(command.Contains("dumpsys activity activities"))
            {
                if(mode=="foreground-failed") { Console.Error.Write("not available"); return 1; }
                Console.WriteLine("Display #0 (activities from top to bottom):\n  topResumedActivity=ActivityRecord{a u0 com.game.upper/.Main t1}\nDisplay #4 (activities from top to bottom):\n  topResumedActivity=ActivityRecord{b u0 com.ayn.panel/.Main t2}"); return 0;
            }
            if(command.Contains("getevent"))
            {
                if(mode=="input-denied") { Console.Error.WriteLine("Permission denied"); return 1; }
                if(command.Contains("-lp")) { Console.WriteLine(InputFixture); return 0; }
                Console.WriteLine("[ 100.000000] 0001 0130 00000001\n[ 100.000000] 0000 0000 00000000"); Console.Out.Flush();
                Thread.Sleep(700);
                Console.WriteLine("[ 100.700000] 0001 0130 00000000\n[ 100.700000] 0000 0000 00000000"); Console.Out.Flush();
                Thread.Sleep(10000); return 0;
            }
            if (command == "hang") { Thread.Sleep(10000); return 0; }
            if (command == "flood") { Console.Error.Write(new string('e', 150000)); Console.Out.Write(new string('o', 150000)); return 0; }
            if (command == "devices") { Console.WriteLine("List of devices attached\ntest\t" + (mode == "unauthorized" ? "unauthorized" : "device")); return 0; }
            if (command.Contains("--help")) { Console.WriteLine("screenrecord --display-id ID"); return 0; }
            if (command.Contains("exec-out"))
            {
                byte[] data = File.ReadAllBytes(Path.Combine(dir, "fixture.png"));
                if (mode == "invalid-stream" || mode == "corrupt-all") data = new byte[] { 137, 80, 78, 71, 1, 2, 3, 4, 5 };
                Console.OpenStandardOutput().Write(data, 0, data.Length);
                return mode == "nonzero-stream" ? 1 : 0;
            }
            if (command.Contains(" pull "))
            {
                string target = args[args.Length - 1];
                if (target.EndsWith(".partial"))
                {
                    if(mode=="paired-pull-fail"&&target.Contains("-lower-")) { File.WriteAllText(target,"incomplete"); return 1; }
                    using (FileStream file = File.Create(target))
                    foreach (string atom in new string[] { "ftyp", "mdat", "moov" })
                    { byte[] bytes = Encoding.ASCII.GetBytes("\0\0\0\x0c" + atom + "test"); file.Write(bytes, 0, bytes.Length); }
                }
                else if (mode == "corrupt-all") File.WriteAllBytes(target, new byte[] { 137, 80, 78, 71, 0 });
                else if (mode == "nonzero-stream") return 1;
                else File.Copy(Path.Combine(dir, "fixture.png"), target, true);
                return 0;
            }
            if (command.Contains("shell cat") && command.EndsWith(".pid")) { Console.Write("1234"); return 0; }
            if (command.Contains("/cmdline")) { Console.Write("screenrecord /other.mp4"); return 0; }
            return 0;
        }
    }
}
