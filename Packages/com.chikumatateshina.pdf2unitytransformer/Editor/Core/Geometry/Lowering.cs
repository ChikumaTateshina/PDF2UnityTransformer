using System;
using System.Collections.Generic;
using PDF2Unity.Core.Content;
using PDF2Unity.Core.Fonts;
using PDF2Unity.Core.Graphics;

namespace PDF2Unity.Core.Geometry
{
    public sealed class Profile
    {
        public string Name = "pc-standard";
        public string Target = "pc";
        public double CurveTolerancePt = 0.05;
        public double MinStrokeWidthPt = 0.25;
        public double CompositeDpi = 300;
        public double ImageDpi = 300;
        public double LightImageDpi = 200; // image density cap in the light image mode
        public int MaxTextureSize = 4096;
        public bool EnforceBudgets;
        public double BytesPerPixel = 1.0, MipFactor = 1.333;
        public int MaxTrianglesPerPage = int.MaxValue, MaxTrianglesTotal = int.MaxValue, MaxBatchesPerPage = int.MaxValue;
        public double MaxTextureMemoryMB = double.MaxValue, MaxOverdraw = double.MaxValue;

        public static Profile PcStandard() => new Profile();
        public static Profile PcHigh() => new Profile { Name = "pc-high", CurveTolerancePt = 0.02, MinStrokeWidthPt = 0.2, CompositeDpi = 450, ImageDpi = 450, LightImageDpi = 300, MaxTextureSize = 8192 };
        public static Profile QuestStandard() => new Profile
        {
            Name = "quest-standard", Target = "quest", CurveTolerancePt = 0.15, MinStrokeWidthPt = 0.35, CompositeDpi = 200, ImageDpi = 200, LightImageDpi = 150,
            MaxTextureSize = 2048, EnforceBudgets = true, BytesPerPixel = 0.445, MaxTrianglesPerPage = 120000, MaxTrianglesTotal = 600000,
            MaxBatchesPerPage = 32, MaxTextureMemoryMB = 96, MaxOverdraw = 6,
        };

        public static Profile ByName(string n)
        {
            switch (n)
            {
                case "pc-high": return PcHigh();
                case "quest-standard": return QuestStandard();
                default: return PcStandard();
            }
        }
    }

    /// <summary>One native draw item in painting order (page space geometry).</summary>
    public sealed class DrawItem
    {
        public bool Textured;
        public string Texture;      // image key or composite key
        public Mesh2 Mesh;
        public byte[] Colors;       // rgba per vertex
        public double[] Uv;         // per vertex (textured)
        public double Order;        // painting position (op id; composites use op id + 0.5)
        public long Truncate;       // content offset after which this item is fully painted
        public Rect DisplayBox;
        public bool Composite;
        public bool CompositeAlpha; // delta composite: only changed pixels are opaque
    }

    public sealed class CompositeRegion
    {
        public Rect Rect;          // display space points
        public TruncPoint After;   // render content up to and including the last unsupported operation
        public TruncPoint Before;  // ... and up to (excluding) the first one; null = whole page (page-fallback)
        public int FirstOp = int.MaxValue, LastOp = -1;
        public double Order;
        public readonly List<string> Reasons = new List<string>();
        public readonly List<int> OpIds = new List<int>();
        public string Key;
        public bool Partial;       // delta composite (transparent where the effect changed nothing)
    }

    public sealed class ImageUse
    {
        public ImageRef Image;
        public double Ratio; // needed pixels / source pixels (max over uses)
    }

    /// <summary>Clip node polygons, resolved iteratively (deep clip chains do not recurse).</summary>
    sealed class ClipResolver
    {
        public sealed class Shape
        {
            public Region Region;
            public Rect? RectShape;
            public Rect Bounds;
            public bool Empty;
        }

        readonly PageModel page;
        readonly double tol;
        readonly Dictionary<int, Shape> cache = new Dictionary<int, Shape>();

        public ClipResolver(PageModel p, double t) { page = p; tol = t; }

        public Shape Get(int id)
        {
            var pending = new List<int>();
            int cur = id;
            while (cur >= 0 && !cache.ContainsKey(cur)) { pending.Add(cur); cur = page.Clips[cur].Parent; }
            for (int i = pending.Count - 1; i >= 0; i--) cache[pending[i]] = Resolve(pending[i]);
            return cache[id];
        }

        Shape Resolve(int id)
        {
            var node = page.Clips[id];
            Shape parent = node.Parent >= 0 ? cache[node.Parent] : null;
            if (parent != null && parent.Empty) return parent;
            var region = Geo.Normalize(Geo.FillContours(node.Path, node.Ctm, tol), node.Rule);
            var rect = AsRect(region);
            if (parent == null) return Make(region, rect);
            if (rect.HasValue && parent.RectShape.HasValue)
            {
                var r = rect.Value.Intersect(parent.RectShape.Value);
                return r.IsEmpty ? new Shape { Empty = true, Region = new Region() } : Make(Geo.RectRegion(r), r);
            }
            var res = Geo.Intersect(region, parent.Region);
            return Make(res, AsRect(res));
        }

        static Shape Make(Region r, Rect? rect)
        {
            var b = rect ?? r.Bounds;
            return new Shape { Region = r, RectShape = rect, Bounds = b, Empty = r.IsEmpty || b.IsEmpty };
        }

        static Rect? AsRect(Region r)
        {
            if (r.Contours.Count != 1) return null;
            var c = r.Contours[0];
            var xs = new SortedSet<double>();
            var ys = new SortedSet<double>();
            for (int i = 0; i + 1 < c.Length; i += 2) { xs.Add(Math.Round(c[i], 6)); ys.Add(Math.Round(c[i + 1], 6)); }
            if (xs.Count != 2 || ys.Count != 2 || c.Length != 8) return null;
            return new Rect(xs.Min, ys.Min, xs.Max, ys.Max);
        }
    }

    /// <summary>Tessellated glyphs in text space, keyed by tolerance bucket.</summary>
    public sealed class GlyphCache
    {
        readonly Dictionary<(string, string, double), (Mesh2 mesh, Region region, Rect bounds)> cache =
            new Dictionary<(string, string, double), (Mesh2, Region, Rect)>();

        public (Mesh2 mesh, Region region, Rect bounds) Get(PdfFont font, string key, double tolEm)
        {
            double bucket = Math.Pow(2, Math.Floor(Math.Log(Math.Max(tolEm, 1e-6), 2)));
            var ck = (font.Resource.Ref, key, bucket);
            lock (cache) if (cache.TryGetValue(ck, out var hit)) return hit;
            var outline = font.Outline(key) ?? new List<PathCmd>();
            var contours = Geo.FillContours(outline, Matrix.Identity, bucket);
            var region = Geo.Normalize(contours, "nonzero");
            var mesh = Geo.RegionTriangles(region);
            var res = (mesh, region, region.Bounds);
            lock (cache) cache[ck] = res;
            return res;
        }
    }

    /// <summary>Lowers one page for one target profile into draw items and composite regions.</summary>
    public sealed class PageLowering
    {
        readonly PageModel page;
        readonly Profile profile;
        readonly string mode;
        readonly Diagnostics diag;
        readonly GlyphCache glyphs;
        readonly bool precomposeTranslucent;
        readonly ClipResolver clips;
        readonly double tol, minW;
        readonly int pno;
        public readonly List<DrawItem> Items = new List<DrawItem>();
        public readonly List<(Rect box, string reason, int op, TruncPoint point)> Unsupported = new List<(Rect, string, int, TruncPoint)>();
        public readonly Dictionary<string, ImageUse> Images = new Dictionary<string, ImageUse>();
        readonly Rect displayRect;
        public readonly Job Job;

        public PageLowering(PageModel p, Profile prof, string mode, Diagnostics d, GlyphCache g, bool precomposeTranslucent, Job job)
        {
            page = p;
            profile = prof;
            this.mode = mode;
            diag = d;
            glyphs = g;
            this.precomposeTranslucent = precomposeTranslucent;
            Job = job;
            pno = p.Index + 1;
            tol = prof.CurveTolerancePt / Math.Max(p.UserUnit, 1e-6);
            minW = prof.MinStrokeWidthPt / Math.Max(p.UserUnit, 1e-6);
            clips = new ClipResolver(p, tol);
            displayRect = new Rect(0, 0, p.DisplayW, p.DisplayH);
        }

        public void Run()
        {
            int k = 0;
            foreach (var op in page.Ops)
            {
                if ((++k & 63) == 0) Job?.Report(Job.Stage, Job.Progress, $"{k}/{page.Ops.Count}");
                try
                {
                    switch (op.Kind)
                    {
                        case OpKind.Path: PathOp(op); break;
                        case OpKind.Glyphs: GlyphOp(op); break;
                        case OpKind.Image: ImageOp(op); break;
                        case OpKind.Shading: ShadingOp(op); break;
                        case OpKind.Unsupported: Mark(op, op.BBox, op.Reason); break;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (CompileException) { throw; }
                catch (Exception e)
                {
                    diag.Warn("geometry.failed", $"operation could not be tessellated ({e.GetType().Name}); precomposited", pno, "precomposite");
                    Mark(op, OpBounds(op), "geometry.failed");
                }
            }
        }

        Rect OpBounds(DrawOp op)
        {
            switch (op.Kind)
            {
                case OpKind.Path: return Geo.Stroke(op.Path, op.Ctm, new StrokeStyle { Width = 0 }, 1, 0).Bounds.Grow(op.Style.Width * op.Ctm.MaxScale * 5 + 1);
                case OpKind.Glyphs: return op.BBox;
                case OpKind.Image: return op.Ctm.TransformRect(new Rect(0, 0, 1, 1));
            }
            return page.CropBox;
        }

        Rect ToDisplay(Rect r) => page.PageToDisplay.TransformRect(r);

        void Mark(DrawOp op, Rect bbox, string reason)
        {
            var clip = clips.Get(op.Clip);
            if (clip.Empty) return;
            var b = bbox.IsEmpty ? clip.Bounds : bbox.Intersect(clip.Bounds);
            if (b.IsEmpty) return;
            var db = ToDisplay(b).Grow(1.0).Intersect(displayRect);
            if (db.IsEmpty) return;
            Unsupported.Add((db, reason, op.Id, op.Point));
        }

        static byte[] Rgba(double r, double g, double b, double a) => new[]
        {
            (byte)(Clamp01(r) * 255 + 0.5), (byte)(Clamp01(g) * 255 + 0.5), (byte)(Clamp01(b) * 255 + 0.5), (byte)(Clamp01(a) * 255 + 0.5),
        };

        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        void Add(Mesh2 m, byte[] color, DrawOp op, double[] uv = null, string texture = null, byte[] colors = null)
        {
            if (m == null || m.IsEmpty) return;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            for (int i = 0; i + 1 < m.V.Count; i += 2)
            {
                if (m.V[i] < x0) x0 = m.V[i];
                if (m.V[i] > x1) x1 = m.V[i];
                if (m.V[i + 1] < y0) y0 = m.V[i + 1];
                if (m.V[i + 1] > y1) y1 = m.V[i + 1];
            }
            var db = ToDisplay(new Rect(x0, y0, x1, y1));
            if (!db.Intersects(displayRect)) return;
            if (colors == null)
            {
                colors = new byte[m.VertexCount * 4];
                for (int i = 0; i < m.VertexCount; i++) Buffer.BlockCopy(color, 0, colors, i * 4, 4);
            }
            Items.Add(new DrawItem { Textured = texture != null, Texture = texture, Mesh = m, Colors = colors, Uv = uv, Order = op.Id, Truncate = op.TruncateAt, DisplayBox = db });
        }

        bool Translucent(DrawOp op, Rect bbox)
        {
            if (precomposeTranslucent)
            {
                Mark(op, bbox, "transparency.linear-blend");
                return true;
            }
            diag.Info("transparency.linear-blend", "translucent content is blended by the GPU; in Linear colour space the result differs slightly from PDF", pno);
            return false;
        }

        Mesh2 Clipped(IEnumerable<double[]> subject, string rule, ClipResolver.Shape clip, Rect bbox)
        {
            if (clip.Empty || bbox.IsEmpty || !bbox.Intersects(clip.Bounds)) return null;
            if (clip.RectShape.HasValue && clip.RectShape.Value.Contains(bbox)) return Geo.FillTriangles(subject, rule, null);
            return Geo.FillTriangles(subject, rule, clip.Region);
        }

        static Rect Bounds(List<double[]> contours)
        {
            var r = new Region();
            r.Contours.AddRange(contours);
            return r.Bounds;
        }

        void PathOp(DrawOp op)
        {
            var clip = clips.Get(op.Clip);
            if (clip.Empty) return;
            if (((op.Fill != null && op.Fill.Alpha < 1) || (op.Stroke != null && op.Stroke.Alpha < 1)) &&
                Translucent(op, Bounds(Geo.FillContours(op.Path, op.Ctm, 1)).Grow(op.Stroke != null ? op.Style.Width * op.Ctm.MaxScale * 5 + 1 : 0)))
                return;
            if (op.Fill != null && op.FillRule != null)
            {
                var subj = Geo.FillContours(op.Path, op.Ctm, tol);
                Add(Clipped(subj, op.FillRule, clip, Bounds(subj)), Rgba(op.Fill.R, op.Fill.G, op.Fill.B, op.Fill.Alpha), op);
            }
            if (op.Stroke != null)
            {
                var region = Geo.Stroke(op.Path, op.Ctm, op.Style, tol, minW);
                Add(Clipped(region.Contours, "nonzero", clip, region.Bounds), Rgba(op.Stroke.R, op.Stroke.G, op.Stroke.B, op.Stroke.Alpha), op);
            }
        }

        void GlyphOp(DrawOp op)
        {
            var clip = clips.Get(op.Clip);
            if (clip.Empty) return;
            if (((op.Fill != null && op.Fill.Alpha < 1) || (op.Stroke != null && op.Stroke.Alpha < 1)) && Translucent(op, op.BBox.Grow(2))) return;
            int mode = op.TextMode;
            if (op.Fill != null && (mode == 0 || mode == 2 || mode == 4 || mode == 6))
            {
                var mesh = new Mesh2();
                foreach (var g in op.Glyphs)
                {
                    var m = g.M.Then(op.Ctm);
                    double sc = m.MeanScale;
                    if (sc <= 0) continue;
                    var (gm, gr, gb) = glyphs.Get(op.Font, g.Key, tol / sc);
                    if (gb.IsEmpty) continue;
                    var pb = m.TransformRect(gb);
                    if (!pb.Intersects(clip.Bounds)) continue;
                    if (clip.RectShape.HasValue && clip.RectShape.Value.Contains(pb)) mesh.Append(gm, m);
                    else
                    {
                        var contours = new List<double[]>();
                        foreach (var c in gr.Contours)
                        {
                            var t = new double[c.Length];
                            for (int i = 0; i + 1 < c.Length; i += 2) m.Apply(c[i], c[i + 1], out t[i], out t[i + 1]);
                            contours.Add(t);
                        }
                        var cm = Clipped(contours, "nonzero", clip, pb);
                        if (cm != null) mesh.Append(cm, Matrix.Identity);
                    }
                }
                Add(mesh, Rgba(op.Fill.R, op.Fill.G, op.Fill.B, op.Fill.Alpha), op);
            }
            if (op.Stroke != null && (mode == 1 || mode == 2 || mode == 5 || mode == 6))
            {
                Region all = new Region();
                foreach (var g in op.Glyphs)
                {
                    var outline = op.Font.Outline(g.Key);
                    if (outline == null || outline.Count == 0) continue;
                    var user = new List<PathCmd>(outline.Count);
                    foreach (var c in outline) user.Add(c.Transform(g.M));
                    all.Contours.AddRange(Geo.Stroke(user, op.Ctm, op.Style, tol, minW).Contours);
                }
                var u = Geo.Normalize(all.Contours, "nonzero");
                Add(Clipped(u.Contours, "nonzero", clip, u.Bounds), Rgba(op.Stroke.R, op.Stroke.G, op.Stroke.B, op.Stroke.Alpha), op);
            }
        }

        void ImageOp(DrawOp op)
        {
            var clip = clips.Get(op.Clip);
            if (clip.Empty) return;
            var m = op.Ctm;
            if (!m.TryInvert(out var inv)) return;
            if (op.Alpha < 1 && Translucent(op, m.TransformRect(new Rect(0, 0, 1, 1)))) return;
            var quad = new double[8];
            m.Apply(0, 0, out quad[0], out quad[1]);
            m.Apply(1, 0, out quad[2], out quad[3]);
            m.Apply(1, 1, out quad[4], out quad[5]);
            m.Apply(0, 1, out quad[6], out quad[7]);
            var subj = new List<double[]> { quad };
            var mesh = Clipped(subj, "nonzero", clip, Bounds(subj));
            if (mesh == null || mesh.IsEmpty) return;
            var uv = new double[mesh.V.Count];
            for (int i = 0; i + 1 < mesh.V.Count; i += 2)
            {
                inv.Apply(mesh.V[i], mesh.V[i + 1], out var u, out var v);
                uv[i] = Clamp01(u);
                uv[i + 1] = Clamp01(v);
            }
            Add(mesh, Rgba(1, 1, 1, op.Alpha), op, uv, op.Image.Key);
            double k = profile.ImageDpi / 72.0 * page.UserUnit;
            double ratio = Math.Max(Math.Sqrt(m.A * m.A + m.B * m.B) * k / Math.Max(1, op.Image.Width),
                                    Math.Sqrt(m.C * m.C + m.D * m.D) * k / Math.Max(1, op.Image.Height));
            if (!Images.TryGetValue(op.Image.Key, out var use)) Images[op.Image.Key] = use = new ImageUse { Image = op.Image };
            use.Ratio = Math.Max(use.Ratio, ratio);
        }

        // ---------------- shadings ----------------
        void ShadingOp(DrawOp op)
        {
            var sh = op.Shading;
            var clip = clips.Get(op.Clip);
            if (clip.Empty) return;
            Region region;
            if (op.Path != null)
            {
                var subj = Geo.Normalize(Geo.FillContours(op.Path, op.Ctm, tol), op.FillRule ?? "nonzero");
                region = Geo.Intersect(subj, clip.Region);
            }
            else region = clip.Region;
            if (sh.BBox.HasValue)
            {
                var b = sh.BBox.Value;
                var bp = new double[8];
                op.ShadingMatrix.Apply(b.X0, b.Y0, out bp[0], out bp[1]);
                op.ShadingMatrix.Apply(b.X1, b.Y0, out bp[2], out bp[3]);
                op.ShadingMatrix.Apply(b.X1, b.Y1, out bp[4], out bp[5]);
                op.ShadingMatrix.Apply(b.X0, b.Y1, out bp[6], out bp[7]);
                region = Geo.Intersect(region, Geo.Normalize(new[] { bp }, "nonzero"));
            }
            if (region.IsEmpty) return;
            if (op.Alpha < 1 && Translucent(op, region.Bounds)) return;
            if (op.Path != null && sh.Background != null)
                Add(Geo.RegionTriangles(region), Rgba(sh.Background[0], sh.Background[1], sh.Background[2], op.Alpha), op);
            if (!op.ShadingMatrix.TryInvert(out var inv)) return;
            if (sh.Type == 2) Axial(op, sh, inv, region);
            else Radial(op, sh, inv, region);
        }

        static void RampColor(ShadingResource sh, double s, byte[] dst, int at, double alpha)
        {
            s = Clamp01(s) * (Shadings.RampSamples - 1);
            int i0 = Math.Min((int)s, Shadings.RampSamples - 2);
            double w = s - i0;
            for (int c = 0; c < 3; c++)
            {
                double v = sh.Ramp[i0 * 3 + c] * (1 - w) + sh.Ramp[(i0 + 1) * 3 + c] * w;
                dst[at + c] = (byte)(Clamp01(v) * 255 + 0.5);
            }
            dst[at + 3] = (byte)(Clamp01(alpha) * 255 + 0.5);
        }

        void Axial(DrawOp op, ShadingResource sh, Matrix inv, Region region)
        {
            double x0 = sh.Coords[0], y0 = sh.Coords[1], x1 = sh.Coords[2], y1 = sh.Coords[3];
            double dx = x1 - x0, dy = y1 - y0, L2 = dx * dx + dy * dy;
            if (L2 <= 0) return;
            double qx = -dy / Math.Sqrt(L2), qy = dx / Math.Sqrt(L2);
            var rb = region.Bounds;
            double smin = double.MaxValue, smax = double.MinValue, pmin = double.MaxValue, pmax = double.MinValue;
            foreach (var (cx, cy) in new[] { (rb.X0, rb.Y0), (rb.X1, rb.Y0), (rb.X1, rb.Y1), (rb.X0, rb.Y1) })
            {
                inv.Apply(cx, cy, out var sx, out var sy);
                double s = ((sx - x0) * dx + (sy - y0) * dy) / L2, p = (sx - x0) * qx + (sy - y0) * qy;
                smin = Math.Min(smin, s); smax = Math.Max(smax, s); pmin = Math.Min(pmin, p); pmax = Math.Max(pmax, p);
            }
            pmin -= 1; pmax += 1;
            double lo = sh.Extend0 ? smin : Math.Max(smin, 0), hi = sh.Extend1 ? smax : Math.Min(smax, 1);
            if (lo >= hi) return;
            op.ShadingMatrix.Apply(x0, y0, out var ax, out var ay);
            op.ShadingMatrix.Apply(x1, y1, out var bx, out var by);
            double phys = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            int n = (int)Math.Min(255, Math.Max(1, Math.Ceiling(phys / 1.5)));
            var cuts = new SortedSet<double> { lo, hi };
            for (int k = 0; k <= n; k++) { double c = (double)k / n; if (c > lo && c < hi) cuts.Add(c); }
            var list = new List<double>(cuts);
            var mesh = new Mesh2();
            var colors = new List<byte>();
            for (int i = 0; i + 1 < list.Count; i++)
            {
                double sa = list[i], sb = list[i + 1];
                var band = new double[8];
                void P(int at, double s, double p) => op.ShadingMatrix.Apply(x0 + s * dx + p * qx, y0 + s * dy + p * qy, out band[at], out band[at + 1]);
                P(0, sa, pmin); P(2, sb, pmin); P(4, sb, pmax); P(6, sa, pmax);
                var m = Geo.FillTriangles(new[] { band }, "nonzero", region);
                if (m.IsEmpty) continue;
                var c = new byte[m.VertexCount * 4];
                for (int v = 0; v < m.VertexCount; v++)
                {
                    inv.Apply(m.V[2 * v], m.V[2 * v + 1], out var sx, out var sy);
                    double s = Math.Max(sa, Math.Min(sb, ((sx - x0) * dx + (sy - y0) * dy) / L2));
                    RampColor(sh, s, c, v * 4, op.Alpha);
                }
                mesh.Append(m, Matrix.Identity);
                colors.AddRange(c);
            }
            Add(mesh, null, op, colors: colors.ToArray());
        }

        void Radial(DrawOp op, ShadingResource sh, Matrix inv, Region region)
        {
            double x0 = sh.Coords[0], y0 = sh.Coords[1], r0 = sh.Coords[2], x1 = sh.Coords[3], y1 = sh.Coords[4], r1 = sh.Coords[5];
            double cdx = x1 - x0, cdy = y1 - y0, dr = r1 - r0;
            var rb = region.Bounds;
            var corners = new List<(double, double)>();
            foreach (var (cx, cy) in new[] { (rb.X0, rb.Y0), (rb.X1, rb.Y0), (rb.X1, rb.Y1), (rb.X0, rb.Y1) })
            {
                inv.Apply(cx, cy, out var sx, out var sy);
                corners.Add((sx, sy));
            }
            bool Covers(double s)
            {
                double cx = x0 + s * cdx, cy = y0 + s * cdy, r = r0 + s * dr, far = 0;
                foreach (var (px, py) in corners) far = Math.Max(far, Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)));
                return r >= far;
            }
            double lo = 0, hi = 1;
            if (sh.Extend0)
            {
                if (dr > 0) lo = -r0 / dr;
                else { double step = 1; while (!Covers(-step) && step < 1e6) step *= 2; lo = -step; }
            }
            if (sh.Extend1)
            {
                if (dr < 0) hi = -r0 / dr;
                else { double step = 1; while (!Covers(1 + step) && step < 1e6) step *= 2; hi = 1 + step; }
            }
            double scale = op.ShadingMatrix.MeanScale;
            double span = (Math.Abs(dr) + Math.Sqrt(cdx * cdx + cdy * cdy)) * (hi - lo) * scale;
            int n = (int)Math.Min(384, Math.Max(8, Math.Ceiling(span / 1.5)));
            double tolSh = tol / Math.Max(scale, 1e-9);
            Region Disc(double s)
            {
                double r = r0 + s * dr;
                if (r <= 0) return new Region();
                var c = Geo.Circle(x0 + s * cdx, y0 + s * cdy, r, tolSh);
                for (int i = 0; i + 1 < c.Length; i += 2) op.ShadingMatrix.Apply(c[i], c[i + 1], out c[i], out c[i + 1]);
                return Geo.Normalize(new[] { c }, "nonzero");
            }
            var cuts = new double[n + 1];
            var discs = new Region[n + 1];
            for (int k = 0; k <= n; k++) { cuts[k] = lo + (hi - lo) * k / n; discs[k] = Disc(cuts[k]); }
            // Later (larger s) bands are painted on top; remove their coverage from earlier bands so the
            // shading is a single non-overlapping layer (correct for translucent shadings).
            var bands = new List<(double, double, Region)>();
            var covered = new Region();
            for (int k = n - 1; k >= 0; k--)
            {
                Job?.Check();
                var ring = Geo.Xor(discs[k + 1], discs[k]);
                if (ring.IsEmpty) continue;
                ring = Geo.Intersect(ring, region);
                if (!covered.IsEmpty) ring = Geo.Difference(ring, covered);
                if (ring.IsEmpty) continue;
                covered = Geo.Union(covered, ring);
                bands.Add((cuts[k], cuts[k + 1], ring));
            }
            bands.Reverse();
            var mesh = new Mesh2();
            var colors = new List<byte>();
            foreach (var (sa, sb, ring) in bands)
            {
                var m = Geo.RegionTriangles(ring);
                if (m.IsEmpty) continue;
                var c = new byte[m.VertexCount * 4];
                for (int v = 0; v < m.VertexCount; v++)
                {
                    inv.Apply(m.V[2 * v], m.V[2 * v + 1], out var px, out var py);
                    double s = RadialS(px - x0, py - y0, cdx, cdy, r0, dr, sa, sb);
                    RampColor(sh, s, c, v * 4, op.Alpha);
                }
                mesh.Append(m, Matrix.Identity);
                colors.AddRange(c);
            }
            Add(mesh, null, op, colors: colors.ToArray());
        }

        static double RadialS(double pdx, double pdy, double cdx, double cdy, double r0, double dr, double sa, double sb)
        {
            double a = cdx * cdx + cdy * cdy - dr * dr;
            double b = -2 * (pdx * cdx + pdy * cdy + r0 * dr);
            double c = pdx * pdx + pdy * pdy - r0 * r0;
            double mid = (sa + sb) / 2, s;
            if (Math.Abs(a) < 1e-12) s = Math.Abs(b) > 1e-12 ? -c / b : mid;
            else
            {
                double disc = b * b - 4 * a * c;
                if (disc < 0) s = mid;
                else
                {
                    double sq = Math.Sqrt(disc), s1 = (-b + sq) / (2 * a), s2 = (-b - sq) / (2 * a);
                    double hiRoot = Math.Max(s1, s2), loRoot = Math.Min(s1, s2);
                    s = r0 + hiRoot * dr >= 0 ? hiRoot : loRoot;
                }
            }
            return Math.Max(sa, Math.Min(sb, s));
        }

        // ---------------- composites ----------------
        /// <summary>
        /// Merges unsupported areas into rectangles. Each rectangle is rendered by PDFium from the page
        /// content truncated after its last unsupported operation, and drawn at that painting position:
        /// content painted later (e.g. text over an effect) stays native on top.
        /// </summary>
        public List<CompositeRegion> Composites()
        {
            var output = new List<CompositeRegion>();
            if (Unsupported.Count == 0) return output;
            if (mode == "strict")
            {
                var reasons = new SortedSet<string>();
                foreach (var u in Unsupported) reasons.Add(u.reason);
                throw new CompileException("strict.unsupported", $"page {pno}: {Unsupported.Count} operation(s) cannot be reproduced natively ({string.Join(", ", reasons)})");
            }
            if (mode == "page-fallback")
            {
                var r = new CompositeRegion { Rect = displayRect, Order = double.MaxValue };
                foreach (var u in Unsupported) { r.OpIds.Add(u.op); if (!r.Reasons.Contains(u.reason)) r.Reasons.Add(u.reason); }
                diag.Warn("fallback.page", "page precomposited as a whole (page-fallback mode)", pno, "page-raster");
                output.Add(r);
                return output;
            }
            foreach (var u in Unsupported)
            {
                var r = new CompositeRegion { Rect = u.box, After = u.point, Before = u.point, FirstOp = u.op, LastOp = u.op, Order = u.op + 0.5 };
                r.OpIds.Add(u.op);
                r.Reasons.Add(u.reason);
                output.Add(r);
            }
            // Each delta composite is drawn at its own painting position, so overlapping composites do not
            // need merging. Only merge overlapping ones with no native content painted between them
            // (fewer renders, identical result); merging across native content would rasterise that content.
            bool NativeBetween(CompositeRegion a, CompositeRegion b, Rect area)
            {
                int lo = Math.Min(a.LastOp, b.LastOp), hi = Math.Max(a.FirstOp, b.FirstOp);
                foreach (var it in Items)
                    if (it.Order > lo && it.Order < hi && it.DisplayBox.Intersects(area)) return true;
                return false;
            }
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < output.Count && !merged; i++)
                    for (int j = i + 1; j < output.Count && !merged; j++)
                    {
                        if (!output[i].Rect.Grow(0.5).Intersects(output[j].Rect)) continue;
                        if (NativeBetween(output[i], output[j], output[i].Rect.Union(output[j].Rect))) continue;
                        var a = output[i];
                        var b = output[j];
                        a.Rect = a.Rect.Union(b.Rect);
                        if (b.LastOp > a.LastOp) { a.LastOp = b.LastOp; a.After = b.After; }
                        if (b.FirstOp < a.FirstOp) { a.FirstOp = b.FirstOp; a.Before = b.Before; }
                        a.Order = Math.Max(a.Order, b.Order);
                        a.OpIds.AddRange(b.OpIds);
                        foreach (var rs in b.Reasons) if (!a.Reasons.Contains(rs)) a.Reasons.Add(rs);
                        output.RemoveAt(j);
                        merged = true;
                    }
            }
            output.Sort((a, b) => a.Order.CompareTo(b.Order));
            double area = 0;
            foreach (var r in output) area += r.Rect.Width * r.Rect.Height;
            diag.Info("fallback.regions", $"{output.Count} precomposited region(s) covering {100 * area / Math.Max(displayRect.Width * displayRect.Height, 1e-9):0.0}% of the page", pno, "region-raster");
            return output;
        }
    }
}
