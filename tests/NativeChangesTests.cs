using System;
using System.Collections.Generic;
namespace ThorCapture
{
    internal static class NativeChangesTests
    {
        private const string Library="22222222-2222-4222-8222-222222222222",Instance="11111111-1111-4111-8111-111111111111",Review="33333333-3333-4333-8333-333333333333";
        private static Dictionary<string,object> Value()
        {
            string hash=new string('a',64),id="changeset:"+hash;
            object change=NativeEditorClient.Map("id","native-123","kind","metadata","libraryItemId","legacy-item:1","nativeGameId",1,"deliveryClass","database","before",NativeEditorClient.Map("title","Before","isFavorite",0),"after",NativeEditorClient.Map("title","After","isFavorite",1));
            object cs=NativeEditorClient.Map("protocol","cocoon-art.changeset","version",1,"id",id,"createdAt",1,"deviceScope",null,"sourceFingerprint",hash,"intentHash",hash,"status","READY","reversible",true,"changes",new[]{change},"warnings",new object[0],"blockers",new object[0]);
            object plan=NativeEditorClient.Map("protocol","cocoon-art.device-plan","version",1,"id","device-plan:"+hash,"planHash",hash,"changeSetId",id,"changeSetIntentHash",hash,"executionAllowed",false,"blockers",new[]{NativeEditorClient.Map("code","M2_PLAN_ONLY")});
            return (Dictionary<string,object>)NativeEditorJson.Parse(NativeEditorJson.Encode(NativeEditorClient.Map("contract","cocoon.native-changes/1","libraryId",Library,"instanceId",Instance,"reviewId",Review,"revision",1,"expiresAt",10,"changeSet",cs,"devicePlan",plan,
                "summary",NativeEditorClient.Map("requested",1,"changes",1,"metadata",1,"artwork",0,"warnings",0,"blockers",1),"requiresConfirmation",true,"draftChanged",false,"deviceWritesEnabled",false)));
        }
        private static Dictionary<string,object> Change(Dictionary<string,object> value)
        {return (Dictionary<string,object>)((List<object>)((Dictionary<string,object>)value["changeSet"])["changes"])[0];}
        private static void Reject(Action<Dictionary<string,object>> mutate,string name)
        {
            var value=Value();mutate(value);bool rejected=false;try{NativeEditorClient.ParseChangeReview(value,Library,Instance,Review,true);}catch(NativeEditorException error){rejected=error.Code=="EDITOR_RESPONSE_INVALID";}
            Tests.Check(rejected,"native review rejects "+name);
        }
        public static void Run()
        {
            var review=NativeEditorClient.ParseChangeReview(Value(),Library,Instance,Review,true);
            var copy=review.Data;((List<object>)copy["changes"]).Clear();
            Tests.Check(review.IsPreview&&review.ChangeCount==1&&((List<object>)review.Data["changes"]).Count==1,"native review DTO keeps a detached validated field projection");
            Reject(v=>v["libraryId"]=Instance,"foreign library");Reject(v=>v["instanceId"]=Library,"foreign backend instance");Reject(v=>v["reviewId"]=Instance,"different requested review");
            Reject(v=>v["deviceWritesEnabled"]=true,"enabled device writes");Reject(v=>v["draftChanged"]=true,"implicit draft modification");Reject(v=>v["requiresConfirmation"]=false,"missing approval requirement");
            Reject(v=>((Dictionary<string,object>)v["devicePlan"])["executionAllowed"]=true,"executable device plan");
            Reject(v=>((Dictionary<string,object>)v["devicePlan"])["changeSetId"]="changeset:"+new string('b',64),"plan referencing another intent");
            Reject(v=>((Dictionary<string,object>)v["changeSet"])["intentHash"]=new string('b',64),"ChangeSet ID hash mismatch");
            Reject(v=>((Dictionary<string,object>)v["summary"])["changes"]=2d,"summary inconsistent with reviewed fields");
            Reject(v=>Change(v)["libraryItemId"]="legacy-item:2","item identity inconsistent with native ID");
            Reject(v=>((Dictionary<string,object>)Change(v)["after"])["title"]=new object[0],"non-text title");
            Reject(v=>((Dictionary<string,object>)Change(v)["after"])["isFavorite"]=2d,"invalid normalized boolean");
            Reject(v=>((Dictionary<string,object>)Change(v)["after"]).Remove("title"),"missing paired before/after field");
            Reject(v=>((Dictionary<string,object>)Change(v)["after"])["unexpected"]="private","unknown metadata field");
            Reject(v=>v["extra"]="private","unknown response envelope field");
        }
    }
}
