using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;

namespace ThorCapture
{
    internal sealed class ExportPreset
    {
        public readonly string Name;
        public readonly ImageExportPlan Plan;
        public ExportPreset(string name, ImageExportPlan plan)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || plan == null) throw new ArgumentException("预设名称或计划无效。");
            Name = name.Trim(); Plan = plan;
        }
    }

    internal static class ExportPresetStore
    {
        public const string SettingsKey = "export.presets";
        public static IList<ExportPreset> LoadPresets() { return Decode(Settings.Get(SettingsKey, "")); }
        public static bool SavePresets(IList<ExportPreset> presets) { return Settings.Set(SettingsKey, Encode(presets)); }

        public static string Encode(IList<ExportPreset> presets)
        {
            if (presets == null || presets.Count > 32) throw new ArgumentException("最多保存 32 个预设。");
            using (MemoryStream memory = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(memory, Encoding.UTF8))
            {
                w.Write(2); w.Write(presets.Count);
                foreach (ExportPreset preset in presets)
                {
                    if (preset == null) throw new ArgumentException("预设不能为空。");
                    w.Write(preset.Name); ImageExportPlan p = preset.Plan;
                    w.Write((int)p.Layout); w.Write((int)p.Background); w.Write(p.Color1.ToArgb()); w.Write(p.Color2.ToArgb());
                    w.Write(p.Canvas.HasValue); if (p.Canvas.HasValue) { w.Write(p.Canvas.Value.Width); w.Write(p.Canvas.Value.Height); }
                    w.Write(p.PixelScale); w.Write(p.Gap); w.Write(p.GapColor.ToArgb()); w.Write(p.CornerRadius);
                    w.Write(p.Format.Guid == ImageFormat.Png.Guid); w.Write(p.JpegQuality); w.Write(p.MarkMissingScreens);
                    DeviceSkin skin = p.Skin;
                    FieldInfo[] fields = typeof(DeviceSkin).GetFields(BindingFlags.Instance | BindingFlags.Public);
                    w.Write(fields.Length);
                    foreach (FieldInfo f in fields)
                    {
                        w.Write(f.Name); object value = f.GetValue(skin);
                        if (f.FieldType == typeof(Color)) { w.Write((byte)0); Color c = (Color)value; w.Write(c.IsEmpty); w.Write(c.ToArgb()); }
                        else if (f.FieldType == typeof(string)) { w.Write((byte)1); w.Write((string)value ?? ""); }
                        else if (f.FieldType == typeof(bool)) { w.Write((byte)2); w.Write((bool)value); }
                        else if (f.FieldType == typeof(float)) { w.Write((byte)3); w.Write((float)value); }
                        else if (f.FieldType == typeof(Color[])) { w.Write((byte)4); Color[] colors = (Color[])value; w.Write(colors == null ? 0 : colors.Length); if (colors != null) foreach (Color c in colors) w.Write(c.ToArgb()); }
                        else throw new ArgumentException("不支持此配色字段。");
                    }
                }
                w.Flush(); return Convert.ToBase64String(memory.ToArray());
            }
        }

        public static IList<ExportPreset> Decode(string encoded)
        {
            List<ExportPreset> result = new List<ExportPreset>();
            if (string.IsNullOrEmpty(encoded) || encoded.Length > 131072) return result;
            try
            {
                using (MemoryStream memory = new MemoryStream(Convert.FromBase64String(encoded)))
                using (BinaryReader r = new BinaryReader(memory, Encoding.UTF8))
                {
                    if (r.ReadInt32() != 2) return result;
                    int count = r.ReadInt32(); if (count < 0 || count > 32) return result;
                    for (int i = 0; i < count; i++)
                    {
                        string name = r.ReadString(); ExportLayout layout = (ExportLayout)r.ReadInt32(); ExportBackground background = (ExportBackground)r.ReadInt32();
                        Color c1 = Color.FromArgb(r.ReadInt32()), c2 = Color.FromArgb(r.ReadInt32());
                        Size? canvas = r.ReadBoolean() ? new Size(r.ReadInt32(), r.ReadInt32()) : (Size?)null;
                        int factor = r.ReadInt32(), gap = r.ReadInt32(); Color gapColor = Color.FromArgb(r.ReadInt32()); float radius = r.ReadSingle();
                        ImageFormat format = r.ReadBoolean() ? ImageFormat.Png : ImageFormat.Jpeg; int quality = r.ReadInt32(); bool missing = r.ReadBoolean();
                        DeviceSkin skin = new DeviceSkin(); int fields = r.ReadInt32(); if (fields != typeof(DeviceSkin).GetFields(BindingFlags.Instance | BindingFlags.Public).Length) return new List<ExportPreset>();
                        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                        for (int j = 0; j < fields; j++)
                        {
                            string fieldName = r.ReadString(); if (!seen.Add(fieldName)) return new List<ExportPreset>(); int kind = r.ReadByte(); object value;
                            if (kind == 0) { bool empty = r.ReadBoolean(); int argb = r.ReadInt32(); value = empty ? Color.Empty : Color.FromArgb(argb); }
                            else if (kind == 1) value = r.ReadString();
                            else if (kind == 2) value = r.ReadBoolean();
                            else if (kind == 3) value = r.ReadSingle();
                            else if (kind == 4) { int n = r.ReadInt32(); if (n != 0 && n != 4) return new List<ExportPreset>(); Color[] colors = n == 0 ? null : new Color[n]; for (int k = 0; k < n; k++) colors[k] = Color.FromArgb(r.ReadInt32()); value = colors; }
                            else return new List<ExportPreset>();
                            FieldInfo field = typeof(DeviceSkin).GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
                            if (field == null || (value != null && field.FieldType != value.GetType()) || (value == null && field.FieldType != typeof(Color[]))) return new List<ExportPreset>();
                            field.SetValue(skin, value);
                        }
                        if (string.IsNullOrEmpty(skin.Key) || float.IsNaN(skin.GlowLevel) || float.IsInfinity(skin.GlowLevel) || skin.GlowLevel < .1f || skin.GlowLevel > 1f) return new List<ExportPreset>();
                        ImageExportPlan plan = new ImageExportPlan(layout, skin, background, c1, c2, canvas, factor, gap, gapColor, radius, format, quality, missing);
                        result.Add(new ExportPreset(name, plan));
                    }
                    if (memory.Position != memory.Length) return new List<ExportPreset>();
                }
            }
            catch (Exception error)
            {
                if (!(error is FormatException) && !(error is IOException) && !(error is ArgumentException)) throw;
                return new List<ExportPreset>();
            }
            return result;
        }
    }
}
