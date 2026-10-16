#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// Pure logic (no Unity, no logging): also compiled into the bridge's `selftest` (bridge/DescendersBridge.csproj links this file).
namespace FreeTurboDescenders
{
    /// <summary>Minimal JSON writer (invariant culture, compact).</summary>
    internal sealed class JsonWriter
    {
        private readonly StringBuilder _sb = new StringBuilder(512);
        private readonly Stack<bool> _first = new Stack<bool>();

        public JsonWriter Obj() { Sep(); _sb.Append('{'); _first.Push(true); return this; }
        public JsonWriter Obj(string name) { Key(name); _sb.Append('{'); _first.Push(true); return this; }
        public JsonWriter End() { _sb.Append('}'); _first.Pop(); return this; }
        public JsonWriter Arr(string name) { Key(name); _sb.Append('['); _first.Push(true); return this; }
        public JsonWriter EndArr() { _sb.Append(']'); _first.Pop(); return this; }

        public JsonWriter Prop(string name, string v) { Key(name); if (v == null) _sb.Append("null"); else Str(v); return this; }
        public JsonWriter Prop(string name, bool v) { Key(name); _sb.Append(v ? "true" : "false"); return this; }
        public JsonWriter PropBool(string name, bool? v) { Key(name); _sb.Append(v.HasValue ? (v.Value ? "true" : "false") : "null"); return this; }
        public JsonWriter Prop(string name, long v) { Key(name); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
        public JsonWriter Prop(string name, float v)
        {
            Key(name);
            if (float.IsNaN(v) || float.IsInfinity(v)) _sb.Append("null");
            else _sb.Append(v.ToString("0.####", CultureInfo.InvariantCulture));
            return this;
        }
        public JsonWriter PropNull(string name) { Key(name); _sb.Append("null"); return this; }
        /// <summary>A string element of the open array.</summary>
        public JsonWriter Val(string v) { Sep(); if (v == null) _sb.Append("null"); else Str(v); return this; }

        public override string ToString() { return _sb.ToString(); }

        private void Sep()
        {
            if (_first.Count == 0) return;
            if (_first.Peek()) { _first.Pop(); _first.Push(false); }
            else _sb.Append(',');
        }

        private void Key(string name) { Sep(); Str(name); _sb.Append(':'); }

        private void Str(string s) { Quote(_sb, s); }

        /// <summary>Appends <paramref name="s"/> as a quoted, escaped JSON string.</summary>
        internal static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>
    /// Writes a <see cref="JsonReader"/> tree (Dictionary / List / string / double / bool / null) back out, indented, for files a
    /// person may open and edit (the shared TrainerBridgeConfig.json, whose other sections must survive a rewrite).
    /// </summary>
    internal static class JsonPretty
    {
        public static string Write(object tree)
        {
            var sb = new StringBuilder(1024);
            Value(sb, tree, 0);
            sb.Append(Environment.NewLine);
            return sb.ToString();
        }

        private static void Value(StringBuilder sb, object v, int depth)
        {
            if (v == null) { sb.Append("null"); return; }
            var s = v as string;
            if (s != null) { JsonWriter.Quote(sb, s); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is double) { Number(sb, (double)v); return; }
            var d = v as Dictionary<string, object>;
            if (d != null)
            {
                if (d.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                bool first = true;
                foreach (var kv in d)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Indent(sb, depth + 1);
                    JsonWriter.Quote(sb, kv.Key);
                    sb.Append(": ");
                    Value(sb, kv.Value, depth + 1);
                }
                Indent(sb, depth);
                sb.Append('}');
                return;
            }
            var l = v as List<object>;
            if (l != null)
            {
                if (l.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int i = 0; i < l.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Indent(sb, depth + 1);
                    Value(sb, l[i], depth + 1);
                }
                Indent(sb, depth);
                sb.Append(']');
                return;
            }
            throw new ArgumentException("not a JSON tree value: " + v.GetType().Name);
        }

        private static void Number(StringBuilder sb, double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) sb.Append("null");
            else if (x == Math.Floor(x) && Math.Abs(x) < 1e15) sb.Append(((long)x).ToString(CultureInfo.InvariantCulture));
            else sb.Append(x.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void Indent(StringBuilder sb, int depth) { sb.Append(Environment.NewLine).Append(' ', depth * 2); }
    }

    /// <summary>Minimal recursive-descent JSON parser: Dictionary / List / string / double / bool / null.</summary>
    internal static class JsonReader
    {
        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i);
            Ws(s, ref i);
            if (i != s.Length) throw new FormatException("Trailing characters at " + i);
            return v;
        }

        private static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end");
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++; Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    if (s[i++] != ':') throw new FormatException("Expected ':' at " + (i - 1));
                    d[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException("Expected ',' or '}' at " + i);
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new FormatException("Expected ',' or ']' at " + i);
                }
            }
            if (c == '"') return Str(s, ref i);
            if (Lit(s, ref i, "true")) return true;
            if (Lit(s, ref i, "false")) return false;
            if (Lit(s, ref i, "null")) return null;
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (st == i) throw new FormatException("Unexpected '" + c + "' at " + i);
            return double.Parse(s.Substring(st, i - st), CultureInfo.InvariantCulture);
        }

        private static bool Lit(string s, ref int i, string lit)
        {
            if (string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0) return false;
            i += lit.Length;
            return true;
        }

        private static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("Expected string at " + i);
            i++;
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                char c = s[i++];
                if (c == '\\')
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            if (i >= s.Length) throw new FormatException("Unterminated string");
            i++;
            return sb.ToString();
        }
    }
}
