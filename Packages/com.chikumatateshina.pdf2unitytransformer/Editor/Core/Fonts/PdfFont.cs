using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PDF2Unity.Core.Graphics;
using PDF2Unity.Core.Pdf;

namespace PDF2Unity.Core.Fonts
{
    /// <summary>Shared per font program: outlines are cached by glyph key ("g&lt;gid&gt;", "c&lt;cid&gt;", "n&lt;name&gt;").</summary>
    public sealed class FontResource
    {
        public string Ref, Name, Subtype, Program, LicenseStatus, Substitute;
        public int? FsType;
        public bool Embedded;
        public readonly Dictionary<string, List<PathCmd>> Glyphs = new Dictionary<string, List<PathCmd>>();
    }

    public struct DecodedGlyph
    {
        public int Code;
        public string Key;   // null: nothing to draw
        public double W0;    // horizontal displacement (text space, size 1)
        public double W1, Vx, Vy; // vertical writing
        public bool IsSpace;
        public string T3Name;
        public double XScale; // substitute fonts are stretched to the PDF widths
    }

    public sealed class FontOptions
    {
        public string SubstituteDir; // licensed .ttf/.otf supplied by the user
        public bool RightsDeclared;
    }

    /// <summary>A PDF font dictionary: code -> glyph key + metrics, and glyph outlines.</summary>
    public sealed class PdfFont
    {
        readonly FontLoader loader;
        readonly PdfDict fd;
        public readonly string Subtype, BaseName;
        public readonly bool IsType3, Composite;
        public bool Vertical;
        public IFontProgram Program;
        public FontResource Resource;
        public (string code, string message)? Unsupported;
        public Rect BBox = new Rect(0, -0.2, 1, 0.9);
        public string SubstitutePath;

        // simple fonts
        int firstChar;
        double[] widths = new double[0];
        double missingWidth;
        string[] encoding = new string[256];
        bool symbolic, hasDifferences, explicitEncoding;
        // type3
        public Matrix FontMatrix = new Matrix(0.001, 0, 0, 0.001, 0, 0);
        public PdfDict CharProcs, T3Resources;
        // composite
        CMap cmap;
        double dw = 1000;
        readonly Dictionary<int, double> cidWidths = new Dictionary<int, double>();
        readonly List<(int a, int b, double w)> cidRanges = new List<(int, int, double)>();
        double[] dw2 = { 880, -1000 };
        readonly Dictionary<int, (double w1, double vx, double vy)> cidW2 = new Dictionary<int, (double, double, double)>();
        byte[] cidToGid;
        Dictionary<int, string> toUnicode = new Dictionary<int, string>();
        PdfDict descriptor;

        public PdfFont(FontLoader l, PdfDict dict)
        {
            loader = l;
            fd = dict;
            Subtype = dict.GetName("Subtype", "Type1");
            BaseName = dict.GetName("BaseFont", "Untitled");
            IsType3 = Subtype == "Type3";
            Composite = Subtype == "Type0";
            try { Init(); }
            catch (CompileException) { throw; }
            catch (UnsupportedException e) { Unsupported = (e.Code, e.Message); }
            catch (Exception e) { Unsupported = ("font.parse", $"{e.GetType().Name}: {e.Message}"); }
        }

        void Init()
        {
            if (IsType3) { InitType3(); return; }
            if (Composite)
            {
                var desc = fd.GetArray("DescendantFonts")?.Dict(0) ?? throw new UnsupportedException("font.cid", "Type0 font without descendant");
                descriptor = desc.GetDict("FontDescriptor");
                InitCMap(fd.Get("Encoding"));
                InitCidWidths(desc);
                var c2g = desc.Get("CIDToGIDMap");
                if (c2g is PdfStream s) cidToGid = s.Decode(loader.Limits);
            }
            else
            {
                descriptor = fd.GetDict("FontDescriptor");
                firstChar = fd.GetInt("FirstChar", 0);
                widths = fd.GetArray("Widths")?.ToDoubles() ?? new double[0];
                missingWidth = descriptor?.GetNumber("MissingWidth", 0) ?? 0;
            }
            var bb = descriptor?.GetArray("FontBBox")?.ToDoubles();
            if (bb != null && bb.Length == 4 && bb[2] > bb[0] && bb[3] > bb[1]) BBox = new Rect(bb[0] / 1000, bb[1] / 1000, bb[2] / 1000, bb[3] / 1000);
            LoadProgram();
            if (!Composite) InitSimpleEncoding();
        }

        void InitType3()
        {
            FontMatrix = Matrix.FromArray(fd.GetArray("FontMatrix")?.ToDoubles() ?? new[] { 0.001, 0, 0, 0.001, 0, 0 });
            CharProcs = fd.GetDict("CharProcs");
            T3Resources = fd.GetDict("Resources");
            firstChar = fd.GetInt("FirstChar", 0);
            widths = fd.GetArray("Widths")?.ToDoubles() ?? new double[0];
            encoding = EncodingNames(fd.Get("Encoding"), new string[256]);
            var bb = fd.GetArray("FontBBox")?.ToDoubles();
            if (bb != null && bb.Length == 4) BBox = FontMatrix.TransformRect(new Rect(bb[0], bb[1], bb[2], bb[3]));
        }

        void InitCMap(object enc)
        {
            if (enc is PdfName n)
            {
                if (n.Value == "Identity-H" || n.Value == "Identity-V") cmap = CMap.IdentityMap(n.Value.EndsWith("V"));
                else throw new UnsupportedException("font.cmap", $"predefined CMap {n.Value} not bundled");
            }
            else if (enc is PdfStream s)
            {
                cmap = CMap.Parse(s.Decode(loader.Limits));
                var use = s.Dict.Get("UseCMap");
                if (use is PdfName un && (un.Value == "Identity-H" || un.Value == "Identity-V")) cmap.Identity = true;
                else if (use != null) throw new UnsupportedException("font.cmap", "UseCMap chains are not supported");
                if (s.Dict.GetInt("WMode", 0) == 1) cmap.Vertical = true;
            }
            else throw new UnsupportedException("font.cmap", "missing CMap");
            Vertical = cmap.Vertical;
        }

        void InitCidWidths(PdfDict desc)
        {
            dw = desc.GetNumber("DW", 1000);
            var w = desc.GetArray("W");
            for (int i = 0; w != null && i < w.Count;)
            {
                int first = (int)w.Num(i);
                if (i + 1 < w.Count && w[i + 1] is PdfArray arr)
                {
                    for (int j = 0; j < arr.Count; j++) cidWidths[first + j] = arr.Num(j);
                    i += 2;
                }
                else
                {
                    if (i + 2 >= w.Count) break;
                    cidRanges.Add((first, (int)w.Num(i + 1), w.Num(i + 2)));
                    i += 3;
                }
            }
            var d2 = desc.GetArray("DW2")?.ToDoubles();
            if (d2 != null && d2.Length == 2) dw2 = d2;
            var w2 = desc.GetArray("W2");
            for (int i = 0; w2 != null && i < w2.Count;)
            {
                int first = (int)w2.Num(i);
                if (i + 1 < w2.Count && w2[i + 1] is PdfArray arr)
                {
                    var v = arr.ToDoubles();
                    for (int j = 0; j + 2 < v.Length; j += 3) cidW2[first + j / 3] = (v[j], v[j + 1], v[j + 2]);
                    i += 2;
                }
                else
                {
                    if (i + 4 >= w2.Count) break;
                    int last = (int)w2.Num(i + 1);
                    var t = (w2.Num(i + 2), w2.Num(i + 3), w2.Num(i + 4));
                    for (int c = first; c <= last && c - first < 65536; c++) cidW2[c] = t;
                    i += 5;
                }
            }
        }

        (string kind, PdfStream stream) FontFile()
        {
            if (descriptor == null) return (null, null);
            foreach (var key in new[] { "FontFile2", "FontFile3", "FontFile" })
                if (descriptor.Get(key) is PdfStream s)
                    return (key == "FontFile3" ? key + "/" + s.Dict.GetName("Subtype", "") : key, s);
            return (null, null);
        }

        void LoadProgram()
        {
            var (kind, stream) = FontFile();
            if (stream == null) { LoadSubstitute(null); return; }
            var data = stream.Decode(loader.Limits);
            var hash = Sha(data);
            if (!loader.Programs.TryGetValue(hash, out var prog))
            {
                switch (kind)
                {
                    case "FontFile2": case "FontFile3/OpenType": prog = new TrueTypeFont(data); break;
                    case "FontFile3/Type1C": case "FontFile3/CIDFontType0C": prog = new CffFont(data); break;
                    case "FontFile": prog = new Type1Font(data); break;
                    default: throw new UnsupportedException("font.program", $"font file {kind} not supported");
                }
                loader.Programs[hash] = prog;
            }
            Program = prog;
            var status = LicenseStatus(prog.FsType);
            Resource = loader.Register("f-" + hash.Substring(0, 20), BaseName, Subtype, true, status, prog);
            CheckLicense(status);
        }

        static string Sha(byte[] d)
        {
            using (var s = SHA256.Create()) return BitConverter.ToString(s.ComputeHash(d)).Replace("-", "").ToLowerInvariant();
        }

        public static string LicenseStatus(int? fs)
        {
            if (!fs.HasValue) return "unknown";
            int v = fs.Value;
            if ((v & 0x0200) != 0) return "bitmap-only";
            int usage = v & 0xF;
            if (usage == 0) return "installable";
            if ((usage & 8) != 0) return "editable";
            if ((usage & 4) != 0) return "preview-print";
            if ((usage & 2) != 0) return "restricted";
            return "unknown";
        }

        void CheckLicense(string status)
        {
            if (loader.Options.RightsDeclared) return;
            if (status == "restricted" || status == "bitmap-only")
            {
                var sub = loader.FindSubstitute(BaseName, descriptor);
                if (sub != null)
                {
                    loader.Diag.Warn("font.license-substituted", $"font {BaseName} has restricted embedding permission; replaced by substitute {Path.GetFileName(sub)}");
                    Program = null;
                    Resource = null;
                    LoadSubstitute(sub);
                    return;
                }
                throw new CompileException("font.license-restricted",
                    $"embedded font {BaseName} prohibits outline use (fsType={status}). Provide a licensed substitute or declare rights.");
            }
            if (status != "installable")
            {
                loader.Diag.Warn("font.license-review", $"font {BaseName}: embedding permission '{status}'; confirm the right to redistribute derived glyph geometry");
                loader.LicenseReview = true;
            }
        }

        void LoadSubstitute(string forcePath)
        {
            var path = forcePath ?? loader.FindSubstitute(BaseName, descriptor);
            if (path == null) throw new UnsupportedException("font.not-embedded", "font program not embedded and no substitute configured");
            if (!loader.Programs.TryGetValue(path, out var prog))
            {
                prog = new TrueTypeFont(File.ReadAllBytes(path));
                loader.Programs[path] = prog;
            }
            Program = prog;
            SubstitutePath = path;
            Resource = loader.Register("f-sub-" + Sha(System.Text.Encoding.UTF8.GetBytes(path + "|" + BaseName)).Substring(0, 16), BaseName, Subtype, false, "user-supplied-substitute", prog);
            Resource.Substitute = Path.GetFileName(path);
            loader.Diag.Warn("font.substituted", $"font {BaseName} replaced by substitute {Path.GetFileName(path)}; glyph shapes differ from the original");
            if (!Composite && fd.GetArray("Widths") == null)
                loader.Diag.Warn("font.metrics-missing", $"font {BaseName} has no /Widths; substitute advances are used");
            if (fd.Get("ToUnicode") is PdfStream tu) toUnicode = CMap.ParseToUnicode(tu.Decode(loader.Limits));
            if (Composite && toUnicode.Count == 0)
                throw new UnsupportedException("font.not-embedded", "non-embedded CID font without ToUnicode cannot be substituted");
        }

        string[] EncodingNames(object enc, string[] baseEnc)
        {
            var names = (string[])baseEnc.Clone();
            if (enc is PdfName n) names = (string[])(EncodingTable(n.Value) ?? baseEnc).Clone();
            else if (enc is PdfDict d)
            {
                var be = d.GetName("BaseEncoding");
                if (be != null && EncodingTable(be) != null) names = (string[])EncodingTable(be).Clone();
                var diffs = d.GetArray("Differences");
                if (diffs != null)
                {
                    int code = 0;
                    for (int i = 0; i < diffs.Count; i++)
                    {
                        var it = diffs[i];
                        if (it is PdfName nm) { if (code >= 0 && code < 256) names[code] = nm.Value; code++; }
                        else code = (int)PdfUtil.ToDouble(it);
                    }
                }
            }
            return names;
        }

        static string[] EncodingTable(string n)
        {
            switch (n)
            {
                case "StandardEncoding": return FontTables.StandardEncoding;
                case "WinAnsiEncoding": return FontTables.WinAnsiEncoding;
                case "MacRomanEncoding": return FontTables.MacRomanEncoding;
            }
            return null;
        }

        void InitSimpleEncoding()
        {
            int flags = descriptor?.GetInt("Flags", 0) ?? 0;
            symbolic = (flags & 4) != 0;
            var enc = fd.Get("Encoding");
            string[] baseEnc;
            if (Program is TrueTypeFont tt && !tt.IsCff) baseEnc = symbolic ? new string[256] : FontTables.StandardEncoding;
            else baseEnc = Program?.BuiltinEncoding ?? FontTables.StandardEncoding;
            encoding = EncodingNames(enc, baseEnc);
            hasDifferences = enc is PdfDict ed && ed.Has("Differences");
            explicitEncoding = enc != null;
        }

        public static int? GlyphNameToUnicode(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (FontTables.GlyphNameToUnicode.TryGetValue(name, out var u)) return u;
            var m = Regex.Match(name, "^(?:uni|u)([0-9A-Fa-f]{4,6})$");
            if (m.Success) return Convert.ToInt32(m.Groups[1].Value, 16);
            return null;
        }

        // ---- decoding ---------------------------------------------------------
        public IEnumerable<DecodedGlyph> Decode(byte[] data)
        {
            if (Composite)
            {
                foreach (var g in DecodeComposite(data)) yield return g;
            }
            else
            {
                foreach (var b in data) yield return DecodeSimple(b);
            }
        }

        DecodedGlyph DecodeSimple(int code)
        {
            if (IsType3)
            {
                int i = code - firstChar;
                double w = i >= 0 && i < widths.Length ? widths[i] : 0;
                return new DecodedGlyph { Code = code, W0 = w * FontMatrix.A, IsSpace = code == 32, T3Name = encoding[code], XScale = 1 };
            }
            var key = SimpleKey(code);
            int idx = code - firstChar;
            double? width = idx >= 0 && idx < widths.Length ? widths[idx] / 1000.0 : (double?)null;
            if (!width.HasValue && missingWidth != 0) width = missingWidth / 1000.0;
            var adv = ProgramAdvance(key);
            double xs = 1;
            if (!width.HasValue) width = adv ?? (Program == null ? 1.0 : 0.0); // unknown metrics: over-estimate for precomposition bboxes
            else if (SubstitutePath != null && adv.HasValue && adv.Value > 0 && width.Value > 0) xs = width.Value / adv.Value;
            return new DecodedGlyph { Code = code, Key = key, W0 = width.Value, IsSpace = code == 32, XScale = xs };
        }

        double? ProgramAdvance(string key)
        {
            if (key == null || Program == null || key[0] != 'g') return null;
            return Program.AdvanceByGid(int.Parse(key.Substring(1)));
        }

        string SimpleKey(int code)
        {
            var prog = Program;
            if (prog == null) return null;
            var name = code >= 0 && code < 256 ? encoding[code] : null;
            if (prog is TrueTypeFont tt)
            {
                if (SubstitutePath != null)
                {
                    int? u = null;
                    if (toUnicode.TryGetValue(code, out var s) && s.Length > 0) u = char.ConvertToUtf32(s, 0);
                    else u = GlyphNameToUnicode(name);
                    var g = u.HasValue ? tt.GidForUnicode(u.Value) : null;
                    return g.HasValue ? "g" + g.Value : null;
                }
                if (tt.IsCff && name != null && tt.GidForName(name) is int cg) return "g" + cg;
                if (hasDifferences && name != null && name != ".notdef" && tt.GidForName(name) is int dg) return "g" + dg;
                if ((symbolic || !explicitEncoding) && tt.Cmaps.TryGetValue((3, 0), out var sym))
                    foreach (var c in new[] { code, 0xF000 | code, 0xF100 | code, 0xF200 | code })
                        if (sym.TryGetValue(c, out var sg)) return "g" + sg;
                if (name != null && !symbolic)
                {
                    var u = GlyphNameToUnicode(name);
                    if (u.HasValue && tt.GidForUnicode(u.Value) is int ug) return "g" + ug;
                    if (tt.GidForName(name) is int ng) return "g" + ng;
                }
                if (tt.Cmaps.TryGetValue((1, 0), out var mac) && mac.TryGetValue(code, out var mg)) return "g" + mg;
                if (tt.Cmaps.TryGetValue((3, 0), out var sym2))
                    foreach (var c in new[] { code, 0xF000 | code })
                        if (sym2.TryGetValue(c, out var sg2)) return "g" + sg2;
                if (tt.Cmaps.TryGetValue((3, 1), out var uni) && symbolic && uni.TryGetValue(code, out var ug2)) return "g" + ug2;
                if (code < tt.NumGlyphs && tt.Cmaps.Count == 0) return "g" + code;
                return null;
            }
            return name != null ? "n" + name : null;
        }

        IEnumerable<DecodedGlyph> DecodeComposite(byte[] data)
        {
            foreach (var (code, nb) in cmap.Split(data))
            {
                int cid = cmap.Cid(code, nb);
                double w;
                if (!cidWidths.TryGetValue(cid, out w))
                {
                    w = dw;
                    foreach (var (a, b, v) in cidRanges) if (cid >= a && cid <= b) { w = v; break; }
                }
                var key = CidKey(cid, code);
                bool space = nb == 1 && code == 32;
                if (Vertical)
                {
                    var t = cidW2.TryGetValue(cid, out var v2) ? v2 : (dw2[1], w / 2, dw2[0]);
                    yield return new DecodedGlyph { Code = code, Key = key, W0 = w / 1000, W1 = t.Item1 / 1000, Vx = t.Item2 / 1000, Vy = t.Item3 / 1000, IsSpace = space, XScale = 1 };
                }
                else
                {
                    double xs = 1;
                    if (SubstitutePath != null && key != null && ProgramAdvance(key) is double adv && adv > 0) xs = w / 1000 / adv;
                    yield return new DecodedGlyph { Code = code, Key = key, W0 = w / 1000, IsSpace = space, XScale = xs };
                }
            }
        }

        string CidKey(int cid, int code)
        {
            var prog = Program;
            if (prog == null) return null;
            if (SubstitutePath != null)
            {
                if (!toUnicode.TryGetValue(code, out var s) || s.Length == 0) return null;
                var g = (prog as TrueTypeFont)?.GidForUnicode(char.ConvertToUtf32(s, 0));
                return g.HasValue ? "g" + g.Value : null;
            }
            if (prog is CffFont cf) return cf.CidKeyed ? "c" + cid : "g" + cid;
            int gid = cid;
            if (cidToGid != null)
            {
                int i = cid * 2;
                gid = i + 1 < cidToGid.Length ? (cidToGid[i] << 8) | cidToGid[i + 1] : 0;
            }
            if (prog is TrueTypeFont tt && tt.IsCff && gid >= tt.NumGlyphs) return null;
            return "g" + gid;
        }

        // ---- outlines ---------------------------------------------------------
        public List<PathCmd> Outline(string key)
        {
            if (key == null || Resource == null) return null;
            lock (Resource.Glyphs)
            {
                if (Resource.Glyphs.TryGetValue(key, out var hit)) return hit;
            }
            List<PathCmd> cmds = null;
            try
            {
                int v = key.Length > 1 && key[0] != 'n' ? int.Parse(key.Substring(1)) : 0;
                switch (key[0])
                {
                    case 'g': cmds = Program.OutlineByGid(v); break;
                    case 'c': cmds = Program.OutlineByCid(v); break;
                    case 'n': cmds = Program.OutlineByName(key.Substring(1)); break;
                }
            }
            catch (Exception e)
            {
                loader.Diag.Warn("font.glyph", $"font {BaseName}: glyph {key} unreadable ({e.Message})");
            }
            cmds = cmds ?? new List<PathCmd>();
            lock (Resource.Glyphs) Resource.Glyphs[key] = cmds;
            return cmds;
        }
    }

    public sealed class FontLoader
    {
        public readonly Dictionary<string, IFontProgram> Programs = new Dictionary<string, IFontProgram>();
        public readonly Dictionary<string, FontResource> Resources = new Dictionary<string, FontResource>();
        readonly Dictionary<object, PdfFont> fonts = new Dictionary<object, PdfFont>();
        public readonly Diagnostics Diag;
        public readonly Limits Limits;
        public readonly FontOptions Options;
        public bool LicenseReview;
        readonly List<(string stem, string path)> substitutes = new List<(string, string)>();

        public FontLoader(Diagnostics d, Limits l, FontOptions o)
        {
            Diag = d;
            Limits = l;
            Options = o ?? new FontOptions();
            if (!string.IsNullOrEmpty(Options.SubstituteDir) && Directory.Exists(Options.SubstituteDir))
                foreach (var f in Directory.GetFiles(Options.SubstituteDir))
                    if (f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                        substitutes.Add((Path.GetFileNameWithoutExtension(f), f));
            substitutes.Sort((a, b) => string.CompareOrdinal(a.stem, b.stem));
        }

        public PdfFont Load(PdfDict dict)
        {
            var key = dict.Key;
            if (!fonts.TryGetValue(key, out var f))
            {
                f = new PdfFont(this, dict);
                fonts[key] = f;
            }
            return f;
        }

        public FontResource Register(string reference, string name, string subtype, bool embedded, string status, IFontProgram prog)
        {
            if (!Resources.TryGetValue(reference, out var r))
            {
                r = new FontResource
                {
                    Ref = reference, Name = name, Subtype = subtype, Embedded = embedded, LicenseStatus = status,
                    FsType = prog.FsType, Program = prog.Kind,
                };
                Resources[reference] = r;
            }
            return r;
        }

        /// <summary>Picks a user-supplied substitute: exact stem match first, then serif/sans/mono class hints.</summary>
        public string FindSubstitute(string baseName, PdfDict descriptor)
        {
            if (substitutes.Count == 0) return null;
            var name = Regex.Replace(baseName ?? "", "^[A-Z]{6}\\+", "");
            foreach (var (stem, path) in substitutes)
                if (name.IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0 || stem.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return path;
            int flags = descriptor?.GetInt("Flags", 0) ?? 0;
            var low = name.ToLowerInvariant();
            string cls = (flags & 1) != 0 || low.Contains("courier") || low.Contains("mono") ? "mono"
                : (flags & 2) != 0 || low.Contains("times") || low.Contains("mincho") || low.Contains("serif") ? "serif" : "sans";
            foreach (var (stem, path) in substitutes)
                if (stem.ToLowerInvariant().Contains(cls)) return path;
            return substitutes[0].path;
        }
    }
}
