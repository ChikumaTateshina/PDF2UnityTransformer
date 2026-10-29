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
        /// Shelf packing (tallest first) into atlases no larger than <paramref name="max"/>; every atlas reserves a
        /// white <paramref name="white"/>-pixel block at (0, 0). Sizes include no padding; <paramref name="pad"/> is added around each.
        /// </summary>
        public static List<(int w, int h)> Pack(IList<(int w, int h)> sizes, int max, int pad, int white, out Placement[] placed)
        {
            var order = new List<int>();
            for (int i = 0; i < sizes.Count; i++) order.Add(i);
            order.Sort((a, b) => sizes[b].h != sizes[a].h ? sizes[b].h.CompareTo(sizes[a].h) : a.CompareTo(b));
            int widest = white + 2 * pad;
            foreach (var s in sizes) widest = Math.Max(widest, (s.w + 2 * pad + 7) / 8 * 8);
            // Try shelf widths from the widest item up to the maximum; keep the smallest total atlas area
            // (ties: the most square result).
            List<(int w, int h)> best = null;
            placed = null;
            long bestArea = long.MaxValue;
            int bestSide = int.MaxValue;
            for (int width = widest; ; width = Math.Min(max, width + 64))
            {
                var atlases = PackWidth(sizes, order, Math.Min(width, max), max, pad, white, out var pl);
                long area = 0;
                int side = 0;
                foreach (var a in atlases) { area += (long)a.w * a.h; side = Math.Max(side, Math.Max(a.w, a.h)); }
                if (area < bestArea || area == bestArea && side < bestSide) { best = atlases; placed = pl; bestArea = area; bestSide = side; }
                if (width >= max) break;
            }
            return best;
        }

        static List<(int w, int h)> PackWidth(IList<(int w, int h)> sizes, List<int> order, int width, int max, int pad, int white, out Placement[] placed)
        {
            placed = new Placement[sizes.Count];
            var atlases = new List<(int w, int h)>();
            int cur = -1, shelfY = 0, shelfH = 0, x = 0, usedW = 0;
            void NewAtlas()
            {
                if (cur >= 0) atlases[cur] = (usedW, shelfY + shelfH);
                atlases.Add((0, 0));
                cur = atlases.Count - 1;
                shelfY = 0; x = white + pad; shelfH = white + pad; usedW = x;
            }
            NewAtlas();
            // Slots are aligned to 8 px so no JPEG block straddles two images.
            int Align8(int v) => (v + 7) / 8 * 8;
            foreach (var i in order)
            {
                int w = Align8(sizes[i].w + 2 * pad), h = Align8(sizes[i].h + 2 * pad);
                if (x + w > width) { shelfY += shelfH; shelfH = 0; x = 0; }
                if (shelfY + h > max) { NewAtlas(); if (x + w > width) { shelfY += shelfH; shelfH = 0; x = 0; } }
                placed[i] = new Placement { Atlas = cur, X = x + pad, Y = shelfY + pad };
                x += w;
                usedW = Math.Max(usedW, x);
                shelfH = Math.Max(shelfH, h);
            }
            atlases[cur] = (usedW, shelfY + shelfH);
            for (int a = 0; a < atlases.Count; a++)
                atlases[a] = ((atlases[a].w + 3) / 4 * 4, (atlases[a].h + 3) / 4 * 4);
            return atlases;
        }
    }
}
