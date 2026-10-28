using System;
using System.Collections.Generic;
using PDF2Unity.Core.Content;
using PDF2Unity.Core.Graphics;
using PDF2Unity.ThirdParty.LibTess;

namespace PDF2Unity.Core.Geometry
{
    /// <summary>
    /// A region: list of closed contours (x0,y0,x1,y1,...). After <see cref="Geo.Normalize"/> the contours
    /// are non-overlapping with interior winding +1, so regions can be combined with winding rules.
    /// </summary>
    public sealed class Region
    {
        public readonly List<double[]> Contours = new List<double[]>();
        public bool IsEmpty => Contours.Count == 0;

        public Rect Bounds
        {
            get
            {
                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                foreach (var c in Contours)
                    for (int i = 0; i + 1 < c.Length; i += 2)
                    {
                        if (c[i] < x0) x0 = c[i];
                        if (c[i] > x1) x1 = c[i];
                        if (c[i + 1] < y0) y0 = c[i + 1];
                        if (c[i + 1] > y1) y1 = c[i + 1];
                    }
                return x0 == double.MaxValue ? default : new Rect(x0, y0, x1, y1);
            }
        }
    }

    /// <summary>Triangle list (page space).</summary>
    public sealed class Mesh2
    {
        public readonly List<double> V = new List<double>(); // x,y pairs
        public readonly List<int> I = new List<int>();
        public int VertexCount => V.Count / 2;
        public bool IsEmpty => I.Count == 0;

        public void Append(Mesh2 o, Matrix m)
        {
            int baseIdx = VertexCount;
            for (int i = 0; i + 1 < o.V.Count; i += 2)
            {
                m.Apply(o.V[i], o.V[i + 1], out var x, out var y);
                V.Add(x);
                V.Add(y);
            }
            foreach (var k in o.I) I.Add(k + baseIdx);
        }
    }

    public static class Geo
    {
        // ---------------- flattening ----------------
        /// <summary>Path commands -> polylines (closed flag) in the space of <paramref name="m"/>.</summary>
        public static List<(List<double> pts, bool closed)> Flatten(List<PathCmd> cmds, Matrix m, double tol)
        {
            var output = new List<(List<double>, bool)>();
            List<double> pts = null;
            double cx = 0, cy = 0, sx = 0, sy = 0;
            tol = Math.Max(tol, 1e-9);
            foreach (var c in cmds)
            {
                switch (c.Op)
                {
                    case PathOp.Move:
                        if (pts != null && pts.Count > 0) output.Add((pts, false));
                        m.Apply(c.X3, c.Y3, out cx, out cy);
                        sx = cx; sy = cy;
                        pts = new List<double> { cx, cy };
                        break;
                    case PathOp.Line:
                        if (pts == null) pts = new List<double> { cx, cy };
                        m.Apply(c.X3, c.Y3, out cx, out cy);
                        pts.Add(cx); pts.Add(cy);
                        break;
                    case PathOp.Cubic:
                        {
                            if (pts == null) pts = new List<double> { cx, cy };
                            m.Apply(c.X1, c.Y1, out var x1, out var y1);
                            m.Apply(c.X2, c.Y2, out var x2, out var y2);
                            m.Apply(c.X3, c.Y3, out var x3, out var y3);
                            double ddx = Math.Max(Math.Abs(cx - 2 * x1 + x2), Math.Abs(x1 - 2 * x2 + x3));
                            double ddy = Math.Max(Math.Abs(cy - 2 * y1 + y2), Math.Abs(y1 - 2 * y2 + y3));
                            double dd = Math.Sqrt(ddx * ddx + ddy * ddy);
                            int n = dd > 0 ? (int)Math.Ceiling(Math.Sqrt(0.75 * dd / tol)) : 1;
                            n = Math.Max(1, Math.Min(n, 512));
                            for (int i = 1; i <= n; i++)
                            {
                                double t = (double)i / n, u = 1 - t;
                                double w0 = u * u * u, w1 = 3 * u * u * t, w2 = 3 * u * t * t, w3 = t * t * t;
                                pts.Add(w0 * cx + w1 * x1 + w2 * x2 + w3 * x3);
                                pts.Add(w0 * cy + w1 * y1 + w2 * y2 + w3 * y3);
                            }
                            cx = x3; cy = y3;
                        }
                        break;
                    case PathOp.Close:
                        if (pts != null && pts.Count > 0) output.Add((pts, true));
                        pts = null;
                        cx = sx; cy = sy;
                        break;
                }
            }
            if (pts != null && pts.Count > 0) output.Add((pts, false));
            return output;
        }

        // ---------------- tessellation helpers ----------------
        static void AddContour(Tess t, double[] c, bool reverse)
        {
            int n = c.Length / 2;
            if (n < 3) return;
            var vs = new ContourVertex[n];
            for (int i = 0; i < n; i++)
            {
                int k = reverse ? n - 1 - i : i;
                vs[i] = new ContourVertex(new Vec3(c[2 * k], c[2 * k + 1], 0));
            }
            t.AddContour(vs, ContourOrientation.Original);
        }

        static Region Boundary(Tess t)
        {
            var r = new Region();
            if (t.Elements == null) return r;
            for (int e = 0; e < t.ElementCount; e++)
            {
                int start = t.Elements[2 * e], count = t.Elements[2 * e + 1];
                if (count < 3) continue;
                var c = new double[count * 2];
                for (int i = 0; i < count; i++)
                {
                    var p = t.Vertices[start + i].Position;
                    c[2 * i] = p.X;
                    c[2 * i + 1] = p.Y;
                }
                r.Contours.Add(c);
            }
            return r;
        }

        static Mesh2 Triangles(Tess t)
        {
            var m = new Mesh2();
            if (t.Elements == null) return m;
            for (int i = 0; i < t.VertexCount; i++)
            {
                m.V.Add(t.Vertices[i].Position.X);
                m.V.Add(t.Vertices[i].Position.Y);
            }
            for (int e = 0; e < t.ElementCount; e++)
            {
                int a = t.Elements[3 * e], b = t.Elements[3 * e + 1], c = t.Elements[3 * e + 2];
                if (a < 0 || b < 0 || c < 0) continue;
                m.I.Add(a); m.I.Add(b); m.I.Add(c);
            }
            return m;
        }

        static WindingRule Rule(string r) => r == "evenodd" ? WindingRule.EvenOdd : WindingRule.NonZero;

        static Tess NewTess() => new Tess { NoEmptyPolygons = true };

        /// <summary>Fixed normal: otherwise LibTess flips orientation when the summed contour area is negative,
        /// which inverts the winding numbers the boolean operations rely on.</summary>
        static readonly Vec3 Up = new Vec3(0, 0, 1);

        /// <summary>Resolve a fill rule: arbitrary contours -> normalized region.</summary>
        public static Region Normalize(IEnumerable<double[]> contours, string rule)
        {
            var t = NewTess();
            int n = 0;
            foreach (var c in contours) { AddContour(t, c, false); n++; }
            if (n == 0) return new Region();
            t.Tessellate(Rule(rule), ElementType.BoundaryContours, 3, null, Up);
            return Orient(Boundary(t));
        }

        /// <summary>LibTess boundary contours are made counter-clockwise (outer) / clockwise (holes) by a final pass.</summary>
        static Region Orient(Region r)
        {
            // Re-tessellate as triangles would lose holes; instead fix global orientation using winding of the
            // largest contour: LibTess emits interiors consistently, we only ensure the outer orientation is CCW.
            double best = 0;
            foreach (var c in r.Contours)
            {
                double a = Area(c);
                if (Math.Abs(a) > Math.Abs(best)) best = a;
            }
            if (best < 0)
                for (int i = 0; i < r.Contours.Count; i++) r.Contours[i] = Reverse(r.Contours[i]);
            return r;
        }

        public static double Area(double[] c)
        {
            double a = 0;
            int n = c.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++) a += c[2 * j] * c[2 * i + 1] - c[2 * i] * c[2 * j + 1];
            return a / 2;
        }

        static double[] Reverse(double[] c)
        {
            int n = c.Length / 2;
            var r = new double[c.Length];
            for (int i = 0; i < n; i++) { r[2 * i] = c[2 * (n - 1 - i)]; r[2 * i + 1] = c[2 * (n - 1 - i) + 1]; }
            return r;
        }

        /// <summary>Triangulate (subject with rule) ∩ clip region (clip null: no clipping).</summary>
        public static Mesh2 FillTriangles(IEnumerable<double[]> subject, string rule, Region clip)
        {
            var t = NewTess();
            int n = 0;
            if (clip == null)
            {
                foreach (var c in subject) { AddContour(t, c, false); n++; }
                if (n == 0) return new Mesh2();
                t.Tessellate(Rule(rule), ElementType.Polygons, 3, null, Up);
                return Triangles(t);
            }
            var norm = Normalize(subject, rule);
            if (norm.IsEmpty || clip.IsEmpty) return new Mesh2();
            foreach (var c in norm.Contours) AddContour(t, c, false);
            foreach (var c in clip.Contours) AddContour(t, c, false);
            t.Tessellate(WindingRule.AbsGeqTwo, ElementType.Polygons, 3, null, Up);
            return Triangles(t);
        }

        public static Mesh2 RegionTriangles(Region r)
        {
            var t = NewTess();
            if (r.IsEmpty) return new Mesh2();
            foreach (var c in r.Contours) AddContour(t, c, false);
            t.Tessellate(WindingRule.NonZero, ElementType.Polygons, 3, null, Up);
            return Triangles(t);
        }

        public static Region Intersect(Region a, Region b)
        {
            if (a.IsEmpty || b.IsEmpty) return new Region();
            var t = NewTess();
            foreach (var c in a.Contours) AddContour(t, c, false);
            foreach (var c in b.Contours) AddContour(t, c, false);
            t.Tessellate(WindingRule.AbsGeqTwo, ElementType.BoundaryContours, 3, null, Up);
            return Orient(Boundary(t));
        }

        public static Region Union(Region a, Region b)
        {
            if (a.IsEmpty) return b;
            if (b.IsEmpty) return a;
            var t = NewTess();
            foreach (var c in a.Contours) AddContour(t, c, false);
            foreach (var c in b.Contours) AddContour(t, c, false);
            t.Tessellate(WindingRule.Positive, ElementType.BoundaryContours, 3, null, Up);
            return Orient(Boundary(t));
        }

        public static Region Difference(Region a, Region b)
        {
            if (a.IsEmpty || b.IsEmpty) return a;
            var t = NewTess();
            foreach (var c in a.Contours) AddContour(t, c, false);
            foreach (var c in b.Contours) AddContour(t, c, true);
            t.Tessellate(WindingRule.Positive, ElementType.BoundaryContours, 3, null, Up);
            return Orient(Boundary(t));
        }

        public static Region Xor(Region a, Region b)
        {
            var t = NewTess();
            foreach (var c in a.Contours) AddContour(t, c, false);
            foreach (var c in b.Contours) AddContour(t, c, false);
            t.Tessellate(WindingRule.EvenOdd, ElementType.BoundaryContours, 3, null, Up);
            return Orient(Boundary(t));
        }

        public static Region RectRegion(Rect r) => new Region { Contours = { new[] { r.X0, r.Y0, r.X1, r.Y0, r.X1, r.Y1, r.X0, r.Y1 } } };

        public static List<double[]> FillContours(List<PathCmd> path, Matrix ctm, double tol)
        {
            var o = new List<double[]>();
            foreach (var (pts, _) in Flatten(path, ctm, tol)) if (pts.Count >= 6) o.Add(pts.ToArray());
            return o;
        }

        public static double[] Circle(double cx, double cy, double r, double tol)
        {
            int n = Math.Max(8, Math.Min(720, (int)Math.Ceiling(Math.PI / Math.Acos(Math.Max(-1, 1 - tol / Math.Max(r, 1e-9))))));
            var c = new double[2 * n];
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                c[2 * i] = cx + r * Math.Cos(a);
                c[2 * i + 1] = cy + r * Math.Sin(a);
            }
            return c;
        }

        // ---------------- dashes ----------------
        public static List<List<double>> Dash(List<double> pts, bool closed, double[] dashes, double phase)
        {
            var pieces = new List<List<double>>();
            var p = new List<double>(pts);
            if (closed && p.Count >= 4 && (p[0] != p[p.Count - 2] || p[1] != p[p.Count - 1])) { p.Add(p[0]); p.Add(p[1]); }
            double total = 0;
            foreach (var dsh in dashes) total += dsh;
            if (total <= 0 || p.Count < 4) { pieces.Add(p); return pieces; }
            var pattern = new List<double>(dashes);
            if (pattern.Count % 2 == 1) pattern.AddRange(dashes);
            double period = 0;
            foreach (var v in pattern) period += v;
            phase = ((phase % period) + period) % period;
            int idx = 0;
            while (phase >= pattern[idx]) { phase -= pattern[idx]; idx = (idx + 1) % pattern.Count; }
            double remaining = pattern[idx] - phase;
            bool on = idx % 2 == 0;
            var cur = on ? new List<double> { p[0], p[1] } : null;
            int guard = 0;
            for (int k = 0; k + 3 < p.Count; k += 2)
            {
                double x0 = p[k], y0 = p[k + 1], x1 = p[k + 2], y1 = p[k + 3];
                double seg = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)), pos = 0;
                while (seg - pos > remaining)
                {
                    if (++guard > 2_000_000) return pieces;
                    pos += remaining;
                    double t = pos / seg, qx = x0 + (x1 - x0) * t, qy = y0 + (y1 - y0) * t;
                    if (on) { cur.Add(qx); cur.Add(qy); pieces.Add(cur); cur = null; }
                    else cur = new List<double> { qx, qy };
                    on = !on;
                    idx = (idx + 1) % pattern.Count;
                    remaining = pattern[idx];
                    if (remaining <= 0 && on) { pieces.Add(new List<double> { qx, qy }); }
                }
                remaining -= seg - pos;
                if (on && cur != null) { cur.Add(x1); cur.Add(y1); }
            }
            if (on && cur != null && cur.Count >= 2) pieces.Add(cur);
            return pieces;
        }

        // ---------------- strokes ----------------
        /// <summary>Stroke outline in page space (normalized region). Offsetting happens in user space.</summary>
        public static Region Stroke(List<PathCmd> path, Matrix ctm, StrokeStyle style, double tolPage, double minWidthPage)
        {
            double s = Math.Max(ctm.MaxScale, 1e-12);
            double tolUser = tolPage / s;
            double width = style.Width;
            double mean = ctm.MeanScale > 0 ? ctm.MeanScale : s;
            if (width * mean < minWidthPage) width = minWidthPage / Math.Max(mean, 1e-12);
            double h = width / 2;
            var subs = Flatten(path, Matrix.Identity, tolUser);
            var lines = new List<(List<double>, bool)>();
            bool dashed = false;
            if (style.Dash != null && style.Dash.Length > 0)
            {
                double sum = 0;
                foreach (var v in style.Dash) sum += v;
                dashed = sum > 0;
            }
            foreach (var (pts, closed) in subs)
            {
                var clean = Dedup(pts);
                if (dashed) foreach (var piece in Dash(clean, closed, style.Dash, style.DashPhase)) lines.Add((piece, false));
                else lines.Add((clean, closed));
            }
            var polys = new List<double[]>();
            foreach (var (pts, closed) in lines) StrokePolyline(pts, closed, h, style, tolUser, polys);
            if (polys.Count == 0) return new Region();
            // Transform to page space, then union.
            var t = NewTess();
            foreach (var poly in polys)
            {
                var c = new double[poly.Length];
                for (int i = 0; i + 1 < poly.Length; i += 2) ctm.Apply(poly[i], poly[i + 1], out c[i], out c[i + 1]);
                bool rev = Area(c) < 0;
                AddContour(t, c, rev);
            }
            t.Tessellate(WindingRule.Positive, ElementType.BoundaryContours, 3, null, Up);
            return Orient(Boundary(t));
        }

        static List<double> Dedup(List<double> p)
        {
            var o = new List<double>(p.Count);
            for (int i = 0; i + 1 < p.Count; i += 2)
            {
                if (o.Count >= 2 && Math.Abs(o[o.Count - 2] - p[i]) < 1e-9 && Math.Abs(o[o.Count - 1] - p[i + 1]) < 1e-9) continue;
                o.Add(p[i]);
                o.Add(p[i + 1]);
            }
            return o;
        }

        static void StrokePolyline(List<double> p, bool closed, double h, StrokeStyle st, double tol, List<double[]> polys)
        {
            int n = p.Count / 2;
            if (closed && n >= 2 && Math.Abs(p[0] - p[2 * n - 2]) < 1e-9 && Math.Abs(p[1] - p[2 * n - 1]) < 1e-9) n--;
            if (n == 1 || (n >= 1 && closed && n < 2))
            {
                double x = p[0], y = p[1];
                if (st.Cap == 1) polys.Add(Circle(x, y, h, tol));
                else if (st.Cap == 2) polys.Add(new[] { x - h, y - h, x + h, y - h, x + h, y + h, x - h, y + h });
                return;
            }
            if (n < 2) return;
            int segs = closed ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                int j = (i + 1) % n;
                double x0 = p[2 * i], y0 = p[2 * i + 1], x1 = p[2 * j], y1 = p[2 * j + 1];
                double dx = x1 - x0, dy = y1 - y0, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-12) continue;
                double nx = -dy / len * h, ny = dx / len * h;
                double ex0 = 0, ey0 = 0, ex1 = 0, ey1 = 0;
                if (!closed && st.Cap == 2)
                {
                    if (i == 0) { ex0 = -dx / len * h; ey0 = -dy / len * h; }
                    if (i == segs - 1) { ex1 = dx / len * h; ey1 = dy / len * h; }
                }
                polys.Add(new[]
                {
                    x0 + ex0 + nx, y0 + ey0 + ny, x0 + ex0 - nx, y0 + ey0 - ny,
                    x1 + ex1 - nx, y1 + ey1 - ny, x1 + ex1 + nx, y1 + ey1 + ny,
                });
            }
            // joins
            int firstJoin = closed ? 0 : 1, lastJoin = closed ? n - 1 : n - 2;
            for (int i = firstJoin; i <= lastJoin; i++)
            {
                int a = (i - 1 + n) % n, c = (i + 1) % n;
                Join(p[2 * a], p[2 * a + 1], p[2 * i], p[2 * i + 1], p[2 * c], p[2 * c + 1], h, st, tol, polys);
            }
            if (!closed && st.Cap == 1)
            {
                polys.Add(Circle(p[0], p[1], h, tol));
                polys.Add(Circle(p[2 * n - 2], p[2 * n - 1], h, tol));
            }
        }

        static void Join(double ax, double ay, double vx, double vy, double bx, double by, double h, StrokeStyle st, double tol, List<double[]> polys)
        {
            double d0x = vx - ax, d0y = vy - ay, d1x = bx - vx, d1y = by - vy;
            double l0 = Math.Sqrt(d0x * d0x + d0y * d0y), l1 = Math.Sqrt(d1x * d1x + d1y * d1y);
            if (l0 < 1e-12 || l1 < 1e-12) return;
            d0x /= l0; d0y /= l0; d1x /= l1; d1y /= l1;
            double cross = d0x * d1y - d0y * d1x;
            double dot = d0x * d1x + d0y * d1y;
            if (Math.Abs(cross) < 1e-12 && dot > 0) return; // collinear
            if (st.Join == 1) { polys.Add(Circle(vx, vy, h, tol)); return; }
            double sgn = cross > 0 ? -1 : 1; // outer side
            double n0x = -d0y * h * sgn, n0y = d0x * h * sgn, n1x = -d1y * h * sgn, n1y = d1x * h * sgn;
            double px = vx + n0x, py = vy + n0y, qx = vx + n1x, qy = vy + n1y;
            if (st.Join == 0)
            {
                double theta = Math.Acos(Math.Max(-1, Math.Min(1, -dot))); // angle between segments
                double ratio = 1 / Math.Max(Math.Sin(theta / 2), 1e-12);
                if (ratio <= st.MiterLimit)
                {
                    // miter tip: intersection of the two offset lines
                    double denom = d0x * d1y - d0y * d1x;
                    if (Math.Abs(denom) > 1e-12)
                    {
                        double t = ((qx - px) * d1y - (qy - py) * d1x) / denom;
                        double tx = px + d0x * t, ty = py + d0y * t;
                        polys.Add(new[] { vx, vy, px, py, tx, ty, qx, qy });
                        return;
                    }
                }
            }
            polys.Add(new[] { vx, vy, px, py, qx, qy });
        }
    }
}
