using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal enum NativeEditorRoute { Info, Invoke, ArchiveForeign }
    internal sealed class NativeEditorException : Exception
    {
        public readonly string Code;
        public readonly int Status;
        public NativeEditorException(string code, int status) : base("Native editor request failed: " + code) { Code = code; Status = status; }
    }
    internal sealed class NativeEditorHttpReply
    {
        public readonly int Status; public readonly object Value;
        internal NativeEditorHttpReply(int status, object value) { Status = status; Value = value; }
    }
    // Captured by the backend owner. Tokens have no public getter and never enter logs/URLs.
    internal sealed partial class NativeEditorHttpTransport
    {
        private readonly Uri root;
        private readonly string token;
        private readonly Func<bool> current;
        internal NativeEditorHttpTransport(Uri address, string credential, Func<bool> isCurrent)
        {
            if (address == null || address.Scheme != "http" || address.Host != "127.0.0.1" || address.Port < 1 || address.UserInfo.Length != 0
                || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0 || string.IsNullOrEmpty(credential)
                || credential.Length > 512 || Regex.IsMatch(credential,@"[\r\n]") || isCurrent == null) throw new NativeEditorException("ENDPOINT_REJECTED",0);
            root = address; token = credential; current = isCurrent;
        }
        internal Uri Endpoint { get { return root; } }
        internal Task<NativeEditorHttpReply> RequestAsync(NativeEditorRoute route, string body, CancellationToken cancellation,int deadlineMs=15000)
        {
            if (!Enum.IsDefined(typeof(NativeEditorRoute),route)) throw new ArgumentException("Invalid editor route");
            byte[] bytes = body == null ? null : Encoding.UTF8.GetBytes(body);
            bool get = route == NativeEditorRoute.Info;
            if ((get && bytes != null) || (!get && (bytes == null || bytes.Length > 65536))) throw new ArgumentException("Invalid editor body");
            string path = get ? "/api/native-editor" : route == NativeEditorRoute.Invoke ? "/api/native-editor/invoke" : "/api/native-editor/archive-foreign";
            return SendAsync(path,get,bytes == null ? null : new MemoryStream(bytes,false),bytes == null ? 0 : bytes.Length,true,false,cancellation,deadlineMs);
        }
        internal Task<NativeEditorHttpReply> DiagnosticsAsync(int maximum,CancellationToken cancellation)
        {
            if(maximum<1 || maximum>256)throw new ArgumentOutOfRangeException("maximum");
            return SendAsync("/api/backend/diagnostics?max="+maximum.ToString(CultureInfo.InvariantCulture),true,null,0,false,false,cancellation);
        }
        internal Task<NativeEditorHttpReply> UploadAsync(string instance,string library,string item,string slot,Stream source,long length,CancellationToken cancellation)
        {
            Guid id;
            if(!Guid.TryParseExact(instance,"D",out id)||id==Guid.Empty || !Guid.TryParseExact(library,"D",out id)||id==Guid.Empty
                || !Regex.IsMatch(item??"",@"\A(?:legacy-item|unresolved-native):[1-9][0-9]{0,14}\z") || (slot!="icon"&&slot!="logo"&&slot!="hero")
                || source==null || !source.CanRead || length<1 || length>32L*1024*1024)throw new ArgumentException("Invalid upload");
            string path="/api/native-editor/upload?instanceId="+Uri.EscapeDataString(instance)+"&libraryId="+Uri.EscapeDataString(library)+"&itemId="+Uri.EscapeDataString(item)+"&slot="+slot;
            return SendAsync(path,false,source,length,false,true,cancellation);
        }
        private Task<NativeEditorHttpReply> SendAsync(string path,bool get,Stream body,long length,bool ownsBody,bool binary,CancellationToken cancellation,int deadlineMs=15000)
        {
            if(deadlineMs<1||deadlineMs>45000)throw new ArgumentOutOfRangeException("deadlineMs");
            return Task.Run(async delegate
            {
                using(CancellationTokenSource deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    deadline.CancelAfter(deadlineMs);HttpWebRequest request=null;bool completed=false;
                    try
                    {
                        cancellation.ThrowIfCancellationRequested();if(!current())throw new NativeEditorException("EDITOR_SESSION_STALE",409);
                        request=(HttpWebRequest)WebRequest.Create(new Uri(root,path));
                        request.Method=get?"GET":"POST";request.Timeout=request.ReadWriteTimeout=deadlineMs;
                        request.AllowAutoRedirect=false;request.Proxy=null;request.ContentType=binary?"application/octet-stream":"application/json";
                        request.AllowWriteStreamBuffering=false;request.SendChunked=false;
                        request.Headers["X-Cocoon-Art-Token"]=token;if(!get)request.Headers["Origin"]=root.GetLeftPart(UriPartial.Authority);
                        using(deadline.Token.Register(request.Abort))
                        {
                            if(body!=null)
                            {
                                request.ContentLength=length;
                                Stream output=await request.GetRequestStreamAsync().ConfigureAwait(false);bool copied=false;
                                try
                                {
                                    byte[] buffer=new byte[16384];long remaining=length;
                                    while(remaining>0)
                                    {
                                        deadline.Token.ThrowIfCancellationRequested();if(!current()){request.Abort();throw new NativeEditorException("EDITOR_SESSION_STALE",409);}
                                        int count;try{count=await body.ReadAsync(buffer,0,(int)Math.Min(buffer.Length,remaining),deadline.Token).ConfigureAwait(false);}
                                        catch(OperationCanceledException){request.Abort();if(deadline.IsCancellationRequested)throw;throw new NativeEditorException("EDITOR_UPLOAD_SOURCE_FAILED",0);}
                                        catch(Exception){request.Abort();throw new NativeEditorException("EDITOR_UPLOAD_SOURCE_FAILED",0);}
                                        if(count<1 || count>Math.Min(buffer.Length,remaining)){request.Abort();throw new NativeEditorException("EDITOR_UPLOAD_INCOMPLETE",400);}
                                        if(!current()){request.Abort();throw new NativeEditorException("EDITOR_SESSION_STALE",409);}
                                        await output.WriteAsync(buffer,0,count,deadline.Token).ConfigureAwait(false);remaining-=count;
                                    }
                                    copied=true;
                                }
                                finally{try{output.Dispose();}catch{if(copied)throw new NativeEditorException("EDITOR_CONNECTION_FAILED",0);}}
                            }
                            HttpWebResponse response;
                            try{response=(HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);}
                            catch(WebException error){if(error.Response==null)throw;response=error.Response as HttpWebResponse;if(response==null)throw;}
                            using(response)
                            {
                                int status=(int)response.StatusCode;
                                if(status>=300&&status<400)throw new NativeEditorException("EDITOR_REDIRECT_REJECTED",status);
                                if(response.ContentType==null||!Regex.IsMatch(response.ContentType,@"\Aapplication/json(?:\s*;|$)",RegexOptions.IgnoreCase)||response.ContentLength>1024*1024)throw NativeEditorClient.Invalid(status);
                                using(Stream input=response.GetResponseStream())using(MemoryStream content=new MemoryStream())
                                {
                                    byte[] buffer=new byte[16384];int read;
                                    while((read=await input.ReadAsync(buffer,0,buffer.Length,deadline.Token).ConfigureAwait(false))>0)
                                    {if(content.Length+read>1024*1024)throw new NativeEditorException("EDITOR_RESPONSE_TOO_LARGE",status);content.Write(buffer,0,read);}
                                    deadline.Token.ThrowIfCancellationRequested();if(!current())throw new NativeEditorException("EDITOR_SESSION_STALE",409);
                                    object value;try{value=NativeEditorJson.Parse(new UTF8Encoding(false,true).GetString(content.ToArray()));}catch(Exception){throw NativeEditorClient.Invalid(status);}
                                    completed=true;return new NativeEditorHttpReply(status,value);
                                }
                            }
                        }
                    }
                    catch(OperationCanceledException){if(cancellation.IsCancellationRequested)throw new OperationCanceledException(cancellation);throw new NativeEditorException("EDITOR_TIMEOUT",0);}
                    catch(WebException error){if(error.Response!=null)error.Response.Close();if(cancellation.IsCancellationRequested)throw new OperationCanceledException(cancellation);throw new NativeEditorException(deadline.IsCancellationRequested||error.Status==WebExceptionStatus.Timeout?"EDITOR_TIMEOUT":"EDITOR_CONNECTION_FAILED",0);}
                    catch(IOException){if(cancellation.IsCancellationRequested)throw new OperationCanceledException(cancellation);throw new NativeEditorException(deadline.IsCancellationRequested?"EDITOR_TIMEOUT":"EDITOR_CONNECTION_FAILED",0);}
                    catch(ObjectDisposedException){if(cancellation.IsCancellationRequested)throw new OperationCanceledException(cancellation);throw new NativeEditorException(deadline.IsCancellationRequested?"EDITOR_TIMEOUT":"EDITOR_CONNECTION_FAILED",0);}
                    catch(NativeEditorException){throw;}
                    catch(Exception){throw new NativeEditorException(binary?"EDITOR_UPLOAD_SOURCE_FAILED":"EDITOR_CONNECTION_FAILED",0);}
                    finally{if(!completed&&request!=null){try{request.Abort();}catch{}}if(ownsBody&&body!=null)body.Dispose();}
                }
            });
        }

    }
    internal sealed class NativeEditorInfo
    {
        public readonly string InstanceId, LibraryId;
        internal NativeEditorInfo(string instance, string library) { InstanceId = instance; LibraryId = library; }
    }
    // The validated DTO is immutable; callers receive a fresh deep copy for display binding.
    internal sealed class NativeEditorResult
    {
        private readonly string data;
        public readonly string RequestId, InstanceId, LibraryId, Operation;
        public Dictionary<string,object> Data { get { return (Dictionary<string,object>)NativeEditorJson.Parse(data); } }
        internal NativeEditorResult(string request, NativeEditorInfo info, string operation, object value)
        { RequestId=request; InstanceId=info.InstanceId; LibraryId=info.LibraryId; Operation=operation; data=NativeEditorJson.Encode(value); }
    }
    internal sealed class NativeEditorCandidate
    {
        public readonly string Id, SourceBlobSha256; public readonly int Width,Height;
        internal NativeEditorCandidate(string id,string hash,int width,int height){Id=id;SourceBlobSha256=hash;Width=width;Height=height;}
    }
    internal sealed class NativeEditorUploadResult
    {
        public readonly string InstanceId,LibraryId; public readonly NativeEditorCandidate Candidate;
        internal NativeEditorUploadResult(NativeEditorInfo info,NativeEditorCandidate candidate){InstanceId=info.InstanceId;LibraryId=info.LibraryId;Candidate=candidate;}
    }
    internal sealed class NativeEditorArchiveResult
    {
        public readonly string InstanceId,LibraryId,ArchiveId; public readonly long PreviousRevision;
        public long Revision {get{return 0;}} public int EntryCount {get{return 0;}}
        internal NativeEditorArchiveResult(NativeEditorInfo info,string archive,long previous){InstanceId=info.InstanceId;LibraryId=info.LibraryId;ArchiveId=archive;PreviousRevision=previous;}
    }
    internal enum NativeDiagnosticEvent {OperationStarted,OperationCompleted,OperationRejected,OperationCancelled,OperationTimedOut,BackendReady,BackendStopped,BackendFailed}
    internal enum NativeDiagnosticComponent {Backend,Broker,DevicePull,Catalog,Settings,Runtime,Api,Artwork}
    internal enum NativeDiagnosticCode {OK,CANCELLED,TIMEOUT,BUSY,DISCONNECTED,UNSUPPORTED,STALE_SESSION,VALIDATION_FAILED,BACKEND_COMMAND_REJECTED,BACKEND_START_FAILED,SHUTDOWN_FAILED,BROKER_UNAVAILABLE,DEVICE_PULL_FAILED,CATALOG_SCAN_FAILED,SETTINGS_WRITE_FAILED,INTERNAL_FAILED}
    internal sealed class NativeBackendDiagnosticEntry
    {
        public readonly Guid Id; public readonly DateTime Timestamp; public readonly NativeDiagnosticEvent Event;
        public readonly NativeDiagnosticComponent Component; public readonly NativeDiagnosticCode Code;
        public readonly Guid? OperationId; public readonly long? DurationMs,Count; public readonly bool? Success;
        internal NativeBackendDiagnosticEntry(Guid id,DateTime timestamp,NativeDiagnosticEvent ev,NativeDiagnosticComponent component,NativeDiagnosticCode code,Guid? operation,long? duration,long? count,bool? success)
        {Id=id;Timestamp=timestamp;Event=ev;Component=component;Code=code;OperationId=operation;DurationMs=duration;Count=count;Success=success;}
    }
    internal sealed class NativeBackendDiagnostics
    {
        public readonly Guid InstanceId; public readonly DateTime Timestamp; public readonly int Capacity,Retained;
        public readonly long Recorded,Dropped; public readonly IList<NativeBackendDiagnosticEntry> Events;
        internal NativeBackendDiagnostics(Guid id,DateTime time,int capacity,int retained,long recorded,long dropped,List<NativeBackendDiagnosticEntry> entries)
        {InstanceId=id;Timestamp=time;Capacity=capacity;Retained=retained;Recorded=recorded;Dropped=dropped;Events=entries.AsReadOnly();}
    }
    internal sealed partial class NativeEditorClient
    {
        public const string Contract = "cocoon.native-editor/1";
        private readonly Func<Uri> endpoint;
        private readonly Func<NativeEditorHttpTransport> capture;
        private readonly object sync = new object();
        private NativeEditorInfo info;
        private Uri bound;
        private Guid? diagnosticInstance;
        private long connectionRevision;
        public NativeEditorClient(CocoonBackend owner, Func<NativeEditorHttpTransport> captureTransport)
            : this(delegate { return owner == null ? null : owner.Endpoint; },captureTransport) { if(owner==null) throw new ArgumentNullException("owner"); }
        internal NativeEditorClient(Func<Uri> currentEndpoint, Func<NativeEditorHttpTransport> captureTransport)
        { if(currentEndpoint==null || captureTransport==null) throw new ArgumentNullException("editor dependencies");endpoint=currentEndpoint;capture=captureTransport; }
        internal static NativeEditorException Invalid(int status = 0) { return new NativeEditorException("EDITOR_RESPONSE_INVALID",status); }
        private void Current(Uri address)
        { if (address == null || !address.Equals(endpoint())) throw new NativeEditorException("EDITOR_SESSION_STALE",409); }
        public async Task<NativeEditorInfo> ConnectAsync(CancellationToken token)
        {
            long attempt;lock(sync)attempt=++connectionRevision;
            NativeEditorHttpTransport transport=capture(); Current(transport.Endpoint);
            NativeEditorHttpReply reply=await transport.RequestAsync(NativeEditorRoute.Info,null,token).ConfigureAwait(false); Current(transport.Endpoint);
            Dictionary<string,object> value=Obj(reply.Value,"contract","instanceId","libraryId","deviceWritesEnabled","previewAppliesAutomatically");
            if(value.Count!=5 || reply.Status!=200 || Text(value,"contract")!=Contract || Flag(value,"deviceWritesEnabled") || Flag(value,"previewAppliesAutomatically")) throw Invalid(reply.Status);
            NativeEditorInfo next=new NativeEditorInfo(Uuid(value,"instanceId"),Uuid(value,"libraryId"));
            lock(sync) { if(attempt!=connectionRevision)throw new NativeEditorException("EDITOR_SESSION_STALE",409);info=next;bound=transport.Endpoint;diagnosticInstance=null; } return next;
        }
        public Task<NativeEditorResult> CandidatesAsync(string itemId, ArtworkSlot slot, CancellationToken token)
        { return Invoke("candidates",Map("itemId",Item(itemId),"slot",Slot(slot)),token); }
        public Task<NativeEditorResult> OriginalsAsync(string itemId,ArtworkSlot slot,string candidateId,CancellationToken token)
        { return Invoke("originals",Map("itemId",Item(itemId),"slot",Slot(slot),"candidateId",Id(candidateId)),token); }
        public Task<NativeEditorResult> PendingAsync(CancellationToken token) { return Invoke("pending",Map(),token); }
        public Task<NativeEditorResult> CropAsync(string itemId,ArtworkSlot slot,string candidateId,int x,int y,int width,int height,int outputWidth,int outputHeight,CancellationToken token)
        {
            if(x<0 || y<0 || width<1 || height<1 || outputWidth<1 || outputHeight<1 || outputWidth>8192 || outputHeight>8192 || (long)outputWidth*outputHeight>32L*1024*1024) throw new ArgumentException("Invalid crop");
            string name=Slot(slot);
            return Invoke("crop",Map("itemId",Item(itemId),"slot",name,"candidateId",Id(candidateId),"crop",Map("x",x,"y",y,"width",width,"height",height),"targetProfile",Map("id","native-"+name,"slot",name,"width",outputWidth,"height",outputHeight,"format","png")),token);
        }
        public Task<NativeEditorResult> StageAsync(string itemId,ArtworkSlot slot,string candidateId,long revision,string variantToken,CancellationToken token)
        { Dictionary<string,object> input=Map("itemId",Item(itemId),"slot",Slot(slot),"candidateId",Id(candidateId),"expectedRevision",Revision(revision));if(variantToken!=null) input.Add("variantToken",Id(variantToken));return Invoke("stage",input,token); }
        public Task<NativeEditorResult> UndoAsync(string entryId,long revision,CancellationToken token)
        { return Invoke("undo",Map("entryId",Id(entryId),"expectedRevision",Revision(revision)),token); }
        public Task<NativeEditorResult> MetadataPreviewAsync(string itemId,Dictionary<string,object> fields,CancellationToken token)
        { ValidateFields(fields);return Invoke("metadataPreview",Map("itemId",Item(itemId),"fields",fields),token); }
        public Task<NativeEditorResult> MetadataConfirmAsync(string previewToken,string[] confirmedFields,long revision,CancellationToken token)
        {
            if(confirmedFields==null || confirmedFields.Length<1 || confirmedFields.Length>19 || new HashSet<string>(confirmedFields).Count!=confirmedFields.Length) throw new ArgumentException("Invalid selected fields");
            foreach(string field in confirmedFields) if(!FieldTypes.ContainsKey(field)) throw new ArgumentException("Invalid selected fields");
            return Invoke("metadataConfirm",Map("previewToken",Id(previewToken),"confirmedFields",confirmedFields,"expectedRevision",Revision(revision)),token);
        }
        public Task<NativeEditorUploadResult> UploadAsync(string itemId,ArtworkSlot slot,byte[] bytes,CancellationToken token)
        {
            Item(itemId);Slot(slot);if(bytes==null||bytes.Length<1||bytes.Length>32*1024*1024)throw new ArgumentException("Invalid upload length");
            byte[] snapshot=(byte[])bytes.Clone();return UploadBytes(itemId,slot,snapshot,token);
        }
        private async Task<NativeEditorUploadResult> UploadBytes(string itemId,ArtworkSlot slot,byte[] bytes,CancellationToken token)
        {using(MemoryStream source=new MemoryStream(bytes,false))return await UploadAsync(itemId,slot,source,bytes.Length,token).ConfigureAwait(false);}
        public async Task<NativeEditorUploadResult> UploadAsync(string itemId,ArtworkSlot slot,Stream source,long length,CancellationToken token)
        {
            string item=Item(itemId),name=Slot(slot);
            if(source==null||!source.CanRead||length<1||length>32L*1024*1024)throw new ArgumentException("Invalid upload source");
            NativeEditorInfo snapshot;Uri address;Snapshot(out snapshot,out address);
            NativeEditorHttpTransport transport=Capture(address);await VerifyContext(transport,snapshot,address,token).ConfigureAwait(false);
            NativeEditorHttpReply reply=await transport.UploadAsync(snapshot.InstanceId,snapshot.LibraryId,item,name,source,length,token).ConfigureAwait(false);
            CheckContext(snapshot,address);ThrowFailure(reply,snapshot);
            Dictionary<string,object> value=Obj(reply.Value,"contract","instanceId","libraryId","deviceWritesEnabled","previewAppliesAutomatically","ok","data");
            if(reply.Status!=201||value.Count!=7||!Flag(value,"ok"))throw Invalid(reply.Status);ValidateBinding(value,snapshot);
            Dictionary<string,object> data=Obj(Json.Get(value,"data"),"candidate","pendingChanged");if(data.Count!=2||Flag(data,"pendingChanged"))throw Invalid(reply.Status);
            object candidate=Json.Get(data,"candidate");Candidate(candidate);if(Text(candidate,"id")!=Text(candidate,"sourceBlobSha256"))throw Invalid(reply.Status);
            if(Integer(candidate,"width",1,8192)*Integer(candidate,"height",1,8192)>16L*1024*1024)throw Invalid(reply.Status);
            await VerifyContext(transport,snapshot,address,token).ConfigureAwait(false);
            return new NativeEditorUploadResult(snapshot,new NativeEditorCandidate(Text(candidate,"id"),Text(candidate,"sourceBlobSha256"),(int)Integer(candidate,"width",1,8192),(int)Integer(candidate,"height",1,8192)));
        }
        public async Task<NativeEditorArchiveResult> ArchiveForeignAsync(bool explicitConfirm,CancellationToken token)
        {
            if(!explicitConfirm)throw new NativeEditorException("EDITOR_CONFIRM_REQUIRED",400);
            NativeEditorInfo snapshot;Uri address;Snapshot(out snapshot,out address);NativeEditorHttpTransport transport=Capture(address);
            await VerifyContext(transport,snapshot,address,token).ConfigureAwait(false);
            string body=NativeEditorJson.Encode(Map("instanceId",snapshot.InstanceId,"libraryId",snapshot.LibraryId,"confirm",true));
            NativeEditorHttpReply reply=await transport.RequestAsync(NativeEditorRoute.ArchiveForeign,body,token).ConfigureAwait(false);CheckContext(snapshot,address);ThrowFailure(reply,snapshot);
            Dictionary<string,object> value=Obj(reply.Value,"contract","instanceId","libraryId","deviceWritesEnabled","previewAppliesAutomatically","archived","archiveId","previousRevision","revision","entries");
            if(reply.Status!=200||value.Count!=10||!Flag(value,"archived")||Integer(value,"revision",0,0)!=0)throw Invalid(reply.Status);ValidateBinding(value,snapshot);
            List<object> entries=Json.Get(value,"entries")as List<object>;if(entries==null||entries.Count!=0)throw Invalid(reply.Status);
            string archive=Uuid(value,"archiveId");long previous=Integer(value,"previousRevision",0,9007199254740991L);
            await VerifyContext(transport,snapshot,address,token).ConfigureAwait(false);
            return new NativeEditorArchiveResult(snapshot,archive,previous);
        }
        public async Task<NativeBackendDiagnostics> DiagnosticsAsync(int maximum,CancellationToken token)
        {
            if(maximum<1||maximum>256)throw new ArgumentOutOfRangeException("maximum");
            NativeEditorInfo snapshot;Uri address;Snapshot(out snapshot,out address);NativeEditorHttpTransport transport=Capture(address);
            await VerifyContext(transport,snapshot,address,token).ConfigureAwait(false);
            NativeEditorHttpReply reply=await transport.DiagnosticsAsync(maximum,token).ConfigureAwait(false);CheckContext(snapshot,address);ThrowFailure(reply,snapshot);
            if(reply.Status!=200)throw Invalid(reply.Status);NativeBackendDiagnostics result=ParseDiagnostics(reply.Value,maximum);
            await VerifyContext(transport,snapshot,address,token).ConfigureAwait(false);
            lock(sync){if(!ReferenceEquals(snapshot,info))throw new NativeEditorException("EDITOR_SESSION_STALE",409);if(diagnosticInstance.HasValue&&diagnosticInstance.Value!=result.InstanceId)throw new NativeEditorException("EDITOR_SESSION_STALE",409);diagnosticInstance=result.InstanceId;}
            return result;
        }
        private void Snapshot(out NativeEditorInfo snapshot,out Uri address)
        {lock(sync){snapshot=info;address=bound;}if(snapshot==null)throw new NativeEditorException("EDITOR_NOT_CONNECTED",0);Current(address);}
        private NativeEditorHttpTransport Capture(Uri address)
        {NativeEditorHttpTransport transport=capture();if(!transport.Endpoint.Equals(address))throw new NativeEditorException("EDITOR_SESSION_STALE",409);return transport;}
        private void CheckContext(NativeEditorInfo snapshot,Uri address)
        {Current(address);lock(sync)if(!ReferenceEquals(snapshot,info))throw new NativeEditorException("EDITOR_SESSION_STALE",409);}
        private static void ValidateBinding(object value,NativeEditorInfo snapshot)
        {if(Text(value,"contract")!=Contract||Uuid(value,"instanceId")!=snapshot.InstanceId||Uuid(value,"libraryId")!=snapshot.LibraryId||Flag(value,"deviceWritesEnabled")||Flag(value,"previewAppliesAutomatically"))throw new NativeEditorException("EDITOR_SESSION_STALE",409);}
        private async Task VerifyContext(NativeEditorHttpTransport transport,NativeEditorInfo snapshot,Uri address,CancellationToken token)
        {
            CheckContext(snapshot,address);NativeEditorHttpReply reply=await transport.RequestAsync(NativeEditorRoute.Info,null,token).ConfigureAwait(false);CheckContext(snapshot,address);ThrowFailure(reply,snapshot);
            Dictionary<string,object> value=Obj(reply.Value,"contract","instanceId","libraryId","deviceWritesEnabled","previewAppliesAutomatically");if(reply.Status!=200||value.Count!=5)throw Invalid(reply.Status);ValidateBinding(value,snapshot);
        }
        private static void ThrowFailure(NativeEditorHttpReply reply,NativeEditorInfo snapshot)
        {
            if(reply.Status<400)return;Dictionary<string,object> value=reply.Value as Dictionary<string,object>;if(value==null)throw Invalid(reply.Status);
            if(value.ContainsKey("contract"))
            {
                Obj(value,"contract","instanceId","requestId","ok","data","error","deviceWritesEnabled");if(value.Count!=7||Text(value,"contract")!=Contract||Flag(value,"ok")||Flag(value,"deviceWritesEnabled")||Json.Get(value,"data")!=null)throw Invalid(reply.Status);
                if(Uuid(value,"instanceId")!=snapshot.InstanceId)throw new NativeEditorException("EDITOR_SESSION_STALE",409);Uuid(value,"requestId");Dictionary<string,object> error=Obj(Json.Get(value,"error"),"code","phase","status");if(error.Count!=3||Text(error,"phase")!="NATIVE_EDITOR"||Integer(error,"status",100,599)!=reply.Status)throw Invalid(reply.Status);throw new NativeEditorException(Code(error,"code"),reply.Status);
            }
            Obj(value,"code","phase");if(value.Count!=2||(Text(value,"phase")!="NATIVE_EDITOR"&&Text(value,"phase")!="DIAGNOSTICS"))throw Invalid(reply.Status);throw new NativeEditorException(Code(value,"code"),reply.Status);
        }
        private static readonly string[] DiagnosticEvents={"operation.started","operation.completed","operation.rejected","operation.cancelled","operation.timed_out","backend.ready","backend.stopped","backend.failed"};
        private static readonly string[] DiagnosticComponents={"backend","broker","device-pull","catalog","settings","runtime","api","artwork"};
        private static Guid DiagnosticUuid(object value,string key)
        {string text=Uuid(value,key);if(!Regex.IsMatch(text,@"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-8][0-9a-fA-F]{3}-[89aAbB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}\z"))throw Invalid();return Guid.Parse(text);}
        private static DateTime DiagnosticTime(object value,string key)
        {DateTime date;if(!DateTime.TryParseExact(Text(value,key),"yyyy-MM-dd'T'HH:mm:ss.fff'Z'",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out date))throw Invalid();return date;}
        internal static NativeBackendDiagnostics ParseDiagnostics(object input,int maximum)
        {
            Dictionary<string,object> value=Obj(input,"contract","instanceId","timestamp","capacity","recorded","dropped","retained","events");
            if(value.Count!=8||Text(value,"contract")!="cocoon.diagnostics/1")throw Invalid();
            Guid identity=DiagnosticUuid(value,"instanceId");DateTime timestamp=DiagnosticTime(value,"timestamp");int capacity=(int)Integer(value,"capacity",1,2048),retained=(int)Integer(value,"retained",0,capacity);
            long recorded=Integer(value,"recorded",0,9007199254740991L),dropped=Integer(value,"dropped",0,recorded);if(recorded<retained)throw Invalid();
            List<object> rows=Json.Get(value,"events")as List<object>;if(rows==null||rows.Count!=Math.Min(retained,maximum))throw Invalid();List<NativeBackendDiagnosticEntry> entries=new List<NativeBackendDiagnosticEntry>();HashSet<Guid> ids=new HashSet<Guid>();
            foreach(object row in rows)
            {
                Dictionary<string,object> entry=Obj(row,"id","timestamp","event","code","component","operationId","durationMs","count","success");Guid id=DiagnosticUuid(entry,"id");if(!ids.Add(id))throw Invalid();
                DateTime time=DiagnosticTime(entry,"timestamp");int ev=Array.IndexOf(DiagnosticEvents,Text(entry,"event")),component=Array.IndexOf(DiagnosticComponents,Text(entry,"component"));
                NativeDiagnosticCode code;string name=Text(entry,"code");if(ev<0||component<0||!Regex.IsMatch(name,@"\A[A-Z_]{1,64}\z")||!Enum.TryParse<NativeDiagnosticCode>(name,false,out code)||!Enum.IsDefined(typeof(NativeDiagnosticCode),code))throw Invalid();
                Guid? operation=entry.ContainsKey("operationId")?(Guid?)DiagnosticUuid(entry,"operationId"):null;
                long? duration=entry.ContainsKey("durationMs")?(long?)Integer(entry,"durationMs",0,86400000):null,count=entry.ContainsKey("count")?(long?)Integer(entry,"count",0,1000000000):null;
                bool? success=entry.ContainsKey("success")?(bool?)Flag(entry,"success"):null;
                entries.Add(new NativeBackendDiagnosticEntry(id,time,(NativeDiagnosticEvent)ev,(NativeDiagnosticComponent)component,code,operation,duration,count,success));
            }
            return new NativeBackendDiagnostics(identity,timestamp,capacity,retained,recorded,dropped,entries);
        }
        private async Task<NativeEditorResult> Invoke(string operation,Dictionary<string,object> input,CancellationToken token)
        {
            NativeEditorInfo snapshot; Uri address; lock(sync){snapshot=info;address=bound;}
            if(snapshot==null) throw new NativeEditorException("EDITOR_NOT_CONNECTED",0); Current(address);
            // Encode before yielding so mutable UI dictionaries cannot change an in-flight request.
            string body=NativeEditorJson.Encode(Map("contract",Contract,"instanceId",snapshot.InstanceId,"operation",operation,"input",input));
            NativeEditorHttpTransport transport=capture();if(!transport.Endpoint.Equals(address)) throw new NativeEditorException("EDITOR_SESSION_STALE",409);
            await VerifyContext(transport,snapshot,address,token).ConfigureAwait(false);
            NativeEditorHttpReply reply=await transport.RequestAsync(NativeEditorRoute.Invoke,body,token,operation=="videoPoster"?45000:15000).ConfigureAwait(false);Current(address);
            lock(sync) if(!ReferenceEquals(snapshot,info)) throw new NativeEditorException("EDITOR_SESSION_STALE",409);
            Dictionary<string,object> value=reply.Value as Dictionary<string,object>;
            // Pre-envelope session/parameter failures have the fixed code/phase shape.
            if(reply.Status>=400 && value!=null && !value.ContainsKey("contract"))
            { Obj(value,"code","phase");if(Text(value,"phase")!="NATIVE_EDITOR")throw Invalid(reply.Status);throw new NativeEditorException(Code(value,"code"),reply.Status); }
            value=Obj(value,"contract","instanceId","requestId","ok","data","error","deviceWritesEnabled");
            if(value.Count!=7)throw Invalid(reply.Status);
            if(Text(value,"contract")!=Contract || Uuid(value,"instanceId")!=snapshot.InstanceId || Flag(value,"deviceWritesEnabled")) throw new NativeEditorException("EDITOR_SESSION_STALE",409);
            string request=Uuid(value,"requestId"); bool ok=Flag(value,"ok");
            if(!ok)
            { Dictionary<string,object> error=Obj(Json.Get(value,"error"),"code","phase","status");int status=(int)Integer(error,"status",100,599);if(reply.Status!=status || Text(error,"phase")!="NATIVE_EDITOR" || Json.Get(value,"data")!=null)throw Invalid(reply.Status);throw new NativeEditorException(Code(error,"code"),status); }
            if(reply.Status!=200 || Json.Get(value,"error")!=null)throw Invalid(reply.Status);
            try
            {
                ValidateData(operation,Json.Get(value,"data"));
                foreach(string key in new[]{"itemId","slot","candidateId"})if(input.ContainsKey(key) && Json.Get(Json.Get(value,"data"),key)!=null && !object.Equals(input[key],Json.Get(Json.Get(value,"data"),key)))throw Invalid(reply.Status);
                if(input.ContainsKey("expectedRevision") && Integer(Json.Get(value,"data"),"revision",0,9007199254740991L)!=(long)input["expectedRevision"]+1)throw Invalid(reply.Status);
            }
            catch(ArgumentException){throw Invalid(reply.Status);}
            await VerifyContext(transport,snapshot,address,token).ConfigureAwait(false);
            return new NativeEditorResult(request,snapshot,operation,Json.Get(value,"data"));
        }
        internal static Dictionary<string,object> Map(params object[] pairs)
        { Dictionary<string,object> result=new Dictionary<string,object>(StringComparer.Ordinal);for(int i=0;i<pairs.Length;i+=2)result.Add((string)pairs[i],pairs[i+1]);return result; }
        internal static Dictionary<string,object> Obj(object v,params string[] keys)
        { Dictionary<string,object> d=v as Dictionary<string,object>;if(d==null)throw Invalid();foreach(string k in d.Keys)if(Array.IndexOf(keys,k)<0)throw Invalid();return d; }
        private static string Text(object v,string k) {string text=Json.Get(v,k) as string;if(text==null)throw Invalid();return text;}
        private static bool Flag(object v,string k) {object b=Json.Get(v,k);if(!(b is bool))throw Invalid();return (bool)b;}
        private static string Uuid(object v,string k) {string s=Text(v,k);Guid id;if(!Guid.TryParseExact(s,"D",out id) || id==Guid.Empty)throw Invalid();return s;}
        private static string Code(object v,string k) {string s=Text(v,k);if(!Regex.IsMatch(s,@"\A[A-Z_]{1,64}\z"))throw Invalid();return s;}
        private static long Integer(object v,string k,long min,long max) {object n=Json.Get(v,k);if(!(n is double))throw Invalid();double d=(double)n;if(double.IsNaN(d)||double.IsInfinity(d)||d!=Math.Truncate(d)||d<min||d>max)throw Invalid();return (long)d;}
        private static string Item(string s) {if(!Regex.IsMatch(s??"",@"\A(?:legacy-item|unresolved-native):[1-9][0-9]{0,14}\z"))throw new ArgumentException("Invalid item");return s;}
        private static string Id(string s) {if(!Regex.IsMatch(s??"",@"\A[A-Za-z0-9_-]{1,128}\z"))throw new ArgumentException("Invalid editor identity");return s;}
        private static long Revision(long v) {if(v<0 || v>9007199254740990L)throw new ArgumentException("Invalid revision");return v;}
        private static string Slot(ArtworkSlot slot) {if(!Enum.IsDefined(typeof(ArtworkSlot),slot))throw new ArgumentException("Invalid slot");return slot.ToString().ToLowerInvariant();}
        private static readonly Dictionary<string,string> FieldTypes=new Dictionary<string,string> { {"displayName","text"},{"title","text"},{"summary","text"},{"developer","text"},{"publisher","text"},{"releaseDate","int"},{"rating","number"},{"genres","text"},{"players","text"},{"region","text"},{"videoUrl","url"},{"launchboxVideoUrl","url"},{"isFavorite","bool"},{"isHidden","bool"},{"sortOrder","int"},{"logoAnchor","int"},{"logoScaleX","number"},{"logoScaleY","number"},{"logoPlacementOverridden","bool"} };
        private static void ValidateFields(Dictionary<string,object> d)
        {
            if(d==null||d.Count<1||d.Count>19)throw new ArgumentException("Invalid fields");
            foreach(KeyValuePair<string,object> field in d)
            {
                string type;if(!FieldTypes.TryGetValue(field.Key,out type))throw new ArgumentException("Invalid field");object v=field.Value;if(v==null)continue;
                if(type=="bool") {if(!(v is bool))throw new ArgumentException("Invalid boolean");}
                else if(type=="text"||type=="url") {string s=v as string;if(s==null||s.Length>(field.Key=="summary"?16384:2048)||Regex.IsMatch(s,@"[\x00-\x08\x0b\x0c\x0e-\x1f]"))throw new ArgumentException("Invalid field text");if(type=="url"&&s.Length>0){Uri uri;if(!Uri.TryCreate(s,UriKind.Absolute,out uri)||(uri.Scheme!="http"&&uri.Scheme!="https")||uri.UserInfo.Length>0)throw new ArgumentException("Invalid URL field");}}
                else {if(!(v is int)&&!(v is long)&&!(v is double)&&!(v is float)&&!(v is decimal))throw new ArgumentException("Invalid field number");double n=Convert.ToDouble(v,CultureInfo.InvariantCulture);if(double.IsNaN(n)||double.IsInfinity(n)||(type=="int"&&(n!=Math.Truncate(n)||Math.Abs(n)>9007199254740991d)))throw new ArgumentException("Invalid field number");}
            }
        }
        private static void Hash(object d,string key) {if(!Regex.IsMatch(Text(d,key),@"\A[a-f0-9]{64}\z"))throw Invalid();}
        private static void Candidate(object value)
        {Dictionary<string,object>d=Obj(value,"id","sourceBlobSha256","width","height");Id(Text(d,"id"));Hash(d,"sourceBlobSha256");long w=Integer(d,"width",1,8192),h=Integer(d,"height",1,8192);if(w*h>32L*1024*1024)throw Invalid();}
        private static void Variant(object d) {string s=Text(d,"variantId");if(!Regex.IsMatch(s,@"\A(?:[A-Za-z0-9_-]{1,128}|asset-variant:[a-f0-9]{64})\z"))throw Invalid();Hash(d,"blobSha256");}
        internal static void ValidateData(string operation,object data)
        {
            Dictionary<string,object> d=data as Dictionary<string,object>;if(d==null)throw Invalid();
            if(operation=="logoPreview") {ValidateLogoPreview(d);return;}
            if(operation=="logoResetPreview") {ValidateData("metadataPreview",d);return;}
            if(operation=="videos" || operation=="videoPoster") {ValidateVideos(operation,d);return;}
            if(operation=="candidates") {Obj(d,"itemId","slot","items","recommendationOnly");Item(Text(d,"itemId"));ValidateSlot(d);if(!Flag(d,"recommendationOnly"))throw Invalid();List<object> rows=Json.Get(d,"items")as List<object>;if(rows==null||rows.Count>128)throw Invalid();HashSet<string> seen=new HashSet<string>();foreach(object row in rows){Candidate(row);if(!seen.Add(Text(row,"id")))throw Invalid();}}
            else if(operation=="originals")
            {
                Obj(d,"itemId","slot","candidateId","items","pendingChanged");Item(Text(d,"itemId"));ValidateSlot(d);Id(Text(d,"candidateId"));
                var rows=Json.Get(d,"items")as List<object>;if(d.Count!=5||Flag(d,"pendingChanged")||rows==null||rows.Count>128)throw Invalid();
                var seen=new HashSet<string>();foreach(object row in rows)
                {
                    if(Obj(row,"sha256","mime","byteSize","width","height").Count!=5)throw Invalid();Hash(row,"sha256");if(!seen.Add(Text(row,"sha256")))throw Invalid();
                    if(Array.IndexOf(new[]{"image/png","image/jpeg","image/webp","image/gif"},Text(row,"mime"))<0)throw Invalid();
                    Integer(row,"byteSize",1,32*1024*1024);long width=Integer(row,"width",1,8192),height=Integer(row,"height",1,8192);if(width*height>16*1024*1024)throw Invalid();
                }
            }
            else if(operation=="crop" || operation=="layout") {Obj(d,"itemId","slot","candidateId","sourceBlobSha256","variantId","blobSha256","variantToken","pendingChanged");Item(Text(d,"itemId"));ValidateSlot(d);Id(Text(d,"candidateId"));Hash(d,"sourceBlobSha256");Variant(d);Id(Text(d,"variantToken"));if(Flag(d,"pendingChanged"))throw Invalid();}
            else if(operation=="metadataPreview") {Obj(d,"itemId","revision","previewToken","changes","recommendationOnly");Item(Text(d,"itemId"));Integer(d,"revision",0,9007199254740991L);Id(Text(d,"previewToken"));if(!Flag(d,"recommendationOnly"))throw Invalid();Dictionary<string,object> changes=Json.Get(d,"changes")as Dictionary<string,object>;if(changes==null||changes.Count>19)throw Invalid();foreach(KeyValuePair<string,object> change in changes){Dictionary<string,object> pair=Obj(change.Value,"before","after");if(!pair.ContainsKey("before")||!pair.ContainsKey("after"))throw Invalid();ValidateFields(Map(change.Key,Json.Get(pair,"before")));ValidateFields(Map(change.Key,Json.Get(pair,"after")));}}
            else
            {
                Obj(d,"revision","entries");Integer(d,"revision",0,9007199254740991L);List<object> entries=Json.Get(d,"entries")as List<object>;if(entries==null||entries.Count>512)throw Invalid();HashSet<string> ids=new HashSet<string>(),identities=new HashSet<string>();
                foreach(object entry in entries){Dictionary<string,object> e=entry as Dictionary<string,object>;if(e==null)throw Invalid();string kind=Text(e,"kind");Obj(e,kind=="asset"?new[]{"entryId","kind","itemId","slot","candidateId","sourceBlobSha256","variantId","blobSha256"}:new[]{"entryId","kind","itemId","fields"});string id=Id(Text(e,"entryId")),item=Item(Text(e,"itemId"));if(!ids.Add(id))throw Invalid();if(kind=="asset"){ValidateSlot(e);Id(Text(e,"candidateId"));Hash(e,"sourceBlobSha256");if(e.ContainsKey("variantId")||e.ContainsKey("blobSha256"))Variant(e);}else if(kind=="metadata")ValidateFields(Json.Get(e,"fields")as Dictionary<string,object>);else throw Invalid();if(!identities.Add(item+":"+(kind=="asset"?Text(e,"slot"):"metadata")))throw Invalid();}
            }
        }
        private static void ValidateSlot(object d) {string s=Text(d,"slot");if(s!="icon"&&s!="logo"&&s!="hero")throw Invalid();}
    }
    internal static class NativeEditorJson
    {
        private static readonly Regex Number = new Regex(@"\G-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?",RegexOptions.None,TimeSpan.FromSeconds(1));
        public static string Encode(object value)
        {
            if(value==null)return "null";string s=value as string;if(s!=null)return Json.Quote(s);if(value is bool)return (bool)value?"true":"false";
            IDictionary<string,object> d=value as IDictionary<string,object>;if(d!=null){List<string> pairs=new List<string>();foreach(KeyValuePair<string,object> p in d)pairs.Add(Json.Quote(p.Key)+":"+Encode(p.Value));return "{"+string.Join(",",pairs.ToArray())+"}";}
            IEnumerable sequence=value as IEnumerable;if(sequence!=null){List<string> values=new List<string>();foreach(object item in sequence)values.Add(Encode(item));return "["+string.Join(",",values.ToArray())+"]";}
            if(value is double || value is float || value is decimal || value is int || value is long){double n=Convert.ToDouble(value,CultureInfo.InvariantCulture);if(double.IsNaN(n)||double.IsInfinity(n))throw new ArgumentException("Invalid JSON number");return Convert.ToString(value,CultureInfo.InvariantCulture);}
            throw new ArgumentException("Invalid JSON value");
        }
        public static object Parse(string text) {if(text==null||text.Length>1024*1024)throw NativeEditorClient.Invalid();int i=0;Check(text,ref i,0);White(text,ref i);if(i!=text.Length)throw NativeEditorClient.Invalid();return Json.Parse(text);}
        private static void White(string s,ref int i){while(i<s.Length&&" \t\r\n".IndexOf(s[i])>=0)i++;}
        private static string String(string s,ref int i){int start=i;if(i>=s.Length||s[i++]!='"')throw NativeEditorClient.Invalid();while(i<s.Length){char c=s[i++];if(c=='"')return (string)Json.Parse(s.Substring(start,i-start));if(c<32)throw NativeEditorClient.Invalid();if(c=='\\'){if(i>=s.Length)throw NativeEditorClient.Invalid();char e=s[i++];if(e=='u'){if(i+4>s.Length||!Regex.IsMatch(s.Substring(i,4),@"\A[0-9a-fA-F]{4}\z"))throw NativeEditorClient.Invalid();i+=4;}else if("\"\\/bfnrt".IndexOf(e)<0)throw NativeEditorClient.Invalid();}}throw NativeEditorClient.Invalid();}
        private static void Check(string s,ref int i,int depth)
        {
            if(depth>12)throw NativeEditorClient.Invalid();White(s,ref i);if(i>=s.Length)throw NativeEditorClient.Invalid();char c=s[i];
            if(c=='"'){String(s,ref i);return;}if(c=='{'||c=='['){bool obj=c=='{';char end=obj?'}':']';i++;White(s,ref i);if(i<s.Length&&s[i]==end){i++;return;}HashSet<string> keys=new HashSet<string>();while(true){if(obj){White(s,ref i);if(!keys.Add(String(s,ref i)))throw NativeEditorClient.Invalid();White(s,ref i);if(i>=s.Length||s[i++]!=':')throw NativeEditorClient.Invalid();}Check(s,ref i,depth+1);White(s,ref i);if(i<s.Length&&s[i]==end){i++;return;}if(i>=s.Length||s[i++]!=',')throw NativeEditorClient.Invalid();}}
            foreach(string word in new[]{"true","false","null"})if(string.CompareOrdinal(s,i,word,0,word.Length)==0){i+=word.Length;return;}
            Match m=Number.Match(s,i);if(!m.Success || m.Index!=i)throw NativeEditorClient.Invalid();i+=m.Length;
        }
    }
}
