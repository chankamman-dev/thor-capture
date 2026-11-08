using System;
namespace ThorCapture
{
    internal static class NativeLayoutTests
    {
        private static void Reject(Action action,string label)
        {bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}Tests.Check(rejected,"layout rejects "+label);}
        public static void Run()
        {
            var target=new NativeArtworkTarget("fixture",ArtworkSlot.Logo,200,100);
            var layout=new NativeArtworkLayout();var json=layout.ToJson(target);
            Tests.Check(!json.ContainsKey("crop")&&(string)json["fit"]=="contain","default layout contains full original source with transparent canvas");
            layout.CropWidth=20;layout.CropHeight=10;layout.FocusX=1;layout.PaddingLeft=5;
            Tests.Check(layout.ToJson(target).ContainsKey("crop"),"layout serializes explicit crop and alignment");
            Reject(()=>new NativeArtworkTarget("../private",ArtworkSlot.Hero,2,2),"path profile");
            Reject(()=>new NativeArtworkTarget("large",ArtworkSlot.Hero,8192,8192),"pixel overflow");
            Reject(()=>new NativeArtworkLayout{FocusX=double.NaN}.ToJson(target),"nonfinite focus");
            Reject(()=>new NativeArtworkLayout{FocusY=double.PositiveInfinity}.ToJson(target),"infinite focus");
            Reject(()=>new NativeArtworkLayout{PaddingLeft=100,PaddingRight=100}.ToJson(target),"empty inner canvas");
            Reject(()=>new NativeArtworkLayout{PaddingTop=-1}.ToJson(target),"negative padding");
            Reject(()=>new NativeArtworkLayout{CropWidth=1}.ToJson(target),"partial zero crop");
            Reject(()=>new NativeArtworkLayout{CropX=1}.ToJson(target),"offset without crop");
            Reject(()=>new NativeArtworkLayout{Fit=(NativeLayoutFit)99}.ToJson(target),"unknown fit");
            Tests.Check(NativeArtworkTarget.UpperHero.Width==1920&&NativeArtworkTarget.LowerHero.Width==1240&&NativeArtworkTarget.LowerHero.Height==1080,"Thor upper and lower canvas presets retain physical dimensions");
        }
    }
}
