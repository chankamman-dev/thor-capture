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
        internal Task<NativeEditorHttpReply> ScrapeJobsAsync(CancellationToken cancellation)
        { return SendAsync("/api/scrape/jobs", true, null, 0, false, false, cancellation); }
        internal Task<NativeEditorHttpReply> ScrapeJobAsync(string jobId, bool cancel, string body, CancellationToken cancellation)
        {
            Guid id;
            bool create = body != null;
            if ((create && (jobId != null || cancel)) || (!create && (!Guid.TryParseExact(jobId, "D", out id) || id == Guid.Empty)))
                throw new ArgumentException("Invalid scrape job route");
            string path = "/api/scrape/jobs" + (create ? "" : "/" + jobId + (cancel ? "/cancel" : ""));
            byte[] bytes = create ? Encoding.UTF8.GetBytes(body) : cancel ? Encoding.UTF8.GetBytes("{}") : null;
            if (bytes != null && bytes.Length > 16384) throw new ArgumentException("Scrape request too large");
            return SendAsync(path, !create && !cancel, bytes == null ? null : new MemoryStream(bytes, false),
                bytes == null ? 0 : bytes.Length, true, false, cancellation);
        }
    }

    internal sealed class NativeScrapeJob
    {
        public readonly string JobId, LibraryId, InstanceId, State, Code;
        public readonly int Total, Completed, Identified, Failed;
        private readonly string json;
        internal NativeScrapeJob(string job, string library, string instance, string state, string code, int total, int completed, int identified, int failed, object value)
        { JobId = job; LibraryId = library; InstanceId = instance; State = state; Code = code; Total = total; Completed = completed; Identified = identified; Failed = failed; json = NativeEditorJson.Encode(value); }
        public Dictionary<string, object> Data { get { return (Dictionary<string, object>)NativeEditorJson.Parse(json); } }
        public bool Finished { get { return State == "completed" || State == "cancelled" || State == "failed"; } }
        public bool HasDetails { get { return Data.ContainsKey("games"); } }
    }

    internal sealed partial class NativeEditorClient
    {
        public async Task<NativeScrapeJob> StartScrapeAsync(IEnumerable<long> gameIds, IEnumerable<string> providerIds,
            string mode, string language, bool useExistingHashes, CancellationToken token)
        {
            if (gameIds == null || providerIds == null || Array.IndexOf(new[] { "conservative", "balanced", "aggressive" }, mode) < 0
                || language == null || !Regex.IsMatch(language, @"\A[a-zA-Z-]{1,24}\z")) throw new ArgumentException("Invalid scrape options");
            var ids = new List<object>(); var seen = new HashSet<long>();
            foreach (long id in gameIds)
            { if (id < 1 || id > 9007199254740991L || ids.Count >= 200) throw new ArgumentException("Invalid game IDs"); if (seen.Add(id)) ids.Add(id); }
            var providers = new List<object>(); var unique = new HashSet<string>();
            foreach (string provider in providerIds)
            {
                if (Array.IndexOf(new[] { "steam", "steamgriddb", "thegamesdb", "screenscraper", "retroachievements", "igdb" }, provider) < 0)
                    throw new ArgumentException("Invalid source");
                if (unique.Add(provider)) providers.Add(provider);
            }
            if (ids.Count == 0 || providers.Count == 0) throw new ArgumentException("Select games and sources");
            NativeEditorInfo snapshot; Uri address; Snapshot(out snapshot, out address);
            string body = NativeEditorJson.Encode(new Dictionary<string, object> {
                { "libraryId", snapshot.LibraryId }, { "ids", ids }, { "providers", providers },
                { "mode", mode }, { "language", language }, { "useHash", useExistingHashes } });
            NativeScrapeJob result = await ScrapeRequest(snapshot, address, null, false, body, token).ConfigureAwait(false);
            if (result.Total != ids.Count) throw Invalid();
            foreach (object row in (List<object>)result.Data["games"])
                if (!seen.Contains(Integer(row, "id", 1, 9007199254740991L))) throw Invalid();
            return result;
        }
        public async Task<IList<NativeScrapeJob>> ScrapeJobsAsync(CancellationToken token)
        {
            NativeEditorInfo snapshot; Uri address; Snapshot(out snapshot, out address);
            NativeEditorHttpTransport transport = Capture(address);
            await VerifyContext(transport, snapshot, address, token).ConfigureAwait(false);
            NativeEditorHttpReply reply = await transport.ScrapeJobsAsync(token).ConfigureAwait(false);
            CheckContext(snapshot, address);
            Dictionary<string, object> value = Obj(reply.Value, "contract", "jobs");
            List<object> jobs = Json.Get(value, "jobs") as List<object>;
            if (reply.Status != 200 || value.Count != 2 || Text(value, "contract") != "cocoon.scrape-jobs/1" || jobs == null || jobs.Count > 24) throw Invalid(reply.Status);
            var results = new List<NativeScrapeJob>(); var unique = new HashSet<string>();
            foreach (object item in jobs)
            {
                string library = Uuid(item, "libraryId");
                if (library != snapshot.LibraryId) continue;
                NativeScrapeJob job = ParseScrapeJob(item, library, null, snapshot.InstanceId, false);
                if (!unique.Add(job.JobId)) throw Invalid();
                results.Add(job);
            }
            await VerifyContext(transport, snapshot, address, token).ConfigureAwait(false);
            return results.AsReadOnly();
        }
        public Task<NativeScrapeJob> ScrapeStatusAsync(NativeScrapeJob job, CancellationToken token)
        { return ExistingScrape(job, false, token); }
        public Task<NativeScrapeJob> CancelScrapeAsync(NativeScrapeJob job, CancellationToken token)
        { return ExistingScrape(job, true, token); }
        private Task<NativeScrapeJob> ExistingScrape(NativeScrapeJob job, bool cancel, CancellationToken token)
        {
            if (job == null) throw new ArgumentNullException("job");
            NativeEditorInfo snapshot; Uri address; Snapshot(out snapshot, out address);
            if (job.LibraryId != snapshot.LibraryId || job.InstanceId != snapshot.InstanceId) throw new NativeEditorException("EDITOR_SESSION_STALE", 409);
            return ScrapeRequest(snapshot, address, job.JobId, cancel, null, token);
        }
        private async Task<NativeScrapeJob> ScrapeRequest(NativeEditorInfo snapshot, Uri address, string jobId, bool cancel, string body, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            NativeEditorHttpTransport transport = Capture(address);
            await VerifyContext(transport, snapshot, address, token).ConfigureAwait(false);
            NativeEditorHttpReply reply = await transport.ScrapeJobAsync(jobId, cancel, body, token).ConfigureAwait(false);
            CheckContext(snapshot, address);
            if (reply.Status < 200 || reply.Status >= 300)
            {
                await VerifyContext(transport, snapshot, address, token).ConfigureAwait(false);
                string code = Json.Get(reply.Value, "code") as string;
                if (code == null || !Regex.IsMatch(code, @"\A[A-Z_]{1,64}\z")) throw Invalid(reply.Status);
                throw new NativeEditorException(code, reply.Status);
            }
            if (reply.Status != (body == null ? 200 : 202)) throw Invalid(reply.Status);
            NativeScrapeJob result = ParseScrapeJob(reply.Value, snapshot.LibraryId, jobId, snapshot.InstanceId);
            await VerifyContext(transport, snapshot, address, token).ConfigureAwait(false);
            return result;
        }
        internal static NativeScrapeJob ParseScrapeJob(object raw, string library, string requestedId, string instance, bool detailed = true)
        {
            Dictionary<string, object> value = Obj(raw, "contract", "jobId", "libraryId", "state", "summary", "currentGameId",
                "createdAt", "finishedAt", "code", "games", "reviewRequired", "deviceWritesEnabled");
            if (value.Count != (detailed ? 12 : 11) || value.ContainsKey("games") != detailed || Text(value, "contract") != "cocoon.scrape-jobs/1" || Uuid(value, "libraryId") != library
                || !Flag(value, "reviewRequired") || Flag(value, "deviceWritesEnabled")) throw Invalid();
            string job = Uuid(value, "jobId"), state = Text(value, "state"), code = Json.Get(value, "code") as string;
            if ((requestedId != null && requestedId != job) || Array.IndexOf(new[] { "queued", "running", "cancelling", "completed", "failed", "cancelled" }, state) < 0
                || (Json.Get(value, "code") != null && (code == null || Array.IndexOf(new[] { "SCRAPE_TIMEOUT", "SCRAPE_LIBRARY_CHANGED", "SCRAPE_FAILED", "PROVIDER_CANCELLED" }, code) < 0))) throw Invalid();
            Integer(value, "createdAt", 1, 9007199254740991L);
            bool finished = state == "completed" || state == "failed" || state == "cancelled";
            if (finished != (Json.Get(value, "finishedAt") != null)) throw Invalid();
            if (finished) Integer(value, "finishedAt", 1, 9007199254740991L);
            if (Json.Get(value, "currentGameId") != null) Integer(value, "currentGameId", 1, 9007199254740991L);
            if (finished && Json.Get(value, "currentGameId") != null) throw Invalid();
            object summary = Obj(Json.Get(value, "summary"), "total", "completed", "identified", "highConfidence", "review", "noResult", "failed");
            int total = (int)Integer(summary, "total", 1, 200), completed = (int)Integer(summary, "completed", 0, total);
            int identified = (int)Integer(summary, "identified", 0, completed), failed = (int)Integer(summary, "failed", 0, completed);
            int noResult = (int)Integer(summary, "noResult", 0, completed);
            long high = Integer(summary, "highConfidence", 0, 9007199254740991L), review = Integer(summary, "review", 0, 9007199254740991L);
            if (state == "completed" && completed != total) throw Invalid();
            if (!detailed) return new NativeScrapeJob(job, library, instance, state, code, total, completed, identified, failed, value);
            List<object> rows = Json.Get(value, "games") as List<object>;
            if (rows == null || rows.Count != completed || (state == "completed" && completed != total)) throw Invalid();
            var ids = new HashSet<long>(); int actualIdentified = 0, actualFailed = 0, actualNoResult = 0; long actualHigh = 0, actualReview = 0;
            foreach (object row in rows)
            {
                Obj(row, "id", "candidateCount", "highConfidence", "review", "errors");
                if (!ids.Add(Integer(row, "id", 1, 9007199254740991L))) throw Invalid();
                long count = Integer(row, "candidateCount", 0, 9007199254740991L);
                actualHigh += Integer(row, "highConfidence", 0, count); actualReview += Integer(row, "review", 0, count);
                List<object> errors = Json.Get(row, "errors") as List<object>;
                if (errors == null || errors.Count > 4) throw Invalid();
                foreach (object error in errors)
                {
                    Dictionary<string, object> fields = Obj(error, "code", "message", "provider", "term");
                    Code(error, "code"); if (Text(error, "message").Length > 96) throw Invalid();
                    if (fields.ContainsKey("provider") && Text(error, "provider").Length > 32) throw Invalid();
                    if (fields.ContainsKey("term") && Text(error, "term").Length > 96) throw Invalid();
                }
                if (count > 0) actualIdentified++; if (errors.Count > 0) actualFailed++; if (count == 0 && errors.Count == 0) actualNoResult++;
            }
            if (actualIdentified != identified || actualFailed != failed || actualNoResult != noResult || actualHigh != high || actualReview != review) throw Invalid();
            return new NativeScrapeJob(job, library, instance, state, code, total, completed, identified, failed, value);
        }
    }
}
