using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThorCapture
{
    internal enum ExportKind { Framed, Stacked, SideBySide, Showcase, Single }

    // Background swatch: transparent, a flat colour or a vertical gradient. Stored as
    // "none", "solid:RRGGBB" or "grad:RRGGBB:RRGGBB".
    internal sealed class ExportBackdrop
    {
        public readonly string Key, Name;
        public readonly ExportBackground Kind;
        public readonly Color Color1, Color2;

        public ExportBackdrop(string key, string name)
        {
            Key = key; Name = name;
            string[] parts = (key ?? "").Split(':');
            if (parts.Length == 2 && parts[0] == "solid") { Kind = ExportBackground.Solid; Color1 = Color2 = Hex(parts[1]); }
            else if (parts.Length == 3 && parts[0] == "grad") { Kind = ExportBackground.Gradient; Color1 = Hex(parts[1]); Color2 = Hex(parts[2]); }
            else { Key = "none"; Kind = ExportBackground.Transparent; Color1 = Color2 = Color.Transparent; }
        }

        public bool Transparent { get { return Kind == ExportBackground.Transparent; } }

        public static string Solid(Color color) { return "solid:" + (color.ToArgb() & 0xFFFFFF).ToString("X6"); }

        private static Color Hex(string text)
        {
            int rgb;
            if (text.Length != 6 || !int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out rgb)) return Color.Transparent;
            return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }

        public static readonly ExportBackdrop[] Presets =
        {
            new ExportBackdrop("none", "透明"),
            new ExportBackdrop("solid:ECEDF1", "浅灰"),
            new ExportBackdrop("solid:FFFFFF", "白色"),
            new ExportBackdrop("solid:1C1D21", "石墨"),
            new ExportBackdrop("solid:000000", "纯黑"),
            new ExportBackdrop("grad:EEF1F6:C9D3E2", "晨雾"),
            new ExportBackdrop("grad:F3DCCB:C4B6E2", "暮光"),
            new ExportBackdrop("grad:2B3048:0F1118", "夜空"),
        };

        // JPEG and the clipboard's plain bitmap have no alpha; they fall back to the light grey.
        public static readonly Color Fallback = Color.FromArgb(236, 237, 241);
    }

    // Everything the export dialog lets the user choose; remembered between exports.
    internal sealed class ExportStyle
    {
        public ExportKind Kind = ExportKind.Framed;
        public string Background = "none";
        public int Padding = 2;          // 0 none, 1 narrow, 2 standard, 3 wide
        public bool Shadow = true, Rounded = true, Jpeg;
        public int Scale = 1;

        // Framed padding is in shell design units, mosaic padding and gaps in screen pixels.
        private static readonly int[] FramePad = { 0, 18, 44, 96 }, TilePad = { 0, 32, 64, 128 }, TileGap = { 16, 24, 48, 72 };
        public const int CornerRadius = 36;

        public static ExportStyle Load()
        {
            ExportStyle style = new ExportStyle();
            style.Background = new ExportBackdrop(Settings.Get("export.bg", "none"), null).Key;
            int value;
            if (int.TryParse(Settings.Get("export.pad", "2"), out value)) style.Padding = Math.Max(0, Math.Min(3, value));
            if (int.TryParse(Settings.Get("export.scale", "1"), out value)) style.Scale = Math.Max(1, Math.Min(3, value));
            style.Shadow = Settings.Get("export.shadow", "1") != "0";
            style.Rounded = Settings.Get("export.round", "1") != "0";
            style.Jpeg = Settings.Get("export.format", "png") == "jpg";
            return style;
        }

        public void Save()
        {
            Settings.Set("export.bg", Background);
            Settings.Set("export.pad", Padding.ToString());
            Settings.Set("export.scale", Scale.ToString());
            Settings.Set("export.shadow", Shadow ? "1" : "0");
            Settings.Set("export.round", Rounded ? "1" : "0");
            Settings.Set("export.format", Jpeg ? "jpg" : "png");
        }

        public ExportStyle Clone() { return (ExportStyle)MemberwiseClone(); }

        public bool Showcase { get { return Kind == ExportKind.Showcase; } }

        // What actually gets drawn behind: JPEG cannot be transparent.
        public ExportBackdrop Backdrop
        {
            get
            {
                ExportBackdrop chosen = new ExportBackdrop(Background, null);
                return Jpeg && chosen.Transparent ? new ExportBackdrop(ExportBackdrop.Solid(ExportBackdrop.Fallback), null) : chosen;
            }
        }

        public bool HasAlpha { get { return !Showcase && Backdrop.Transparent; } }

        public string Extension { get { return Jpeg ? ".jpg" : ".png"; } }

        public string FilePrefix
        {
            get
            {
                switch (Kind)
                {
                    case ExportKind.Stacked: return "thor-dual-";
                    case ExportKind.SideBySide: return "thor-side-";
                    case ExportKind.Showcase: return "thor-showcase-";
                    case ExportKind.Single: return "thor-shot-";
                    default: return "thor-framed-";
                }
            }
        }

        public ImageExportPlan Plan(DeviceSkin skin, bool preview)
        {
            ExportLayout layout = Kind == ExportKind.Stacked ? ExportLayout.StackVertical : Kind == ExportKind.SideBySide ? ExportLayout.SideBySide
                : Kind == ExportKind.Single ? ExportLayout.Single : ExportLayout.Framed;
            bool framed = layout == ExportLayout.Framed;
            int n = preview ? 1 : Scale;
            ExportBackdrop back = Backdrop;
            return new ImageExportPlan(layout, skin, back.Kind, back.Kind == ExportBackground.Transparent ? ExportBackdrop.Fallback : back.Color1, back.Color2,
                null, n, framed ? 0 : TileGap[Padding] * n, Color.Transparent, Rounded && !framed ? CornerRadius * n : 0,
                Jpeg ? ImageFormat.Jpeg : ImageFormat.Png, 92, false, Shadow, framed ? FramePad[Padding] : TilePad[Padding]);
        }

        // Output size in pixels; throws ArgumentException when the picture would be too large.
        public Size Measure(DeviceSkin skin, Image top, Image bottom, string title, string label)
        {
            if (Showcase)
            {
                Size page;
                PreviewBox.ShowcaseLayout(top.Size, bottom.Size, !string.IsNullOrWhiteSpace(title), !string.IsNullOrWhiteSpace(label), out page);
                return page;
            }
            return ImageExporter.MeasureOutput(Plan(skin, false), top == null ? (Size?)null : top.Size, bottom == null ? (Size?)null : bottom.Size);
        }

        // Full-size picture, or one fitted inside `fit` for the dialog's preview.
        public Bitmap Render(DeviceSkin skin, Image top, Image bottom, string title, string label, Size? fit)
        {
            if (Showcase)
            {
                Size page = Measure(skin, top, bottom, title, label);
                float s = fit.HasValue ? Math.Min(1f, Math.Min(fit.Value.Width / (float)page.Width, fit.Value.Height / (float)page.Height)) : 1f;
                Bitmap sheet = new Bitmap(Math.Max(1, (int)Math.Round(page.Width * s)), Math.Max(1, (int)Math.Round(page.Height * s)), PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(sheet))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.ScaleTransform(s, s);
                    PreviewBox.DrawShowcase(g, top, bottom, title, label);
                }
                return sheet;
            }
            if (!fit.HasValue) return ImageExporter.Render(Plan(skin, false), top, bottom);
            ImageExportPlan plan = Plan(skin, true);
            Size natural = ImageExporter.MeasureOutput(plan, top == null ? (Size?)null : top.Size, bottom == null ? (Size?)null : bottom.Size);
            float k = Math.Min(1f, Math.Min(fit.Value.Width / (float)natural.Width, fit.Value.Height / (float)natural.Height));
            Size small = new Size(Math.Max(1, (int)Math.Round(natural.Width * k)), Math.Max(1, (int)Math.Round(natural.Height * k)));
            if (plan.Layout == ExportLayout.Framed)
            {
                // The shell is vector art: draw it straight at the small size.
                ImageExportPlan fitted = new ImageExportPlan(plan.Layout, plan.Skin, plan.Background, plan.Color1, plan.Color2, small, 1, plan.Gap, plan.GapColor,
                    plan.CornerRadius, plan.Format, plan.JpegQuality, false, plan.Shadow, plan.Padding);
                return ImageExporter.Render(fitted, top, bottom);
            }
            // Mosaics are drawn pixel for pixel; shrink the full picture smoothly instead.
            using (Bitmap full = ImageExporter.Render(plan, top, bottom))
            {
                Bitmap result = new Bitmap(small.Width, small.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(result))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(full, new Rectangle(0, 0, small.Width, small.Height));
                }
                return result;
            }
        }
    }

    internal static class ImageOutput
    {
        // Writes beside the target first so a failed export never leaves half a file behind.
        public static void Save(Bitmap bitmap, string path, bool jpeg)
        {
            string target = Path.GetFullPath(path);
            string temporary = Path.Combine(Path.GetDirectoryName(target), ".thor-export-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                if (jpeg)
                {
                    ImageCodecInfo codec = null;
                    foreach (ImageCodecInfo candidate in ImageCodecInfo.GetImageEncoders()) if (candidate.FormatID == ImageFormat.Jpeg.Guid) codec = candidate;
                    using (Bitmap flat = Flatten(bitmap, ExportBackdrop.Fallback))
                    using (EncoderParameters parameters = new EncoderParameters(1))
                    {
                        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                        flat.Save(temporary, codec, parameters);
                    }
                }
                else bitmap.Save(temporary, ImageFormat.Png);
                if (File.Exists(target)) File.Delete(target);
                File.Move(temporary, target);
                temporary = null;
            }
            finally { if (temporary != null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        // The clipboard gets the PNG itself (keeps transparency in apps that read it) plus a flat
        // bitmap on the fallback colour for everything else.
        public static void ToClipboard(Bitmap bitmap)
        {
            DataObject data = new DataObject();
            using (MemoryStream png = new MemoryStream())
            using (Bitmap flat = Flatten(bitmap, ExportBackdrop.Fallback))
            {
                bitmap.Save(png, ImageFormat.Png);
                data.SetData("PNG", false, png);
                data.SetImage(flat);
                Clipboard.SetDataObject(data, true);
            }
        }

        public static Bitmap Flatten(Bitmap bitmap, Color backdrop)
        {
            Bitmap flat = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(flat))
            {
                g.Clear(backdrop);
                g.DrawImage(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            }
            return flat;
        }
    }

    // A row of background swatches; the last one opens a colour picker.
    internal sealed class SwatchRow : Control
    {
        private readonly List<ExportBackdrop> items = new List<ExportBackdrop>(ExportBackdrop.Presets);
        private ExportBackdrop custom;
        private int hover = -1;
        private string selected = "none";
        private readonly ToolTip tip = new ToolTip();
        private int tipFor = -2;
        public event EventHandler Changed;
        public event EventHandler CustomRequested;
        public bool TransparentAllowed = true;

        public SwatchRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Height = Ui.S(40);
            Cursor = Cursors.Hand;
        }

        public string Selected
        {
            get { return selected; }
            set
            {
                ExportBackdrop backdrop = new ExportBackdrop(value, "自定义");
                selected = backdrop.Key;
                if (items.FindIndex(b => b.Key == selected) < 0) custom = backdrop;
                Invalidate();
            }
        }

        private int Count { get { return items.Count + 1; } }

        private Rectangle Cell(int i)
        {
            int size = Ui.S(34), gap = Count > 1 ? Math.Max(Ui.S(4), (Width - size * Count) / (Count - 1)) : 0;
            return new Rectangle(i * (size + gap), (Height - size) / 2, size, size);
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < Count; i++) if (Cell(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int at = HitTest(e.Location);
            if (at != hover) { hover = at; Invalidate(); }
            if (at != tipFor)
            {
                tipFor = at;
                string text = at < 0 ? null : at < items.Count ? items[at].Name : custom != null ? "自定义颜色（点击更换）" : "自定义颜色…";
                tip.SetToolTip(this, text);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; tipFor = -2; tip.SetToolTip(this, null); Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int at = HitTest(e.Location);
            if (at < 0 || !Enabled) return;
            if (at == items.Count) { if (CustomRequested != null) CustomRequested(this, EventArgs.Empty); return; }
            if (!TransparentAllowed && items[at].Transparent) return;
            selected = items[at].Key;
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        public void SetCustom(Color color)
        {
            custom = new ExportBackdrop(ExportBackdrop.Solid(color), "自定义");
            selected = custom.Key;
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < Count; i++)
            {
                Rectangle cell = Cell(i);
                ExportBackdrop item = i < items.Count ? items[i] : custom;
                bool chosen = item != null && item.Key == selected && (i < items.Count || items.FindIndex(b => b.Key == selected) < 0);
                bool blocked = !Enabled || (item != null && item.Transparent && !TransparentAllowed);
                RectangleF chip = new RectangleF(cell.X + Ui.S(4), cell.Y + Ui.S(4), cell.Width - Ui.S(8), cell.Height - Ui.S(8));
                if (chosen) Ui.Stroke(g, new RectangleF(cell.X + 1, cell.Y + 1, cell.Width - 2, cell.Height - 2), Ui.S(10), Ui.Accent, Ui.S(2));
                else if (i == hover && !blocked) Ui.Stroke(g, new RectangleF(cell.X + 1, cell.Y + 1, cell.Width - 2, cell.Height - 2), Ui.S(10), Ui.Bg3, Ui.S(2));
                using (GraphicsPath path = Ui.Round(chip, Ui.S(7)))
                {
                    if (item == null)
                    {
                        // Empty custom slot: a dashed outline with a plus.
                        using (Pen dash = new Pen(Ui.TextFaint, Ui.S(1)) { DashStyle = DashStyle.Dash }) g.DrawPath(dash, path);
                        float cx = chip.X + chip.Width / 2, cy = chip.Y + chip.Height / 2, r = chip.Width * .22f;
                        using (Pen plus = new Pen(Ui.TextDim, Ui.S(1.5f))) { g.DrawLine(plus, cx - r, cy, cx + r, cy); g.DrawLine(plus, cx, cy - r, cx, cy + r); }
                        continue;
                    }
                    if (item.Transparent) PaintChecker(g, path, chip, Ui.S(5));
                    else if (item.Kind == ExportBackground.Gradient)
                        using (LinearGradientBrush brush = new LinearGradientBrush(chip, item.Color1, item.Color2, 90f)) g.FillPath(brush, path);
                    else using (SolidBrush brush = new SolidBrush(item.Color1)) g.FillPath(brush, path);
                    using (Pen edge = new Pen(Color.FromArgb(40, 0, 0, 0), 1)) g.DrawPath(edge, path);
                    if (blocked) using (SolidBrush veil = new SolidBrush(Color.FromArgb(170, Ui.Bg1))) g.FillPath(veil, path);
                }
            }
        }

        internal static void PaintChecker(Graphics g, GraphicsPath clip, RectangleF area, int cell)
        {
            GraphicsState state = g.Save();
            g.SetClip(clip, CombineMode.Intersect);
            g.SmoothingMode = SmoothingMode.None;
            using (SolidBrush light = new SolidBrush(Color.FromArgb(252, 252, 251)), dark = new SolidBrush(Color.FromArgb(222, 221, 218)))
            {
                g.FillRectangle(light, area);
                for (int y = 0; y * cell < area.Height; y++)
                    for (int x = (y & 1); x * cell < area.Width; x += 2)
                        g.FillRectangle(dark, area.X + x * cell, area.Y + y * cell, cell, cell);
            }
            g.Restore(state);
        }
    }

    // Shows the rendered picture on a checkerboard so transparency is visible.
    internal sealed class ExportPreviewPanel : Control
    {
        private Bitmap image;
        private bool busy;
        public string Message;

        public ExportPreviewPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        public Size Room { get { return new Size(Math.Max(1, Width - Ui.S(40)), Math.Max(1, Height - Ui.S(40))); } }

        public bool Busy { get { return busy; } set { if (busy == value) return; busy = value; Invalidate(); } }

        public void SetImage(Bitmap next)
        {
            if (image != null) image.Dispose();
            image = next;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && image != null) { image.Dispose(); image = null; }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF card = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Ui.Fill(g, card, Ui.S(16), Ui.Bg0);
            if (image != null)
            {
                Size room = Room;
                float k = Math.Min(1f, Math.Min(room.Width / (float)image.Width, room.Height / (float)image.Height));
                int w = (int)Math.Round(image.Width * k), h = (int)Math.Round(image.Height * k);
                Rectangle place = new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
                using (GraphicsPath box = new GraphicsPath()) { box.AddRectangle(place); SwatchRow.PaintChecker(g, box, place, Ui.S(8)); }
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(image, place);
                if (busy) using (SolidBrush veil = new SolidBrush(Color.FromArgb(90, Ui.Bg0))) g.FillRectangle(veil, place);
            }
            string text = Message ?? (image == null || busy ? "正在生成预览…" : null);
            if (text != null)
                Ui.Write(g, text, Ui.Small, new Rectangle(Ui.S(16), Height - Ui.S(34), Width - Ui.S(32), Ui.S(24)), Message != null ? Ui.Danger : Ui.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // Image export with a live preview: layout, background (transparent PNG), padding, shadow,
    // rounded screens, format and integer scale.
    internal sealed class ExportImageDialog : ThemedDialog
    {
        private readonly ExportStyle style;
        private readonly DeviceSkin skin;
        private readonly Image top, bottom;
        private readonly string title, label, skinKey;
        private readonly ExportKind[] kinds;
        private readonly Segmented layout, padding, format, scale;
        private readonly SwatchRow swatches;
        private readonly ToggleSwitch shadow, rounded;
        private readonly Label sizeLine, note;
        private readonly ExportPreviewPanel preview;
        private readonly FlatButton copy, save;
        private readonly Timer debounce = new Timer { Interval = 90 };
        private readonly object gate = new object();
        private int version;
        private bool working;

        // Set when the dialog closes after copying or saving, for the main window to show.
        public string Outcome;
        public bool OutcomeIsError;

        // Images are copied, so rendering on a worker thread never touches the live preview's bitmaps.
        public ExportImageDialog(ExportStyle style, DeviceSkin skin, Image top, Image bottom, string title, string label)
            : base("导出图片", null, null, 900, 540)
        {
            this.style = style;
            this.skin = ImageExportPlan.CopySkin(skin);
            skinKey = skin.Key;
            this.top = top == null ? null : new Bitmap(top);
            this.bottom = bottom == null ? null : new Bitmap(bottom);
            this.title = title ?? "";
            this.label = label ?? "";
            bool pair = top != null && bottom != null;
            kinds = pair ? new[] { ExportKind.Framed, ExportKind.Stacked, ExportKind.SideBySide, ExportKind.Showcase } : new[] { ExportKind.Framed, ExportKind.Single };
            if (Array.IndexOf(kinds, style.Kind) < 0) style.Kind = ExportKind.Framed;

            preview = new ExportPreviewPanel();
            preview.SetBounds(Ui.S(24), Ui.S(12), Ui.S(440), Ui.S(486));
            Body.Controls.Add(preview);
            sizeLine = AddText("", Ui.Small, Ui.TextDim, 24, 506, 440, 22);
            sizeLine.TextAlign = ContentAlignment.MiddleCenter;

            const int x = 492, w = 384;
            layout = new Segmented(pair ? new[] { "带壳", "上下拼接", "左右拼接", "展示图" } : new[] { "带壳", "仅截图" }) { Stretch = true };
            layout.SelectedIndex = Array.IndexOf(kinds, style.Kind);
            AddField("版式", layout, x, 8, w);

            swatches = new SwatchRow { Selected = style.Background };
            AddField("背景", swatches, x, 78, w);

            padding = new Segmented("无", "窄", "标准", "宽") { Stretch = true };
            padding.SelectedIndex = style.Padding;
            AddField("留白", padding, x, 152, w);

            shadow = new ToggleSwitch("投影", style.Shadow);
            AddField("效果", shadow, x, 222, 180);
            rounded = new ToggleSwitch("屏幕圆角", style.Rounded);
            AddField("", rounded, x + 204, 222, 180);

            format = new Segmented("PNG", "JPEG") { Stretch = true };
            format.SelectedIndex = style.Jpeg ? 1 : 0;
            AddField("格式", format, x, 300, 180);
            scale = new Segmented("1×", "2×", "3×") { Stretch = true };
            scale.SelectedIndex = style.Scale - 1;
            AddField("尺寸", scale, x + 204, 300, 180);

            note = AddText("", Ui.Small, Ui.TextDim, x, 372, w, 60);

            AddAction("取消", ButtonKind.Subtle, DialogResult.Cancel);
            copy = AddAction("复制到剪贴板", ButtonKind.Subtle, DialogResult.None);
            save = AddAction("导出…", ButtonKind.Primary, DialogResult.None);
            copy.Click += delegate { Deliver(false); };
            save.Click += delegate { Deliver(true); };

            layout.SelectedIndexChanged += delegate { style.Kind = kinds[Math.Max(0, layout.SelectedIndex)]; Changed(); };
            swatches.Changed += delegate { style.Background = swatches.Selected; Changed(); };
            swatches.CustomRequested += delegate
            {
                using (ColorDialog picker = new ColorDialog())
                {
                    picker.FullOpen = true;
                    ExportBackdrop current = new ExportBackdrop(style.Background, null);
                    picker.Color = current.Transparent ? ExportBackdrop.Fallback : current.Color1;
                    if (picker.ShowDialog(this) == DialogResult.OK) swatches.SetCustom(picker.Color);
                }
            };
            padding.SelectedIndexChanged += delegate { style.Padding = Math.Max(0, padding.SelectedIndex); Changed(); };
            shadow.CheckedChanged += delegate { style.Shadow = shadow.Checked; Changed(); };
            rounded.CheckedChanged += delegate { style.Rounded = rounded.Checked; Changed(); };
            format.SelectedIndexChanged += delegate { style.Jpeg = format.SelectedIndex == 1; Changed(); };
            scale.SelectedIndexChanged += delegate { style.Scale = Math.Max(1, scale.SelectedIndex + 1); Changed(); };
            debounce.Tick += delegate { debounce.Stop(); RenderPreview(); };
            Shown += delegate { RenderPreview(); };
            Changed();
        }

        public ExportStyle Style { get { return style; } }

        private void Changed()
        {
            bool showcase = style.Showcase, framed = style.Kind == ExportKind.Framed;
            swatches.Enabled = padding.Enabled = shadow.Enabled = scale.Enabled = !showcase;
            rounded.Enabled = !showcase && !framed;
            swatches.TransparentAllowed = !style.Jpeg;

            string text;
            if (showcase) text = "展示图使用固定的深色版式，标题和下屏说明在预览工具栏的「文字…」里修改。";
            else if (style.Jpeg && new ExportBackdrop(style.Background, null).Transparent) text = "JPEG 不支持透明，将使用浅灰底色。需要透明背景请选 PNG。";
            else if (style.Backdrop.Transparent) text = framed ? "透明 PNG：外壳以外全部透明，投影保留柔和的半透明边缘。" : "透明 PNG：屏幕以外全部透明，圆角与投影边缘保持柔和。";
            else if (style.Scale > 1) text = "整数倍放大，像素保持锐利，适合像素风游戏。";
            else text = framed ? "上屏按 1920 像素原尺寸绘制。" : "屏幕按原始分辨率逐像素绘制。";
            note.Text = text;

            Size size = Size.Empty;
            string error = null;
            try { size = style.Measure(skin, top, bottom, title, label); }
            catch (ArgumentException) { error = "尺寸超过上限（4800 万像素），请降低倍率或留白"; }
            sizeLine.Text = error ?? (size.Width + " × " + size.Height + " 像素 · " + (style.HasAlpha ? "透明 PNG" : style.Jpeg ? "JPEG" : "PNG"));
            sizeLine.ForeColor = error != null ? Ui.Danger : Ui.TextDim;
            copy.Enabled = save.Enabled = error == null && !working;
            debounce.Stop();
            debounce.Start();
            preview.Busy = true;
        }

        private async void RenderPreview()
        {
            int ticket = ++version;
            ExportStyle snapshot = style.Clone();
            Size room = preview.Room;
            Bitmap result = null;
            string error = null;
            try
            {
                result = await Task.Run(delegate
                {
                    lock (gate) return ticket == version ? snapshot.Render(skin, top, bottom, title, label, room) : null;
                });
            }
            catch (ArgumentException) { error = "这个组合无法生成预览"; }
            if (IsDisposed || ticket != version) { if (result != null) result.Dispose(); return; }
            preview.Message = error;
            if (result != null) preview.SetImage(result);
            preview.Busy = false;
        }

        private async void Deliver(bool toFile)
        {
            if (working) return;
            string path = null;
            if (toFile)
            {
                using (SaveFileDialog dialog = new SaveFileDialog())
                {
                    dialog.Filter = style.Jpeg ? "JPEG 图片|*.jpg" : "PNG 图片|*.png";
                    dialog.FileName = style.FilePrefix + (style.Kind == ExportKind.Framed ? skinKey + "-" : "") + DateTime.Now.ToString("yyyyMMdd-HHmmss") + style.Extension;
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    path = dialog.FileName;
                }
            }
            working = true;
            copy.Enabled = save.Enabled = false;
            save.Text = toFile ? "正在导出…" : save.Text;
            copy.Text = toFile ? copy.Text : "正在复制…";
            ExportStyle snapshot = style.Clone();
            Bitmap picture = null;
            string failure = null;
            try
            {
                picture = await Task.Run(delegate
                {
                    lock (gate)
                    {
                        Bitmap made = snapshot.Render(skin, top, bottom, title, label, null);
                        if (path != null) ImageOutput.Save(made, path, snapshot.Jpeg);
                        return made;
                    }
                });
                if (path == null) ImageOutput.ToClipboard(picture);
            }
            catch (Exception error) { failure = error.Message; }
            finally { if (picture != null) picture.Dispose(); }
            if (IsDisposed) return;
            style.Save();
            if (failure != null) { Outcome = failure; OutcomeIsError = true; }
            else Outcome = path != null ? "已导出 " + Path.GetFileName(path) : snapshot.HasAlpha ? "已复制到剪贴板（支持透明的软件会保留透明背景）" : "已复制到剪贴板";
            DialogResult = DialogResult.OK;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                debounce.Dispose();
                version++;
                // A preview render may still be using the copies; wait for it before freeing them.
                lock (gate)
                {
                    if (top != null) top.Dispose();
                    if (bottom != null) bottom.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
