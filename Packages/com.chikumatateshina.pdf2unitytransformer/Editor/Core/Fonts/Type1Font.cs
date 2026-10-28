using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PDF2Unity.Core.Graphics;

namespace PDF2Unity.Core.Fonts
{
    /// <summary>Type 1 font program (FontFile): eexec-encrypted charstrings.</summary>
    public sealed class Type1Font : IFontProgram
    {
        readonly Dictionary<string, byte[]> charStrings = new Dictionary<string, byte[]>();
        readonly List<byte[]> subrs = new List<byte[]>();
        readonly double[] fontMatrix = { 0.001, 0, 0, 0.001, 0, 0 };

        public string Kind => "Type1";
        public int? FsType => null;
        public int NumGlyphs => charStrings.Count;
        public string[] BuiltinEncoding { get; }

        public Type1Font(byte[] data)
        {
            int eexec = IndexOf(data, "eexec");
            string clear = Encoding.GetEncoding(28591).GetString(data, 0, eexec > 0 ? eexec : data.Length);
            var fm = Regex.Match(clear, @"/FontMatrix\s*\[([^\]]*)\]");
            if (fm.Success)
            {
                var parts = fm.Groups[1].Value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 6)
                    for (int i = 0; i < 6; i++) double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out fontMatrix[i]);
            }
            if (Regex.IsMatch(clear, @"/Encoding\s+StandardEncoding")) BuiltinEncoding = (string[])FontTables.StandardEncoding.Clone();
            else
            {
                var enc = new string[256];
                bool any = false;
                foreach (Match m in Regex.Matches(clear, @"dup\s+(\d+)\s*/([^\s/\[\]{}()<>]+)\s+put"))
                {
                    int code = int.Parse(m.Groups[1].Value);
                    if (code < 256) { enc[code] = m.Groups[2].Value; any = true; }
                }
                if (any) BuiltinEncoding = enc;
            }
            if (eexec < 0) return;
            int p = eexec + 5;
            while (p < data.Length && (data[p] == 13 || data[p] == 10 || data[p] == 32 || data[p] == 9)) p++;
            var enc2 = new byte[data.Length - p];
            Buffer.BlockCopy(data, p, enc2, 0, enc2.Length);
            if (IsHex(enc2)) enc2 = Unhex(enc2);
            var plain = Decrypt(enc2, 55665, 4);
            ParsePrivate(plain);
        }

        static int IndexOf(byte[] d, string s)
        {
            for (int i = 0; i <= d.Length - s.Length; i++)
            {
                int k = 0;
                while (k < s.Length && d[i + k] == s[k]) k++;
                if (k == s.Length) return i;
            }
            return -1;
        }

        static bool IsHex(byte[] d)
        {
            for (int i = 0; i < Math.Min(4, d.Length); i++)
            {
                int c = d[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            }
            return true;
        }

        static byte[] Unhex(byte[] d)
        {
            var o = new List<byte>(d.Length / 2);
            int hi = -1;
            foreach (var c in d)
            {
                int h = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                if (h < 0) continue;
                if (hi < 0) hi = h;
                else { o.Add((byte)(hi * 16 + h)); hi = -1; }
            }
            return o.ToArray();
        }

        static byte[] Decrypt(byte[] data, int r, int skip)
        {
            const int c1 = 52845, c2 = 22719;
            var o = new byte[Math.Max(0, data.Length - skip)];
            for (int i = 0; i < data.Length; i++)
            {
                int c = data[i];
                int pl = c ^ (r >> 8);
                r = ((c + r) * c1 + c2) & 0xFFFF;
                if (i >= skip) o[i - skip] = (byte)pl;
            }
            return o;
        }

        void ParsePrivate(byte[] pl)
        {
            var text = Encoding.GetEncoding(28591).GetString(pl);
            int lenIV = 4;
            var lm = Regex.Match(text, @"/lenIV\s+(\d+)");
            if (lm.Success) lenIV = int.Parse(lm.Groups[1].Value);
            // Subrs: dup <i> <n> RD <binary> NP
            int sIdx = text.IndexOf("/Subrs", StringComparison.Ordinal);
            int csIdx = text.IndexOf("/CharStrings", StringComparison.Ordinal);
            if (sIdx >= 0)
            {
                var rx = new Regex(@"dup\s+(\d+)\s+(\d+)\s+(RD|-\|)\s");
                int pos = sIdx;
                while (true)
                {
                    var m = rx.Match(text, pos);
                    if (!m.Success || (csIdx > 0 && m.Index > csIdx)) break;
                    int i = int.Parse(m.Groups[1].Value), n = int.Parse(m.Groups[2].Value);
                    int start = m.Index + m.Length;
                    if (start + n > pl.Length) break;
                    var bin = new byte[n];
                    Buffer.BlockCopy(pl, start, bin, 0, n);
                    while (subrs.Count <= i) subrs.Add(null);
                    subrs[i] = Decrypt(bin, 4330, lenIV);
                    pos = start + n;
                }
            }
            if (csIdx >= 0)
            {
                var rx = new Regex(@"/([^\s/\[\]{}()<>]+)\s+(\d+)\s+(RD|-\|)\s");
                int pos = csIdx + 12;
                while (true)
                {
                    var m = rx.Match(text, pos);
                    if (!m.Success) break;
                    int n = int.Parse(m.Groups[2].Value);
                    int start = m.Index + m.Length;
                    if (start + n > pl.Length) break;
                    var bin = new byte[n];
                    Buffer.BlockCopy(pl, start, bin, 0, n);
                    charStrings[m.Groups[1].Value] = Decrypt(bin, 4330, lenIV);
                    pos = start + n;
                }
            }
        }

        public double? AdvanceByGid(int gid) => null;
        public List<PathCmd> OutlineByGid(int gid) => null;
        public List<PathCmd> OutlineByCid(int cid) => null;

        public List<PathCmd> OutlineByName(string name)
        {
            if (name == null || !charStrings.TryGetValue(name, out var cs)) return null;
            var sink = new PathSink(fontMatrix[0], fontMatrix[3], fontMatrix[4], fontMatrix[5]);
            new Type1Interpreter(this, sink).Run(cs, 0);
            sink.Finish();
            return sink.Cmds;
        }

        sealed class Type1Interpreter
        {
            readonly Type1Font f;
            readonly PathSink sink;
            readonly List<double> st = new List<double>();
            readonly List<double> ps = new List<double>(); // PostScript stack (callothersubr results)
            double x, y, sbx;
            bool flex;
            readonly List<(double, double)> flexPts = new List<(double, double)>();
            bool done;

            public Type1Interpreter(Type1Font font, PathSink s)
            {
                f = font;
                sink = s;
            }

            double Arg(int i) => i < st.Count ? st[i] : 0;

            void RMove(double dx, double dy)
            {
                x += dx; y += dy;
                if (flex) { flexPts.Add((x, y)); return; }
                sink.MoveTo(x, y);
            }

            public void Run(byte[] cs, int depth)
            {
                if (cs == null || depth > 12 || done) return;
                int p = 0;
                while (p < cs.Length && !done)
                {
                    int v = cs[p++];
                    if (v >= 32)
                    {
                        if (v <= 246) st.Add(v - 139);
                        else if (v <= 250) st.Add((v - 247) * 256 + cs[p++] + 108);
                        else if (v <= 254) st.Add(-(v - 251) * 256 - cs[p++] - 108);
                        else { st.Add((cs[p] << 24) | (cs[p + 1] << 16) | (cs[p + 2] << 8) | cs[p + 3]); p += 4; }
                        continue;
                    }
                    switch (v)
                    {
                        case 13: // hsbw
                            sbx = Arg(0);
                            x = sbx; y = 0;
                            st.Clear();
                            break;
                        case 21: RMove(Arg(0), Arg(1)); st.Clear(); break;
                        case 22: RMove(Arg(0), 0); st.Clear(); break;
                        case 4: RMove(0, Arg(0)); st.Clear(); break;
                        case 5: x += Arg(0); y += Arg(1); sink.LineTo(x, y); st.Clear(); break;
                        case 6: x += Arg(0); sink.LineTo(x, y); st.Clear(); break;
                        case 7: y += Arg(0); sink.LineTo(x, y); st.Clear(); break;
                        case 8: Curve(Arg(0), Arg(1), Arg(2), Arg(3), Arg(4), Arg(5)); st.Clear(); break;
                        case 30: Curve(0, Arg(0), Arg(1), Arg(2), Arg(3), 0); st.Clear(); break; // vhcurveto
                        case 31: Curve(Arg(0), 0, Arg(1), Arg(2), 0, Arg(3)); st.Clear(); break; // hvcurveto
                        case 9: sink.ClosePath(); st.Clear(); break;
                        case 10:
                            {
                                int idx = st.Count > 0 ? (int)st[st.Count - 1] : -1;
                                if (st.Count > 0) st.RemoveAt(st.Count - 1);
                                if (idx >= 0 && idx < f.subrs.Count) Run(f.subrs[idx], depth + 1);
                            }
                            break;
                        case 11: return;
                        case 14: sink.ClosePath(); done = true; return;
                        case 1: case 3: st.Clear(); break; // hstem vstem
                        case 12:
                            Escape(cs[p++], depth);
                            break;
                        default: st.Clear(); break;
                    }
                }
            }

            void Curve(double a, double b, double c, double d, double e, double g)
            {
                double x1 = x + a, y1 = y + b, x2 = x1 + c, y2 = y1 + d;
                x = x2 + e; y = y2 + g;
                sink.CurveTo(x1, y1, x2, y2, x, y);
            }

            void Escape(int op, int depth)
            {
                switch (op)
                {
                    case 7: // sbw
                        sbx = Arg(0);
                        x = sbx; y = Arg(1);
                        st.Clear();
                        break;
                    case 6: // seac asb adx ady bchar achar
                        {
                            double asb = Arg(0), adx = Arg(1), ady = Arg(2);
                            int bchar = (int)Arg(3), achar = (int)Arg(4);
                            st.Clear();
                            var bn = bchar >= 0 && bchar < 256 ? FontTables.StandardEncoding[bchar] : null;
                            var an = achar >= 0 && achar < 256 ? FontTables.StandardEncoding[achar] : null;
                            if (bn != null && f.charStrings.TryGetValue(bn, out var bcs)) new Type1Interpreter(f, sink).Run(bcs, depth + 1);
                            if (an != null && f.charStrings.TryGetValue(an, out var acs))
                            {
                                var ai = new Type1Interpreter(f, sink);
                                ai.Run(acs, depth + 1, sbx - asb + adx, ady);
                            }
                            done = true;
                        }
                        break;
                    case 12: // div
                        if (st.Count >= 2)
                        {
                            double b = st[st.Count - 1], a = st[st.Count - 2];
                            st.RemoveRange(st.Count - 2, 2);
                            st.Add(b != 0 ? a / b : 0);
                        }
                        break;
                    case 16: // callothersubr
                        {
                            int other = st.Count > 0 ? (int)st[st.Count - 1] : -1;
                            int n = st.Count > 1 ? (int)st[st.Count - 2] : 0;
                            if (st.Count >= 2) st.RemoveRange(st.Count - 2, 2);
                            var args = new List<double>();
                            for (int i = 0; i < n && st.Count > 0; i++) { args.Add(st[st.Count - 1]); st.RemoveAt(st.Count - 1); }
                            if (other == 1) { flex = true; flexPts.Clear(); }
                            else if (other == 0 && flex)
                            {
                                flex = false;
                                // 7 points: reference + 6 curve points
                                if (flexPts.Count >= 7)
                                {
                                    var q = flexPts;
                                    sink.CurveTo(q[1].Item1, q[1].Item2, q[2].Item1, q[2].Item2, q[3].Item1, q[3].Item2);
                                    sink.CurveTo(q[4].Item1, q[4].Item2, q[5].Item1, q[5].Item2, q[6].Item1, q[6].Item2);
                                    x = q[6].Item1; y = q[6].Item2;
                                }
                                // flex end leaves x y on the PS stack for two "pop"s followed by setcurrentpoint
                                ps.Clear();
                                ps.Add(y); ps.Add(x);
                                break;
                            }
                            else if (other == 3) { ps.Clear(); ps.Add(3); break; }
                            ps.Clear();
                            ps.AddRange(args);
                        }
                        break;
                    case 17: // pop
                        if (ps.Count > 0) { st.Add(ps[ps.Count - 1]); ps.RemoveAt(ps.Count - 1); }
                        else st.Add(0);
                        break;
                    case 33: // setcurrentpoint
                        if (st.Count >= 2) { x = st[st.Count - 2]; y = st[st.Count - 1]; }
                        st.Clear();
                        break;
                    default: // dotsection, vstem3, hstem3, ...
                        st.Clear();
                        break;
                }
            }

            public void Run(byte[] cs, int depth, double ox, double oy)
            {
                // Accent of seac: hsbw inside the accent resets x to its sidebearing; offset afterwards.
                var sub = new PathSink(1, 1);
                var inner = new Type1Interpreter(f, sub);
                inner.Run(cs, depth);
                sub.Finish();
                foreach (var c in sub.Cmds)
                {
                    var t = c;
                    t.X1 += ox; t.Y1 += oy; t.X2 += ox; t.Y2 += oy; t.X3 += ox; t.Y3 += oy;
                    switch (t.Op)
                    {
                        case PathOp.Move: sink.MoveTo(t.X3, t.Y3); break;
                        case PathOp.Line: sink.LineTo(t.X3, t.Y3); break;
                        case PathOp.Cubic: sink.CurveTo(t.X1, t.Y1, t.X2, t.Y2, t.X3, t.Y3); break;
                        case PathOp.Close: sink.ClosePath(); break;
                    }
                }
            }
        }
    }
}
