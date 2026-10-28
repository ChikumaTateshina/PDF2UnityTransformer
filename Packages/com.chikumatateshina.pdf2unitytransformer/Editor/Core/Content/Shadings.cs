using PDF2Unity.Core.Graphics;
using PDF2Unity.Core.Pdf;

namespace PDF2Unity.Core.Content
{
    /// <summary>Axial (2) and radial (3) shadings sampled into a 256-entry sRGB ramp; other types are precomposited.</summary>
    public static class Shadings
    {
        public const int RampSamples = 256;

        public static ShadingResource Load(FrontendContext ctx, object obj, PdfDict resources)
        {
            var d = obj as PdfDict ?? (obj as PdfStream)?.Dict;
            if (d == null) throw new UnsupportedException("shading.missing", "shading object missing");
            var key = d.Key;
            lock (ctx.Shadings)
                if (ctx.Shadings.TryGetValue(key, out var hit))
                    return hit ?? throw new UnsupportedException("shading.type", "shading type is precomposited");
            ShadingResource res = null;
            try { res = Build(ctx, d, resources); }
            finally { lock (ctx.Shadings) ctx.Shadings[key] = res; }
            return res;
        }

        static ShadingResource Build(FrontendContext ctx, PdfDict d, PdfDict resources)
        {
            int st = d.GetInt("ShadingType");
            if (st != 2 && st != 3) throw new UnsupportedException("shading.type", $"shading type {st} is precomposited");
            var cs = ctx.ColorSpaces.Resolve(d.Get("ColorSpace"), resources);
            var coords = d.GetArray("Coords")?.ToDoubles();
            if (coords == null || coords.Length != (st == 2 ? 4 : 6)) throw new UnsupportedException("shading.coords", "invalid shading coordinates");
            var domain = d.GetArray("Domain")?.ToDoubles() ?? new double[] { 0, 1 };
            var ext = d.GetArray("Extend");
            var fnObj = d.Get("Function") ?? throw new UnsupportedException("shading.function", "shading without function");
            var fn = PdfFunction.Load(fnObj, ctx.Limits);
            var ramp = new float[RampSamples * 3];
            for (int i = 0; i < RampSamples; i++)
            {
                double t = domain[0] + (domain[1] - domain[0]) * i / (RampSamples - 1);
                cs.ToRgb(fn.Eval(new[] { t }), out var r, out var g, out var b);
                ramp[i * 3] = (float)r;
                ramp[i * 3 + 1] = (float)g;
                ramp[i * 3 + 2] = (float)b;
            }
            double[] bg = null;
            var bga = d.GetArray("Background")?.ToDoubles();
            if (bga != null) { cs.ToRgb(bga, out var r, out var g, out var b); bg = new[] { r, g, b }; }
            var bb = d.GetArray("BBox")?.ToDoubles();
            return new ShadingResource
            {
                Type = st, Coords = coords, Domain = domain, Ramp = ramp, Background = bg,
                Extend0 = ext != null && ext.Count > 0 && ext[0] is bool e0 && e0,
                Extend1 = ext != null && ext.Count > 1 && ext[1] is bool e1 && e1,
                BBox = bb != null && bb.Length == 4 ? new Rect(bb[0], bb[1], bb[2], bb[3]) : (Rect?)null,
            };
        }
    }

    /// <summary>Default visibility of optional content groups (/OCProperties /D).</summary>
    public sealed class OptionalContent
    {
        readonly System.Collections.Generic.HashSet<object> off = new System.Collections.Generic.HashSet<object>();

        public OptionalContent(PdfFile file, Diagnostics diag)
        {
            var ocp = file.Root?.GetDict("OCProperties");
            var d = ocp?.GetDict("D");
            if (d == null) return;
            bool baseOn = d.GetName("BaseState", "ON") != "OFF";
            if (baseOn)
            {
                var a = d.GetArray("OFF");
                for (int i = 0; a != null && i < a.Count; i++) if (a.Dict(i) != null) off.Add(a.Dict(i).Key);
            }
            else
            {
                var all = ocp.GetArray("OCGs");
                var on = new System.Collections.Generic.HashSet<object>();
                var oa = d.GetArray("ON");
                for (int i = 0; oa != null && i < oa.Count; i++) if (oa.Dict(i) != null) on.Add(oa.Dict(i).Key);
                for (int i = 0; all != null && i < all.Count; i++) if (all.Dict(i) != null && !on.Contains(all.Dict(i).Key)) off.Add(all.Dict(i).Key);
            }
            if (d.Has("AS")) diag.Info("oc.usage", "optional content usage dictionaries ignored; default state used");
        }

        public bool Visible(PdfDict oc)
        {
            if (oc == null) return true;
            if (oc.GetName("Type", "OCG") == "OCMD")
            {
                var g = oc.Get("OCGs");
                var states = new System.Collections.Generic.List<bool>();
                if (g is PdfDict gd) states.Add(!off.Contains(gd.Key));
                else if (g is PdfArray ga) for (int i = 0; i < ga.Count; i++) if (ga.Dict(i) != null) states.Add(!off.Contains(ga.Dict(i).Key));
                if (states.Count == 0) return true;
                switch (oc.GetName("P", "AnyOn"))
                {
                    case "AllOn": return states.TrueForAll(s => s);
                    case "AnyOff": return !states.TrueForAll(s => s);
                    case "AllOff": return states.TrueForAll(s => !s);
                    default: return states.Exists(s => s);
                }
            }
            return !off.Contains(oc.Key);
        }
    }
}
