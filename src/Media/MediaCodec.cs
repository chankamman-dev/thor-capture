using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ThorCapture.Media
{
    internal static class MediaCodec
    {
        internal const string Schema = "thor.media-group/1";
        internal static string Quote(string value)
        {
            StringBuilder text = new StringBuilder("\"");
            foreach (char c in value ?? "")
            {
                if (c == '"' || c == '\\') text.Append('\\').Append(c);
                else if (c < 32 || Char.IsSurrogate(c)) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else text.Append(c);
            }
            return text.Append('"').ToString();
        }
        static string Bool(bool value) { return value ? "true" : "false"; }
        internal static string Payload(MediaGroup group)
        {
            StringBuilder body = new StringBuilder();
            body.Append("{\"groupId\":").Append(Quote(group.GroupId)).Append(",\"revision\":").Append(Quote(group.Revision.ToString(CultureInfo.InvariantCulture)))
                .Append(",\"createdUtc\":").Append(Quote(group.CreatedUtc.ToString("o", CultureInfo.InvariantCulture))).Append(",\"legacyKey\":").Append(Quote(group.LegacyKey))
                .Append(",\"favorite\":").Append(Bool(group.Favorite)).Append(",\"gameMissing\":").Append(Bool(group.GameMissing)).Append(",\"game\":");
            if (group.Game == null) body.Append("null");
            else body.Append("{\"provider\":").Append(Quote(group.Game.Provider)).Append(",\"libraryId\":").Append(Quote(group.Game.LibraryId)).Append(",\"gameId\":").Append(Quote(group.Game.GameId)).Append(",\"displayName\":").Append(Quote(group.Game.DisplayName)).Append('}');
            body.Append(",\"tags\":[").Append(String.Join(",", group.Tags.Select(Quote))).Append("],\"tracks\":[");
            bool first = true;
            foreach (MediaTrack track in group.Tracks)
            {
                if (!first) body.Append(','); first = false;
                body.Append("{\"trackId\":").Append(Quote(track.TrackId)).Append(",\"relativePath\":").Append(Quote(track.RelativePath)).Append(",\"sha256\":").Append(Quote(track.Sha256))
                    .Append(",\"fileIdentity\":").Append(Quote(track.FileIdentity)).Append(",\"package\":").Append(Quote(track.Package)).Append(",\"role\":").Append(Quote(track.Role.ToString()))
                    .Append(",\"kind\":").Append(Quote(track.Kind.ToString())).Append(",\"state\":").Append(Quote(track.State.ToString())).Append(",\"length\":").Append(Quote(track.Length.ToString(CultureInfo.InvariantCulture)))
                    .Append(",\"writeUtc\":").Append(Quote(track.WriteUtc.ToString("o", CultureInfo.InvariantCulture))).Append('}');
            }
            return body.Append("]}").ToString();
        }
        static string Hash(string text)
        { using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", ""); }
        internal static string Encode(MediaGroup group) { string body = Payload(group); return Schema + "\n" + Hash(body) + "\n" + body; }
        static object Field(object node, string key)
        {
            Dictionary<string, object> fields = node as Dictionary<string, object>; object value;
            if (fields == null || !fields.TryGetValue(key, out value)) throw new InvalidDataException("Missing media field");
            return value;
        }
        static string Text(object node, string key, int max)
        { string text = Field(node, key) as string; if (text == null || text.Length > max) throw new InvalidDataException("Invalid media text"); return text; }
        static bool Flag(object node, string key)
        { object value = Field(node, key); if (!(value is bool)) throw new InvalidDataException("Invalid media flag"); return (bool)value; }
        static long Number(object node, string key, long max)
        { long value; if (!Int64.TryParse(Text(node, key, 24), NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 0 || value > max) throw new InvalidDataException("Invalid media number"); return value; }
        static DateTime Time(object node, string key)
        { DateTime value; if (!DateTime.TryParseExact(Text(node, key, 40), "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value) || value.Kind != DateTimeKind.Utc) throw new InvalidDataException("Invalid UTC media time"); return value; }
        internal static string Id(string value)
        { if (!Regex.IsMatch(value ?? "", "^[0-9a-f]{32}$")) throw new InvalidDataException("Invalid media ID"); return value; }
        static T EnumValue<T>(object node, string field) where T : struct
        { T value; if (!Enum.TryParse(Text(node, field, 32), false, out value) || !Enum.IsDefined(typeof(T), value)) throw new InvalidDataException("Invalid media enum"); return value; }
        internal static MediaGroup Decode(string text)
        {
            if (text == null || text.Length > 1024 * 1024) throw new InvalidDataException("Media sidecar size limit");
            int first = text.IndexOf('\n'), second = first < 0 ? -1 : text.IndexOf('\n', first + 1);
            if (first < 0 || second < 0 || text.Substring(0, first) != Schema) throw new InvalidDataException("Unsupported media schema");
            string body = text.Substring(second + 1);
            if (text.Substring(first + 1, second - first - 1) != Hash(body)) throw new InvalidDataException("Media sidecar checksum mismatch");
            object node = Json.Parse(body);
            string id = Id(Text(node, "groupId", 32)); long revision = Number(node, "revision", Int64.MaxValue - 1);
            if (revision < 1) throw new InvalidDataException("Invalid revision");
            List<object> tracks = Field(node, "tracks") as List<object>, tags = Field(node, "tags") as List<object>;
            if (tracks == null || tracks.Count < 1 || tracks.Count > 16 || tags == null || tags.Count > 64) throw new InvalidDataException("Media collection limit");
            List<MediaTrack> parsed = new List<MediaTrack>();
            foreach (object track in tracks)
            {
                string path = Text(track, "relativePath", 255), sha = Text(track, "sha256", 64), identity = Text(track, "fileIdentity", 64);
                if (path.Length == 0 || Path.GetFileName(path) != path || path.Contains(":") || path == "." || path == ".." || !Regex.IsMatch(sha, "^[0-9A-F]{64}$") || !Regex.IsMatch(identity, "^$|^[0-9A-F]{8}:[0-9A-F]{16}$")) throw new InvalidDataException("Invalid media source");
                parsed.Add(new MediaTrack(Id(Text(track, "trackId", 32)), path, sha, identity, Text(track, "package", 255), EnumValue<ScreenRole>(track, "role"), EnumValue<MediaKind>(track, "kind"), EnumValue<TrackState>(track, "state"), Number(track, "length", 64L * 1024 * 1024 * 1024), Time(track, "writeUtc")));
            }
            if (parsed.Select(t => t.TrackId).Distinct().Count() != parsed.Count || parsed.Select(t => t.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != parsed.Count) throw new InvalidDataException("Duplicate media tracks");
            GameReference game = null; object gameNode = Field(node, "game");
            if (gameNode != null) game = new GameReference(Text(gameNode, "provider", 64), Text(gameNode, "libraryId", 128), Text(gameNode, "gameId", 128), Text(gameNode, "displayName", 512));
            bool missing = Flag(node, "gameMissing"); if (missing && game == null) throw new InvalidDataException("Missing unbound game");
            string[] tagValues = tags.Select(t => t as string).ToArray();
            if (tagValues.Any(t => String.IsNullOrWhiteSpace(t) || t.Length > 128) || tagValues.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tagValues.Length) throw new InvalidDataException("Invalid media tags");
            return new MediaGroup(id, revision, Time(node, "createdUtc"), Text(node, "legacyKey", 300), parsed, game, missing, Flag(node, "favorite"), tagValues);
        }
    }
}
