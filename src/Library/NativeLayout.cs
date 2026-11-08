using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal enum NativeLayoutFit { Contain, Cover, Stretch }

    // Canvas presets describe pixels only; they do not attest Cocoon masks or write compatibility.
    internal sealed class NativeArtworkTarget
    {
        public string Id { get; private set; }
        public ArtworkSlot Slot { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public NativeArtworkTarget(string id,ArtworkSlot slot,int width,int height)
        {
            if(!System.Text.RegularExpressions.Regex.IsMatch(id??"",@"\A[A-Za-z0-9_-]{1,128}\z") || !Enum.IsDefined(typeof(ArtworkSlot),slot)
                || width<1 || height<1 || width>8192 || height>8192 || (long)width*height>32L*1024*1024) throw new ArgumentException("Invalid artwork target");
            Id=id;Slot=slot;Width=width;Height=height;
        }
        public static NativeArtworkTarget UpperHero { get { return new NativeArtworkTarget("thor-upper-canvas-v1",ArtworkSlot.Hero,1920,1080); } }
        public static NativeArtworkTarget LowerHero { get { return new NativeArtworkTarget("thor-lower-canvas-v1",ArtworkSlot.Hero,1240,1080); } }
        internal Dictionary<string,object> ToJson()
        { return NativeEditorClient.Map("id",Id,"slot",Slot.ToString().ToLowerInvariant(),"width",Width,"height",Height,"format","png"); }
    }

    internal sealed class NativeArtworkLayout
    {
        public NativeLayoutFit Fit { get; set; }
        public double FocusX { get; set; }
        public double FocusY { get; set; }
        public int CropX { get; set; }
        public int CropY { get; set; }
        // Zero width and height together use the full source; other crops must be positive.
        public int CropWidth { get; set; }
        public int CropHeight { get; set; }
        public int PaddingTop { get; set; }
        public int PaddingRight { get; set; }
        public int PaddingBottom { get; set; }
        public int PaddingLeft { get; set; }
        public System.Drawing.Color Background { get; set; }
        public NativeArtworkLayout() { Fit=NativeLayoutFit.Contain;FocusX=0.5;FocusY=0.5;Background=System.Drawing.Color.FromArgb(0,0,0,0); }
        internal Dictionary<string,object> ToJson(NativeArtworkTarget target)
        {
            if(target==null || !Enum.IsDefined(typeof(NativeLayoutFit),Fit) || double.IsNaN(FocusX) || double.IsNaN(FocusY)
                || FocusX<0 || FocusX>1 || FocusY<0 || FocusY>1 || CropX<0 || CropY<0 || CropX>8191 || CropY>8191
                || CropWidth<0 || CropHeight<0 || CropWidth>8192 || CropHeight>8192
                || ((CropWidth==0 || CropHeight==0) && (CropX!=0 || CropY!=0 || CropWidth!=0 || CropHeight!=0))) throw new ArgumentException("Invalid artwork layout");
            foreach(int value in new[]{PaddingTop,PaddingRight,PaddingBottom,PaddingLeft})if(value<0||value>8191)throw new ArgumentException("Invalid artwork padding");
            if(PaddingLeft+PaddingRight>=target.Width || PaddingTop+PaddingBottom>=target.Height)throw new ArgumentException("Empty artwork canvas");
            var result=NativeEditorClient.Map("fit",Fit.ToString().ToLowerInvariant(),"focus",NativeEditorClient.Map("x",FocusX,"y",FocusY),
                "padding",NativeEditorClient.Map("top",PaddingTop,"right",PaddingRight,"bottom",PaddingBottom,"left",PaddingLeft),
                "background",new[]{(int)Background.R,(int)Background.G,(int)Background.B,(int)Background.A});
            if(CropWidth>0)result.Add("crop",NativeEditorClient.Map("x",CropX,"y",CropY,"width",CropWidth,"height",CropHeight));
            return result;
        }
    }
    internal sealed partial class NativeEditorClient
    {
        public Task<NativeEditorResult> LayoutAsync(string itemId,string candidateId,NativeArtworkTarget target,NativeArtworkLayout layout,CancellationToken token)
        {
            if(target==null || layout==null)throw new ArgumentNullException("layout");
            return Invoke("layout",Map("itemId",Item(itemId),"slot",Slot(target.Slot),"candidateId",Id(candidateId),
                "recipe",layout.ToJson(target),"targetProfile",target.ToJson()),token);
        }
    }
}
