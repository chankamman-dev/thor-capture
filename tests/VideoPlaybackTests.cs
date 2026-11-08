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
    internal static class VideoPlaybackTests
    {
        internal static void Run()
        {
            string folder=Path.Combine(Tests.Root,"pair-detection"); Directory.CreateDirectory(folder);
            string upper=Path.Combine(folder,"thor-upper-batch.mp4"),lower=Path.Combine(folder,"thor-lower-batch.mp4");
            File.WriteAllText(upper,"fixture"); File.WriteAllText(lower,"fixture");
            Tests.Check(VideoPlayback.ExistingPeer(upper)==lower&&VideoPlayback.ExistingPeer(lower)==upper,"video pairing works from either screen");
            Tests.Check(VideoPlayback.ExistingPeer(Path.Combine(folder,"other.mp4"))==null,"unmarked videos do not pair with themselves");
            File.Delete(lower);
            Tests.Check(VideoPlayback.ExistingPeer(upper)==null,"missing video track remains a single-screen source");
            using(PreviewBox stage=new PreviewBox { Size=new Size(900,1120) })
            {
                Bitmap red=new Bitmap(160,90),blue=new Bitmap(124,108);
                using(Graphics g=Graphics.FromImage(red)) g.Clear(Color.Red);
                using(Graphics g=Graphics.FromImage(blue)) g.Clear(Color.Blue);
                stage.SetPlaybackFrames(red,blue,true);
                using(Bitmap image=stage.ExportDevice(false))
                {
                    Color top=image.GetPixel(image.Width/2,(int)(180*1920.0/790)),bottom=image.GetPixel(image.Width/2,(int)(820*1920.0/790));
                    Tests.Check(top.R>200&&bottom.B>200,"selecting the lower video retains upper/lower shell placement");
                }
                stage.Mode=PreviewMode.Fit;
                Tests.Check(stage.Current.Width==124,"single-image video view follows the selected track");
            }
        }
        private static void Fixture(string exe,string args)
        {
            using(Process process=new Process { StartInfo=new ProcessStartInfo(exe,"-v error -y "+args) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true } })
            {
                process.Start(); Task<string> errors=Task.Run(()=>process.StandardError.ReadToEnd());
                if(!process.WaitForExit(15000)) { process.Kill(); throw new IOException("playback fixture timeout"); }
                if(process.ExitCode!=0) throw new IOException(errors.Result);
            }
        }
        private static void Until(Func<bool> predicate,string reason,bool pump)
        {
            Stopwatch timer=Stopwatch.StartNew();
            while(!predicate()&&timer.ElapsedMilliseconds<9000) { if(pump) Application.DoEvents(); Thread.Sleep(10); }
            if(!predicate()) throw new Exception(reason);
        }
        private static PlaybackFrame Frame(VideoPlayback player)
        {
            PlaybackFrame frame=null;
            Until(delegate { if(player.Error!=null) throw new IOException(player.Error); return frame!=null||(frame=player.TakeLatest())!=null; },"no playback frame",false);
            return frame;
        }
        internal static void RunStream(string ffmpeg)
        {
            string upper=Path.Combine(Tests.Root,"thor-upper-playback-fixture.mp4"),lower=Path.Combine(Tests.Root,"thor-lower-playback-fixture.mp4");
            Fixture(ffmpeg,"-f lavfi -i color=red:size=160x90:rate=30 -vf \"drawbox=x=0:y=0:w=iw:h=ih:color=yellow:t=fill:enable='gte(t,1)'\" -t 2.2 -c:v libx264 -pix_fmt yuv420p "+VideoExport.Quote(upper));
            Fixture(ffmpeg,"-f lavfi -i color=blue:size=124x108:rate=30 -vf \"drawbox=x=0:y=0:w=iw:h=ih:color=green:t=fill:enable='gte(t,1)'\" -t 1.6 -c:v libx264 -pix_fmt yuv420p "+VideoExport.Quote(lower));
            File.WriteAllLines(lower+".input.csv",new string[] { "#thor-input-v1",PadTimeline.Format(0,100,new PadState { Buttons=PadButton.A }),PadTimeline.Format(1000,101,new PadState { Buttons=PadButton.B }) });
            using(VideoPlayback player=new VideoPlayback(upper,lower,ffmpeg))
            {
                using(PlaybackFrame first=Frame(player))
                    Tests.Check(first.Upper.GetPixel(50,40).R>200&&first.Lower.GetPixel(50,40).B>200,"packed playback decodes both first frames without an external player");
                Tests.Check(Math.Abs(player.Duration-1.6)<.06&&!player.Playing,"paired playback opens paused and ends at the shorter source");
                Tests.Check(player.HasTimeline&&player.InputAt(1.2).Down(PadButton.B),"playback reads a timeline available only beside the companion");
                using(PlaybackFrame absent=player.TakeLatest()) Tests.Check(absent==null,"paused playback holds one frame without a historical queue");
                player.Play(); Thread.Sleep(180); player.Pause(); double paused=player.Position; Thread.Sleep(100);
                Tests.Check(paused>.1&&Math.Abs(player.Position-paused)<.005,"play and pause use a stable shared clock");
                player.Seek(.2); player.Seek(.8); player.Seek(1.2);
                using(PlaybackFrame seek=Frame(player))
                {
                    Color a=seek.Upper.GetPixel(50,40),b=seek.Lower.GetPixel(50,40);
                    Tests.Check(seek.Time>=1.19&&a.R>200&&a.G>200&&b.G>90&&b.B<30,"rapid paused seeks discard old generations and move both tracks together");
                }
                player.Seek(double.NaN); Tests.Check(Math.Abs(player.Position-1.2)<.005,"invalid seek values cannot corrupt playback time");
                player.Play(); Until(()=>player.Ended,"playback did not reach end",false);
                Tests.Check(!player.Playing&&Math.Abs(player.Position-player.Duration)<.005,"shorter track stops both screens at end");
                player.Play(); using(PlaybackFrame replay=Frame(player)) Tests.Check(replay.Time<.2&&replay.Upper.GetPixel(50,40).G<30,"replay restarts the shared timeline");
                Tests.Check(player.StopAsync().Wait(5000),"stopping playback releases its decoder within a bound");
                using(FileStream exclusive=File.Open(upper,FileMode.Open,FileAccess.Read,FileShare.None)) Tests.Check(exclusive.Length>0,"closed playback releases the original file handle");
            }
            using(VideoPlayback player=new VideoPlayback(lower,upper,ffmpeg))
            using(PlaybackFrame first=Frame(player)) Tests.Check(first.Upper.Width==160&&first.Lower.Width==124&&first.Upper.GetPixel(50,40).R>200,"lower-first playback preserves track roles");
            using(VideoPlayback single=new VideoPlayback(lower,null,ffmpeg))
            using(PlaybackFrame first=Frame(single)) Tests.Check(first.Upper==null&&first.Lower!=null&&!single.Paired,"a single lower-screen video does not invent an upper track");
            string broken=Path.Combine(Tests.Root,"bad-playback.mp4"); File.WriteAllText(broken,"not an MP4");
            using(VideoPlayback corrupt=new VideoPlayback(broken,null,ffmpeg))
            {
                Until(()=>corrupt.Error!=null,"bad source had no visible error",false);
                Tests.Check(!corrupt.Ready&&!corrupt.Playing,"corrupt video reports a playback error rather than fake readiness");
            }
            FormChecks(upper,lower);
            string output=Path.Combine(Tests.Root,"playback-pair-framed.mp4");
            VideoExport.Export(lower,upper,output,DeviceSkin.All[2],0,p=>{},CancellationToken.None);
            Fixture(ffmpeg,"-i "+VideoExport.Quote(output)+" -f null -");
            Tests.Check(File.Exists(output)&&File.Exists(upper)&&File.Exists(lower),"lower-selected paired export fully decodes and preserves both originals");
        }
        private static void FormChecks(string upper,string lower)
        {
            BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            using(MainForm form=new MainForm(true) { Opacity=0,ShowInTaskbar=false })
            {
                form.Show(); Application.DoEvents();
                GalleryView gallery=(GalleryView)typeof(MainForm).GetField("gallery",flags).GetValue(form);
                PreviewBox preview=(PreviewBox)typeof(MainForm).GetField("preview",flags).GetValue(form);
                gallery.SetItems(new List<GalleryItem> {
                    new GalleryItem { Path=lower,Name=Path.GetFileName(lower),IsVideo=true,Screen=1,Paired=true,Time=DateTime.Now },
                    new GalleryItem { Path=upper,Name=Path.GetFileName(upper),IsVideo=true,Screen=0,Paired=true,Time=DateTime.Now } });
                gallery.SelectOnly(0);
                Until(()=>preview.Current!=null&&preview.HasCompanion,"real form did not display paired video",true);
                FlatButton export=(FlatButton)typeof(MainForm).GetField("exportButton",flags).GetValue(form);
                Tests.Check(export.Enabled&&export.Text=="导出带壳视频","gallery video enables the main framed-video export entry");
                VideoPlayback player=(VideoPlayback)typeof(MainForm).GetField("playback",flags).GetValue(form);
                typeof(MainForm).GetMethod("OpenSelected",flags).Invoke(form,null);
                Tests.Check(player.Playing,"gallery activation starts the internal player");
                typeof(MainForm).GetMethod("OnShortcut",flags).Invoke(form,new object[] { form,new KeyEventArgs(Keys.Space) });
                Tests.Check(!player.Playing,"space pauses internal playback");
                player.Seek(1.2);
                Until(()=>preview.Current!=null&&((Bitmap)preview.Current).GetPixel(50,40).G>90,"real form seek did not display both tracks",true);
                typeof(MainForm).GetMethod("SetBusy",flags).Invoke(form,new object[] { true });
                Tests.Check(!player.Playing&&!export.Enabled,"device or export work pauses playback and disables repeated export");
                preview.SetInput(new PadState { Buttons=PadButton.Y }); preview.AdvanceAnimation(.1);
                typeof(MainForm).GetMethod("SetBusy",flags).Invoke(form,new object[] { false });
                Until(()=>preview.VisualInput.Press(PadButton.B)>.8&&preview.VisualInput.Press(PadButton.Y)<.001,"paused timeline was not restored after busy work",true);
                Tests.Check(!player.Playing,"busy work restores the recorded pose without restarting playback");
                using(Bitmap shot=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(shot,new Rectangle(0,0,form.Width,form.Height)); shot.Save(Path.Combine(Tests.Root,"playback-window.png")); }
                Tests.Check(preview.LowerScreen&&preview.HasCompanion,"lower-selected playback stays paired across seeks in the real form");
                gallery.SelectOnly(1); Application.DoEvents();
                Until(()=>player.Completion.IsCompleted,"selection retained previous decoder",true);
                Tests.Check(((VideoPlayback)typeof(MainForm).GetField("playback",flags).GetValue(form))!=player,"gallery switching cancels the old player generation");
                form.Close(); Until(()=>form.IsDisposed,"form did not close",true);
                Tests.Check(form.IsDisposed,"window closure awaits local playback cleanup");
            }
        }
    }
}
