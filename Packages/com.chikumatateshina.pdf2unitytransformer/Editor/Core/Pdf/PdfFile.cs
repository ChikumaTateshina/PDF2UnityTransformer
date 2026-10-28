using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PDF2Unity.Core.Pdf
{
    /// <summary>
    /// Random-access PDF reader: classic xref tables, xref streams, hybrid files,
    /// object streams, /Prev chains, and a full-scan reconstruction for damaged files.
    /// </summary>
    public sealed class PdfFile
    {
        struct Entry
        {
            public int Type; // 1 = offset, 2 = in object stream
            public long Offset; // type 1: byte offset; type 2: object stream number
            public int Index; // type 2: index within the object stream
            public int Gen;
        }

        public readonly byte[] Data;
        public PdfDict Trailer { get; private set; }
        public PdfDict Root => Trailer?.GetDict("Root");
        public long StartXref { get; private set; }
        public bool Reconstructed { get; private set; }
        public readonly Limits Limits;

        readonly Dictionary<int, Entry> xref = new Dictionary<int, Entry>();
        readonly Dictionary<int, object> cache = new Dictionary<int, object>();
        readonly Dictionary<int, (int[] nums, int[] offs, byte[] data, int first)> objStreams =
            new Dictionary<int, (int[], int[], byte[], int)>();
        readonly HashSet<int> resolving = new HashSet<int>();

        public PdfFile(byte[] data, Limits limits)
        {
            Data = data;
            Limits = limits ?? new Limits();
            int head = IndexOf(Data, "%PDF-", 0, Math.Min(Data.Length, 1024));
            if (head < 0) throw new CompileException("input.not-pdf", "missing %PDF- header");
            try
            {
                ReadXrefChain();
            }
            catch (Exception e) when (!(e is CompileException))
            {
                xref.Clear();
                Trailer = null;
            }
            if (Trailer == null || Root == null || xref.Count == 0) Reconstruct();
            if (Trailer.Has("Encrypt"))
                throw new CompileException("input.encrypted",
                    "document is encrypted / permission restricted; protection is not bypassed");
            if (Root == null) throw new CompileException("input.corrupt", "document catalog not found");
        }

        public int MaxObjectNumber
        {
            get
            {
                int m = 0;
                foreach (var k in xref.Keys) m = Math.Max(m, k);
                return m;
            }
        }

        // ------------------------------------------------------------------
        static int IndexOf(byte[] d, string s, int start, int end)
        {
            for (int i = start; i <= end - s.Length; i++)
            {
                int k = 0;
                while (k < s.Length && d[i + k] == s[k]) k++;
                if (k == s.Length) return i;
            }
            return -1;
        }

        static int LastIndexOf(byte[] d, string s, int from)
        {
            for (int i = Math.Min(from, d.Length - s.Length); i >= 0; i--)
            {
                int k = 0;
                while (k < s.Length && d[i + k] == s[k]) k++;
                if (k == s.Length) return i;
            }
            return -1;
        }

        void ReadXrefChain()
        {
            int sx = LastIndexOf(Data, "startxref", Data.Length - 1);
            if (sx < 0) return;
            var lx = new Lexer(Data, sx + 9);
            var off = lx.Next();
            long pos = (long)PdfUtil.ToDouble(off, -1);
            StartXref = pos;
            var seen = new HashSet<long>();
            PdfDict first = null;
            while (pos > 0 && pos < Data.Length && seen.Add(pos))
            {
                var t = ReadXrefSection(pos);
                if (t == null) break;
                if (first == null) first = t;
                if (t.Has("XRefStm"))
                {
                    long xs = (long)t.GetNumber("XRefStm", -1);
                    if (xs > 0 && seen.Add(xs)) ReadXrefSection(xs);
                }
                pos = (long)t.GetNumber("Prev", -1);
            }
            Trailer = first;
        }

        PdfDict ReadXrefSection(long pos)
        {
            var lx = new Lexer(Data, (int)pos) { Owner = this };
            lx.SkipWhite();
            int p = lx.Pos;
            if (p + 4 <= Data.Length && Data[p] == 'x' && Data[p + 1] == 'r' && Data[p + 2] == 'e' && Data[p + 3] == 'f')
            {
                lx.Pos = p + 4;
                while (true)
                {
                    lx.SkipWhite();
                    var tok = lx.Next();
                    if (tok is PdfKeyword k && k.Value == "trailer")
                    {
                        var tr = lx.Next() as PdfDict;
                        return tr;
                    }
                    if (!(tok is double)) return null;
                    int start = (int)(double)tok;
                    int count = (int)PdfUtil.ToDouble(lx.Next());
                    lx.SkipWhite();
                    for (int i = 0; i < count; i++)
                    {
                        lx.SkipWhite();
                        int s = lx.Pos;
                        if (s + 18 > Data.Length) return null;
                        long o = 0;
                        int k2 = s;
                        while (k2 < Data.Length && Data[k2] >= '0' && Data[k2] <= '9') o = o * 10 + (Data[k2++] - '0');
                        while (k2 < Data.Length && Data[k2] == ' ') k2++;
                        int g = 0;
                        while (k2 < Data.Length && Data[k2] >= '0' && Data[k2] <= '9') g = g * 10 + (Data[k2++] - '0');
                        while (k2 < Data.Length && Data[k2] == ' ') k2++;
                        char type = (char)Data[k2];
                        lx.Pos = k2 + 1;
                        int num = start + i;
                        if (type == 'n' && !xref.ContainsKey(num) && o > 0)
                            xref[num] = new Entry { Type = 1, Offset = o, Gen = g };
                        else if (type == 'f' && !xref.ContainsKey(num))
                            xref[num] = new Entry { Type = 0 };
                    }
                }
            }
            // xref stream: "n g obj << ... >> stream"
            var obj = ParseIndirectAt((int)pos, out _);
            if (!(obj is PdfStream st) || st.Dict.GetName("Type") != "XRef") return null;
            var w = st.Dict.GetArray("W");
            int size = st.Dict.GetInt("Size");
            var index = st.Dict.GetArray("Index");
            var idx = new List<int>();
            if (index != null) for (int i = 0; i + 1 < index.Count; i += 2) { idx.Add((int)index.Num(i)); idx.Add((int)index.Num(i + 1)); }
            else { idx.Add(0); idx.Add(size); }
            var data = st.Decode(Limits);
            int w0 = (int)w.Num(0), w1 = (int)w.Num(1), w2 = (int)w.Num(2);
            int rec = w0 + w1 + w2;
            int at = 0;
            for (int s = 0; s + 1 < idx.Count; s += 2)
            {
                for (int i = 0; i < idx[s + 1]; i++)
                {
                    if (at + rec > data.Length) break;
                    long f0 = w0 == 0 ? 1 : Read(data, at, w0);
                    long f1 = Read(data, at + w0, w1);
                    long f2 = Read(data, at + w0 + w1, w2);
                    at += rec;
                    int num = idx[s] + i;
                    if (xref.ContainsKey(num)) continue;
                    if (f0 == 1) xref[num] = new Entry { Type = 1, Offset = f1, Gen = (int)f2 };
                    else if (f0 == 2) xref[num] = new Entry { Type = 2, Offset = f1, Index = (int)f2 };
                    else xref[num] = new Entry { Type = 0 };
                }
            }
            return st.Dict;
        }

        static long Read(byte[] d, int at, int n)
        {
            long v = 0;
            for (int i = 0; i < n; i++) v = (v << 8) | d[at + i];
            return v;
        }

        void Reconstruct()
        {
            Reconstructed = true;
            xref.Clear();
            cache.Clear();
            var text = PdfUtil.Latin1.GetString(Data);
            foreach (Match m in Regex.Matches(text, @"(?<![0-9])(\d{1,10})\s+(\d{1,5})\s+obj\b"))
            {
                int num = int.Parse(m.Groups[1].Value);
                xref[num] = new Entry { Type = 1, Offset = m.Index, Gen = int.Parse(m.Groups[2].Value) };
            }
            PdfDict trailer = null;
            foreach (Match m in Regex.Matches(text, @"trailer\s*<<"))
            {
                var lx = new Lexer(Data, m.Index + 7) { Owner = this };
                if (lx.Next() is PdfDict t && t.Has("Root")) trailer = t;
            }
            if (trailer == null)
            {
                // xref streams or object streams: find a catalog
                foreach (var kv in new List<KeyValuePair<int, Entry>>(xref))
                {
                    var o = GetObject(kv.Key);
                    var d = o as PdfDict ?? (o as PdfStream)?.Dict;
                    if (d == null) continue;
                    if (d.GetName("Type") == "XRef" && d.Has("Root")) { trailer = d; break; }
                    if (d.GetName("Type") == "ObjStm") IndexObjectStream(kv.Key);
                }
                if (trailer == null)
                {
                    foreach (var kv in new List<KeyValuePair<int, Entry>>(xref))
                    {
                        var d = GetObject(kv.Key) as PdfDict;
                        if (d != null && d.GetName("Type") == "Catalog")
                        {
                            trailer = new PdfDict { Owner = this };
                            trailer.Set("Root", new PdfRef(kv.Key, kv.Value.Gen));
                            break;
                        }
                    }
                }
            }
            Trailer = trailer ?? throw new CompileException("input.corrupt", "PDF cannot be parsed (no trailer / catalog)");
        }

        void IndexObjectStream(int num)
        {
            if (!LoadObjStream(num, out var os)) return;
            for (int i = 0; i < os.nums.Length; i++)
                if (!xref.ContainsKey(os.nums[i]))
                    xref[os.nums[i]] = new Entry { Type = 2, Offset = num, Index = i };
        }

        // ------------------------------------------------------------------
        public object Resolve(object o)
        {
            int guard = 0;
            while (o is PdfRef r && guard++ < 32) o = GetObject(r.Num);
            return o;
        }

        public object GetObject(int num)
        {
            if (cache.TryGetValue(num, out var c)) return c;
            if (!xref.TryGetValue(num, out var e) || e.Type == 0) return PdfNull.Value;
            if (!resolving.Add(num)) return PdfNull.Value; // reference cycle while parsing
            object result = PdfNull.Value;
            try
            {
                if (e.Type == 1)
                {
                    result = ParseIndirectAt((int)e.Offset, out int foundNum);
                    if (foundNum != num && !Reconstructed)
                    {
                        // Broken offset: try reconstruction once.
                        Reconstruct();
                        cache.Clear();
                        resolving.Remove(num);
                        return GetObject(num);
                    }
                }
                else if (e.Type == 2)
                {
                    result = FromObjStream((int)e.Offset, e.Index, num);
                }
            }
            catch (CompileException) { throw; }
            catch (Exception) { result = PdfNull.Value; }
            finally { resolving.Remove(num); }
            switch (result)
            {
                case PdfContainer pc when pc.Ref == null: pc.Ref = new PdfRef(num, e.Gen); break;
            }
            if (result is PdfStream ps) ps.Dict.Ref = ps.Ref;
            cache[num] = result;
            return result;
        }

        object ParseIndirectAt(int offset, out int num)
        {
            num = -1;
            if (offset < 0 || offset >= Data.Length) return PdfNull.Value;
            var lx = new Lexer(Data, offset) { Owner = null };
            var n = lx.Next();
            var g = lx.Next();
            var kw = lx.Next() as PdfKeyword;
            if (!(n is double) || !(g is double) || kw == null || kw.Value != "obj") return PdfNull.Value;
            num = (int)(double)n;
            lx.Owner = this;
            var obj = lx.Next();
            if (obj is PdfDict dict)
            {
                int save = lx.Pos;
                var next = lx.Next();
                if (next is PdfKeyword sk && sk.Value == "stream")
                {
                    int p = lx.Pos;
                    if (p < Data.Length && Data[p] == 13) p++;
                    if (p < Data.Length && Data[p] == 10) p++;
                    long len = (long)PdfUtil.ToDouble(Resolve(dict.Raw("Length")), -1);
                    int endPos;
                    if (len >= 0 && p + len <= Data.Length && LooksLikeEndstream((int)(p + len)))
                        endPos = (int)(p + len);
                    else
                    {
                        endPos = IndexOf(Data, "endstream", p, Data.Length);
                        if (endPos < 0) endPos = Data.Length;
                        // trim EOL before endstream
                        while (endPos > p && (Data[endPos - 1] == 10 || Data[endPos - 1] == 13)) endPos--;
                    }
                    var raw = new byte[endPos - p];
                    Buffer.BlockCopy(Data, p, raw, 0, raw.Length);
                    return new PdfStream(dict, raw) { Owner = this };
                }
                lx.Pos = save;
            }
            return obj ?? PdfNull.Value;
        }

        bool LooksLikeEndstream(int at)
        {
            int p = at;
            while (p < Data.Length && Lexer.IsWhite(Data[p])) p++;
            return IndexOf(Data, "endstream", p, Math.Min(Data.Length, p + 9)) == p;
        }

        bool LoadObjStream(int stmNum, out (int[] nums, int[] offs, byte[] data, int first) os)
        {
            if (objStreams.TryGetValue(stmNum, out os)) return true;
            if (!(GetObject(stmNum) is PdfStream st)) return false;
            var data = st.Decode(Limits);
            int n = st.Dict.GetInt("N");
            int first = st.Dict.GetInt("First");
            var lx = new Lexer(data, 0, Math.Min(first, data.Length));
            var nums = new int[n];
            var offs = new int[n];
            for (int i = 0; i < n; i++)
            {
                nums[i] = (int)PdfUtil.ToDouble(lx.Next(), -1);
                offs[i] = (int)PdfUtil.ToDouble(lx.Next(), 0);
            }
            os = (nums, offs, data, first);
            objStreams[stmNum] = os;
            return true;
        }

        object FromObjStream(int stmNum, int index, int num)
        {
            if (!LoadObjStream(stmNum, out var os)) return PdfNull.Value;
            int i = index;
            if (i < 0 || i >= os.nums.Length || os.nums[i] != num) i = Array.IndexOf(os.nums, num);
            if (i < 0) return PdfNull.Value;
            var lx = new Lexer(os.data, os.first + os.offs[i]) { Owner = this };
            return lx.Next() ?? PdfNull.Value;
        }

        // ------------------------------------------------------------------
        public List<PdfDict> Pages()
        {
            var list = new List<PdfDict>();
            var seen = new HashSet<object>();
            void Walk(PdfDict node, int depth)
            {
                if (node == null || depth > 64 || !seen.Add(node.Key)) return;
                var kids = node.GetArray("Kids");
                if (node.GetName("Type") == "Page" || (kids == null && node.Has("Contents")))
                {
                    list.Add(node);
                    return;
                }
                if (kids == null) return;
                for (int i = 0; i < kids.Count; i++)
                {
                    var k = kids[i] as PdfDict;
                    if (k != null && k.Raw("Parent") == null) k.Set("Parent", node);
                    Walk(k, depth + 1);
                }
            }
            Walk(Root?.GetDict("Pages"), 0);
            return list;
        }

        public static object Inherited(PdfDict page, string key)
        {
            var n = page;
            for (int i = 0; n != null && i < 64; i++)
            {
                if (n.Has(key)) return n.Get(key);
                n = n.GetDict("Parent");
            }
            return null;
        }
    }
}
