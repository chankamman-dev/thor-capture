using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture
{
    internal sealed class DevicePullJob
    {
        public const string Contract = "cocoon.device-read/1";
        public readonly string JobId, State, Code, SessionId;
        public readonly LibraryImportPlan Plan;
        internal DevicePullJob(object value)
        {
            Guid id, session;
            JobId = Json.Text(value, "jobId"); State = Json.Text(value, "state"); Code = Json.Text(value, "code"); SessionId = Json.Text(value, "sessionId");
            if (Json.Text(value, "contract") != Contract || !Guid.TryParse(JobId, out id) || !Guid.TryParse(SessionId, out session)
                || Array.IndexOf(new[] { "reading", "checking", "planned", "failed", "cancelled" }, State) < 0) throw new BackendRequestException("BACKEND_RESPONSE_INVALID", 0);
            if (State == "planned") Plan = new LibraryImportPlan(Json.Get(value, "plan") as Dictionary<string, object>);
        }
    }
    internal sealed class DevicePullClient
    {
        private readonly CocoonBackend backend;
        public DevicePullClient(CocoonBackend owner) { if (owner == null) throw new ArgumentNullException("owner"); backend = owner; }
        public Task<DevicePullJob> StartPlanAsync(DeviceSession session, CancellationToken token)
        {
            if (session == null || !session.Connected) throw new ArgumentException("Current device session required");
            string body = "{\"serial\":" + Json.Quote(session.Serial) + ",\"sessionId\":" + Json.Quote(session.Id.ToString("D")) + "}";
            return Task.Run(() => new DevicePullJob(backend.PostJson("/api/backend/device/pull/plan", body, 15000, token)), token);
        }
        private static string Identity(string id) { Guid value; if (!Guid.TryParse(id, out value)) throw new ArgumentException("Invalid device read job"); return value.ToString("D"); }
        public Task<DevicePullJob> StatusAsync(string jobId, CancellationToken token)
        { string route = "/api/backend/device/pull/jobs/" + Identity(jobId); return Task.Run(() => new DevicePullJob(backend.GetJson(route, token, false)), token); }
        public Task<DevicePullJob> CancelAsync(string jobId)
        { string route = "/api/backend/device/pull/jobs/" + Identity(jobId) + "/cancel"; return Task.Run(() => new DevicePullJob(backend.PostJson(route, "{}"))); }
    }
}
