// Scraping through the owned backend: which sources are usable, searching one game, the stored
// candidates, and turning a chosen candidate into catalog field values for review. Nothing here
// stages or applies anything; chosen values go through the editor's own preview and confirm.
// All calls block on HTTP: use them from worker threads (the Async wrappers do).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal sealed class ScrapeProvider
    {
        public string Id, Label;
        public bool Configured, Metadata, Assets;
    }

    internal sealed class ScrapeCandidate
    {
        public long Id;
        public string Provider, Title, Platform, Decision, Summary, Year;
        public double Confidence;
        public bool Chosen;
    }

    internal sealed class ScrapeAsset
    {
        public string Url, Thumb, Style, Provider, Mime;
        public int Width, Height;
    }

    // Fetches artwork offered by scrape sources: HTTPS only, no redirects off HTTPS, bounded size.
    internal static class WebImage
    {
        public const int MaxBytes = 32 * 1024 * 1024;

        public static bool Allowed(string url)
        {
            Uri parsed;
            return url != null && Uri.TryCreate(url, UriKind.Absolute, out parsed) && parsed.Scheme == Uri.UriSchemeHttps && parsed.UserInfo.Length == 0 && !parsed.IsLoopback;
        }

        public static byte[] Download(string url, int maxBytes, int timeoutMs, CancellationToken token)
        {
            if (!Allowed(url)) throw new System.Net.WebException("URL not allowed");
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            System.Net.HttpWebRequest request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            request.Timeout = request.ReadWriteTimeout = timeoutMs;
            request.AllowAutoRedirect = true; request.MaximumAutomaticRedirections = 3;
            request.UserAgent = "ThorCapture";
            using (token.Register(() => request.Abort()))
            using (System.Net.HttpWebResponse response = (System.Net.HttpWebResponse)request.GetResponse())
            {
                if (response.ResponseUri.Scheme != Uri.UriSchemeHttps || response.ContentLength > maxBytes) throw new System.Net.WebException("Response not allowed");
                using (System.IO.Stream stream = response.GetResponseStream())
                using (System.IO.MemoryStream copy = new System.IO.MemoryStream())
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        copy.Write(buffer, 0, read);
                        if (copy.Length > maxBytes) throw new System.Net.WebException("Response too large");
                    }
                    return copy.ToArray();
                }
            }
        }
    }

    internal sealed class ScrapeSearch
    {
        public List<ScrapeCandidate> Candidates = new List<ScrapeCandidate>();
        public List<string> Errors = new List<string>(); // "source: message", as reported by the backend
    }

    internal sealed class ScrapeClient
    {
        public static readonly string[] CredentialKeys = { "sgdbKey", "thegamesdbKey", "screenscraperUser", "screenscraperPassword", "screenscraperDevId",
            "screenscraperDevPassword", "retroachievementsKey", "igdbClientId", "igdbToken" };
        private readonly CocoonBackend backend;
        public ScrapeClient(CocoonBackend owner) { if (owner == null) throw new ArgumentNullException("owner"); backend = owner; }

        public static string ProviderName(string id)
        {
            switch (id)
            {
                case "steam": return "Steam";
                case "steamgriddb": return "SteamGridDB";
                case "thegamesdb": return "TheGamesDB";
                case "screenscraper": return "ScreenScraper";
                case "retroachievements": return "RetroAchievements";
                case "igdb": return "IGDB";
                default: return id ?? "";
            }
        }

        public Task<List<ScrapeProvider>> ProvidersAsync(CancellationToken token)
        {
            return Task.Run(delegate
            {
                List<ScrapeProvider> result = new List<ScrapeProvider>();
                List<object> rows = backend.GetJson("/api/scrape/providers", token, false) as List<object>;
                if (rows == null) throw new BackendRequestException("BACKEND_RESPONSE_INVALID", 0);
                foreach (object row in rows)
                {
                    List<object> capabilities = Json.Get(row, "capabilities") as List<object> ?? new List<object>();
                    string id = Json.Text(row, "id");
                    if (id == null) continue;
                    result.Add(new ScrapeProvider { Id = id, Label = ProviderName(id), Configured = Json.Flag(row, "configured"), Metadata = capabilities.Contains("metadata"), Assets = capabilities.Contains("assets") });
                }
                return result;
            }, token);
        }

        // Asks the source itself whether the stored credentials work; returns the backend's one-line verdict.
        public Task<KeyValuePair<bool, string>> TestAsync(string provider, CancellationToken token)
        {
            return Task.Run(delegate
            {
                object reply = backend.PostJson("/api/config/test", "{\"provider\":" + Json.Quote(provider) + ",\"config\":{}}", 45000, token);
                return new KeyValuePair<bool, string>(Json.Flag(reply, "ok"), Json.Text(reply, "message") ?? Json.Text(reply, "error") ?? "");
            }, token);
        }

        // Stores credentials in the backend's own config; only known keys, never echoed back.
        public Task SaveCredentialsAsync(IDictionary<string, string> values, CancellationToken token)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            foreach (KeyValuePair<string, string> pair in values)
            {
                if (Array.IndexOf(CredentialKeys, pair.Key) < 0 || pair.Value == null || pair.Value.Length > 512) throw new ArgumentException("Unknown credential");
                body[pair.Key] = pair.Value.Trim();
            }
            string json = NativeEditorJson.Encode(body);
            return Task.Run(delegate { backend.PostJson("/api/config", json, 15000, token); }, token);
        }

        private static ScrapeCandidate Candidate(object row)
        {
            object metadata = Json.Get(row, "metadata");
            string year = null;
            List<object> dates = Json.Get(metadata, "releaseDates") as List<object>;
            if (dates != null && dates.Count > 0) { string date = Json.Text(dates[0], "date"); if (date != null && date.Length >= 4) year = date.Substring(0, 4); }
            return new ScrapeCandidate
            {
                Id = (long)Json.Number(row, "id", 0), Provider = Json.Text(row, "provider"), Title = Json.Text(row, "title") ?? Json.Text(metadata, "canonicalTitle") ?? "",
                Platform = Json.Text(row, "platformId") ?? "", Decision = Json.Text(row, "decision") ?? "", Summary = Json.Text(metadata, "summary"), Year = year,
                Confidence = Json.Number(row, "confidence", 0), Chosen = Json.Text(row, "status") == "chosen"
            };
        }

        public Task<List<ScrapeCandidate>> CandidatesAsync(int gameId, CancellationToken token)
        {
            return Task.Run(delegate
            {
                List<ScrapeCandidate> result = new List<ScrapeCandidate>();
                List<object> rows = backend.GetJson("/api/scrape/candidates?id=" + gameId.ToString(CultureInfo.InvariantCulture) + "&limit=60", token, false) as List<object>;
                if (rows != null) foreach (object row in rows) { ScrapeCandidate item = Candidate(row); if (item.Id > 0) result.Add(item); }
                return result;
            }, token);
        }

        // One game, the chosen sources, optionally a title to search for instead of the game's own.
        public Task<ScrapeSearch> SearchAsync(int gameId, IList<string> providers, string term, CancellationToken token)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["id"] = (long)gameId; body["providers"] = new List<object>(providers); body["mode"] = "balanced"; body["language"] = "zh"; body["hash"] = false;
            body["includeAllPlatforms"] = providers.Contains("steam"); // Steam only lists PC releases; without this it answers nothing for console games
            if (!string.IsNullOrWhiteSpace(term)) { body["term"] = term.Trim(); body["strictTerm"] = true; }
            string json = NativeEditorJson.Encode(body);
            return Task.Run(delegate
            {
                object reply = backend.PostJson("/api/scrape/search", json, 150000, token);
                ScrapeSearch result = new ScrapeSearch();
                List<object> rows = Json.Get(reply, "candidates") as List<object>;
                if (rows != null) foreach (object row in rows) { ScrapeCandidate item = Candidate(row); if (item.Id > 0) result.Candidates.Add(item); }
                HashSet<string> seen = new HashSet<string>();
                List<object> errors = Json.Get(reply, "errors") as List<object>;
                if (errors != null)
                    foreach (object error in errors)
                    {
                        string line = ProviderName(Json.Text(error, "provider")) + "：" + (Json.Text(error, "message") ?? Json.Text(error, "code") ?? "失败");
                        if (seen.Add(line)) result.Errors.Add(line);
                    }
                return result;
            }, token);
        }

        // Chooses a candidate (the backend fetches its full details) and returns what it offers as catalog
        // fields: field name -> value in the catalog's own form. Locked fields are left out.
        public Task<Dictionary<string, object>> SelectAsync(int gameId, long candidateId, CancellationToken token)
        {
            string json = "{\"id\":" + gameId.ToString(CultureInfo.InvariantCulture) + ",\"candidateId\":" + candidateId.ToString(CultureInfo.InvariantCulture) + ",\"language\":\"zh\"}";
            return Task.Run(delegate
            {
                object merge = Json.Get(backend.PostJson("/api/scrape/select", json, 150000, token), "merge");
                return NativeFields(Json.Get(merge, "selected") as Dictionary<string, object>, Json.Get(merge, "locks") as Dictionary<string, object>);
            }, token);
        }

        // Same projection as the backend's lib/metadata/nativeSelection.js, ending in catalog values.
        internal static Dictionary<string, object> NativeFields(Dictionary<string, object> selected, Dictionary<string, object> locks)
        {
            Dictionary<string, object> output = new Dictionary<string, object>();
            if (selected == null) return output;
            foreach (KeyValuePair<string, object> pair in selected)
            {
                string native = Json.Text(pair.Value, "nativeField");
                if (native == null || (locks != null && locks.ContainsKey(pair.Key))) continue;
                object value = Json.Get(pair.Value, "value"), result = null;
                if (pair.Key == "canonicalTitle" || pair.Key == "summary") result = value as string;
                else if (pair.Key == "localizedTitles") { string name = Json.Text(value, "zhHans"); result = string.IsNullOrEmpty(name) ? Json.Text(value, "zhHant") : name; }
                else if (pair.Key == "developer" || pair.Key == "publisher" || pair.Key == "genres" || pair.Key == "region") result = Joined(value);
                else if (pair.Key == "releaseDates")
                {
                    List<object> dates = value as List<object>;
                    DateTime day;
                    if (dates != null && dates.Count > 0 && DateTime.TryParseExact(Json.Text(dates[0], "date") ?? "", new[] { "yyyy-MM-dd", "yyyy-MM", "yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
                        result = (DateTime.SpecifyKind(day, DateTimeKind.Utc) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                }
                else if (pair.Key == "rating") { double rating = Json.Number(value, "value", 0); if (rating > 0 && rating <= 100) result = rating; }
                else if (pair.Key == "players")
                {
                    double low = Json.Number(value, "offlineMin", 0), high = Json.Number(value, "offlineMax", 0), online = Json.Number(value, "onlineMax", 0);
                    if (low > 0 || high > 0 || online > 0)
                        result = "{\"offlineMin\":" + ((long)low).ToString(CultureInfo.InvariantCulture) + ",\"offlineMax\":" + ((long)high).ToString(CultureInfo.InvariantCulture) + ",\"onlineMax\":" + ((long)online).ToString(CultureInfo.InvariantCulture) + "}";
                }
                string text = result as string;
                if (result == null || (text != null && text.Trim().Length == 0)) continue;
                output[native] = text != null ? (object)text.Trim() : result;
            }
            return output;
        }

        // Artwork a source offers for one of its candidates, for one slot. Only HTTPS images are kept.
        public Task<List<ScrapeAsset>> AssetsAsync(int gameId, ScrapeCandidate candidate, ArtworkSlot slot, CancellationToken token)
        {
            string type = slot == ArtworkSlot.Icon ? "icon" : slot == ArtworkSlot.Logo ? "logo" : "hero";
            string json = "{\"id\":" + gameId.ToString(CultureInfo.InvariantCulture) + ",\"candidateId\":" + candidate.Id.ToString(CultureInfo.InvariantCulture) + ",\"type\":\"" + type + "\",\"language\":\"zh\"}";
            return Task.Run(delegate
            {
                List<ScrapeAsset> result = new List<ScrapeAsset>();
                object reply = backend.PostJson("/api/scrape/assets", json, 90000, token);
                List<object> rows = reply as List<object> ?? Json.Get(reply, "assets") as List<object> ?? Json.Get(reply, "items") as List<object>;
                if (rows == null) return result;
                foreach (object row in rows)
                {
                    string url = Json.Text(row, "url"), thumb = Json.Text(row, "thumb") ?? url;
                    if (!WebImage.Allowed(url) || !WebImage.Allowed(thumb)) continue;
                    result.Add(new ScrapeAsset { Url = url, Thumb = thumb, Width = (int)Json.Number(row, "width", 0), Height = (int)Json.Number(row, "height", 0),
                        Style = Json.Text(row, "style") ?? Json.Text(row, "label"), Provider = candidate.Provider, Mime = Json.Text(row, "mime") });
                }
                return result;
            }, token);
        }

        private static string Joined(object value)
        {
            List<object> list = value as List<object>;
            if (list == null) return value as string;
            List<string> parts = new List<string>();
            foreach (object item in list) { string text = item as string; if (!string.IsNullOrWhiteSpace(text)) parts.Add(text.Trim()); }
            return string.Join(", ", parts.ToArray());
        }
    }
}
