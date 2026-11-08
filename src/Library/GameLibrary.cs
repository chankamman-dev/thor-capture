using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    // One game as the Cocoon backend lists it (read-only view; edits go through the backend).
    internal sealed class GameEntry
    {
        public int Id;
        public string Name, Platform, FileName, Summary, Developer, Publisher, Genres, Players, Region;
        public DateTime? Released;
        public double Rating;
        public bool Favorite, HasCover, HasLogo, HasHero;
        public string CoverSource, LogoSource, HeroSource;
        // Where each image lives on the device, as the catalog records it (a path or a document URI).
        public string CoverUri, LogoUri, HeroUri;
        public RectangleF Bounds;

        public int ArtCount { get { return (HasCover ? 1 : 0) + (HasLogo ? 1 : 0) + (HasHero ? 1 : 0); } }

        public static GameEntry From(object node)
        {
            object art = Json.Get(node, "art"), meta = Json.Get(node, "metadata");
            GameEntry game = new GameEntry
            {
                Id = (int)Json.Number(node, "id", -1),
                Name = Json.Text(node, "name") ?? Json.Text(node, "fileName") ?? "未命名",
                Platform = Json.Text(node, "platform") ?? "",
                FileName = Json.Text(node, "fileName") ?? "",
                Favorite = Json.Flag(node, "favorite"),
                Summary = Json.Text(meta, "summary"),
                Developer = Json.Text(meta, "developer"),
                Publisher = Json.Text(meta, "publisher"),
                Genres = Json.Text(meta, "genres"),
                Players = Json.Text(meta, "players"),
                Region = Json.Text(meta, "region"),
                Rating = Json.Number(meta, "rating", 0),
                CoverUri = Json.Text(Json.Get(art, "icon"), "uri"), LogoUri = Json.Text(Json.Get(art, "logo"), "uri"), HeroUri = Json.Text(Json.Get(art, "hero"), "uri"),
                HasCover = Json.Text(Json.Get(art, "icon"), "uri") != null,
                HasLogo = Json.Text(Json.Get(art, "logo"), "uri") != null,
                HasHero = Json.Text(Json.Get(art, "hero"), "uri") != null,
                CoverSource = Json.Text(Json.Get(art, "icon"), "source"),
                LogoSource = Json.Text(Json.Get(art, "logo"), "source"),
                HeroSource = Json.Text(Json.Get(art, "hero"), "source")
            };
            double released = Json.Number(meta, "releaseDate", 0);
            if (released > 0) game.Released = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(released > 1e11 ? released / 1000 : released);
            return game;
        }

        // Display names for platform ids.
        public static string PlatformName(string id)
        {
            switch ((id ?? "").ToLowerInvariant())
            {
                case "switch": return "Switch";
                case "nds": return "NDS";
                case "3ds": return "3DS";
                case "gba": return "GBA";
                case "gb": return "GB";
                case "gbc": return "GBC";
                case "psp": return "PSP";
                case "psx": case "ps1": return "PS1";
                case "ps2": return "PS2";
                case "snes": return "SFC";
                case "nes": return "FC";
                case "n64": return "N64";
                case "gc": case "gamecube": return "GC";
                case "wii": return "Wii";
                case "md": case "genesis": return "MD";
                default: return string.IsNullOrEmpty(id) ? "其他" : id.ToUpperInvariant();
            }
        }
    }

    internal sealed class PlatformCount
    {
        public string Id;
        public int Visible, Missing;
    }

    // Artwork thumbnails from the backend (cocoon.thumbnail/1): at most four requests in
    // flight, sized to what is drawn, decoded off the UI thread and kept in a bounded cache.
    // After a library change entries are revalidated with their ETag; 304 keeps the bitmap.
    // Ready fires on the UI thread.
    internal sealed class GameArtCache : IDisposable
    {
        public enum Kind { Cover, Logo, Hero }

        private sealed class Entry
        {
            public Bitmap Image;
            public string ETag;
            public int Generation;
            public bool Missing, Pending;
        }

        private readonly ArtworkThumbnailClient thumbnails;
        private readonly SynchronizationContext ui;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private readonly LinkedList<string> order = new LinkedList<string>();
        private readonly SemaphoreSlim workers = new SemaphoreSlim(4);
        private CancellationTokenSource cancel = new CancellationTokenSource();
        private int generation, session;
        private bool disposed;
        private const int Capacity = 200;
        public event EventHandler Ready;
        internal int Requests, Revalidated;

        public GameArtCache(CocoonBackend backend, SynchronizationContext context)
        {
            thumbnails = new ArtworkThumbnailClient(backend);
            ui = context;
        }

        private static int Bucket(int edge) { return Math.Max(64, Math.Min(1024, (edge + 63) / 64 * 64)); }

        // Returns the cached image (owned by the cache; do not dispose) or null, queueing a fetch.
        // `edge` is the drawn size in pixels; 0 uses a default for the slot.
        public Bitmap Get(int id, Kind kind, int edge = 0)
        {
            if (disposed || id <= 0) return null;
            int w, h;
            if (kind == Kind.Cover) { w = h = Bucket(edge > 0 ? edge : 320); }
            else if (kind == Kind.Logo) { w = Bucket(edge > 0 ? edge : 640); h = Math.Max(64, w / 2); }
            else { w = Bucket(edge > 0 ? edge : 1024); h = Math.Max(64, w * 9 / 16); }
            string key = id.ToString(CultureInfo.InvariantCulture) + ":" + (int)kind + ":" + w + "x" + h;
            Entry entry;
            if (!entries.TryGetValue(key, out entry)) { entry = new Entry { Generation = -1 }; entries[key] = entry; }
            else { order.Remove(key); }
            order.AddFirst(key);
            if (!entry.Pending && entry.Generation != generation) Fetch(key, entry, id, kind, w, h);
            return entry.Missing ? null : entry.Image;
        }

        private void Fetch(string key, Entry entry, int id, Kind kind, int w, int h)
        {
            entry.Pending = true;
            int token = generation, owner = session;
            string etag = entry.Image != null ? entry.ETag : null;
            CancellationToken stop = cancel.Token;
            ArtworkSlot slot = kind == Kind.Cover ? ArtworkSlot.Icon : kind == Kind.Logo ? ArtworkSlot.Logo : ArtworkSlot.Hero;
            Requests++;
            Task.Run(async delegate
            {
                ArtworkThumbnail result = null; Bitmap decoded = null; bool absent = false;
                try
                {
                    await workers.WaitAsync(stop);
                    try
                    {
                        result = await thumbnails.GetAsync(id, slot, w, h, kind == Kind.Cover ? ArtworkFit.Cover : ArtworkFit.Contain, etag, stop);
                        if (!result.NotModified)
                            using (MemoryStream input = new MemoryStream(result.Bytes))
                            using (Image image = Image.FromStream(input))
                                decoded = new Bitmap(image);
                    }
                    finally { workers.Release(); }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { absent = true; } // 404 / unsupported / codec: show the placeholder
                ui.Post(delegate { Arrive(key, entry, token, owner, result, decoded, absent); }, null);
            });
        }

        private void Arrive(string key, Entry entry, int token, int owner, ArtworkThumbnail result, Bitmap decoded, bool absent)
        {
            Entry current;
            bool live = !disposed && owner == session && entries.TryGetValue(key, out current) && ReferenceEquals(current, entry);
            if (!live) { if (decoded != null) decoded.Dispose(); return; }
            entry.Pending = false;
            entry.Generation = token;
            if (result == null && !absent) { entry.Generation = -1; return; } // cancelled: fetch again next time
            if (absent) { entry.Missing = true; if (entry.Image != null) { entry.Image.Dispose(); entry.Image = null; } }
            else if (result.NotModified) { entry.Missing = false; Revalidated++; }
            else
            {
                if (entry.Image != null) entry.Image.Dispose();
                entry.Image = decoded; entry.ETag = result.ETag; entry.Missing = false;
            }
            Trim();
            if (Ready != null) Ready(this, EventArgs.Empty);
        }

        private void Trim()
        {
            while (order.Count > Capacity)
            {
                string last = order.Last.Value;
                Entry entry = entries[last];
                if (entry.Pending) break;
                order.RemoveLast();
                if (entry.Image != null) entry.Image.Dispose();
                entries.Remove(last);
            }
        }

        // The library changed (import, restore, reload): keep images, revalidate on next use.
        public void Invalidate() { generation++; }

        // Backend restarted or page closing: cancel requests and drop everything.
        public void Clear()
        {
            session++; generation++;
            cancel.Cancel();
            cancel.Dispose();
            cancel = new CancellationTokenSource();
            foreach (Entry entry in entries.Values) if (entry.Image != null) entry.Image.Dispose();
            entries.Clear(); order.Clear();
        }

        public void Dispose() { if (disposed) return; Clear(); disposed = true; }
    }
}
