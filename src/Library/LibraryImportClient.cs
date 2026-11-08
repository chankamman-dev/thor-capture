using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal sealed class LibraryImportFile
    {
        public readonly string RelativePath, Sha256;
        public readonly long Bytes;
        internal LibraryImportFile(object value)
        {
            RelativePath = Json.Text(value, "relativePath"); Sha256 = Json.Text(value, "sha256"); Bytes = (long)Json.Number(value, "bytes", -1);
            if (string.IsNullOrEmpty(RelativePath) || string.IsNullOrEmpty(Sha256) || Sha256.Length != 64 || Bytes < 0)
                throw new BackendRequestException("BACKEND_RESPONSE_INVALID", 0);
        }
    }
    internal sealed class LibraryImportPlan
    {
        public readonly string PlanId;
        public readonly long TotalBytes;
        public readonly int TotalGames, VisibleGames, PendingChanges;
        public readonly bool ReplacesExistingLibrary;
        public readonly LibraryImportFile[] Files;
        public readonly string[] Warnings;
        public readonly string ExpiresUtc;
        internal LibraryImportPlan(Dictionary<string, object> value)
        {
            LibraryImportClient.Validate(value);
            PlanId = Json.Text(value, "planId"); Guid identity;
            if (!Guid.TryParse(PlanId, out identity)) throw new BackendRequestException("BACKEND_RESPONSE_INVALID", 0);
            TotalBytes = (long)Json.Number(value, "totalBytes", 0); ReplacesExistingLibrary = Json.Flag(value, "replacesExistingLibrary");
            Dictionary<string, object> summary = Json.Get(value, "summary") as Dictionary<string, object>;
            TotalGames = (int)Json.Number(summary, "totalGames", 0); VisibleGames = (int)Json.Number(summary, "visibleGames", 0);
            PendingChanges = (int)Json.Number(summary, "pendingChanges", 0);
            List<object> files = Json.Get(value, "files") as List<object>;
            if (files == null || files.Count == 0 || files.Count > 50000 || TotalBytes < 0 || summary == null)
                throw new BackendRequestException("BACKEND_RESPONSE_INVALID", 0);
            Files = files.ConvertAll(item => new LibraryImportFile(item)).ToArray();
            List<object> warnings = Json.Get(value, "warnings") as List<object>;
            Warnings = warnings == null ? new string[0] : warnings.ConvertAll(item => Convert.ToString(item)).ToArray(); ExpiresUtc = Json.Text(value, "expiresUtc");
        }
    }
    internal sealed class LibraryImportJob
    {
        public readonly string JobId, State, Code, ImportId;
        public readonly int CopiedFiles, TotalFiles;
        public readonly long CopiedBytes, TotalBytes;
        public readonly bool CanRestore;
        public bool Finished { get { return State == "completed" || State == "failed" || State == "rollback_failed" || State == "restored"; } }
        internal LibraryImportJob(Dictionary<string, object> value)
        {
            LibraryImportClient.Validate(value);
            JobId = Json.Text(value, "jobId"); State = Json.Text(value, "state"); Code = Json.Text(value, "code"); ImportId = Json.Text(value, "importId");
            Guid identity; if (!Guid.TryParse(JobId, out identity)) throw new BackendRequestException("BACKEND_RESPONSE_INVALID", 0);
            if (State != "preparing" && State != "backing_up" && State != "committing" && State != "completed" && State != "failed" && State != "rollback_failed" && State != "restored")
                throw new BackendRequestException("BACKEND_RESPONSE_INVALID", 0);
            CopiedFiles = (int)Json.Number(value, "copiedFiles", 0); TotalFiles = (int)Json.Number(value, "totalFiles", 0);
            CopiedBytes = (long)Json.Number(value, "copiedBytes", 0); TotalBytes = (long)Json.Number(value, "totalBytes", 0); CanRestore = Json.Flag(value, "canRestore");
        }
    }

    // Backend transport only. All HTTP runs off the WinForms synchronization context.
    internal sealed class LibraryImportClient
    {
        public const string Contract = "cocoon.library-import/1";
        private readonly CocoonBackend backend;
        public LibraryImportClient(CocoonBackend owner) { if (owner == null) throw new ArgumentNullException("owner"); backend = owner; }
        internal static void Validate(Dictionary<string, object> value)
        {
            if (value == null || Json.Text(value, "contract") != Contract) throw new BackendRequestException("BACKEND_RESPONSE_INVALID", 0);
        }
        private static string Identity(string value)
        {
            Guid parsed; if (!Guid.TryParse(value, out parsed)) throw new ArgumentException("Invalid import identity"); return parsed.ToString();
        }
        public Task<LibraryImportPlan> PlanAsync(string sourceRoot, bool includeCache = true, bool includeStaging = true)
        {
            string body = "{\"sourceRoot\":" + Json.Quote(sourceRoot) + ",\"includeCache\":" + (includeCache ? "true" : "false")
                + ",\"includeStaging\":" + (includeStaging ? "true" : "false") + "}";
            return Task.Run(() => new LibraryImportPlan(backend.PostJson("/api/backend/import/plan", body, 120000) as Dictionary<string, object>));
        }
        public Task<LibraryImportJob> ExecuteAsync(string planId, bool confirmed)
        {
            string body = "{\"planId\":" + Json.Quote(Identity(planId)) + ",\"confirm\":" + (confirmed ? "true" : "false") + "}";
            return Task.Run(() => new LibraryImportJob(backend.PostJson("/api/backend/import/execute", body) as Dictionary<string, object>));
        }
        public Task<LibraryImportJob> StatusAsync(string jobId)
        {
            string route = "/api/backend/import/jobs/" + Identity(jobId);
            return Task.Run(() => new LibraryImportJob(backend.GetJson(route, false) as Dictionary<string, object>));
        }
        public Task RestoreAsync(string importId, bool confirmed)
        {
            string body = "{\"importId\":" + Json.Quote(Identity(importId)) + ",\"confirm\":" + (confirmed ? "true" : "false") + "}";
            return Task.Run(delegate
            {
                Dictionary<string, object> reply = backend.PostJson("/api/backend/import/restore", body, 120000) as Dictionary<string, object>; Validate(reply);
                if (Json.Text(reply, "state") != "restored") throw new BackendRequestException("BACKEND_RESPONSE_INVALID", 0);
            });
        }
    }
}
