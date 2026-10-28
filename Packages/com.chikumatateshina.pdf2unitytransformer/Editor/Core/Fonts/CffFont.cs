using System;
using System.Collections.Generic;
using System.Text;
using PDF2Unity.Core.Graphics;

namespace PDF2Unity.Core.Fonts
{
    /// <summary>Bare CFF font program (Type1C / CIDFontType0C / OpenType CFF table).</summary>
    public sealed class CffFont : IFontProgram
    {
        readonly byte[] d;
        readonly List<(int off, int len)> charStrings;
        readonly List<(int off, int len)> gsubrs;
        readonly List<string> strings;
        readonly int[] charsetSids; // gid -> SID (or CID for CID-keyed fonts)
        readonly Dictionary<int, int> cidToGid = new Dictionary<int, int>();
        readonly Dictionary<string, int> nameToGid = new Dictionary<string, int>();
        readonly List<PrivateDict> fdPrivates = new List<PrivateDict>();
        readonly byte[] fdSelect; // gid -> fd index (CID fonts)
        readonly PrivateDict mainPrivate;
        readonly double[] fontMatrix;
        readonly double unitScale; // used when the CFF lives in OpenType (1/unitsPerEm)
        public readonly bool CidKeyed;

        sealed class PrivateDict
        {
            public List<(int off, int len)> Subrs = new List<(int, int)>();
            public double DefaultWidth, NominalWidth;
        }

        public string Kind => CidKeyed ? "CFF-CID" : "CFF";
        public int? FsType => null;
        public int NumGlyphs => charStrings.Count;
        public string[] BuiltinEncoding { get; }

        public CffFont(byte[] data, double unitScale = 0)
        {
            d = data;
            this.unitScale = unitScale;
            int hdrSize = d[2];
            int p = hdrSize;
            var names = ReadIndex(ref p);
            var topDicts = ReadIndex(ref p);
            var stringIdx = ReadIndex(ref p);
            gsubrs = ReadIndex(ref p);
            strings = new List<string>();
            foreach (var (o, l) in stringIdx) strings.Add(Encoding.ASCII.GetString(d, o, l));
            if (topDicts.Count == 0) throw new InvalidOperationException("CFF without Top DICT");
            var top = ReadDict(topDicts[0].off, topDicts[0].len);
            CidKeyed = top.ContainsKey(1230); // ROS
            fontMatrix = top.TryGetValue(1207, out var fm) && fm.Length >= 6 ? fm : new[] { 0.001, 0, 0, 0.001, 0, 0 };
            int csOff = (int)Get(top, 17, 0);
            int cp = csOff;
            charStrings = ReadIndex(ref cp);
            int n = charStrings.Count;
            if (top.TryGetValue(18, out var priv) && priv.Length >= 2) mainPrivate = ReadPrivate((int)priv[1], (int)priv[0]);
            else mainPrivate = new PrivateDict();
            charsetSids = ReadCharset((int)Get(top, 15, 0), n);
            if (CidKeyed)
            {
                for (int g = 0; g < n; g++) cidToGid[charsetSids[g]] = g;
                int fdaOff = (int)Get(top, 1236, 0);
                if (fdaOff > 0)
                {
                    int fp = fdaOff;
                    foreach (var (o, l) in ReadIndex(ref fp))
                    {
                        var fd = ReadDict(o, l);
                        fdPrivates.Add(fd.TryGetValue(18, out var pr) && pr.Length >= 2 ? ReadPrivate((int)pr[1], (int)pr[0]) : new PrivateDict());
                    }
                }
                int fsOff = (int)Get(top, 1237, 0);
                if (fsOff > 0) fdSelect = ReadFdSelect(fsOff, n);
            }
            else
            {
                for (int g = 0; g < n; g++)
                {
                    var nm = SidString(charsetSids[g]);
                    if (nm != null && !nameToGid.ContainsKey(nm)) nameToGid[nm] = g;
                }
                BuiltinEncoding = ReadEncoding((int)Get(top, 16, 0), n);
            }
        }

        static double Get(Dictionary<int, double[]> dict, int key, double def) =>
            dict.TryGetValue(key, out var v) && v.Length > 0 ? v[v.Length - 1] : def;

        public string GlyphName(int gid) => !CidKeyed && gid >= 0 && gid < charsetSids.Length ? SidString(charsetSids[gid]) : null;

        string SidString(int sid)
        {
            if (sid < FontTables.CffStandardStrings.Length) return FontTables.CffStandardStrings[sid];
            int i = sid - FontTables.CffStandardStrings.Length;
            return i >= 0 && i < strings.Count ? strings[i] : null;
        }

        List<(int off, int len)> ReadIndex(ref int p)
        {
            var r = new List<(int off, int len)>();
            if (p + 2 > d.Length) return r;
            int count = BE.U16(d, p);
            if (count == 0) { p += 2; return r; }
            int offSize = d[p + 2];
            int offs = p + 3;
            int dataStart = offs + (count + 1) * offSize - 1;
            int Off(int i)
            {
                int v = 0;
                for (int k = 0; k < offSize; k++) v = (v << 8) | d[offs + i * offSize + k];
                return v;
            }
            for (int i = 0; i < count; i++)
            {
                int a = Off(i), b = Off(i + 1);
                r.Add((dataStart + a, Math.Max(0, b - a)));
            }
            p = dataStart + Off(count);
            return r;
        }

        Dictionary<int, double[]> ReadDict(int off, int len)
        {
            var dict = new Dictionary<int, double[]>();
            var operands = new List<double>();
            int p = off, end = off + len;
            while (p < end)
            {
                int b0 = d[p];
                if (b0 <= 21)
                {
                    int op = b0;
                    p++;
                    if (b0 == 12) { op = 1200 + d[p]; p++; }
                    dict[op] = operands.ToArray();
                    operands.Clear();
                }
                else if (b0 == 28) { operands.Add((short)((d[p + 1] << 8) | d[p + 2])); p += 3; }
                else if (b0 == 29) { operands.Add((int)BE.U32(d, p + 1)); p += 5; }
                else if (b0 == 30)
                {
                    p++;
                    var sb = new StringBuilder();
                    bool done = false;
                    while (p < end && !done)
                    {
                        int b = d[p++];
                        foreach (int nib in new[] { b >> 4, b & 15 })
                        {
                            if (nib <= 9) sb.Append((char)('0' + nib));
                            else if (nib == 10) sb.Append('.');
                            else if (nib == 11) sb.Append('E');
                            else if (nib == 12) sb.Append("E-");
                            else if (nib == 14) sb.Append('-');
                            else if (nib == 15) { done = true; break; }
                        }
                    }
                    double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v);
                    operands.Add(v);
                }
                else if (b0 >= 32 && b0 <= 246) { operands.Add(b0 - 139); p++; }
                else if (b0 >= 247 && b0 <= 250) { operands.Add((b0 - 247) * 256 + d[p + 1] + 108); p += 2; }
                else if (b0 >= 251 && b0 <= 254) { operands.Add(-(b0 - 251) * 256 - d[p + 1] - 108); p += 2; }
                else p++;
            }
            return dict;
        }

        PrivateDict ReadPrivate(int off, int len)
        {
            var pd = new PrivateDict();
            if (off <= 0 || off + len > d.Length) return pd;
            var dict = ReadDict(off, len);
            pd.DefaultWidth = Get(dict, 20, 0);
            pd.NominalWidth = Get(dict, 21, 0);
            if (dict.TryGetValue(19, out var subrs) && subrs.Length > 0)
            {
                int sp = off + (int)subrs[0];
                pd.Subrs = ReadIndex(ref sp);
            }
            return pd;
        }

        int[] ReadCharset(int off, int n)
        {
            var r = new int[n];
            if (off <= 2)
            {
                // Predefined ISOAdobe (0): gid i -> SID i. Expert charsets are approximated the same way.
                for (int i = 0; i < n; i++) r[i] = i;
                return r;
            }
            int fmt = d[off];
            int p = off + 1, g = 1;
            if (fmt == 0)
                for (; g < n; g++, p += 2) r[g] = BE.U16(d, p);
            else
            {
                while (g < n && p < d.Length)
                {
                    int first = BE.U16(d, p);
                    int left = fmt == 1 ? d[p + 2] : BE.U16(d, p + 2);
                    p += fmt == 1 ? 3 : 4;
                    for (int k = 0; k <= left && g < n; k++) r[g++] = first + k;
                }
            }
            return r;
        }

        byte[] ReadFdSelect(int off, int n)
        {
            var r = new byte[n];
            int fmt = d[off];
            if (fmt == 0)
            {
                for (int i = 0; i < n; i++) r[i] = d[off + 1 + i];
            }
            else if (fmt == 3)
            {
                int nr = BE.U16(d, off + 1);
                int p = off + 3;
                for (int i = 0; i < nr; i++)
                {
                    int first = BE.U16(d, p), fd = d[p + 2], next = BE.U16(d, p + 3);
                    for (int g = first; g < next && g < n; g++) r[g] = (byte)fd;
                    p += 3;
                }
            }
            return r;
        }

        string[] ReadEncoding(int off, int n)
        {
            var enc = new string[256];
            if (off == 0)
            {
                Array.Copy(FontTables.StandardEncoding, enc, 256);
                return enc;
            }
            if (off == 1) return null; // Expert encoding: rare, fall back to names
            int fmt = d[off] & 0x7F;
            bool supp = (d[off] & 0x80) != 0;
            int p = off + 1;
            if (fmt == 0)
            {
                int nc = d[p++];
                for (int i = 1; i <= nc && i < n; i++) enc[d[p++]] = GlyphName(i);
            }
            else if (fmt == 1)
            {
                int nr = d[p++];
                int g = 1;
                for (int i = 0; i < nr; i++)
                {
                    int first = d[p], left = d[p + 1];
                    p += 2;
                    for (int k = 0; k <= left && g < n; k++, g++) if (first + k < 256) enc[first + k] = GlyphName(g);
                }
            }
            if (supp)
            {
                int ns = d[p++];
                for (int i = 0; i < ns; i++)
                {
                    int code = d[p], sid = BE.U16(d, p + 1);
                    p += 3;
                    enc[code] = SidString(sid);
                }
            }
            return enc;
        }

        public double? AdvanceByGid(int gid) => null;

        public List<PathCmd> OutlineByName(string name) =>
            name != null && nameToGid.TryGetValue(name, out var g) ? OutlineByGid(g) : null;

        public List<PathCmd> OutlineByCid(int cid)
        {
            if (!CidKeyed) return OutlineByGid(cid);
            return cidToGid.TryGetValue(cid, out var g) ? OutlineByGid(g) : null;
        }

        public List<PathCmd> OutlineByGid(int gid)
        {
            if (gid < 0 || gid >= charStrings.Count) return null;
            double sx, sy, tx, ty;
            if (unitScale > 0) { sx = sy = unitScale; tx = ty = 0; }
            else { sx = fontMatrix[0]; sy = fontMatrix[3]; tx = fontMatrix[4]; ty = fontMatrix[5]; }
            var sink = new PathSink(sx, sy, tx, ty);
            var pd = mainPrivate;
            if (CidKeyed && fdSelect != null && fdPrivates.Count > 0) pd = fdPrivates[Math.Min(fdSelect[gid], fdPrivates.Count - 1)];
            new Type2Interpreter(this, pd, sink).Run(charStrings[gid]);
            sink.Finish();
            return sink.Cmds;
        }

        int GidForStandardCode(int code)
        {
            var nm = code >= 0 && code < 256 ? FontTables.StandardEncoding[code] : null;
            return nm != null && nameToGid.TryGetValue(nm, out var g) ? g : -1;
        }

        static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

        /// <summary>Type 2 charstring interpreter.</summary>
        sealed class Type2Interpreter
        {
            readonly CffFont f;
            readonly PrivateDict pd;
            readonly PathSink sink;
            readonly double[] st = new double[64];
            int sp;
            int nStems;
            bool haveWidth;
            double x, y;
            int depth;
            readonly double[] transient = new double[32];
            bool done;

            public Type2Interpreter(CffFont font, PrivateDict p, PathSink s)
            {
                f = font;
                pd = p;
                sink = s;
            }

            void Push(double v) { if (sp < st.Length) st[sp++] = v; }

            void ClearWidth(int expectEven)
            {
                // The first stack-clearing operator may carry the advance width as an extra first argument.
                if (!haveWidth)
                {
                    haveWidth = true;
                    if (expectEven >= 0 && sp % 2 != expectEven % 2 && sp > 0) Shift();
                }
            }

            void Shift()
            {
                for (int i = 1; i < sp; i++) st[i - 1] = st[i];
                sp--;
            }

            void MoveTo(double dx, double dy)
            {
                x += dx; y += dy;
                sink.MoveTo(x, y);
            }

            void LineTo(double dx, double dy)
            {
                x += dx; y += dy;
                sink.LineTo(x, y);
            }

            void CurveTo(double a, double b, double c, double dd, double e, double ff)
            {
                double x1 = x + a, y1 = y + b, x2 = x1 + c, y2 = y1 + dd;
                x = x2 + e; y = y2 + ff;
                sink.CurveTo(x1, y1, x2, y2, x, y);
            }

            public void Run((int off, int len) cs)
            {
                if (done || ++depth > 12) return;
                var d = f.d;
                int p = cs.off, end = cs.off + cs.len;
                while (p < end && !done)
                {
                    int b0 = d[p++];
                    if (b0 >= 32 || b0 == 28)
                    {
                        if (b0 == 28) { Push((short)((d[p] << 8) | d[p + 1])); p += 2; }
                        else if (b0 <= 246) Push(b0 - 139);
                        else if (b0 <= 250) { Push((b0 - 247) * 256 + d[p++] + 108); }
                        else if (b0 <= 254) { Push(-(b0 - 251) * 256 - d[p++] - 108); }
                        else { Push((int)BE.U32(d, p) / 65536.0); p += 4; }
                        continue;
                    }
                    switch (b0)
                    {
                        case 1: case 3: case 18: case 23: // hstem vstem hstemhm vstemhm
                            ClearWidth(0);
                            nStems += sp / 2;
                            sp = 0;
                            break;
                        case 19: case 20: // hintmask cntrmask
                            ClearWidth(0);
                            nStems += sp / 2;
                            sp = 0;
                            p += (nStems + 7) / 8;
                            break;
                        case 21: // rmoveto
                            ClearWidth(0);
                            MoveTo(sp > 1 ? st[sp - 2] : 0, sp > 0 ? st[sp - 1] : 0);
                            sp = 0;
                            break;
                        case 22: // hmoveto
                            ClearWidth(1);
                            MoveTo(sp > 0 ? st[sp - 1] : 0, 0);
                            sp = 0;
                            break;
                        case 4: // vmoveto
                            ClearWidth(1);
                            MoveTo(0, sp > 0 ? st[sp - 1] : 0);
                            sp = 0;
                            break;
                        case 5: // rlineto
                            for (int i = 0; i + 1 < sp; i += 2) LineTo(st[i], st[i + 1]);
                            sp = 0;
                            break;
                        case 6: case 7: // hlineto vlineto
                            {
                                bool h = b0 == 6;
                                for (int i = 0; i < sp; i++, h = !h) LineTo(h ? st[i] : 0, h ? 0 : st[i]);
                                sp = 0;
                            }
                            break;
                        case 8: // rrcurveto
                            for (int i = 0; i + 5 < sp; i += 6) CurveTo(st[i], st[i + 1], st[i + 2], st[i + 3], st[i + 4], st[i + 5]);
                            sp = 0;
                            break;
                        case 24: // rcurveline
                            {
                                int i = 0;
                                for (; i + 5 < sp - 2; i += 6) CurveTo(st[i], st[i + 1], st[i + 2], st[i + 3], st[i + 4], st[i + 5]);
                                if (i + 1 < sp) LineTo(st[i], st[i + 1]);
                                sp = 0;
                            }
                            break;
                        case 25: // rlinecurve
                            {
                                int i = 0;
                                for (; i + 1 < sp - 6; i += 2) LineTo(st[i], st[i + 1]);
                                if (i + 5 < sp) CurveTo(st[i], st[i + 1], st[i + 2], st[i + 3], st[i + 4], st[i + 5]);
                                sp = 0;
                            }
                            break;
                        case 26: // vvcurveto
                            {
                                int i = 0;
                                double dx1 = 0;
                                if (sp % 4 == 1) dx1 = st[i++];
                                for (; i + 3 < sp; i += 4) { CurveTo(dx1, st[i], st[i + 1], st[i + 2], 0, st[i + 3]); dx1 = 0; }
                                sp = 0;
                            }
                            break;
                        case 27: // hhcurveto
                            {
                                int i = 0;
                                double dy1 = 0;
                                if (sp % 4 == 1) dy1 = st[i++];
                                for (; i + 3 < sp; i += 4) { CurveTo(st[i], dy1, st[i + 1], st[i + 2], st[i + 3], 0); dy1 = 0; }
                                sp = 0;
                            }
                            break;
                        case 30: case 31: // vhcurveto hvcurveto
                            {
                                bool h = b0 == 31;
                                int i = 0;
                                while (i + 3 < sp)
                                {
                                    bool last = i + 4 >= sp - 1;
                                    double extra = last && i + 4 < sp ? st[i + 4] : 0;
                                    if (h) CurveTo(st[i], 0, st[i + 1], st[i + 2], extra, st[i + 3]);
                                    else CurveTo(0, st[i], st[i + 1], st[i + 2], st[i + 3], extra);
                                    i += 4;
                                    h = !h;
                                }
                                sp = 0;
                            }
                            break;
                        case 10: // callsubr
                            {
                                if (sp == 0) break;
                                int idx = (int)st[--sp] + Bias(pd.Subrs.Count);
                                if (idx >= 0 && idx < pd.Subrs.Count) Run(pd.Subrs[idx]);
                            }
                            break;
                        case 29: // callgsubr
                            {
                                if (sp == 0) break;
                                int idx = (int)st[--sp] + Bias(f.gsubrs.Count);
                                if (idx >= 0 && idx < f.gsubrs.Count) Run(f.gsubrs[idx]);
                            }
                            break;
                        case 11: // return
                            depth--;
                            return;
                        case 14: // endchar
                            ClearWidth(0);
                            if (sp >= 4)
                            {
                                // seac: adx ady bchar achar
                                double adx = st[sp - 4], ady = st[sp - 3];
                                int bchar = (int)st[sp - 2], achar = (int)st[sp - 1];
                                int bg = f.GidForStandardCode(bchar), ag = f.GidForStandardCode(achar);
                                sp = 0;
                                if (bg >= 0) new Type2Interpreter(f, pd, sink).Run(f.charStrings[bg]);
                                if (ag >= 0)
                                {
                                    var accent = new Type2Interpreter(f, pd, sink) { x = adx, y = ady };
                                    accent.Run(f.charStrings[ag]);
                                }
                            }
                            sink.ClosePath();
                            done = true;
                            return;
                        case 12:
                            Escape(d[p++]);
                            break;
                        default:
                            sp = 0;
                            break;
                    }
                }
                depth--;
            }

            void Escape(int op)
            {
                switch (op)
                {
                    case 35: // flex
                        if (sp >= 12)
                        {
                            CurveTo(st[0], st[1], st[2], st[3], st[4], st[5]);
                            CurveTo(st[6], st[7], st[8], st[9], st[10], st[11]);
                        }
                        sp = 0;
                        break;
                    case 34: // hflex
                        if (sp >= 7)
                        {
                            CurveTo(st[0], 0, st[1], st[2], st[3], 0);
                            CurveTo(st[4], 0, st[5], -st[2], st[6], 0);
                        }
                        sp = 0;
                        break;
                    case 36: // hflex1
                        if (sp >= 9)
                        {
                            double y0 = y;
                            CurveTo(st[0], st[1], st[2], st[3], st[4], 0);
                            CurveTo(st[5], 0, st[6], st[7], st[8], y0 - (y + st[7]));
                        }
                        sp = 0;
                        break;
                    case 37: // flex1
                        if (sp >= 11)
                        {
                            double x0 = x, y0 = y;
                            double dx = st[0] + st[2] + st[4] + st[6] + st[8];
                            double dy = st[1] + st[3] + st[5] + st[7] + st[9];
                            CurveTo(st[0], st[1], st[2], st[3], st[4], st[5]);
                            if (Math.Abs(dx) > Math.Abs(dy)) CurveTo(st[6], st[7], st[8], st[9], st[10], y0 - (y + st[7] + st[9]));
                            else CurveTo(st[6], st[7], st[8], st[9], x0 - (x + st[6] + st[8]), st[10]);
                        }
                        sp = 0;
                        break;
                    // arithmetic (rarely used)
                    case 3: if (sp >= 2) { st[sp - 2] = (st[sp - 2] != 0 && st[sp - 1] != 0) ? 1 : 0; sp--; } break;
                    case 4: if (sp >= 2) { st[sp - 2] = (st[sp - 2] != 0 || st[sp - 1] != 0) ? 1 : 0; sp--; } break;
                    case 5: if (sp >= 1) st[sp - 1] = st[sp - 1] == 0 ? 1 : 0; break;
                    case 9: if (sp >= 1) st[sp - 1] = Math.Abs(st[sp - 1]); break;
                    case 10: if (sp >= 2) { st[sp - 2] += st[sp - 1]; sp--; } break;
                    case 11: if (sp >= 2) { st[sp - 2] -= st[sp - 1]; sp--; } break;
                    case 12: if (sp >= 2) { st[sp - 2] = st[sp - 1] != 0 ? st[sp - 2] / st[sp - 1] : 0; sp--; } break;
                    case 14: if (sp >= 1) st[sp - 1] = -st[sp - 1]; break;
                    case 15: if (sp >= 2) { st[sp - 2] = st[sp - 2] == st[sp - 1] ? 1 : 0; sp--; } break;
                    case 18: if (sp >= 1) sp--; break;
                    case 20: if (sp >= 2) { int i = (int)st[sp - 1]; if (i >= 0 && i < 32) transient[i] = st[sp - 2]; sp -= 2; } break;
                    case 21: if (sp >= 1) { int i = (int)st[sp - 1]; st[sp - 1] = i >= 0 && i < 32 ? transient[i] : 0; } break;
                    case 22: if (sp >= 4) { var r = st[sp - 2] <= st[sp - 1] ? st[sp - 4] : st[sp - 3]; sp -= 3; st[sp - 1] = r; } break;
                    case 23: Push(0.5); break;
                    case 24: if (sp >= 2) { st[sp - 2] *= st[sp - 1]; sp--; } break;
                    case 26: if (sp >= 1) st[sp - 1] = Math.Sqrt(Math.Max(0, st[sp - 1])); break;
                    case 27: if (sp >= 1) Push(st[sp - 1]); break;
                    case 28: if (sp >= 2) { var t = st[sp - 1]; st[sp - 1] = st[sp - 2]; st[sp - 2] = t; } break;
                    case 29: if (sp >= 1) { int i = (int)st[sp - 1]; st[sp - 1] = i >= 0 && i < sp - 1 ? st[sp - 2 - i] : 0; } break;
                    case 30:
                        if (sp >= 2)
                        {
                            int j = (int)st[sp - 1], n = (int)st[sp - 2];
                            sp -= 2;
                            if (n > 0 && n <= sp)
                            {
                                j = ((j % n) + n) % n;
                                var seg = new double[n];
                                Array.Copy(st, sp - n, seg, 0, n);
                                for (int i = 0; i < n; i++) st[sp - n + (i + j) % n] = seg[i];
                            }
                        }
                        break;
                    default:
                        sp = 0;
                        break;
                }
            }
        }
    }
}
