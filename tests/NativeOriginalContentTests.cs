using System;
using System.IO;
using System.Threading;
namespace ThorCapture
{
    internal static class NativeOriginalContentTests
    {
        private static NativeOriginalContent Content(byte[] bytes)
        {using(var stream=new MemoryStream(bytes))return NativeOriginalContent.Read(stream,bytes.Length,NativeOriginalContent.Digest(bytes),"image/png","instance","library",CancellationToken.None);}
        private static void Reject(Action action,string label)
        {bool rejected=false;try{action();}catch(NativeEditorException){rejected=true;}Tests.Check(rejected,"original content rejects "+label);}
        public static void Run()
        {
            byte[] bytes={1,2,3,4,5};var content=Content(bytes);var copy=content.Bytes;copy[0]=99;
            Tests.Check(content.ByteSize==5&&content.Bytes[0]==1,"original content retains a verified immutable byte snapshot");
            Reject(()=>{using(var s=new MemoryStream(bytes))NativeOriginalContent.Read(s,5,new string('a',64),"image/png","i","l",CancellationToken.None);},"mismatched body digest");
            Reject(()=>{using(var s=new MemoryStream(bytes))NativeOriginalContent.Read(s,6,content.Sha256,"image/png","i","l",CancellationToken.None);},"truncated body");
            Reject(()=>{using(var s=new MemoryStream(bytes))NativeOriginalContent.Read(s,4,content.Sha256,"image/png","i","l",CancellationToken.None);},"extra body bytes");
            Reject(()=>{using(var s=new MemoryStream(bytes))NativeOriginalContent.Read(s,5,content.Sha256,"text/html","i","l",CancellationToken.None);},"unsupported MIME");
            Reject(()=>{using(var s=new MemoryStream(bytes))NativeOriginalContent.Read(s,32L*1024*1024+1,content.Sha256,"image/png","i","l",CancellationToken.None);},"allocation above byte budget");
            string root=Path.Combine(Tests.Root,"original-export-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            string file=Path.Combine(root,"original.png");NativeOriginalFiles.Publish(content,file,CancellationToken.None);
            Tests.Check(NativeOriginalFiles.Matches(file,content),"original export atomically publishes hash-verified bytes");
            File.WriteAllText(file,"keep existing");Reject(()=>NativeOriginalFiles.Publish(content,file,CancellationToken.None),"existing destination");
            Tests.Check(File.ReadAllText(file)=="keep existing"&&!NativeOriginalFiles.Matches(file,content),"export preserves existing data and detects changed playback sources");
            using(var cancel=new CancellationTokenSource()){cancel.Cancel();bool cancelled=false;try{NativeOriginalFiles.Publish(content,Path.Combine(root,"cancelled.png"),cancel.Token);}catch(OperationCanceledException){cancelled=true;}Tests.Check(cancelled&&Directory.GetFiles(root).Length==1,"pre-cancelled export creates no partial or destination file");}
        }
    }
}
