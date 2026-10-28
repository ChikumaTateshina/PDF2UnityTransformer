using System;
using System.Collections.Generic;
using System.Globalization;

namespace PDF2Unity.Core.Pdf
{
    /// <summary>Tokenizer/parser for PDF objects (file syntax and content streams).</summary>
    public sealed class Lexer
    {
        readonly byte[] d;
        public int Pos;
        readonly int end;
        public PdfFile Owner;

        public Lexer(byte[] data, int start = 0, int end = -1)
        {
            d = data;
            Pos = start;
            this.end = end < 0 ? data.Length : end;
        }

        public int End => end;
        public byte[] Data => d;

        public static bool IsWhite(int c) => c == 0 || c == 9 || c == 10 || c == 12 || c == 13 || c == 32;
        public static bool IsDelim(int c) => c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' ||
                                             c == '{' || c == '}' || c == '/' || c == '%';

        public void SkipWhite()
        {
            while (Pos < end)
            {
                int c = d[Pos];
                if (IsWhite(c)) { Pos++; continue; }
                if (c == '%')
                {
                    while (Pos < end && d[Pos] != 10 && d[Pos] != 13) Pos++;
                    continue;
                }
                break;
            }
        }

        public bool AtEnd { get { SkipWhite(); return Pos >= end; } }

        /// <summary>Reads the next object or keyword; returns null at end of data.</summary>
        public object Next()
        {
            SkipWhite();
            if (Pos >= end) return null;
            int c = d[Pos];
            switch (c)
            {
                case '/': return ReadName();
                case '(': return ReadLiteralString();
                case '[':
                    Pos++;
                    return ReadArray();
                case ']':
                    Pos++;
                    return ArrayEnd;
                case '<':
                    if (Pos + 1 < end && d[Pos + 1] == '<')
                    {
                        Pos += 2;
                        return ReadDict();
                    }
                    return ReadHexString();
                case '>':
                    if (Pos + 1 < end && d[Pos + 1] == '>') { Pos += 2; return DictEnd; }
                    Pos++;
                    return Next();
                case '{': Pos++; return new PdfKeyword("{");
                case '}': Pos++; return new PdfKeyword("}");
                case ')': Pos++; return Next();
            }
            if (c == '+' || c == '-' || c == '.' || (c >= '0' && c <= '9')) return ReadNumberOrRef();
            return ReadKeyword();
        }

        public static readonly object ArrayEnd = new object();
        public static readonly object DictEnd = new object();

        object ReadKeyword()
        {
            int s = Pos;
            while (Pos < end && !IsWhite(d[Pos]) && !IsDelim(d[Pos])) Pos++;
            if (Pos == s) { Pos++; return Next(); }
            var w = PdfUtil.Latin1.GetString(d, s, Pos - s);
            switch (w)
            {
                case "true": return true;
                case "false": return false;
                case "null": return PdfNull.Value;
            }
            return new PdfKeyword(w);
        }

        object ReadNumberOrRef()
        {
            var n = ReadNumber();
            if (Owner == null || !(n is double v) || v < 0 || v != Math.Floor(v)) return n;
            // Lookahead for "num gen R"
            int save = Pos;
            SkipWhite();
            int s1 = Pos;
            while (Pos < end && d[Pos] >= '0' && d[Pos] <= '9') Pos++;
            if (Pos > s1 && Pos < end && (IsWhite(d[Pos]) || IsDelim(d[Pos])))
            {
                int gen = ParseInt(s1, Pos);
                SkipWhite();
                if (Pos < end && d[Pos] == 'R' && (Pos + 1 >= end || IsWhite(d[Pos + 1]) || IsDelim(d[Pos + 1])))
                {
                    Pos++;
                    return new PdfRef((int)v, gen);
                }
            }
            Pos = save;
            return n;
        }

        int ParseInt(int s, int e)
        {
            long r = 0;
            for (int i = s; i < e && i - s < 10; i++) r = r * 10 + (d[i] - '0');
            return (int)Math.Min(int.MaxValue, r);
        }

        object ReadNumber()
        {
            int s = Pos;
            if (d[Pos] == '+' || d[Pos] == '-') Pos++;
            while (Pos < end && (d[Pos] == '-' || d[Pos] == '+')) Pos++; // tolerate "--1"
            while (Pos < end && ((d[Pos] >= '0' && d[Pos] <= '9') || d[Pos] == '.')) Pos++;
            var txt = PdfUtil.Latin1.GetString(d, s, Pos - s).Replace("--", "-").Replace("+-", "-");
            if (double.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            if (txt == "-" || txt == "+" || txt == "." || txt == "-.") return 0.0;
            return 0.0;
        }

        PdfName ReadName()
        {
            Pos++;
            var sb = new List<byte>();
            while (Pos < end && !IsWhite(d[Pos]) && !IsDelim(d[Pos]))
            {
                if (d[Pos] == '#' && Pos + 2 < end && Hex(d[Pos + 1]) >= 0 && Hex(d[Pos + 2]) >= 0)
                {
                    sb.Add((byte)(Hex(d[Pos + 1]) * 16 + Hex(d[Pos + 2])));
                    Pos += 3;
                }
                else sb.Add(d[Pos++]);
            }
            var arr = sb.ToArray();
            // Names are byte strings; UTF-8 when valid, Latin-1 otherwise.
            string s;
            try { s = new System.Text.UTF8Encoding(false, true).GetString(arr); }
            catch (ArgumentException) { s = PdfUtil.Latin1.GetString(arr); }
            return new PdfName(s);
        }

        static int Hex(int c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        PdfString ReadLiteralString()
        {
            Pos++;
            var o = new List<byte>();
            int depth = 1;
            while (Pos < end)
            {
                int c = d[Pos++];
                if (c == '(') { depth++; o.Add((byte)c); continue; }
                if (c == ')') { if (--depth == 0) break; o.Add((byte)c); continue; }
                if (c != '\\') { o.Add((byte)c); continue; }
                if (Pos >= end) break;
                c = d[Pos++];
                switch (c)
                {
                    case 'n': o.Add(10); break;
                    case 'r': o.Add(13); break;
                    case 't': o.Add(9); break;
                    case 'b': o.Add(8); break;
                    case 'f': o.Add(12); break;
                    case '(': case ')': case '\\': o.Add((byte)c); break;
                    case 13:
                        if (Pos < end && d[Pos] == 10) Pos++;
                        break;
                    case 10: break;
                    default:
                        if (c >= '0' && c <= '7')
                        {
                            int v = c - '0';
                            for (int k = 0; k < 2 && Pos < end && d[Pos] >= '0' && d[Pos] <= '7'; k++) v = v * 8 + (d[Pos++] - '0');
                            o.Add((byte)v);
                        }
                        else o.Add((byte)c);
                        break;
                }
            }
            return new PdfString(o.ToArray());
        }

        PdfString ReadHexString()
        {
            Pos++;
            var o = new List<byte>();
            int hi = -1;
            while (Pos < end)
            {
                int c = d[Pos++];
                if (c == '>') break;
                int h = Hex(c);
                if (h < 0) continue;
                if (hi < 0) hi = h;
                else { o.Add((byte)(hi * 16 + h)); hi = -1; }
            }
            if (hi >= 0) o.Add((byte)(hi * 16));
            return new PdfString(o.ToArray());
        }

        PdfArray ReadArray()
        {
            var a = new PdfArray { Owner = Owner };
            int guard = 0;
            while (true)
            {
                var o = Next();
                if (o == null || o == ArrayEnd) break;
                if (o == DictEnd) continue;
                if (o is PdfKeyword k && (k.Value == "endobj" || k.Value == "stream")) { Pos -= k.Value.Length; break; }
                a.Add(o);
                if (++guard > 50_000_000) break;
            }
            return a;
        }

        PdfDict ReadDict()
        {
            var dict = new PdfDict { Owner = Owner };
            while (true)
            {
                var k = Next();
                if (k == null || k == DictEnd) break;
                if (k is PdfKeyword kw && (kw.Value == "endobj" || kw.Value == "stream")) { Pos -= kw.Value.Length; break; }
                if (!(k is PdfName name)) continue;
                var v = Next();
                if (v == null || v == DictEnd) { dict.Set(name.Value, PdfNull.Value); break; }
                if (v == ArrayEnd) continue;
                dict.Set(name.Value, v);
            }
            return dict;
        }
    }
}
