using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PDF2Unity.Core.Pdf;

namespace PDF2Unity.Core.Render
{
    /// <summary>
    /// DeviceCMYK -> sRGB table rendered once by PDFium (17^4 patches) and cached on disk, so
    /// vector colours match the colours PDFium uses for images and precomposited regions.
    /// </summary>
    public static class CmykTable
    {
        public const int N = 17;
        static float[] table; // N^4 * 3
        static readonly object gate = new object();

        public static float[] Get(string cacheDir)
        {
            lock (gate)
            {
                if (table != null) return table;
                string path = cacheDir != null ? Path.Combine(cacheDir, "cmyk-table-8076.bin") : null;
                if (path != null && File.Exists(path))
                {
                    var b = File.ReadAllBytes(path);
                    if (b.Length == N * N * N * N * 3)
                    {
                        table = new float[b.Length];
                        for (int i = 0; i < b.Length; i++) table[i] = b[i] / 255f;
                        return table;
                    }
                }
                var bytes = Build();
                if (path != null)
                {
                    Directory.CreateDirectory(cacheDir);
                    File.WriteAllBytes(path, bytes);
                }
                table = new float[bytes.Length];
                for (int i = 0; i < bytes.Length; i++) table[i] = bytes[i] / 255f;
                return table;
            }
        }

        static byte[] Build()
        {
            int side = N * N; // 289 x 289 patches of 1pt
            var sb = new StringBuilder(N * N * N * N * 48);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < N * N * N * N; i++)
            {
                int k = i % N, y = i / N % N, m = i / (N * N) % N, c = i / (N * N * N);
                int px = i % side, py = i / side;
                sb.Append(string.Format(inv, "{0:0.####} {1:0.####} {2:0.####} {3:0.####} k {4} {5} 1 1 re f\n",
                    c / (double)(N - 1), m / (double)(N - 1), y / (double)(N - 1), k / (double)(N - 1), px, side - 1 - py));
            }
            var w = new PdfWriter();
            w.Raw("%PDF-1.7\n");
            w.WriteObject(1, new Dictionary<string, object> { ["Type"] = new PdfName("Catalog"), ["Pages"] = new PdfRef(2, 0) });
            w.WriteObject(2, new Dictionary<string, object> { ["Type"] = new PdfName("Pages"), ["Kids"] = new List<object> { new PdfRef(3, 0) }, ["Count"] = 1.0 });
            w.WriteObject(3, new Dictionary<string, object>
            {
                ["Type"] = new PdfName("Page"), ["Parent"] = new PdfRef(2, 0),
                ["MediaBox"] = new List<object> { 0.0, 0.0, (double)side, (double)side }, ["Contents"] = new PdfRef(4, 0),
            });
            w.WriteStream(4, null, PdfUtil.Latin1.GetBytes(sb.ToString()));
            w.Finish(new Dictionary<string, object> { ["Root"] = new PdfRef(1, 0) }, 0, 5);
            using (var doc = new PdfiumDocument(w.ToArray()))
            {
                const int s = 4;
                var img = doc.Render(0, 0, 0, side, side, side * s, side * s, false);
                var o = new byte[N * N * N * N * 3];
                for (int i = 0; i < N * N * N * N; i++)
                {
                    int px = i % side, py = i / side;
                    int at = ((py * s + s / 2) * img.Width + px * s + s / 2) * 4;
                    o[i * 3] = img.Pixels[at];
                    o[i * 3 + 1] = img.Pixels[at + 1];
                    o[i * 3 + 2] = img.Pixels[at + 2];
                }
                return o;
            }
        }

        /// <summary>Quadrilinear interpolation.</summary>
        public static void Lookup(float[] t, double c, double m, double y, double k, out double r, out double g, out double b)
        {
            double fc = Clamp(c) * (N - 1), fm = Clamp(m) * (N - 1), fy = Clamp(y) * (N - 1), fk = Clamp(k) * (N - 1);
            int ic = Math.Min((int)fc, N - 2), im = Math.Min((int)fm, N - 2), iy = Math.Min((int)fy, N - 2), ik = Math.Min((int)fk, N - 2);
            double tc = fc - ic, tm = fm - im, ty = fy - iy, tk = fk - ik;
            r = g = b = 0;
            for (int corner = 0; corner < 16; corner++)
            {
                int bc = corner & 1, bm = corner >> 1 & 1, by = corner >> 2 & 1, bk = corner >> 3 & 1;
                double wgt = (bc == 1 ? tc : 1 - tc) * (bm == 1 ? tm : 1 - tm) * (by == 1 ? ty : 1 - ty) * (bk == 1 ? tk : 1 - tk);
                if (wgt == 0) continue;
                int idx = ((((ic + bc) * N + im + bm) * N + iy + by) * N + ik + bk) * 3;
                r += wgt * t[idx];
                g += wgt * t[idx + 1];
                b += wgt * t[idx + 2];
            }
        }

        static double Clamp(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
