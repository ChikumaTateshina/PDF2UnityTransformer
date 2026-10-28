using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace PDF2Unity.Core.Pdf
{
    /// <summary>Thrown for image codecs that are decoded by PDFium instead (DCT, JPX, CCITT, JBIG2).</summary>
    public sealed class UnsupportedFilterException : Exception
    {
        public UnsupportedFilterException(string f) : base("filter " + f) { }
    }

    /// <summary>Standard stream filters: Flate, LZW (+predictors), ASCIIHex, ASCII85, RunLength.</summary>
    public static class Filters
    {
        public static byte[] Decode(PdfStream s, Limits limits)
        {
            limits = limits ?? new Limits();
            var data = s.Raw;
            var names = s.FilterNames();
            var parmsObj = s.Dict.Get("DecodeParms") ?? s.Dict.Get("DP");
            for (int i = 0; i < names.Count; i++)
            {
                PdfDict parms = null;
                if (parmsObj is PdfDict pd) parms = pd;
                else if (parmsObj is PdfArray pa && i < pa.Count) parms = pa.Dict(i);
                data = Apply(names[i], data, parms, limits);
                if (data.Length > limits.MaxStreamBytes)
                    throw new CompileException("limit.stream", $"decoded stream of {data.Length} bytes exceeds limit");
            }
            return data;
        }

        public static byte[] Apply(string name, byte[] data, PdfDict parms, Limits limits)
        {
            switch (name)
            {
                case "FlateDecode":
                case "Fl":
                    return Predict(Inflate(data, limits.MaxStreamBytes), parms);
                case "LZWDecode":
                case "LZW":
                    return Predict(Lzw(data, parms == null || parms.GetInt("EarlyChange", 1) != 0, limits.MaxStreamBytes), parms);
                case "ASCIIHexDecode":
                case "AHx":
                    return AsciiHex(data);
                case "ASCII85Decode":
                case "A85":
                    return Ascii85(data);
                case "RunLengthDecode":
                case "RL":
                    return RunLength(data);
                case "Crypt":
                    return data;
                default:
                    throw new UnsupportedFilterException(name);
            }
        }

        public static byte[] Inflate(byte[] data, long max)
        {
            int start = 0;
            // zlib header (CMF/FLG); tolerate raw deflate data too.
            if (data.Length >= 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0) start = 2;
            using (var input = new MemoryStream(data, start, data.Length - start))
            using (var z = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var buf = new byte[1 << 16];
                try
                {
                    int n;
                    while ((n = z.Read(buf, 0, buf.Length)) > 0)
                    {
                        output.Write(buf, 0, n);
                        if (output.Length > max)
                            throw new CompileException("limit.stream", "decoded stream exceeds limit");
                    }
                }
                catch (InvalidDataException) { /* truncated / damaged stream: keep what was decoded */ }
                return output.ToArray();
            }
        }

        static byte[] Predict(byte[] data, PdfDict parms)
        {
            if (parms == null) return data;
            int pred = parms.GetInt("Predictor", 1);
            if (pred <= 1) return data;
            int colors = Math.Max(1, parms.GetInt("Colors", 1));
            int bpc = Math.Max(1, parms.GetInt("BitsPerComponent", 8));
            int columns = Math.Max(1, parms.GetInt("Columns", 1));
            int bpp = Math.Max(1, (colors * bpc + 7) / 8);
            int row = (colors * bpc * columns + 7) / 8;
            if (pred == 2)
            {
                var o = (byte[])data.Clone();
                if (bpc == 8)
                    for (int r = 0; r + row <= o.Length; r += row)
                        for (int i = bpp; i < row; i++) o[r + i] = (byte)(o[r + i] + o[r + i - bpp]);
                return o;
            }
            var output = new MemoryStream();
            var prev = new byte[row];
            var cur = new byte[row];
            int p = 0;
            while (p < data.Length)
            {
                int type = data[p++];
                int n = Math.Min(row, data.Length - p);
                Array.Clear(cur, 0, row);
                Buffer.BlockCopy(data, p, cur, 0, n);
                p += n;
                for (int i = 0; i < row; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    int b = prev[i];
                    int c = i >= bpp ? prev[i - bpp] : 0;
                    switch (type)
                    {
                        case 1: cur[i] = (byte)(cur[i] + a); break;
                        case 2: cur[i] = (byte)(cur[i] + b); break;
                        case 3: cur[i] = (byte)(cur[i] + ((a + b) >> 1)); break;
                        case 4:
                            int pp = a + b - c, pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
                            cur[i] = (byte)(cur[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
                            break;
                    }
                }
                output.Write(cur, 0, row);
                var t = prev; prev = cur; cur = t;
            }
            return output.ToArray();
        }

        static byte[] Lzw(byte[] data, bool early, long max)
        {
            var output = new MemoryStream();
            var dict = new List<byte[]>(4096);
            void Reset()
            {
                dict.Clear();
                for (int i = 0; i < 256; i++) dict.Add(new[] { (byte)i });
                dict.Add(null);
                dict.Add(null);
            }
            Reset();
            int codeLen = 9;
            long bitBuf = 0;
            int bits = 0;
            byte[] prev = null;
            foreach (var b in data)
            {
                bitBuf = (bitBuf << 8) | b;
                bits += 8;
                while (bits >= codeLen)
                {
                    int code = (int)((bitBuf >> (bits - codeLen)) & ((1 << codeLen) - 1));
                    bits -= codeLen;
                    if (code == 256) { Reset(); codeLen = 9; prev = null; continue; }
                    if (code == 257) return output.ToArray();
                    byte[] entry;
                    if (code < dict.Count && dict[code] != null) entry = dict[code];
                    else if (prev != null)
                    {
                        entry = new byte[prev.Length + 1];
                        Buffer.BlockCopy(prev, 0, entry, 0, prev.Length);
                        entry[prev.Length] = prev[0];
                    }
                    else continue;
                    output.Write(entry, 0, entry.Length);
                    if (output.Length > max) throw new CompileException("limit.stream", "decoded stream exceeds limit");
                    if (prev != null && dict.Count < 4096)
                    {
                        var ne = new byte[prev.Length + 1];
                        Buffer.BlockCopy(prev, 0, ne, 0, prev.Length);
                        ne[prev.Length] = entry[0];
                        dict.Add(ne);
                    }
                    prev = entry;
                    int threshold = dict.Count + (early ? 1 : 0);
                    if (threshold >= 4096) codeLen = 12;
                    else if (threshold >= 2048) codeLen = 12;
                    else if (threshold >= 1024) codeLen = 11;
                    else if (threshold >= 512) codeLen = 10;
                    else codeLen = 9;
                }
            }
            return output.ToArray();
        }

        static byte[] AsciiHex(byte[] d)
        {
            var o = new List<byte>(d.Length / 2);
            int hi = -1;
            foreach (var c in d)
            {
                if (c == '>') break;
                int h = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                if (h < 0) continue;
                if (hi < 0) hi = h;
                else { o.Add((byte)(hi * 16 + h)); hi = -1; }
            }
            if (hi >= 0) o.Add((byte)(hi * 16));
            return o.ToArray();
        }

        static byte[] Ascii85(byte[] d)
        {
            var o = new List<byte>(d.Length);
            uint tuple = 0;
            int count = 0;
            for (int i = 0; i < d.Length; i++)
            {
                int c = d[i];
                if (c == '~') break;
                if (Lexer.IsWhite(c)) continue;
                if (c == 'z' && count == 0) { o.Add(0); o.Add(0); o.Add(0); o.Add(0); continue; }
                if (c < '!' || c > 'u') continue;
                tuple = tuple * 85 + (uint)(c - '!');
                if (++count == 5)
                {
                    o.Add((byte)(tuple >> 24)); o.Add((byte)(tuple >> 16)); o.Add((byte)(tuple >> 8)); o.Add((byte)tuple);
                    tuple = 0;
                    count = 0;
                }
            }
            if (count > 1)
            {
                for (int k = count; k < 5; k++) tuple = tuple * 85 + 84;
                for (int k = 0; k < count - 1; k++) o.Add((byte)(tuple >> (24 - 8 * k)));
            }
            return o.ToArray();
        }

        static byte[] RunLength(byte[] d)
        {
            var o = new List<byte>(d.Length * 2);
            int i = 0;
            while (i < d.Length)
            {
                int n = d[i++];
                if (n == 128) break;
                if (n < 128)
                {
                    for (int k = 0; k <= n && i < d.Length; k++) o.Add(d[i++]);
                }
                else if (i < d.Length)
                {
                    byte b = d[i++];
                    for (int k = 0; k < 257 - n; k++) o.Add(b);
                }
            }
            return o.ToArray();
        }
    }
}
