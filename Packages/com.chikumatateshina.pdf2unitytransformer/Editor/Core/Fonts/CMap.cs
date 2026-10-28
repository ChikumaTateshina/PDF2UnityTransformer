using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace PDF2Unity.Core.Fonts
{
    /// <summary>Character code -> CID mapping (embedded CMap streams and Identity-H/V).</summary>
    public sealed class CMap
    {
        public readonly List<(int nb, int lo, int hi)> Codespaces = new List<(int, int, int)>();
        public readonly List<(int nb, int lo, int hi, int cid)> Ranges = new List<(int, int, int, int)>();
        public readonly Dictionary<(int, int), int> Chars = new Dictionary<(int, int), int>();
        public bool Identity;
        public bool Vertical;

        public static CMap IdentityMap(bool vertical) => new CMap { Identity = true, Vertical = vertical };

        public IEnumerable<(int code, int nb)> Split(byte[] data)
        {
            if (Identity && Codespaces.Count == 0)
            {
                for (int i = 0; i + 1 < data.Length; i += 2) yield return ((data[i] << 8) | data[i + 1], 2);
                yield break;
            }
            var spaces = Codespaces.Count > 0 ? Codespaces : new List<(int, int, int)> { (2, 0, 0xFFFF) };
            int p = 0;
            while (p < data.Length)
            {
                bool matched = false;
                for (int nb = 1; nb <= 4 && p + nb <= data.Length && !matched; nb++)
                {
                    int code = 0;
                    for (int k = 0; k < nb; k++) code = (code << 8) | data[p + k];
                    foreach (var (snb, lo, hi) in spaces)
                    {
                        if (snb == nb && code >= lo && code <= hi)
                        {
                            yield return (code, nb);
                            p += nb;
                            matched = true;
                            break;
                        }
                    }
                }
                if (!matched)
                {
                    int nb = int.MaxValue;
                    foreach (var s in spaces) nb = Math.Min(nb, s.Item1);
                    yield return (-1, nb);
                    p += nb;
                }
            }
        }

        public int Cid(int code, int nb)
        {
            if (code < 0) return 0;
            if (Chars.TryGetValue((nb, code), out var c)) return c;
            foreach (var (rnb, lo, hi, cid) in Ranges)
                if (rnb == nb && code >= lo && code <= hi) return cid + code - lo;
            return Identity ? code : 0;
        }

        static readonly Regex Hex = new Regex("<([0-9A-Fa-f\\s]*)>");

        static (int value, int nb) HexVal(string s)
        {
            var h = Regex.Replace(s, "\\s", "");
            if (h.Length == 0) return (0, 1);
            if (h.Length > 8) h = h.Substring(h.Length - 8);
            return ((int)Convert.ToUInt32(h, 16), Math.Max(1, h.Length / 2));
        }

        public static CMap Parse(byte[] data)
        {
            var text = Encoding.GetEncoding(28591).GetString(data);
            var cm = new CMap();
            if (Regex.IsMatch(text, "/WMode\\s+1")) cm.Vertical = true;
            var use = Regex.Match(text, "/(Identity-[HV])\\s+usecmap");
            if (use.Success) { cm.Identity = true; if (use.Groups[1].Value.EndsWith("V")) cm.Vertical = true; }
            foreach (Match block in Regex.Matches(text, "begincodespacerange(.*?)endcodespacerange", RegexOptions.Singleline))
            {
                var toks = Hex.Matches(block.Groups[1].Value);
                for (int i = 0; i + 1 < toks.Count; i += 2)
                {
                    var (lo, nb) = HexVal(toks[i].Groups[1].Value);
                    var (hi, _) = HexVal(toks[i + 1].Groups[1].Value);
                    cm.Codespaces.Add((nb, lo, hi));
                }
            }
            foreach (Match block in Regex.Matches(text, "begincidrange(.*?)endcidrange", RegexOptions.Singleline))
                foreach (Match m in Regex.Matches(block.Groups[1].Value, "<([0-9A-Fa-f\\s]*)>\\s*<([0-9A-Fa-f\\s]*)>\\s*(\\d+)"))
                {
                    var (lo, nb) = HexVal(m.Groups[1].Value);
                    var (hi, _) = HexVal(m.Groups[2].Value);
                    cm.Ranges.Add((nb, lo, hi, int.Parse(m.Groups[3].Value)));
                }
            foreach (Match block in Regex.Matches(text, "begincidchar(.*?)endcidchar", RegexOptions.Singleline))
                foreach (Match m in Regex.Matches(block.Groups[1].Value, "<([0-9A-Fa-f\\s]*)>\\s*(\\d+)"))
                {
                    var (code, nb) = HexVal(m.Groups[1].Value);
                    cm.Chars[(nb, code)] = int.Parse(m.Groups[2].Value);
                }
            if (cm.Codespaces.Count == 0 && !cm.Identity) cm.Codespaces.Add((2, 0, 0xFFFF));
            return cm;
        }

        /// <summary>code -> Unicode string (only used for substitute fonts).</summary>
        public static Dictionary<int, string> ParseToUnicode(byte[] data)
        {
            var text = Encoding.GetEncoding(28591).GetString(data);
            var o = new Dictionary<int, string>();
            foreach (Match block in Regex.Matches(text, "beginbfchar(.*?)endbfchar", RegexOptions.Singleline))
                foreach (Match m in Regex.Matches(block.Groups[1].Value, "<([0-9A-Fa-f\\s]*)>\\s*<([0-9A-Fa-f\\s]*)>"))
                    o[HexVal(m.Groups[1].Value).value] = Utf16(m.Groups[2].Value);
            foreach (Match block in Regex.Matches(text, "beginbfrange(.*?)endbfrange", RegexOptions.Singleline))
                foreach (Match m in Regex.Matches(block.Groups[1].Value, "<([0-9A-Fa-f\\s]*)>\\s*<([0-9A-Fa-f\\s]*)>\\s*(\\[[^\\]]*\\]|<[0-9A-Fa-f\\s]*>)"))
                {
                    int lo = HexVal(m.Groups[1].Value).value, hi = HexVal(m.Groups[2].Value).value;
                    if (hi - lo > 65535 || hi < lo) continue;
                    var rest = m.Groups[3].Value;
                    if (rest.StartsWith("["))
                    {
                        int i = 0;
                        foreach (Match t in Hex.Matches(rest)) o[lo + i++] = Utf16(t.Groups[1].Value);
                    }
                    else
                    {
                        var b = Utf16(rest.Substring(1, rest.Length - 2));
                        if (b.Length == 0) continue;
                        for (int i = 0; i <= hi - lo; i++) o[lo + i] = b.Substring(0, b.Length - 1) + (char)(b[b.Length - 1] + i);
                    }
                }
            return o;
        }

        static string Utf16(string hex)
        {
            var h = Regex.Replace(hex, "\\s", "");
            if (h.Length % 2 == 1) h += "0";
            var bytes = new byte[h.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(h.Substring(2 * i, 2), 16);
            return Encoding.BigEndianUnicode.GetString(bytes);
        }
    }
}
