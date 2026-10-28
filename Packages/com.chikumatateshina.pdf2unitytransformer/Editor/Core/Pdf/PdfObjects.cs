using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PDF2Unity.Core.Pdf
{
    /// <summary>
    /// PDF object model. Numbers are <see cref="double"/>, booleans <see cref="bool"/>,
    /// null is <see cref="PdfNull.Value"/>. Containers know their file so that indirect
    /// references can be resolved lazily.
    /// </summary>
    public sealed class PdfName : IEquatable<PdfName>
    {
        public readonly string Value; // without the leading '/'
        public PdfName(string v) { Value = v; }
        public bool Equals(PdfName o) => o != null && o.Value == Value;
        public override bool Equals(object o) => o is PdfName n && n.Value == Value;
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => "/" + Value;
    }

    public sealed class PdfString
    {
        public readonly byte[] Bytes;
        public PdfString(byte[] b) { Bytes = b; }
        public override string ToString() => Encoding.GetEncoding(28591).GetString(Bytes);
    }

    public sealed class PdfNull
    {
        public static readonly PdfNull Value = new PdfNull();
        PdfNull() { }
        public override string ToString() => "null";
    }

    public sealed class PdfRef : IEquatable<PdfRef>
    {
        public readonly int Num, Gen;
        public PdfRef(int n, int g) { Num = n; Gen = g; }
        public bool Equals(PdfRef o) => o != null && o.Num == Num && o.Gen == Gen;
        public override bool Equals(object o) => o is PdfRef r && Equals(r);
        public override int GetHashCode() => Num * 31 + Gen;
        public override string ToString() => $"{Num} {Gen} R";
    }

    /// <summary>Operator / keyword token inside content streams.</summary>
    public sealed class PdfKeyword
    {
        public readonly string Value;
        public PdfKeyword(string v) { Value = v; }
        public override string ToString() => Value;
    }

    public abstract class PdfContainer
    {
        public PdfFile Owner;
        /// <summary>Indirect object id when this container was loaded as an indirect object.</summary>
        public PdfRef Ref;

        public object Resolve(object o) => Owner != null ? Owner.Resolve(o) : o;
    }

    public sealed class PdfArray : PdfContainer
    {
        public readonly List<object> Items = new List<object>();
        public int Count => Items.Count;
        public object this[int i] => Resolve(Items[i]);
        public object Raw(int i) => Items[i];
        public void Add(object o) => Items.Add(o);

        public double Num(int i, double def = 0) => PdfUtil.ToDouble(this[i], def);
        public PdfDict Dict(int i) => this[i] as PdfDict ?? (this[i] as PdfStream)?.Dict;

        public double[] ToDoubles()
        {
            var r = new double[Count];
            for (int i = 0; i < Count; i++) r[i] = Num(i);
            return r;
        }
    }

    public sealed class PdfDict : PdfContainer
    {
        public readonly Dictionary<string, object> Items = new Dictionary<string, object>();
        /// <summary>Stream that owns this dictionary (if any).</summary>
        public PdfStream Stream;

        public bool Has(string k) => Items.ContainsKey(k);
        public object Raw(string k) => Items.TryGetValue(k, out var v) ? v : null;
        public object Get(string k) => Items.TryGetValue(k, out var v) ? Resolve(v) : null;
        public void Set(string k, object v) => Items[k] = v;

        public PdfDict GetDict(string k)
        {
            var o = Get(k);
            return o as PdfDict ?? (o as PdfStream)?.Dict;
        }

        public PdfArray GetArray(string k) => Get(k) as PdfArray;
        public PdfStream GetStream(string k) => Get(k) as PdfStream;
        public string GetName(string k, string def = null) => Get(k) is PdfName n ? n.Value : def;
        public double GetNumber(string k, double def = 0) => PdfUtil.ToDouble(Get(k), def);
        public int GetInt(string k, int def = 0) => (int)PdfUtil.ToDouble(Get(k), def);
        public bool GetBool(string k, bool def = false) => Get(k) is bool b ? b : def;

        /// <summary>Stable identity for caching: the indirect reference or the object itself.</summary>
        public object Key => (object)(Stream?.Ref ?? Ref) ?? this;
    }

    public sealed class PdfStream : PdfContainer
    {
        public readonly PdfDict Dict;
        public byte[] Raw; // encoded bytes
        byte[] decoded;

        public PdfStream(PdfDict d, byte[] raw)
        {
            Dict = d;
            d.Stream = this;
            Raw = raw;
        }

        public object Key => (object)Ref ?? this;

        /// <summary>Data with all standard filters applied (throws for image-only codecs).</summary>
        public byte[] Decode(Limits limits = null)
        {
            if (decoded == null) decoded = Filters.Decode(this, limits);
            return decoded;
        }

        public List<string> FilterNames()
        {
            var r = new List<string>();
            var f = Dict.Get("Filter");
            if (f is PdfName n) r.Add(n.Value);
            else if (f is PdfArray a)
                for (int i = 0; i < a.Count; i++)
                    if (a[i] is PdfName m) r.Add(m.Value);
            return r;
        }
    }

    public static class PdfUtil
    {
        public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public static double ToDouble(object o, double def = 0)
        {
            switch (o)
            {
                case double d: return double.IsNaN(d) || double.IsInfinity(d) ? def : d;
                case int i: return i;
                case long l: return l;
                case bool b: return b ? 1 : 0;
                default: return def;
            }
        }

        public static string Fmt(double v)
        {
            if (Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e15) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
