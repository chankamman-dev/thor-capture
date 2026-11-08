using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace ThorCapture
{
    internal static class PreviewPacer
    {
        internal static bool Due(double now,int fps,ref double deadline)
        {
            if(now<deadline) return false;
            double interval=1.0/fps;
            // Preserve the cadence across a coarser UI timer; never replay missed frames.
            deadline+=(Math.Floor((now-deadline)/interval)+1)*interval;
            return true;
        }
    }
    // Source encoding and host presentation are deliberately separate settings.
    internal sealed class PreviewOptions
    {
        internal int MaxHeight=540,BitRate=3000000,DisplayFps=60,Screens=0;
        internal bool PixelMode;
        internal PreviewOptions Copy() { return (PreviewOptions)MemberwiseClone(); }
        internal static int Number(string text,int fallback,int min,int max)
        { int n; return int.TryParse(text,NumberStyles.Integer,CultureInfo.InvariantCulture,out n)&&n>=min&&n<=max?n:fallback; }
        internal static PreviewOptions Load()
        { return Read(Settings.Get); }
        internal static PreviewOptions Read(Func<string,string,string> read)
        {
            return new PreviewOptions {
                MaxHeight=Number(read("previewHeight","540"),540,0,2160),
                BitRate=Number(read("previewBitRate","3000000"),3000000,100000,100000000),
                DisplayFps=Number(read("previewFps","60"),60,1,120),
                Screens=Number(read("previewScreens","0"),0,0,2),
                PixelMode=read("previewPixels","0")=="1"
            };
        }
        internal bool Save()
        {
            bool ok=Settings.Set("previewHeight",MaxHeight.ToString(CultureInfo.InvariantCulture));
            ok=Settings.Set("previewBitRate",BitRate.ToString(CultureInfo.InvariantCulture))&&ok;
            ok=Settings.Set("previewFps",DisplayFps.ToString(CultureInfo.InvariantCulture))&&ok;
            ok=Settings.Set("previewScreens",Screens.ToString(CultureInfo.InvariantCulture))&&ok;
            return Settings.Set("previewPixels",PixelMode?"1":"0")&&ok;
        }
        internal bool Includes(DeviceDisplay display) { return Screens==0 || (Screens==2)==display.IsLower; }
        internal Size SizeFor(DeviceDisplay display)
        {
            if(display.Width<2||display.Height<2||display.Width>8192||display.Height>8192) throw new System.IO.IOException("预览屏幕尺寸无效");
            double ratio=MaxHeight==0?1:Math.Min(1,Math.Min(MaxHeight*16.0/9/display.Width,(double)MaxHeight/display.Height));
            return new Size(Math.Max(2,(int)(display.Width*ratio)/2*2),Math.Max(2,(int)(display.Height*ratio)/2*2));
        }
        internal static PreviewOptions Preset(int index)
        {
            if(index==0) return new PreviewOptions { MaxHeight=360,BitRate=2000000 };
            if(index==2) return new PreviewOptions { MaxHeight=720,BitRate=8000000 };
            if(index==3) return new PreviewOptions { MaxHeight=0,BitRate=20000000 };
            if(index==4) return new PreviewOptions { MaxHeight=0,PixelMode=true,DisplayFps=15 };
            return new PreviewOptions();
        }
        internal string Summary { get { return PixelMode?"原生 PNG 截图取景":(BitRate/1000000.0).ToString("0.0",CultureInfo.InvariantCulture)+" Mbps/屏 · 显示上限 "+DisplayFps+" fps"; } }
    }

    internal sealed class PreviewOptionsDialog : ThemedDialog
    {
        private readonly NumberBox height=new NumberBox(),rate=new NumberBox(),fps=new NumberBox();
        private readonly Segmented screens=new Segmented("双屏","仅上屏","仅下屏"),mode=new Segmented("H.264 视频流","原生 PNG 截图");
        private readonly OptionCards preset=new OptionCards();
        private bool setting;
        internal PreviewOptions Value
        {
            get { return new PreviewOptions { MaxHeight=(int)height.Value,BitRate=(int)(rate.Value*1000000),DisplayFps=(int)fps.Value,Screens=screens.SelectedIndex,PixelMode=mode.SelectedIndex==1 }; }
        }
        internal PreviewOptionsDialog(PreviewOptions current) : base("实时预览设置",null,null,600,340)
        {
            // Presets as cards; index 5 is "custom" and follows manual edits below.
            preset.Add(null,"低延迟","360p · 2 Mbps");
            preset.Add(null,"均衡","540p · 3 Mbps");
            preset.Add(null,"清晰","720p · 8 Mbps");
            preset.Add(null,"原生","20 Mbps");
            preset.Add(null,"PNG","逐帧截图");
            preset.Add(null,"自定义","");
            preset.Height=Ui.S(146);
            AddField("预设",preset,24,6,552);
            screens.Stretch=mode.Stretch=true;
            screens.Height=mode.Height=Ui.S(34);
            AddField("屏幕",screens,24,188,268);
            AddField("取景",mode,308,188,268);
            height.Minimum=0; height.Maximum=2160; height.Increment=90; height.Unit="p";
            rate.Minimum=.1m; rate.Maximum=100; rate.DecimalPlaces=1; rate.Increment=.5m; rate.Unit="Mbps";
            fps.Minimum=1; fps.Maximum=120; fps.Unit="fps";
            AddField("高度（0 为原生）",height,24,254,172);
            AddField("每屏码率",rate,214,254,172);
            AddField("显示帧率上限",fps,404,254,172);
            AddAction("取消",ButtonKind.Subtle,DialogResult.Cancel);
            AddAction("应用",ButtonKind.Primary,DialogResult.OK);
            Set(current); preset.SelectedIndex=5;
            for(int i=0;i<5;i++)
            {
                PreviewOptions p=PreviewOptions.Preset(i);
                if(p.MaxHeight==current.MaxHeight&&p.BitRate==current.BitRate&&p.DisplayFps==current.DisplayFps&&p.PixelMode==current.PixelMode)
                { preset.SelectedIndex=i; break; }
            }
            preset.SelectedIndexChanged+=delegate { if(!setting&&preset.SelectedIndex<5) { PreviewOptions p=PreviewOptions.Preset(preset.SelectedIndex); p.Screens=screens.SelectedIndex; Set(p); } };
            mode.SelectedIndexChanged+=delegate { height.Enabled=rate.Enabled=mode.SelectedIndex==0; Custom(); };
            height.ValueChanged+=delegate { Custom(); }; rate.ValueChanged+=delegate { Custom(); }; fps.ValueChanged+=delegate { Custom(); };
            height.Enabled=rate.Enabled=!current.PixelMode;
        }
        private void Set(PreviewOptions p)
        {
            setting=true;
            try
            {
                height.Value=p.MaxHeight; rate.Value=(decimal)p.BitRate/1000000; fps.Value=p.DisplayFps; screens.SelectedIndex=p.Screens; mode.SelectedIndex=p.PixelMode?1:0;
                height.Enabled=rate.Enabled=!p.PixelMode;
            }
            finally { setting=false; }
        }
        private void Custom() { if(!setting) preset.SelectedIndex=5; }
    }
}
