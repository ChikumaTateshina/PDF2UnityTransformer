using System;
using System.IO;

namespace PDF2Unity.Core.Util
{
    /// <summary>
    /// Minimal deterministic baseline JPEG encoder (YCbCr 4:4:4, standard Huffman tables, AAN float DCT).
    /// No chroma subsampling, so coloured edges and small coloured text inside images stay clean.
    /// </summary>
    public static class Jpeg
    {
        static readonly int[] ZigZag =
        {
            0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
        };

        static readonly int[] StdLuma =
        {
            16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
            18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
        };

        static readonly int[] StdChroma =
        {
            17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        };

        static readonly byte[] DcLumaBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
        static readonly byte[] DcChromaBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
        static readonly byte[] DcVals = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        static readonly byte[] AcLumaBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
        static readonly byte[] AcLumaVals =
        {
            0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
            0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
            0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
            0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
            0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
            0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
        };
        static readonly byte[] AcChromaBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
        static readonly byte[] AcChromaVals =
        {
            0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
            0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0, 0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
            0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
            0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
            0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
            0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
            0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
        };

        static readonly double[] AanScale = { 1.0, 1.387039845, 1.306562965, 1.175875602, 1.0, 0.785694958, 0.541196100, 0.275899379 };

        sealed class Huff
        {
            public readonly int[] Code = new int[256];
            public readonly int[] Size = new int[256];

            public Huff(byte[] bits, byte[] vals)
            {
                int code = 0, k = 0;
                for (int len = 1; len <= 16; len++)
                {
                    for (int i = 0; i < bits[len - 1]; i++, k++)
                    {
                        Code[vals[k]] = code++;
                        Size[vals[k]] = len;
                    }
                    code <<= 1;
                }
            }
        }

        static readonly Huff DcLuma = new Huff(DcLumaBits, DcVals), DcChroma = new Huff(DcChromaBits, DcVals);
        static readonly Huff AcLuma = new Huff(AcLumaBits, AcLumaVals), AcChroma = new Huff(AcChromaBits, AcChromaVals);

        sealed class BitWriter
        {
            readonly Stream s;
            int acc, n;
            public BitWriter(Stream st) { s = st; }

            public void Put(int code, int size)
            {
                acc = (acc << size) | (code & ((1 << size) - 1));
                n += size;
                while (n >= 8)
                {
                    int b = (acc >> (n - 8)) & 0xFF;
                    s.WriteByte((byte)b);
                    if (b == 0xFF) s.WriteByte(0);
                    n -= 8;
                }
                acc &= (1 << n) - 1;
            }

            public void Flush()
            {
                if (n > 0) Put((1 << (8 - n)) - 1, 8 - n); // pad with 1-bits
            }
        }

        static int[] Scale(int[] std, int quality)
        {
            quality = Math.Max(1, Math.Min(100, quality));
            int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
            var t = new int[64];
            for (int i = 0; i < 64; i++) t[i] = Math.Max(1, Math.Min(255, (std[i] * scale + 50) / 100));
            return t;
        }

        static double[] Divisors(int[] q)
        {
            var d = new double[64];
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                    d[r * 8 + c] = 1.0 / (q[r * 8 + c] * AanScale[r] * AanScale[c] * 8.0);
            return d;
        }

        /// <summary>Encodes RGBA pixels (rows top to bottom; alpha ignored) as a baseline JPEG.</summary>
        public static byte[] Encode(byte[] rgba, int width, int height, int quality = 92)
        {
            var ql = Scale(StdLuma, quality);
            var qc = Scale(StdChroma, quality);
            var dl = Divisors(ql);
            var dc = Divisors(qc);
            using (var ms = new MemoryStream())
            {
                void Marker(int m) { ms.WriteByte(0xFF); ms.WriteByte((byte)m); }
                void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
                Marker(0xD8);
                Marker(0xE0); U16(16);
                foreach (var b in new byte[] { (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 }) ms.WriteByte(b);
                Marker(0xDB); U16(2 + 65 * 2);
                ms.WriteByte(0); for (int i = 0; i < 64; i++) ms.WriteByte((byte)ql[ZigZag[i]]);
                ms.WriteByte(1); for (int i = 0; i < 64; i++) ms.WriteByte((byte)qc[ZigZag[i]]);
                Marker(0xC0); U16(17); ms.WriteByte(8); U16(height); U16(width); ms.WriteByte(3);
                for (int c = 0; c < 3; c++) { ms.WriteByte((byte)(c + 1)); ms.WriteByte(0x11); ms.WriteByte((byte)(c == 0 ? 0 : 1)); }
                void Dht(int cls, byte[] bits, byte[] vals)
                {
                    Marker(0xC4); U16(2 + 1 + 16 + vals.Length);
                    ms.WriteByte((byte)cls);
                    ms.Write(bits, 0, 16);
                    ms.Write(vals, 0, vals.Length);
                }
                Dht(0x00, DcLumaBits, DcVals); Dht(0x10, AcLumaBits, AcLumaVals);
                Dht(0x01, DcChromaBits, DcVals); Dht(0x11, AcChromaBits, AcChromaVals);
                Marker(0xDA); U16(12); ms.WriteByte(3);
                for (int c = 0; c < 3; c++) { ms.WriteByte((byte)(c + 1)); ms.WriteByte((byte)(c == 0 ? 0x00 : 0x11)); }
                ms.WriteByte(0); ms.WriteByte(63); ms.WriteByte(0);

                var bw = new BitWriter(ms);
                var Y = new double[64]; var Cb = new double[64]; var Cr = new double[64];
                var coef = new int[64];
                int py = 0, pcb = 0, pcr = 0;
                for (int by = 0; by < height; by += 8)
                    for (int bx = 0; bx < width; bx += 8)
                    {
                        for (int r = 0; r < 8; r++)
                        {
                            int yy = Math.Min(by + r, height - 1);
                            for (int c = 0; c < 8; c++)
                            {
                                int xx = Math.Min(bx + c, width - 1);
                                int i = (yy * width + xx) * 4;
                                double R = rgba[i], G = rgba[i + 1], B = rgba[i + 2];
                                int k = r * 8 + c;
                                Y[k] = 0.299 * R + 0.587 * G + 0.114 * B - 128;
                                Cb[k] = -0.168736 * R - 0.331264 * G + 0.5 * B;
                                Cr[k] = 0.5 * R - 0.418688 * G - 0.081312 * B;
                            }
                        }
                        py = Block(bw, Y, dl, py, DcLuma, AcLuma, coef);
                        pcb = Block(bw, Cb, dc, pcb, DcChroma, AcChroma, coef);
                        pcr = Block(bw, Cr, dc, pcr, DcChroma, AcChroma, coef);
                    }
                bw.Flush();
                Marker(0xD9);
                return ms.ToArray();
            }
        }

        static int Block(BitWriter bw, double[] d, double[] div, int prevDc, Huff dcH, Huff acH, int[] coef)
        {
            Fdct(d);
            for (int i = 0; i < 64; i++)
            {
                double v = d[ZigZag[i]] * div[ZigZag[i]];
                coef[i] = (int)Math.Round(v, MidpointRounding.AwayFromZero);
            }
            int diff = coef[0] - prevDc;
            int cat = Category(diff);
            bw.Put(dcH.Code[cat], dcH.Size[cat]);
            if (cat > 0) bw.Put(diff < 0 ? diff + (1 << cat) - 1 : diff, cat);
            int run = 0;
            for (int i = 1; i < 64; i++)
            {
                int v = coef[i];
                if (v == 0) { run++; continue; }
                while (run > 15) { bw.Put(acH.Code[0xF0], acH.Size[0xF0]); run -= 16; }
                int c = Category(v);
                int sym = (run << 4) | c;
                bw.Put(acH.Code[sym], acH.Size[sym]);
                bw.Put(v < 0 ? v + (1 << c) - 1 : v, c);
                run = 0;
            }
            if (run > 0) bw.Put(acH.Code[0x00], acH.Size[0x00]);
            return coef[0];
        }

        static int Category(int v)
        {
            v = Math.Abs(v);
            int n = 0;
            while (v > 0) { n++; v >>= 1; }
            return n;
        }

        /// <summary>AAN forward DCT in place (rows, then columns); output is scaled by AanScale[r]*AanScale[c]*8.</summary>
        static void Fdct(double[] d)
        {
            for (int pass = 0; pass < 2; pass++)
                for (int k = 0; k < 8; k++)
                {
                    int o = pass == 0 ? k * 8 : k, s = pass == 0 ? 1 : 8;
                    double d0 = d[o], d1 = d[o + s], d2 = d[o + 2 * s], d3 = d[o + 3 * s], d4 = d[o + 4 * s], d5 = d[o + 5 * s], d6 = d[o + 6 * s], d7 = d[o + 7 * s];
                    double t0 = d0 + d7, t7 = d0 - d7, t1 = d1 + d6, t6 = d1 - d6, t2 = d2 + d5, t5 = d2 - d5, t3 = d3 + d4, t4 = d3 - d4;
                    double t10 = t0 + t3, t13 = t0 - t3, t11 = t1 + t2, t12 = t1 - t2;
                    d[o] = t10 + t11;
                    d[o + 4 * s] = t10 - t11;
                    double z1 = (t12 + t13) * 0.707106781;
                    d[o + 2 * s] = t13 + z1;
                    d[o + 6 * s] = t13 - z1;
                    t10 = t4 + t5; t11 = t5 + t6; t12 = t6 + t7;
                    double z5 = (t10 - t12) * 0.382683433;
                    double z2 = 0.541196100 * t10 + z5;
                    double z4 = 1.306562965 * t12 + z5;
                    double z3 = t11 * 0.707106781;
                    double z11 = t7 + z3, z13 = t7 - z3;
                    d[o + 5 * s] = z13 + z2;
                    d[o + 3 * s] = z13 - z2;
                    d[o + s] = z11 + z4;
                    d[o + 7 * s] = z11 - z4;
                }
        }
    }
}
