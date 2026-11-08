// The local draft at a glance: every staged change with an undo, and the one explicit step
// that archives a draft left over from a different library. Nothing here applies anything.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThorCapture
{
    internal sealed class PendingDialog : ThemedDialog
    {
        private readonly GameEditSession session;
        private readonly Func<int, string> nameOf;
        private readonly PendingList list = new PendingList();
        private readonly FlatButton archive, apply;
        public bool ApplyRequested { get; private set; }
        private readonly EventHandler changed;
        private string error;
        private bool busy;
        internal string Describe { get { return "rows=" + list.Count + " archive=" + session.NeedsArchive + " busy=" + busy + " error=" + error; } }

        public PendingDialog(GameEditSession owner, Func<int, string> gameName) : base("暂存的修改", null, null, 560, 420)
        {
            session = owner;
            nameOf = gameName;
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(Body, true, null);
            Body.Paint += PaintCanvas;
            Body.Controls.Add(list);
            list.SetBounds(Ui.S(24), Ui.S(12), Ui.S(512), Ui.S(352));
            list.Undo += delegate(object sender, PendingList.Row row) { Undo(row.EntryId); };
            AddAction("关闭", ButtonKind.Subtle, DialogResult.Cancel);
            archive = AddAction("归档旧草稿", ButtonKind.Primary, DialogResult.None);
            archive.Click += delegate { Archive(); };
            // Applying is its own reviewed flow; this only hands over to it.
            apply = AddAction("应用到掌机…", ButtonKind.Primary, DialogResult.None);
            apply.Click += delegate { if (busy) return; ApplyRequested = true; Close(); };
            changed = delegate { Rebuild(); };
            session.Changed += changed;
            FormClosed += delegate { session.Changed -= changed; };
            Rebuild();
        }

        private void Rebuild()
        {
            if (IsDisposed) return;
            List<PendingList.Row> rows = new List<PendingList.Row>();
            foreach (StagedMetadata entry in session.Metadata)
            {
                List<string> labels = new List<string>();
                foreach (string field in EditText.Fields) if (entry.Fields.ContainsKey(field)) labels.Add(EditText.Label(field));
                foreach (string field in entry.Fields.Keys) if (Array.IndexOf(EditText.Fields, field) < 0) labels.Add(EditText.Label(field));
                rows.Add(new PendingList.Row { EntryId = entry.EntryId, Title = nameOf(GameEditSession.GameOf(entry.ItemId)), Detail = "资料：" + string.Join("、", labels.ToArray()) });
            }
            foreach (StagedAsset asset in session.Assets)
                rows.Add(new PendingList.Row { EntryId = asset.EntryId, Title = nameOf(GameEditSession.GameOf(asset.ItemId)), Detail = "素材：" + EditText.SlotName(asset.Slot) + (asset.BlobSha != null ? "（已裁剪）" : "") });
            list.SetRows(rows);
            list.Visible = !session.NeedsArchive;
            list.Enabled = !busy;
            archive.Visible = session.NeedsArchive;
            archive.Enabled = !busy;
            apply.Visible = !session.NeedsArchive && rows.Count > 0;
            apply.Enabled = !busy;
            Body.Invalidate();
        }

        private async void Undo(string entryId)
        {
            if (busy) return;
            busy = true; error = null; Rebuild();
            string code = null;
            try { await session.Undo(entryId); }
            catch (Exception failure) { code = EditText.Code(failure); }
            // A failed or cancelled undo may still have committed: show what the draft holds now.
            if (code != null) { error = EditText.Error(code); await session.Refresh(); }
            busy = false;
            Rebuild();
        }

        private async void Archive()
        {
            if (busy) return;
            busy = true; error = null; Rebuild();
            string code = null;
            try { await session.Archive(); }
            catch (Exception failure) { code = EditText.Code(failure); }
            if (code != null) { error = EditText.Error(code); await session.Refresh(); }
            busy = false;
            Rebuild();
        }

        private void PaintCanvas(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Ui.S(24), width = Ui.S(512);
            if (session.NeedsArchive)
            {
                Ui.Write(g, "现有草稿属于另一个游戏库", Ui.Strong, new Rectangle(pad, Ui.S(130), width, Ui.S(24)), Ui.Text, TextFormatFlags.HorizontalCenter);
                Ui.Write(g, "归档后，旧草稿文件原样保留在本机，当前游戏库从空草稿开始。不归档就无法在这个游戏库里暂存修改。", Ui.Body,
                    new Rectangle(pad + Ui.S(40), Ui.S(162), width - Ui.S(80), Ui.S(60)), Ui.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            }
            else if (list.Count == 0)
                Ui.Write(g, session.Loaded ? "没有暂存的修改" : "正在读取草稿…", Ui.Body, new Rectangle(0, Ui.S(170), Body.Width, Ui.S(24)), Ui.TextDim, TextFormatFlags.HorizontalCenter);
            string line = error ?? (session.NeedsArchive ? null : "这些修改只保存在本机草稿里。点「应用到掌机…」审阅并确认后，才会写入掌机。");
            if (line == null) return;
            Color tone = error != null ? Ui.Danger : Ui.TextFaint;
            using (SolidBrush dot = new SolidBrush(tone)) g.FillEllipse(dot, pad, Ui.S(380) + Ui.S(6), Ui.S(6), Ui.S(6));
            Ui.Write(g, line, Ui.Small, new Rectangle(pad + Ui.S(14), Ui.S(380), width - Ui.S(14), Ui.S(36)), error != null ? Ui.Text : Ui.TextDim, TextFormatFlags.Left | TextFormatFlags.WordBreak);
        }
    }

    // One row per staged entry: the game, what was staged, and an undo pill on the right.
    internal sealed class PendingList : Control
    {
        public sealed class Row { public string EntryId, Title, Detail; }
        private List<Row> rows = new List<Row>();
        private float scroll;
        private int hover = -1;
        private bool hoverUndo;
        public event Action<object, Row> Undo;
        public int Count { get { return rows.Count; } }
        internal Row this[int index] { get { return rows[index]; } }

        public PendingList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }

        public void SetRows(List<Row> list)
        {
            rows = list;
            scroll = Math.Max(0, Math.Min(Math.Max(0, rows.Count * RowHeight - Height), scroll));
            hover = -1; hoverUndo = false;
            Invalidate();
        }

        private int RowHeight { get { return Ui.S(58); } }

        private Rectangle UndoRect(int index)
        {
            int w = Ui.Measure("撤销", Ui.Small) + Ui.S(28), h = Ui.S(28);
            return new Rectangle(Width - w - Ui.S(14), (int)(index * RowHeight - scroll) + (RowHeight - h) / 2, w, h);
        }

        private int At(Point p) { int i = (int)((p.Y + scroll) / RowHeight); return i >= 0 && i < rows.Count ? i : -1; }

        internal void RaiseUndo(int index) { if (Undo != null && index >= 0 && index < rows.Count) Undo(this, rows[index]); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = At(e.Location);
            bool over = i >= 0 && UndoRect(i).Contains(e.Location);
            if (i != hover || over != hoverUndo) { hover = i; hoverUndo = over; Cursor = over ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; hoverUndo = false; Cursor = Cursors.Default; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = At(e.Location);
            if (Enabled && e.Button == MouseButtons.Left && i >= 0 && UndoRect(i).Contains(e.Location)) RaiseUndo(i);
            base.OnMouseDown(e);
        }

        protected override void OnMouseEnter(EventArgs e) { Focus(); base.OnMouseEnter(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll = Math.Max(0, Math.Min(Math.Max(0, rows.Count * RowHeight - Height), scroll - e.Delta * Ui.S(60) / 120f));
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int h = RowHeight;
            for (int i = 0; i < rows.Count; i++)
            {
                int y = (int)(i * h - scroll);
                if (y + h < 0 || y > Height) continue;
                Row row = rows[i];
                Ui.Fill(g, new RectangleF(0, y + Ui.S(3), Width - 1, h - Ui.S(6)), Ui.S(12), Ui.Card);
                using (SolidBrush dot = new SolidBrush(Ui.Warn)) g.FillEllipse(dot, Ui.S(16), y + h / 2f - Ui.S(3), Ui.S(6), Ui.S(6));
                Rectangle undo = UndoRect(i);
                int x = Ui.S(34), w = undo.X - x - Ui.S(12);
                Ui.Write(g, row.Title, Ui.Strong, new Rectangle(x, y + Ui.S(9), w, Ui.S(22)), Ui.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                Ui.Write(g, row.Detail, Ui.Small, new Rectangle(x, y + Ui.S(31), w, Ui.S(18)), Ui.TextDim, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                Ui.Fill(g, undo, undo.Height / 2f, Enabled && i == hover && hoverUndo ? Ui.Bg3 : Ui.Bg2);
                Ui.Write(g, "撤销", Ui.Small, undo, Enabled ? Ui.Text : Ui.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
