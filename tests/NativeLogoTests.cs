using System;
using System.Collections.Generic;
namespace ThorCapture
{
    internal static class NativeLogoTests
    {
        private static Dictionary<string,object> Value()
        {
            var metadata=NativeEditorClient.Map("itemId","legacy-item:1","revision",0,"previewToken","token","recommendationOnly",true,
                "changes",NativeEditorClient.Map("logoAnchor",NativeEditorClient.Map("before",null,"after",4)));
            return (Dictionary<string,object>)NativeEditorJson.Parse(NativeEditorJson.Encode(NativeEditorClient.Map("itemId","legacy-item:1","heroCandidateId","hero","logoCandidateId","logo",
                "blobSha256",new string('a',64),"width",1920,"height",1080,"compatibility","EDITOR_APPROXIMATION",
                "placement",new NativeLogoPlacement(4,.5,.5).ToJson(),"metadata",metadata,"pendingChanged",false)));
        }
        private static void Reject(Action<Dictionary<string,object>> mutate,string label)
        {var v=Value();mutate(v);bool rejected=false;try{NativeEditorClient.ValidateData("logoPreview",v);}catch(NativeEditorException){rejected=true;}Tests.Check(rejected,"logo preview rejects "+label);}
        public static void Run()
        {
            NativeEditorClient.ValidateData("logoPreview",Value());Tests.Check(true,"logo preview accepts bounded editor approximation with metadata review");
            bool snap=true;for(int i=0;i<9;i++)snap&=NativeLogoPlacement.SnapAnchor(new[]{.25,.5,.75}[i%3],new[]{.27,.52,.75}[i/3])==i;
            Tests.Check(snap&&NativeLogoPlacement.SnapAnchor(.375,.395)==4,"logo drag snapping matches all nine editor anchors and boundary");
            Tests.Reject(()=>new NativeLogoPlacement(9,.5,.5),"logo placement rejects invalid anchor");
            Tests.Reject(()=>new NativeLogoPlacement(4,double.NaN,.5),"logo placement rejects nonfinite scale");
            Tests.Reject(()=>NativeLogoPlacement.SnapAnchor(-.1,.5),"logo drag rejects outside canvas pointer");
            Reject(v=>v["compatibility"]="SUPPORTED_WRITE","unverified device compatibility");
            Reject(v=>v["pendingChanged"]=true,"automatic persistence");
            Reject(v=>v["width"]=100d,"unknown canvas width");
            Reject(v=>((Dictionary<string,object>)v["placement"])["anchor"]=9d,"anchor overflow");
            Reject(v=>((Dictionary<string,object>)v["metadata"])["itemId"]="legacy-item:2","different metadata item");
            Reject(v=>{var m=(Dictionary<string,object>)v["metadata"];var c=(Dictionary<string,object>)m["changes"];((Dictionary<string,object>)c["logoAnchor"])["after"]=5d;},"confirmation conflicting with rendered placement");
            Reject(v=>{var m=(Dictionary<string,object>)v["metadata"];((Dictionary<string,object>)m["changes"])["title"]=NativeEditorClient.Map("before","old","after","new");},"unrelated metadata edit");
        }
    }
}
