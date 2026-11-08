using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThorCapture
{
    internal partial class MainForm
    {
        private FlatButton liveButton,inputButton,calibrateButton,videoExportButton,previewSettingsButton,audioSettingsButton;
        private bool recordSystemAudio;
        private PreviewOptions previewOptions=new PreviewOptions();
        private Label inputLabel;
        private readonly System.Windows.Forms.Timer inputTimer=new System.Windows.Forms.Timer();
        private InputMonitor inputMonitor;
        private InputDevice inputDevice;
        private string inputPrefix;
        private bool livePreview;
        private bool recordingPreviewEnabled;
        private bool liveFallback,fallbackFetching;
        private bool closingLive;
        private Task fallbackCapture=Task.FromResult(0);
        private DateTime nextFallback=DateTime.MinValue;
        private int liveGeneration;
        private LiveVideo[] liveStreams;
        private Task liveRelease=Task.FromResult(0);
        private string liveRate="";
        private DateTime nextLiveStatus=DateTime.MinValue;
        private DateTime nextInputStatus=DateTime.MinValue;

        private void InitializeInputUi()
        {
            // MainForm has loaded the settings file before constructing this toolbar.
            previewOptions=PreviewOptions.Load();
            recordSystemAudio=Settings.Get("recordSystemAudio","1")!="0";
            // One grouped command row: preview | audio | input | export, status text on the right.
            Strip bar=new Strip { Dock=DockStyle.Top,Height=Ui.S(52),BackColor=Ui.Bg1 };
            liveButton=MakeButton(bar,Glyphs.Video,"实时预览",ButtonKind.Subtle,"普通预览为视频流；录屏期间自动连续取景，可暂停显示",delegate { ToggleLive(); });
            inputButton=MakeButton(bar,Glyphs.Game,"操作反馈",ButtonKind.Subtle,"只读手柄与两块触摸屏的事件，录屏时同时保存操作时间线；不控制实机",delegate { ToggleInput(); });
            calibrateButton=MakeButton(bar,Glyphs.Tune,null,ButtonKind.Ghost,"按键校准：核对实机 A/B 布局与摇杆轴",delegate { CalibrateInput(); });
            videoExportButton=MakeButton(bar,Glyphs.Export,"视频导出…",ButtonKind.Ghost,"选择原片或带壳＋按键动画；支持同步偏移",delegate { ExportVideo(); });
            previewSettingsButton=MakeButton(bar,Glyphs.Settings,null,ButtonKind.Ghost,"预览设置：分辨率、码率、电脑刷新上限与预览屏幕",delegate { ShowPreviewOptions(); });
            audioSettingsButton=MakeButton(bar,recordSystemAudio?Glyphs.Volume:Glyphs.Mute,recordSystemAudio?"系统声音":"无声",ButtonKind.Ghost,"录制游戏/媒体声音，保留 Thor 外放；点击切换无声。不会录制麦克风",delegate
            {
                if(busy) return;
                recordSystemAudio=!recordSystemAudio;
                if(!Settings.Set("recordSystemAudio",recordSystemAudio?"1":"0")) Notify("声音设置已应用，但无法保存设置文件",true);
                audioSettingsButton.Glyph=recordSystemAudio?Glyphs.Volume:Glyphs.Mute;
                audioSettingsButton.Text=recordSystemAudio?"系统声音":"无声";
                audioSettingsButton.Checked=recordSystemAudio;
            });
            audioSettingsButton.Checked=recordSystemAudio;
            inputLabel=new Label { ForeColor=Ui.TextFaint,BackColor=Ui.Bg1,Font=Ui.Small,Text="输入反馈未开启 · 视频原片始终保留",AutoEllipsis=true,TextAlign=ContentAlignment.MiddleRight };
            bar.Controls.Add(inputLabel);
            inputLabel.Visible=false; // status goes to the status bar; no standing hint text
            FlatButton[][] groups={ new FlatButton[] {liveButton,previewSettingsButton}, new FlatButton[] {audioSettingsButton}, new FlatButton[] {inputButton,calibrateButton}, new FlatButton[] {videoExportButton} };
            List<int> dividers=new List<int>();
            foreach(FlatButton[] group in groups) foreach(FlatButton button in group) button.TextChanged+=delegate { bar.PerformLayout(); };
            bar.Painter=delegate(object sender,PaintEventArgs e)
            {
                using(Pen line=new Pen(Ui.Line)) foreach(int x in dividers) e.Graphics.DrawLine(line,x,Ui.S(16),x,bar.Height-Ui.S(16));
            };
            bar.Layout+=delegate
            {
                int x=Ui.S(16),h=Ui.S(34),y=(bar.Height-h)/2;
                dividers.Clear();
                for(int g=0;g<groups.Length;g++)
                {
                    if(g>0) { x+=Ui.S(8); dividers.Add(x); x+=Ui.S(9); }
                    foreach(FlatButton button in groups[g])
                    {
                        button.Height=h; button.Fit(); if(button.Text.Length==0) button.Width=h;
                        button.Location=new Point(x,y); x+=button.Width+Ui.S(4);
                    }
                }
                int left=x+Ui.S(12);
                int line=Ui.S(20);
                inputLabel.SetBounds(left,(bar.Height-line)/2,Math.Max(1,bar.Width-left-Ui.S(16)),line);
                bar.Invalidate();
            };
            split.Panel2.Controls.Add(bar);
            inputTimer.Interval=15;
            inputTimer.Tick+=InputTick;
            inputTimer.Start();
        }
        private void ShowPreviewOptions()
        {
            if(busy) return;
            using(PreviewOptionsDialog dialog=new PreviewOptionsDialog(previewOptions))
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK) return;
                ApplyPreviewOptions(dialog.Value,true);
            }
        }
        private void ApplyPreviewOptions(PreviewOptions value,bool persist)
        {
            previewOptions=value.Copy();
            if(persist&&!previewOptions.Save()) Notify("预览设置已应用，但无法写入设置文件",true);
            SuspendLive(); liveFallback=previewOptions.PixelMode; nextFallback=DateTime.MinValue;
            if(livePreview) { preview.SetImage(null); preview.LowerScreen=false; liveRate="正在应用预览设置…"; nextLiveStatus=DateTime.MinValue; }
        }
        private void UpdateInputControls()
        {
            if(liveButton==null) return;
            liveButton.Enabled=recording!=null || (!busy && serial!=null && displays.Count>0);
            inputButton.Enabled=!busy && serial!=null;
            calibrateButton.Enabled=!busy && inputMonitor!=null && inputMonitor.Error==null;
            videoExportButton.Enabled=!busy && gallery.Primary!=null && gallery.Primary.IsVideo;
            previewSettingsButton.Enabled=!busy;
            audioSettingsButton.Enabled=!busy;
            if(livePreview||recording!=null) LockGallery(true);
        }
        private void SuspendLive()
        {
            liveGeneration++;
            LiveVideo[] streams=liveStreams; liveStreams=null;
            if(streams==null) return;
            Task previous=liveRelease;
            liveRelease=Task.Run(async delegate
            {
                await previous;
                Task[] stops=new Task[streams.Length];
                for(int i=0;i<streams.Length;i++) { LiveVideo stream=streams[i]; stops[i]=Task.Run(()=>stream.Dispose()); }
                await Task.WhenAll(stops);
            });
        }
        private async Task WaitForLiveStop() { await liveRelease; await fallbackCapture; await playbackRelease; }
        private bool DelayCloseForLive(FormClosingEventArgs e)
        {
            if(closingLive) { e.Cancel=true; return true; }
            if(liveStreams==null && liveRelease.IsCompleted && fallbackCapture.IsCompleted && playback==null && playbackRelease.IsCompleted) return false;
            e.Cancel=true; closingLive=true; Enabled=false; livePreview=false; SuspendLive();
            StopPlayback();
            CloseAfterLiveStop(); return true;
        }
        private async void CloseAfterLiveStop()
        {
            try { await WaitForLiveStop(); }
            catch(Exception failure) { Say("预览清理："+failure.Message,true); }
            finally
            {
                // Even a completed cleanup must leave the original FormClosing stack first.
                if(!IsDisposed&&IsHandleCreated)
                    BeginInvoke((MethodInvoker)delegate { closingLive=false; if(!IsDisposed) Close(); });
                else closingLive=false;
            }
        }
        private void StopInput()
        {
            livePreview=false; SuspendLive();
            if(inputMonitor!=null) { try { inputMonitor.Dispose(); } catch(Exception error) { Say(error.Message,true); } inputMonitor=null; }
            preview.SetInput(null); preview.SetTouches(null);
            LockGallery(false);
            if(inputButton!=null) { inputButton.Text="操作反馈"; liveButton.Text="实时预览"; inputLabel.Text="输入反馈未开启 · 视频原片始终保留"; }
        }
        // The gallery cannot change selection while frames stream into the preview; say so on
        // the gallery itself and offer the way out.
        private void LockGallery(bool locked)
        {
            bool recordingNow=recording!=null||recordingPreviewEnabled;
            if(!locked) gallery.SetLock(null,null);
            else gallery.SetLock(recordingNow?"录屏中":"实时预览中",recordingNow?null:"停止");
        }
        private void DisposeInput() { inputTimer.Stop(); inputTimer.Dispose(); DisposePlayback(); StopInput(); }
        private void ToggleLive()
        {
            if(recording!=null)
            {
                recordingPreviewEnabled=!recordingPreviewEnabled; SuspendLive();
                nextFallback=DateTime.MinValue;
                liveButton.Text=recordingPreviewEnabled?"暂停取景":"恢复取景";
                liveRate=recordingPreviewEnabled?"录屏取景正在连接…":"取景已暂停，录屏继续";
                infoTitle="录屏中"; infoDetail=liveRate; infoBar.Invalidate();
                return;
            }
            if(busy || serial==null) return;
            livePreview=!livePreview; SuspendLive();
            liveButton.Text=livePreview?"停止预览":"实时预览";
            LockGallery(livePreview);
            if(livePreview)
            {
                StopPlayback();
                liveFallback=previewOptions.PixelMode;
                viewPicker.SelectedIndex=0; preview.SetImage(null); preview.LowerScreen=false;
                liveRate="视频流正在连接…"; nextLiveStatus=DateTime.MinValue;
            }
            else ShowSelected();
            UpdateInputControls();
        }
        private void BeginRecordingPreview()
        {
            recordingPreviewEnabled=true; nextFallback=DateTime.MinValue;
            viewPicker.SelectedIndex=0; preview.SetImage(null); preview.LowerScreen=false;
            LockGallery(true); liveButton.Text="暂停取景";
            liveRate="录屏取景正在连接…";
            infoTitle="录屏中"; infoDetail="连续截图取景 · 不占额外视频编码器 · 原片按原生分辨率录制"; infoBar.Invalidate();
        }
        private void EndRecordingPreview()
        {
            recordingPreviewEnabled=false; SuspendLive();
            LockGallery(livePreview);
            liveButton.Text=livePreview?"停止预览":"实时预览";
            nextFallback=DateTime.MinValue; nextLiveStatus=DateTime.MinValue;
        }
        // Active toggles show a live dot in place of their icon, kept in sync from the input tick.
        private void SyncToggleDots()
        {
            Color live=livePreview||(recordingPreviewEnabled&&recording!=null)?Ui.Ok:Color.Empty;
            Color input=inputMonitor==null?Color.Empty:inputMonitor.Error!=null?Ui.Danger:Ui.Ok;
            if(liveButton.Dot!=live) { liveButton.Dot=live; liveButton.Glyph=live.IsEmpty?Glyphs.Video:null; liveButton.Parent.PerformLayout(); }
            if(inputButton.Dot!=input) { inputButton.Dot=input; inputButton.Glyph=input.IsEmpty?Glyphs.Game:null; inputButton.Parent.PerformLayout(); }
        }
        private void InputTick(object sender,EventArgs e)
        {
            SyncToggleDots();
            bool updateStatus=DateTime.UtcNow>=nextInputStatus;
            if(updateStatus) nextInputStatus=DateTime.UtcNow.AddMilliseconds(250);
            if(playback!=null&&!busy&&recording==null)
            {
                if(updateStatus) inputLabel.Text="录屏回放 · 按键动画来自录制时间线 · 鼠标仅演示外壳 · 不控制实机";
            }
            else if(inputMonitor!=null)
            {
                preview.SetTouches(inputMonitor.Touches);
                if(inputMonitor.Error!=null)
                {
                    if(updateStatus) inputLabel.Text="输入反馈异常："+inputMonitor.Error;
                    preview.SetInput(null);
                }
                else
                {
                    PadState target=inputMonitor.State;
                    preview.SetInput(target);
                    if(updateStatus) inputLabel.Text=(recording!=null?"录屏中 · 按键时间线记录中 · "+liveRate:"手柄已连接 · "+(livePreview?liveRate+" / 实时按键":"当前为图库静态画面 / 实时按键"))
                        +" · L("+target.LX.ToString("0.00")+", "+target.LY.ToString("0.00")+") R("+target.RX.ToString("0.00")+", "+target.RY.ToString("0.00")+")";
                }
            }
            else if(updateStatus) inputLabel.Text=recording!=null?"录屏中 · "+liveRate:livePreview?(busy?"画面取景暂停 · 设备操作进行中":liveRate+" · 鼠标可演示外壳动画") : "外壳可用鼠标演示 · 拖动摇杆 / 右键 L3、R3 · 不控制实机";
            if(recording!=null)
            {
                if(recordingPreviewEnabled&&liveRelease.IsCompleted&&fallbackCapture.IsCompleted&&!fallbackFetching&&DateTime.UtcNow>=nextFallback)
                    fallbackCapture=CaptureFallback(true);
                return;
            }
            if(!livePreview||busy||!liveRelease.IsCompleted||!fallbackCapture.IsCompleted) return;
            if(liveFallback)
            {
                if(!fallbackFetching&&DateTime.UtcNow>=nextFallback) fallbackCapture=CaptureFallback();
                return;
            }
            try
            {
                if(liveStreams==null)
                {
                    // Constructors only start background readers; no adb or decoder waits on the UI.
                    string ffmpeg=VideoExport.Locate();
                    List<LiveVideo> streams=new List<LiveVideo>();
                    try { foreach(DeviceDisplay display in displays) if(previewOptions.Includes(display)) streams.Add(new LiveVideo(serial,display,ffmpeg,previewOptions));
                        if(streams.Count==0) throw new IOException("没有找到所选预览屏幕"); }
                    catch { foreach(LiveVideo stream in streams) Task.Run(()=>stream.Dispose()); throw; }
                    liveStreams=streams.ToArray();
                    liveRate="视频流正在连接…"; nextLiveStatus=DateTime.MinValue;
                }
                foreach(LiveVideo stream in liveStreams)
                {
                    if(stream.Error!=null) throw new IOException(stream.Display.Label+"："+stream.Error);
                    Bitmap frame=stream.TakeLatest();
                    if(frame!=null) preview.SetLiveFrame(frame,stream.Display.IsLower,previewOptions.Screens==2);
                }
                if(DateTime.UtcNow>=nextLiveStatus)
                {
                    List<string> rates=new List<string>(),sizes=new List<string>();
                    foreach(LiveVideo stream in liveStreams)
                    {
                        rates.Add(stream.Display.Label+" "+(stream.Frames==0?"连接中":stream.Fps.ToString("0.0")+" 接收 / "+stream.PresentationFps.ToString("0.0")+" 送显 fps"));
                        sizes.Add(stream.Display.Label+" "+stream.Width+"×"+stream.Height+(stream.FrameAge>2?"（画面未更新 "+stream.FrameAge.ToString("0.0")+"s）":""));
                    }
                    liveRate="视频流接收 · "+string.Join(" / ",rates);
                    infoTitle="实时预览";
                    infoDetail=string.Join(" · ",sizes)+" · "+previewOptions.Summary+" · 最新帧，不积队 · 未保存";
                    infoBar.Invalidate(); nextLiveStatus=DateTime.UtcNow.AddMilliseconds(500);
                }
            }
            catch(Exception failure)
            {
                SuspendLive(); liveFallback=true; liveRate="兼容截图预览 · 正在连接";
                Notify("视频流不可用，改用兼容截图预览："+failure.Message,true);
            }
        }
        private async Task CaptureFallback(bool duringRecording=false)
        {
            fallbackFetching=true;
            int generation=liveGeneration;
            string deviceSerial=serial;
            PreviewOptions configuration=previewOptions.Copy();
            List<DeviceDisplay> screens=displays.FindAll(configuration.Includes);
            Bitmap[] frames=new Bitmap[screens.Count];
            var timer=System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if(screens.Count==0) throw new IOException("没有找到所选预览屏幕");
                Task[] captures=new Task[screens.Count];
                for(int i=0;i<captures.Length;i++)
                {
                    int index=i;
                    captures[i]=Task.Run(delegate { frames[index]=configuration.PixelMode?LiveFrame.CapturePng(deviceSerial,screens[index].PhysicalId,0):LiveFrame.CaptureConfigured(deviceSerial,screens[index].PhysicalId,configuration.MaxHeight); });
                }
                await Task.WhenAll(captures);
                if(IsDisposed||generation!=liveGeneration||(duringRecording?(recording==null||!recordingPreviewEnabled):(busy||!livePreview))) return;
                for(int i=0;i<frames.Length;i++)
                { preview.SetLiveFrame(frames[i],screens[i].IsLower,configuration.Screens==2); frames[i]=null; }
                liveRate=(duringRecording?"连续截图取景":configuration.PixelMode?"原生 PNG 取景":"兼容截图预览")+" · "+(1/Math.Max(.001,timer.Elapsed.TotalSeconds+Math.Max(.06,1.0/configuration.DisplayFps))).ToString("0.0")+" fps";
                infoTitle=duringRecording?"录屏中 · 实时取景":configuration.PixelMode?"原生 PNG 取景":"实时预览（兼容模式）";
                infoDetail=(duringRecording?"原片录制继续 · 取景不占额外视频编码器":configuration.PixelMode?"原生像素 · 连续 PNG 截图，非高帧率视频":"视频流不可用 · 连续截图")+" · 取景帧不保存"; infoBar.Invalidate();
            }
            catch(Exception failure)
            {
                if(!IsDisposed&&generation==liveGeneration)
                {
                    if(duringRecording)
                    {
                        recordingPreviewEnabled=false; liveButton.Text="恢复取景"; liveRate="取景异常，录屏继续";
                        infoTitle="录屏中"; infoDetail=liveRate+" · "+failure.Message; infoBar.Invalidate();
                        Notify("录屏取景失败，原片录制继续；可点击恢复取景重试："+failure.Message,true);
                        return;
                    }
                    livePreview=false; LockGallery(false); liveButton.Text="实时预览";
                    Notify("预览已停止："+failure.Message,true); UpdateInputControls();
                }
            }
            finally
            {
                foreach(Bitmap frame in frames) if(frame!=null) frame.Dispose();
                fallbackFetching=false; nextFallback=DateTime.UtcNow.AddMilliseconds(Math.Max(60,1000/configuration.DisplayFps));
            }
        }
        private async void ToggleInput()
        {
            if(busy) return;
            if(inputMonitor!=null) { StopInput(); UpdateInputControls(); return; }
            if(serial==null) return;
            string target=serial;
            SetBusy(true);
            InputMonitor next=null;
            try
            {
                List<TouchDevice> panels=null;
                List<InputDevice> sources=await Task.Run(()=>InputDevice.Discover(target,out panels));
                inputDevice=sources[0];
                if(sources.Count>1)
                {
                    using(ThemedDialog dialog=new ThemedDialog("选择手柄输入设备",null,null,520,96))
                    {
                        ChoiceBox choice=new ChoiceBox();
                        foreach(InputDevice source in sources) choice.Items.Add(source);
                        choice.SelectedIndex=0;
                        dialog.AddField("输入设备",choice,24,14,472);
                        AddDialogButtons(dialog);
                        if(dialog.ShowDialog(this)!=DialogResult.OK) return;
                        inputDevice=(InputDevice)choice.SelectedItem;
                    }
                }
                inputPrefix="input."+Uri.EscapeDataString(target+"."+inputDevice.Name);
                InputProfile profile=new InputProfile(inputDevice); profile.Load(inputPrefix);
                next=new InputMonitor(inputDevice,profile);
                await Task.Run(()=>next.Start(target,inputDevice.Path));
                // Touch panels are optional: a denied or missing panel leaves the gamepad running.
                List<TouchDecoder> touchDecoders=TouchDecoders(target,panels);
                if(touchDecoders.Count>0) { InputMonitor started=next; await Task.Run(()=>started.StartTouch(target,touchDecoders)); }
                inputMonitor=next; next=null;
                inputButton.Text="停止反馈"; viewPicker.SelectedIndex=0;
                Notify("操作反馈已开启 · "+(touchDecoders.Count>0?touchDecoders.Count+" 块触摸屏":"未找到可读的触摸屏")+"；请先松开所有按键，并用「按键校准」核对布局",false);
            }
            catch(Exception failure) { Notify(failure.Message,true); }
            finally { if(next!=null) next.Dispose(); SetBusy(false); }
        }

        // Each panel's screen and rotation: a saved calibration if there is one, otherwise the
        // aspect-ratio guess against the displays that were detected.
        private List<TouchDecoder> TouchDecoders(string target,List<TouchDevice> panels)
        {
            List<TouchDecoder> result=new List<TouchDecoder>();
            if(panels==null||panels.Count==0) return result;
            List<TouchMap> guesses=TouchMap.Guess(panels,displays);
            for(int i=0;i<panels.Count;i++)
            {
                TouchMap map=TouchMap.Parse(Settings.Get("touch."+Uri.EscapeDataString(target+"."+panels[i].Name+"."+panels[i].Path),""))??guesses[i];
                if(map!=null) result.Add(new TouchDecoder(panels[i],map,i));
            }
            return result;
        }

        private static void AddDialogButtons(ThemedDialog dialog)
        {
            dialog.AddAction("取消",ButtonKind.Subtle,DialogResult.Cancel);
            dialog.AddAction("确定",ButtonKind.Primary,DialogResult.OK);
        }
        private static void StyleGrid(DataGridView grid)
        {
            grid.BackgroundColor=Ui.Surface; grid.BorderStyle=BorderStyle.None; grid.GridColor=Ui.Line;
            grid.CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal;
            grid.EnableHeadersVisualStyles=false; grid.ColumnHeadersBorderStyle=DataGridViewHeaderBorderStyle.None;
            grid.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.DisableResizing; grid.ColumnHeadersHeight=Ui.S(32);
            grid.ColumnHeadersDefaultCellStyle.BackColor=Ui.Card; grid.ColumnHeadersDefaultCellStyle.ForeColor=Ui.TextDim; grid.ColumnHeadersDefaultCellStyle.Font=Ui.Small;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor=Ui.Card;
            grid.DefaultCellStyle.BackColor=Ui.Surface; grid.DefaultCellStyle.ForeColor=Ui.Text;
            grid.DefaultCellStyle.SelectionBackColor=Ui.AccentSoft; grid.DefaultCellStyle.SelectionForeColor=Ui.AccentDeep;
            grid.DefaultCellStyle.Padding=new Padding(Ui.S(8),0,Ui.S(8),0);
            grid.RowTemplate.Height=Ui.S(30);
        }
        private void CalibrateInput()
        {
            if(busy||inputMonitor==null||inputMonitor.Error!=null) return;
            using(ThemedDialog dialog=new ThemedDialog("按键校准",null,null,560,470))
            using(System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer())
            {
                DataGridView grid=new DataGridView { AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill };
                StyleGrid(grid);
                grid.Columns.Add("button","外壳按钮"); grid.Columns.Add("code","事件代码（hex）"); grid.Columns[0].ReadOnly=true;
                foreach(PadButton button in Enum.GetValues(typeof(PadButton)))
                {
                    string code=""; foreach(var pair in inputMonitor.Decoder.Profile.Keys) if(pair.Value==button) code=pair.Key.ToString("x4");
                    grid.Rows.Add(button.ToString(),code);
                }
                grid.SetBounds(Ui.S(24),Ui.S(12),Ui.S(512),Ui.S(340)); dialog.Body.Controls.Add(grid);
                FlatButton bind=new FlatButton(Glyphs.Game,"绑定下一次按键") { Kind=ButtonKind.Subtle }; bind.SetBounds(Ui.S(24),Ui.S(390),Ui.S(170),Ui.S(36)); dialog.Body.Controls.Add(bind);
                int sequence=0,armed=-1;
                bind.Click+=delegate { if(grid.CurrentRow!=null) { armed=grid.CurrentRow.Index; sequence=inputMonitor.KeySequence; bind.Text="请按实机按钮…"; } };
                timer.Interval=33;
                timer.Tick+=delegate { if(armed>=0&&inputMonitor.KeySequence!=sequence) { grid.Rows[armed].Cells[1].Value=inputMonitor.LastKey.ToString("x4"); armed=-1; bind.Text="绑定下一次按键"; } };
                timer.Start();
                TextField rightX=dialog.AddField("右摇杆 X 轴",new TextField { Text=inputMonitor.Decoder.Profile.RightX.ToString("x2") },366,368,80);
                TextField rightY=dialog.AddField("右摇杆 Y 轴",new TextField { Text=inputMonitor.Decoder.Profile.RightY.ToString("x2") },456,368,80);
                AddDialogButtons(dialog);
                if(dialog.ShowDialog(this)!=DialogResult.OK) return;
                try
                {
                    Dictionary<int,PadButton> bindings=new Dictionary<int,PadButton>();
                    foreach(DataGridViewRow row in grid.Rows)
                    {
                        string code=Convert.ToString(row.Cells[1].Value).Trim(); if(code.Length==0) continue;
                        int number=int.Parse(code,NumberStyles.HexNumber,CultureInfo.InvariantCulture);
                        if(number<0||number>0x2ff||bindings.ContainsKey(number)) throw new IOException("按键代码重复或超出范围");
                        bindings[number]=(PadButton)Enum.Parse(typeof(PadButton),(string)row.Cells[0].Value);
                    }
                    int x=int.Parse(rightX.Text,NumberStyles.HexNumber),y=int.Parse(rightY.Text,NumberStyles.HexNumber);
                    if(x==y||x==0||x==1||y==0||y==1||!inputDevice.Axes.ContainsKey(x)||!inputDevice.Axes.ContainsKey(y)) throw new IOException("请选择设备上存在的两根独立右摇杆轴");
                    bool saved=true;
                    inputMonitor.Calibrate(bindings,x,y);
                    foreach(PadButton button in Enum.GetValues(typeof(PadButton)))
                    {
                        int code=-1; foreach(var pair in bindings) if(pair.Value==button) code=pair.Key;
                        saved=Settings.Set(inputPrefix+"."+button,code.ToString("x8"))&&saved;
                    }
                    saved=Settings.Set(inputPrefix+".rightX",x.ToString())&&saved; saved=Settings.Set(inputPrefix+".rightY",y.ToString())&&saved;
                    Notify(saved?"映射已保存，请推动摇杆并核对 A/B/X/Y":"当前映射已更新，但设置写入失败",!saved);
                }
                catch(Exception failure) { Notify("未应用校准："+failure.Message,true); }
            }
        }

        private async void ExportVideo()
        {
            GalleryItem item=gallery.Primary;
            if(busy||item==null||!item.IsVideo) return;
            string source=item.Path;
            string peer=VideoPlayback.ExistingPeer(source);
            bool framed=false,showTouches=true; double offset=0;
            bool interrupted=false;
            string timelinePath=File.Exists(source+".input.csv")?source+".input.csv":peer!=null&&File.Exists(peer+".input.csv")?peer+".input.csv":null;
            string timelineError=null;
            try { if(timelinePath!=null) interrupted=PadTimeline.Load(timelinePath).Interrupted; }
            catch(Exception failure) { timelineError="时间线损坏，可导出原片："+failure.Message; }
            // Only real problems get a line of text; normal cases need no explanation.
            string warning=timelineError??(interrupted?"按键记录中途断开，后半段可能没有动画":null);
            using(ThemedDialog options=new ThemedDialog("视频导出",null,null,560,warning==null?232:300))
            {
                OptionCards mode=new OptionCards { Columns=2,Height=Ui.S(104) };
                mode.Add(null,peer==null?"原始视频":"双屏原片","复制原片");
                mode.Add(null,peer==null?"带壳视频":"双屏带壳视频",timelinePath!=null&&timelineError==null?"外壳＋按键动画":"外壳");
                mode.SelectedIndex=1;
                options.AddField("导出方式",mode,24,8,512);
                mode.Height=Ui.S(96);
                if(warning!=null) options.AddNote(null,warning,24,144,512,52);
                NumberBox shift=new NumberBox { Minimum=-5000,Maximum=5000,Increment=20,Unit="ms" };
                options.AddField("按键同步偏移",shift,24,warning==null?144:212,220);
                ToggleSwitch touchMarks=new ToggleSwitch("显示触摸反馈",true);
                options.AddField("触摸",touchMarks,272,warning==null?144:212,264);
                mode.SelectedIndexChanged+=delegate { touchMarks.Enabled=mode.SelectedIndex==1; };
                AddDialogButtons(options);
                if(options.ShowDialog(this)!=DialogResult.OK) return;
                framed=mode.SelectedIndex==1; offset=(double)shift.Value; showTouches=touchMarks.Checked;
            }
            using(SaveFileDialog file=new SaveFileDialog())
            {
                file.Filter="MP4 视频|*.mp4"; file.FileName=Path.GetFileNameWithoutExtension(source)+(framed?"-framed":"-copy")+".mp4";
                if(file.ShowDialog(this)!=DialogResult.OK) return;
                if(!framed)
                {
                    try
                    {
                        string other=peer==null?null:PeerOf(file.FileName);
                        if(peer!=null&&(other==null||other==file.FileName)) throw new IOException("双屏原片文件名请保留 upper / lower 标记");
                        if(other!=null&&File.Exists(other)) throw new IOException("另一屏目标文件已存在，请更换导出名称："+other);
                        if(peer!=null&&string.Equals(Path.GetFullPath(peer),Path.GetFullPath(file.FileName),StringComparison.OrdinalIgnoreCase)) throw new IOException("不能覆盖另一屏原片");
                        if(!string.Equals(Path.GetFullPath(source),Path.GetFullPath(file.FileName),StringComparison.OrdinalIgnoreCase)) File.Copy(source,file.FileName,true);
                        if(peer!=null) File.Copy(peer,other,false);
                        Notify(peer==null?"原始视频已导出":"双屏原片已分别导出",false);
                    }
                    catch(Exception failure) { Notify(failure.Message,true); }
                    return;
                }
                SetBusy(true);
                using(CancellationTokenSource cancel=new CancellationTokenSource())
                using(ThemedDialog progress=new ThemedDialog("合成带壳视频",null,null,440,84))
                {
                    Label label=progress.AddText("准备合成…",Ui.Body,Ui.Text,24,14,392,22);
                    ProgressLine bar=new ProgressLine { Value=-1 }; bar.SetBounds(Ui.S(24),Ui.S(46),Ui.S(392),Ui.S(10)); progress.Body.Controls.Add(bar);
                    FlatButton stop=progress.AddAction("取消",ButtonKind.Subtle,DialogResult.None); stop.Click+=delegate { cancel.Cancel(); label.Text="正在取消…"; };
                    bool done=false;
                    progress.FormClosing+=delegate(object sender,FormClosingEventArgs e) { if(!done) { cancel.Cancel(); e.Cancel=true; } };
                    progress.Show(this);
                    IProgress<int> report=new Progress<int>(p=> { if(!progress.IsDisposed) { label.Text="合成中 "+p+"%"; bar.Value=p/100f; } });
                    DeviceSkin skin=preview.Skin;
                    try
                    {
                        cancel.CancelAfter(TimeSpan.FromMinutes(20));
                        await WaitForLiveStop();
                        await Task.Run(()=>VideoExport.Export(source,peer,file.FileName,skin,offset,showTouches,p=>report.Report(p),cancel.Token));
                        Notify("带壳视频已导出："+Path.GetFileName(file.FileName),false);
                    }
                    catch(Exception failure) { Notify(cancel.IsCancellationRequested?"已取消合成，原始视频保留":failure.Message,!cancel.IsCancellationRequested); }
                    finally { done=true; progress.Close(); SetBusy(false); }
                }
            }
        }
    }
}
