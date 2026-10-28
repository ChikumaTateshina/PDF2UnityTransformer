using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PDF2Unity.Core.Util
{
    /// <summary>Deterministic JSON writer (sorted keys, fixed number formatting).</summary>
    public static class JsonOut
    {
        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        static void Indent(StringBuilder sb, int n) => sb.Append(' ', n * 2);

        static void WriteValue(StringBuilder sb, object v, int depth)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: Str(sb, s); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case float f: Num(sb, f); break;
                case double d: Num(sb, d); break;
                case IDictionary<string, object> dict:
                    {
                        var keys = new List<string>(dict.Keys);
                        keys.Sort(StringComparer.Ordinal);
                        if (keys.Count == 0) { sb.Append("{}"); break; }
                        sb.Append("{\n");
                        for (int k = 0; k < keys.Count; k++)
                        {
                            Indent(sb, depth + 1);
                            Str(sb, keys[k]);
                            sb.Append(": ");
                            WriteValue(sb, dict[keys[k]], depth + 1);
                            sb.Append(k + 1 < keys.Count ? ",\n" : "\n");
                        }
                        Indent(sb, depth);
                        sb.Append('}');
                    }
                    break;
                case IEnumerable list:
                    {
                        var items = new List<object>();
                        foreach (var o in list) items.Add(o);
                        bool simple = items.TrueForAll(o => o == null || o is double || o is int || o is long || o is float || o is bool);
                        if (items.Count == 0) { sb.Append("[]"); break; }
                        if (simple)
                        {
                            sb.Append('[');
                            for (int k = 0; k < items.Count; k++) { if (k > 0) sb.Append(", "); WriteValue(sb, items[k], depth); }
                            sb.Append(']');
                            break;
                        }
                        sb.Append("[\n");
                        for (int k = 0; k < items.Count; k++)
                        {
                            Indent(sb, depth + 1);
                            WriteValue(sb, items[k], depth + 1);
                            sb.Append(k + 1 < items.Count ? ",\n" : "\n");
                        }
                        Indent(sb, depth);
                        sb.Append(']');
                    }
                    break;
                default: Str(sb, v.ToString()); break;
            }
        }

        static void Num(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append('0'); return; }
            double r = Math.Round(d, 6);
            if (r == 0) r = 0;
            sb.Append(r.ToString("0.######", CultureInfo.InvariantCulture));
        }

        static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
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
}
