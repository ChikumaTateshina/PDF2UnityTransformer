using System;
using System.Collections.Generic;
using System.Text;
using PDF2Unity.Core.Graphics;

namespace PDF2Unity.Core.Fonts
{
    /// <summary>Glyph outlines from a font program, in text space for a font size of 1.</summary>
    public interface IFontProgram
    {
        string Kind { get; }
        int? FsType { get; }
        int NumGlyphs { get; }
        List<PathCmd> OutlineByGid(int gid);
        List<PathCmd> OutlineByName(string name);
        List<PathCmd> OutlineByCid(int cid);
        double? AdvanceByGid(int gid);
        string[] BuiltinEncoding { get; }
    }

    static class BE
    {
        public static int U16(byte[] d, int o) => o + 1 < d.Length && o >= 0 ? (d[o] << 8) | d[o + 1] : 0;
        public static short S16(byte[] d, int o) => (short)U16(d, o);
        public static uint U32(byte[] d, int o) => o + 3 < d.Length && o >= 0 ? (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]) : 0;
    }

    /// <summary>TrueType / OpenType font program (glyf or CFF outlines).</summary>
    public sealed class TrueTypeFont : IFontProgram
    {
        readonly byte[] d;
        readonly Dictionary<string, (int off, int len)> tables = new Dictionary<string, (int, int)>();
        readonly int upem, numGlyphs, locaFormat, numHMetrics;
        readonly int[] loca;
        readonly CffFont cff;
        public readonly Dictionary<(int, int), Dictionary<int, int>> Cmaps = new Dictionary<(int, int), Dictionary<int, int>>();
        readonly Dictionary<string, int> nameToGid = new Dictionary<string, int>();
        readonly string[] gidNames;

        public string Kind => cff != null ? "OpenType-CFF" : "TrueType";
        public int? FsType { get; }
        public int NumGlyphs => numGlyphs;
        public int UnitsPerEm => upem;
        public string[] BuiltinEncoding => null;
        public bool IsCff => cff != null;

        public TrueTypeFont(byte[] data)
        {
            d = data;
            int baseOff = 0;
            if (Encoding.ASCII.GetString(d, 0, Math.Min(4, d.Length)) == "ttcf") baseOff = (int)BE.U32(d, 12);
            int n = BE.U16(d, baseOff + 4);
            for (int i = 0; i < n; i++)
            {
                int r = baseOff + 12 + 16 * i;
                if (r + 16 > d.Length) break;
                var tag = Encoding.ASCII.GetString(d, r, 4);
                int off = (int)BE.U32(d, r + 8), len = (int)BE.U32(d, r + 12);
                if (off >= 0 && off < d.Length) tables[tag] = (off, Math.Min(len, d.Length - off));
            }
            upem = tables.TryGetValue("head", out var head) ? BE.U16(d, head.off + 18) : 1000;
            if (upem <= 0) upem = 1000;
            locaFormat = tables.ContainsKey("head") ? BE.S16(d, head.off + 50) : 0;
            numGlyphs = tables.TryGetValue("maxp", out var maxp) ? BE.U16(d, maxp.off + 4) : 0;
            numHMetrics = tables.TryGetValue("hhea", out var hhea) ? BE.U16(d, hhea.off + 34) : 0;
            if (tables.TryGetValue("OS/2", out var os2) && os2.len >= 10) FsType = BE.U16(d, os2.off + 8);
            if (tables.TryGetValue("CFF ", out var cffT))
            {
                var bytes = new byte[cffT.len];
                Buffer.BlockCopy(d, cffT.off, bytes, 0, cffT.len);
                cff = new CffFont(bytes, 1.0 / upem);
                if (numGlyphs == 0) numGlyphs = cff.NumGlyphs;
            }
            else if (tables.TryGetValue("loca", out var lt) && numGlyphs > 0)
            {
                loca = new int[numGlyphs + 1];
                for (int i = 0; i <= numGlyphs; i++)
                    loca[i] = locaFormat == 0 ? BE.U16(d, lt.off + 2 * i) * 2 : (int)BE.U32(d, lt.off + 4 * i);
            }
            ReadCmap();
            gidNames = ReadPost();
            if (gidNames != null)
                for (int i = 0; i < gidNames.Length; i++)
                    if (gidNames[i] != null && !nameToGid.ContainsKey(gidNames[i])) nameToGid[gidNames[i]] = i;
            if (cff != null)
                for (int i = 0; i < cff.NumGlyphs; i++)
                {
                    var nm = cff.GlyphName(i);
                    if (nm != null && !nameToGid.ContainsKey(nm)) nameToGid[nm] = i;
                }
        }

        public int? GidForName(string name) => name != null && nameToGid.TryGetValue(name, out var g) ? g : (int?)null;

        public int? GidForUnicode(int cp)
        {
            foreach (var key in new[] { (3, 10), (3, 1), (0, 4), (0, 3), (0, 6), (0, 1), (0, 0) })
                if (Cmaps.TryGetValue(key, out var m) && m.TryGetValue(cp, out var g)) return g;
            return null;
        }

        void ReadCmap()
        {
            if (!tables.TryGetValue("cmap", out var t)) return;
            int n = BE.U16(d, t.off + 2);
            for (int i = 0; i < n; i++)
            {
                int r = t.off + 4 + 8 * i;
                int pid = BE.U16(d, r), eid = BE.U16(d, r + 2);
                int off = t.off + (int)BE.U32(d, r + 4);
                if (Cmaps.ContainsKey((pid, eid))) continue;
                try
                {
                    var m = ReadCmapSubtable(off);
                    if (m != null) Cmaps[(pid, eid)] = m;
                }
                catch (Exception) { }
            }
        }

        Dictionary<int, int> ReadCmapSubtable(int off)
        {
            int format = BE.U16(d, off);
            var m = new Dictionary<int, int>();
            switch (format)
            {
                case 0:
                    for (int c = 0; c < 256; c++) if (off + 6 + c < d.Length) m[c] = d[off + 6 + c];
                    return m;
                case 4:
                    {
                        int segX2 = BE.U16(d, off + 6);
                        int ends = off + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
                        for (int s = 0; s < segX2 / 2; s++)
                        {
                            int end = BE.U16(d, ends + 2 * s), start = BE.U16(d, starts + 2 * s);
                            int delta = BE.S16(d, deltas + 2 * s), ro = BE.U16(d, ranges + 2 * s);
                            if (start > end) continue;
                            for (int c = start; c <= end && c != 0xFFFF; c++)
                            {
                                int g;
                                if (ro == 0) g = (c + delta) & 0xFFFF;
                                else
                                {
                                    int gi = ranges + 2 * s + ro + 2 * (c - start);
                                    g = BE.U16(d, gi);
                                    if (g != 0) g = (g + delta) & 0xFFFF;
                                }
                                if (g != 0) m[c] = g;
                            }
                        }
                        return m;
                    }
                case 6:
                    {
                        int first = BE.U16(d, off + 6), count = BE.U16(d, off + 8);
                        for (int i = 0; i < count; i++) m[first + i] = BE.U16(d, off + 10 + 2 * i);
                        return m;
                    }
                case 12:
                    {
                        uint groups = BE.U32(d, off + 12);
                        for (uint gIdx = 0; gIdx < groups && gIdx < 100000; gIdx++)
                        {
                            int r = off + 16 + (int)gIdx * 12;
                            uint s = BE.U32(d, r), e = BE.U32(d, r + 4), g0 = BE.U32(d, r + 8);
                            for (uint c = s; c <= e && c - s < 0x110000; c++) m[(int)c] = (int)(g0 + c - s);
                        }
                        return m;
                    }
            }
            return null;
        }

        string[] ReadPost()
        {
            if (!tables.TryGetValue("post", out var t) || t.len < 32) return null;
            uint ver = BE.U32(d, t.off);
            if (ver == 0x00010000)
            {
                var r = new string[Math.Min(numGlyphs, 258)];
                for (int i = 0; i < r.Length; i++) r[i] = FontTables.MacGlyphOrder[i];
                return r;
            }
            if (ver != 0x00020000) return null;
            int n = BE.U16(d, t.off + 32);
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = BE.U16(d, t.off + 34 + 2 * i);
            var extra = new List<string>();
            int p = t.off + 34 + 2 * n, end = t.off + t.len;
            while (p < end)
            {
                int len = d[p];
                if (p + 1 + len > d.Length) break;
                extra.Add(Encoding.ASCII.GetString(d, p + 1, len));
                p += 1 + len;
            }
            var names = new string[n];
            for (int i = 0; i < n; i++)
            {
                int k = idx[i];
                names[i] = k < 258 ? FontTables.MacGlyphOrder[k] : (k - 258 < extra.Count ? extra[k - 258] : null);
            }
            return names;
        }

        public double? AdvanceByGid(int gid)
        {
            if (!tables.TryGetValue("hmtx", out var t) || numHMetrics == 0) return null;
            int i = Math.Min(gid, numHMetrics - 1);
            return BE.U16(d, t.off + 4 * i) / (double)upem;
        }

        public List<PathCmd> OutlineByName(string name)
        {
            var g = GidForName(name);
            return g.HasValue ? OutlineByGid(g.Value) : null;
        }

        public List<PathCmd> OutlineByCid(int cid) => cff != null ? cff.OutlineByCid(cid) : OutlineByGid(cid);

        public List<PathCmd> OutlineByGid(int gid)
        {
            if (gid < 0 || gid >= Math.Max(numGlyphs, 1)) return null;
            if (cff != null) return cff.OutlineByGid(gid);
            if (loca == null || !tables.TryGetValue("glyf", out _)) return null;
            var sink = new PathSink(1.0 / upem, 1.0 / upem);
            AddGlyph(gid, sink, Matrix.Identity, 0);
            sink.Finish();
            return sink.Cmds;
        }

        void AddGlyph(int gid, PathSink sink, Matrix m, int depth)
        {
            if (depth > 8 || gid < 0 || gid + 1 >= loca.Length) return;
            var glyf = tables["glyf"];
            int start = glyf.off + loca[gid], end = glyf.off + loca[gid + 1];
            if (end <= start || start + 10 > d.Length) return;
            int nContours = BE.S16(d, start);
            if (nContours >= 0) AddSimple(start, nContours, sink, m);
            else AddComposite(start, sink, m, depth);
        }

        void AddSimple(int start, int nContours, PathSink sink, Matrix m)
        {
            int p = start + 10;
            var endPts = new int[nContours];
            for (int i = 0; i < nContours; i++) endPts[i] = BE.U16(d, p + 2 * i);
            p += 2 * nContours;
            int nPts = nContours > 0 ? endPts[nContours - 1] + 1 : 0;
            if (nPts <= 0 || nPts > 65535) return;
            int insLen = BE.U16(d, p);
            p += 2 + insLen;
            var flags = new byte[nPts];
            for (int i = 0; i < nPts && p < d.Length;)
            {
                byte f = d[p++];
                flags[i++] = f;
                if ((f & 8) != 0 && p < d.Length)
                {
                    int rep = d[p++];
                    for (int k = 0; k < rep && i < nPts; k++) flags[i++] = f;
                }
            }
            var xs = new int[nPts];
            var ys = new int[nPts];
            int v = 0;
            for (int i = 0; i < nPts; i++)
            {
                byte f = flags[i];
                if ((f & 2) != 0) { int dx = p < d.Length ? d[p++] : 0; v += (f & 16) != 0 ? dx : -dx; }
                else if ((f & 16) == 0) { v += BE.S16(d, p); p += 2; }
                xs[i] = v;
            }
            v = 0;
            for (int i = 0; i < nPts; i++)
            {
                byte f = flags[i];
                if ((f & 4) != 0) { int dy = p < d.Length ? d[p++] : 0; v += (f & 32) != 0 ? dy : -dy; }
                else if ((f & 32) == 0) { v += BE.S16(d, p); p += 2; }
                ys[i] = v;
            }
            int s = 0;
            for (int c = 0; c < nContours; c++)
            {
                int e = endPts[c];
                if (e < s || e >= nPts) break;
                EmitContour(xs, ys, flags, s, e, sink, m);
                s = e + 1;
            }
        }

        static void EmitContour(int[] xs, int[] ys, byte[] flags, int s, int e, PathSink sink, Matrix m)
        {
            int n = e - s + 1;
            if (n < 2) return;
            bool On(int i) => (flags[s + (i % n)] & 1) != 0;
            void P(int i, out double x, out double y) => m.Apply(xs[s + (i % n)], ys[s + (i % n)], out x, out y);
            // find a start point on the curve
            int first = -1;
            for (int i = 0; i < n; i++) if (On(i)) { first = i; break; }
            double sx, sy;
            if (first < 0)
            {
                P(0, out var x0, out var y0);
                P(1, out var x1, out var y1);
                sx = (x0 + x1) / 2; sy = (y0 + y1) / 2;
                first = 0;
                sink.MoveTo(sx, sy);
                // walk starting with point 1 as control
                for (int k = 1; k <= n; k++)
                {
                    P(k, out var cx, out var cy);
                    P(k + 1, out var nx, out var ny);
                    sink.QuadTo(cx, cy, (cx + nx) / 2, (cy + ny) / 2);
                }
                sink.ClosePath();
                return;
            }
            P(first, out sx, out sy);
            sink.MoveTo(sx, sy);
            bool haveCtrl = false;
            double qx = 0, qy = 0;
            for (int k = 1; k <= n; k++)
            {
                int i = first + k;
                P(i, out var x, out var y);
                if (On(i))
                {
                    if (haveCtrl) sink.QuadTo(qx, qy, x, y);
                    else sink.LineTo(x, y);
                    haveCtrl = false;
                }
                else
                {
                    if (haveCtrl) sink.QuadTo(qx, qy, (qx + x) / 2, (qy + y) / 2);
                    qx = x; qy = y;
                    haveCtrl = true;
                }
            }
            if (haveCtrl) sink.QuadTo(qx, qy, sx, sy);
            sink.ClosePath();
        }

        void AddComposite(int start, PathSink sink, Matrix m, int depth)
        {
            int p = start + 10;
            for (int guard = 0; guard < 256; guard++)
            {
                int flags = BE.U16(d, p), glyph = BE.U16(d, p + 2);
                p += 4;
                double dx, dy;
                if ((flags & 1) != 0) { dx = BE.S16(d, p); dy = BE.S16(d, p + 2); p += 4; }
                else { dx = (sbyte)d[p]; dy = (sbyte)d[p + 1]; p += 2; }
                if ((flags & 2) == 0) { dx = 0; dy = 0; } // point matching not supported
                double a = 1, b = 0, c = 0, dd = 1;
                if ((flags & 8) != 0) { a = dd = BE.S16(d, p) / 16384.0; p += 2; }
                else if ((flags & 0x40) != 0) { a = BE.S16(d, p) / 16384.0; dd = BE.S16(d, p + 2) / 16384.0; p += 4; }
                else if ((flags & 0x80) != 0)
                {
                    a = BE.S16(d, p) / 16384.0; b = BE.S16(d, p + 2) / 16384.0;
                    c = BE.S16(d, p + 4) / 16384.0; dd = BE.S16(d, p + 6) / 16384.0;
                    p += 8;
                }
                var cm = new Matrix(a, b, c, dd, dx, dy).Then(m);
                AddGlyph(glyph, sink, cm, depth + 1);
                if ((flags & 0x20) == 0) break;
            }
        }
    }
}
