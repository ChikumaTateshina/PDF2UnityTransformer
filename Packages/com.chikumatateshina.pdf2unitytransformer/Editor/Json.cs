using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PDF2Unity.Editor
{
    /// <summary>
    /// Minimal JSON reader for the exchange package (objects -> Dictionary, arrays -> List,
    /// numbers -> double). JsonUtility cannot read dictionaries, and the package must not
    /// depend on third-party assemblies.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var p = new Parser(text);
            var v = p.ParseValue();
            p.SkipWs();
            if (!p.End) throw new FormatException("trailing characters in JSON");
            return v;
        }

        sealed class Parser
        {
            readonly string s;
            int i;
            int depth;

            public Parser(string s) { this.s = s; }
            public bool End => i >= s.Length;

            public void SkipWs()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            public object ParseValue()
            {
                SkipWs();
                if (End) throw new FormatException("unexpected end of JSON");
                char c = s[i];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ParseNumber();
                }
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                    throw new FormatException($"expected {word} at {i}");
                i += word.Length;
            }

            Dictionary<string, object> ParseObject()
            {
                if (++depth > 256) throw new FormatException("JSON nesting too deep");
                var d = new Dictionary<string, object>();
                i++;
                SkipWs();
                if (s[i] == '}') { i++; depth--; return d; }
                while (true)
                {
                    SkipWs();
                    var key = ParseString();
                    SkipWs();
                    if (s[i] != ':') throw new FormatException($"expected ':' at {i}");
                    i++;
                    d[key] = ParseValue();
                    SkipWs();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; break; }
                    throw new FormatException($"expected ',' or '}}' at {i}");
                }
                depth--;
                return d;
            }

            List<object> ParseArray()
            {
                if (++depth > 256) throw new FormatException("JSON nesting too deep");
                var l = new List<object>();
                i++;
                SkipWs();
                if (s[i] == ']') { i++; depth--; return l; }
                while (true)
                {
                    l.Add(ParseValue());
                    SkipWs();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; break; }
                    throw new FormatException($"expected ',' or ']' at {i}");
                }
                depth--;
                return l;
            }

            string ParseString()
            {
                if (s[i] != '"') throw new FormatException($"expected string at {i}");
                i++;
                var sb = new StringBuilder();
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                            i += 4;
                            break;
                        default: throw new FormatException($"bad escape at {i}");
                    }
                }
                throw new FormatException("unterminated string");
            }

            object ParseNumber()
            {
                int start = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                if (start == i) throw new FormatException($"unexpected character '{s[i]}' at {i}");
                return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Typed accessors over parsed JSON.</summary>
    public static class J
    {
        public static Dictionary<string, object> Obj(object o, string key)
            => (o as Dictionary<string, object>)?.TryGetValue(key, out var v) == true ? v as Dictionary<string, object> : null;

        public static List<object> Arr(object o, string key)
            => (o as Dictionary<string, object>)?.TryGetValue(key, out var v) == true ? v as List<object> : null;

        public static string Str(object o, string key, string def = null)
            => (o as Dictionary<string, object>)?.TryGetValue(key, out var v) == true && v is string s ? s : def;

        public static double Num(object o, string key, double def = 0)
            => (o as Dictionary<string, object>)?.TryGetValue(key, out var v) == true && v is double d ? d : def;

        public static bool Bool(object o, string key, bool def = false)
            => (o as Dictionary<string, object>)?.TryGetValue(key, out var v) == true && v is bool b ? b : def;

        public static float[] Floats(object o, string key)
        {
            var a = Arr(o, key);
            if (a == null) return null;
            var r = new float[a.Count];
            for (int k = 0; k < a.Count; k++) r[k] = (float)(double)a[k];
            return r;
        }
    }
}
