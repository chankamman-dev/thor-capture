using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal enum ArtworkSlot { Icon, Logo, Hero }
    internal enum ArtworkFit { Contain, Cover }
    internal sealed class ArtworkThumbnail
    {
        public const string Contract = "cocoon.thumbnail/1";
        public readonly byte[] Bytes;
        public readonly string ETag, SourceSha256, Cache;
        public readonly int Width, Height;
        public readonly bool NotModified;
        private ArtworkThumbnail(byte[] bytes, string etag, string source, string cache, int width, int height, bool notModified)
        { Bytes = bytes; ETag = etag; SourceSha256 = source; Cache = cache; Width = width; Height = height; NotModified = notModified; }
        private static BackendRequestException Invalid() { return new BackendRequestException("BACKEND_RESPONSE_INVALID", 0); }
        internal static ArtworkThumbnail Read(HttpWebResponse response, string previous)
        {
            bool unchanged = response.StatusCode == HttpStatusCode.NotModified; int width, height;
            string etag = response.Headers["ETag"], source = response.Headers["X-Thumbnail-Source-SHA256"], cache = response.Headers["X-Thumbnail-Cache"];
            if (response.Headers["X-Cocoon-Thumbnail-Contract"] != Contract || (!unchanged && response.StatusCode != HttpStatusCode.OK)
                || !Regex.IsMatch(etag ?? "", @"\A""cth1-[a-f0-9]{64}""\z") || !Regex.IsMatch(source ?? "", @"\A[a-f0-9]{64}\z")
                || !int.TryParse(response.Headers["X-Thumbnail-Width"], out width) || !int.TryParse(response.Headers["X-Thumbnail-Height"], out height)
                || width < 1 || width > 1024 || height < 1 || height > 1024 || (unchanged && etag != previous)) throw Invalid();
            if (unchanged) return new ArtworkThumbnail(null, etag, source, cache, width, height, true);
            if (response.ContentType != "image/png" || response.ContentLength > 8 * 1024 * 1024) throw Invalid();
            using (Stream input = response.GetResponseStream())
            using (MemoryStream copy = new MemoryStream())
            {
                byte[] chunk = new byte[32768]; int read;
                while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
                { copy.Write(chunk, 0, read); if (copy.Length > 8 * 1024 * 1024) throw new BackendRequestException("BACKEND_RESPONSE_TOO_LARGE", 0); }
                byte[] bytes = copy.ToArray();
                byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
                if (bytes.Length < 33) throw Invalid();
                for (int i = 0; i < signature.Length; i++) if (bytes[i] != signature[i]) throw Invalid();
                int actualWidth = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                int actualHeight = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                if (actualWidth != width || actualHeight != height) throw Invalid();
                return new ArtworkThumbnail(bytes, etag, source, cache, width, height, false);
            }
        }
    }
    // Backend contract only. The frontend owns decoded Bitmaps and its visible-item cache.
    internal sealed class ArtworkThumbnailClient
    {
        private readonly CocoonBackend backend;
        public ArtworkThumbnailClient(CocoonBackend owner) { if (owner == null) throw new ArgumentNullException("owner"); backend = owner; }
        private static string Route(long gameId, ArtworkSlot slot, int width, int height, ArtworkFit fit, string source, string extension)
        {
            if (gameId <= 0 || gameId > 999999999999999L || width < 16 || width > 1024 || height < 16 || height > 1024
                || !Enum.IsDefined(typeof(ArtworkSlot), slot) || !Enum.IsDefined(typeof(ArtworkFit), fit)) throw new ArgumentException("Invalid thumbnail request");
            return "/api/art/thumbnail?id=" + gameId.ToString(CultureInfo.InvariantCulture) + "&type=" + slot.ToString().ToLowerInvariant()
                + "&width=" + width + "&height=" + height + "&fit=" + fit.ToString().ToLowerInvariant() + "&source=" + source + "&ext=" + extension;
        }
        public Task<ArtworkThumbnail> GetAsync(long gameId, ArtworkSlot slot, int width, int height, ArtworkFit fit = ArtworkFit.Contain,
            string previousEtag = null, CancellationToken cancellation = default(CancellationToken))
        {
            string route = Route(gameId, slot, width, height, fit, "current", "png");
            cancellation.ThrowIfCancellationRequested();
            return backend.CaptureNativeEditorTransport().ThumbnailAsync(route, previousEtag, cancellation);
        }
        public Task<ArtworkThumbnail> GetStagedAsync(long gameId, ArtworkSlot slot, string extension, int width, int height,
            ArtworkFit fit = ArtworkFit.Contain, string previousEtag = null, CancellationToken cancellation = default(CancellationToken))
        {
            if (extension != "jpg" && extension != "png" && extension != "webp" && extension != "gif") throw new ArgumentException("Invalid staged image extension");
            string route = Route(gameId, slot, width, height, fit, "staged", extension);
            cancellation.ThrowIfCancellationRequested();
            return backend.CaptureNativeEditorTransport().ThumbnailAsync(route, previousEtag, cancellation);
        }
        // Pixels of a native-editor candidate or of a crop rendered from one, scoped to the editor instance and item/slot.
        public Task<ArtworkThumbnail> GetNativeAsync(string editorInstance, string itemId, ArtworkSlot slot, string blobSha256, int width, int height,
            ArtworkFit fit = ArtworkFit.Contain, string previousEtag = null, CancellationToken cancellation = default(CancellationToken))
        {
            if (!Regex.IsMatch(editorInstance ?? "", @"\A[a-f0-9]{8}(-[a-f0-9]{4}){3}-[a-f0-9]{12}\z") || !Regex.IsMatch(itemId ?? "", @"\A(legacy-item|unresolved-native):[1-9][0-9]{0,14}\z")
                || !Regex.IsMatch(blobSha256 ?? "", @"\A[a-f0-9]{64}\z") || width < 16 || width > 1024 || height < 16 || height > 1024
                || !Enum.IsDefined(typeof(ArtworkSlot), slot) || !Enum.IsDefined(typeof(ArtworkFit), fit)) throw new ArgumentException("Invalid native preview request");
            string route = "/api/native-editor/preview?instanceId=" + editorInstance + "&itemId=" + Uri.EscapeDataString(itemId) + "&slot=" + slot.ToString().ToLowerInvariant()
                + "&blob=" + blobSha256 + "&width=" + width + "&height=" + height + "&fit=" + fit.ToString().ToLowerInvariant();
            return Task.Run(() => backend.GetThumbnail(route, previousEtag, cancellation), cancellation);
        }
    }
}
