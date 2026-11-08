using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal enum NativeMigrationRoute { Plan, Execute, Restore, Records, Status }
    internal sealed partial class NativeEditorHttpTransport
    {
        internal Task<NativeEditorHttpReply> MigrationAsync(NativeMigrationRoute route, string id, string body, CancellationToken cancellation)
        {
            if (!Enum.IsDefined(typeof(NativeMigrationRoute), route)) throw new ArgumentException("Invalid migration route");
            bool get = route == NativeMigrationRoute.Records || route == NativeMigrationRoute.Status;
            Guid parsed;
            if ((route == NativeMigrationRoute.Status && (!Guid.TryParseExact(id, "D", out parsed) || parsed == Guid.Empty)) ||
                (route != NativeMigrationRoute.Status && id != null) || (get && body != null) || (!get && body == null))
                throw new ArgumentException("Invalid migration request");
            string suffix = route == NativeMigrationRoute.Status ? "records/" + id : route.ToString().ToLowerInvariant();
            byte[] bytes = body == null ? null : Encoding.UTF8.GetBytes(body);
            if (bytes != null && bytes.Length > 16384) throw new ArgumentException("Migration request too large");
            return SendAsync("/api/backend/migration/" + suffix, get, bytes == null ? null : new MemoryStream(bytes, false),
                bytes == null ? 0 : bytes.Length, true, false, cancellation);
        }
    }
    internal sealed class NativeMigrationPlan
    {
        public readonly string PlanId, LibraryId, InstanceId;
        public readonly int CurrentVersion, TargetVersion;
        public readonly bool NoChanges;
        private readonly string json;
        internal NativeMigrationPlan(string plan, string library, string instance, int current, int target, bool noChanges, object value)
        { PlanId = plan; LibraryId = library; InstanceId = instance; CurrentVersion = current; TargetVersion = target; NoChanges = noChanges; json = NativeEditorJson.Encode(value); }
        public Dictionary<string, object> Data { get { return (Dictionary<string, object>)NativeEditorJson.Parse(json); } }
    }
    internal sealed class NativeMigrationRecord
    {
        public readonly string MigrationId, LibraryId, InstanceId, State;
        public readonly int CurrentVersion;
        public readonly bool CanRestore;
        internal NativeMigrationRecord(string id, string library, string instance, string state, int version, bool restore)
        { MigrationId = id; LibraryId = library; InstanceId = instance; State = state; CurrentVersion = version; CanRestore = restore; }
    }
    internal sealed partial class NativeEditorClient
    {
        public async Task<NativeMigrationPlan> MigrationPlanAsync(CancellationToken token)
        {
            NativeEditorInfo snapshot; Uri address; Snapshot(out snapshot, out address);
            object value = await MigrationRequest(snapshot, address, NativeMigrationRoute.Plan, null,
                NativeEditorJson.Encode(Map("libraryId", snapshot.LibraryId)), token).ConfigureAwait(false);
            return ParseMigrationPlan(value, snapshot.LibraryId, snapshot.InstanceId);
        }
        public async Task<NativeMigrationRecord> ConfirmMigrationAsync(NativeMigrationPlan plan, bool confirm, CancellationToken token)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            if (!confirm) throw new NativeEditorException("MIGRATION_CONFIRM_REQUIRED", 400);
            NativeEditorInfo snapshot; Uri address; Snapshot(out snapshot, out address);
            if (plan.LibraryId != snapshot.LibraryId || plan.InstanceId != snapshot.InstanceId) throw new NativeEditorException("EDITOR_SESSION_STALE", 409);
            object value = await MigrationRequest(snapshot, address, NativeMigrationRoute.Execute, null,
                NativeEditorJson.Encode(Map("planId", plan.PlanId, "confirm", true)), token).ConfigureAwait(false);
            return ParseMigrationRecord(value, snapshot.LibraryId, snapshot.InstanceId, plan.PlanId);
        }
        public async Task<NativeMigrationRecord> MigrationStatusAsync(string migrationId, CancellationToken token)
        {
            NativeEditorInfo snapshot; Uri address; Snapshot(out snapshot, out address);
            object value = await MigrationRequest(snapshot, address, NativeMigrationRoute.Status, migrationId, null, token).ConfigureAwait(false);
            return ParseMigrationRecord(value, snapshot.LibraryId, snapshot.InstanceId, migrationId);
        }
        public async Task<IList<NativeMigrationRecord>> MigrationRecordsAsync(CancellationToken token)
        {
            NativeEditorInfo snapshot; Uri address; Snapshot(out snapshot, out address);
            object raw = await MigrationRequest(snapshot, address, NativeMigrationRoute.Records, null, null, token).ConfigureAwait(false);
            Dictionary<string, object> value = Obj(raw, "contract", "items");
            List<object> items = Json.Get(value, "items") as List<object>;
            if (value.Count != 2 || Text(value, "contract") != "cocoon.managed-migration/1" || items == null || items.Count > 50) throw Invalid();
            var records = new List<NativeMigrationRecord>(); var ids = new HashSet<string>();
            foreach (object item in items)
            {
                if (Uuid(item, "libraryId") != snapshot.LibraryId) continue;
                NativeMigrationRecord record = ParseMigrationRecord(item, snapshot.LibraryId, snapshot.InstanceId, null);
                if (!ids.Add(record.MigrationId)) throw Invalid();
                records.Add(record);
            }
            return records.AsReadOnly();
        }
        public async Task<NativeMigrationRecord> RestoreMigrationAsync(NativeMigrationRecord record, bool confirm, CancellationToken token)
        {
            if (record == null) throw new ArgumentNullException("record");
            if (!confirm) throw new NativeEditorException("MIGRATION_CONFIRM_REQUIRED", 400);
            NativeEditorInfo snapshot; Uri address; Snapshot(out snapshot, out address);
            if (record.LibraryId != snapshot.LibraryId || record.InstanceId != snapshot.InstanceId) throw new NativeEditorException("EDITOR_SESSION_STALE", 409);
            object value = await MigrationRequest(snapshot, address, NativeMigrationRoute.Restore, null,
                NativeEditorJson.Encode(Map("migrationId", record.MigrationId, "confirm", true)), token).ConfigureAwait(false);
            return ParseMigrationRecord(value, snapshot.LibraryId, snapshot.InstanceId, record.MigrationId);
        }
        private async Task<object> MigrationRequest(NativeEditorInfo snapshot, Uri address, NativeMigrationRoute route, string id, string body, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); NativeEditorHttpTransport transport = Capture(address);
            await VerifyContext(transport, snapshot, address, token).ConfigureAwait(false);
            NativeEditorHttpReply reply = await transport.MigrationAsync(route, id, body, token).ConfigureAwait(false);
            CheckContext(snapshot, address);
            await VerifyContext(transport, snapshot, address, token).ConfigureAwait(false);
            if (reply.Status != 200)
            {
                string code = Json.Get(reply.Value, "code") as string;
                if (reply.Status < 400 || Array.IndexOf(new[] { "MIGRATION_INPUT_INVALID", "MIGRATION_CONFIRM_REQUIRED", "MIGRATION_PLAN_EXPIRED",
                    "MIGRATION_SOURCE_CHANGED", "MIGRATION_LIBRARY_CHANGED", "MIGRATION_PATH_UNSAFE", "MIGRATION_LIMIT", "MIGRATION_BUSY",
                    "MIGRATION_NOT_FOUND", "MIGRATION_BACKUP_INVALID", "MIGRATION_RECOVERY_REQUIRED", "MIGRATION_FAILED",
                    "MIGRATION_NEWER_VERSION", "MIGRATION_CANCELLED", "MIGRATION_MANAGED_ONLY" }, code) < 0) throw Invalid(reply.Status);
                throw new NativeEditorException(code, reply.Status);
            }
            return reply.Value;
        }
        internal static NativeMigrationPlan ParseMigrationPlan(object raw, string library, string instance)
        {
            Dictionary<string, object> value = Obj(raw, "contract", "planId", "libraryId", "currentVersion", "targetVersion",
                "pending", "versionSourcesAgree", "report", "expiresAt", "sourceFingerprint", "requiresConfirmation", "noChanges", "nativeDatabaseModified", "deviceWritesEnabled");
            if (value.Count != 14 || Text(value, "contract") != "cocoon.managed-migration/1" || Uuid(value, "libraryId") != library
                || !Flag(value, "requiresConfirmation") || Flag(value, "nativeDatabaseModified") || Flag(value, "deviceWritesEnabled")) throw Invalid();
            string id = Uuid(value, "planId");
            int current = (int)Integer(value, "currentVersion", 0, 1000), target = (int)Integer(value, "targetVersion", current, 1000);
            Flag(value, "versionSourcesAgree"); Integer(value, "expiresAt", 1, 9007199254740991L); Hash(value, "sourceFingerprint");
            List<object> pending = Json.Get(value, "pending") as List<object>;
            bool noChanges = Flag(value, "noChanges");
            if (pending == null || pending.Count > 10 || noChanges != (pending.Count == 0)) throw Invalid();
            long previous = current;
            foreach (object step in pending)
            { Obj(step, "version", "name"); previous = Integer(step, "version", previous + 1, target); if (!Regex.IsMatch(Text(step, "name"), @"\A[a-z0-9-]{1,128}\z")) throw Invalid(); }
            if (previous != target) throw Invalid();
            Dictionary<string, object> report = Obj(Json.Get(value, "report"), "counts", "reports");
            Dictionary<string, object> counts = Obj(Json.Get(report, "counts"), "game_identities", "scrape_candidates", "metadata", "library_items", "change_sets");
            foreach (string key in counts.Keys) Integer(counts, key, 0, 9007199254740991L);
            List<object> reports = Json.Get(report, "reports") as List<object>;
            if (report.Count != 2 || reports == null || reports.Count > 10) throw Invalid();
            foreach (object row in reports)
            {
                Obj(row, "version", "name", "status", "counts", "warnings"); Integer(row, "version", 0, target);
                if (!Regex.IsMatch(Text(row, "name"), @"\A[a-z0-9-]{1,128}\z") || Array.IndexOf(new[] { "warning", "complete" }, Text(row, "status")) < 0) throw Invalid();
                foreach (string part in new[] { "counts", "warnings" })
                {
                    Dictionary<string, object> metrics = Json.Get(row, part) as Dictionary<string, object>;
                    if (metrics == null || metrics.Count > 32) throw Invalid();
                    foreach (string key in metrics.Keys)
                    { if (!Regex.IsMatch(key, @"\A[a-zA-Z]{1,64}\z")) throw Invalid(); Integer(metrics, key, 0, 9007199254740991L); }
                }
            }
            return new NativeMigrationPlan(id, library, instance, current, target, noChanges, value);
        }
        internal static NativeMigrationRecord ParseMigrationRecord(object raw, string library, string instance, string requestedId)
        {
            Dictionary<string, object> value = Obj(raw, "contract", "migrationId", "libraryId", "state", "currentVersion", "canRestore", "nativeDatabaseModified", "deviceWritesEnabled");
            if (value.Count != 8 || Text(value, "contract") != "cocoon.managed-migration/1" || Uuid(value, "libraryId") != library
                || Flag(value, "nativeDatabaseModified") || Flag(value, "deviceWritesEnabled")) throw Invalid();
            string id = Uuid(value, "migrationId"), state = Text(value, "state"); bool restore = Flag(value, "canRestore");
            if (requestedId != null && requestedId != id || Array.IndexOf(new[] { "unchanged", "prepared", "publishing", "committed", "restoring", "restored", "rolled_back" }, state) < 0 ||
                restore && state != "committed") throw Invalid();
            return new NativeMigrationRecord(id, library, instance, state, (int)Integer(value, "currentVersion", 0, 1000), restore);
        }
    }
}
