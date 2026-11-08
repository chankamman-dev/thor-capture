using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThorCapture
{
    internal partial class MainForm
    {
        private VideoPlayback playback;
        private Task playbackRelease=Task.FromResult(0);
        private Strip playbackBar;
        private FlatButton playbackButton,playbackExport,playbackExternal;
        private SeekBar playbackSeek;
        private Label playbackTime;
        private readonly Timer playbackTimer=new Timer();
        private bool playbackDragging,playbackWasPlaying,settingPlaybackSeek;
        private double playbackFrameTime;

        private void InitializePlaybackUi()
        {
            // Player bar: round play button, seek bar with a time bubble, status line underneath.
            playbackBar=new Strip { Dock=DockStyle.Bottom,Height=Ui.S(76),BackColor=Ui.Bg1,TopLine=true,Visible=false };
            playbackButton=MakeButton(playbackBar,Glyphs.Play,null,ButtonKind.Primary,"播放 / 暂停（空格）",delegate { TogglePlayback(); });
            playbackButton.Pill=true;
            playbackExport=MakeButton(playbackBar,Glyphs.Export,"导出视频",ButtonKind.Subtle,"同组双屏可合成一个带壳 MP4",delegate { ExportVideo(); });
            playbackExternal=MakeButton(playbackBar,Glyphs.OpenOut,null,ButtonKind.Ghost,"用系统播放器单独打开所选原片",delegate { OpenOriginal(); });
            playbackSeek=new SeekBar { Minimum=0,Maximum=10000 };
            playbackSeek.HoverLabel=delegate(int at) { VideoPlayback current=playback; return current==null||!current.Ready?null:PlaybackStamp(current.Duration*at/10000); };
            playbackTime=new Label { ForeColor=Ui.TextDim,BackColor=Ui.Bg1,Font=Ui.Small,AutoEllipsis=true,Text="正在读取视频…" };
            playbackBar.Controls.Add(playbackSeek); playbackBar.Controls.Add(playbackTime);
            playbackBar.Layout+=delegate
            {
                int pad=Ui.S(18),d=Ui.S(44),right=playbackBar.Width-pad,mid=playbackBar.Height/2;
                playbackButton.SetBounds(pad,mid-d/2,d,d);
                playbackExternal.SetBounds(right-Ui.S(36),mid-Ui.S(18),Ui.S(36),Ui.S(36)); right-=Ui.S(44);
                playbackExport.Height=Ui.S(36); playbackExport.Fit();
                playbackExport.Location=new Point(right-playbackExport.Width,mid-Ui.S(18)); right-=playbackExport.Width+Ui.S(18);
                int left=playbackButton.Right+Ui.S(10);
                playbackSeek.SetBounds(left,Ui.S(2),Math.Max(1,right-left),Ui.S(46));
                playbackTime.SetBounds(left+Ui.S(9),Ui.S(46),Math.Max(1,right-left-Ui.S(18)),Ui.S(22));
            };
            playbackSeek.MouseDown+=delegate
            {
                if(playback==null||!playback.Ready) return;
                playbackDragging=true; playbackWasPlaying=playback.Playing; playback.Pause();
            };
            playbackSeek.MouseUp+=delegate
            {
                FinishPlaybackSeek();
            };
            playbackSeek.ValueChanged+=delegate
            {
                if(!settingPlaybackSeek&&!playbackDragging&&playback!=null&&playback.Ready) playback.Seek(playback.Duration*playbackSeek.Value/10000);
            };
            playbackSeek.MouseCaptureChanged+=delegate
            {
                if(playbackDragging&&!playbackSeek.Capture) FinishPlaybackSeek();
            };
            split.Panel2.Controls.Add(playbackBar);
            playbackTimer.Interval=15; playbackTimer.Tick+=delegate { TickPlayback(); }; playbackTimer.Start();
        }
        private void FinishPlaybackSeek()
        {
            if(!playbackDragging) return;
            playbackDragging=false;
            if(playback!=null) { playback.Seek(playback.Duration*playbackSeek.Value/10000); if(playbackWasPlaying) playback.Play(); }
        }
        private void StartPlayback(GalleryItem item)
        {
            if(livePreview) return;
            playbackBar.Visible=true;
            preview.SetInput(null); preview.SetTouches(null); preview.SetImage(null); preview.LowerScreen=item.Screen==1;
            playbackTime.Text="正在读取视频…";
            playbackFrameTime=0;
            try { playback=new VideoPlayback(item.Path,VideoPlayback.ExistingPeer(item.Path),VideoExport.Locate()); }
            catch(Exception failure) { playbackTime.Text=failure.Message; tips.SetToolTip(playbackTime,failure.Message); }
            UpdatePlaybackControls();
        }
        private Task StopPlayback()
        {
            VideoPlayback old=playback; playback=null; playbackDragging=false;
            if(old!=null) playbackRelease=Task.WhenAll(playbackRelease,old.StopAsync());
            if(playbackBar!=null) playbackBar.Visible=false;
            preview.SetInput(null); preview.SetTouches(null);
            return playbackRelease;
        }
        private void TogglePlayback()
        {
            if(busy||playback==null||!playback.Ready||playback.Error!=null) return;
            if(playback.Playing) playback.Pause(); else playback.Play();
            UpdatePlaybackControls();
        }
        private void PausePlayback() { if(playback!=null) { playback.Pause(); UpdatePlaybackControls(); } }
        private void UpdatePlaybackControls()
        {
            if(playbackBar==null) return;
            bool ready=playback!=null&&playback.Ready&&playback.Error==null&&!busy;
            playbackButton.Enabled=playbackSeek.Enabled=ready;
            string glyph=playback!=null&&playback.Playing?Glyphs.Pause:playback!=null&&playback.Ended?Glyphs.Replay:Glyphs.Play;
            if(playbackButton.Glyph!=glyph) { playbackButton.Glyph=glyph; playbackButton.Invalidate(); }
            playbackExport.Enabled=!busy&&gallery.Primary!=null&&gallery.Primary.IsVideo;
            playbackExternal.Enabled=!busy&&gallery.Primary!=null&&gallery.Primary.IsVideo;
        }
        private static string PlaybackStamp(double seconds)
        {
            TimeSpan span=TimeSpan.FromSeconds(Math.Max(0,seconds));
            return span.TotalHours>=1?((int)span.TotalHours).ToString("00")+":"+span.Minutes.ToString("00")+":"+span.Seconds.ToString("00")
                :((int)span.TotalMinutes).ToString("00")+":"+span.Seconds.ToString("00");
        }
        private void TickPlayback()
        {
            VideoPlayback current=playback;
            if(current==null||busy||recording!=null) return;
            using(PlaybackFrame frame=current.TakeLatest())
            {
                if(frame!=null)
                {
                    bool first=preview.Current==null;
                    preview.SetPlaybackFrames(frame.Upper,frame.Lower,gallery.Primary!=null&&gallery.Primary.Screen==1);
                    frame.Upper=frame.Lower=null;
                    playbackFrameTime=frame.Time;
                    if(first) UpdateControls();
                }
            }
            preview.SetInput(current.InputAt(playbackFrameTime));
            preview.SetTouches(current.TouchAt(playbackFrameTime));
            UpdatePlaybackControls();
            // Just the clock; only problems add words.
            string status=current.Error??(!current.Ready?"正在读取视频…":PlaybackStamp(current.Position)+" / "+PlaybackStamp(current.Duration)+(current.Warning==null?"":"   "+current.Warning));
            if(playbackTime.Text!=status) { playbackTime.Text=status; tips.SetToolTip(playbackTime,status); }
            if(current.Ready&&!playbackDragging)
            {
                settingPlaybackSeek=true;
                playbackSeek.Value=Math.Max(0,Math.Min(10000,(int)(current.Position/Math.Max(.001,current.Duration)*10000)));
                settingPlaybackSeek=false;
            }
        }
        private void DisposePlayback()
        {
            StopPlayback(); playbackTimer.Stop(); playbackTimer.Dispose();
        }
    }
}
