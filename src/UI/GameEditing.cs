// Native editing of the local game library (cocoon.native-editor/1): one session per library
// page holds the editor binding, the draft revision and the staged entries. Every change is a
// local draft only; nothing here writes to the device or to the imported library.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace ThorCapture
{
    internal sealed class StagedAsset
    {
        public string EntryId, ItemId, CandidateId, SourceSha, BlobSha;
        public ArtworkSlot Slot;
        // The cropped output when there is one, otherwise the candidate itself.
        public string ShownSha { get { return BlobSha ?? SourceSha; } }
    }

    internal sealed class StagedMetadata
    {
        public string EntryId, ItemId;
        public Dictionary<string, object> Fields;
    }

    internal static class EditText
    {
        // Fields shown in the editor, in order. Logo placement and sort order stay with the web page.
        public static readonly string[] Fields = { "displayName", "title", "summary", "developer", "publisher", "releaseDate", "rating", "genres", "players", "region", "isFavorite", "isHidden" };

        public static string Label(string field)
        {
            switch (field)
            {
                case "displayName": return "显示名称";
                case "title": return "正式标题";
                case "summary": return "简介";
                case "developer": return "开发商";
                case "publisher": return "发行商";
                case "releaseDate": return "发售日期";
                case "rating": return "评分";
                case "genres": return "类型";
                case "players": return "玩家人数";
                case "region": return "地区";
                case "isFavorite": return "收藏";
                case "isHidden": return "隐藏";
                case "videoUrl": return "视频地址";
                case "launchboxVideoUrl": return "LaunchBox 视频地址";
                case "sortOrder": return "排序";
                case "logoAnchor": return "Logo 锚点";
                case "logoScaleX": return "Logo 横向缩放";
                case "logoScaleY": return "Logo 纵向缩放";
                case "logoPlacementOverridden": return "自定义 Logo 位置";
                default: return field;
            }
        }

        public static string SlotName(ArtworkSlot slot) { return slot == ArtworkSlot.Icon ? "封面" : slot == ArtworkSlot.Logo ? "Logo" : "背景"; }

        // releaseDate is stored as epoch milliseconds or seconds; show the calendar day.
        public static DateTime? Date(object value)
        {
            if (!(value is double)) return null;
            double n = (double)value;
            if (n <= 0) return null;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(n > 1e11 ? n : n * 1000).Date;
        }

        public static string Value(string field, object value)
        {
            if (value == null) return "（空）";
            if (value is bool) return (bool)value ? "是" : "否";
            if (field == "releaseDate") { DateTime? day = Date(value); return day.HasValue ? day.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "（空）"; }
            if (value is double) return ((double)value).ToString("0.##", CultureInfo.InvariantCulture);
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            return text.Length == 0 ? "（空）" : text;
        }

        public static string Error(string code)
        {
            switch (code ?? "")
            {
                case "EDITOR_REVISION_CONFLICT": case "EDITOR_BASE_CHANGED": return "草稿刚被其他操作修改，已重新读取，请再操作一次。";
                case "EDITOR_PREVIEW_EXPIRED": case "EDITOR_VARIANT_STALE": return "预览已过期（5 分钟），请重新生成。";
                case "EDITOR_ASSET_INVALID": case "EDITOR_ASSET_DECODE_FAILED": return "无法读取这张图片，请换一张 PNG、JPG、WebP 或 GIF。";
                case "EDITOR_ASSET_LIMIT": return "图片太大：文件需小于 32 MB，边长不超过 8192 像素。";
                case "EDITOR_CODEC_UNAVAILABLE": return "读取 JPG、WebP、GIF 需要 FFmpeg；PNG 可以直接使用。";
                case "EDITOR_DECODE_TIMEOUT": case "EDITOR_RENDER_TIMEOUT": return "处理图片超时，请稍后重试或换一张较小的图。";
                case "EDITOR_RENDER_BUSY": case "EDITOR_DRAFT_QUEUE_LIMIT": return "正在处理其他图片，请稍后再试。";
                case "EDITOR_CANDIDATE_LIMIT": return "这个槽位的候选图已达 128 张上限。";
                case "EDITOR_PENDING_LIMIT": case "EDITOR_DRAFT_LIMIT": return "暂存的修改太多，请先处理一部分。";
                case "EDITOR_LIBRARY_REQUIRED": return "还没有导入游戏库。";
                case "EDITOR_LIBRARY_CHANGED": case "EDITOR_DRAFT_CONFIRM_REQUIRED": return "现有草稿属于另一个游戏库，需要先归档。";
                case "EDITOR_DRAFT_CORRUPT": case "EDITOR_PENDING_INVALID": case "EDITOR_DRAFT_INVALID": return "草稿文件已损坏，为安全起见没有继续。";
                case "EDITOR_ITEM_NOT_FOUND": return "游戏库里已找不到这个游戏，请重新读取。";
                case "EDITOR_SESSION_STALE": case "BACKEND_NOT_READY": case "BACKEND_STOPPING": return "游戏库服务已重启，请重新打开。";
                case "EDITOR_TIMEOUT": case "EDITOR_CONNECTION_FAILED": case "BACKEND_TIMEOUT": return "游戏库服务没有响应，请稍后重试。";
                case "EDITOR_UPLOAD_SOURCE_FAILED": return "读取本地文件失败。";
                default: return "操作没有完成（" + (string.IsNullOrEmpty(code) ? "未知原因" : code) + "）。";
            }
        }

        public static string Code(Exception error)
        {
            NativeEditorException editor = error as NativeEditorException;
            if (editor != null) return editor.Code;
            BackendRequestException backend = error as BackendRequestException;
            if (backend != null) return backend.Code;
            return error is OperationCanceledException ? "EDITOR_CANCELLED" : "EDITOR_UNAVAILABLE";
        }
    }

    internal sealed class GameEditSession : IDisposable
    {
        private readonly CocoonBackend backend;
        private readonly SynchronizationContext ui;
        private NativeEditorClient client;
        private NativeEditorInfo info;
        private readonly Dictionary<int, string> itemIds = new Dictionary<int, string>();
        private readonly List<StagedAsset> assets = new List<StagedAsset>();
        private readonly List<StagedMetadata> metadata = new List<StagedMetadata>();
        private CancellationTokenSource stop = new CancellationTokenSource();
        private int epoch;
        private bool disposed, refreshing;
        public long Revision { get; private set; }
        public string Error { get; private set; }
        public bool Loaded { get; private set; }
        public event EventHandler Changed;

        public GameEditSession(CocoonBackend owner, SynchronizationContext context)
        {
            backend = owner;
            ui = context;
        }

        public CocoonBackend Backend { get { return backend; } }
        public string InstanceId { get { return info == null ? null : info.InstanceId; } }
        public IList<StagedAsset> Assets { get { return assets.AsReadOnly(); } }
        public IList<StagedMetadata> Metadata { get { return metadata.AsReadOnly(); } }
        public int Count { get { return assets.Count + metadata.Count; } }
        public CancellationToken Token { get { return stop.Token; } }
        public bool NeedsArchive { get { return Error == "EDITOR_LIBRARY_CHANGED" || Error == "EDITOR_DRAFT_CONFIRM_REQUIRED"; } }

        private void Raise() { if (!disposed && Changed != null) Changed(this, EventArgs.Empty); }

        // Backend restarted or library replaced: forget the binding and every staged entry.
        public void Reset()
        {
            epoch++;
            stop.Cancel(); stop.Dispose(); stop = new CancellationTokenSource();
            client = null; info = null; itemIds.Clear(); assets.Clear(); metadata.Clear();
            // A refresh still in flight belongs to the old epoch and no longer clears this itself.
            Revision = 0; Error = null; Loaded = false; refreshing = false;
            Raise();
        }

        private async Task<NativeEditorClient> Connect()
        {
            if (client != null && info != null) return client;
            NativeEditorClient next = new NativeEditorClient(backend, backend.CaptureNativeEditorTransport);
            int mine = epoch;
            NativeEditorInfo bound = await next.ConnectAsync(stop.Token);
            if (mine != epoch) throw new NativeEditorException("EDITOR_SESSION_STALE", 409);
            client = next; info = bound;
            return client;
        }

        // Re-reads the draft. Runs on the UI context; continuations come back to it.
        public async Task Refresh()
        {
            if (disposed || refreshing || backend.State != BackendState.Ready) return;
            refreshing = true;
            int mine = epoch;
            try
            {
                NativeEditorClient editor = await Connect();
                NativeEditorResult result = await editor.PendingAsync(stop.Token);
                if (mine != epoch) return;
                Apply(result);
                Error = null;
            }
            catch (Exception failure)
            {
                if (mine != epoch || failure is OperationCanceledException) return;
                Error = EditText.Code(failure);
                if (Error == "EDITOR_SESSION_STALE") { client = null; info = null; }
            }
            finally { if (mine == epoch) { refreshing = false; Loaded = true; Raise(); } }
        }

        // stage, undo and metadataConfirm all answer with the committed {revision, entries}.
        public void Apply(NativeEditorResult result)
        {
            Dictionary<string, object> data = result.Data;
            Revision = (long)Json.Number(data, "revision", 0);
            assets.Clear(); metadata.Clear();
            List<object> entries = Json.Get(data, "entries") as List<object>;
            if (entries != null)
                foreach (object entry in entries)
                {
                    string kind = Json.Text(entry, "kind");
                    if (kind == "asset")
                    {
                        ArtworkSlot slot;
                        if (!TryParseSlot(Json.Text(entry, "slot"), out slot)) continue;
                        assets.Add(new StagedAsset
                        {
                            EntryId = Json.Text(entry, "entryId"), ItemId = Json.Text(entry, "itemId"), Slot = slot,
                            CandidateId = Json.Text(entry, "candidateId"), SourceSha = Json.Text(entry, "sourceBlobSha256"), BlobSha = Json.Text(entry, "blobSha256")
                        });
                    }
                    else if (kind == "metadata")
                        metadata.Add(new StagedMetadata { EntryId = Json.Text(entry, "entryId"), ItemId = Json.Text(entry, "itemId"), Fields = Json.Get(entry, "fields") as Dictionary<string, object> ?? new Dictionary<string, object>() });
                }
            LearnItems();
            Error = null;
            Raise();
        }

        public static bool TryParseSlot(string text, out ArtworkSlot slot)
        {
            slot = ArtworkSlot.Icon;
            if (text == "icon") return true;
            if (text == "logo") { slot = ArtworkSlot.Logo; return true; }
            if (text == "hero") { slot = ArtworkSlot.Hero; return true; }
            return false;
        }

        // Catalog item id (legacy-item:N or unresolved-native:N) for a game; cached per session.
        public async Task<string> ItemIdAsync(int gameId)
        {
            string known;
            if (itemIds.TryGetValue(gameId, out known)) return known;
            int mine = epoch;
            CancellationToken token = stop.Token;
            object node = await Task.Run(() => backend.GetJson("/api/v2/catalog/items/legacy-item:" + gameId.ToString(CultureInfo.InvariantCulture), token, false), token);
            string id = Json.Text(node, "id");
            if (id == null || !System.Text.RegularExpressions.Regex.IsMatch(id, @"\A(legacy-item|unresolved-native):" + gameId.ToString(CultureInfo.InvariantCulture) + @"\z"))
                throw new NativeEditorException("EDITOR_ITEM_NOT_FOUND", 404);
            if (mine == epoch) itemIds[gameId] = id;
            return id;
        }

        // Synchronous lookups for painting; null until ItemIdAsync has resolved the game once.
        public string KnownItemId(int gameId) { string id; return itemIds.TryGetValue(gameId, out id) ? id : null; }

        public StagedAsset AssetFor(int gameId, ArtworkSlot slot)
        {
            string item = KnownItemId(gameId);
            if (item == null) return null;
            foreach (StagedAsset asset in assets) if (asset.ItemId == item && asset.Slot == slot) return asset;
            return null;
        }

        public StagedMetadata MetadataFor(int gameId)
        {
            string item = KnownItemId(gameId);
            if (item == null) return null;
            foreach (StagedMetadata entry in metadata) if (entry.ItemId == item) return entry;
            return null;
        }

        public static int GameOf(string itemId)
        {
            int colon = itemId == null ? -1 : itemId.IndexOf(':');
            int id;
            return colon > 0 && int.TryParse(itemId.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out id) ? id : -1;
        }

        // Remembers ids of entries already in the draft so the detail view can mark them without a lookup.
        public void LearnItems()
        {
            foreach (StagedAsset asset in assets) { int game = GameOf(asset.ItemId); if (game > 0) itemIds[game] = asset.ItemId; }
            foreach (StagedMetadata entry in metadata) { int game = GameOf(entry.ItemId); if (game > 0) itemIds[game] = entry.ItemId; }
        }

        public async Task<NativeEditorClient> Editor()
        {
            NativeEditorClient editor = await Connect();
            return editor;
        }

        public async Task Undo(string entryId)
        {
            NativeEditorClient editor = await Connect();
            int mine = epoch;
            NativeEditorResult result = await editor.UndoAsync(entryId, Revision, stop.Token);
            if (mine == epoch) Apply(result);
        }

        public async Task Archive()
        {
            NativeEditorClient editor = await Connect();
            await editor.ArchiveForeignAsync(true, stop.Token);
            Error = null;
            await Refresh();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            epoch++;
            stop.Cancel();
        }
    }
}
