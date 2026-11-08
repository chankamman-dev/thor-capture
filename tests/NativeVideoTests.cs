using System;
using System.Collections.Generic;
namespace ThorCapture
{
    internal static class NativeVideoTests
    {
        private static Dictionary<string,object> Value()
        {
            var video=NativeEditorClient.Map("id",new string('a',64),"sourceBlobSha256",new string('a',64),"mime","video/mp4","byteSize",1024,"width",32,"height",16,"durationMs",1000,"codec","h264","posterBlobSha256",new string('b',64),"posterAtMs",0,"deviceCompatibility","UNVERIFIED");
            return (Dictionary<string,object>)NativeEditorJson.Parse(NativeEditorJson.Encode(NativeEditorClient.Map("itemId","legacy-item:1","video",video,"pendingChanged",false)));
        }
        private static void Reject(Action<Dictionary<string,object>> mutation,string label)
        {var value=Value();mutation((Dictionary<string,object>)value["video"]);bool rejected=false;try{NativeEditorClient.ValidateData("videoPoster",value);}catch(NativeEditorException){rejected=true;}Tests.Check(rejected,"video descriptor rejects "+label);}
        public static void Run()
        {
            NativeEditorClient.ValidateData("videoPoster",Value());Tests.Check(true,"video descriptor accepts a bounded decoded video and unverified device compatibility");
            Reject(v=>v["mime"]="image/png","image as video");Reject(v=>v["sourceBlobSha256"]=new string('c',64),"different source identity");
            Reject(v=>v["deviceCompatibility"]="SUPPORTED_WRITE","unverified write promotion");Reject(v=>v["posterAtMs"]=1000d,"poster outside duration");
            Reject(v=>v["durationMs"]=0d,"empty duration");Reject(v=>v["codec"]="private/path","unbounded codec text");
            Reject(v=>{v["width"]=8192d;v["height"]=8192d;},"decoded pixel overflow");Reject(v=>v["path"]="secret","unexpected local path");
        }
    }
}
