using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Media;

namespace ThorCapture.Features.Library
{
    internal static class LibraryPathGuard
    {
        // Walk from the volume root before inspecting descendants: never follow an earlier junction.
        internal static void NoLinks(string path)
        {
            string full=Path.GetFullPath(path),current=Path.GetPathRoot(full);Check(current);
            foreach(string component in full.Substring(current.Length).Split(new[]{Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar},StringSplitOptions.RemoveEmptyEntries)){current=Path.Combine(current,component);Check(current);}
        }
        static void Check(string path){try{if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked path rejected");}catch(FileNotFoundException){}catch(DirectoryNotFoundException){}}
        internal static string Journal(string dataRoot,string id,string extension)
        {string path=Path.Combine(dataRoot,"transactions",MediaCodec.Id(id)+extension);NoLinks(path);NoLinks(path+".bak");NoLinks(path+".lock");return path;}
        internal static void Roots(MediaRepository repository){NoLinks(repository.DataRoot);NoLinks(repository.MediaRoot);NoLinks(Path.Combine(repository.DataRoot,"transactions"));}
        internal static void RootsForImport(string mediaRoot,string journal){NoLinks(mediaRoot);NoLinks(journal);NoLinks(journal+".bak");NoLinks(journal+".lock");}
        internal static void AtomicJournal(string path,string text,string temporary,bool replaceExisting=true)
        {
            if(Path.GetDirectoryName(temporary)!=Path.GetDirectoryName(path)||!temporary.StartsWith(path+".",StringComparison.Ordinal)||!temporary.EndsWith(".tmp",StringComparison.Ordinal))throw new ArgumentException("Invalid journal temporary");
            NoLinks(path);NoLinks(path+".bak");NoLinks(temporary);Directory.CreateDirectory(Path.GetDirectoryName(path));NoLinks(path);NoLinks(path+".bak");NoLinks(temporary);bool own=false;
            try
            {
                using(FileStream stream=File.Open(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){own=true;using(StreamWriter writer=new StreamWriter(stream,new UTF8Encoding(false,true))){writer.Write(text);writer.Flush();stream.Flush(true);}}
                NoLinks(path);NoLinks(path+".bak");NoLinks(temporary);if(replaceExisting&&File.Exists(path))File.Replace(temporary,path,path+".bak");else File.Move(temporary,path);
            }
            finally{if(own){NoLinks(temporary);if(File.Exists(temporary))File.Delete(temporary);}}
        }
    }
    internal enum LibraryOperationOutcome { Success, Cancelled, Conflict, ConfirmationRequired, Unsupported, Failed, TimedOut }
    internal sealed class PlannedMove
    {
        public readonly string Source, Target, Sha256, TrackId;
        public readonly long Length;
        internal PlannedMove(string source,string target,string hash,long length,string trackId){Source=source;Target=target;Sha256=hash;Length=length;TrackId=trackId;}
    }
    internal sealed class GroupOperationPlan
    {
        public readonly string TransactionId, RepositoryId, GroupId, Kind;
        public readonly long ExpectedRevision;
        public readonly MediaGroup SourceGroup;
        public readonly string DestinationRepositoryId;
        public readonly ReadOnlyCollection<PlannedMove> Moves;
        public readonly ReadOnlyCollection<string> Conflicts;
        public readonly bool RequiresConfirmation=true;
        internal GroupOperationPlan(string transaction,string repository,string group,long revision,string kind,IEnumerable<PlannedMove> moves,IEnumerable<string> conflicts,MediaGroup sourceGroup=null,string destinationRepositoryId="")
        {TransactionId=transaction;RepositoryId=repository;GroupId=group;ExpectedRevision=revision;Kind=kind;Moves=new ReadOnlyCollection<PlannedMove>(moves.ToArray());Conflicts=new ReadOnlyCollection<string>(conflicts.ToArray());SourceGroup=sourceGroup;DestinationRepositoryId=destinationRepositoryId;}
    }
    internal sealed class GroupOperationResult
    {
        public readonly LibraryOperationOutcome Outcome;
        public readonly string Code,TransactionId;
        public readonly MediaRepositoryResult RepositoryResult;
        internal GroupOperationResult(LibraryOperationOutcome outcome,string code,string transaction,MediaRepositoryResult result){Outcome=outcome;Code=code;TransactionId=transaction;RepositoryResult=result;}
        internal static GroupOperationResult From(MediaRepositoryResult result,string id)
        {return new GroupOperationResult(result.Outcome==MediaOutcome.Success?LibraryOperationOutcome.Success:result.Outcome==MediaOutcome.Cancelled?LibraryOperationOutcome.Cancelled:result.Outcome==MediaOutcome.Conflict?LibraryOperationOutcome.Conflict:result.Outcome==MediaOutcome.TimedOut?LibraryOperationOutcome.TimedOut:LibraryOperationOutcome.Failed,result.Code,id,result);}
    }
    internal static class GroupOperations
    {
        internal static bool Leaf(string name){return !String.IsNullOrWhiteSpace(name)&&name.Length<=180&&Path.GetFileName(name)==name&&name!="."&&name!=".."&&name.IndexOfAny(Path.GetInvalidFileNameChars())<0&&!name.EndsWith(".")&&!name.EndsWith(" ")&&!System.Text.RegularExpressions.Regex.IsMatch(name,@"\A(con|prn|aux|nul|com[1-9]|lpt[1-9])(\.|\z)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);}
        internal static string Hash(string path,CancellationToken token)
        {using(FileStream input=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))using(SHA256 hash=SHA256.Create()){byte[] buffer=new byte[65536];int count;while((count=input.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();hash.TransformBlock(buffer,0,count,null,0);}hash.TransformFinalBlock(buffer,0,0);return BitConverter.ToString(hash.Hash).Replace("-","");}}
        public static Task<GroupOperationPlan> PlanRenameAsync(MediaRepository repository,string groupId,string stem,CancellationToken token)
        {
            if(repository==null)throw new ArgumentNullException("repository");
            MediaSnapshot snapshot=repository.GetSnapshot();MediaGroup group=snapshot.FindGroup(groupId);
            return Task.Run(()=>{
                token.ThrowIfCancellationRequested();List<PlannedMove> moves=new List<PlannedMove>();List<string> conflicts=new List<string>();
                if(group==null)conflicts.Add("GROUP_NOT_FOUND");else if(!Leaf(stem)||stem.Length>128)conflicts.Add("INVALID_NAME");else
                {
                    int index=0;
                    foreach(MediaTrack track in group.Tracks)
                    {
                        token.ThrowIfCancellationRequested();string source=repository.ResolvePath(track),suffix=track.Role==ScreenRole.Upper?"-upper":track.Role==ScreenRole.Lower?"-lower":"-track"+(++index);
                        string target=Path.Combine(repository.MediaRoot,stem+suffix+Path.GetExtension(source));
                        if(track.State!=TrackState.Present)conflicts.Add("SOURCE_NEEDS_REVIEW");
                        Add(moves,conflicts,source,target,track.TrackId,track.Sha256,token);
                        foreach(string sidecar in new[]{".thor-meta.txt",".input.csv"})if(File.Exists(source+sidecar))Add(moves,conflicts,source+sidecar,target+sidecar,"",null,token);
                        string audio=Path.ChangeExtension(source,".wav");if(File.Exists(audio))Add(moves,conflicts,audio,Path.ChangeExtension(target,".wav"),"",null,token);
                    }
                }
                if(moves.Select(x=>x.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=moves.Count)conflicts.Add("TARGET_COLLISION");
                return new GroupOperationPlan(Guid.NewGuid().ToString("N"),snapshot.RepositoryId,groupId,group==null?0:group.Revision,"rename",moves,conflicts.Distinct(),group);
            },token);
        }
        private static void Add(List<PlannedMove> moves,List<string> conflicts,string source,string target,string track,string expected,CancellationToken token)
        {
            if(!Leaf(Path.GetFileName(source))||!Leaf(Path.GetFileName(target))){conflicts.Add("INVALID_NAME");return;}
            if(String.Equals(source,target,StringComparison.OrdinalIgnoreCase)){conflicts.Add("SAME_PATH");return;}
            try{FileInfo info=new FileInfo(source);if(!info.Exists||(info.Attributes&(FileAttributes.ReparsePoint|FileAttributes.ReadOnly))!=0){conflicts.Add("SOURCE_UNAVAILABLE");return;}if(File.Exists(target)||Directory.Exists(target)){conflicts.Add("TARGET_EXISTS");return;}
                string hash=Hash(source,token);if(expected!=null&&expected!=hash){conflicts.Add("SOURCE_CHANGED");return;}moves.Add(new PlannedMove(source,target,hash,info.Length,track));}
            catch(OperationCanceledException){throw;}catch{conflicts.Add("SOURCE_UNAVAILABLE");}
        }
        public static Task<GroupOperationPlan> PlanCopyAsync(MediaRepository repository,MediaRepository destination,string groupId,CancellationToken token)
        {
            MediaSnapshot sourceSnapshot=repository.GetSnapshot(),destinationSnapshot=destination.GetSnapshot();MediaGroup group=sourceSnapshot.FindGroup(groupId);
            return Task.Run(()=>{
                List<PlannedMove> moves=new List<PlannedMove>();List<string> conflicts=new List<string>();
                if(group==null)conflicts.Add("GROUP_NOT_FOUND");else if(repository.MediaRoot==destination.MediaRoot||destinationSnapshot.RepositoryId.Length==0)conflicts.Add("INVALID_DESTINATION");else foreach(MediaTrack track in group.Tracks)
                {
                    token.ThrowIfCancellationRequested();string source=repository.ResolvePath(track),target=Path.Combine(destination.MediaRoot,track.RelativePath);
                    if(track.State!=TrackState.Present)conflicts.Add("SOURCE_NEEDS_REVIEW");
                    if(destinationSnapshot.Groups.SelectMany(x=>x.Tracks).Any(x=>x.RelativePath.Equals(track.RelativePath,StringComparison.OrdinalIgnoreCase)))conflicts.Add("TARGET_REGISTERED");
                    Add(moves,conflicts,source,target,track.TrackId,track.Sha256,token);
                    foreach(string sidecar in new[]{".thor-meta.txt",".input.csv"})if(File.Exists(source+sidecar))Add(moves,conflicts,source+sidecar,target+sidecar,"",null,token);
                    string audio=Path.ChangeExtension(source,".wav");if(File.Exists(audio))Add(moves,conflicts,audio,Path.ChangeExtension(target,".wav"),"",null,token);
                }
                if(moves.Select(x=>x.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=moves.Count)conflicts.Add("TARGET_COLLISION");
                return new GroupOperationPlan(Guid.NewGuid().ToString("N"),sourceSnapshot.RepositoryId,groupId,group==null?0:group.Revision,"copy",moves,conflicts.Distinct(),group,destinationSnapshot.RepositoryId);
            },token);
        }
        public static async Task<GroupOperationResult> ExecuteCopyAsync(MediaRepository repository,MediaRepository destination,GroupOperationPlan plan,bool confirmed,CancellationToken token,IProgress<int> progress=null)
        {
            if(!confirmed)return new GroupOperationResult(LibraryOperationOutcome.ConfirmationRequired,"CONFIRMATION_REQUIRED",plan.TransactionId,null);
            try{await Task.Run(()=>{LibraryPathGuard.Roots(repository);LibraryPathGuard.Roots(destination);LibraryPathGuard.Journal(repository.DataRoot,plan.TransactionId,".thor-copy");}).ConfigureAwait(false);}catch{return new GroupOperationResult(LibraryOperationOutcome.Conflict,"LINKED_TRANSACTION_PATH",plan.TransactionId,null);}
            if(plan.Kind!="copy"||plan.Conflicts.Count>0||plan.DestinationRepositoryId!=destination.GetSnapshot().RepositoryId||plan.Moves.Any(x=>Path.GetDirectoryName(x.Target)!=destination.MediaRoot))return new GroupOperationResult(LibraryOperationOutcome.Conflict,"COPY_PLAN_CONFLICT",plan.TransactionId,null);
            return GroupOperationResult.From(await destination.ExecuteCopyRegistrationAsync(repository,plan,token,progress).ConfigureAwait(false),plan.TransactionId);
        }
        public static async Task<GroupOperationResult> RecoverCopyAsync(MediaRepository repository,MediaRepository destination,string transactionId,bool confirmed,CancellationToken token)
        {if(!confirmed)return new GroupOperationResult(LibraryOperationOutcome.ConfirmationRequired,"CONFIRMATION_REQUIRED",transactionId,null);try{GroupOperationPlan plan=await repository.ReadCopyPlanAsync(destination,transactionId,token).ConfigureAwait(false);return await ExecuteCopyAsync(repository,destination,plan,true,token).ConfigureAwait(false);}catch(OperationCanceledException){return new GroupOperationResult(LibraryOperationOutcome.Cancelled,"CANCELLED",transactionId,null);}catch{return new GroupOperationResult(LibraryOperationOutcome.Conflict,"COPY_JOURNAL_INVALID",transactionId,null);}}
        public static async Task<GroupOperationResult> ExecuteAsync(MediaRepository repository,GroupOperationPlan plan,bool confirmed,CancellationToken token,IProgress<int> progress=null)
        {
            if(repository==null||plan==null)throw new ArgumentNullException("plan");
            if(!confirmed)return new GroupOperationResult(LibraryOperationOutcome.ConfirmationRequired,"CONFIRMATION_REQUIRED",plan.TransactionId,null);
            try{await Task.Run(()=>{LibraryPathGuard.Roots(repository);LibraryPathGuard.Journal(repository.DataRoot,plan.TransactionId,".thor-rename");}).ConfigureAwait(false);}catch{return new GroupOperationResult(LibraryOperationOutcome.Conflict,"LINKED_TRANSACTION_PATH",plan.TransactionId,null);}
            if(plan.Kind!="rename")return await Unsupported(plan.TransactionId).ConfigureAwait(false);
            if(plan.Conflicts.Count>0)return new GroupOperationResult(LibraryOperationOutcome.Conflict,"PLAN_CONFLICT",plan.TransactionId,null);
            return await Map(repository.ExecutePlannedRenameAsync(plan,token,progress),plan.TransactionId).ConfigureAwait(false);
        }
        private static async Task<GroupOperationResult> Map(Task<MediaRepositoryResult> work,string id){return GroupOperationResult.From(await work.ConfigureAwait(false),id);}
        public static async Task<GroupOperationResult> RecoverAsync(MediaRepository repository,string transactionId,bool rollback,bool confirmed,CancellationToken token)
        {
            if(!confirmed)return new GroupOperationResult(LibraryOperationOutcome.ConfirmationRequired,"CONFIRMATION_REQUIRED",transactionId,null);
            try{await Task.Run(()=>{LibraryPathGuard.Roots(repository);LibraryPathGuard.Journal(repository.DataRoot,transactionId,".thor-rename");}).ConfigureAwait(false);}catch{return new GroupOperationResult(LibraryOperationOutcome.Conflict,"LINKED_TRANSACTION_PATH",transactionId,null);}
            return await Map(repository.RecoverPlannedRenameAsync(transactionId,rollback,token),transactionId).ConfigureAwait(false);
        }
        public static Task<GroupOperationResult> RecycleAsync(){return Unsupported("");}
        public static Task<GroupOperationResult> PermanentDeleteAsync(){return Unsupported("");}
        public static Task<GroupOperationResult> CrossRootMoveAsync(){return Unsupported("");}
        private static Task<GroupOperationResult> Unsupported(string id){return Task.FromResult(new GroupOperationResult(LibraryOperationOutcome.Unsupported,"UNSUPPORTED_SAFE_RELEASE",id,null));}
    }
    internal sealed class AnnotationRequest
    {
        public readonly string GroupId;public readonly long Revision;public readonly bool Favorite;public readonly ReadOnlyCollection<string> Tags;
        public AnnotationRequest(string id,long revision,bool favorite,IEnumerable<string> tags){GroupId=id;Revision=revision;Favorite=favorite;Tags=new ReadOnlyCollection<string>(tags.ToArray());}
    }
    internal static class MediaTags
    {
        internal sealed class BatchResult
        {public readonly LibraryOperationOutcome Outcome;public readonly ReadOnlyCollection<MediaRepositoryResult> Results;internal BatchResult(LibraryOperationOutcome outcome,IEnumerable<MediaRepositoryResult> results){Outcome=outcome;Results=new ReadOnlyCollection<MediaRepositoryResult>(results.ToArray());}}
        public static async Task<BatchResult> ApplyBatchAsync(IMediaRepository repository,IEnumerable<AnnotationRequest> requests,CancellationToken token)
        {
            AnnotationRequest[] copy=requests.ToArray();if(copy.Length>200)throw new ArgumentException("Batch limit exceeded");List<MediaRepositoryResult> results=new List<MediaRepositoryResult>();
            foreach(AnnotationRequest request in copy){if(token.IsCancellationRequested)return new BatchResult(LibraryOperationOutcome.Cancelled,results);results.Add(await repository.SetAnnotationsAsync(request.GroupId,request.Revision,request.Favorite,request.Tags,token).ConfigureAwait(false));}
            return new BatchResult(results.All(x=>x.Outcome==MediaOutcome.Success)?LibraryOperationOutcome.Success:token.IsCancellationRequested?LibraryOperationOutcome.Cancelled:LibraryOperationOutcome.Conflict,results);
        }
    }
}
namespace ThorCapture.Media
{
    using ThorCapture.Features.Library;
    public sealed partial class MediaRepository
    {
        // Reserve the destination repository before publishing any final filenames; its watcher cannot split peers.
        internal Task<MediaRepositoryResult> ExecuteCopyRegistrationAsync(MediaRepository source,GroupOperationPlan plan,CancellationToken token,IProgress<int> progress)
        {
            return Run(t=>{
                if(repositoryId!=plan.DestinationRepositoryId||source.MediaRoot==mediaRoot)return Result(MediaOutcome.Conflict,"COPY_SCOPE_CONFLICT","复制范围已改变",null);
                List<MediaIssue> issues=new List<MediaIssue>();List<MediaGroup> groups=Load(issues,t);if(issues.Count>0)return Result(MediaOutcome.Conflict,"RECORDS_NEED_REVIEW","目标媒体记录需审阅",null);
                string[] targetNames=plan.Moves.Where(x=>x.TrackId.Length>0).Select(x=>Path.GetFileName(x.Target)).ToArray();
                MediaGroup[] existing=groups.Where(group=>group.Tracks.Any(track=>targetNames.Contains(track.RelativePath,StringComparer.OrdinalIgnoreCase))).ToArray();
                MediaGroup imported;
                if(existing.Length>0)
                {
                    imported=existing.Length==1?existing[0]:null;
                    if(imported==null||imported.LegacyKey!="copy:"+plan.TransactionId||imported.Tracks.Count!=plan.SourceGroup.Tracks.Count||imported.Tracks.Any(track=>!plan.SourceGroup.Tracks.Any(original=>original.Role==track.Role&&original.Sha256==track.Sha256&&plan.Moves.Any(move=>move.TrackId==original.TrackId&&Path.GetFileName(move.Target)==track.RelativePath))))return Result(MediaOutcome.Conflict,"DESTINATION_GROUP_CONFLICT","复制文件已关联其他组，未覆盖",null);
                }
                else
                {
                    if(plan.Moves.Any(move=>File.Exists(move.Target)||Directory.Exists(move.Target)))return Result(MediaOutcome.Conflict,"TARGET_EXISTS","目标名称已存在，未覆盖",null);
                    MediaTrack[] reserved=plan.SourceGroup.Tracks.Select(original=>{PlannedMove move=plan.Moves.Single(x=>x.TrackId==original.TrackId);return new MediaTrack(Guid.NewGuid().ToString("N"),Path.GetFileName(move.Target),move.Sha256,"",original.Package,original.Role,original.Kind,TrackState.Missing,move.Length,original.WriteUtc);}).ToArray();
                    imported=new MediaGroup(Guid.NewGuid().ToString("N"),1,plan.SourceGroup.CreatedUtc,"copy:"+plan.TransactionId,reserved,plan.SourceGroup.Game,plan.SourceGroup.GameMissing,plan.SourceGroup.Favorite,plan.SourceGroup.Tags);t.ThrowIfCancellationRequested();Store(imported);groups.Add(imported);
                }
                MediaRepositoryResult copied=source.ExecutePlannedCopyAsync(plan,t,progress).GetAwaiter().GetResult();
                bool[] published=new bool[plan.Moves.Count];string copyPhase;try{source.ReadCopy(plan,out copyPhase,out published);}catch{if(copied.Outcome==MediaOutcome.Success)return Result(MediaOutcome.Conflict,"COPY_JOURNAL_INVALID","复制日志无法验证",null);}
                List<MediaTrack> actual=new List<MediaTrack>();
                foreach(MediaTrack reserved in imported.Tracks)
                {
                    string target=Path.Combine(mediaRoot,reserved.RelativePath);int step=plan.Moves.ToList().FindIndex(move=>Path.GetFileName(move.Target)==reserved.RelativePath);if(published[step]&&File.Exists(target)){MediaSource read=MediaSource.Read(SafePath(target),CancellationToken.None);if(read.Sha256!=reserved.Sha256)return Result(MediaOutcome.Conflict,"DESTINATION_CONTENT_CHANGED","目标文件已改变",imported);actual.Add(read.Track(reserved.TrackId,reserved.Role,false));}else actual.Add(reserved.Status(TrackState.Missing));
                }
                MediaGroup updated=imported.Replace(actual,imported.Game,imported.GameMissing,imported.Favorite,imported.Tags);Store(updated);groups[groups.IndexOf(imported)]=updated;Publish(groups,issues,updated);
                if(copied.Outcome!=MediaOutcome.Success)return Result(copied.Outcome,copied.Code,copied.Reason,updated.Tracks.Any(x=>x.State==TrackState.Present)?updated:null);
                if(updated.Tracks.Any(x=>x.State!=TrackState.Present))return Result(MediaOutcome.Conflict,"COPY_INCOMPLETE","复制尚未完成",updated);
                MediaRepositoryResult marked=source.CommitPlannedCopyAsync(plan,t).GetAwaiter().GetResult();if(marked.Outcome!=MediaOutcome.Success)return Result(marked.Outcome,marked.Code,marked.Reason,updated);return Publish(groups,issues,updated);
            },token);
        }
        string CopyPath(string id){return LibraryPathGuard.Journal(dataRoot,id,".thor-copy");}
        string CopyText(GroupOperationPlan plan,string phase,bool[] done)
        {string body="thor.group-copy/1\n"+plan.TransactionId+"\n"+plan.RepositoryId+"\n"+plan.DestinationRepositoryId+"\n"+plan.GroupId+"\n"+plan.ExpectedRevision+"\n"+phase+"\n"+Enc(MediaCodec.Encode(plan.SourceGroup))+"\n";for(int i=0;i<plan.Moves.Count;i++){PlannedMove move=plan.Moves[i];body+=Enc(move.Source)+"|"+Enc(move.Target)+"|"+move.Sha256+"|"+move.Length+"|"+move.TrackId+"|"+(done[i]?"1":"0")+"\n";}return body+Digest(body);}
        void PersistCopy(GroupOperationPlan plan,string phase,bool[] done)
        {string path=CopyPath(plan.TransactionId);LibraryPathGuard.AtomicJournal(path,CopyText(plan,phase,done),path+"."+Guid.NewGuid().ToString("N")+".tmp");}
        void ReadCopy(GroupOperationPlan plan,out string phase,out bool[] done)
        {string path=CopyPath(plan.TransactionId);FileInfo file=new FileInfo(path);if(file.Length>1024*1024||(file.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException();string text=File.ReadAllText(path,new UTF8Encoding(false,true));string[] lines=text.Split('\n');if(lines.Length!=9+plan.Moves.Count)throw new InvalidDataException();phase=lines[6];if(!new[]{"prepared","copying","copied","committed"}.Contains(phase))throw new InvalidDataException();done=new bool[plan.Moves.Count];for(int i=0;i<done.Length;i++){string[] fields=lines[i+8].Split('|');if(fields.Length!=6||(fields[5]!="1"&&fields[5]!="0"))throw new InvalidDataException();done[i]=fields[5]=="1";}if(text!=CopyText(plan,phase,done))throw new InvalidDataException();}
        internal Task<GroupOperationPlan> ReadCopyPlanAsync(MediaRepository destination,string id,CancellationToken token)
        {
            return Task.Run(()=>{token.ThrowIfCancellationRequested();string path=CopyPath(id);FileInfo file=new FileInfo(path);if(file.Length>1024*1024||(file.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException();string[] lines=File.ReadAllText(path,new UTF8Encoding(false,true)).Split('\n');if(lines.Length<10||lines[0]!="thor.group-copy/1"||lines[1]!=id||lines[2]!=GetSnapshot().RepositoryId||lines[3]!=destination.GetSnapshot().RepositoryId)throw new InvalidDataException();MediaGroup group=MediaCodec.Decode(Dec(lines[7]));long expected;if(!Int64.TryParse(lines[5],out expected)||group.GroupId!=lines[4]||group.Revision!=expected)throw new InvalidDataException();List<PlannedMove> moves=new List<PlannedMove>();for(int i=8;i<lines.Length-1;i++){string[] fields=lines[i].Split('|');long length;if(fields.Length!=6||!Int64.TryParse(fields[3],out length)||length<0)throw new InvalidDataException();string source=Dec(fields[0]),target=Dec(fields[1]);if(Path.GetDirectoryName(source)!=mediaRoot||Path.GetDirectoryName(target)!=destination.MediaRoot||!GroupOperations.Leaf(Path.GetFileName(source))||!GroupOperations.Leaf(Path.GetFileName(target)))throw new InvalidDataException();moves.Add(new PlannedMove(source,target,fields[2],length,fields[4]));}if(moves.Count<1||moves.Count>16||group.Tracks.Any(track=>moves.Count(x=>x.TrackId==track.TrackId)!=1))throw new InvalidDataException();GroupOperationPlan plan=new GroupOperationPlan(id,lines[2],group.GroupId,expected,"copy",moves,new string[0],group,lines[3]);string phase;bool[] done;ReadCopy(plan,out phase,out done);return plan;},token);
        }
        internal Task<MediaRepositoryResult> ExecutePlannedCopyAsync(GroupOperationPlan plan,CancellationToken token,IProgress<int> progress)
        {
            return Run(t=>{
                if(plan.RepositoryId!=repositoryId||plan.SourceGroup==null)return Result(MediaOutcome.Conflict,"COPY_SCOPE_CONFLICT","复制范围已改变",null);
                string phase="prepared";bool[] done=new bool[plan.Moves.Count];if(File.Exists(CopyPath(plan.TransactionId)))ReadCopy(plan,out phase,out done);
                if(phase=="committed")return Result(MediaOutcome.Success,"COPY_COMMITTED","",null);
                if(phase=="copied")return Result(MediaOutcome.Success,"COPY_STORED","",null);
                List<MediaIssue> issues=new List<MediaIssue>();List<MediaGroup> groups=Load(issues,t);MediaGroup group=groups.FirstOrDefault(x=>x.GroupId==plan.GroupId);
                if(issues.Count>0||group==null||group.Revision!=plan.ExpectedRevision)return Result(MediaOutcome.Conflict,"REVISION_CONFLICT","源媒体组已改变",group);
                List<FileStream> held=new List<FileStream>();try{
                    foreach(PlannedMove move in plan.Moves){t.ThrowIfCancellationRequested();if(Path.GetDirectoryName(move.Source)!=mediaRoot||(File.GetAttributes(move.Source)&FileAttributes.ReparsePoint)!=0||(File.GetAttributes(Path.GetDirectoryName(move.Target))&FileAttributes.ReparsePoint)!=0)return Result(MediaOutcome.Invalid,"INVALID_SCOPE","复制目录不安全",group);FileStream hold=File.Open(move.Source,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);held.Add(hold);if(hold.Length!=move.Length||GroupOperations.Hash(move.Source,t)!=move.Sha256)return Result(MediaOutcome.Conflict,"SOURCE_CHANGED","源指纹已改变",group);if(File.Exists(move.Target)&&(phase=="prepared"||GroupOperations.Hash(move.Target,t)!=move.Sha256))return Result(MediaOutcome.Conflict,"TARGET_EXISTS","目标未覆盖",group);}
                    PersistCopy(plan,"copying",done);for(int i=0;i<plan.Moves.Count;i++){
                        t.ThrowIfCancellationRequested();PlannedMove move=plan.Moves[i];if(!File.Exists(move.Target)){
                            string temporary=move.Target+".copy-"+plan.TransactionId+".partial";if(File.Exists(temporary)){if((File.GetAttributes(temporary)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException();File.Move(temporary,temporary+".interrupted-"+Guid.NewGuid().ToString("N"));}
                            PersistCopy(plan,"copying",done);using(FileStream output=File.Open(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){FileStream input=held[i];input.Position=0;byte[] buffer=new byte[65536];int count;while((count=input.Read(buffer,0,buffer.Length))>0){t.ThrowIfCancellationRequested();output.Write(buffer,0,count);}output.Flush(true);}if(GroupOperations.Hash(temporary,t)!=move.Sha256)throw new InvalidDataException();t.ThrowIfCancellationRequested();File.Move(temporary,move.Target);
                        }done[i]=true;PersistCopy(plan,"copying",done);if(progress!=null)progress.Report(i+1);
                    }PersistCopy(plan,"copied",done);return Result(MediaOutcome.Success,"COPY_STORED","原片全部保留",group);
                }finally{foreach(FileStream handle in held)handle.Dispose();}
            },token);
        }
        internal Task<MediaRepositoryResult> CommitPlannedCopyAsync(GroupOperationPlan plan,CancellationToken token)
        {return Run(t=>{string phase;bool[] done;ReadCopy(plan,out phase,out done);if(!done.All(x=>x))return Result(MediaOutcome.Conflict,"COPY_INCOMPLETE","复制未完成",null);t.ThrowIfCancellationRequested();PersistCopy(plan,"committed",done);return Result(MediaOutcome.Success,"COPY_COMMITTED","",null);},token);}
        sealed class RenameJournal{internal GroupOperationPlan Plan;internal string Phase;internal bool[] Done;}
        string JournalPath(string id){return LibraryPathGuard.Journal(dataRoot,id,".thor-rename");}
        static string Enc(string value){return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));}
        static string Dec(string value){return new UTF8Encoding(false,true).GetString(Convert.FromBase64String(value));}
        void WriteJournal(RenameJournal journal)
        {
            string path=JournalPath(journal.Plan.TransactionId);
            string body="thor.group-rename/1\n"+journal.Plan.TransactionId+"\n"+journal.Plan.RepositoryId+"\n"+journal.Plan.GroupId+"\n"+journal.Plan.ExpectedRevision.ToString(CultureInfo.InvariantCulture)+"\n"+journal.Phase+"\n"+Enc(MediaCodec.Encode(journal.Plan.SourceGroup))+"\n";
            for(int i=0;i<journal.Plan.Moves.Count;i++){PlannedMove move=journal.Plan.Moves[i];body+=Enc(Path.GetFileName(move.Source))+"|"+Enc(Path.GetFileName(move.Target))+"|"+move.Sha256+"|"+move.Length.ToString(CultureInfo.InvariantCulture)+"|"+move.TrackId+"|"+(journal.Done[i]?"1":"0")+"\n";}
            LibraryPathGuard.AtomicJournal(path,body+Digest(body),path+"."+Guid.NewGuid().ToString("N")+".tmp");
        }
        RenameJournal ReadJournal(string id)
        {
            string path=JournalPath(id);FileInfo info=new FileInfo(path);if(info.Length>1024*1024||(info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException();
            string text=File.ReadAllText(path,new UTF8Encoding(false,true));int last=text.LastIndexOf('\n');if(last<0||Digest(text.Substring(0,last+1))!=text.Substring(last+1))throw new InvalidDataException();
            string[] lines=text.Substring(0,last).Split('\n');if(lines.Length<8||!new[]{"prepared","applying","rolling-back","committed","rolled-back"}.Contains(lines[5])||lines[0]!="thor.group-rename/1"||lines[1]!=id||MediaCodec.Id(lines[2])!=repositoryId)throw new InvalidDataException();
            long revision;if(!Int64.TryParse(lines[4],NumberStyles.None,CultureInfo.InvariantCulture,out revision)||revision<1)throw new InvalidDataException();MediaCodec.Id(lines[3]);List<PlannedMove> moves=new List<PlannedMove>();List<bool> done=new List<bool>();
            for(int i=7;i<lines.Length;i++){string[] fields=lines[i].Split('|');long length;if(fields.Length!=6||!Int64.TryParse(fields[3],out length)||length<0||!System.Text.RegularExpressions.Regex.IsMatch(fields[2],"^[A-F0-9]{64}$")||(fields[5]!="0"&&fields[5]!="1"))throw new InvalidDataException();string source=Dec(fields[0]),target=Dec(fields[1]);if(!GroupOperations.Leaf(source)||!GroupOperations.Leaf(target))throw new InvalidDataException();if(fields[4].Length>0)MediaCodec.Id(fields[4]);moves.Add(new PlannedMove(Path.Combine(mediaRoot,source),Path.Combine(mediaRoot,target),fields[2],length,fields[4]));done.Add(fields[5]=="1");}
            if(moves.Count<1||moves.Count>16)throw new InvalidDataException();return new RenameJournal{Plan=new GroupOperationPlan(id,repositoryId,lines[3],revision,"rename",moves,new string[0],MediaCodec.Decode(Dec(lines[6]))),Phase=lines[5],Done=done.ToArray()};
        }
        internal Task<MediaRepositoryResult> ExecutePlannedRenameAsync(GroupOperationPlan plan,CancellationToken token,IProgress<int> progress)
        {return Run(t=>Rename(plan,null,false,t,progress),token);}
        internal Task<MediaRepositoryResult> RecoverPlannedRenameAsync(string id,bool rollback,CancellationToken token)
        {return Run(t=>{RenameJournal journal=ReadJournal(id);return Rename(journal.Plan,journal,rollback,t,null);},token);}
        MediaRepositoryResult Rename(GroupOperationPlan plan,RenameJournal journal,bool rollback,CancellationToken token,IProgress<int> progress)
        {
            List<MediaIssue> issues=new List<MediaIssue>();List<MediaGroup> groups=Load(issues,token);MediaGroup group=groups.FirstOrDefault(x=>x.GroupId==plan.GroupId);
            if(plan.RepositoryId!=repositoryId||group==null||issues.Count>0)return Result(MediaOutcome.Conflict,"TRANSACTION_SCOPE_CONFLICT","媒体库记录需审阅",null);
            bool metadataSame=plan.SourceGroup!=null&&group.Favorite==plan.SourceGroup.Favorite&&group.GameMissing==plan.SourceGroup.GameMissing&&group.Tags.SequenceEqual(plan.SourceGroup.Tags)&&(group.Game==null?"":group.Game.Key)==(plan.SourceGroup.Game==null?"":plan.SourceGroup.Game.Key);
            bool refreshedRecovery=journal!=null&&metadataSame&&group.Tracks.Count==plan.SourceGroup.Tracks.Count&&group.Tracks.All(track=>track.State!=TrackState.Changed&&plan.Moves.Any(move=>move.TrackId==track.TrackId&&move.Sha256==track.Sha256&&(Path.GetFileName(move.Source)==track.RelativePath||Path.GetFileName(move.Target)==track.RelativePath)));
            bool committed=(group.Revision==plan.ExpectedRevision+1||refreshedRecovery)&&group.Tracks.All(track=>plan.Moves.Any(move=>move.TrackId==track.TrackId&&Path.GetFileName(move.Target)==track.RelativePath));
            bool rolledBack=journal!=null&&(journal.Phase=="rolling-back"||journal.Phase=="rolled-back")&&(group.Revision==plan.ExpectedRevision+1||group.Revision==plan.ExpectedRevision+2)&&group.Tracks.All(track=>plan.Moves.Any(move=>move.TrackId==track.TrackId&&Path.GetFileName(move.Source)==track.RelativePath));
            if(rolledBack){journal.Phase="rolled-back";WriteJournal(journal);return Publish(groups,issues,group);}
            if(group.Revision!=plan.ExpectedRevision&&!committed&&!refreshedRecovery)return Result(MediaOutcome.Conflict,"REVISION_CONFLICT","媒体组已改变，请重新计划",group);
            if(!rollback&&committed){if(journal!=null){journal.Phase="committed";WriteJournal(journal);}return Publish(groups,issues,group);}
            if(journal==null){if(File.Exists(JournalPath(plan.TransactionId)))return Result(MediaOutcome.Conflict,"TRANSACTION_EXISTS","使用恢复入口处理既有事务",group);journal=new RenameJournal{Plan=plan,Phase="prepared",Done=new bool[plan.Moves.Count]};}
            List<FileStream> handles=new List<FileStream>();
            try
            {
                foreach(PlannedMove move in plan.Moves)
                {
                    token.ThrowIfCancellationRequested();if(!String.Equals(Path.GetDirectoryName(move.Source),mediaRoot,StringComparison.OrdinalIgnoreCase)||!String.Equals(Path.GetDirectoryName(move.Target),mediaRoot,StringComparison.OrdinalIgnoreCase))return Result(MediaOutcome.Invalid,"INVALID_SCOPE","仅支持同目录改名",group);
                    bool source=File.Exists(move.Source),target=File.Exists(move.Target);if(source==target)return Result(MediaOutcome.Conflict,"FILE_STATE_CONFLICT","源和目标状态不明确，原片未覆盖",group);
                    string current=source?move.Source:move.Target;if(journal.Phase=="prepared"&&journal.Done.All(x=>!x)&&target&&!rollback)return Result(MediaOutcome.Conflict,"TARGET_EXISTS","目标已存在",group);
                    if((File.GetAttributes(current)&(FileAttributes.ReparsePoint|FileAttributes.ReadOnly))!=0)return Result(MediaOutcome.Conflict,"SOURCE_UNAVAILABLE","源无法安全处理",group);
                    FileStream hold=File.Open(current,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);handles.Add(hold);
                    if(hold.Length!=move.Length||GroupOperations.Hash(current,token)!=move.Sha256)return Result(MediaOutcome.Conflict,"SOURCE_CHANGED","源指纹已改变",group);
                }
                journal.Phase=rollback?"rolling-back":"applying";WriteJournal(journal);
                for(int i=rollback?plan.Moves.Count-1:0;rollback?i>=0:i<plan.Moves.Count;i+=rollback?-1:1)
                {
                    token.ThrowIfCancellationRequested();PlannedMove move=plan.Moves[i];string source=rollback?move.Target:move.Source,target=rollback?move.Source:move.Target;
                    if(File.Exists(source)){WriteJournal(journal);File.Move(source,target);}journal.Done[i]=!rollback;WriteJournal(journal);if(progress!=null)progress.Report(i+1);
                }
                token.ThrowIfCancellationRequested();
                MediaTrack[] tracks=group.Tracks.Select(track=>{PlannedMove move=plan.Moves.Single(x=>x.TrackId==track.TrackId);MediaSource source=MediaSource.Read(rollback?move.Source:move.Target,token);return source.Track(track.TrackId,track.Role,false);}).ToArray();
                MediaGroup changed=group.Replace(tracks,group.Game,group.GameMissing,group.Favorite,group.Tags);Store(changed);groups[groups.FindIndex(x=>x.GroupId==group.GroupId)]=changed;
                journal.Phase=rollback?"rolled-back":"committed";WriteJournal(journal);return Publish(groups,issues,changed);
            }
            finally{foreach(FileStream handle in handles)handle.Dispose();}
        }
    }
}
