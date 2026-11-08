using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;

namespace ThorCapture
{
    // Read-only acceptance against a saved recording; all output goes to evidence.
    internal static class PlaybackCheck
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                string source=args[0],folder=args[1],peer=VideoPlayback.ExistingPeer(source);
                Directory.CreateDirectory(folder);
                DeviceSkin skin=DeviceSkin.All[2].WithLamp(false,Color.Empty,1);
                using(VideoPlayback player=new VideoPlayback(source,peer,VideoExport.Locate()))
                {
                    using(PlaybackFrame first=Read(player))
                        if(first.Upper==null||first.Lower==null) throw new IOException("Saved recording did not pair");
                    Console.WriteLine("Saved dual recording: "+player.Duration.ToString("F3")+" seconds; timeline="+player.HasTimeline);
                    for(int i=0;i<3;i++)
                    {
                        double seconds=player.Duration*i/3;
                        player.Seek(seconds);
                        using(PlaybackFrame frame=Read(player))
                        using(Bitmap bitmap=new Bitmap(900,1120))
                        using(Graphics graphics=Graphics.FromImage(bitmap))
                        {
                            if(Math.Abs(frame.Time-seconds)>.05) throw new IOException("Seek did not present the requested generation");
                            graphics.Clear(Ui.Bg0);
                            DeviceArt.Draw(graphics,new RectangleF(20,12,860,1050),skin,frame.Upper,frame.Lower,null,null,player.InputAt(frame.Time));
                            bitmap.Save(Path.Combine(folder,"saved-playback-"+i+".png"));
                        }
                        Console.WriteLine("Dual seek checked at "+seconds.ToString("F3")+" seconds");
                    }
                }
                using(CancellationTokenSource timeout=new CancellationTokenSource(TimeSpan.FromMinutes(15)))
                    VideoExport.Export(source,peer,Path.Combine(folder,"saved-dual-framed.mp4"),skin,0,p=>{ if(p%10==0) Console.WriteLine("Export "+p+"%"); },timeout.Token);
                Console.WriteLine("Saved dual recording exported; originals untouched");
                return 0;
            }
            catch(Exception failure) { Console.Error.WriteLine(failure); return 1; }
        }
        private static PlaybackFrame Read(VideoPlayback player)
        {
            Stopwatch wait=Stopwatch.StartNew(); PlaybackFrame frame;
            while(wait.Elapsed.TotalSeconds<25)
            {
                if(player.Error!=null) throw new IOException(player.Error);
                if((frame=player.TakeLatest())!=null) return frame;
                Thread.Sleep(10);
            }
            throw new IOException("Saved recording playback timeout");
        }
    }
}
