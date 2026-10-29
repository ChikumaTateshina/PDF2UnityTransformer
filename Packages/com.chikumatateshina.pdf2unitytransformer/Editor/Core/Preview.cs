using System;
using System.Collections.Generic;
using PDF2Unity.Core.Content;
using PDF2Unity.Core.Geometry;
using PDF2Unity.Core.Graphics;
using PDF2Unity.Core.Util;

namespace PDF2Unity.Core
{
    /// <summary>
    /// Interpreted pages kept in memory, so the curve detail can be tried (and its triangle count seen) before
    /// converting: only the tessellation runs again when the detail changes.
    /// </summary>
    public sealed class PreviewDocument
    {
        readonly CompileOptions options;
        readonly List<PageModel> pages;
        readonly GlyphCache glyphs = new GlyphCache();

        PreviewDocument(CompileOptions o, List<PageModel> pages)
        {
            options = o;
            this.pages = pages;
        }

        public int PageCount => pages.Count;
        public int PageNumber(int k) => pages[k].Index + 1;

        public static PreviewDocument Load(CompileOptions o, Job job)
        {
            var pages = Compiler.Frontend(o, job, new Diagnostics(), out _, out _, out _);
            return new PreviewDocument(o, pages);
        }

        /// <summary>Tessellates page <paramref name="k"/> for <paramref name="prof"/> at <paramref name="curveDetail"/>.</summary>
        public PreviewPage Lower(int k, Profile prof, double curveDetail, Job job)
        {
            var pm = pages[k];
            PageLowering low;
            lock (glyphs) // one lowering at a time (the glyph cache is shared)
            {
                low = new PageLowering(pm, prof, options.Mode, new Diagnostics(), glyphs, options.PrecomposeTranslucent, job, curveDetail, options.TransparentCutoff);
                low.Run();
            }
            var comps = low.Composites();
            var p = new PreviewPage { Width = pm.DisplayW, Height = pm.DisplayH };
            var pos = new List<float>();
            var col = new List<byte>();
            var idx = new List<int>();
            foreach (var it in low.Items)
            {
                // Items completely under a page-wide composite are not drawn natively.
                bool covered = false;
                foreach (var r in comps)
                    if (!r.Partial && r.After == null && r.Rect.Contains(it.DisplayBox)) { covered = true; break; }
                if (covered) continue;
                int baseV = pos.Count / 2;
                int n = it.Mesh.VertexCount;
                var dx = new double[n];
                var dy = new double[n];
                for (int v = 0; v < n; v++)
                {
                    pm.PageToDisplay.Apply(it.Mesh.V[2 * v], it.Mesh.V[2 * v + 1], out dx[v], out dy[v]);
                    pos.Add((float)dx[v]); pos.Add((float)dy[v]);
                    if (it.Textured) { col.Add(200); col.Add(204); col.Add(212); col.Add(255); } // images: placeholder grey
                    else for (int c = 0; c < 4; c++) col.Add(it.Colors[4 * v + c]);
                }
                for (int q = 0; q + 2 < it.Mesh.I.Count; q += 3)
                {
                    int a = it.Mesh.I[q], b = it.Mesh.I[q + 1], c = it.Mesh.I[q + 2];
                    double cross = (dx[b] - dx[a]) * (dy[c] - dy[a]) - (dy[b] - dy[a]) * (dx[c] - dx[a]);
                    if (Math.Abs(cross) < 1e-10) continue;
                    idx.Add(a + baseV); idx.Add(b + baseV); idx.Add(c + baseV);
                }
            }
            foreach (var r in comps) p.Composites.Add(r.Rect);
            p.Positions = pos.ToArray();
            p.Colors = col.ToArray();
            p.Indices = idx.ToArray();
            p.Triangles = idx.Count / 3 + 2 * comps.Count;
            return p;
        }
    }

    /// <summary>One tessellated page in display space (points, y down).</summary>
    public sealed class PreviewPage
    {
        public double Width, Height;
        public float[] Positions;
        public byte[] Colors;
        public int[] Indices;
        public long Triangles;
        public readonly List<Rect> Composites = new List<Rect>();
    }
}
