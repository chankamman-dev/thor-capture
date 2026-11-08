using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ThorCapture
{
    // Minimal JSON reader for backend events and API replies (no extra assemblies allowed).
    // Objects become Dictionary<string, object>, arrays List<object>, numbers double,
    // plus string, bool and null. Input size and nesting are bounded.
    internal static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) throw new InvalidDataException("JSON 为空");
            if (text.Length > 64 * 1024 * 1024) throw new InvalidDataException("JSON 过大");
            int i = 0;
            object value = Value(text, ref i, 0);
            Skip(text, ref i);
            if (i != text.Length) throw new InvalidDataException("JSON 末尾有多余内容");
            return value;
        }

        private static void Skip(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object Value(string s, ref int i, int depth)
        {
            if (depth > 64) throw new InvalidDataException("JSON 嵌套过深");
            Skip(s, ref i);
            if (i >= s.Length) throw new InvalidDataException("JSON 不完整");
            char c = s[i];
            if (c == '{')
            {
                Dictionary<string, object> obj = new Dictionary<string, object>(StringComparer.Ordinal);
                i++; Skip(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                while (true)
                {
                    Skip(s, ref i);
                    string key = Str(s, ref i);
                    Skip(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new InvalidDataException("JSON 缺少冒号");
                    i++;
                    obj[key] = Value(s, ref i, depth + 1);
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return obj; }
                    throw new InvalidDataException("JSON 对象未结束");
                }
            }
            if (c == '[')
            {
                List<object> list = new List<object>();
                i++; Skip(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(Value(s, ref i, depth + 1));
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return list; }
                    throw new InvalidDataException("JSON 数组未结束");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            double number;
            if (i == start || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                throw new InvalidDataException("JSON 数值无效");
            return number;
        }

        private static bool Word(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        private static string Str(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new InvalidDataException("JSON 字符串无效");
            i++;
            StringBuilder text = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return text.ToString();
                if (c != '\\') { text.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': text.Append('"'); break;
                    case '\\': text.Append('\\'); break;
                    case '/': text.Append('/'); break;
                    case 'b': text.Append('\b'); break;
                    case 'f': text.Append('\f'); break;
                    case 'n': text.Append('\n'); break;
                    case 'r': text.Append('\r'); break;
                    case 't': text.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new InvalidDataException("JSON 转义不完整");
                        text.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new InvalidDataException("JSON 转义无效");
                }
            }
            throw new InvalidDataException("JSON 字符串未结束");
        }

        // Tolerant accessors: a missing or mistyped field reads as the fallback.
        public static object Get(object node, string key)
        {
            Dictionary<string, object> obj = node as Dictionary<string, object>;
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value : null;
        }

        public static string Text(object node, string key) { return Get(node, key) as string; }

        public static double Number(object node, string key, double fallback)
        {
            object value = Get(node, key);
            return value is double ? (double)value : fallback;
        }

        public static bool Flag(object node, string key) { object value = Get(node, key); return value is bool && (bool)value; }

        public static string Quote(string value)
        {
            StringBuilder text = new StringBuilder("\"");
            foreach (char c in value ?? "")
            {
                if (c == '"' || c == '\\') text.Append('\\').Append(c);
                else if (c < 32) text.Append("\\u").Append(((int)c).ToString("x4"));
                else text.Append(c);
            }
            return text.Append('"').ToString();
        }
    }
}
