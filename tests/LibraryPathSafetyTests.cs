using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Features.Library;
using ThorCapture.Media;

namespace ThorCapture
{
    internal static class LibraryPathSafetyTests
    {
        sealed class Provider:IReadOnlyMediaProvider
        {
            readonly Guid id=Guid.NewGuid();internal byte[] Bytes;internal RemoteMedia Item;internal int Reads;
            public Guid InstanceId{get{return id;}}public Task<ReadOnlyCollection<RemoteMedia>> ListAsync(string dir,CancellationToken token){return Task.FromResult(new ReadOnlyCollection<RemoteMedia>(new[]{Item}));}
            public Task<Stream> OpenReadAsync(RemoteMedia item,long offset,CancellationToken token){Reads++;return Task.FromResult<Stream>(new MemoryStream(Bytes,false));}
        }
        static string Folder(){string path=Path.Combine(Tests.Root,"path-safe-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
        static void Fixture(string path){if(!Path.GetFullPath(path).StartsWith(Path.GetFullPath(Tests.Root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Fixture path escaped test root");}
        static byte[] Png(){using(Bitmap bitmap=new Bitmap(12,12))using(MemoryStream stream=new MemoryStream()){bitmap.Save(stream,ImageFormat.Png);return stream.ToArray();}}
        static string Hash(byte[] bytes){using(SHA256 hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","");}
        static void Junction(string link,string target)
        {
            // mklink /J needs no PowerShell; the fixture paths are generated and contain no quotes.
            Fixture(link);Fixture(target);if(link.Contains("\"")||target.Contains("\""))throw new Exception("Unsafe junction fixture path");
            using(Process process=Process.Start(new ProcessStartInfo("cmd.exe","/d /c mklink /J \""+link+"\" \""+target+"\" >nul"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true}))
            {if(!process.WaitForExit(10000)||process.ExitCode!=0)throw new Exception("Temporary junction fixture failed");}
            if((File.GetAttributes(link)&FileAttributes.ReparsePoint)==0)throw new Exception("Fixture is not a real junction");
        }
        static void RemoveJunction(string link){Fixture(link);if((File.GetAttributes(link)&FileAttributes.ReparsePoint)==0)throw new Exception("Refusing to remove a normal fixture directory");Directory.Delete(link,false);}
        internal static void Run(){Task.Run((Func<Task>)RunAsync).GetAwaiter().GetResult();}
        static async Task RunAsync()
        {
            string media=Folder(),data=Folder(),external=Folder(),peerMedia=Folder(),peerData=Folder();string sentinel=Path.Combine(external,"sentinel.txt");File.WriteAllText(sentinel,"preserve-me");
            using(MediaRepository repo=new MediaRepository(media,data))using(MediaRepository peer=new MediaRepository(peerMedia,peerData))
            {
                await repo.RefreshAsync(CancellationToken.None);await peer.RefreshAsync(CancellationToken.None);string frame=Path.Combine(media,"frame.png");File.WriteAllBytes(frame,Png());MediaGroup group=(await repo.RegisterGroupAsync(new[]{new MediaTrackInput(frame,ScreenRole.Unknown)},CancellationToken.None)).Group;
                GroupOperationPlan rename=await GroupOperations.PlanRenameAsync(repo,group.GroupId,"renamed",CancellationToken.None),copy=await GroupOperations.PlanCopyAsync(repo,peer,group.GroupId,CancellationToken.None);
                Provider provider=new Provider{Bytes=Png()};provider.Item=new RemoteMedia("/sdcard/Pictures/Selected","import.png",provider.Bytes.Length,DateTime.UtcNow,Hash(provider.Bytes));ImportScan scan=await DeviceImport.ScanAsync(provider,new[]{provider.Item.Directory},CancellationToken.None);DeviceImportPlan import=DeviceImport.Plan(scan,provider.Item,repo);string transactions=Path.Combine(data,"transactions");Junction(transactions,external);
                try
                {
                    GroupOperationResult result=await GroupOperations.ExecuteAsync(repo,rename,true,CancellationToken.None);Tests.Check(result.Code=="LINKED_TRANSACTION_PATH"&&File.Exists(frame),"rename rejects transactions junction before any journal or original move");
                    result=await GroupOperations.ExecuteCopyAsync(repo,peer,copy,true,CancellationToken.None);Tests.Check(result.Code=="LINKED_TRANSACTION_PATH"&&peer.GetSnapshot().Groups.Count==0,"copy rejects source transactions junction before destination reservation");
                    result=await DeviceImport.ImportAsync(provider,repo,import,true,CancellationToken.None);Tests.Check(result.Outcome==LibraryOperationOutcome.Conflict&&provider.Reads==0&&!File.Exists(Path.Combine(media,"import.png")),"import rejects transactions junction before folder lock or remote stream creation");
                    result=await DeviceImport.ResumeAsync(provider,repo,import.TransactionId,true,CancellationToken.None);Tests.Check(result.Code=="IMPORT_JOURNAL_INVALID"&&provider.Reads==0,"import resume refuses a linked log directory before reading or contacting provider");
                    result=await GroupOperations.RecoverAsync(repo,rename.TransactionId,false,true,CancellationToken.None);Tests.Check(result.Code=="LINKED_TRANSACTION_PATH","rename recovery refuses linked private transaction directory");
                    result=await GroupOperations.RecoverCopyAsync(repo,peer,copy.TransactionId,true,CancellationToken.None);Tests.Check(result.Code=="COPY_JOURNAL_INVALID","copy recovery refuses linked journal before reading another directory");
                    Tests.Check(Directory.GetFileSystemEntries(external).SequenceEqual(new[]{sentinel})&&File.ReadAllText(sentinel)=="preserve-me","all operations leave exactly zero outside writes through a real transactions junction");
                }
                finally{RemoveJunction(transactions);}
                Directory.CreateDirectory(transactions);
                string renameLog=Path.Combine(transactions,rename.TransactionId+".thor-rename"),copyLog=Path.Combine(transactions,copy.TransactionId+".thor-copy"),importLog=Path.Combine(transactions,import.TransactionId+".thor-import");
                Junction(renameLog,external);try{Tests.Check((await GroupOperations.ExecuteAsync(repo,rename,true,CancellationToken.None)).Code=="LINKED_TRANSACTION_PATH","main rename journal junction is rejected before writing");}finally{RemoveJunction(renameLog);}
                Junction(copyLog+".bak",external);try{Tests.Check((await GroupOperations.ExecuteCopyAsync(repo,peer,copy,true,CancellationToken.None)).Code=="LINKED_TRANSACTION_PATH","copy backup junction is rejected before writing main log or destination metadata");}finally{RemoveJunction(copyLog+".bak");}
                Junction(importLog+".bak",external);try{Tests.Check((await DeviceImport.ImportAsync(provider,repo,import,true,CancellationToken.None)).Outcome==LibraryOperationOutcome.Conflict&&provider.Reads==0,"import backup junction is rejected before temporary file or stream creation");}finally{RemoveJunction(importLog+".bak");}
                Junction(importLog+".lock",external);try{Tests.Check((await DeviceImport.ImportAsync(provider,repo,import,true,CancellationToken.None)).Outcome==LibraryOperationOutcome.Conflict,"import lock junction is rejected before OpenOrCreate");}finally{RemoveJunction(importLog+".lock");}
                Tests.Check(Directory.GetFileSystemEntries(external).SequenceEqual(new[]{sentinel})&&Directory.GetFileSystemEntries(transactions).Length==0,"main backup and lock junction fixtures create no outside files or leaked journal temporaries");
                string temp=renameLog+".fixed.tmp";File.WriteAllText(temp,"unowned-original");Tests.Reject(()=>LibraryPathGuard.AtomicJournal(renameLog,"new",temp),"CreateNew collision produces a journal failure");Tests.Check(File.ReadAllText(temp)=="unowned-original"&&!File.Exists(renameLog),"failed CreateNew cleanup never removes or overwrites the preexisting temporary file");
                string metadata=Path.Combine(media,"fixed-metadata.txt"),metadataTemp=metadata+".new.tmp";File.WriteAllText(metadata,"preserve-metadata");Tests.Reject(()=>LibraryPathGuard.AtomicJournal(metadata,"replacement",metadataTemp,false),"strict no-overwrite metadata publication rejects an existing target");Tests.Check(File.ReadAllText(metadata)=="preserve-metadata"&&!File.Exists(metadataTemp),"no-overwrite metadata failure preserves the original and cleans only its owned temporary");
                string ancestor=Folder(),real=ancestor+".real",ancestorExternal=Folder();Fixture(real);Directory.Move(ancestor,real);Junction(ancestor,ancestorExternal);try{Tests.Reject(()=>LibraryPathGuard.Journal(Path.Combine(ancestor,"private"),Guid.NewGuid().ToString("N"),".thor-copy"),"journal rejects a junction ancestor above the private data root");Tests.Check(Directory.GetFileSystemEntries(ancestorExternal).Length==0,"ancestor junction rejection leaves external target entirely untouched");}finally{RemoveJunction(ancestor);}
            }
            string catalogAncestor=Folder(),catalogReal=catalogAncestor+".real",catalogMedia=Folder(),catalogData=Path.Combine(catalogAncestor,"private"),catalogExternal=Folder();Directory.CreateDirectory(catalogData);
            using(MediaRepository catalogRepo=new MediaRepository(catalogMedia,catalogData))
            {
                await catalogRepo.RefreshAsync(CancellationToken.None);string id=catalogRepo.GetSnapshot().RepositoryId,outsideData=Path.Combine(catalogExternal,"private"),outsideFolder=Path.Combine(outsideData,"transactions");Directory.CreateDirectory(outsideFolder);File.Copy(Path.Combine(catalogData,"repository.thor-root"),Path.Combine(outsideData,"repository.thor-root"));string outsideLog=Path.Combine(outsideFolder,Guid.NewGuid().ToString("N")+".thor-import");File.WriteAllText(outsideLog,"outside-private-log");byte[] outsideBefore=File.ReadAllBytes(outsideLog);Fixture(catalogReal);Directory.Move(catalogAncestor,catalogReal);Junction(catalogAncestor,catalogExternal);
                try{RecoveryCatalogResult listed=await RecoveryCatalog.ScanAsync(catalogRepo,id,CancellationToken.None);Tests.Check(listed.Code=="RECOVERY_ROOT_INVALID"&&listed.Entries.Count==0&&listed.InspectedCount==0,"recovery catalog rejects an ancestor junction before reading any outside log");Tests.Check(File.ReadAllBytes(outsideLog).SequenceEqual(outsideBefore)&&Directory.GetFiles(outsideFolder).Length==1,"recovery ancestor rejection keeps outside log bytes and directory unchanged");}finally{RemoveJunction(catalogAncestor);}
            }
        }
    }
}
