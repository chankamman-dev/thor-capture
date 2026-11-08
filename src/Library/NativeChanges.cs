using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal sealed partial class NativeEditorHttpTransport
    {
        internal Task<NativeEditorHttpReply> ChangesAsync(string body,CancellationToken token)
        {
            byte[] bytes=Encoding.UTF8.GetBytes(body);if(bytes.Length>16384)throw new ArgumentException("Review request too large");
            return SendAsync("/api/native-editor/changes",false,new MemoryStream(bytes,false),bytes.Length,true,false,token);
        }
    }
    internal sealed class NativeChangeReview
    {
        public readonly string ReviewId,LibraryId,InstanceId,ChangeSetId,State;
        public readonly long Revision;
        public readonly bool IsPreview;
        public readonly int ChangeCount;
        private readonly string json;
        internal NativeChangeReview(string id,string library,string instance,string changeSet,string state,long revision,bool preview,int count,object projection)
        {ReviewId=id;LibraryId=library;InstanceId=instance;ChangeSetId=changeSet;State=state;Revision=revision;IsPreview=preview;ChangeCount=count;json=NativeEditorJson.Encode(projection);}
        public Dictionary<string,object> Data {get{return (Dictionary<string,object>)NativeEditorJson.Parse(json);}}
    }
    internal sealed class NativeChangeReviewHandle
    {
        public readonly string ReviewId,ChangeSetId,LibraryId,InstanceId;
        public readonly long Revision;
        internal NativeChangeReviewHandle(string id,string cs,string library,string instance,long revision)
        {ReviewId=id;ChangeSetId=cs;LibraryId=library;InstanceId=instance;Revision=revision;}
    }
    internal sealed partial class NativeEditorClient
    {
        public async Task<NativeChangeReview> PreviewChangesAsync(long revision,CancellationToken token)
        {
            NativeEditorInfo snapshot;Uri address;Snapshot(out snapshot,out address);
            object value=await ChangesRequest(snapshot,address,"preview",Map("revision",Revision(revision)),token).ConfigureAwait(false);
            NativeChangeReview result=ParseChangeReview(value,snapshot.LibraryId,snapshot.InstanceId,null,true);
            if(result.Revision!=revision)throw Invalid();return result;
        }
        public Task<NativeChangeReview> ConfirmChangesAsync(NativeChangeReview plan,bool confirm,CancellationToken token)
        {if(plan==null)throw new ArgumentNullException("plan");if(!plan.IsPreview)throw new ArgumentException("Expected review plan");return ChangeAction("confirm",plan.ReviewId,plan.LibraryId,plan.InstanceId,plan.ChangeSetId,confirm,token);}
        public Task<NativeChangeReview> RevokeChangesAsync(NativeChangeReview record,bool confirm,CancellationToken token)
        {if(record==null)throw new ArgumentNullException("record");if(record.IsPreview)throw new ArgumentException("Expected persisted record");return ChangeAction("revoke",record.ReviewId,record.LibraryId,record.InstanceId,record.ChangeSetId,confirm,token);}
        public Task<NativeChangeReview> ChangeStatusAsync(NativeChangeReviewHandle handle,CancellationToken token)
        {if(handle==null)throw new ArgumentNullException("handle");return ChangeAction("status",handle.ReviewId,handle.LibraryId,handle.InstanceId,handle.ChangeSetId,true,token);}
        public Task<NativeChangeReview> ChangeStatusAsync(NativeChangeReview record,CancellationToken token)
        {if(record==null)throw new ArgumentNullException("record");return ChangeAction("status",record.ReviewId,record.LibraryId,record.InstanceId,record.ChangeSetId,true,token);}
        private async Task<NativeChangeReview> ChangeAction(string operation,string id,string library,string instance,string cs,bool confirm,CancellationToken token)
        {
            if(!confirm)throw new NativeEditorException("EDITOR_CONFIRM_REQUIRED",400);
            NativeEditorInfo snapshot;Uri address;Snapshot(out snapshot,out address);
            if(snapshot.LibraryId!=library||snapshot.InstanceId!=instance)throw new NativeEditorException("EDITOR_SESSION_STALE",409);
            Dictionary<string,object> input=Map("reviewId",id);if(operation!="status")input.Add("confirm",true);
            object value=await ChangesRequest(snapshot,address,operation,input,token).ConfigureAwait(false);
            NativeChangeReview result=ParseChangeReview(value,library,instance,id,false);
            if(result.ChangeSetId!=cs)throw Invalid();return result;
        }
        public async Task<IList<NativeChangeReviewHandle>> ChangeReviewsAsync(CancellationToken token)
        {
            NativeEditorInfo snapshot;Uri address;Snapshot(out snapshot,out address);
            object raw=await ChangesRequest(snapshot,address,"list",Map(),token).ConfigureAwait(false);
            var value=Obj(raw,"contract","libraryId","instanceId","items","deviceWritesEnabled");ChangeBinding(value,snapshot.LibraryId,snapshot.InstanceId);
            var items=Json.Get(value,"items") as List<object>;if(value.Count!=5||items==null||items.Count>50)throw Invalid();
            var output=new List<NativeChangeReviewHandle>();var ids=new HashSet<string>();
            foreach(object row in items)
            {
                Obj(row,"reviewId","changeSetId","revision","createdAt");string id=Uuid(row,"reviewId"),cs=ChangeId(row,"changeSetId","changeset:");
                if(!ids.Add(id))throw Invalid();Integer(row,"createdAt",0,9007199254740991L);
                output.Add(new NativeChangeReviewHandle(id,cs,snapshot.LibraryId,snapshot.InstanceId,Integer(row,"revision",0,9007199254740991L)));
            }
            return output.AsReadOnly();
        }
        private async Task<object> ChangesRequest(NativeEditorInfo snapshot,Uri address,string operation,object input,CancellationToken token)
        {
            NativeEditorHttpTransport transport=Capture(address);await VerifyContext(transport,snapshot,address,token).ConfigureAwait(false);
            NativeEditorHttpReply reply=await transport.ChangesAsync(NativeEditorJson.Encode(Map("instanceId",snapshot.InstanceId,"libraryId",snapshot.LibraryId,"operation",operation,"input",input)),token).ConfigureAwait(false);
            CheckContext(snapshot,address);await VerifyContext(transport,snapshot,address,token).ConfigureAwait(false);
            if(reply.Status!=200)
            {
                string code=Json.Get(reply.Value,"code") as string;
                if(reply.Status<400||Array.IndexOf(new[]{"EDITOR_PARAMETERS_INVALID","EDITOR_SESSION_STALE","EDITOR_OPERATION_INVALID","EDITOR_RENDER_BUSY","EDITOR_CANCELLED",
                    "EDITOR_LIBRARY_CHANGED","EDITOR_REVIEW_BUSY","EDITOR_MIGRATION_REQUIRED","EDITOR_REVIEW_LIMIT","EDITOR_REVIEW_FAILED","EDITOR_REVISION_CONFLICT",
                    "EDITOR_REVIEW_EMPTY","EDITOR_ITEM_NOT_FOUND","EDITOR_DESTINATION_INVALID","EDITOR_REVIEW_STALE","EDITOR_REVISION_INVALID","EDITOR_CONFIRM_REQUIRED",
                    "EDITOR_REVIEW_EXPIRED","EDITOR_REVIEW_NOT_FOUND","EDITOR_CANDIDATE_NOT_FOUND","EDITOR_ASSET_INVALID","EDITOR_VARIANT_STALE","EDITOR_UNAVAILABLE"},code)<0)throw Invalid(reply.Status);
                throw new NativeEditorException(code,reply.Status);
            }
            return reply.Value;
        }
        private static void ChangeBinding(object value,string library,string instance)
        {if(Text(value,"contract")!="cocoon.native-changes/1"||Uuid(value,"libraryId")!=library||Uuid(value,"instanceId")!=instance||Flag(value,"deviceWritesEnabled"))throw Invalid();}
        private static string ChangeHash(object value,string key) { Hash(value,key);return Text(value,key); }
        private static string ChangeId(object value,string key,string prefix)
        {string id=Text(value,key);if(!Regex.IsMatch(id,"\\A"+Regex.Escape(prefix)+"[a-f0-9]{64}\\z"))throw Invalid();return id;}
        internal static NativeChangeReview ParseChangeReview(object raw,string library,string instance,string requested,bool preview)
        {
            var value=preview?Obj(raw,"contract","libraryId","instanceId","reviewId","revision","expiresAt","changeSet","devicePlan","summary","requiresConfirmation","draftChanged","deviceWritesEnabled"):
                Obj(raw,"contract","libraryId","instanceId","reviewId","revision","changeSet","devicePlan","lifecycle","operation","draftChanged","deviceWritesEnabled");
            if(value.Count!=(preview?12:11)||Flag(value,"draftChanged"))throw Invalid();ChangeBinding(value,library,instance);
            string id=Uuid(value,"reviewId");if(requested!=null&&id!=requested)throw Invalid();long revision=Integer(value,"revision",0,9007199254740991L);
            var cs=Obj(Json.Get(value,"changeSet"),"protocol","version","id","createdAt","deviceScope","sourceFingerprint","intentHash","status","reversible","changes","warnings","blockers");
            if(cs.Count!=12||Text(cs,"protocol")!="cocoon-art.changeset"||Integer(cs,"version",1,1)!=1||!Flag(cs,"reversible")||Json.Get(cs,"deviceScope")!=null)throw Invalid();
            string csId=ChangeId(cs,"id","changeset:"),intent=ChangeHash(cs,"intentHash");Hash(cs,"sourceFingerprint");Integer(cs,"createdAt",0,9007199254740991L);
            string state=Text(cs,"status");if(csId!="changeset:"+intent||(state!="READY"&&state!="DRAFT"))throw Invalid();
            var changes=Json.Get(cs,"changes") as List<object>;if(changes==null||changes.Count<1||changes.Count>100)throw Invalid();
            var seen=new HashSet<string>();int metadata=0;
            foreach(object row in changes)
            {
                string kind=Text(row,"kind");bool meta=kind=="metadata";
                var change=meta?Obj(row,"id","kind","libraryItemId","nativeGameId","deliveryClass","before","after"):
                    Obj(row,"id","kind","libraryItemId","nativeGameId","deliveryClass","slot","action","before","after");
                long game=Integer(change,"nativeGameId",1,999999999999999L);string item=Item(Text(change,"libraryItemId"));
                if(!seen.Add(Text(change,"id"))||!Regex.IsMatch(Text(change,"id"),@"\Anative-[A-Za-z0-9_-]{1,128}\z")||
                    !item.EndsWith(":"+game.ToString(System.Globalization.CultureInfo.InvariantCulture),StringComparison.Ordinal)||Text(change,"deliveryClass")!="database")throw Invalid();
                if(meta)
                {
                    metadata++;var before=Json.Get(change,"before") as Dictionary<string,object>;var after=Json.Get(change,"after") as Dictionary<string,object>;
                    if(before==null||after==null||before.Count<1||before.Count!=after.Count||before.Count>FieldTypes.Count)throw Invalid();
                    foreach(string field in before.Keys)
                    {
                        if(!FieldTypes.ContainsKey(field)||!after.ContainsKey(field))throw Invalid();
                        foreach(object part in new[]{before[field],after[field]})
                        {
                            if(part==null)continue;string type=FieldTypes[field];
                            if(type=="text"||type=="url"){string text=part as string;if(text==null||text.Length>(field=="summary"?16384:2048)||Regex.IsMatch(text,@"[\x00-\x08\x0b\x0c\x0e-\x1f]"))throw Invalid();}
                            else {double number;if(!(part is double)||double.IsNaN(number=(double)part)||double.IsInfinity(number)||(type!="number"&&Math.Truncate(number)!=number)||(type=="bool"&&number!=0&&number!=1))throw Invalid();}
                        }
                    }
                }
                else
                {
                    if(kind!="artwork"||Array.IndexOf(new[]{"icon","logo","hero"},Text(change,"slot"))<0||Text(change,"action")!="set")throw Invalid();
                    object old=Json.Get(change,"before");if(old!=null){var baseline=Obj(old,"uri","source");foreach(string key in new[]{"uri","source"})if(Json.Get(baseline,key)!=null&&!(Json.Get(baseline,key) is string))throw Invalid();}
                    var asset=Obj(Json.Get(change,"after"),"sha256","mime","byteSize","width","height","stagedRef","destination");string hash=ChangeHash(asset,"sha256");
                    string mime=Text(asset,"mime"),ext=mime=="image/png"?"png":mime=="video/mp4"?"mp4":mime=="video/webm"?"webm":null;
                    if(asset.Count!=7||ext==null||(ext!="png"&&Text(change,"slot")!="hero")||Text(asset,"stagedRef")!="native-assets/sha256/"+hash.Substring(0,2)+"/"+hash+"."+ext)throw Invalid();
                    Integer(asset,"byteSize",1,32*1024*1024);Integer(asset,"width",1,8192);Integer(asset,"height",1,8192);
                    var dest=Obj(Json.Get(asset,"destination"),"platform","fileName");foreach(string key in new[]{"platform","fileName"})if(!Regex.IsMatch(Text(dest,key),@"\A[^\\/\x00-\x1f:]{1,200}\z")||Text(dest,key).Contains(".."))throw Invalid();
                }
            }
            var warnings=Json.Get(cs,"warnings") as List<object>;if(warnings==null||warnings.Count>100)throw Invalid();
            foreach(object warning in warnings){Obj(warning,"code","changeId");if(Text(warning,"code")!="NATIVE_METADATA_NOOP"||!Regex.IsMatch(Text(warning,"changeId"),@"\Anative-[A-Za-z0-9_-]{1,128}\z"))throw Invalid();}
            var plan=Obj(Json.Get(value,"devicePlan"),"protocol","version","id","createdAt","planHash","changeSetId","changeSetIntentHash","deviceScope","profile","preflight","actions","verification","recovery","blockers","executionAllowed");
            if(Text(plan,"protocol")!="cocoon-art.device-plan"||Integer(plan,"version",1,1)!=1||Flag(plan,"executionAllowed")||Text(plan,"changeSetId")!=csId||ChangeHash(plan,"changeSetIntentHash")!=intent||ChangeId(plan,"id","device-plan:")!="device-plan:"+ChangeHash(plan,"planHash"))throw Invalid();
            var blockers=Json.Get(plan,"blockers") as List<object>;if(blockers==null||blockers.Count<1||blockers.Count>2500)throw Invalid();var codes=new List<string>();
            foreach(object blocker in blockers){Obj(blocker,"code","changeId","field","slot");string code=Text(blocker,"code");if(Array.IndexOf(new[]{"M2_PLAN_ONLY","VIDEO_PROFILE_UNVERIFIED","LIBRARY_ITEM_UNRESOLVED","PROFILE_METADATA_UNSUPPORTED","PROFILE_ARTWORK_UNSUPPORTED","ADAPTER_READ_ONLY","UNKNOWN_PROFILE_READ_ONLY","SCHEMA_NOT_INSPECTED"},code)<0)throw Invalid();codes.Add(code);}
            if(!codes.Contains("M2_PLAN_ONLY"))throw Invalid();
            foreach(var change in changes)if(Text(change,"kind")=="artwork"&&Text(Json.Get(change,"after"),"mime").StartsWith("video/",StringComparison.Ordinal)
                &&!blockers.Exists(b=>Text(b,"code")=="VIDEO_PROFILE_UNVERIFIED"&&Text(b,"changeId")==Text(change,"id")))throw Invalid();
            if(preview)
            {
                Integer(value,"expiresAt",1,9007199254740991L);if(!Flag(value,"requiresConfirmation"))throw Invalid();
                var summary=Obj(Json.Get(value,"summary"),"requested","changes","metadata","artwork","warnings","blockers");
                Integer(summary,"requested",changes.Count+warnings.Count,changes.Count+warnings.Count);Integer(summary,"changes",changes.Count,changes.Count);Integer(summary,"metadata",metadata,metadata);Integer(summary,"artwork",changes.Count-metadata,changes.Count-metadata);Integer(summary,"warnings",warnings.Count,warnings.Count);Integer(summary,"blockers",blockers.Count,blockers.Count);
            }
            else
            {
                var lifecycle=Obj(Json.Get(value,"lifecycle"),"status","updatedAt","revokedAt");state=Text(lifecycle,"status");if(Array.IndexOf(new[]{"READY","DRAFT","REVOKED"},state)<0)throw Invalid();Integer(lifecycle,"updatedAt",0,9007199254740991L);
                object operation=Json.Get(value,"operation");if(Text(operation,"changeSetId")!=csId||Array.IndexOf(new[]{"WAITING_DEVICE","BLOCKED","CANCELED"},Text(operation,"status"))<0||Flag(Json.Get(operation,"summary"),"executionAllowed"))throw Invalid();
            }
            return new NativeChangeReview(id,library,instance,csId,state,revision,preview,changes.Count,Map("changes",changes,"warnings",warnings,"blockerCodes",codes.ToArray(),"expiresAt",preview?Json.Get(value,"expiresAt"):null));
        }
    }
}
