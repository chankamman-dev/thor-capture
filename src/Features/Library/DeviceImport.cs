using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Media;

namespace ThorCapture.Features.Library
{
    internal sealed class RemoteMedia
    {
        public readonly string Directory,Name,Sha256;
        public readonly long Length;
        public readonly DateTime ModifiedUtc;
        public RemoteMedia(string directory,string name,long length,DateTime modified,string sha)
        {if(!DeviceImport.DirectoryAllowed(directory)||!GroupOperations.Leaf(name)||!MediaSource.Supported(name)||length<1||length>1024L*1024*1024||modified.Kind!=DateTimeKind.Utc||!Regex.IsMatch(sha??"","^[A-Fa-f0-9]{64}$"))throw new ArgumentException("Invalid read-only remote descriptor");Directory=directory;Name=name;Length=length;ModifiedUtc=modified;Sha256=sha.ToUpperInvariant();}
    }
    internal interface IReadOnlyMediaProvider
    {
        Guid InstanceId { get; }
        Task<ReadOnlyCollection<RemoteMedia>> ListAsync(string selectedDirectory,CancellationToken token);
        Task<Stream> OpenReadAsync(RemoteMedia selected,long offset,CancellationToken token);
    }
    internal sealed class ImportScan
    {
        public readonly Guid ProviderId;
        public readonly LibraryOperationOutcome Outcome;
        public readonly ReadOnlyCollection<RemoteMedia> Items;
        internal ImportScan(Guid provider,LibraryOperationOutcome outcome,IEnumerable<RemoteMedia> items){ProviderId=provider;Outcome=outcome;Items=new ReadOnlyCollection<RemoteMedia>(items.ToArray());}
    }
    internal sealed class DeviceImportPlan
    {
        public readonly string TransactionId,RepositoryId,MediaRoot,TargetName;
        public readonly Guid ProviderId;
        public readonly RemoteMedia Source;
        internal DeviceImportPlan(string id,string repository,string root,string target,Guid provider,RemoteMedia source){TransactionId=id;RepositoryId=repository;MediaRoot=root;TargetName=target;ProviderId=provider;Source=source;}
    }
    internal static class DeviceImport
    {
        static async Task<T> ProviderWait<T>(Task<T> pending,CancellationToken token)
        {
            using(CancellationTokenSource clock=CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task timer=Task.Delay(3000,clock.Token);if(await Task.WhenAny(pending,timer).ConfigureAwait(false)!=pending)
                {Task cleanup=pending.ContinueWith(task=>{if(task.Status==TaskStatus.RanToCompletion){IDisposable resource=task.Result as IDisposable;if(resource!=null)resource.Dispose();}else if(task.IsFaulted){var ignored=task.Exception;}},TaskScheduler.Default);GC.KeepAlive(cleanup);token.ThrowIfCancellationRequested();throw new TimeoutException();}
                clock.Cancel();return await pending.ConfigureAwait(false);
            }
        }
        internal static bool DirectoryAllowed(string directory)
        {return directory!=null&&directory.StartsWith("/sdcard/",StringComparison.Ordinal)&&directory.Length<=512&&!directory.EndsWith("/")&&!directory.Contains("//")&&!directory.Split('/').Any(x=>x=="."||x=="..")&&!directory.Any(x=>Char.IsControl(x)||x=='*'||x=='?'||x=='\\');}
        public static async Task<ImportScan> ScanAsync(IReadOnlyMediaProvider provider,IEnumerable<string> selectedDirectories,CancellationToken token)
        {
            if(provider==null||provider.InstanceId==Guid.Empty)throw new ArgumentException("Invalid fixed provider");string[] directories=selectedDirectories.Distinct(StringComparer.Ordinal).ToArray();if(directories.Length<1||directories.Length>8||directories.Any(x=>!DirectoryAllowed(x)))throw new ArgumentException("Explicit selected directories required");
            Guid fixedId=provider.InstanceId;List<RemoteMedia> items=new List<RemoteMedia>();try{foreach(string directory in directories){token.ThrowIfCancellationRequested();ReadOnlyCollection<RemoteMedia> listed=await ProviderWait(provider.ListAsync(directory,token),token).ConfigureAwait(false);if(provider.InstanceId!=fixedId||listed==null||listed.Count>2000||listed.Any(x=>x==null||x.Directory!=directory))return new ImportScan(fixedId,LibraryOperationOutcome.Conflict,items);items.AddRange(listed);if(items.Count>5000)return new ImportScan(fixedId,LibraryOperationOutcome.Conflict,new RemoteMedia[0]);}return new ImportScan(fixedId,LibraryOperationOutcome.Success,items);}catch(OperationCanceledException){return new ImportScan(fixedId,LibraryOperationOutcome.Cancelled,items);}catch(TimeoutException){return new ImportScan(fixedId,LibraryOperationOutcome.TimedOut,items);}catch{return new ImportScan(fixedId,LibraryOperationOutcome.Failed,items);}
        }
        public static DeviceImportPlan Plan(ImportScan scan,RemoteMedia selected,MediaRepository repository,string targetName=null)
        {
            if(scan==null||scan.Outcome!=LibraryOperationOutcome.Success||!scan.Items.Contains(selected)||repository==null)throw new ArgumentException("Select a scanned source");string name=targetName??selected.Name;if(!GroupOperations.Leaf(name)||!MediaSource.Supported(name)||!Path.GetExtension(name).Equals(Path.GetExtension(selected.Name),StringComparison.OrdinalIgnoreCase)||repository.GetSnapshot().RepositoryId.Length==0)throw new ArgumentException("Invalid target or uninitialized repository");
            return new DeviceImportPlan(Guid.NewGuid().ToString("N"),repository.GetSnapshot().RepositoryId,repository.MediaRoot,name,scan.ProviderId,selected);
        }
        private static string HashText(string value){using(SHA256 sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","");}
        private static string Body(DeviceImportPlan plan,long bytes,string phase)
        {return "thor.device-import/1\n"+plan.TransactionId+"\n"+plan.RepositoryId+"\n"+plan.ProviderId.ToString("D")+"\n"+Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.Source.Directory+"/"+plan.Source.Name))+"\n"+Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.MediaRoot))+"\n"+Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.TargetName))+"\n"+plan.Source.Sha256+"\n"+plan.Source.Length.ToString(CultureInfo.InvariantCulture)+"\n"+plan.Source.ModifiedUtc.ToString("o",CultureInfo.InvariantCulture)+"\n"+bytes.ToString(CultureInfo.InvariantCulture)+"\n"+phase+"\n";}
        private static void Persist(string path,DeviceImportPlan plan,long bytes,string phase)
        {LibraryPathGuard.RootsForImport(plan.MediaRoot,path);string body=Body(plan,bytes,phase);LibraryPathGuard.AtomicJournal(path,body+HashText(body),path+"."+Guid.NewGuid().ToString("N")+".tmp");}
        private static void Read(string path,DeviceImportPlan plan,out long bytes,out string phase)
        {LibraryPathGuard.NoLinks(path);LibraryPathGuard.NoLinks(path+".bak");FileInfo info=new FileInfo(path);if(info.Length>16384||(info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException();string text=File.ReadAllText(path,new UTF8Encoding(false,true));string[] lines=text.Split('\n');if(lines.Length!=13||!Int64.TryParse(lines[10],NumberStyles.None,CultureInfo.InvariantCulture,out bytes)||bytes<0||bytes>plan.Source.Length)throw new InvalidDataException();phase=lines[11];string body=Body(plan,bytes,phase);if(text!=body+HashText(body)||!new[]{"prepared","copying","verified","stored","committed"}.Contains(phase))throw new InvalidDataException();}
        static void SaveImportMetadata(string target,DateTime capturedUtc)
        {
            string sidecar=CaptureMetadata.Sidecar(target),text="version=1\npackage=\nstatus=device-import\nserial=\ndisplayId=0\nphysicalId=\ncapturedUtc="+capturedUtc.ToString("o",CultureInfo.InvariantCulture)+"\nforegroundObservedUtc="+DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)+"\n";
            LibraryPathGuard.AtomicJournal(sidecar,text,sidecar+"."+Guid.NewGuid().ToString("N")+".tmp",false);
        }
        public static async Task<GroupOperationResult> ResumeAsync(IReadOnlyMediaProvider provider,MediaRepository repository,string transactionId,bool confirmed,CancellationToken token)
        {
            if(!confirmed)return new GroupOperationResult(LibraryOperationOutcome.ConfirmationRequired,"CONFIRMATION_REQUIRED",transactionId,null);
            try
            {
                DeviceImportPlan plan=await Task.Run(()=>{token.ThrowIfCancellationRequested();MediaCodec.Id(transactionId);LibraryPathGuard.Roots(repository);string path=LibraryPathGuard.Journal(repository.DataRoot,transactionId,".thor-import");FileInfo file=new FileInfo(path);if(file.Length>16384||(file.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException();string[] lines=File.ReadAllText(path,new UTF8Encoding(false,true)).Split('\n');if(lines.Length!=13||lines[0]!="thor.device-import/1"||lines[1]!=transactionId||lines[2]!=repository.GetSnapshot().RepositoryId)throw new InvalidDataException();Guid providerId;if(!Guid.TryParse(lines[3],out providerId)||providerId!=provider.InstanceId)throw new InvalidDataException();string source=new UTF8Encoding(false,true).GetString(Convert.FromBase64String(lines[4]));int slash=source.LastIndexOf('/');string root=new UTF8Encoding(false,true).GetString(Convert.FromBase64String(lines[5])),target=new UTF8Encoding(false,true).GetString(Convert.FromBase64String(lines[6]));long length;DateTime modified;if(slash<0||root!=repository.MediaRoot||!GroupOperations.Leaf(target)||!Int64.TryParse(lines[8],out length)||!DateTime.TryParseExact(lines[9],"o",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out modified))throw new InvalidDataException();DeviceImportPlan restored=new DeviceImportPlan(transactionId,lines[2],root,target,providerId,new RemoteMedia(source.Substring(0,slash),source.Substring(slash+1),length,modified,lines[7]));long bytes;string phase;Read(path,restored,out bytes,out phase);return restored;},token).ConfigureAwait(false);
                return await ImportAsync(provider,repository,plan,true,token).ConfigureAwait(false);
            }
            catch(OperationCanceledException){return new GroupOperationResult(LibraryOperationOutcome.Cancelled,"IMPORT_CANCELLED",transactionId,null);}
            catch{return new GroupOperationResult(LibraryOperationOutcome.Conflict,"IMPORT_JOURNAL_INVALID",transactionId,null);}
        }
        public static Task<GroupOperationResult> ImportAsync(IReadOnlyMediaProvider provider,MediaRepository repository,DeviceImportPlan plan,bool confirmed,CancellationToken token,IProgress<long> progress=null)
        {
            if(!confirmed)return Task.FromResult(new GroupOperationResult(LibraryOperationOutcome.ConfirmationRequired,"CONFIRMATION_REQUIRED",plan.TransactionId,null));
            if(token.IsCancellationRequested)return Task.FromResult(new GroupOperationResult(LibraryOperationOutcome.Cancelled,"IMPORT_CANCELLED",plan.TransactionId,null));
            if(provider==null||provider.InstanceId!=plan.ProviderId||repository.MediaRoot!=plan.MediaRoot||repository.GetSnapshot().RepositoryId!=plan.RepositoryId)return Task.FromResult(new GroupOperationResult(LibraryOperationOutcome.Conflict,"IMPORT_SCOPE_CHANGED",plan.TransactionId,null));
            return Task.Run(async()=>{
                string journal="",target=Path.Combine(repository.MediaRoot,plan.TargetName),partial=target+".import-"+plan.TransactionId+".partial";FileStream lease=null;
                long written=0;string phase="prepared";
                try
                {
                    LibraryPathGuard.Roots(repository);journal=LibraryPathGuard.Journal(repository.DataRoot,plan.TransactionId,".thor-import");LibraryPathGuard.NoLinks(target);LibraryPathGuard.NoLinks(partial);LibraryPathGuard.NoLinks(CaptureMetadata.Sidecar(target));Directory.CreateDirectory(Path.GetDirectoryName(journal));LibraryPathGuard.Journal(repository.DataRoot,plan.TransactionId,".thor-import");
                    Stopwatch deadline=Stopwatch.StartNew();for(;;){token.ThrowIfCancellationRequested();LibraryPathGuard.NoLinks(journal+".lock");try{lease=File.Open(journal+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);break;}catch(IOException){if(deadline.ElapsedMilliseconds>=3000)return new GroupOperationResult(LibraryOperationOutcome.TimedOut,"IMPORT_BUSY",plan.TransactionId,null);}await Task.Delay(25,token).ConfigureAwait(false);}
                    if((File.GetAttributes(repository.MediaRoot)&FileAttributes.ReparsePoint)!=0)return new GroupOperationResult(LibraryOperationOutcome.Conflict,"REPARSE_ROOT",plan.TransactionId,null);
                    if(File.Exists(journal))Read(journal,plan,out written,out phase);
                    else
                    {
                        if(File.Exists(target)||File.Exists(partial)||File.Exists(CaptureMetadata.Sidecar(target)))return new GroupOperationResult(LibraryOperationOutcome.Conflict,"TARGET_EXISTS",plan.TransactionId,null);
                        if(repository.GetSnapshot().Groups.SelectMany(x=>x.Tracks).Any(x=>x.Sha256==plan.Source.Sha256))return new GroupOperationResult(LibraryOperationOutcome.Conflict,"DUPLICATE_CONTENT",plan.TransactionId,null);
                        Persist(journal,plan,0,phase);
                    }
                    if(phase=="prepared"||phase=="copying")
                    {
                        if(File.Exists(target)){if(written!=plan.Source.Length||GroupOperations.Hash(target,token)!=plan.Source.Sha256)throw new InvalidDataException();phase="stored";Persist(journal,plan,written,phase);}
                        else
                        {
                            if(File.Exists(partial)&&(File.GetAttributes(partial)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException();
                            using(FileStream output=File.Open(partial,phase=="prepared"?FileMode.CreateNew:FileMode.Open,FileAccess.Write,FileShare.None))
                            {
                                if(output.Length<written)throw new InvalidDataException();output.SetLength(written);output.Position=written;Persist(journal,plan,written,"copying");phase="copying";
                                using(Stream input=await ProviderWait(provider.OpenReadAsync(plan.Source,written,token),token).ConfigureAwait(false))
                                {byte[] buffer=new byte[65536];while(written<plan.Source.Length){token.ThrowIfCancellationRequested();int count=await ProviderWait(input.ReadAsync(buffer,0,(int)Math.Min(buffer.Length,plan.Source.Length-written),token),token).ConfigureAwait(false);if(count==0)throw new IOException("Remote source incomplete");output.Write(buffer,0,count);output.Flush(true);written+=count;Persist(journal,plan,written,"copying");if(progress!=null)progress.Report(written);}if(await ProviderWait(input.ReadAsync(buffer,0,1,token),token).ConfigureAwait(false)!=0)throw new InvalidDataException();}
                            }
                            if(GroupOperations.Hash(partial,token)!=plan.Source.Sha256)return new GroupOperationResult(LibraryOperationOutcome.Conflict,"SOURCE_HASH_MISMATCH",plan.TransactionId,null);
                            token.ThrowIfCancellationRequested();Persist(journal,plan,written,"verified");phase="verified";
                        }
                    }
                    if(provider.InstanceId!=plan.ProviderId)return new GroupOperationResult(LibraryOperationOutcome.Conflict,"IMPORT_SCOPE_CHANGED",plan.TransactionId,null);
                    if(phase=="verified")
                    {token.ThrowIfCancellationRequested();LibraryPathGuard.NoLinks(target);LibraryPathGuard.NoLinks(partial);if(!File.Exists(target)){if(GroupOperations.Hash(partial,token)!=plan.Source.Sha256)throw new InvalidDataException();File.Move(partial,target);}else if(GroupOperations.Hash(target,token)!=plan.Source.Sha256)throw new InvalidDataException();Persist(journal,plan,written,"stored");phase="stored";}
                    if(GroupOperations.Hash(target,token)!=plan.Source.Sha256)throw new InvalidDataException();
                    LibraryPathGuard.NoLinks(CaptureMetadata.Sidecar(target));if(!File.Exists(CaptureMetadata.Sidecar(target)))SaveImportMetadata(target,plan.Source.ModifiedUtc);
                    else {CaptureMetadata metadata=CaptureMetadata.Load(target);if(metadata==null||metadata.Status!="device-import"||metadata.Serial.Length!=0||metadata.CapturedUtc!=plan.Source.ModifiedUtc)return new GroupOperationResult(LibraryOperationOutcome.Conflict,"IMPORT_METADATA_CONFLICT",plan.TransactionId,null);}
                    MediaRepositoryResult registered=await repository.RegisterGroupAsync(new[]{new MediaTrackInput(target,ScreenRole.Unknown)},token).ConfigureAwait(false);
                    if(registered.Outcome!=MediaOutcome.Success)return GroupOperationResult.From(registered,plan.TransactionId);
                    Persist(journal,plan,written,"committed");return GroupOperationResult.From(registered,plan.TransactionId);
                }
                catch(OperationCanceledException){return new GroupOperationResult(LibraryOperationOutcome.Cancelled,"IMPORT_CANCELLED",plan.TransactionId,null);}
                catch(InvalidDataException){return new GroupOperationResult(LibraryOperationOutcome.Conflict,"IMPORT_INTEGRITY_CONFLICT",plan.TransactionId,null);}
                catch(TimeoutException){return new GroupOperationResult(LibraryOperationOutcome.TimedOut,"IMPORT_PROVIDER_TIMEOUT",plan.TransactionId,null);}
                catch{ return new GroupOperationResult(LibraryOperationOutcome.Failed,"IMPORT_READ_FAILED",plan.TransactionId,null); }
                finally{if(lease!=null)lease.Dispose();}
            });
        }
    }
}
