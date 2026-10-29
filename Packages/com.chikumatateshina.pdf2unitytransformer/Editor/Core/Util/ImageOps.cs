using System;
using System.Collections.Generic;
using PDF2Unity.Core.Render;

namespace PDF2Unity.Core.Util
{
    /// <summary>Raster helpers for texture output: resampling, visually lossless reduction, atlas packing.</summary>
    public static class ImageOps
    {
        /// <summary>Box-filter resample of an RGBA image (alpha-weighted colour; nearest when enlarging).</summary>
        public static RgbaImage Resample(RgbaImage src, int w, int h)
        {
            if (w == src.Width && h == src.Height) return src;
            var o = new RgbaImage(w, h);
            double sx = (double)src.Width / w, sy = (double)src.Height / h;
            for (int y = 0; y < h; y++)
            {
                int y0 = (int)(y * sy), y1 = Math.Max(y0 + 1, (int)((y + 1) * sy));
                for (int x = 0; x < w; x++)
                {
                    int x0 = (int)(x * sx), x1 = Math.Max(x0 + 1, (int)((x + 1) * sx));
                    double r = 0, g = 0, b = 0, a = 0;
                    int n = 0;
                    for (int yy = y0; yy < y1 && yy < src.Height; yy++)
                        for (int xx = x0; xx < x1 && xx < src.Width; xx++)
                        {
                            int i = (yy * src.Width + xx) * 4;
                            double al = src.Pixels[i + 3];
                            r += src.Pixels[i] * al; g += src.Pixels[i + 1] * al; b += src.Pixels[i + 2] * al; a += al;
                            n++;
                        }
                    int oi = (y * w + x) * 4;
                    if (a > 0) { o.Pixels[oi] = (byte)(r / a + 0.5); o.Pixels[oi + 1] = (byte)(g / a + 0.5); o.Pixels[oi + 2] = (byte)(b / a + 0.5); }
                    o.Pixels[oi + 3] = (byte)(n > 0 ? a / n + 0.5 : 0);
                }
            }
            return o;
        }

        /// <summary>Block compressed GPU formats (DXT/BC) need sizes that are multiples of 4.</summary>
        public static RgbaImage Multiple4(RgbaImage img)
        {
            int w = Math.Max(4, img.Width / 4 * 4), h = Math.Max(4, img.Height / 4 * 4);
            return w == img.Width && h == img.Height ? img : Resample(img, w, h);
        }

        public static bool HasAlpha(RgbaImage img)
        {
            for (int i = 3; i < img.Pixels.Length; i += 4) if (img.Pixels[i] != 255) return true;
            return false;
        }

        /// <summary>
        /// Smallest downscale (down to <paramref name="minScale"/>) whose bilinear reconstruction differs from the
        /// original by less than what can be seen: mean error ≤ 0.6/255 and 99.9th percentile ≤ 8/255 per channel
        /// (both well below the error of the JPEG encoding applied afterwards). Returns the original if none qualifies.
        /// </summary>
        public static RgbaImage VisualReduce(RgbaImage img, double minScale = 0.25)
        {
            if (img.Width < 64 || img.Height < 64) return img;
            RgbaImage best = img;
            foreach (var s in new[] { 0.85, 0.7, 0.5, 0.35, 0.25 })
            {
                if (s < minScale - 1e-9) break;
                int w = Math.Max(4, (int)(img.Width * s) / 4 * 4), h = Math.Max(4, (int)(img.Height * s) / 4 * 4);
                if (w >= best.Width && h >= best.Height) continue;
                var small = Resample(img, w, h);
                if (!Indistinguishable(img, small)) break;
                best = small;
            }
            return best;
        }

        static bool Indistinguishable(RgbaImage orig, RgbaImage small)
        {
            int W = orig.Width, H = orig.Height, w = small.Width, h = small.Height;
            var hist = new long[256];
            long sum = 0, n = 0;
            double fx = (double)w / W, fy = (double)h / H;
            var p = small.Pixels;
            for (int y = 0; y < H; y++)
            {
                double sy = Math.Max(0, Math.Min(h - 1, (y + 0.5) * fy - 0.5));
                int y0 = (int)sy, y1 = Math.Min(h - 1, y0 + 1);
                double ty = sy - y0;
                for (int x = 0; x < W; x++)
                {
                    double sx = Math.Max(0, Math.Min(w - 1, (x + 0.5) * fx - 0.5));
                    int x0 = (int)sx, x1 = Math.Min(w - 1, x0 + 1);
                    double tx = sx - x0;
                    int a = (y0 * w + x0) * 4, b = (y0 * w + x1) * 4, c = (y1 * w + x0) * 4, d = (y1 * w + x1) * 4;
                    int oi = (y * W + x) * 4;
                    int worst = 0;
                    double oa = orig.Pixels[oi + 3] / 255.0;
                    for (int ch = 0; ch < 4; ch++)
                    {
                        double v = (p[a + ch] * (1 - tx) + p[b + ch] * tx) * (1 - ty) + (p[c + ch] * (1 - tx) + p[d + ch] * tx) * ty;
                        double e = Math.Abs(v - orig.Pixels[oi + ch]);
                        if (ch < 3) e *= oa; // colour under transparent pixels does not matter
                        int ei = (int)(e + 0.5);
                        if (ei > worst) worst = ei;
                    }
                    hist[Math.Min(255, worst)]++;
                    sum += worst;
                    n++;
                }
            }
            if (sum > n * 0.6) return false;
            long limit = n / 1000, above = 0;
            for (int e = 255; e > 8; e--) { above += hist[e]; if (above > limit) return false; }
            return true;
        }

        /// <summary>
        /// Error of a simple DXT1 (BC1) encoding of an opaque image: per 4x4 block, RGB565 endpoints on the colour box
        /// diagonal (oriented by the channel covariance) and the nearest of the four palette colours. Real encoders find
        /// better endpoints, so this overestimates. Returns the mean and 99.9th percentile of the per-pixel maximum channel error.
        /// </summary>
        public static (double mean, int p999) Dxt1Error(RgbaImage img)
        {
            int W = img.Width, H = img.Height;
            var px = img.Pixels;
            var hist = new long[256];
            long sum = 0, n = 0;
            var pal = new int[4, 3];
            for (int by = 0; by < H; by += 4)
                for (int bx = 0; bx < W; bx += 4)
                {
                    int x1 = Math.Min(W, bx + 4), y1 = Math.Min(H, by + 4);
                    int[] lo = { 255, 255, 255 }, hi = { 0, 0, 0 };
                    double[] mean = new double[3];
                    int cnt = 0;
                    for (int y = by; y < y1; y++)
                        for (int x = bx; x < x1; x++)
                        {
                            int i = (y * W + x) * 4;
                            for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], px[i + c]); hi[c] = Math.Max(hi[c], px[i + c]); mean[c] += px[i + c]; }
                            cnt++;
                        }
                    for (int c = 0; c < 3; c++) mean[c] /= cnt;
                    // Flip red/blue along the diagonal when they vary against green.
                    double crg = 0, cbg = 0;
                    for (int y = by; y < y1; y++)
                        for (int x = bx; x < x1; x++)
                        {
                            int i = (y * W + x) * 4;
                            double g = px[i + 1] - mean[1];
                            crg += (px[i] - mean[0]) * g;
                            cbg += (px[i + 2] - mean[2]) * g;
                        }
                    int[] e0 = { lo[0], lo[1], lo[2] }, e1 = { hi[0], hi[1], hi[2] };
                    if (crg < 0) { e0[0] = hi[0]; e1[0] = lo[0]; }
                    if (cbg < 0) { e0[2] = hi[2]; e1[2] = lo[2]; }
                    for (int c = 0; c < 3; c++)
                    {
                        int bits = c == 1 ? 6 : 5, max = (1 << bits) - 1;
                        int q0 = (int)Math.Round(e0[c] * max / 255.0), q1 = (int)Math.Round(e1[c] * max / 255.0);
                        int v0 = (q0 << (8 - bits)) | (q0 >> (2 * bits - 8)), v1 = (q1 << (8 - bits)) | (q1 >> (2 * bits - 8));
                        pal[0, c] = v0; pal[1, c] = v1; pal[2, c] = (2 * v0 + v1) / 3; pal[3, c] = (v0 + 2 * v1) / 3;
                    }
                    for (int y = by; y < y1; y++)
                        for (int x = bx; x < x1; x++)
                        {
                            int i = (y * W + x) * 4, best = int.MaxValue, bestMax = 0;
                            for (int k = 0; k < 4; k++)
                            {
                                int dr = px[i] - pal[k, 0], dg = px[i + 1] - pal[k, 1], db = px[i + 2] - pal[k, 2];
                                int d = dr * dr + dg * dg + db * db;
                                if (d < best) { best = d; bestMax = Math.Max(Math.Abs(dr), Math.Max(Math.Abs(dg), Math.Abs(db))); }
                            }
                            hist[Math.Min(255, bestMax)]++;
                            sum += bestMax;
                            n++;
                        }
                }
            long limit = n / 1000, above = 0;
            int p = 0;
            for (int e = 255; e >= 0; e--) { above += hist[e]; if (above > limit) { p = e; break; } }
            return (n > 0 ? (double)sum / n : 0, p);
        }

        /// <summary>Copies <paramref name="src"/> into <paramref name="dst"/> at (x, y) and extrudes its edges by <paramref name="pad"/> pixels.</summary>
        public static void Blit(RgbaImage src, RgbaImage dst, int x, int y, int pad)
        {
            for (int yy = -pad; yy < src.Height + pad; yy++)
            {
                int ty = y + yy;
                if (ty < 0 || ty >= dst.Height) continue;
                int sy = Math.Max(0, Math.Min(src.Height - 1, yy));
                for (int xx = -pad; xx < src.Width + pad; xx++)
                {
                    int tx = x + xx;
                    if (tx < 0 || tx >= dst.Width) continue;
                    int sx = Math.Max(0, Math.Min(src.Width - 1, xx));
                    Buffer.BlockCopy(src.Pixels, (sy * src.Width + sx) * 4, dst.Pixels, (ty * dst.Width + tx) * 4, 4);
                }
            }
        }

        public struct Placement { public int Atlas, X, Y; }

        /// <summary>
        /// MaxRects (bottom-left) packing into atlases whose sides are powers of two no larger than <paramref name="max"/>
        /// (GPU block compression in Unity needs them); every atlas reserves a white <paramref name="white"/>-pixel block
        /// at (0, 0). Sizes include no padding; <paramref name="pad"/> is added around each, and every item must fit a
        /// max x max atlas beside the white block.
        /// Slots are aligned to 8 px so no JPEG/DXT block straddles two images.
        /// </summary>
        public static List<(int w, int h)> Pack(IList<(int w, int h)> sizes, int max, int pad, int white, out Placement[] placed)
        {
            var order = new List<int>();
            for (int i = 0; i < sizes.Count; i++) order.Add(i);
            int PadW(int i) => Align8(sizes[i].w + 2 * pad);
            int PadH(int i) => Align8(sizes[i].h + 2 * pad);
            order.Sort((a, b) => PadH(b) != PadH(a) ? PadH(b).CompareTo(PadH(a)) : PadW(b) != PadW(a) ? PadW(b).CompareTo(PadW(a)) : a.CompareTo(b));
            int widest = Align8(white + pad), tallest = widest;
            foreach (var i in order) { widest = Math.Max(widest, PadW(i)); tallest = Math.Max(tallest, PadH(i)); }
            // Try every power-of-two atlas size that holds the largest item; keep the smallest total area
            // (ties: fewer atlases, then the most square result). The last atlas of a set shrinks to what it uses.
            List<(int w, int h)> best = null;
            placed = null;
            long bestArea = long.MaxValue;
            int bestCount = int.MaxValue, bestSide = int.MaxValue;
            for (int width = Pot(widest); width <= max; width <<= 1)
                for (int height = Pot(tallest); height <= max; height <<= 1)
                {
                    var atlases = PackBins(order, PadW, PadH, width, height, pad, white, sizes.Count, out var pl);
                    if (atlases == null) continue; // some item does not fit beside the white block
                    long area = 0;
                    int side = 0;
                    foreach (var a in atlases) { area += (long)a.w * a.h; side = Math.Max(side, Math.Max(a.w, a.h)); }
                    if (area < bestArea || area == bestArea && (atlases.Count < bestCount || atlases.Count == bestCount && side < bestSide))
                    { best = atlases; placed = pl; bestArea = area; bestCount = atlases.Count; bestSide = side; }
                }
            if (best == null) throw new ArgumentException($"images up to {widest}x{tallest} (padded) do not fit a {max} atlas");
            return best;
        }

        /// <summary>Smallest power of two &gt;= <paramref name="v"/>.</summary>
        public static int Pot(int v)
        {
            int p = 1;
            while (p < v) p <<= 1;
            return p;
        }

        static int Align8(int v) => (v + 7) / 8 * 8;

        struct R { public int X, Y, W, H; public R(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; } }

        sealed class Bin
        {
            public readonly List<R> Free = new List<R>();
            public int UsedW, UsedH;

            public Bin(int w, int h) => Free.Add(new R(0, 0, w, h));

            public bool Find(int w, int h, out int x, out int y)
            {
                x = y = 0;
                int bestBottom = int.MaxValue, bestX = int.MaxValue;
                bool found = false;
                foreach (var f in Free)
                {
                    if (f.W < w || f.H < h) continue;
                    if (f.Y + h < bestBottom || f.Y + h == bestBottom && f.X < bestX)
                    { bestBottom = f.Y + h; bestX = f.X; x = f.X; y = f.Y; found = true; }
                }
                return found;
            }

            public void Place(R r)
            {
                for (int i = Free.Count - 1; i >= 0; i--)
                {
                    var f = Free[i];
                    if (r.X >= f.X + f.W || r.X + r.W <= f.X || r.Y >= f.Y + f.H || r.Y + r.H <= f.Y) continue;
                    Free.RemoveAt(i);
                    if (r.X > f.X) Free.Add(new R(f.X, f.Y, r.X - f.X, f.H));
                    if (r.X + r.W < f.X + f.W) Free.Add(new R(r.X + r.W, f.Y, f.X + f.W - r.X - r.W, f.H));
                    if (r.Y > f.Y) Free.Add(new R(f.X, f.Y, f.W, r.Y - f.Y));
                    if (r.Y + r.H < f.Y + f.H) Free.Add(new R(f.X, r.Y + r.H, f.W, f.Y + f.H - r.Y - r.H));
                }
                // Drop free rectangles contained in another one.
                for (int i = Free.Count - 1; i >= 0; i--)
                    for (int j = 0; j < Free.Count; j++)
                    {
                        if (i == j) continue;
                        R a = Free[i], b = Free[j];
                        if (a.X >= b.X && a.Y >= b.Y && a.X + a.W <= b.X + b.W && a.Y + a.H <= b.Y + b.H) { Free.RemoveAt(i); break; }
                    }
                UsedW = Math.Max(UsedW, r.X + r.W);
                UsedH = Math.Max(UsedH, r.Y + r.H);
            }
        }

        static List<(int w, int h)> PackBins(List<int> order, Func<int, int> padW, Func<int, int> padH, int width, int height, int pad, int white,
            int count, out Placement[] placed)
        {
            placed = new Placement[count];
            var bins = new List<Bin>();
            foreach (var i in order)
            {
                int w = padW(i), h = padH(i), x = 0, y = 0, bi = -1;
                for (int k = 0; k < bins.Count && bi < 0; k++)
                    if (bins[k].Find(w, h, out x, out y)) bi = k;
                if (bi < 0)
                {
                    var b = new Bin(width, height);
                    int wb = Align8(white + pad);
                    b.Place(new R(0, 0, wb, wb));
                    if (!b.Find(w, h, out x, out y)) return null;
                    bins.Add(b);
                    bi = bins.Count - 1;
                }
                bins[bi].Place(new R(x, y, w, h));
                placed[i] = new Placement { Atlas = bi, X = x + pad, Y = y + pad };
            }
            var atlases = new List<(int w, int h)>();
            foreach (var b in bins) atlases.Add((Pot(b.UsedW), Pot(b.UsedH)));
            return atlases;
        }

        public static RgbaImage Crop(RgbaImage src, int x0, int y0, int x1, int y1)
        {
            int w = x1 - x0, h = y1 - y0;
            var o = new RgbaImage(w, h);
            for (int y = 0; y < h; y++) Buffer.BlockCopy(src.Pixels, ((y0 + y) * src.Width + x0) * 4, o.Pixels, y * w * 4, w * 4);
            return o;
        }
    }
}
