using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Media;

namespace ThorCapture
{
    internal enum GameMediaOutcome { Success, EmptyLibrary, NotFound, Invalid, ContextChanged, Cancelled, RepositoryFailure, Failed }
    internal sealed class GameMediaResult
    {
        public readonly GameMediaOutcome Outcome;
        public readonly string Code;
        public readonly GameReference Game;
        public readonly MediaRepositoryResult RepositoryResult;
        public readonly ReadOnlyCollection<MediaGroup> Groups;
        internal GameMediaResult(GameMediaOutcome outcome, string code, GameReference game, MediaRepositoryResult repository, IEnumerable<MediaGroup> groups)
        { Outcome = outcome; Code = code; Game = game; RepositoryResult = repository; Groups = new ReadOnlyCollection<MediaGroup>(groups.ToList()); }
    }
    // Identity and game validation are read-only HTTP operations. Local media association never writes originals.
    internal sealed class GameMediaClient : IDisposable
    {
        readonly IMediaRepository repository;
        readonly Func<Uri> endpoint;
        readonly Func<long> version;
        readonly Func<string, CancellationToken, object> get;
        readonly CancellationTokenSource closed = new CancellationTokenSource();
        readonly CocoonBackend backend;
        long generation;
        sealed class Context { internal Uri Endpoint; internal long Version; }
        sealed class ClientFailure : Exception { internal GameMediaOutcome Outcome; internal string Code; internal GameReference Game; internal ClientFailure(GameMediaOutcome outcome, string code, GameReference game = null) { Outcome = outcome; Code = code; Game = game; } }
        public GameMediaClient(CocoonBackend backend, IMediaRepository repository)
        {
            if (backend == null || repository == null) throw new ArgumentNullException();
            this.backend = backend; this.repository = repository;
            endpoint = () => backend.State == BackendState.Ready ? backend.Endpoint : null;
            version = () => Interlocked.Read(ref generation);
            get = (path, token) => backend.GetJson(path, token);
            backend.StateChanged += OnBackendChanged;
        }
        internal GameMediaClient(IMediaRepository repository, Func<Uri> endpoint, Func<long> version, Func<string, CancellationToken, object> get)
        { if (repository == null || endpoint == null || version == null || get == null) throw new ArgumentNullException(); this.repository = repository; this.endpoint = endpoint; this.version = version; this.get = get; }
        void OnBackendChanged(object sender, EventArgs args) { Interlocked.Increment(ref generation); }
        public static string NormalizeGameId(string id)
        {
            Match match = Regex.Match(id ?? "", "\\A(?:(?:native|legacy-item|unresolved-native):)?([0-9]{1,16})\\z"); long number;
            if (!match.Success || !Int64.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number > 9007199254740991L) throw new ArgumentException("Invalid native game ID");
            return "native:" + number.ToString(CultureInfo.InvariantCulture);
        }
        Context Capture()
        { Context value = new Context { Endpoint = endpoint(), Version = version() }; if (value.Endpoint == null) throw new ClientFailure(GameMediaOutcome.Failed, "BACKEND_NOT_READY"); Check(value); return value; }
        void Check(Context context)
        { if (context.Version != version() || endpoint() == null || !context.Endpoint.Equals(endpoint())) throw new ClientFailure(GameMediaOutcome.ContextChanged, "BACKEND_CONTEXT_CHANGED"); }
        object Read(Context context, string path, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Check(context); object result = get(path, token); token.ThrowIfCancellationRequested(); Check(context); return result; }
        string Identity(Context context, CancellationToken token)
        {
            object node = Read(context, "/api/backend/library/identity", token);
            if (Json.Text(node, "contract") != "cocoon.library-identity/1" || Json.Text(node, "provider") != "cocoon") throw new ClientFailure(GameMediaOutcome.Invalid, "LIBRARY_IDENTITY_INVALID");
            Dictionary<string, object> fields = node as Dictionary<string, object>;
            if (fields == null || !fields.ContainsKey("libraryId")) throw new ClientFailure(GameMediaOutcome.Invalid, "LIBRARY_IDENTITY_INVALID");
            object raw = Json.Get(node, "libraryId"); if (raw == null) throw new ClientFailure(GameMediaOutcome.EmptyLibrary, "LIBRARY_NOT_IMPORTED");
            string id = raw as string;
            if (!Regex.IsMatch(id ?? "", "\\A[a-f0-9]{8}(-[a-f0-9]{4}){3}-[a-f0-9]{12}\\z", RegexOptions.IgnoreCase)) throw new ClientFailure(GameMediaOutcome.Invalid, "LIBRARY_IDENTITY_INVALID");
            return id.ToLowerInvariant();
        }
        GameReference Resolve(Context context, string id, CancellationToken token)
        {
            string normalized = NormalizeGameId(id), library = Identity(context, token);
            GameReference reference = new GameReference("cocoon", library, normalized, "");
            object node = Read(context, "/api/v2/catalog/items/" + Uri.EscapeDataString("legacy-item:" + normalized.Substring(7)), token);
            string after = Identity(context, token);
            if (after != library) throw new ClientFailure(GameMediaOutcome.ContextChanged, "LIBRARY_REPLACED", reference);
            if (node == null) throw new ClientFailure(GameMediaOutcome.NotFound, "GAME_NOT_FOUND", reference);
            string actual;
            try { actual = NormalizeGameId(Json.Text(node, "id")); } catch (ArgumentException) { throw new ClientFailure(GameMediaOutcome.Invalid, "GAME_RESPONSE_INVALID", reference); }
            double number = Json.Number(node, "nativeGameId", -1);
            if (actual != normalized || number < 0 || number > 9007199254740991D || number != Math.Truncate(number) || "native:" + number.ToString("0", CultureInfo.InvariantCulture) != normalized) throw new ClientFailure(GameMediaOutcome.Invalid, "GAME_RESPONSE_INVALID", reference);
            string title = Json.Text(node, "displayName") ?? Json.Text(node, "title") ?? Json.Text(node, "fileName") ?? "";
            return new GameReference("cocoon", library, normalized, title.Length > 512 ? title.Substring(0, 512) : title);
        }
        GameMediaResult Result(GameMediaOutcome outcome, string code, GameReference game, MediaRepositoryResult media = null)
        { return new GameMediaResult(outcome, code, game, media, game == null ? (IEnumerable<MediaGroup>)new MediaGroup[0] : repository.GetSnapshot().ForGame(game)); }
        Task<GameMediaResult> Run(Func<CancellationToken, Task<GameMediaResult>> action, CancellationToken cancellation)
        {
            return Task.Run(async () =>
            {
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, closed.Token))
                {
                    try { linked.Token.ThrowIfCancellationRequested(); return await action(linked.Token).ConfigureAwait(false); }
                    catch (ClientFailure error) { return Result(error.Outcome, error.Code, error.Game); }
                    catch (OperationCanceledException) { return Result(GameMediaOutcome.Cancelled, "CANCELLED", null); }
                    catch (ArgumentException) { return Result(GameMediaOutcome.Invalid, "INVALID_GAME_ID", null); }
                    catch (BackendRequestException error) { return Result(GameMediaOutcome.Failed, error.Code, null); }
                    catch (Exception) { return Result(GameMediaOutcome.Failed, "GAME_MEDIA_FAILED", null); }
                }
            });
        }
        public Task<GameMediaResult> ResolveGameAsync(int gameId, CancellationToken token) { return ResolveGameAsync(gameId.ToString(CultureInfo.InvariantCulture), token); }
        public Task<GameMediaResult> ResolveGameAsync(string gameId, CancellationToken token)
        { return Run(cancel => Task.FromResult(Result(GameMediaOutcome.Success, "OK", Resolve(Capture(), gameId, cancel))), token); }
        public Task<GameMediaResult> QueryByGameAsync(string gameId, CancellationToken token) { return ResolveGameAsync(gameId, token); }
        public ReadOnlyCollection<MediaGroup> QueryStored(GameReference game) { return repository.GetSnapshot().ForGame(game); }
        public Task<GameMediaResult> AssociateAsync(string groupId, long revision, string gameId, CancellationToken token)
        {
            return Run(async cancel =>
            {
                Context context = Capture(); GameReference game = Resolve(context, gameId, cancel);
                Check(context); MediaRepositoryResult write = await repository.AssociateGameAsync(groupId, revision, game, cancel).ConfigureAwait(false);
                // Cross-HTTP/local-disk atomicity is impossible: report a committed old-namespace reference if context changed.
                try { Check(context); if (Identity(context, cancel) != game.LibraryId) return Result(GameMediaOutcome.ContextChanged, "LIBRARY_REPLACED", game, write); }
                catch (ClientFailure error) { return Result(GameMediaOutcome.ContextChanged, error.Code, game, write); }
                catch (OperationCanceledException) { return Result(GameMediaOutcome.Cancelled, "CANCELLED", game, write); }
                catch (BackendRequestException error) { return Result(GameMediaOutcome.Failed, error.Code, game, write); }
                catch (Exception) { return Result(GameMediaOutcome.Failed, "POST_WRITE_VALIDATION_FAILED", game, write); }
                return Result(write.Outcome == MediaOutcome.Success ? GameMediaOutcome.Success : GameMediaOutcome.RepositoryFailure, write.Code, game, write);
            }, token);
        }
        public Task<GameMediaResult> UnlinkAsync(string groupId, long revision, CancellationToken token)
        { return Run(async cancel => { MediaRepositoryResult write = await repository.UnlinkGameAsync(groupId, revision, cancel).ConfigureAwait(false); return Result(write.Outcome == MediaOutcome.Success ? GameMediaOutcome.Success : GameMediaOutcome.RepositoryFailure, write.Code, null, write); }, token); }
        public Task<GameMediaResult> CheckReferenceAsync(GameReference reference, CancellationToken token)
        {
            return Run(cancel =>
            {
                if (reference == null || reference.Provider != "cocoon") throw new ArgumentException();
                try {
                    Context context = Capture(); if (Identity(context, cancel) != reference.LibraryId) return Task.FromResult(Result(GameMediaOutcome.ContextChanged, "LIBRARY_REPLACED", reference));
                    return Task.FromResult(Result(GameMediaOutcome.Success, "OK", Resolve(context, reference.GameId, cancel)));
                }
                catch (ClientFailure error) { return Task.FromResult(Result(error.Outcome, error.Code, reference)); }
            }, token);
        }
        public void Dispose() { if (backend != null) backend.StateChanged -= OnBackendChanged; closed.Cancel(); }
    }
}
