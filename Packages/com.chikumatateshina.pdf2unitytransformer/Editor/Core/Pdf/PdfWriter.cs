using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PDF2Unity.Core.Pdf
{
    /// <summary>
    /// Writes PDF objects. Used to build incremental updates of the source document in memory
    /// (synthetic pages rendered by PDFium) and small standalone PDFs.
    /// </summary>
    public sealed class PdfWriter
    {
        readonly MemoryStream ms = new MemoryStream();
        readonly SortedDictionary<int, long> offsets = new SortedDictionary<int, long>();

        public long Position => ms.Position;

        public void Raw(byte[] b) => ms.Write(b, 0, b.Length);
        public void Raw(string s) => Raw(PdfUtil.Latin1.GetBytes(s));

        public void BeginObject(int num, int gen = 0)
        {
            offsets[num] = ms.Position;
            Raw($"{num} {gen} obj\n");
        }

        public void EndObject() => Raw("\nendobj\n");

        public void WriteObject(int num, object value)
        {
            BeginObject(num);
            Value(value);
            EndObject();
        }

        public void WriteStream(int num, PdfDict dict, byte[] data)
        {
            BeginObject(num);
            var d = new Dictionary<string, object>(dict?.Items ?? new Dictionary<string, object>());
            d["Length"] = (double)data.Length;
            DictValue(d);
            Raw("\nstream\n");
            Raw(data);
            Raw("\nendstream");
            EndObject();
        }

        public void Value(object v)
        {
            switch (v)
            {
                case null:
                case PdfNull _:
                    Raw("null");
                    break;
                case bool b:
                    Raw(b ? "true" : "false");
                    break;
                case double d:
                    Raw(PdfUtil.Fmt(d));
                    break;
                case int i:
                    Raw(i.ToString());
                    break;
                case PdfName n:
                    Name(n.Value);
                    break;
                case PdfString s:
                    StringValue(s.Bytes);
                    break;
                case PdfRef r:
                    Raw($"{r.Num} {r.Gen} R");
                    break;
                case PdfArray a:
                    Raw("[");
                    for (int i = 0; i < a.Count; i++)
                    {
                        if (i > 0) Raw(" ");
                        Value(a.Raw(i));
                    }
                    Raw("]");
                    break;
                case PdfDict dict:
                    if (dict.Stream != null && dict.Stream.Ref != null) { Value(dict.Stream.Ref); break; }
                    DictValue(dict.Items);
                    break;
                case PdfStream st:
                    if (st.Ref == null) throw new InvalidOperationException("direct stream cannot be written inline");
                    Value(st.Ref);
                    break;
                case Dictionary<string, object> map:
                    DictValue(map);
                    break;
                case List<object> list:
                    Raw("[");
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) Raw(" ");
                        Value(list[i]);
                    }
                    Raw("]");
                    break;
                default:
                    throw new InvalidOperationException("cannot serialise " + v.GetType());
            }
        }

        void DictValue(Dictionary<string, object> items)
        {
            Raw("<<");
            foreach (var kv in items)
            {
                Name(kv.Key);
                Raw(" ");
                Value(kv.Value);
                Raw(" ");
            }
            Raw(">>");
        }

        void Name(string n)
        {
            var sb = new StringBuilder("/");
            foreach (var b in Encoding.UTF8.GetBytes(n))
            {
                if (b < 0x21 || b > 0x7E || b == '#' || Lexer.IsDelim(b)) sb.Append('#').Append(b.ToString("X2"));
                else sb.Append((char)b);
            }
            Raw(sb.ToString());
        }

        void StringValue(byte[] bytes)
        {
            var sb = new StringBuilder("<");
            foreach (var b in bytes) sb.Append(b.ToString("X2"));
            sb.Append('>');
            Raw(sb.ToString());
        }

        /// <summary>Classic cross-reference table + trailer for the objects written so far.</summary>
        public void Finish(Dictionary<string, object> trailer, long baseOffset, int size)
        {
            long xrefPos = baseOffset + ms.Position;
            Raw("xref\n");
            // Contiguous subsections.
            var nums = new List<int>(offsets.Keys);
            int i = 0;
            if (baseOffset == 0 && (nums.Count == 0 || nums[0] != 0))
                Raw("0 1\n0000000000 65535 f\r\n");
            while (i < nums.Count)
            {
                int j = i;
                while (j + 1 < nums.Count && nums[j + 1] == nums[j] + 1) j++;
                Raw($"{nums[i]} {j - i + 1}\n");
                for (int k = i; k <= j; k++) Raw($"{baseOffset + offsets[nums[k]]:D10} 00000 n\r\n");
                i = j + 1;
            }
            trailer["Size"] = (double)size;
            Raw("trailer\n");
            DictValue(trailer);
            Raw($"\nstartxref\n{xrefPos}\n%%EOF\n");
        }

        /// <summary>Cross-reference stream (for updates of files that use xref streams).</summary>
        public void FinishWithXrefStream(Dictionary<string, object> trailer, long baseOffset, int size, int xrefObjNum)
        {
            long xrefPos = baseOffset + ms.Position;
            offsets[xrefObjNum] = ms.Position;
            var nums = new List<int>(offsets.Keys);
            var index = new List<object>();
            var data = new MemoryStream();
            int i = 0;
            while (i < nums.Count)
            {
                int j = i;
                while (j + 1 < nums.Count && nums[j + 1] == nums[j] + 1) j++;
                index.Add((double)nums[i]);
                index.Add((double)(j - i + 1));
                for (int k = i; k <= j; k++)
                {
                    long off = baseOffset + offsets[nums[k]];
                    data.WriteByte(1);
                    data.WriteByte((byte)(off >> 24)); data.WriteByte((byte)(off >> 16));
                    data.WriteByte((byte)(off >> 8)); data.WriteByte((byte)off);
                    data.WriteByte(0); data.WriteByte(0);
                }
                i = j + 1;
            }
            var d = new Dictionary<string, object>(trailer)
            {
                ["Type"] = new PdfName("XRef"),
                ["Size"] = (double)size,
                ["W"] = new List<object> { 1.0, 4.0, 2.0 },
                ["Index"] = index,
            };
            d.Remove("XRefStm");
            d.Remove("DecodeParms");
            d.Remove("Filter");
            d.Remove("Length");
            var bytes = data.ToArray();
            d["Length"] = (double)bytes.Length;
            Raw($"{xrefObjNum} 0 obj\n");
            DictValue(d);
            Raw("\nstream\n");
            Raw(bytes);
            Raw($"\nendstream\nendobj\nstartxref\n{xrefPos}\n%%EOF\n");
        }

        public byte[] ToArray() => ms.ToArray();
    }
}
