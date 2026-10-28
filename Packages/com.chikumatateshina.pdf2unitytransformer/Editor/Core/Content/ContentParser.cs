using System;
using System.Collections.Generic;
using PDF2Unity.Core.Pdf;

namespace PDF2Unity.Core.Content
{
    public sealed class Instruction
    {
        public string Op;
        public List<object> Operands;
        public int Start, End;      // byte range in the content buffer
        public PdfDict InlineDict;  // BI ... ID
        public byte[] InlineData;   // raw inline image data
    }

    /// <summary>Splits a content stream into operators with operands and byte offsets.</summary>
    public static class ContentParser
    {
        public static List<Instruction> Parse(byte[] data, int maxOps)
        {
            var list = new List<Instruction>();
            var lx = new Lexer(data);
            var operands = new List<object>();
            int opStart = -1;
            while (true)
            {
                lx.SkipWhite();
                if (lx.Pos >= data.Length) break;
                int tokStart = lx.Pos;
                if (opStart < 0) opStart = tokStart;
                object t;
                try { t = lx.Next(); }
                catch (Exception) { break; }
                if (t == null) break;
                if (t == Lexer.ArrayEnd || t == Lexer.DictEnd) continue;
                if (t is PdfKeyword k)
                {
                    if (k.Value == "BI")
                    {
                        var ins = ReadInline(data, lx, opStart);
                        if (ins != null) list.Add(ins);
                    }
                    else
                    {
                        list.Add(new Instruction { Op = k.Value, Operands = operands, Start = opStart, End = lx.Pos });
                    }
                    operands = new List<object>();
                    opStart = -1;
                    if (list.Count > maxOps) throw new CompileException("limit.ops", "content stream exceeds operator limit");
                    continue;
                }
                operands.Add(t);
                if (operands.Count > 100_000) operands.Clear();
            }
            return list;
        }

        static Instruction ReadInline(byte[] data, Lexer lx, int start)
        {
            var dict = new PdfDict();
            while (true)
            {
                var k = lx.Next();
                if (k == null) return null;
                if (k is PdfKeyword kw && kw.Value == "ID") break;
                var v = lx.Next();
                if (k is PdfName n) dict.Set(Expand(n.Value), v is PdfName vn ? new PdfName(ExpandValue(vn.Value)) : v);
            }
            int p = lx.Pos;
            if (p < data.Length && Lexer.IsWhite(data[p])) p++;
            int dataStart = p;
            // Find "EI" delimited by whitespace (heuristic used by most readers).
            int end = -1;
            for (int i = dataStart; i + 1 < data.Length; i++)
            {
                if (data[i] == 'E' && data[i + 1] == 'I' && (i == dataStart || Lexer.IsWhite(data[i - 1])) &&
                    (i + 2 >= data.Length || Lexer.IsWhite(data[i + 2]) || Lexer.IsDelim(data[i + 2])))
                {
                    end = i;
                    break;
                }
            }
            if (end < 0) { lx.Pos = data.Length; return null; }
            int dataEnd = end;
            if (dataEnd > dataStart && Lexer.IsWhite(data[dataEnd - 1])) dataEnd--;
            var raw = new byte[dataEnd - dataStart];
            Buffer.BlockCopy(data, dataStart, raw, 0, raw.Length);
            lx.Pos = end + 2;
            return new Instruction { Op = "BI", Operands = new List<object>(), Start = start, End = lx.Pos, InlineDict = dict, InlineData = raw };
        }

        static string Expand(string k)
        {
            switch (k)
            {
                case "BPC": return "BitsPerComponent";
                case "CS": return "ColorSpace";
                case "D": return "Decode";
                case "DP": return "DecodeParms";
                case "F": return "Filter";
                case "H": return "Height";
                case "IM": return "ImageMask";
                case "I": return "Interpolate";
                case "W": return "Width";
                default: return k;
            }
        }

        static string ExpandValue(string v)
        {
            switch (v)
            {
                case "G": return "DeviceGray";
                case "RGB": return "DeviceRGB";
                case "CMYK": return "DeviceCMYK";
                case "I": return "Indexed";
                case "AHx": return "ASCIIHexDecode";
                case "A85": return "ASCII85Decode";
                case "LZW": return "LZWDecode";
                case "Fl": return "FlateDecode";
                case "RL": return "RunLengthDecode";
                case "CCF": return "CCITTFaxDecode";
                case "DCT": return "DCTDecode";
                default: return v;
            }
        }
    }
}
