// Game library grid: square cover cards with the name, platform and three artwork pips
// (cover / logo / background). Same motion language as the capture gallery: cards pop in
// on a spring, hover brings a card forward, selection draws a dark outline.
// Grouped by platform, each group under a header that folds it away; the header of the group
// in view stays pinned to the top while scrolling and offers folding all groups at once.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ThorCapture
{
    internal sealed class GameGridView : Control
    {
        private readonly List<GameEntry> games = new List<GameEntry>();
        private sealed class Header { public RectangleF Bounds; public string Platform; public int Count, Covered; }
        private readonly List<Header> headers = new List<Header>();
        private readonly HashSet<string> collapsed = new HashSet<string>();
        private readonly Dictionary<string, Anim> foldA = new Dictionary<string, Anim>();
        private int hoverHeader = -1;
        private bool hoverAll;
        private Rectangle allRect = Rectangle.Empty;
        private readonly Dictionary<int, Anim> hoverA = new Dictionary<int, Anim>(), selectA = new Dictionary<int, Anim>();
        private readonly Dictionary<int, double> appearAt = new Dictionary<int, double>();
        private readonly Timer scrollTimer = new Timer();
        private readonly Anim appearClock;
        private float scroll, target, cardSize;
        private int contentHeight, hover = -1, selected = -1;
        private bool barDrag, barHover;
        private float barGrab;
        public GameArtCache Art;
        public bool Grouped = true;
        public string EmptyText;
        public event EventHandler SelectionChanged;
        private const int AppearMs = 620, StaggerMs = 28;

        public GameGridView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Ui.Bg0;
            Font = Ui.Body;
            appearClock = new Anim(this, 0, 0, Ease.Linear);
            foreach (string platform in Settings.Get("library.collapsed", "").Split(',')) if (platform.Length > 0) collapsed.Add(platform);
            scrollTimer.Interval = 15;
            scrollTimer.Tick += delegate
            {
                float diff = target - scroll;
                if (Math.Abs(diff) < 0.8f) { scroll = target; scrollTimer.Stop(); }
                else scroll += diff * 0.3f;
                Invalidate();
            };
        }

        public List<GameEntry> Games { get { return games; } }
        public GameEntry Selected { get { return selected >= 0 && selected < games.Count ? games[selected] : null; } }

        protected override void Dispose(bool disposing)
        {
            if (disposing) scrollTimer.Dispose();
            base.Dispose(disposing);
        }

        private Anim Fx(Dictionary<int, Anim> map, int id, int duration, Ease ease)
        {
            Anim anim;
            if (!map.TryGetValue(id, out anim)) { anim = new Anim(this, 0, duration, ease); map[id] = anim; }
            return anim;
        }

        // Replaces the list. Games already shown keep their state; new ones pop in (cascading
        // when the whole list is new). The previous selection is kept if still present.
        public void SetGames(List<GameEntry> list, int keepId)
        {
            HashSet<int> known = new HashSet<int>();
            foreach (GameEntry game in games) known.Add(game.Id);
            int fresh = 0;
            foreach (GameEntry game in list) if (!known.Contains(game.Id)) fresh++;
            bool cascade = fresh == list.Count;
            double now = Motion.Now;
            int order = 0;
            appearAt.Clear();
            if (Motion.Enabled && Visible)
                foreach (GameEntry game in list)
                    if (!known.Contains(game.Id)) appearAt[game.Id] = now + (cascade ? Math.Min(order++, 18) * StaggerMs : 0);
            games.Clear();
            games.AddRange(list);
            hover = -1;
            selected = -1;
            for (int i = 0; i < games.Count; i++) if (games[i].Id == keepId) selected = i;
            foreach (Anim anim in selectA.Values) anim.Snap(0);
            if (selected >= 0) Fx(selectA, games[selected].Id, 480, Ease.Spring).Snap(1);
            scroll = target = 0;
            Relayout();
            if (fresh > 0) { appearClock.Snap(0); appearClock.To(1, AppearMs + 19 * StaggerMs + 40); }
            if (selected >= 0) EnsureVisible(selected);
            Invalidate();
        }

        public void Select(int index)
        {
            if (index < -1 || index >= games.Count || index == selected) return;
            if (selected >= 0) Fx(selectA, games[selected].Id, 480, Ease.Spring).To(0, 200);
            selected = index;
            if (selected >= 0) { Fx(selectA, games[selected].Id, 480, Ease.Spring).To(1, 480); EnsureVisible(selected); }
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        private int Pad { get { return Ui.S(20); } }
        private int Gap { get { return Ui.S(14); } }
        private int TextBand { get { return Ui.S(54); } }
        private int HeaderHeight { get { return Ui.S(44); } }
        private bool Shown(int index) { return index >= 0 && index < games.Count && !games[index].Bounds.IsEmpty; }

        private Anim Fold(string platform)
        {
            Anim anim;
            if (!foldA.TryGetValue(platform, out anim)) { anim = new Anim(this, collapsed.Contains(platform) ? 0 : 1, 260, Ease.OutCubic); foldA[platform] = anim; }
            return anim;
        }

        // Folds or unfolds one group, keeping its header where it was on screen; unfolded cards pop back in.
        internal void Toggle(int index)
        {
            Header header = headers[index];
            float before = header.Bounds.Y - scroll;
            bool fold = !collapsed.Contains(header.Platform);
            if (fold) collapsed.Add(header.Platform); else collapsed.Remove(header.Platform);
            Fold(header.Platform).Target = fold ? 0 : 1;
            if (!fold) Reveal(header.Platform);
            Remember();
            Relayout();
            foreach (Header h in headers) if (h.Platform == header.Platform) scroll = target = Clamp(h.Bounds.Y - Math.Max(0, before));
            Invalidate();
        }

        internal void ToggleAll()
        {
            bool fold = headers.Exists(h => !collapsed.Contains(h.Platform));
            foreach (Header header in headers)
            {
                if (fold) collapsed.Add(header.Platform); else { collapsed.Remove(header.Platform); Reveal(header.Platform); }
                Fold(header.Platform).Target = fold ? 0 : 1;
            }
            Remember();
            Relayout();
            scroll = target = 0;
            Invalidate();
        }

        internal int HeaderCount { get { return headers.Count; } }
        internal bool IsFolded(string platform) { return collapsed.Contains(platform); }

        private void Reveal(string platform)
        {
            if (!Motion.Enabled) return;
            double now = Motion.Now;
            int order = 0;
            foreach (GameEntry game in games) if (game.Platform == platform) appearAt[game.Id] = now + Math.Min(order++, 18) * StaggerMs;
            appearClock.Snap(0); appearClock.To(1, AppearMs + 19 * StaggerMs + 40);
        }

        private void Remember() { Settings.Set("library.collapsed", string.Join(",", new List<string>(collapsed).ToArray())); }

        // The header pinned to the top: the last group that starts above the view, pushed up by the next one.
        private int StickyIndex(out float offset)
        {
            offset = 0;
            int found = -1;
            for (int i = 0; i < headers.Count; i++) if (headers[i].Bounds.Y < scroll) found = i;
            if (found >= 0 && found + 1 < headers.Count)
            {
                float next = headers[found + 1].Bounds.Y - scroll;
                if (next < HeaderHeight) offset = next - HeaderHeight;
            }
            return found;
        }

        // Header under the pointer (screen coordinates), the pinned one first.
        private int HeaderAt(Point p)
        {
            float offset;
            int sticky = StickyIndex(out offset);
            if (sticky >= 0 && p.Y >= 0 && p.Y < HeaderHeight + offset) return sticky;
            float y = p.Y + scroll;
            for (int i = 0; i < headers.Count; i++) if (headers[i].Bounds.Contains(p.X, y)) return i;
            return -1;
        }

        private void Relayout()
        {
            headers.Clear();
            int pad = Pad, gap = Gap;
            int width = Math.Max(Ui.S(160), ClientSize.Width - pad * 2 - Ui.S(6));
            int columns = Math.Max(1, (width + gap) / (Ui.S(148) + gap));
            cardSize = (width - gap * (columns - 1)) / (float)columns;
            float cardHeight = cardSize + TextBand;
            float y = Ui.S(8);
            int column = 0;
            string group = null;
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (GameEntry game in games) { int c; counts.TryGetValue(game.Platform, out c); counts[game.Platform] = c + 1; }
            Header current = null;
            foreach (GameEntry game in games)
            {
                if (Grouped && game.Platform != group)
                {
                    if (column != 0) { y += cardHeight + gap; column = 0; }
                    group = game.Platform;
                    current = new Header { Bounds = new RectangleF(pad, y, width, HeaderHeight), Platform = group };
                    headers.Add(current);
                    y += HeaderHeight + (collapsed.Contains(group) ? Ui.S(4) : 0);
                }
                if (current != null) { current.Count++; if (game.HasCover) current.Covered++; }
                // A folded group's cards take no room and cannot be hit.
                if (Grouped && collapsed.Contains(game.Platform)) { game.Bounds = RectangleF.Empty; continue; }
                game.Bounds = new RectangleF(pad + column * (cardSize + gap), y, cardSize, cardHeight);
                if (++column == columns) { column = 0; y += cardHeight + gap; }
            }
            if (column != 0) y += cardHeight + gap;
            contentHeight = (int)(y + pad);
            target = Clamp(target);
            scroll = Clamp(scroll);
        }

        private float MaxScroll { get { return Math.Max(0, contentHeight - ClientSize.Height); } }
        private float Clamp(float value) { return Math.Max(0, Math.Min(MaxScroll, value)); }

        public void EnsureVisible(int index)
        {
            if (!Shown(index)) return;
            RectangleF b = games[index].Bounds;
            if (b.Y - HeaderHeight - Ui.S(8) < target) target = Clamp(b.Y - HeaderHeight - Ui.S(8));
            else if (b.Bottom + Gap > target + ClientSize.Height) target = Clamp(b.Bottom + Gap - ClientSize.Height);
            if (Math.Abs(target - scroll) > 0.5f) scrollTimer.Start();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); Invalidate(); }

        private int HitTest(Point p)
        {
            float y = p.Y + scroll;
            if (HeaderAt(p) >= 0) return -1;
            for (int i = 0; i < games.Count; i++) if (Shown(i) && games[i].Bounds.Contains(p.X, y)) return i;
            return -1;
        }

        private Rectangle CardArea(int index)
        {
            RectangleF b = games[index].Bounds;
            Rectangle r = new Rectangle((int)b.X, (int)(b.Y - scroll), (int)b.Width, (int)b.Height);
            r.Inflate(Ui.S(12), Ui.S(14));
            return r;
        }

        private Rectangle BarThumb()
        {
            if (MaxScroll <= 0) return Rectangle.Empty;
            int h = ClientSize.Height, thumb = Math.Max(Ui.S(36), (int)((long)h * h / contentHeight));
            int y = (int)((h - thumb) * (scroll / MaxScroll)), w = barHover || barDrag ? Ui.S(8) : Ui.S(5);
            return new Rectangle(ClientSize.Width - w - Ui.S(3), y, w, thumb);
        }

        private bool InBar(Point p) { return MaxScroll > 0 && p.X >= ClientSize.Width - Ui.S(14); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (barDrag)
            {
                Rectangle thumb = BarThumb();
                float range = ClientSize.Height - thumb.Height;
                if (range > 0) scroll = target = Clamp((e.Y - barGrab) / range * MaxScroll);
                Invalidate();
                return;
            }
            bool near = InBar(e.Location);
            int index = near ? -1 : HitTest(e.Location);
            int headerOver = near ? -1 : HeaderAt(e.Location);
            bool allOver = !near && allRect.Contains(e.Location);
            if (headerOver != hoverHeader || allOver != hoverAll) { hoverHeader = headerOver; hoverAll = allOver; Invalidate(); }
            if (index != hover || near != barHover)
            {
                if (hover >= 0 && hover < games.Count) { Fx(hoverA, games[hover].Id, 180, Ease.OutCubic).Target = 0; Invalidate(CardArea(hover)); }
                if (index >= 0) { Fx(hoverA, games[index].Id, 180, Ease.OutCubic).Target = 1; Invalidate(CardArea(index)); }
                if (near != barHover) Invalidate(new Rectangle(ClientSize.Width - Ui.S(16), 0, Ui.S(16), ClientSize.Height));
                hover = index; barHover = near;
            }
            Cursor = index >= 0 || headerOver >= 0 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hover >= 0 && hover < games.Count) Fx(hoverA, games[hover].Id, 180, Ease.OutCubic).Target = 0;
            hover = -1; barHover = false; hoverHeader = -1; hoverAll = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left && InBar(e.Location))
            {
                Rectangle thumb = BarThumb();
                barGrab = thumb.Contains(e.Location) ? e.Y - thumb.Y : thumb.Height / 2f;
                barDrag = true; Capture = true;
                OnMouseMove(e);
                return;
            }
            if (e.Button == MouseButtons.Left && allRect.Contains(e.Location)) { ToggleAll(); return; }
            int header = HeaderAt(e.Location);
            if (e.Button == MouseButtons.Left && header >= 0) { Toggle(header); return; }
            int index = HitTest(e.Location);
            if (e.Button == MouseButtons.Left && index >= 0) Select(index);
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { if (barDrag) { barDrag = false; Capture = false; Invalidate(); } base.OnMouseUp(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            target = Clamp(target - e.Delta * Ui.S(110) / 120f);
            scrollTimer.Start();
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode) { case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: case Keys.Home: case Keys.End: return true; }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (games.Count == 0) { base.OnKeyDown(e); return; }
            int from = Shown(selected) ? selected : games.FindIndex(game => !game.Bounds.IsEmpty), next = -1;
            if (from < 0) { base.OnKeyDown(e); return; } // every group is folded
            switch (e.KeyCode)
            {
                case Keys.Left: next = from; for (int i = from - 1; i >= 0; i--) if (Shown(i)) { next = i; break; } break;
                case Keys.Right: next = from; for (int i = from + 1; i < games.Count; i++) if (Shown(i)) { next = i; break; } break;
                case Keys.Home: next = games.FindIndex(game => !game.Bounds.IsEmpty); break;
                case Keys.End: next = games.FindLastIndex(game => !game.Bounds.IsEmpty); break;
                case Keys.Up: case Keys.Down:
                    {
                        RectangleF b = games[from].Bounds;
                        float best = float.MaxValue;
                        for (int i = 0; i < games.Count; i++)
                        {
                            if (!Shown(i)) continue;
                            float dy = games[i].Bounds.Y - b.Y;
                            if (e.KeyCode == Keys.Up ? dy >= -1 : dy <= 1) continue;
                            float cost = Math.Abs(dy) * 4 + Math.Abs(games[i].Bounds.X - b.X);
                            if (cost < best) { best = cost; next = i; }
                        }
                        if (next < 0) next = from;
                        break;
                    }
            }
            if (next >= 0) { Select(next); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (games.Count == 0 && EmptyText != null)
            {
                Ui.Write(g, EmptyText, Ui.Strong, new Rectangle(0, ClientSize.Height / 2 - Ui.S(40), ClientSize.Width, Ui.S(24)), Ui.TextDim, TextFormatFlags.HorizontalCenter);
                return;
            }
            float top = scroll, bottom = scroll + ClientSize.Height;
            allRect = Rectangle.Empty;
            for (int i = 0; i < headers.Count; i++)
            {
                RectangleF h = headers[i].Bounds;
                if (h.Bottom < top || h.Y > bottom) continue;
                DrawHeader(g, i, new RectangleF(h.X, h.Y - scroll, h.Width, h.Height), i == 0 && scroll <= 0);
            }
            for (int i = 0; i < games.Count; i++)
            {
                if (!Shown(i)) continue;
                RectangleF b = games[i].Bounds;
                if (b.Bottom < top - Ui.S(20) || b.Y > bottom + Ui.S(20)) continue;
                DrawCard(g, games[i], new RectangleF(b.X, b.Y - scroll, b.Width, b.Height), i == selected && Focused);
            }
            // The group in view keeps its header at the top, over the cards scrolling beneath it.
            float push;
            int sticky = StickyIndex(out push);
            if (sticky >= 0)
            {
                allRect = Rectangle.Empty;
                RectangleF pinned = new RectangleF(headers[sticky].Bounds.X, push, headers[sticky].Bounds.Width, HeaderHeight);
                using (SolidBrush cover = new SolidBrush(BackColor)) g.FillRectangle(cover, 0, push, ClientSize.Width - Ui.S(14), HeaderHeight + Ui.S(2));
                using (LinearGradientBrush fade = new LinearGradientBrush(new RectangleF(0, push + HeaderHeight + Ui.S(2) - 1, 10, Ui.S(8) + 2), Ui.Alpha(Ui.Ink, 20), Ui.Alpha(Ui.Ink, 0), 90f))
                    g.FillRectangle(fade, 0, push + HeaderHeight + Ui.S(2), ClientSize.Width - Ui.S(14), Ui.S(8));
                DrawHeader(g, sticky, pinned, true);
            }
            Rectangle bar = BarThumb();
            if (!bar.IsEmpty) Ui.Fill(g, bar, bar.Width / 2f, Ui.Wash(barDrag ? 0.4f : barHover ? 0.28f : 0.14f));
        }

        // Chevron, platform, count, and how many still lack a cover; a soft wash on hover. A folded group reads as a closed bar.
        private void DrawHeader(Graphics g, int index, RectangleF r, bool top)
        {
            Header header = headers[index];
            float open = Fold(header.Platform).Value;
            bool folded = collapsed.Contains(header.Platform);
            RectangleF band = new RectangleF(r.X - Ui.S(8), r.Y + Ui.S(4), r.Width + Ui.S(10), r.Height - Ui.S(8));
            if (folded) Ui.Fill(g, band, Ui.S(12), Ui.Card);
            if (index == hoverHeader) Ui.Fill(g, band, Ui.S(12), Ui.Wash(0.05f));
            // Chevron: points down when open, right when folded, turning in between.
            float cx = r.X + Ui.S(8), cy = r.Y + r.Height / 2f, arm = Ui.S(4.5f);
            double angle = (1 - open) * -Math.PI / 2;
            Func<float, float, PointF> turn = delegate(float dx, float dy) { return new PointF(cx + (float)(dx * Math.Cos(angle) - dy * Math.Sin(angle)), cy + (float)(dx * Math.Sin(angle) + dy * Math.Cos(angle))); };
            using (Pen pen = new Pen(index == hoverHeader ? Ui.Text : Ui.TextDim, Ui.S(1.8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { turn(-arm, -arm / 2), turn(0, arm / 2), turn(arm, -arm / 2) });
            int x = (int)r.X + Ui.S(26);
            string name = GameEntry.PlatformName(header.Platform);
            int w = Ui.Measure(name, Ui.Strong);
            Ui.Write(g, name, Ui.Strong, new Rectangle(x, (int)r.Y, (int)r.Width, (int)r.Height), Ui.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            string count = header.Count + " 款" + (header.Covered < header.Count ? " · " + (header.Count - header.Covered) + " 款缺封面" : "");
            Ui.Write(g, count, Ui.Small, new Rectangle(x + w + Ui.S(10), (int)r.Y, Ui.S(260), (int)r.Height), Ui.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            if (top && headers.Count > 1)
            {
                // Fold or unfold every group, at the right end of the header on top.
                string text = headers.Exists(h => !collapsed.Contains(h.Platform)) ? "全部收起" : "全部展开";
                int bw = Ui.Measure(text, Ui.Small) + Ui.S(22), bh = Ui.S(26);
                allRect = new Rectangle((int)r.Right - bw - Ui.S(4), (int)(r.Y + (r.Height - bh) / 2), bw, bh);
                Ui.Fill(g, allRect, bh / 2f, hoverAll ? Ui.Bg3 : Ui.Bg2);
                Ui.Write(g, text, Ui.Small, allRect, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private void DrawCard(Graphics g, GameEntry game, RectangleF r, bool focused)
        {
            float radius = Ui.S(14);
            float h = Fx(hoverA, game.Id, 180, Ease.OutCubic).Value;
            float s = Fx(selectA, game.Id, 480, Ease.Spring).Value, sel = Math.Max(0, Math.Min(1, s));
            double at;
            float appear = 1, t = 1;
            if (appearAt.TryGetValue(game.Id, out at))
            {
                t = (float)((Motion.Now - at) / AppearMs);
                appear = Motion.Apply(Ease.Spring, t);
                if (t >= 1) appearAt.Remove(game.Id);
            }
            if (t <= 0) return;
            GraphicsState state = g.Save();
            if (appear != 1)
            {
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, scale = 0.6f + 0.4f * appear;
                g.TranslateTransform(cx, cy);
                g.ScaleTransform(scale + (appear - 1) * 0.3f, scale - (appear - 1) * 0.3f);
                g.TranslateTransform(-cx, -cy);
            }
            r.Y -= h * Ui.S(3);
            if (h > 0.01f) Ui.Shadow(g, r, radius, Ui.S(10), (int)(26 * h), Ui.S(4), Ui.Ink);
            Ui.Fill(g, r, radius, Ui.Mix(Ui.Card, Ui.Surface, Math.Max(h, sel)));

            // Cover, fitted inside a soft square.
            int p = Ui.S(7);
            RectangleF cover = new RectangleF(r.X + p, r.Y + p, r.Width - p * 2, r.Width - p * 2);
            using (GraphicsPath clip = Ui.Round(cover, Ui.S(10)))
            {
                Bitmap art = game.HasCover && Art != null ? Art.Get(game.Id, GameArtCache.Kind.Cover, (int)cover.Width) : null;
                GraphicsState inner = g.Save();
                g.SetClip(clip, CombineMode.Intersect);
                if (art != null)
                {
                    // Cover-crop to the square so box art of any shape fills the tile.
                    float scale = Math.Max(cover.Width / art.Width, cover.Height / art.Height);
                    float w = art.Width * scale, hh = art.Height * scale;
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(art, cover.X + (cover.Width - w) / 2, cover.Y + (cover.Height - hh) / 2, w, hh);
                }
                else Placeholder(g, cover, game);
                g.Restore(inner);
            }
            if (game.Favorite && Ui.IconSmall != null)
            {
                RectangleF badge = new RectangleF(cover.Right - Ui.S(28), cover.Y + Ui.S(6), Ui.S(22), Ui.S(22));
                Ui.Fill(g, badge, badge.Width / 2, Ui.Alpha(Color.White, 225));
                Ui.Glyph(g, "", Ui.IconSmall, Rectangle.Round(badge), Ui.Danger); // filled heart
            }

            // Name, platform, artwork pips.
            int tx = (int)r.X + Ui.S(12), ty = (int)cover.Bottom + Ui.S(6), tw = (int)r.Width - Ui.S(24);
            Ui.Write(g, game.Name, Ui.Strong, new Rectangle(tx, ty, tw, Ui.S(20)), Ui.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            Ui.Write(g, GameEntry.PlatformName(game.Platform), Ui.Small, new Rectangle(tx, ty + Ui.S(21), tw - Ui.S(40), Ui.S(18)), Ui.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            bool[] slots = { game.HasCover, game.HasLogo, game.HasHero };
            float pip = Ui.S(6), px = r.Right - Ui.S(12) - pip * 3 - Ui.S(4) * 2, py = ty + Ui.S(21) + (Ui.S(18) - pip) / 2f;
            for (int i = 0; i < 3; i++)
            {
                RectangleF dot = new RectangleF(px + i * (pip + Ui.S(4)), py, pip, pip);
                if (slots[i]) using (SolidBrush fill = new SolidBrush(Ui.TextDim)) g.FillEllipse(fill, dot);
                else using (Pen ring = new Pen(Ui.TextFaint, 1.2f)) g.DrawEllipse(ring, dot.X + 0.6f, dot.Y + 0.6f, dot.Width - 1.2f, dot.Height - 1.2f);
            }

            if (sel > 0.01f)
            {
                RectangleF edge = r;
                float grow = (1 - s) * Ui.S(4);
                edge.Inflate(Ui.S(1) + grow, Ui.S(1) + grow);
                Ui.Stroke(g, edge, radius + Ui.S(1) + grow, Ui.Alpha(Ui.Accent, (int)(255 * sel)), Math.Max(1.5f, Ui.S(2)));
            }
            else if (focused)
            {
                RectangleF edge = r; edge.Inflate(-0.5f, -0.5f);
                Ui.Stroke(g, edge, radius, Ui.Wash(0.2f), 1f);
            }
            g.Restore(state);
        }

        // No cover yet: a quiet tile with the platform and the first character of the name.
        internal static void Placeholder(Graphics g, RectangleF area, GameEntry game)
        {
            using (LinearGradientBrush fill = new LinearGradientBrush(new RectangleF(area.X, area.Y - 1, area.Width, area.Height + 2), Ui.Bg2, Ui.Bg3, 90f))
                g.FillRectangle(fill, area);
            string initial = string.IsNullOrEmpty(game.Name) ? "?" : game.Name.Substring(0, 1).ToUpperInvariant();
            using (Font big = new Font("Microsoft YaHei UI", Math.Max(8f, area.Height * 0.28f), FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush ink = new SolidBrush(Ui.Alpha(Ui.TextFaint, 150)))
            using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(initial, big, ink, area, center);
            using (Font small = new Font("Segoe UI Semibold", Math.Max(7f, area.Height * 0.075f), GraphicsUnit.Pixel))
            using (SolidBrush ink = new SolidBrush(Ui.TextFaint))
            using (StringFormat bottom = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far })
                g.DrawString(GameEntry.PlatformName(game.Platform), small, ink, new RectangleF(area.X, area.Y, area.Width, area.Height - area.Height * 0.08f), bottom);
        }
    }
}
