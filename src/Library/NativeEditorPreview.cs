using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal sealed partial class NativeEditorHttpTransport
    {
        // Capture this transport before queueing work: a delayed request must never retarget a new owner.
        internal Task<ArtworkThumbnail> ThumbnailAsync(string path, string etag, CancellationToken cancellation)
        {
            Uri target;
            if (path == null || !(path.StartsWith("/api/art/thumbnail?", StringComparison.Ordinal)
                || path.StartsWith("/api/native-editor/preview?", StringComparison.Ordinal))
                || !Uri.TryCreate(root, path, out target) || target.Authority != root.Authority
                || target.Scheme != root.Scheme || target.Fragment.Length != 0)
                throw new ArgumentException("Invalid thumbnail route");
            if (etag != null && !Regex.IsMatch(etag, @"\A""cth1-[a-f0-9]{64}""\z"))
                throw new ArgumentException("Invalid thumbnail validator");
            return Task.Run(delegate
            {
                using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    deadline.CancelAfter(15000);
                    CheckThumbnailContext(cancellation, deadline.Token);
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(target);
                    request.Method = "GET"; request.Timeout = request.ReadWriteTimeout = 15000;
                    request.AllowAutoRedirect = false; request.Proxy = null; request.ServicePoint.ConnectionLimit = 4;
                    request.Headers["X-Cocoon-Art-Token"] = token;
                    if (etag != null) request.Headers[HttpRequestHeader.IfNoneMatch] = etag;
                    using (deadline.Token.Register(request.Abort))
                    {
                        try
                        {
                            ArtworkThumbnail result;
                            try
                            {
                                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                                    result = ArtworkThumbnail.Read(response, etag);
                            }
                            catch (WebException failure)
                            {
                                HttpWebResponse response = failure.Response as HttpWebResponse;
                                if (response != null && response.StatusCode == HttpStatusCode.NotModified)
                                    using (response) result = ArtworkThumbnail.Read(response, etag);
                                else throw CocoonBackend.RequestFailure(failure);
                            }
                            CheckThumbnailContext(cancellation, deadline.Token);
                            return result;
                        }
                        catch (Exception failure)
                        {
                            // Abort during body reads can be surfaced as IO/disposal errors, not just WebException.
                            CheckThumbnailContext(cancellation, deadline.Token);
                            if (failure is BackendRequestException) throw;
                            if (failure is IOException || failure is ObjectDisposedException || failure is WebException)
                                throw new BackendRequestException("BACKEND_REQUEST_FAILED", 0);
                            throw;
                        }
                    }
                }
            }, cancellation);
        }

        private void CheckThumbnailContext(CancellationToken cancellation, CancellationToken deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!current()) throw new BackendRequestException("BACKEND_SESSION_STALE", 409);
            if (deadline.IsCancellationRequested) throw new BackendRequestException("BACKEND_TIMEOUT", 0);
        }
    }

    internal sealed partial class NativeEditorClient
    {
        // Candidate/crop pixels stay bound to the same editor and library as the other editor operations.
        public async Task<ArtworkThumbnail> PreviewAsync(string itemId, ArtworkSlot slot, string blobSha256,
            int width, int height, ArtworkFit fit, string previousEtag, CancellationToken cancellation)
        {
            string item = Item(itemId), name = Slot(slot);
            if (!Regex.IsMatch(blobSha256 ?? "", @"\A[a-f0-9]{64}\z") || width < 16 || width > 1024
                || height < 16 || height > 1024 || !Enum.IsDefined(typeof(ArtworkFit), fit))
                throw new ArgumentException("Invalid native preview request");
            if (previousEtag != null && !Regex.IsMatch(previousEtag, @"\A""cth1-[a-f0-9]{64}""\z"))
                throw new ArgumentException("Invalid thumbnail validator");
            cancellation.ThrowIfCancellationRequested();
            NativeEditorInfo snapshot; Uri address; Snapshot(out snapshot, out address);
            NativeEditorHttpTransport transport = Capture(address);
            await VerifyContext(transport, snapshot, address, cancellation).ConfigureAwait(false);
            string path = "/api/native-editor/preview?instanceId=" + Uri.EscapeDataString(snapshot.InstanceId)
                + "&itemId=" + Uri.EscapeDataString(item) + "&slot=" + name + "&blob=" + blobSha256
                + "&width=" + width.ToString(CultureInfo.InvariantCulture) + "&height=" + height.ToString(CultureInfo.InvariantCulture)
                + "&fit=" + fit.ToString().ToLowerInvariant();
            ArtworkThumbnail result;
            try { result = await transport.ThumbnailAsync(path, previousEtag, cancellation).ConfigureAwait(false); }
            catch (BackendRequestException failure)
            {
                throw new NativeEditorException(failure.Code == "BACKEND_SESSION_STALE" ? "EDITOR_SESSION_STALE"
                    : failure.Code == "BACKEND_TIMEOUT" ? "EDITOR_TIMEOUT" : failure.Code, failure.Status);
            }
            CheckContext(snapshot, address);
            if (result.SourceSha256 != blobSha256 || result.Width > width || result.Height > height) throw Invalid();
            await VerifyContext(transport, snapshot, address, cancellation).ConfigureAwait(false);
            return result;
        }
    }
}
