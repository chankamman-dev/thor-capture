using System;
using System.Collections.Generic;
namespace ThorCapture
{
    internal static class NativeOriginalsTests
    {
        private static Dictionary<string,object> Value()
        {return (Dictionary<string,object>)NativeEditorJson.Parse(NativeEditorJson.Encode(NativeEditorClient.Map("itemId","legacy-item:1","slot","icon","candidateId","candidate","items",new[]{NativeEditorClient.Map("sha256",new string('a',64),"mime","image/jpeg","byteSize",100,"width",20,"height",10)},"pendingChanged",false)));}
        private static Dictionary<string,object> Row(Dictionary<string,object> value){return (Dictionary<string,object>)((List<object>)value["items"])[0];}
        private static void Reject(Action<Dictionary<string,object>> mutate,string label)
        {var value=Value();mutate(value);bool rejected=false;try{NativeEditorClient.ValidateData("originals",value);}catch(NativeEditorException error){rejected=error.Code=="EDITOR_RESPONSE_INVALID";}Tests.Check(rejected,"original image descriptor rejects "+label);}
        public static void Run()
        {
            NativeEditorClient.ValidateData("originals",Value());Tests.Check(true,"original image descriptor accepts bounded original JPEG identity");
            Reject(v=>Row(v)["sha256"]="invalid","invalid digest");Reject(v=>Row(v)["mime"]="image/svg+xml","unsupported content type");
            Reject(v=>Row(v)["byteSize"]=0d,"empty byte count");Reject(v=>{Row(v)["width"]=8192d;Row(v)["height"]=8192d;},"excessive pixel count combined with height");
            Reject(v=>Row(v)["width"]=1.5d,"fractional dimensions");Reject(v=>v["pendingChanged"]=true,"automatic draft change");
            Reject(v=>Row(v)["path"]="private","unexpected private fields");Reject(v=>((List<object>)v["items"]).Add(Row(v)),"duplicate original identity");
        }
    }
}
