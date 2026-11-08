// Game detail: a background banner with the logo laid over it, the cover and title, the
// known facts, the summary and the three artwork slots. Facts are edited through the local
// draft (GameEditSession); artwork editing stays in the Cocoon page until rebuilt natively.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace ThorCapture
{
    internal sealed class GameDetailView : Control
    {
        private GameEntry game;
        private readonly Anim showA;
        private float scroll;
        private int contentHeight;
        private readonly MascotClock mascot;
        private Rectangle editRect, scrapeRect;
        private readonly Rectangle[] slotRects = new Rectangle[3];
        private int hoverSlot = -1;
        // Clicking a slot opens the artwork picker for it.
        public event Action<ArtworkSlot> ArtworkRequested;
        // Pixels of a staged replacement, when the page has them; null shows the badge only.
        public Func<int, ArtworkSlot, Bitmap> StagedArt;
        private bool editHover, scrapeHover;
        public event EventHandler ScrapeRequested;
        public GameArtCache Art;
        // Local draft of this library; null keeps the view read-only.
        public GameEditSession Edits;
        public event EventHandler EditRequested;

        public GameDetailView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg1;
            Font = Ui.Body;
            showA = new Anim(this, 1, 360, Ease.OutCubic);
            mascot = new MascotClock(this);
        }

        public GameEntry Game
        {
            get { return game; }
            set
            {
                if (ReferenceEquals(game, value)) return;
                game = value;
                scroll = 0;
                // New selection slides up and fades in.
                if (value != null) showA.Play(0, 1);
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll = Math.Max(0, Math.Min(Math.Max(0, contentHeight - ClientSize.Height), scroll - e.Delta * Ui.S(80) / 120f));
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnMouseEnter(EventArgs e) { Focus(); base.OnMouseEnter(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool over = game != null && editRect.Contains(e.Location), overScrape = game != null && scrapeRect.Contains(e.Location);
            int slotOver = -1;
            if (game != null && Edits != null) for (int i = 0; i < 3; i++) if (slotRects[i].Contains(e.Location)) slotOver = i;
            if (slotOver != hoverSlot) { hoverSlot = slotOver; Cursor = slotOver >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            if (over != editHover || overScrape != scrapeHover)
            {
                editHover = over; scrapeHover = overScrape;
                Cursor = over || overScrape || hoverSlot >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate(editRect); Invalidate(scrapeRect);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hoverSlot >= 0) { hoverSlot = -1; Invalidate(); }
            if (editHover || scrapeHover) { editHover = scrapeHover = false; Cursor = Cursors.Default; Invalidate(editRect); Invalidate(scrapeRect); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && game != null && scrapeRect.Contains(e.Location) && ScrapeRequested != null) ScrapeRequested(this, EventArgs.Empty);
            if (e.Button == MouseButtons.Left && game != null && Edits != null && ArtworkRequested != null)
                for (int i = 0; i < 3; i++) if (slotRects[i].Contains(e.Location)) ArtworkRequested((ArtworkSlot)i);
            if (e.Button == MouseButtons.Left && game != null && editRect.Contains(e.Location) && EditRequested != null) EditRequested(this, EventArgs.Empty);
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            mascot.Run(game == null && Visible);
            if (game == null)
            {
                editRect = scrapeRect = Rectangle.Empty;
                int d = Ui.S(92), mid = ClientSize.Height / 2 - Ui.S(20);
                mascot.Area = new Rectangle(ClientSize.Width / 2 - d / 2 - Ui.S(20), mid - d - Ui.S(20), d + Ui.S(40), d + Ui.S(26));
                Mascot.Draw(g, new RectangleF(ClientSize.Width / 2 - d / 2, mid - d, d, d), MascotMood.Idle, mascot.Time);
                Ui.Write(g, "选择一个游戏", Ui.Strong, new Rectangle(0, mid + Ui.S(14), ClientSize.Width, Ui.S(24)), Ui.TextDim, TextFormatFlags.HorizontalCenter);
                return;
            }
            // TextRenderer ignores transforms, so the slide-in and scroll offsets go into y.
            float v = showA.Value;
            int offset = (int)Math.Round((1 - v) * Ui.S(14) - scroll);
            int bottom = Compose(g, offset);
            contentHeight = bottom - offset + Ui.S(24);
            if (v < 1) using (SolidBrush veil = new SolidBrush(Color.FromArgb((int)(255 * (1 - v)), BackColor))) g.FillRectangle(veil, ClientRectangle);
        }

        private int Compose(Graphics g, int offset)
        {
            int pad = Ui.S(24), width = ClientSize.Width - pad * 2, y = Ui.S(20) + offset;

            // Banner: background art with the logo; otherwise a quiet tile.
            RectangleF banner = new RectangleF(pad, y, width, Math.Min(Ui.S(240), width * 9 / 16f));
            Bitmap hero = game.HasHero && Art != null ? Art.Get(game.Id, GameArtCache.Kind.Hero, (int)banner.Width) : null;
            using (GraphicsPath clip = Ui.Round(banner, Ui.S(16)))
            {
                GraphicsState inner = g.Save();
                g.SetClip(clip, CombineMode.Intersect);
                if (hero != null) Cover(g, hero, banner);
                else
                    using (LinearGradientBrush fill = new LinearGradientBrush(banner, Ui.Bg2, Ui.Bg3, 35f)) g.FillRectangle(fill, banner);
                // A shade from the left so the logo reads on any art.
                if (hero != null)
                    using (LinearGradientBrush shade = new LinearGradientBrush(new RectangleF(banner.X - 1, banner.Y, banner.Width * 0.65f + 2, banner.Height),
                        Color.FromArgb(120, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 0f))
                        g.FillRectangle(shade, banner.X, banner.Y, banner.Width * 0.65f, banner.Height);
                // The logo is made for the background art; without it the platform name stands in.
                Bitmap logo = hero != null && game.HasLogo && Art != null ? Art.Get(game.Id, GameArtCache.Kind.Logo, (int)(banner.Width * 0.55f)) : null;
                if (logo != null)
                {
                    float scale = Math.Min(banner.Width * 0.55f / logo.Width, banner.Height * 0.32f / logo.Height);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    // Left side, centred above the cover tile that overlaps the banner edge.
                    float free = banner.Height - Ui.S(44);
                    g.DrawImage(logo, banner.X + Ui.S(22), banner.Y + (free - logo.Height * scale) / 2, logo.Width * scale, logo.Height * scale);
                }
                else if (hero == null && !(game.HasHero && Art != null))
                    using (Font big = new Font("Segoe UI Semibold", Math.Max(10f, banner.Height * 0.2f), GraphicsUnit.Pixel))
                    using (SolidBrush ink = new SolidBrush(Ui.Alpha(Ui.TextFaint, 90)))
                    using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(GameEntry.PlatformName(game.Platform), big, ink, banner, center);
                g.Restore(inner);
            }
            y = (int)banner.Bottom;

            // Cover overlapping the banner edge, then the title block.
            int cover = Ui.S(88);
            RectangleF tile = new RectangleF(pad + Ui.S(16), y - cover / 2, cover, cover);
            Ui.Shadow(g, tile, Ui.S(14), Ui.S(8), 40, Ui.S(3), Ui.Ink);
            Ui.Fill(g, tile, Ui.S(14), Ui.Surface);
            RectangleF inset = tile; inset.Inflate(-Ui.S(4), -Ui.S(4));
            using (GraphicsPath clip = Ui.Round(inset, Ui.S(11)))
            {
                GraphicsState inner = g.Save();
                g.SetClip(clip, CombineMode.Intersect);
                Bitmap art = game.HasCover && Art != null ? Art.Get(game.Id, GameArtCache.Kind.Cover, (int)inset.Width) : null;
                if (art != null) Cover(g, art, inset); else GameGridView.Placeholder(g, inset, game);
                g.Restore(inner);
            }
            int tx = (int)tile.Right + Ui.S(16);
            Ui.Write(g, game.Name, Ui.Heading, new Rectangle(tx, y + Ui.S(10), pad + width - tx, Ui.S(28)), Ui.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (Edits != null)
            {
                // Edit entry on the chip row, right edge.
                int ew = Ui.Measure("编辑资料", Ui.Small) + Ui.S(24);
                editRect = new Rectangle(pad + width - ew, y + Ui.S(40), ew, Ui.S(26));
                Ui.Fill(g, editRect, editRect.Height / 2f, editHover ? Ui.Bg3 : Ui.Bg2);
                Ui.Write(g, "编辑资料", Ui.Small, editRect, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                // Looking the game up online sits beside editing it by hand.
                int sw = Ui.Measure("刮削", Ui.Small) + Ui.S(24);
                scrapeRect = new Rectangle(editRect.X - sw - Ui.S(6), editRect.Y, sw, editRect.Height);
                Ui.Fill(g, scrapeRect, scrapeRect.Height / 2f, scrapeHover ? Ui.Bg3 : Ui.Bg2);
                Ui.Write(g, "刮削", Ui.Small, scrapeRect, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else editRect = scrapeRect = Rectangle.Empty;
            string chip = GameEntry.PlatformName(game.Platform);
            int cw = Ui.Measure(chip, Ui.Small) + Ui.S(16);
            Rectangle chipRect = new Rectangle(tx, y + Ui.S(42), cw, Ui.S(22));
            Ui.Fill(g, chipRect, chipRect.Height / 2f, Ui.Bg2);
            Ui.Write(g, chip, Ui.Small, chipRect, Ui.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (game.Favorite && Ui.IconSmall != null)
                Ui.Glyph(g, "", Ui.IconSmall, new Rectangle(chipRect.Right + Ui.S(6), chipRect.Y, Ui.S(22), chipRect.Height), Ui.Danger);
            y = (int)tile.Bottom + Ui.S(22);

            // Staged facts are a local draft: say so, and keep showing the library values below.
            StagedMetadata staged = Edits == null ? null : Edits.MetadataFor(game.Id);
            if (staged != null && staged.Fields.Count > 0)
            {
                RectangleF note = new RectangleF(pad, y + Ui.S(6), width, Ui.S(34));
                Ui.Fill(g, note, Ui.S(12), Ui.Card);
                using (SolidBrush dot = new SolidBrush(Ui.Warn)) g.FillEllipse(dot, note.X + Ui.S(14), note.Y + note.Height / 2f - Ui.S(3), Ui.S(6), Ui.S(6));
                Ui.Write(g, "已暂存 " + staged.Fields.Count + " 项资料修改，尚未应用", Ui.Small, new Rectangle((int)note.X + Ui.S(30), (int)note.Y, (int)note.Width - Ui.S(40), (int)note.Height), Ui.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                y += Ui.S(56);
            }

            // Facts: two columns of label / value, skipping what is unknown.
            List<KeyValuePair<string, string>> facts = new List<KeyValuePair<string, string>>();
            Add(facts, "开发", game.Developer);
            Add(facts, "发行", game.Publisher);
            Add(facts, "发售", game.Released.HasValue ? game.Released.Value.ToString("yyyy年M月", CultureInfo.InvariantCulture) : null);
            Add(facts, "类型", game.Genres);
            Add(facts, "人数", game.Players);
            Add(facts, "地区", game.Region);
            int column = width / 2;
            for (int i = 0; i < facts.Count; i++)
            {
                int x = pad + (i % 2) * column, row = y + (i / 2) * Ui.S(44);
                Ui.Write(g, facts[i].Key, Ui.Small, new Rectangle(x, row, column - Ui.S(12), Ui.S(18)), Ui.TextFaint, TextFormatFlags.Left);
                Ui.Write(g, facts[i].Value, Ui.Body, new Rectangle(x, row + Ui.S(18), column - Ui.S(12), Ui.S(22)), Ui.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
            y += (facts.Count + 1) / 2 * Ui.S(44);
            if (game.Rating > 0)
            {
                Ui.Write(g, "评分", Ui.Small, new Rectangle(pad, y, Ui.S(60), Ui.S(18)), Ui.TextFaint, TextFormatFlags.Left);
                // The catalog rates out of 100; older data may already be out of 5.
                Stars(g, pad, y + Ui.S(22), (float)Math.Min(5, game.Rating > 5 ? game.Rating / 20 : game.Rating));
                y += Ui.S(48);
            }
            if (!string.IsNullOrEmpty(game.Summary))
            {
                Rectangle text = new Rectangle(pad, y + Ui.S(4), width, 1000);
                Size need = TextRenderer.MeasureText(g, game.Summary, Ui.Body, new Size(width, 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                text.Height = Math.Min(need.Height, Ui.S(160));
                Ui.Write(g, game.Summary, Ui.Body, text, Ui.TextDim, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                y = text.Bottom + Ui.S(18);
            }

            // Artwork slots.
            Ui.Write(g, "素材", Ui.Strong, new Rectangle(pad, y, width, Ui.S(22)), Ui.Text, TextFormatFlags.Left);
            y += Ui.S(30);
            int gap = Ui.S(10), slot = (width - gap * 2) / 3;
            Slot(g, new RectangleF(pad, y, slot, slot), "封面", game.HasCover, game.CoverSource, GameArtCache.Kind.Cover, ArtworkSlot.Icon, game.CoverUri);
            Slot(g, new RectangleF(pad + slot + gap, y, slot, slot), "Logo", game.HasLogo, game.LogoSource, GameArtCache.Kind.Logo, ArtworkSlot.Logo, game.LogoUri);
            Slot(g, new RectangleF(pad + (slot + gap) * 2, y, slot, slot), "背景", game.HasHero, game.HeroSource, GameArtCache.Kind.Hero, ArtworkSlot.Hero, game.HeroUri);
            y += slot + Ui.S(44);
            return y;
        }

        private static void Add(List<KeyValuePair<string, string>> facts, string label, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) facts.Add(new KeyValuePair<string, string>(label, value.Trim()));
        }

        private static void Cover(Graphics g, Bitmap image, RectangleF area)
        {
            float scale = Math.Max(area.Width / image.Width, area.Height / image.Height);
            float w = image.Width * scale, h = image.Height * scale;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        }

        // Five dots, partially filled.
        private static void Stars(Graphics g, int x, int y, float rating)
        {
            float d = Ui.S(10), step = Ui.S(15);
            for (int i = 0; i < 5; i++)
            {
                RectangleF dot = new RectangleF(x + i * step, y, d, d);
                using (SolidBrush empty = new SolidBrush(Ui.Bg3)) g.FillEllipse(empty, dot);
                float fill = Math.Max(0, Math.Min(1, rating - i));
                if (fill <= 0) continue;
                GraphicsState state = g.Save();
                g.SetClip(new RectangleF(dot.X, dot.Y, dot.Width * fill, dot.Height), CombineMode.Intersect);
                using (SolidBrush full = new SolidBrush(Ui.Text)) g.FillEllipse(full, dot);
                g.Restore(state);
            }
        }

        private void Slot(Graphics g, RectangleF area, string name, bool present, string source, GameArtCache.Kind kind, ArtworkSlot which, string uri)
        {
            float radius = Ui.S(12);
            slotRects[(int)which] = Rectangle.Round(area);
            // A staged replacement is shown in place of the current one, marked as not applied yet.
            StagedAsset staged = Edits == null ? null : Edits.AssetFor(game.Id, which);
            Bitmap art = staged != null && StagedArt != null ? StagedArt(game.Id, which) : present && Art != null ? Art.Get(game.Id, kind, (int)area.Width) : null;
            bool video = staged == null && uri != null && System.Text.RegularExpressions.Regex.IsMatch(uri, @".(mp4|webm|mkv)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (art != null)
            {
                // Logos are usually light artwork for dark backgrounds.
                Ui.Fill(g, area, radius, kind == GameArtCache.Kind.Logo ? Color.FromArgb(46, 46, 44) : Ui.Bg2);
                using (GraphicsPath clip = Ui.Round(area, radius))
                {
                    GraphicsState state = g.Save();
                    g.SetClip(clip, CombineMode.Intersect);
                    RectangleF inner = area; inner.Inflate(-Ui.S(8), -Ui.S(8));
                    float scale = Math.Min(inner.Width / art.Width, inner.Height / art.Height);
                    float w = art.Width * scale, h = art.Height * scale;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(art, inner.X + (inner.Width - w) / 2, inner.Y + (inner.Height - h) / 2, w, h);
                    g.Restore(state);
                }
            }
            else
            {
                // Missing: a dashed outline, nothing louder.
                Ui.Fill(g, area, radius, Ui.Card);
                using (GraphicsPath outline = Ui.Round(new RectangleF(area.X + 1, area.Y + 1, area.Width - 2, area.Height - 2), radius))
                using (Pen dash = new Pen(Ui.Bg3, 1.4f) { DashStyle = DashStyle.Dash })
                    g.DrawPath(dash, outline);
                Ui.Write(g, staged != null ? "新视频" : video ? "视频背景" : present ? "载入中" : "缺失", Ui.Small, Rectangle.Round(area), Ui.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            if (staged != null)
            {
                Rectangle tag = new Rectangle((int)area.X + Ui.S(6), (int)area.Y + Ui.S(6), Ui.Measure("已暂存", Ui.Small) + Ui.S(16), Ui.S(20));
                Ui.Fill(g, tag, tag.Height / 2f, Ui.Warn);
                Ui.Write(g, "已暂存", Ui.Small, tag, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            if (Edits != null && hoverSlot == (int)which)
                using (GraphicsPath outline = Ui.Round(area, radius)) using (Pen pen = new Pen(Ui.Text, 1.6f)) g.DrawPath(pen, outline);
            Ui.Write(g, name, Ui.Small, new Rectangle((int)area.X, (int)area.Bottom + Ui.S(6), (int)area.Width, Ui.S(18)), Ui.TextDim, TextFormatFlags.Left);
            if (present && !string.IsNullOrEmpty(source))
                Ui.Write(g, source, Ui.Small, new Rectangle((int)area.X, (int)area.Bottom + Ui.S(6), (int)area.Width, Ui.S(18)), Ui.TextFaint, TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }
    }
}
