using System;
using System.Collections.Generic;
using PDF2Unity.Core.Pdf;

namespace PDF2Unity.Core.Graphics
{
    /// <summary>PDF functions (ISO 32000-1 §7.10): sampled, exponential, stitching, PostScript calculator.</summary>
    public abstract class PdfFunction
    {
        protected double[] Domain, Range;

        public double[] Eval(double[] input)
        {
            var x = (double[])input.Clone();
            if (Domain != null)
                for (int i = 0; i < x.Length && 2 * i + 1 < Domain.Length; i++) x[i] = Clamp(x[i], Domain[2 * i], Domain[2 * i + 1]);
            var y = Evaluate(x);
            if (Range != null && Range.Length > 0)
                for (int i = 0; i < y.Length && 2 * i + 1 < Range.Length; i++) y[i] = Clamp(y[i], Range[2 * i], Range[2 * i + 1]);
            return y;
        }

        protected abstract double[] Evaluate(double[] x);

        protected static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        public static PdfFunction Load(object obj, Limits limits)
        {
            if (obj is PdfArray arr)
            {
                var fs = new List<PdfFunction>();
                for (int i = 0; i < arr.Count; i++) fs.Add(Load(arr[i], limits));
                return new ArrayFunction(fs);
            }
            var stream = obj as PdfStream;
            var d = stream?.Dict ?? obj as PdfDict;
            if (d == null) throw new UnsupportedException("function.missing", "function object missing");
            int type = d.GetInt("FunctionType", -1);
            var domain = d.GetArray("Domain")?.ToDoubles() ?? new double[] { 0, 1 };
            var range = d.GetArray("Range")?.ToDoubles();
            switch (type)
            {
                case 0: return new SampledFunction(stream, domain, range, limits);
                case 2: return new ExponentialFunction(d, domain, range);
                case 3: return new StitchingFunction(d, domain, range, limits);
                case 4: return new PostScriptFunction(stream, domain, range, limits);
                default: throw new UnsupportedException("function.type", $"function type {type} not supported");
            }
        }

        sealed class ArrayFunction : PdfFunction
        {
            readonly List<PdfFunction> fs;
            public ArrayFunction(List<PdfFunction> f) { fs = f; }
            protected override double[] Evaluate(double[] x)
            {
                var r = new double[fs.Count];
                for (int i = 0; i < fs.Count; i++) r[i] = fs[i].Eval(x)[0];
                return r;
            }
        }

        sealed class ExponentialFunction : PdfFunction
        {
            readonly double[] c0, c1;
            readonly double n;

            public ExponentialFunction(PdfDict d, double[] domain, double[] range)
            {
                Domain = domain;
                Range = range;
                c0 = d.GetArray("C0")?.ToDoubles() ?? new double[] { 0 };
                c1 = d.GetArray("C1")?.ToDoubles() ?? new double[] { 1 };
                n = d.GetNumber("N", 1);
            }

            protected override double[] Evaluate(double[] x)
            {
                double xn = Math.Pow(x[0], n);
                if (double.IsNaN(xn) || double.IsInfinity(xn)) xn = 0;
                var r = new double[Math.Min(c0.Length, c1.Length)];
                for (int i = 0; i < r.Length; i++) r[i] = c0[i] + xn * (c1[i] - c0[i]);
                return r;
            }
        }

        sealed class StitchingFunction : PdfFunction
        {
            readonly List<PdfFunction> fs = new List<PdfFunction>();
            readonly double[] bounds, encode;

            public StitchingFunction(PdfDict d, double[] domain, double[] range, Limits limits)
            {
                Domain = domain;
                Range = range;
                var a = d.GetArray("Functions");
                for (int i = 0; a != null && i < a.Count; i++) fs.Add(Load(a[i], limits));
                bounds = d.GetArray("Bounds")?.ToDoubles() ?? new double[0];
                encode = d.GetArray("Encode")?.ToDoubles() ?? new double[0];
                if (fs.Count == 0) throw new UnsupportedException("function.stitching", "stitching function without functions");
            }

            protected override double[] Evaluate(double[] x)
            {
                double v = x[0];
                int k = fs.Count, i = 0;
                while (i < k - 1 && i < bounds.Length && v >= bounds[i]) i++;
                double lo = i == 0 ? Domain[0] : bounds[i - 1];
                double hi = i == k - 1 ? Domain[1] : bounds[i];
                double e0 = 2 * i < encode.Length ? encode[2 * i] : 0, e1 = 2 * i + 1 < encode.Length ? encode[2 * i + 1] : 1;
                double t = hi == lo ? e0 : e0 + (v - lo) * (e1 - e0) / (hi - lo);
                return fs[i].Eval(new[] { t });
            }
        }

        sealed class SampledFunction : PdfFunction
        {
            readonly int[] size;
            readonly int bps, nOut, m;
            readonly double[] encode, decode;
            readonly int[] samples;
            readonly int[] strides;

            public SampledFunction(PdfStream s, double[] domain, double[] range, Limits limits)
            {
                if (s == null) throw new UnsupportedException("function.sampled", "sampled function must be a stream");
                Domain = domain;
                Range = range;
                var d = s.Dict;
                var sz = d.GetArray("Size").ToDoubles();
                m = sz.Length;
                size = new int[m];
                for (int i = 0; i < m; i++) size[i] = Math.Max(1, (int)sz[i]);
                bps = d.GetInt("BitsPerSample", 8);
                nOut = range.Length / 2;
                var enc = new List<double>();
                for (int i = 0; i < m; i++) { enc.Add(0); enc.Add(size[i] - 1); }
                encode = d.GetArray("Encode")?.ToDoubles() ?? enc.ToArray();
                decode = d.GetArray("Decode")?.ToDoubles() ?? range;
                long total = nOut;
                foreach (var v in size) total *= v;
                if (total > 50_000_000) throw new UnsupportedException("function.sampled", "sampled function too large");
                var data = s.Decode(limits);
                samples = new int[total];
                long bit = 0;
                for (long i = 0; i < total; i++)
                {
                    int v = 0;
                    for (int b = 0; b < bps; b++, bit++)
                    {
                        long byteIdx = bit >> 3;
                        int bv = byteIdx < data.Length ? (data[byteIdx] >> (7 - (int)(bit & 7))) & 1 : 0;
                        v = (v << 1) | bv;
                    }
                    samples[i] = v;
                }
                strides = new int[m];
                int acc = nOut;
                for (int i = 0; i < m; i++) { strides[i] = acc; acc *= size[i]; }
            }

            protected override double[] Evaluate(double[] x)
            {
                double maxv = (1L << bps) - 1;
                var i0 = new int[m];
                var i1 = new int[m];
                var f = new double[m];
                for (int i = 0; i < m; i++)
                {
                    double d0 = Domain[2 * i], d1 = Domain[2 * i + 1];
                    double e0 = encode[2 * i], e1 = encode[2 * i + 1];
                    double e = d1 == d0 ? e0 : e0 + (x[i] - d0) * (e1 - e0) / (d1 - d0);
                    e = Clamp(e, 0, size[i] - 1);
                    i0[i] = (int)Math.Floor(e);
                    i1[i] = Math.Min(i0[i] + 1, size[i] - 1);
                    f[i] = e - i0[i];
                }
                var o = new double[nOut];
                for (int corner = 0; corner < (1 << m); corner++)
                {
                    double w = 1;
                    int off = 0;
                    for (int i = 0; i < m; i++)
                    {
                        if (((corner >> i) & 1) != 0) { w *= f[i]; off += i1[i] * strides[i]; }
                        else { w *= 1 - f[i]; off += i0[i] * strides[i]; }
                    }
                    if (w == 0) continue;
                    for (int j = 0; j < nOut; j++) o[j] += w * samples[off + j];
                }
                for (int j = 0; j < nOut; j++) o[j] = decode[2 * j] + o[j] * (decode[2 * j + 1] - decode[2 * j]) / maxv;
                return o;
            }
        }

        sealed class PostScriptFunction : PdfFunction
        {
            readonly List<object> proc;
            const int MaxSteps = 100_000;
            int steps;

            public PostScriptFunction(PdfStream s, double[] domain, double[] range, Limits limits)
            {
                if (s == null) throw new UnsupportedException("function.ps", "PostScript function must be a stream");
                Domain = domain;
                Range = range;
                var lx = new Lexer(s.Decode(limits));
                var first = lx.Next() as PdfKeyword;
                if (first == null || first.Value != "{") throw new UnsupportedException("function.ps", "PostScript function must start with '{'");
                proc = ParseProc(lx);
            }

            static List<object> ParseProc(Lexer lx)
            {
                var p = new List<object>();
                while (true)
                {
                    var t = lx.Next();
                    if (t == null) throw new UnsupportedException("function.ps", "unterminated PostScript procedure");
                    if (t is PdfKeyword k)
                    {
                        if (k.Value == "{") { p.Add(ParseProc(lx)); continue; }
                        if (k.Value == "}") return p;
                        p.Add(k.Value);
                    }
                    else if (t is double d) p.Add(d);
                    else if (t is bool b) p.Add(b ? 1.0 : 0.0);
                }
            }

            protected override double[] Evaluate(double[] x)
            {
                var st = new List<double>(x);
                steps = 0;
                Run(proc, st);
                int n = Range.Length / 2;
                var r = new double[n];
                for (int i = 0; i < n; i++)
                {
                    int k = st.Count - n + i;
                    r[i] = k >= 0 ? st[k] : 0;
                }
                return r;
            }

            double Pop(List<double> st)
            {
                if (st.Count == 0) return 0;
                var v = st[st.Count - 1];
                st.RemoveAt(st.Count - 1);
                return v;
            }

            void Run(List<object> p, List<double> st)
            {
                var procStack = new Stack<List<object>>();
                for (int i = 0; i < p.Count; i++)
                {
                    if (++steps > MaxSteps) throw new UnsupportedException("function.ps", "PostScript step limit exceeded");
                    var item = p[i];
                    if (item is double d) { st.Add(d); continue; }
                    if (item is List<object> sub) { procStack.Push(sub); continue; }
                    var op = (string)item;
                    if (op == "if")
                    {
                        var body = procStack.Count > 0 ? procStack.Pop() : null;
                        if (Pop(st) != 0 && body != null) Run(body, st);
                        continue;
                    }
                    if (op == "ifelse")
                    {
                        var b2 = procStack.Count > 0 ? procStack.Pop() : null;
                        var b1 = procStack.Count > 0 ? procStack.Pop() : null;
                        var body = Pop(st) != 0 ? b1 : b2;
                        if (body != null) Run(body, st);
                        continue;
                    }
                    Exec(op, st);
                }
            }

            void Exec(string op, List<double> st)
            {
                double a, b;
                switch (op)
                {
                    case "add": b = Pop(st); st.Add(Pop(st) + b); break;
                    case "sub": b = Pop(st); st.Add(Pop(st) - b); break;
                    case "mul": b = Pop(st); st.Add(Pop(st) * b); break;
                    case "div": b = Pop(st); a = Pop(st); st.Add(b != 0 ? a / b : 0); break;
                    case "idiv": b = Math.Truncate(Pop(st)); a = Math.Truncate(Pop(st)); st.Add(b != 0 ? Math.Truncate(a / b) : 0); break;
                    case "mod": b = Math.Truncate(Pop(st)); a = Math.Truncate(Pop(st)); st.Add(b != 0 ? a % b : 0); break;
                    case "neg": st.Add(-Pop(st)); break;
                    case "abs": st.Add(Math.Abs(Pop(st))); break;
                    case "sqrt": st.Add(Math.Sqrt(Math.Max(0, Pop(st)))); break;
                    case "sin": st.Add(Math.Sin(Pop(st) * Math.PI / 180)); break;
                    case "cos": st.Add(Math.Cos(Pop(st) * Math.PI / 180)); break;
                    case "atan": b = Pop(st); a = Pop(st); { var r = Math.Atan2(a, b) * 180 / Math.PI; st.Add(r < 0 ? r + 360 : r); } break;
                    case "exp": b = Pop(st); a = Pop(st); { var r = Math.Pow(a, b); st.Add(double.IsNaN(r) || double.IsInfinity(r) ? 0 : r); } break;
                    case "ln": a = Pop(st); st.Add(a > 0 ? Math.Log(a) : -1e30); break;
                    case "log": a = Pop(st); st.Add(a > 0 ? Math.Log10(a) : -1e30); break;
                    case "floor": st.Add(Math.Floor(Pop(st))); break;
                    case "ceiling": st.Add(Math.Ceiling(Pop(st))); break;
                    case "round": st.Add(Math.Floor(Pop(st) + 0.5)); break;
                    case "truncate": case "cvi": st.Add(Math.Truncate(Pop(st))); break;
                    case "cvr": break;
                    case "eq": b = Pop(st); st.Add(Pop(st) == b ? 1 : 0); break;
                    case "ne": b = Pop(st); st.Add(Pop(st) != b ? 1 : 0); break;
                    case "gt": b = Pop(st); st.Add(Pop(st) > b ? 1 : 0); break;
                    case "ge": b = Pop(st); st.Add(Pop(st) >= b ? 1 : 0); break;
                    case "lt": b = Pop(st); st.Add(Pop(st) < b ? 1 : 0); break;
                    case "le": b = Pop(st); st.Add(Pop(st) <= b ? 1 : 0); break;
                    case "and": b = Pop(st); st.Add((long)Pop(st) & (long)b); break;
                    case "or": b = Pop(st); st.Add((long)Pop(st) | (long)b); break;
                    case "xor": b = Pop(st); st.Add((long)Pop(st) ^ (long)b); break;
                    case "not": a = Pop(st); st.Add(a == 0 ? 1 : (a == 1 ? 0 : ~(long)a)); break;
                    case "bitshift": b = Pop(st); a = Pop(st); st.Add(b >= 0 ? (long)a << (int)b : (long)a >> (int)-b); break;
                    case "true": st.Add(1); break;
                    case "false": st.Add(0); break;
                    case "dup": a = Pop(st); st.Add(a); st.Add(a); break;
                    case "pop": Pop(st); break;
                    case "exch": b = Pop(st); a = Pop(st); st.Add(b); st.Add(a); break;
                    case "copy":
                        {
                            int n = (int)Pop(st);
                            int s0 = st.Count - n;
                            for (int i = 0; i < n && s0 >= 0; i++) st.Add(st[s0 + i]);
                        }
                        break;
                    case "index":
                        {
                            int n = (int)Pop(st);
                            int k = st.Count - 1 - n;
                            st.Add(k >= 0 ? st[k] : 0);
                        }
                        break;
                    case "roll":
                        {
                            int j = (int)Pop(st), n = (int)Pop(st);
                            if (n > 0 && n <= st.Count)
                            {
                                j = ((j % n) + n) % n;
                                var seg = st.GetRange(st.Count - n, n);
                                st.RemoveRange(st.Count - n, n);
                                for (int i = 0; i < n; i++) st.Add(seg[(i - j + n) % n]);
                            }
                        }
                        break;
                    default: throw new UnsupportedException("function.ps", $"PostScript operator {op} not supported");
                }
            }
        }
    }
}
