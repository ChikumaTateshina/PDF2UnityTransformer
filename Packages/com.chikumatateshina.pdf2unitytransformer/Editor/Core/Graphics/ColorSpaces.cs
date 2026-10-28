using System;
using System.Collections.Generic;
using PDF2Unity.Core.Pdf;
using PDF2Unity.Core.Render;

namespace PDF2Unity.Core.Graphics
{
    /// <summary>
    /// Colour spaces for vector colours (images are decoded by PDFium). Results are display sRGB 0..1.
    /// DeviceCMYK uses the PDFium-derived table so vector colours match images and composites.
    /// ICC profiles are approximated by their device family (RGB/Gray/CMYK) and reported.
    /// </summary>
    public abstract class ColorSpace
    {
        public abstract string Family { get; }
        public abstract int N { get; }
        public abstract void ToRgb(double[] c, out double r, out double g, out double b);
        public virtual double[] Initial() => new double[N];
        public virtual string Describe() => Family;

        protected static double C01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
        protected static double At(double[] c, int i) => c != null && i < c.Length ? c[i] : 0;
    }

    public sealed class DeviceGray : ColorSpace
    {
        public override string Family => "DeviceGray";
        public override int N => 1;
        public override void ToRgb(double[] c, out double r, out double g, out double b) { r = g = b = C01(At(c, 0)); }
    }

    public sealed class DeviceRgb : ColorSpace
    {
        public override string Family => "DeviceRGB";
        public override int N => 3;
        public override void ToRgb(double[] c, out double r, out double g, out double b)
        {
            r = C01(At(c, 0)); g = C01(At(c, 1)); b = C01(At(c, 2));
        }
    }

    public sealed class DeviceCmyk : ColorSpace
    {
        readonly float[] table;
        public DeviceCmyk(float[] t) { table = t; }
        public override string Family => "DeviceCMYK";
        public override int N => 4;
        public override double[] Initial() => new double[] { 0, 0, 0, 1 };

        public override void ToRgb(double[] c, out double r, out double g, out double b)
        {
            if (table != null) { CmykTable.Lookup(table, At(c, 0), At(c, 1), At(c, 2), At(c, 3), out r, out g, out b); return; }
            double k = 1 - C01(At(c, 3));
            r = (1 - C01(At(c, 0))) * k; g = (1 - C01(At(c, 1))) * k; b = (1 - C01(At(c, 2))) * k;
        }
    }

    public sealed class IccBased : ColorSpace
    {
        readonly ColorSpace alt;
        readonly int n;
        public IccBased(int n, ColorSpace alternate) { this.n = n; alt = alternate; }
        public override string Family => "ICCBased";
        public override int N => n;
        public override double[] Initial() => alt.Initial();
        public override void ToRgb(double[] c, out double r, out double g, out double b) => alt.ToRgb(c, out r, out g, out b);
        public override string Describe() => $"ICCBased(N={n})";
    }

    public sealed class Indexed : ColorSpace
    {
        readonly double[][] palette;
        readonly int hival;
        public readonly ColorSpace Base;

        public Indexed(ColorSpace b, int hi, byte[] lookup)
        {
            Base = b;
            hival = Math.Max(0, Math.Min(255, hi));
            palette = new double[hival + 1][];
            int n = b.N;
            for (int i = 0; i <= hival; i++)
            {
                var comps = new double[n];
                for (int k = 0; k < n; k++)
                {
                    int at = i * n + k;
                    double v = at < lookup.Length ? lookup[at] / 255.0 : 0;
                    if (b is Lab lab) v = lab.LookupValue(k, v);
                    comps[k] = v;
                }
                b.ToRgb(comps, out var r, out var g, out var bb);
                palette[i] = new[] { r, g, bb };
            }
        }

        public override string Family => "Indexed";
        public override int N => 1;
        public override double[] Initial() => new double[] { 0 };

        public override void ToRgb(double[] c, out double r, out double g, out double b)
        {
            int i = (int)Math.Max(0, Math.Min(hival, Math.Round(At(c, 0))));
            r = palette[i][0]; g = palette[i][1]; b = palette[i][2];
        }

        public override string Describe() => $"Indexed({Base.Describe()})";
    }

    public sealed class TintSpace : ColorSpace
    {
        readonly string family;
        readonly string[] names;
        readonly ColorSpace alt;
        readonly PdfFunction fn;
        readonly Dictionary<string, double[]> cache = new Dictionary<string, double[]>();

        public TintSpace(string family, string[] names, ColorSpace alternate, PdfFunction f)
        {
            this.family = family;
            this.names = names;
            alt = alternate;
            fn = f;
        }

        public override string Family => family;
        public override int N => names.Length;

        public override double[] Initial()
        {
            var r = new double[N];
            for (int i = 0; i < N; i++) r[i] = 1;
            return r;
        }

        public override void ToRgb(double[] c, out double r, out double g, out double b)
        {
            var key = string.Join(",", Array.ConvertAll(c ?? new double[0], v => Math.Round(v, 5).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (!cache.TryGetValue(key, out var rgb))
            {
                var input = new double[N];
                for (int i = 0; i < N; i++) input[i] = At(c, i);
                bool none = true;
                foreach (var n in names) if (n != "None") none = false;
                alt.ToRgb(fn.Eval(input), out var rr, out var gg, out var bb);
                rgb = none ? new double[] { 1, 1, 1 } : new[] { rr, gg, bb };
                if (cache.Count < 65536) cache[key] = rgb;
            }
            r = rgb[0]; g = rgb[1]; b = rgb[2];
        }

        public override string Describe() => $"{family}({string.Join(",", names)}->{alt.Describe()})";
    }

    public sealed class Lab : ColorSpace
    {
        readonly double[] white, range;

        public Lab(double[] wp, double[] rg)
        {
            white = wp != null && wp.Length == 3 ? wp : new[] { 0.9505, 1.0, 1.089 };
            range = rg != null && rg.Length == 4 ? rg : new double[] { -100, 100, -100, 100 };
        }

        public override string Family => "Lab";
        public override int N => 3;

        public double LookupValue(int k, double v01) =>
            k == 0 ? v01 * 100 : k == 1 ? range[0] + v01 * (range[1] - range[0]) : range[2] + v01 * (range[3] - range[2]);

        public override void ToRgb(double[] c, out double r, out double g, out double b)
        {
            double L = At(c, 0), A = At(c, 1), B = At(c, 2);
            double fy = (L + 16) / 116, fx = fy + A / 500, fz = fy - B / 200;
            double Gf(double t) => t > 6.0 / 29 ? t * t * t : 3 * (6.0 / 29) * (6.0 / 29) * (t - 4.0 / 29);
            double X = white[0] * Gf(fx) * 0.9642 / white[0];
            double Y = white[1] * Gf(fy) / white[1];
            double Z = white[2] * Gf(fz) * 0.8249 / white[2];
            r = Enc(3.1338561 * X - 1.6168667 * Y - 0.4906146 * Z);
            g = Enc(-0.9787684 * X + 1.9161415 * Y + 0.0334540 * Z);
            b = Enc(0.0719453 * X - 0.2289914 * Y + 1.4052427 * Z);
        }

        static double Enc(double v) => C01(v <= 0.0031308 ? 12.92 * Math.Max(v, 0) : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055);
    }

    public sealed class PatternSpace : ColorSpace
    {
        public readonly ColorSpace Base;
        public PatternSpace(ColorSpace b) { Base = b; }
        public override string Family => "Pattern";
        public override int N => Base?.N ?? 1;
        public override void ToRgb(double[] c, out double r, out double g, out double b)
        {
            if (Base != null) Base.ToRgb(c, out r, out g, out b);
            else r = g = b = 0;
        }
    }

    /// <summary>Resolves colour space objects (with caching per indirect object).</summary>
    public sealed class ColorSpaceFactory
    {
        public readonly DeviceGray Gray = new DeviceGray();
        public readonly DeviceRgb Rgb = new DeviceRgb();
        public readonly DeviceCmyk Cmyk;
        readonly Diagnostics diag;
        readonly Limits limits;
        readonly Dictionary<object, ColorSpace> cache = new Dictionary<object, ColorSpace>();

        public ColorSpaceFactory(float[] cmykTable, Diagnostics d, Limits l)
        {
            Cmyk = new DeviceCmyk(cmykTable);
            diag = d;
            limits = l;
        }

        public ColorSpace Device(string name)
        {
            switch (name)
            {
                case "DeviceGray": case "G": case "CalGray": return Gray;
                case "DeviceRGB": case "RGB": case "CalRGB": return Rgb;
                case "DeviceCMYK": case "CMYK": return Cmyk;
                case "Pattern": return new PatternSpace(null);
            }
            return null;
        }

        public ColorSpace Resolve(object obj, PdfDict resources, int depth = 0)
        {
            if (depth > 8) throw new UnsupportedException("color.space", "colour space nesting too deep");
            if (obj is PdfName n)
            {
                var dev = Device(n.Value);
                if (dev != null) return dev;
                var csd = resources?.GetDict("ColorSpace");
                if (csd != null && csd.Has(n.Value)) return Resolve(csd.Get(n.Value), resources, depth + 1);
                throw new UnsupportedException("color.space", $"undefined colour space /{n.Value}");
            }
            if (obj is PdfArray a)
            {
                object key = a.Ref ?? (object)a;
                if (cache.TryGetValue(key, out var hit)) return hit;
                var cs = FromArray(a, resources, depth);
                cache[key] = cs;
                return cs;
            }
            throw new UnsupportedException("color.space", "invalid colour space object");
        }

        ColorSpace FromArray(PdfArray a, PdfDict resources, int depth)
        {
            var fam = (a[0] as PdfName)?.Value ?? "";
            switch (fam)
            {
                case "DeviceGray": case "DeviceRGB": case "DeviceCMYK": case "CalGray": case "CalRGB":
                    return Device(fam);
                case "Pattern":
                    return new PatternSpace(a.Count > 1 ? Resolve(a[1], resources, depth + 1) : null);
                case "Lab":
                    {
                        var d = a.Dict(1);
                        return new Lab(d?.GetArray("WhitePoint")?.ToDoubles(), d?.GetArray("Range")?.ToDoubles());
                    }
                case "ICCBased":
                    {
                        var s = a[1] as PdfStream;
                        int n = s?.Dict.GetInt("N", 3) ?? 3;
                        ColorSpace alt = null;
                        var altObj = s?.Dict.Get("Alternate");
                        if (altObj != null)
                        {
                            try { alt = Resolve(altObj, resources, depth + 1); } catch (UnsupportedException) { alt = null; }
                        }
                        if (alt == null || alt.N != n) alt = n == 1 ? Gray : n == 4 ? (ColorSpace)Cmyk : Rgb;
                        diag.Info("color.icc-approx", "ICC-based vector colours use the device colour space of the same family");
                        return new IccBased(n, alt);
                    }
                case "Indexed": case "I":
                    {
                        var b = Resolve(a[1], resources, depth + 1);
                        int hi = (int)a.Num(2);
                        var lk = a[3];
                        byte[] lookup = lk is PdfStream ls ? ls.Decode(limits) : (lk as PdfString)?.Bytes ?? new byte[0];
                        return new Indexed(b, hi, lookup);
                    }
                case "Separation":
                    {
                        var name = (a[1] as PdfName)?.Value ?? "?";
                        var alt = Resolve(a[2], resources, depth + 1);
                        var fn = PdfFunction.Load(a[3], limits);
                        if (name != "All" && name != "None") diag.Info("color.spot", $"spot colour {name} shown via its alternate space");
                        return new TintSpace("Separation", new[] { name }, alt, fn);
                    }
                case "DeviceN":
                    {
                        var na = a[1] as PdfArray;
                        var names = new string[na?.Count ?? 0];
                        for (int i = 0; i < names.Length; i++) names[i] = (na[i] as PdfName)?.Value ?? "?";
                        var alt = Resolve(a[2], resources, depth + 1);
                        var fn = PdfFunction.Load(a[3], limits);
                        return new TintSpace("DeviceN", names, alt, fn);
                    }
            }
            throw new UnsupportedException("color.space", $"colour space family {fam} not supported");
        }
    }
}
