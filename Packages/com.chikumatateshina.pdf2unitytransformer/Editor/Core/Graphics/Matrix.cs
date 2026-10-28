using System;

namespace PDF2Unity.Core.Graphics
{
    /// <summary>2D affine matrix in PDF convention: [a b c d e f], row vector * M.</summary>
    public readonly struct Matrix
    {
        public readonly double A, B, C, D, E, F;

        public Matrix(double a, double b, double c, double d, double e, double f)
        {
            A = a; B = b; C = c; D = d; E = e; F = f;
        }

        public static readonly Matrix Identity = new Matrix(1, 0, 0, 1, 0, 0);

        public static Matrix Translate(double x, double y) => new Matrix(1, 0, 0, 1, x, y);
        public static Matrix Scale(double x, double y) => new Matrix(x, 0, 0, y, 0, 0);

        public static Matrix FromArray(double[] v) =>
            v != null && v.Length >= 6 ? new Matrix(v[0], v[1], v[2], v[3], v[4], v[5]) : Identity;

        /// <summary>this x m (apply this first, then m).</summary>
        public Matrix Then(Matrix m) => new Matrix(
            A * m.A + B * m.C, A * m.B + B * m.D,
            C * m.A + D * m.C, C * m.B + D * m.D,
            E * m.A + F * m.C + m.E, E * m.B + F * m.D + m.F);

        public void Apply(double x, double y, out double ox, out double oy)
        {
            ox = A * x + C * y + E;
            oy = B * x + D * y + F;
        }

        public double Det => A * D - B * C;
        public double MeanScale => Math.Sqrt(Math.Abs(Det));

        public double MaxScale
        {
            get
            {
                double s1 = A * A + B * B + C * C + D * D;
                double s2 = Math.Sqrt(Math.Max(0, (A * A + B * B - C * C - D * D) * (A * A + B * B - C * C - D * D) + 4 * (A * C + B * D) * (A * C + B * D)));
                return Math.Sqrt(Math.Max(0, (s1 + s2) / 2));
            }
        }

        public bool TryInvert(out Matrix inv)
        {
            double d = Det;
            if (Math.Abs(d) < 1e-300) { inv = Identity; return false; }
            double ia = D / d, ib = -B / d, ic = -C / d, id = A / d;
            inv = new Matrix(ia, ib, ic, id, -(E * ia + F * ic), -(E * ib + F * id));
            return true;
        }

        public Rect TransformRect(Rect r)
        {
            Apply(r.X0, r.Y0, out var x1, out var y1);
            Apply(r.X1, r.Y0, out var x2, out var y2);
            Apply(r.X0, r.Y1, out var x3, out var y3);
            Apply(r.X1, r.Y1, out var x4, out var y4);
            return new Rect(Math.Min(Math.Min(x1, x2), Math.Min(x3, x4)), Math.Min(Math.Min(y1, y2), Math.Min(y3, y4)),
                            Math.Max(Math.Max(x1, x2), Math.Max(x3, x4)), Math.Max(Math.Max(y1, y2), Math.Max(y3, y4)));
        }
    }

    public readonly struct Rect
    {
        public readonly double X0, Y0, X1, Y1;

        public Rect(double x0, double y0, double x1, double y1)
        {
            X0 = Math.Min(x0, x1); Y0 = Math.Min(y0, y1); X1 = Math.Max(x0, x1); Y1 = Math.Max(y0, y1);
        }

        public static Rect FromArray(double[] v) => v != null && v.Length >= 4 ? new Rect(v[0], v[1], v[2], v[3]) : default;
        public double Width => X1 - X0;
        public double Height => Y1 - Y0;
        public bool IsEmpty => !(X1 > X0 && Y1 > Y0);

        public bool Intersects(Rect o) => X0 < o.X1 && o.X0 < X1 && Y0 < o.Y1 && o.Y0 < Y1;
        public bool Contains(Rect o) => X0 <= o.X0 && Y0 <= o.Y0 && X1 >= o.X1 && Y1 >= o.Y1;

        public Rect Intersect(Rect o)
        {
            double x0 = Math.Max(X0, o.X0), y0 = Math.Max(Y0, o.Y0), x1 = Math.Min(X1, o.X1), y1 = Math.Min(Y1, o.Y1);
            return x1 > x0 && y1 > y0 ? new Rect(x0, y0, x1, y1) : default;
        }

        public Rect Union(Rect o)
        {
            if (IsEmpty) return o;
            if (o.IsEmpty) return this;
            return new Rect(Math.Min(X0, o.X0), Math.Min(Y0, o.Y0), Math.Max(X1, o.X1), Math.Max(Y1, o.Y1));
        }

        public Rect Grow(double d) => new Rect(X0 - d, Y0 - d, X1 + d, Y1 + d);
        public override string ToString() => $"[{X0:0.##} {Y0:0.##} {X1:0.##} {Y1:0.##}]";
    }
}
