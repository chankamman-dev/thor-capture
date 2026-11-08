// Visual check for the device frame and the main window, without a device.
// Built and run by tools/render-check.ps1; writes PNGs into evidence/ui-check.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows.Forms;

namespace ThorCapture
{
    internal static class RenderCheck
    {
        private sealed class DemoStage : PreviewBox
        {
            internal void Mouse(PointF design, MouseButtons button, bool down)
            {
                RectangleF bounds = DeviceBounds();
                PointF origin = ShellGeometry.Origin(bounds);
                float scale = ShellGeometry.Scale(bounds);
                Point point = Point.Round(new PointF(origin.X + design.X * scale, origin.Y + design.Y * scale));
                MouseEventArgs args = new MouseEventArgs(button, 1, point.X, point.Y, 0);
                if (down) OnMouseDown(args); else OnMouseUp(args);
            }
            internal void Drag(PointF design)
            {
                RectangleF bounds = DeviceBounds();
                PointF origin = ShellGeometry.Origin(bounds);
                float scale = ShellGeometry.Scale(bounds);
                OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, (int)(origin.X + design.X * scale), (int)(origin.Y + design.Y * scale), 0));
            }
            internal void Render(Graphics graphics) { OnPaint(new PaintEventArgs(graphics, ClientRectangle)); }
        }
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static int Main(string[] args)
        {
            SetProcessDPIAware();
            string playbackError=Path.Combine(args[1],"playback-render-error.txt");
            if(File.Exists(playbackError)) File.Delete(playbackError);
            try { Run(args); return 0; }
            catch (Exception failure)
            {
                File.WriteAllText(Path.Combine(args[1], "render-error.txt"), failure.ToString());
                return 1;
            }
        }

        // Newest upper/lower pair in the gallery, so the frame shows real screens.
        private static void FindPair(string captures, out string upper, out string lower)
        {
            upper = lower = null;
            List<FileInfo> files = new List<FileInfo>(new DirectoryInfo(captures).GetFiles("thor-upper-*.png"));
            files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
            foreach (FileInfo file in files)
            {
                string peer = Path.Combine(captures, file.Name.Replace("-upper-", "-lower-"));
                if (File.Exists(peer)) { upper = file.FullName; lower = peer; return; }
            }
        }

        private static void Run(string[] args)
        {
            string captures = args[0], output = args[1];
            bool window = args.Length > 2 && args[2] == "window";
            string upperPath, lowerPath;
            FindPair(captures, out upperPath, out lowerPath);
            Image top = upperPath == null ? null : Image.FromFile(upperPath);
            Image bottom = lowerPath == null ? null : Image.FromFile(lowerPath);

            using (Bitmap sheet = new Bitmap(DeviceSkin.All.Length * 700, 860))
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.Clear(Ui.Bg0);
                for (int i = 0; i < DeviceSkin.All.Length; i++)
                    DeviceArt.Draw(g, new RectangleF(i * 700 + 20, 20, 660, 820), DeviceSkin.All[i], top, bottom, "上屏\n暂无截图", "下屏\n暂无截图");
                sheet.Save(Path.Combine(output, "skins.png"));
            }
            foreach (DeviceSkin skin in DeviceSkin.All)
                using (Bitmap big = new Bitmap(1400, 1680))
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.Clear(Ui.Bg0);
                    DeviceArt.Draw(g, new RectangleF(20, 20, 1360, 1640), skin, top, bottom, null, null);
                    big.Save(Path.Combine(output, "frame-" + skin.Key + "-large.png"));
                }
            using(Bitmap feedback=new Bitmap(1400,900))
            using(Graphics g=Graphics.FromImage(feedback))
            {
                g.Clear(Ui.Bg0);
                DeviceArt.Draw(g,new RectangleF(20,35,660,820),DeviceSkin.All[1],top,bottom,null,null);
                DeviceArt.Draw(g,new RectangleF(720,35,660,820),DeviceSkin.All[1],top,bottom,null,null,new PadState { Buttons=PadButton.A|PadButton.Up|PadButton.L1|PadButton.R3,LX=.8f,LY=-.7f,RX=-.6f,RY=.4f });
                using(Font label=new Font("Microsoft YaHei UI",12))
                { g.DrawString("静止",label,new SolidBrush(Ui.Text),20,5); g.DrawString("模拟输入 · A / ↑ / L1 / R3 / 双摇杆",label,new SolidBrush(Ui.Text),720,5); }
                feedback.Save(Path.Combine(output,"input-feedback.png"));
            }
            using(Bitmap sheet=new Bitmap(1800,390))
            using(Graphics g=Graphics.FromImage(sheet))
            using(Font label=new Font("Microsoft YaHei UI",14))
            {
                g.Clear(Ui.Bg0);
                PadButton[] states={0,PadButton.Up,PadButton.Left,PadButton.Right,PadButton.Down|PadButton.Right};
                string[] names={"松开","向上按压","向左按压","向右按压","右下斜向按压"};
                for(int i=0;i<states.Length;i++)
                using(Bitmap full=new Bitmap(877,1045))
                using(Graphics fg=Graphics.FromImage(full))
                {
                    fg.Clear(Ui.Bg0);
                    DeviceArt.Draw(fg,new RectangleF(0,0,877,1045),DeviceSkin.All[2],null,null,null,null,new PadState { Buttons=states[i] });
                    g.DrawImage(full,new Rectangle(i*360,45,360,340),new Rectangle(38,780,170,165),GraphicsUnit.Pixel);
                    g.DrawString(names[i],label,new SolidBrush(Ui.Text),i*360+20,10);
                }
                sheet.Save(Path.Combine(output,"dpad-detail.png"));
            }
            using(Bitmap sheet=new Bitmap(1440,400))
            using(Graphics g=Graphics.FromImage(sheet))
            using(Font label=new Font("Microsoft YaHei UI",14))
            {
                g.Clear(Ui.Bg0);
                string[] titles={"居中 · 灯环固定","左上 · 露出右下灯环","向右 · 露出左侧灯环","L3 下压 · 灯环固定"};
                for(int i=0;i<4;i++)
                {
                    g.DrawString(titles[i],label,new SolidBrush(Ui.Text),i*360+10,12);
                    var state=g.Save(); g.TranslateTransform(i*360+180,220); g.ScaleTransform(2.1f,2.1f);
                    g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    DeviceArt.Stick(g,DeviceSkin.All[2],0,0,i==1?-.75f:i==2?1:0,i==1?-.65f:0,i==3);
                    g.Restore(state);
                }
                sheet.Save(Path.Combine(output,"stick-fixed-ring.png"));
            }
            using(Bitmap lighting=new Bitmap(1080,400))
            using(Graphics g=Graphics.FromImage(lighting))
            using(Font label=new Font("Microsoft YaHei UI",14))
            {
                g.Clear(Ui.Bg0);
                string[] names={"熄灯 · 扩散罩","点亮 · 凹槽反光","偏转 · 杆帽遮挡与反光"};
                for(int i=0;i<3;i++)
                {
                    g.DrawString(names[i],label,new SolidBrush(Ui.Text),i*360+12,12);
                    var state=g.Save(); g.TranslateTransform(i*360+180,220); g.ScaleTransform(2.1f,2.1f);
                    g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    DeviceArt.Stick(g,i==0?DeviceSkin.All[2].WithGlow(Color.Empty):DeviceSkin.All[2],0,0,i==2?-.8f:0,i==2?-.6f:0,false);
                    g.Restore(state);
                }
                lighting.Save(Path.Combine(output,"lamp-lighting.png"));
            }
            using(Bitmap bench=new Bitmap(900,1100))
            using(Graphics bg=Graphics.FromImage(bench))
            {
                var watch=System.Diagnostics.Stopwatch.StartNew();
                for(int i=0;i<90;i++)
                {
                    float x=(float)Math.Sin(i*.11),y=(float)Math.Cos(i*.11);
                    bg.Clear(Ui.Bg0);
                    DeviceArt.DrawControls(bg,new RectangleF(0,0,900,1080),DeviceSkin.All[2],new PadState {LX=x,LY=y,RX=-x,RY=-y,Buttons=i%2==0?PadButton.Up:PadButton.Left});
                }
                watch.Stop();
                File.WriteAllText(Path.Combine(output,"controls-render-timing.txt"),"90 offscreen control frames, 900x1100. Mean ms/frame: "+(watch.Elapsed.TotalMilliseconds/90).ToString("F2",System.Globalization.CultureInfo.InvariantCulture)+". CPU render only; not ADB or real-device FPS.");
            }
            if (!window) return;
            MouseSamples(output);
            MascotSamples(output);
            TouchSamples(output, top, bottom);
            string stickDir=Path.Combine(output,"stick-motion"); Directory.CreateDirectory(stickDir);
            for(int frame=0;frame<90;frame++)
            using(Bitmap shot=new Bitmap(480,460))
            using(Graphics canvas=Graphics.FromImage(shot))
            using(Font label=new Font("Microsoft YaHei UI",14))
            {
                double angle=2*Math.PI*(frame-15)/60;
                float amount=frame<15||frame>=75?0:Math.Min(1,Math.Min((frame-15)/5f,(75-frame)/5f));
                canvas.Clear(Ui.Bg0); canvas.DrawString("模拟输入 · 固定灯环 / 活动杆帽",label,new SolidBrush(Ui.Text),12,12);
                var state=canvas.Save(); canvas.TranslateTransform(240,260); canvas.ScaleTransform(2.4f,2.4f);
                canvas.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                DeviceArt.Stick(canvas,DeviceSkin.All[2],0,0,(float)Math.Cos(angle)*amount,(float)Math.Sin(angle)*amount,false);
                canvas.Restore(state); shot.Save(Path.Combine(stickDir,frame.ToString("D3")+".png"));
            }
            // A deterministic animation specimen, never sampled from a device.
            string motionDir=Path.Combine(output,"dpad-motion"); Directory.CreateDirectory(motionDir);
            PadState pose=new PadState();
            for(int frame=0;frame<90;frame++)
            using(Bitmap shot=new Bitmap(480,460))
            using(Graphics canvas=Graphics.FromImage(shot))
            using(Font label=new Font("Microsoft YaHei UI",14))
            {
                int phase=frame/15;
                PadButton buttons=phase==1?PadButton.Up:phase==3?PadButton.Left:phase==5?PadButton.Down|PadButton.Right:0;
                pose=PadState.Blend(pose,new PadState { Buttons=buttons },.8f);
                canvas.Clear(Ui.Bg0);
                canvas.DrawString("模拟输入 · "+(buttons==0?"松开":buttons.ToString()),label,new SolidBrush(Ui.Text),16,12);
                var state=canvas.Save();
                canvas.TranslateTransform(240,260); canvas.ScaleTransform(2.6f,2.6f);
                canvas.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Dpad3D.Draw(canvas,DeviceSkin.All[2],0,0,pose);
                canvas.Restore(state);
                shot.Save(Path.Combine(motionDir,frame.ToString("D3")+".png"));
            }

            // DrawToBitmap cannot capture the dark DWM title bar; everything else is faithful.
            Application.EnableVisualStyles();
            string demo = Path.GetFullPath(Path.Combine(output, Path.Combine("..", "cocoon-demo-root")));
            if (Directory.Exists(demo)) Environment.SetEnvironmentVariable("THOR_COCOON_DATA_ROOT", demo);
            MainForm form = new MainForm(true);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(0, 0);
            form.Shown += delegate
            {
                Timer timer = new Timer();
                int phase=0;
                DateTime waitUntil=DateTime.MinValue;
                timer.Interval = 6000; // thumbnails and device probe settle
                timer.Tick += delegate
                {
                    if(phase==0)
                    {
                        using(Bitmap shot=new Bitmap(form.Width,form.Height))
                        { form.DrawToBitmap(shot,new Rectangle(0,0,form.Width,form.Height)); shot.Save(Path.Combine(output,"window.png")); }
                        using(PreviewOptionsDialog options=new PreviewOptionsDialog(PreviewOptions.Load()))
                        {
                            options.Show(form); options.Refresh();
                            using(Bitmap shot=new Bitmap(options.Width,options.Height))
                            { options.DrawToBitmap(shot,new Rectangle(0,0,options.Width,options.Height)); shot.Save(Path.Combine(output,"preview-settings.png")); }
                            options.Close();
                        }
                        // The dual view (no shell) for the same selection; the saved view is put back afterwards.
                        {
                            Segmented views=(Segmented)typeof(MainForm).GetField("viewPicker",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
                            string savedView=Settings.Get("view","0"); int before=views.SelectedIndex;
                            views.SelectedIndex=1; form.Refresh();
                            using(Bitmap shot=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(shot,new Rectangle(0,0,form.Width,form.Height)); shot.Save(Path.Combine(output,"window-dual.png")); }
                            Segmented style=(Segmented)typeof(MainForm).GetField("dualStyle",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
                            PreviewBox stage=(PreviewBox)typeof(MainForm).GetField("preview",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
                            string savedStyle=Settings.Get("dualStyle","0"); int styleBefore=style.SelectedIndex;
                            style.SelectedIndex=1; stage.ShowcaseTitle="03  地图 — 城镇图标与七个 HUD 项"; stage.ShowcaseLabel="下屏 · 伴侣界面"; form.Refresh();
                            using(Bitmap shot=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(shot,new Rectangle(0,0,form.Width,form.Height)); shot.Save(Path.Combine(output,"window-showcase.png")); }
                            using(Bitmap page=stage.ExportDual(true)) if(page!=null) page.Save(Path.Combine(output,"showcase-export.png"));
                            // Export dialog: transparent framed, and a gradient behind the stitched screens.
                            ExportStyle[] looks={ new ExportStyle{ Kind=ExportKind.Framed }, new ExportStyle{ Kind=ExportKind.Stacked, Background="grad:EEF1F6:C9D3E2" } };
                            string[] names={ "export-dialog-framed.png","export-dialog-dual.png" };
                            for(int i=0;i<looks.Length;i++)
                                using(ExportImageDialog export=new ExportImageDialog(looks[i],stage.Skin,stage.TopImage,stage.BottomImage,stage.ShowcaseTitle,stage.ShowcaseLabel))
                                {
                                    export.Show(form);
                                    ExportPreviewPanel panel=(ExportPreviewPanel)typeof(ExportImageDialog).GetField("preview",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(export);
                                    DateTime until=DateTime.UtcNow.AddSeconds(8);
                                    do { Application.DoEvents(); System.Threading.Thread.Sleep(20); } while((panel.Busy||DateTime.UtcNow<until.AddSeconds(-7.5))&&DateTime.UtcNow<until);
                                    Shot(export,Path.Combine(output,names[i]));
                                    export.Close();
                                }
                            using(Bitmap framed=new ExportStyle{ Kind=ExportKind.Framed }.Render(stage.Skin,stage.TopImage,stage.BottomImage,"","",null)) framed.Save(Path.Combine(output,"export-transparent.png"));
                            style.SelectedIndex=styleBefore; Settings.Set("dualStyle",savedStyle);
                            views.SelectedIndex=before; Settings.Set("view",savedView);
                        }
                        GalleryView gallery=(GalleryView)typeof(MainForm).GetField("gallery",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
                        // Handheld screenshots from the last mirror (no device access: the window is offline).
                        string shotsBase=Path.Combine(Adb.AppDir(),"device-shots");
                        if(Directory.Exists(shotsBase)&&Directory.GetDirectories(shotsBase).Length>0)
                        {
                            string savedSerial=Settings.Get("shots.serial","");
                            Settings.Set("shots.serial",Path.GetFileName(Directory.GetDirectories(shotsBase)[0]));
                            Segmented source=(Segmented)typeof(MainForm).GetField("sourcePicker",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
                            PreviewBox stage=(PreviewBox)typeof(MainForm).GetField("preview",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
                            source.SelectedIndex=1;
                            DateTime until=DateTime.UtcNow.AddSeconds(10);
                            Func<bool> mirrored=()=>gallery.Items.Count>0&&form.ShotMirror!=null&&gallery.Items[0].Path.StartsWith(form.ShotMirror.Root,StringComparison.OrdinalIgnoreCase);
                            while(!mirrored()&&DateTime.UtcNow<until) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                            for(DateTime settle=DateTime.UtcNow.AddMilliseconds(700);DateTime.UtcNow<settle;) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                            foreach(GalleryItem item in gallery.Items) if(item.Paired&&item.Screen==0) { gallery.SelectOnly(gallery.IndexOf(item.Path)); break; }
                            until=DateTime.UtcNow.AddSeconds(6);
                            while(!stage.HasCompanion&&DateTime.UtcNow<until) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                            Shot(form,Path.Combine(output,"window-device-shots.png"));
                            source.SelectedIndex=0;
                            Settings.Set("shots.serial",savedSerial);
                        }
                        Motion.Enabled=false; gallery.SetLock("实时预览中","停止");
                        using(Bitmap shot=new Bitmap(gallery.Width,gallery.Height))
                        { gallery.DrawToBitmap(shot,new Rectangle(0,0,gallery.Width,gallery.Height)); shot.Save(Path.Combine(output,"gallery-locked.png")); }
                        gallery.SetLock(null,null); Motion.Enabled=true;
                        GalleryItem movie=gallery.Items.Find(item=>item.IsVideo&&item.Paired);
                        if(movie==null) { timer.Stop(); timer.Dispose(); form.Close(); return; }
                        gallery.SelectOnly(gallery.IndexOf(movie.Path)); phase=1; timer.Interval=100; waitUntil=DateTime.UtcNow.AddSeconds(20); return;
                    }
                    VideoPlayback player=(VideoPlayback)typeof(MainForm).GetField("playback",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
                    if(player!=null&&player.Error!=null) { File.WriteAllText(Path.Combine(output,"playback-render-error.txt"),player.Error); timer.Stop(); timer.Dispose(); form.Close(); return; }
                    if(phase==1)
                    {
                        if(player!=null&&player.Ready)
                        { player.Seek(Math.Min(2,player.Duration/2)); phase=2; waitUntil=DateTime.UtcNow.AddSeconds(2); return; }
                        if(DateTime.UtcNow>=waitUntil)
                        { File.WriteAllText(Path.Combine(output,"playback-render-error.txt"),"Playback did not become ready"); timer.Stop(); timer.Dispose(); form.Close(); }
                        return;
                    }
                    if(DateTime.UtcNow<waitUntil) return;
                    if(phase==2)
                    {
                        using(Bitmap shot=new Bitmap(form.Width,form.Height))
                        { form.DrawToBitmap(shot,new Rectangle(0,0,form.Width,form.Height)); shot.Save(Path.Combine(output,"window-playback.png")); }
                        // Game library against the offline demo data root.
                        NavRail rail=(NavRail)typeof(MainForm).GetField("rail",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
                        rail.SelectedIndex=1; phase=3; waitUntil=DateTime.UtcNow.AddSeconds(25); return;
                    }
                    LibraryPage library=(LibraryPage)typeof(MainForm).GetField("library",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
                    if(phase==3)
                    {
                        if(library.Current==LibraryPage.Phase.Starting&&DateTime.UtcNow<waitUntil) return;
                        GameGridView games=(GameGridView)typeof(LibraryPage).GetField("grid",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(library);
                        int full=games.Games.FindIndex(game=>game.HasHero&&game.HasLogo&&game.HasCover);
                        if(full>=0) games.Select(full);
                        phase=4; waitUntil=DateTime.UtcNow.AddSeconds(2.5); return; // let art arrive and cards settle
                    }
                    using(Bitmap shot=new Bitmap(form.Width,form.Height))
                    { form.DrawToBitmap(shot,new Rectangle(0,0,form.Width,form.Height)); shot.Save(Path.Combine(output,"window-library.png")); }
                    // Folded groups and the header pinned while scrolling; restored afterwards.
                    {
                        GameGridView games=(GameGridView)typeof(LibraryPage).GetField("grid",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(library);
                        string foldedBefore=Settings.Get("library.collapsed","");
                        Motion.Enabled=false;
                        if(games.HeaderCount>2){ games.Toggle(1); games.Toggle(2); }
                        typeof(GameGridView).GetMethod("OnMouseWheel",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(games,new object[]{new MouseEventArgs(MouseButtons.None,0,10,10,-360)});
                        typeof(GameGridView).GetField("scroll",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(games,typeof(GameGridView).GetField("target",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(games));
                        games.Refresh();
                        using(Bitmap shot=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(shot,new Rectangle(0,0,form.Width,form.Height)); shot.Save(Path.Combine(output,"window-library-folded.png")); }
                        if(games.HeaderCount>2){ games.Toggle(1); games.Toggle(2); }
                        Settings.Set("library.collapsed",foldedBefore);
                        Motion.Enabled=true;
                    }
                    timer.Stop(); timer.Dispose();
                    ImportSamples(output,library);
                    EditSamples(output,form,library); // closes the window when done
                };
                timer.Start();
            };
            Application.Run(form);
        }

        private static void Shot(Control target, string path)
        {
            target.Refresh();
            using(Bitmap shot=new Bitmap(target.Width,target.Height)) { target.DrawToBitmap(shot,new Rectangle(0,0,target.Width,target.Height)); shot.Save(path); }
        }

        // Metadata editing against the demo library: form, review, the staged state in the window
        // and the draft list. The entry staged here is undone again, so the demo draft ends as it began.
        private static async void EditSamples(string output, MainForm form, LibraryPage library)
        {
            try
            {
                GameEditSession edits=library.Edits;
                GameGridView grid=(GameGridView)typeof(LibraryPage).GetField("grid",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(library);
                GameEntry game=grid.Selected;
                if(game==null||library.Current!=LibraryPage.Phase.Ready) return;
                await edits.Refresh();
                BindingFlags hidden=BindingFlags.Instance|BindingFlags.NonPublic;
                using(MetadataDialog dialog=new MetadataDialog(edits,game))
                {
                    dialog.StartPosition=FormStartPosition.Manual; dialog.Location=new Point(-4000,0); dialog.Show(form);
                    for(int i=0;i<200&&!dialog.Describe.StartsWith("Edit");i++) await System.Threading.Tasks.Task.Delay(50);
                    Shot(dialog,Path.Combine(output,"metadata-edit.png"));
                    Dictionary<string,TextField> texts=(Dictionary<string,TextField>)typeof(MetadataDialog).GetField("texts",hidden).GetValue(dialog);
                    texts["developer"].Text="Northwind Works"; texts["genres"].Text="动作 / 冒险"; texts["releaseDate"].Text="2024-03-15";
                    MethodInfo primary=typeof(MetadataDialog).GetMethod("Primary",hidden);
                    primary.Invoke(dialog,null);
                    for(int i=0;i<200&&!dialog.Describe.StartsWith("Review");i++) await System.Threading.Tasks.Task.Delay(50);
                    Shot(dialog,Path.Combine(output,"metadata-review.png"));
                    int had=edits.Count;
                    primary.Invoke(dialog,null);
                    for(int i=0;i<200&&edits.Count==had&&dialog.Describe.StartsWith("Saving");i++) await System.Threading.Tasks.Task.Delay(50);
                    dialog.Close();
                }
                // The real pointer may have moved the selection while the dialog was up.
                if(grid.Selected!=game) grid.Select(grid.Games.IndexOf(game));
                await System.Threading.Tasks.Task.Delay(1500);
                Shot(form,Path.Combine(output,"window-library-staged.png"));
                List<GameEntry> all=(List<GameEntry>)typeof(LibraryPage).GetField("all",hidden).GetValue(library);
                using(PendingDialog pending=new PendingDialog(edits,delegate(int id){GameEntry found=all.Find(entry=>entry.Id==id);return found==null?"?":found.Name;}))
                {
                    pending.StartPosition=FormStartPosition.Manual; pending.Location=new Point(-4000,0); pending.Show(form);
                    Shot(pending,Path.Combine(output,"pending-list.png"));
                    pending.Close();
                }
                // Apply-to-device dialog: sample review, progress and recovery pages. No device command is sent;
                // the pages are set directly and the dialog's buttons are never pressed.
                using(AdbCocoonDevice unused=new AdbCocoonDevice(Adb.ExePath,"render-check"))
                using(ApplyDialog apply=new ApplyDialog(edits,library.NewApplier(unused),delegate(int id){return all.Find(entry=>entry.Id==id);},delegate(DeviceApplyResult done){return System.Threading.Tasks.Task.FromResult<string>(null);}))
                {
                    apply.StartPosition=FormStartPosition.Manual; apply.Location=new Point(-4000,0); apply.Show(form);
                    for(int i=0;i<200&&apply.Describe.StartsWith("Loading");i++) await System.Threading.Tasks.Task.Delay(50);
                    if(apply.Describe.StartsWith("Migration")) Shot(apply,Path.Combine(output,"apply-migration.png"));
                    Type applyStep=typeof(ApplyDialog).GetNestedType("Step",BindingFlags.NonPublic);
                    MethodInfo applyGo=typeof(ApplyDialog).GetMethod("Go",hidden);
                    ReviewList reviewList=(ReviewList)typeof(ApplyDialog).GetField("list",hidden).GetValue(apply);
                    reviewList.SetRows(new List<ReviewList.Row>{
                        new ReviewList.Row{Game=game.Name,Field="开发商",Before="Moon Works",After="Northwind Works"},
                        new ReviewList.Row{Game=game.Name,Field="发售日期",Before="2005-02-02",After="2024-03-15"},
                        new ReviewList.Row{Game=game.Name,Field="类型",Before="Puzzle",After="动作 / 冒险"},
                        new ReviewList.Row{Game="雾港来信",Field="收藏",Before="否",After="是"}});
                    typeof(ApplyDialog).GetField("error",hidden).SetValue(apply,null);
                    applyGo.Invoke(apply,new object[]{Enum.Parse(applyStep,"Review")}); Shot(apply,Path.Combine(output,"apply-review.png"));
                    typeof(ApplyDialog).GetField("stage",hidden).SetValue(apply,"install");
                    applyGo.Invoke(apply,new object[]{Enum.Parse(applyStep,"Running")}); Shot(apply,Path.Combine(output,"apply-running.png"));
                    typeof(ApplyDialog).GetField("error",hidden).SetValue(apply,ApplyText.Error("APPLY_RECOVERY_REQUIRED"));
                    applyGo.Invoke(apply,new object[]{Enum.Parse(applyStep,"Recovery")}); Shot(apply,Path.Combine(output,"apply-recovery.png"));
                    typeof(ApplyDialog).GetField("error",hidden).SetValue(apply,ApplyText.Error("PATCH_CONFLICT"));
                    applyGo.Invoke(apply,new object[]{Enum.Parse(applyStep,"Failed")}); Shot(apply,Path.Combine(output,"apply-conflict.png"));
                    apply.Close();
                }
                // Scraping dialogs with sample rows (the demo root has no source credentials, so nothing goes online).
                using(ScrapeDialog scrape=new ScrapeDialog(edits,game))
                {
                    scrape.StartPosition=FormStartPosition.Manual; scrape.Location=new Point(-4000,0); scrape.Show(form);
                    for(int i=0;i<100&&scrape.Describe.StartsWith("Loading");i++) await System.Threading.Tasks.Task.Delay(50);
                    List<FlatButton> chips=(List<FlatButton>)typeof(ScrapeDialog).GetField("chips",hidden).GetValue(scrape);
                    foreach(string id in new[]{"thegamesdb","steamgriddb","retroachievements"})
                    { FlatButton chip=new FlatButton(null,ScrapeClient.ProviderName(id)){Kind=ButtonKind.Subtle,Tag=id,Checked=id!="steamgriddb"}; chips.Add(chip); scrape.Body.Controls.Add(chip); }
                    ((CandidateList)typeof(ScrapeDialog).GetField("list",hidden).GetValue(scrape)).SetRows(new List<ScrapeCandidate>{
                        new ScrapeCandidate{Id=1,Provider="thegamesdb",Title="Moss Harbor: Tides of Lantern",Platform="switch",Year="2005",Confidence=0.94,Decision="accept",Summary="A quiet puzzle adventure about a harbour town and the lights that guide its boats home."},
                        new ScrapeCandidate{Id=2,Provider="thegamesdb",Title="Moss Harbor",Platform="gba",Year="2003",Confidence=0.85,Decision="review",Summary="Handheld version."},
                        new ScrapeCandidate{Id=3,Provider="steamgriddb",Title="Moss Harbour Stories",Platform="switch",Confidence=0.65,Decision="reject"}});
                    typeof(ScrapeDialog).GetField("note",hidden).SetValue(scrape,null); typeof(ScrapeDialog).GetField("error",hidden).SetValue(scrape,null);
                    Type scrapeStep=typeof(ScrapeDialog).GetNestedType("Step",BindingFlags.NonPublic); MethodInfo scrapeGo=typeof(ScrapeDialog).GetMethod("Go",hidden);
                    scrapeGo.Invoke(scrape,new object[]{Enum.Parse(scrapeStep,"Results")}); Shot(scrape,Path.Combine(output,"scrape-results.png"));
                    ((ChangeList)typeof(ScrapeDialog).GetField("changes",hidden).GetValue(scrape)).SetRows(new List<ChangeList.Row>{
                        new ChangeList.Row{Field="title",Before="Moss Harbor",After="Moss Harbor: Tides of Lantern",Picked=false},
                        new ChangeList.Row{Field="summary",Before="（空）",After="A quiet puzzle adventure about a harbour town and the lights that guide its boats home.",Picked=true},
                        new ChangeList.Row{Field="developer",Before="（空）",After="Moon Works",Picked=true},
                        new ChangeList.Row{Field="releaseDate",Before="（空）",After="2005-02-02",Picked=true},
                        new ChangeList.Row{Field="genres",Before="Puzzle",After="Puzzle, Adventure",Picked=false}});
                    scrapeGo.Invoke(scrape,new object[]{Enum.Parse(scrapeStep,"Review")}); Shot(scrape,Path.Combine(output,"scrape-review.png"));
                    scrape.Close();
                }
                using(BatchScrapeDialog batch=new BatchScrapeDialog(edits,all.GetRange(0,Math.Min(9,all.Count)),all,all))
                {
                    batch.StartPosition=FormStartPosition.Manual; batch.Location=new Point(-4000,0); batch.Show(form);
                    await System.Threading.Tasks.Task.Delay(600);
                    List<FlatButton> chips=(List<FlatButton>)typeof(BatchScrapeDialog).GetField("chips",hidden).GetValue(batch);
                    foreach(string id in new[]{"thegamesdb","steamgriddb","retroachievements"})
                    { FlatButton chip=new FlatButton(null,ScrapeClient.ProviderName(id)){Kind=ButtonKind.Subtle,Tag=id,Checked=id!="steamgriddb"}; chips.Add(chip); batch.Body.Controls.Add(chip); }
                    typeof(BatchScrapeDialog).GetField("note",hidden).SetValue(batch,null); typeof(BatchScrapeDialog).GetField("error",hidden).SetValue(batch,null);
                    MethodInfo batchArrange=typeof(BatchScrapeDialog).GetMethod("Arrange",hidden); Type batchStep=typeof(BatchScrapeDialog).GetNestedType("Step",BindingFlags.NonPublic);
                    batchArrange.Invoke(batch,null); Shot(batch,Path.Combine(output,"scrape-batch-setup.png"));
                    List<BatchResultList.Row> results=new List<BatchResultList.Row>();
                    for(int i=0;i<Math.Min(9,all.Count);i++) results.Add(new BatchResultList.Row{GameId=all[i].Id,Name=all[i].Name,Platform=GameEntry.PlatformName(all[i].Platform),Candidates=i<6?3+i:0,Confident=i<2,Staged=i==0,Error=i==8?"来源连接失败，请稍后重试":null});
                    ((BatchResultList)typeof(BatchScrapeDialog).GetField("list",hidden).GetValue(batch)).SetRows(results);
                    foreach(KeyValuePair<string,int> number in new Dictionary<string,int>{{"total",9},{"completed",9},{"identified",6},{"failed",1}}) typeof(BatchScrapeDialog).GetField(number.Key,hidden).SetValue(batch,number.Value);
                    typeof(BatchScrapeDialog).GetField("step",hidden).SetValue(batch,Enum.Parse(batchStep,"Done")); batchArrange.Invoke(batch,null); Shot(batch,Path.Combine(output,"scrape-batch-done.png"));
                    batch.Close();
                }
                // Artwork picker with sample thumbnails drawn locally (no download happens).
                using(ArtworkDialog artwork=new ArtworkDialog(edits,game,ArtworkSlot.Icon))
                {
                    artwork.StartPosition=FormStartPosition.Manual; artwork.Location=new Point(-4000,0); artwork.Show(form);
                    for(int i=0;i<100&&artwork.Describe.Contains("busy=True");i++) await System.Threading.Tasks.Task.Delay(50);
                    AssetGrid assetGrid=(AssetGrid)typeof(ArtworkDialog).GetField("grid",hidden).GetValue(artwork);
                    Dictionary<string,Bitmap> thumbs=(Dictionary<string,Bitmap>)typeof(AssetGrid).GetField("thumbs",hidden).GetValue(assetGrid);
                    List<ScrapeAsset> samples=new List<ScrapeAsset>();
                    Color[] tints={Color.SeaGreen,Color.SteelBlue,Color.IndianRed,Color.Goldenrod,Color.MediumPurple,Color.CadetBlue,Color.Sienna,Color.OliveDrab,Color.SlateGray,Color.Teal};
                    for(int i=0;i<10;i++)
                    {
                        string url="https://example.invalid/sample-"+i+".png";
                        Bitmap tile=new Bitmap(120,180); using(Graphics tg=Graphics.FromImage(tile)){ using(System.Drawing.Drawing2D.LinearGradientBrush b=new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0,0,120,180),tints[i],Color.Black,70f)) tg.FillRectangle(b,0,0,120,180); tg.FillEllipse(Brushes.White,40,60,40,40); }
                        thumbs[url]=tile;
                        samples.Add(new ScrapeAsset{Url=url,Thumb=url,Width=600,Height=900,Provider=i<7?"steamgriddb":"thegamesdb",Style=i<7?(i%2==0?"alternate":"official"):"boxart · front"});
                    }
                    typeof(ArtworkDialog).GetField("note",hidden).SetValue(artwork,null); typeof(ArtworkDialog).GetField("error",hidden).SetValue(artwork,null);
                    typeof(ArtworkDialog).GetField("busy",hidden).SetValue(artwork,false);
                    assetGrid.SetAssets(samples); assetGrid.Select(1);
                    typeof(ArtworkDialog).GetMethod("Arrange",hidden).Invoke(artwork,null);
                    Shot(artwork,Path.Combine(output,"artwork-picker.png"));
                    artwork.Close();
                }
                using(ScrapeSettingsDialog settings=new ScrapeSettingsDialog(library.Backend))
                {
                    settings.StartPosition=FormStartPosition.Manual; settings.Location=new Point(-4000,0); settings.Show(form);
                    for(int i=0;i<100&&settings.Describe.StartsWith("rows=0");i++) await System.Threading.Tasks.Task.Delay(50);
                    Shot(settings,Path.Combine(output,"scrape-sources.png"));
                    settings.Close();
                }
                StagedMetadata staged=edits.MetadataFor(game.Id);
                if(staged!=null) await edits.Undo(staged.EntryId);
            }
            catch(Exception failure) { File.WriteAllText(Path.Combine(output,"render-error.txt"),"Edit samples: "+failure); }
            finally { form.Close(); }
        }

        // Import wizard with a sample plan and a sample running job (no real import happens).
        private static void ImportSamples(string output, LibraryPage library)
        {
            ImportRunner runner=new ImportRunner(library.Backend,System.Threading.SynchronizationContext.Current);
            string files="";
            for(int i=0;i<6;i++) files+=(i>0?",":"")+"{\"relativePath\":\"_cache/art-"+i+".png\",\"bytes\":"+(180000+i*9000)+",\"sha256\":\""+new string('a',64)+"\"}";
            LibraryImportPlan plan=new LibraryImportPlan((Dictionary<string,object>)Json.Parse("{\"contract\":\"cocoon.library-import/1\",\"planId\":\""+Guid.NewGuid()+"\",\"state\":\"planned\",\"totalBytes\":260046848,"
                +"\"summary\":{\"totalGames\":770,\"visibleGames\":732,\"pendingChanges\":5},\"files\":["+files+"],\"replacesExistingLibrary\":true,\"warnings\":[\"有 5 项尚未应用的暂存修改会一并导入，仍保持待审阅状态\"]}"));
            Type step=typeof(ImportDialog).GetNestedType("Step",BindingFlags.NonPublic);
            MethodInfo go=typeof(ImportDialog).GetMethod("Go",BindingFlags.Instance|BindingFlags.NonPublic);
            using(ImportDialog dialog=new ImportDialog(runner))
            {
                typeof(ImportDialog).GetField("plan",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(dialog,plan);
                go.Invoke(dialog,new object[] { Enum.Parse(step,"Review") });
                dialog.StartPosition=FormStartPosition.Manual; dialog.Location=new Point(-4000,0); dialog.Show(); dialog.Refresh();
                using(Bitmap shot=new Bitmap(dialog.Width,dialog.Height)) { dialog.DrawToBitmap(shot,new Rectangle(0,0,dialog.Width,dialog.Height)); shot.Save(Path.Combine(output,"import-review.png")); }
                LibraryImportJob job=new LibraryImportJob((Dictionary<string,object>)Json.Parse("{\"contract\":\"cocoon.library-import/1\",\"jobId\":\""+Guid.NewGuid()+"\",\"kind\":\"import\",\"state\":\"preparing\",\"copiedFiles\":214,\"totalFiles\":380,\"copiedBytes\":141557760,\"totalBytes\":260046848}"));
                typeof(ImportRunner).GetProperty("Job").GetSetMethod(true).Invoke(runner,new object[] { job });
                go.Invoke(dialog,new object[] { Enum.Parse(step,"Running") }); dialog.Refresh();
                using(Bitmap shot=new Bitmap(dialog.Width,dialog.Height)) { dialog.DrawToBitmap(shot,new Rectangle(0,0,dialog.Width,dialog.Height)); shot.Save(Path.Combine(output,"import-running.png")); }
                dialog.Close();
            }
            runner.Dispose();
        }

        private sealed class TouchStage : PreviewBox
        {
            public void Render(Graphics g) { OnPaint(new PaintEventArgs(g, ClientRectangle)); }
        }

        // Simulated touches (no device): a tap, a drag, a long press and a two-finger spread.
        private static TouchHistory TouchScript()
        {
            TouchHistory history = new TouchHistory(0);
            for (int t = 0; t <= 2600; t += 16)
            {
                List<TouchPoint> points = new List<TouchPoint>();
                if (t < 140) points.Add(new TouchPoint(0, 1, 0.3f, 0.42f));
                if (t >= 320 && t < 820) { float k = (t - 320) / 500f; points.Add(new TouchPoint(0, 2, 0.55f + 0.3f * k, 0.66f - 0.3f * k)); }
                if (t >= 950 && t < 1900) points.Add(new TouchPoint(1, 3, 0.5f, 0.45f));
                if (t >= 2000 && t < 2400) { float k = (t - 2000) / 400f; points.Add(new TouchPoint(1, 4, 0.42f - 0.18f * k, 0.55f + 0.1f * k)); points.Add(new TouchPoint(1, 5, 0.58f + 0.18f * k, 0.45f - 0.1f * k)); }
                history.Add(t, points.ToArray());
            }
            return history;
        }

        private static void TouchSamples(string output, Image top, Image bottom)
        {
            TouchHistory script = TouchScript();
            bool motion = Motion.Enabled;
            Motion.Enabled = false;
            try
            {
                using (TouchStage stage = new TouchStage { Size = new Size(700, 860) })
                {
                    stage.Skin = DeviceSkin.All[1];
                    stage.SetImages(top == null ? null : (Image)top.Clone(), bottom == null ? null : (Image)bottom.Clone(), false);
                    string[] labels = { "点击按下", "拖动尾迹", "长按", "松开波纹", "双指" };
                    double[] moments = { 90, 700, 1700, 900, 2300 };
                    using (Bitmap sheet = new Bitmap(700 * labels.Length, 900))
                    using (Graphics g = Graphics.FromImage(sheet))
                    using (Font font = new Font("Microsoft YaHei UI", 13))
                    using (SolidBrush ink = new SolidBrush(Ui.Text))
                    {
                        g.Clear(Ui.Bg0);
                        for (int i = 0; i < labels.Length; i++)
                        {
                            stage.SetTouches(script.At(moments[i]));
                            using (Bitmap frame = new Bitmap(700, 860))
                            {
                                using (Graphics f = Graphics.FromImage(frame)) stage.Render(f);
                                g.DrawImageUnscaled(frame, i * 700, 36);
                            }
                            g.DrawString("模拟触摸 · " + labels[i], font, ink, i * 700 + 16, 8);
                        }
                        sheet.Save(Path.Combine(output, "touch-feedback.png"));
                    }
                    string frames = Path.Combine(output, "touch-motion");
                    Directory.CreateDirectory(frames);
                    foreach (string old in Directory.GetFiles(frames, "*.png")) File.Delete(old);
                    for (int i = 0; i * 33 <= 2700; i++)
                    {
                        stage.SetTouches(script.At(i * 33));
                        using (Bitmap frame = new Bitmap(700, 860))
                        {
                            using (Graphics f = Graphics.FromImage(frame)) stage.Render(f);
                            frame.Save(Path.Combine(frames, i.ToString("D3") + ".png"));
                        }
                    }
                }
            }
            finally { Motion.Enabled = motion; }
        }

        // Mascot moods and a few frames of each, plus the face drawn on a dark screen.
        private static void MascotSamples(string output)
        {
            using(Bitmap sheet=new Bitmap(900,420))
            using(Graphics g=Graphics.FromImage(sheet))
            {
                g.Clear(Ui.Bg0);
                g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                MascotMood[] moods={MascotMood.Idle,MascotMood.Sleep,MascotMood.Happy};
                for(int m=0;m<3;m++)
                    for(int f=0;f<4;f++)
                        Mascot.Draw(g,new RectangleF(20+f*110,10+m*130,100,100),moods[m],f*300);
                for(int m=0;m<3;m++)
                {
                    RectangleF screen=new RectangleF(480,20+m*130,380,110);
                    using(SolidBrush dark=new SolidBrush(Color.FromArgb(14,16,22))) g.FillRectangle(dark,screen);
                    Mascot.DrawFace(g,screen,moods[m],800,Color.FromArgb(150,214,212,208));
                }
                sheet.Save(Path.Combine(output,"mascot.png"));
            }
            // A capture without its paired screen: the empty screen shows the mascot.
            using(PreviewBox stage=new PreviewBox { Size=new Size(700,860) })
            using(Bitmap shot=new Bitmap(700,860))
            {
                Bitmap upper=new Bitmap(1920,1080);
                using(Graphics g=Graphics.FromImage(upper)) g.Clear(Color.FromArgb(120,170,120));
                stage.Skin=DeviceSkin.All[2];
                stage.SetImage(upper);
                stage.DrawToBitmap(shot,new Rectangle(0,0,700,860));
                shot.Save(Path.Combine(output,"mascot-unpaired.png"));
            }
        }

        private static void MouseSamples(string output)
        {
            string frames = Path.Combine(output, "mouse-demo-motion");
            Directory.CreateDirectory(frames);
            using (DemoStage stage = new DemoStage { Size = new Size(900, 1100), Skin = DeviceSkin.All[2] })
            using (Bitmap full = new Bitmap(900, 1100))
            using (Graphics canvas = Graphics.FromImage(full))
            using (Font label = new Font("Microsoft YaHei UI", 13))
            using (Bitmap sheet = new Bitmap(1280, 580))
            using (Graphics gallery = Graphics.FromImage(sheet))
            {
                stage.CreateControl(); gallery.Clear(Ui.Bg0);
                for (int frame = 0; frame < 120; frame++)
                {
                    int phase = frame / 30, step = frame % 30;
                    PointF point = phase == 0 ? ShellGeometry.FaceCenters[1] : phase == 1 ? new PointF(123, 811)
                        : phase == 2 ? new PointF(115, 696) : new PointF(766, 860);
                    MouseButtons mouse = phase == 3 ? MouseButtons.Right : MouseButtons.Left;
                    if (step == 0) stage.Mouse(point, mouse, true);
                    if (step == 18) stage.Mouse(point, mouse, false);
                    if (step < 18 && phase == 1) stage.Drag(step < 8 ? new PointF(123, 811) : new PointF(142, 842));
                    if (step < 18 && phase == 2) stage.Drag(new PointF(115 + 27 * (float)Math.Sin(step * .2), 696 - 27 * (float)Math.Cos(step * .2)));
                    stage.AdvanceAnimation(1.0 / 30); stage.Render(canvas);
                    using (Bitmap sample = new Bitmap(640, 580))
                    using (Graphics g = Graphics.FromImage(sample))
                    {
                        g.Clear(Ui.Bg0);
                        string name = phase == 0 ? "A 按压 / 回弹" : phase == 1 ? "十字键 / 斜向" : phase == 2 ? "左摇杆拖动 / 归中" : "右键 R3 / 松开";
                        g.DrawString("鼠标演示 · " + name + " · 不控制实机", label, new SolidBrush(Ui.Text), 12, 8);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        Rectangle left = ShellGeometry.ToClient(stage.DeviceBounds(), new RectangleF(30, 604, 184, 352));
                        Rectangle right = ShellGeometry.ToClient(stage.DeviceBounds(), new RectangleF(678, 600, 186, 352));
                        g.DrawImage(full, new Rectangle(20, 46, 282, 532), left, GraphicsUnit.Pixel);
                        g.DrawImage(full, new Rectangle(338, 46, 282, 532), right, GraphicsUnit.Pixel);
                        sample.Save(Path.Combine(frames, frame.ToString("D3") + ".png"));
                        if (frame == 12 || frame == 42)
                            gallery.DrawImageUnscaled(sample, frame == 12 ? 0 : 640, 0);
                    }
                }
                sheet.Save(Path.Combine(output, "mouse-demo.png"));
            }
        }
    }
}
