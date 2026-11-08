using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal enum NativeArtworkScreen { Upper, Lower }
    internal enum NativeLogoResetMode { Original, Automatic }
    internal sealed class NativeLogoPlacement
    {
        public int Anchor { get; private set; }
        public double ScaleX { get; private set; }
        public double ScaleY { get; private set; }
        public NativeLogoPlacement(int anchor,double scaleX,double scaleY)
        {
            if(anchor<0||anchor>8||double.IsNaN(scaleX)||double.IsNaN(scaleY)||scaleX<0.2||scaleX>0.8||scaleY<0.2||scaleY>0.8)throw new ArgumentException("Invalid logo placement");
            Anchor=anchor;ScaleX=scaleX;ScaleY=scaleY;
        }
        public static int SnapAnchor(double x,double y)
        {
            if(double.IsNaN(x)||double.IsNaN(y)||x<0||x>1||y<0||y>1)throw new ArgumentException("Invalid pointer coordinates");
            return (y<0.395?0:y<0.635?1:2)*3+(x<0.375?0:x<0.625?1:2);
        }
        internal Dictionary<string,object> ToJson() {return NativeEditorClient.Map("anchor",Anchor,"scaleX",ScaleX,"scaleY",ScaleY);}
    }
    internal sealed partial class NativeEditorClient
    {
        public Task<NativeEditorResult> LogoPreviewAsync(string itemId,string heroCandidateId,string logoCandidateId,NativeArtworkScreen screen,
            NativeLogoPlacement placement,NativeArtworkLayout heroLayout,CancellationToken token)
        {return LogoPreviewAsync(itemId,heroCandidateId,logoCandidateId,screen,placement,heroLayout,null,false,token);}
        public Task<NativeEditorResult> LogoPreviewAsync(string itemId,string heroCandidateId,string logoCandidateId,NativeArtworkScreen screen,
            NativeLogoPlacement placement,NativeArtworkLayout heroLayout,NativeArtworkLayout logoLayout,bool useStaged,CancellationToken token)
        {return LogoPreviewCore(itemId,heroCandidateId,logoCandidateId,screen,placement,heroLayout,logoLayout,useStaged,null,null,token);}
        public Task<NativeEditorResult> LogoPreviewVariantsAsync(string itemId,string heroCandidateId,string logoCandidateId,NativeArtworkScreen screen,
            NativeLogoPlacement placement,string heroVariantToken,string logoVariantToken,CancellationToken token)
        {
            if(heroVariantToken==null&&logoVariantToken==null)throw new ArgumentException("A temporary variant is required");
            return LogoPreviewCore(itemId,heroCandidateId,logoCandidateId,screen,placement,null,null,false,heroVariantToken,logoVariantToken,token);
        }
        private async Task<NativeEditorResult> LogoPreviewCore(string itemId,string heroCandidateId,string logoCandidateId,NativeArtworkScreen screen,
            NativeLogoPlacement placement,NativeArtworkLayout heroLayout,NativeArtworkLayout logoLayout,bool useStaged,string heroVariantToken,string logoVariantToken,CancellationToken token)
        {
            if(placement==null||!Enum.IsDefined(typeof(NativeArtworkScreen),screen))throw new ArgumentException("Invalid logo preview");
            var target=screen==NativeArtworkScreen.Upper?NativeArtworkTarget.UpperHero:NativeArtworkTarget.LowerHero;
            var input=Map("itemId",Item(itemId),"heroCandidateId",Id(heroCandidateId),"logoCandidateId",Id(logoCandidateId),
                "screen",screen.ToString().ToLowerInvariant(),"placement",placement.ToJson(),"useStaged",useStaged);
            if(heroVariantToken!=null)input.Add("heroVariantToken",Id(heroVariantToken));
            if(logoVariantToken!=null)input.Add("logoVariantToken",Id(logoVariantToken));
            if(heroLayout!=null)input.Add("heroRecipe",heroLayout.ToJson(target));
            if(logoLayout!=null)
            {
                int width=(int)Math.Round(target.Width*Math.Max(.22,Math.Min(.72,placement.ScaleX)),MidpointRounding.AwayFromZero);
                int height=(int)Math.Round(target.Height*Math.Max(.18,Math.Min(.58,placement.ScaleY)),MidpointRounding.AwayFromZero);
                input.Add("logoRecipe",logoLayout.ToJson(new NativeArtworkTarget("editor-logo-box",ArtworkSlot.Logo,width,height)));
            }
            var result=await Invoke("logoPreview",input,token).ConfigureAwait(false);
            var data=result.Data;
            if(Text(data,"heroCandidateId")!=heroCandidateId||Text(data,"logoCandidateId")!=logoCandidateId||Integer(data,"width",1,1920)!=target.Width
                || Integer(Json.Get(data,"placement"),"anchor",0,8)!=placement.Anchor
                || LogoNumber(Json.Get(data,"placement"),"scaleX")!=placement.ScaleX||LogoNumber(Json.Get(data,"placement"),"scaleY")!=placement.ScaleY)throw Invalid();
            return result;
        }
        public async Task<NativeEditorResult> LogoResetPreviewAsync(string itemId,NativeLogoResetMode mode,CancellationToken token)
        {
            if(!Enum.IsDefined(typeof(NativeLogoResetMode),mode))throw new ArgumentException("Invalid reset mode");
            var result=await Invoke("logoResetPreview",Map("itemId",Item(itemId),"mode",mode.ToString().ToLowerInvariant()),token).ConfigureAwait(false);
            foreach(var pair in (Dictionary<string,object>)result.Data["changes"])
            {
                if(mode==NativeLogoResetMode.Automatic)
                {object after=Json.Get(pair.Value,"after");if(pair.Key!="logoPlacementOverridden"||!(after is bool)||(bool)after)throw Invalid();}
                else if(Array.IndexOf(new[]{"logoAnchor","logoScaleX","logoScaleY","logoPlacementOverridden"},pair.Key)<0)throw Invalid();
            }
            return result;
        }
        private static double LogoNumber(object value,string key)
        {object raw=Json.Get(value,key);if(!(raw is double))throw Invalid();double n=(double)raw;if(double.IsNaN(n)||n<0.2||n>0.8)throw Invalid();return n;}
        private static void ValidateLogoPreview(Dictionary<string,object> data)
        {
            Obj(data,"itemId","heroCandidateId","logoCandidateId","blobSha256","width","height","compatibility","placement","metadata","pendingChanged");
            if(data.Count!=10||Flag(data,"pendingChanged")||Text(data,"compatibility")!="EDITOR_APPROXIMATION")throw Invalid();
            Item(Text(data,"itemId"));Id(Text(data,"heroCandidateId"));Id(Text(data,"logoCandidateId"));Hash(data,"blobSha256");
            long width=Integer(data,"width",1,1920);if((width!=1920&&width!=1240)||Integer(data,"height",1,1080)!=1080)throw Invalid();
            var placement=Obj(Json.Get(data,"placement"),"anchor","scaleX","scaleY");if(placement.Count!=3)throw Invalid();
            long anchor=Integer(placement,"anchor",0,8);double sx=LogoNumber(placement,"scaleX"),sy=LogoNumber(placement,"scaleY");
            object metadata=Json.Get(data,"metadata");ValidateData("metadataPreview",metadata);if(Text(metadata,"itemId")!=Text(data,"itemId"))throw Invalid();
            var changes=(Dictionary<string,object>)Json.Get(metadata,"changes");
            foreach(var pair in changes)
            {
                object after=Json.Get(pair.Value,"after");
                if(pair.Key=="logoAnchor") {if(!(after is double)||(double)after!=anchor)throw Invalid();}
                else if(pair.Key=="logoScaleX"||pair.Key=="logoScaleY") {if(!(after is double)||(double)after!=(pair.Key=="logoScaleX"?sx:sy))throw Invalid();}
                else if(pair.Key=="logoPlacementOverridden") {if(!(after is bool)||!(bool)after)throw Invalid();}
                else throw Invalid();
            }
        }
    }
}
