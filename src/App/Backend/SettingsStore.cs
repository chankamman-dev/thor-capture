using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Backend
{
    internal enum SettingsOutcome { Success, Missing, Migrated, Recovered, Corrupt, UnsupportedSchema, Cancelled, FileFailure, TimedOut }
    internal sealed class SettingsSnapshot
    {
        private readonly Dictionary<string,string> values;
        public readonly int SchemaVersion;
        internal SettingsSnapshot(IDictionary<string,string> values,int schema) { this.values=new Dictionary<string,string>(values,StringComparer.Ordinal); SchemaVersion=schema; }
        public string Get(string key,string fallback) { string value; return values.TryGetValue(key,out value)?value:fallback; }
        public int GetInt(string key,int fallback,int minimum,int maximum)
        { int value; return int.TryParse(Get(key,""),NumberStyles.Integer,CultureInfo.InvariantCulture,out value)&&value>=minimum&&value<=maximum?value:fallback; }
        public bool GetBool(string key,bool fallback) { string value=Get(key,""); return value=="1"?true:value=="0"?false:fallback; }
        public Dictionary<string,string> CopyValues() { return new Dictionary<string,string>(values,StringComparer.Ordinal); }
    }
    internal sealed class SettingsResult
    {
        public readonly SettingsOutcome Outcome;
        public readonly SettingsSnapshot Snapshot;
        public readonly string Code;
        public SettingsResult(SettingsOutcome outcome,SettingsSnapshot snapshot,string code) { Outcome=outcome; Snapshot=snapshot; Code=code; }
    }
    internal sealed class SettingsStore
    {
        private readonly string path;
        private readonly SemaphoreSlim serial=new SemaphoreSlim(1,1);
        private readonly object gate=new object();
        private SettingsSnapshot snapshot=new SettingsSnapshot(new Dictionary<string,string>(),1);
        public SettingsStore(string path) { this.path=Path.GetFullPath(path); }
        public SettingsSnapshot Snapshot { get { lock(gate) return snapshot; } }
        public Task<SettingsResult> LoadAsync(string legacyPath,CancellationToken token)
        { return ExecuteAsync(delegate {
            if(File.Exists(path))
            {
                SettingsResult current=Read(path,false);
                if(current.Outcome==SettingsOutcome.Corrupt && File.Exists(path+".bak"))
                {
                    SettingsResult backup=Read(path+".bak",false);
                    if(backup.Outcome==SettingsOutcome.Success) current=new SettingsResult(SettingsOutcome.Recovered,backup.Snapshot,"RECOVERED_BACKUP");
                }
                return current;
            }
            if(!string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath))
            {
                SettingsResult old=Read(legacyPath,true); if(old.Outcome!=SettingsOutcome.Success) return old;
                using(FileStream ownership=AcquireLock(token)) { token.ThrowIfCancellationRequested(); if(File.Exists(path)) return Read(path,false); Commit(old.Snapshot.CopyValues(),false); }
                return new SettingsResult(SettingsOutcome.Migrated,old.Snapshot,"MIGRATED_LEGACY");
            }
            return new SettingsResult(SettingsOutcome.Missing,new SettingsSnapshot(new Dictionary<string,string>(),1),"MISSING");
        },token); }
        public Task<SettingsResult> SaveAsync(IDictionary<string,string> updates,CancellationToken token)
        {
            if(updates==null) throw new ArgumentNullException("updates");
            Dictionary<string,string> frozen=new Dictionary<string,string>(updates,StringComparer.Ordinal);
            foreach(KeyValuePair<string,string> pair in frozen) Validate(pair.Key,pair.Value);
            return ExecuteAsync(delegate {
                using(FileStream ownership=AcquireLock(token))
                {
                    Dictionary<string,string> values=Snapshot.CopyValues(); bool recovering=false;
                    if(File.Exists(path))
                    {
                        SettingsResult latest=Read(path,false);
                        if(latest.Outcome==SettingsOutcome.Corrupt && File.Exists(path+".bak"))
                        { latest=Read(path+".bak",false); recovering=latest.Outcome==SettingsOutcome.Success; }
                        if(latest.Outcome!=SettingsOutcome.Success) return latest;
                        values=latest.Snapshot.CopyValues();
                    }
                    foreach(KeyValuePair<string,string> pair in frozen) values[pair.Key]=pair.Value;
                    token.ThrowIfCancellationRequested(); Commit(values,recovering);
                    return new SettingsResult(SettingsOutcome.Success,new SettingsSnapshot(values,1),"SAVED");
                }
            },token);
        }
        private async Task<SettingsResult> ExecuteAsync(Func<SettingsResult> work,CancellationToken token)
        {
            bool acquired=false;
            try
            {
                await serial.WaitAsync(token).ConfigureAwait(false); acquired=true;
                SettingsResult result=await Task.Run(work,token).ConfigureAwait(false);
                if(result.Outcome==SettingsOutcome.Success||result.Outcome==SettingsOutcome.Migrated||result.Outcome==SettingsOutcome.Recovered||result.Outcome==SettingsOutcome.Missing)
                    lock(gate) snapshot=result.Snapshot;
                return result;
            }
            catch(OperationCanceledException) { return new SettingsResult(SettingsOutcome.Cancelled,Snapshot,"CANCELLED"); }
            catch(TimeoutException) { return new SettingsResult(SettingsOutcome.TimedOut,Snapshot,"STORE_BUSY"); }
            catch(IOException) { return new SettingsResult(SettingsOutcome.FileFailure,Snapshot,"FILE_FAILURE"); }
            catch(UnauthorizedAccessException) { return new SettingsResult(SettingsOutcome.FileFailure,Snapshot,"ACCESS_DENIED"); }
            finally { if(acquired) serial.Release(); }
        }
        private FileStream AcquireLock(CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            System.Diagnostics.Stopwatch elapsed=System.Diagnostics.Stopwatch.StartNew();
            while(true)
            {
                token.ThrowIfCancellationRequested();
                try { return new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
                catch(IOException) { if(elapsed.Elapsed>TimeSpan.FromSeconds(3)) throw new TimeoutException(); token.WaitHandle.WaitOne(25); }
            }
        }
        private static void Validate(string key,string value)
        {
            if(string.IsNullOrEmpty(key)||key.Length>256||key=="schemaVersion"||key=="checksumSha256"||key.IndexOfAny(new char[]{'\r','\n','=','\0'})>=0||value==null||value.Length>32768||value.IndexOf('\0')>=0)
                throw new ArgumentException("Invalid setting");
        }
        private static SettingsResult Read(string file,bool legacy)
        {
            try
            {
                if(new FileInfo(file).Length>2097152) return new SettingsResult(SettingsOutcome.Corrupt,null,"TOO_LARGE");
                string content=new UTF8Encoding(false,true).GetString(File.ReadAllBytes(file)).TrimStart('\uFEFF');
                if(!legacy && content.StartsWith("schemaVersion=",StringComparison.Ordinal))
                {
                    int end=content.IndexOf('\n'); string version=(end<0?content.Substring(14):content.Substring(14,end-14)).TrimEnd('\r');
                    if(version!="1") return new SettingsResult(SettingsOutcome.UnsupportedSchema,null,"UNSUPPORTED_SCHEMA");
                }
                if(!legacy)
                {
                    int footer=content.LastIndexOf("checksumSha256=",StringComparison.Ordinal);
                    if(footer<0 || footer>0&&content[footer-1]!='\n' || !content.EndsWith("\n",StringComparison.Ordinal)) return new SettingsResult(SettingsOutcome.Corrupt,null,"MISSING_CHECKSUM");
                    string digest=content.Substring(footer+15).TrimEnd('\r','\n');
                    if(!string.Equals(Hash(content.Substring(0,footer)),digest,StringComparison.OrdinalIgnoreCase)) return new SettingsResult(SettingsOutcome.Corrupt,null,"CHECKSUM_MISMATCH");
                    content=content.Substring(0,footer);
                }
                Dictionary<string,string> values=new Dictionary<string,string>(StringComparer.Ordinal);
                bool schema=legacy;
                foreach(string line in content.Split('\n'))
                {
                    string text=line.TrimEnd('\r'); if(text.Length==0) continue;
                    int equal=text.IndexOf('='); if(equal<1) return new SettingsResult(SettingsOutcome.Corrupt,null,"INVALID_LINE");
                    string key=text.Substring(0,equal),value=text.Substring(equal+1);
                    if(!legacy&&key=="schemaVersion") { if(schema) return new SettingsResult(SettingsOutcome.Corrupt,null,"DUPLICATE_SCHEMA"); if(value!="1") return new SettingsResult(SettingsOutcome.UnsupportedSchema,null,"UNSUPPORTED_SCHEMA"); schema=true; continue; }
                    if(!legacy) value=Uri.UnescapeDataString(value);
                    Validate(key,value); if(values.ContainsKey(key)&&!legacy) return new SettingsResult(SettingsOutcome.Corrupt,null,"DUPLICATE_KEY");
                    values[key]=value;
                }
                if(!schema) return new SettingsResult(SettingsOutcome.Corrupt,null,"MISSING_SCHEMA");
                return new SettingsResult(SettingsOutcome.Success,new SettingsSnapshot(values,1),"READ");
            }
            catch(DecoderFallbackException) { return new SettingsResult(SettingsOutcome.Corrupt,null,"INVALID_UTF8"); }
            catch(ArgumentException) { return new SettingsResult(SettingsOutcome.Corrupt,null,"INVALID_SETTING"); }
        }
        private void Commit(Dictionary<string,string> values,bool recovering)
        {
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                StringBuilder text=new StringBuilder("schemaVersion=1\n");
                List<string> keys=new List<string>(values.Keys); keys.Sort(StringComparer.Ordinal);
                foreach(string key in keys) text.Append(key).Append('=').Append(Uri.EscapeDataString(values[key])).Append('\n');
                string body=text.ToString(); text.Append("checksumSha256=").Append(Hash(body)).Append('\n');
                byte[] bytes=new UTF8Encoding(false).GetBytes(text.ToString());
                if(bytes.Length>2097152) throw new IOException("Settings limit exceeded");
                using(FileStream file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { file.Write(bytes,0,bytes.Length); file.Flush(true); }
                if(Read(temp,false).Outcome!=SettingsOutcome.Success) throw new IOException("Settings verification failed");
                if(File.Exists(path)) File.Replace(temp,path,recovering?path+".corrupt-"+Guid.NewGuid().ToString("N"):path+".bak");
                else File.Move(temp,path);
            }
            finally { if(File.Exists(temp)) File.Delete(temp); }
        }
        private static string Hash(string text)
        { using(SHA256 hash=SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(new UTF8Encoding(false).GetBytes(text))).Replace("-",""); }
    }
    internal enum DataDirectoryOutcome { Success, Cancelled, InvalidPath, NotWritable }
    internal sealed class DataDirectoryResult
    {
        public readonly DataDirectoryOutcome Outcome;
        public readonly string Directory;
        public readonly bool Portable;
        public DataDirectoryResult(DataDirectoryOutcome outcome,string directory,bool portable) { Outcome=outcome; Directory=directory; Portable=portable; }
    }
    internal static class DataDirectoryPolicy
    {
        public static Task<DataDirectoryResult> ResolveAsync(string executableFolder,string localApplicationData,bool portable,CancellationToken token)
        {
            return Task.Run(delegate {
                string directory=null;
                try
                {
                    token.ThrowIfCancellationRequested();
                    directory=Path.GetFullPath(Path.Combine(portable?executableFolder:localApplicationData,"ThorCapture"));
                    Directory.CreateDirectory(directory);
                    string probe=Path.Combine(directory,".write-probe-"+Guid.NewGuid().ToString("N"));
                    try { using(FileStream file=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { file.WriteByte(1); file.Flush(true); } }
                    finally { if(File.Exists(probe)) File.Delete(probe); }
                    return new DataDirectoryResult(DataDirectoryOutcome.Success,directory,portable);
                }
                catch(OperationCanceledException) { return new DataDirectoryResult(DataDirectoryOutcome.Cancelled,directory,portable); }
                catch(ArgumentException) { return new DataDirectoryResult(DataDirectoryOutcome.InvalidPath,null,portable); }
                catch(NotSupportedException) { return new DataDirectoryResult(DataDirectoryOutcome.InvalidPath,null,portable); }
                catch(IOException) { return new DataDirectoryResult(DataDirectoryOutcome.NotWritable,directory,portable); }
                catch(UnauthorizedAccessException) { return new DataDirectoryResult(DataDirectoryOutcome.NotWritable,directory,portable); }
            });
        }
    }
}
