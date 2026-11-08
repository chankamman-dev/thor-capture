using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture.Media
{
    public enum ScreenRole { Unknown, Upper, Lower }
    public enum MediaKind { Image, Video }
    public enum TrackState { Present, Missing, Unreadable, Changed }
    public enum MediaOutcome { Success, Cancelled, Conflict, NotFound, Invalid, Failed, TimedOut }
    public sealed class GameReference
    {
        public readonly string Provider, LibraryId, GameId, DisplayName;
        public GameReference(string provider, string libraryId, string gameId, string displayName)
        {
            if (!Regex.IsMatch(provider ?? "", "^[A-Za-z][A-Za-z0-9._-]{0,63}$") || !Regex.IsMatch(libraryId ?? "", "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$") || !Regex.IsMatch(gameId ?? "", "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$")) throw new ArgumentException("Invalid game identity");
            if ((displayName ?? "").Length > 512) throw new ArgumentException("Game name too long");
            Provider = provider.ToLowerInvariant(); LibraryId = libraryId; GameId = gameId; DisplayName = displayName ?? "";
        }
        public string Key { get { return Provider + "|" + LibraryId + "|" + GameId; } }
    }
    public sealed class MediaTrackInput
    {
        public readonly string Path;
        public readonly ScreenRole Role;
        public MediaTrackInput(string path, ScreenRole role) { Path = path; Role = role; }
    }
    public sealed class MediaTrack
    {
        public readonly string TrackId, RelativePath, Sha256, FileIdentity, Package;
        public readonly ScreenRole Role;
        public readonly MediaKind Kind;
        public readonly TrackState State;
        public readonly long Length;
        public readonly DateTime WriteUtc;
        internal MediaTrack(string id, string path, string hash, string identity, string package, ScreenRole role, MediaKind kind, TrackState state, long length, DateTime write)
        { TrackId = id; RelativePath = path; Sha256 = hash; FileIdentity = identity; Package = package; Role = role; Kind = kind; State = state; Length = length; WriteUtc = write; }
        internal MediaTrack Status(TrackState state) { return new MediaTrack(TrackId, RelativePath, Sha256, FileIdentity, Package, Role, Kind, state, Length, WriteUtc); }
    }
    public sealed class MediaGroup
    {
        public readonly string GroupId, LegacyKey;
        public readonly long Revision;
        public readonly DateTime CreatedUtc;
        public readonly ReadOnlyCollection<MediaTrack> Tracks;
        public readonly ReadOnlyCollection<string> Tags;
        public readonly GameReference Game;
        public readonly bool GameMissing, Favorite;
        internal MediaGroup(string id, long revision, DateTime created, string legacyKey, IEnumerable<MediaTrack> tracks, GameReference game, bool gameMissing, bool favorite, IEnumerable<string> tags)
        { GroupId = id; Revision = revision; CreatedUtc = created; LegacyKey = legacyKey; Tracks = new ReadOnlyCollection<MediaTrack>(tracks.ToList()); Game = game; GameMissing = gameMissing; Favorite = favorite; Tags = new ReadOnlyCollection<string>(tags.ToList()); }
        internal MediaGroup Replace(IEnumerable<MediaTrack> tracks, GameReference game, bool missing, bool favorite, IEnumerable<string> tags)
        { return new MediaGroup(GroupId, checked(Revision + 1), CreatedUtc, LegacyKey, tracks, game, missing, favorite, tags); }
    }
    public sealed class MediaIssue
    {
        public readonly string Code, Reference, Reason;
        internal MediaIssue(string code, string reference, string reason) { Code = code; Reference = reference; Reason = reason; }
    }
    public sealed class MediaSnapshot
    {
        public readonly string RepositoryId;
        public readonly long Generation;
        public readonly ReadOnlyCollection<MediaGroup> Groups;
        public readonly ReadOnlyCollection<MediaIssue> Issues;
        internal MediaSnapshot(string repositoryId, long generation, IEnumerable<MediaGroup> groups, IEnumerable<MediaIssue> issues)
        { RepositoryId = repositoryId; Generation = generation; Groups = new ReadOnlyCollection<MediaGroup>(groups.OrderByDescending(g => g.CreatedUtc).ThenBy(g => g.GroupId).ToList()); Issues = new ReadOnlyCollection<MediaIssue>(issues.ToList()); }
        public MediaGroup FindGroup(string id) { return Groups.FirstOrDefault(g => g.GroupId == id); }
        public ReadOnlyCollection<MediaGroup> ForGame(GameReference game)
        { if (game == null) throw new ArgumentNullException("game"); return new ReadOnlyCollection<MediaGroup>(Groups.Where(g => g.Game != null && g.Game.Key == game.Key).ToList()); }
    }
    public sealed class MediaRepositoryResult
    {
        public readonly MediaOutcome Outcome;
        public readonly string Code, Reason;
        public readonly MediaSnapshot Snapshot;
        public readonly MediaGroup Group;
        internal MediaRepositoryResult(MediaOutcome outcome, string code, string reason, MediaSnapshot snapshot, MediaGroup group)
        { Outcome = outcome; Code = code; Reason = reason; Snapshot = snapshot; Group = group; }
    }
    public sealed class MediaSnapshotEventArgs : EventArgs
    {
        public readonly MediaSnapshot Snapshot;
        internal MediaSnapshotEventArgs(MediaSnapshot snapshot) { Snapshot = snapshot; }
    }
    public interface IMediaRepository : IDisposable
    {
        MediaSnapshot GetSnapshot();
        string ResolvePath(MediaTrack track);
        Task<MediaRepositoryResult> RebindRootAsync(CancellationToken token);
        Task<MediaRepositoryResult> RefreshAsync(CancellationToken token);
        Task<MediaRepositoryResult> ImportPathsAsync(IEnumerable<string> paths, CancellationToken token);
        Task<MediaRepositoryResult> RegisterGroupAsync(IEnumerable<MediaTrackInput> tracks, CancellationToken token);
        Task<MediaRepositoryResult> AssociateGameAsync(string groupId, long expectedRevision, GameReference game, CancellationToken token);
        Task<MediaRepositoryResult> UnlinkGameAsync(string groupId, long expectedRevision, CancellationToken token);
        Task<MediaRepositoryResult> SetAnnotationsAsync(string groupId, long expectedRevision, bool favorite, IEnumerable<string> tags, CancellationToken token);
        Task<MediaRepositoryResult> ValidateGameLinksAsync(IEnumerable<GameReference> available, CancellationToken token);
        Task<MediaRepositoryResult> ValidateGameLinksAsync(string provider, string libraryId, IEnumerable<string> availableIds, CancellationToken token);
        Task<MediaRepositoryResult> AcknowledgeSourceChangesAsync(string groupId, long expectedRevision, CancellationToken token);
        Task<MediaRepositoryResult> RestorePreviousAnnotationsAsync(string groupId, long expectedRevision, CancellationToken token);
        event EventHandler<MediaSnapshotEventArgs> Changed;
    }
}
