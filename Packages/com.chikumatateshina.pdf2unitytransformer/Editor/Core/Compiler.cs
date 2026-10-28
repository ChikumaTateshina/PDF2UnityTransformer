using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PDF2Unity.Core.Content;
using PDF2Unity.Core.Fonts;
using PDF2Unity.Core.Geometry;
using PDF2Unity.Core.Graphics;
using PDF2Unity.Core.Pdf;
using PDF2Unity.Core.Render;
using PDF2Unity.Core.Util;

namespace PDF2Unity.Core
{
    public sealed class CompileOptions
    {
        public string Input;
        public string Output;           // package directory
        public string Pages = "all";
        public string Mode = "hybrid";  // strict | hybrid | page-fallback
        public List<Profile> Targets = new List<Profile> { Profile.PcStandard() };
        public string SubstituteFontDir;
        public bool FontRightsDeclared;
        public bool PrecomposeTranslucent;
        public double ReferenceDpi = 110;
        public bool WriteReference = true;
        public double WorldScale = 1;
        public double PageGap = 0.25;
        public string CacheDir;
        public Limits Limits = new Limits();
    }

    public sealed class CompileResult
    {
        public string Output;
        public Dictionary<string, object> Manifest;
        public Dictionary<string, string> TargetStatus = new Dictionary<string, string>();
        public int Warnings, Errors;
    }

    /// <summary>
    /// PDF -> Asset Exchange Package (same layout as before: manifest.json, diagnostics.json,
    /// targets/&lt;t&gt;/pages/*.json|*.geom, textures, composites, reference images).
    /// Runs entirely in-process (C#) with PDFium for image decoding and precomposition.
    /// </summary>
    public static class Compiler
    {
        public const string Version = "0.2.0";
        public const string PackageFormat = "pdf2unity-package";

        public static CompileResult Compile(CompileOptions o, Job job)
        {
            var diag = new Diagnostics();
            var limits = o.Limits;
            job.Report("frontend", 0);
            var info = new FileInfo(o.Input);
            if (!info.Exists) throw new CompileException("input.missing", "PDF file not found");
            if (info.Length > limits.MaxInputBytes) throw new CompileException("input.too-large", $"input is {info.Length} bytes");
            var bytes = File.ReadAllBytes(o.Input);
            var sourceHash = Sha(bytes);
            PrepareOutput(o.Output);
            var file = new PdfFile(bytes, limits);
            if (file.Reconstructed) diag.Warn("input.repaired", "cross-reference table was damaged and has been reconstructed");
            var allPages = file.Pages();
            if (allPages.Count == 0) throw new CompileException("input.corrupt", "document has no pages");
            if (allPages.Count > limits.MaxPages) throw new CompileException("input.pages", $"{allPages.Count} pages exceed the limit");
            var indices = ParsePages(o.Pages, allPages.Count);

            var cmyk = CmykTable.Get(o.CacheDir);
            var ctx = new FrontendContext
            {
                File = file, Diag = diag, Limits = limits, Job = job,
                ColorSpaces = new ColorSpaceFactory(cmyk, diag, limits),
                Fonts = new FontLoader(diag, limits, new FontOptions { SubstituteDir = o.SubstituteFontDir, RightsDeclared = o.FontRightsDeclared }),
                OC = new OptionalContent(file, diag),
            };
            if (file.Root.Has("AcroForm")) diag.Info("scope.forms", "interactive form fields are out of scope");

            // ---- frontend ----
            var pages = new List<PageModel>();
            for (int k = 0; k < indices.Count; k++)
            {
                job.Report("frontend", (float)k / indices.Count, $"{k + 1}/{indices.Count}");
                var pm = Geometry(allPages[indices[k]], indices[k], diag);
                new Interpreter(ctx, pm).Run();
                pages.Add(pm);
            }

            // ---- lowering per target ----
            var glyphCache = new GlyphCache();
            var lowered = new Dictionary<string, List<(PageModel page, PageLowering low, List<CompositeRegion> comps)>>();
            int totalSteps = o.Targets.Count * pages.Count, step = 0;
            foreach (var prof in o.Targets)
            {
                var list = new List<(PageModel, PageLowering, List<CompositeRegion>)>();
                foreach (var pm in pages)
                {
                    job.Report($"lower:{prof.Target}", (float)step++ / Math.Max(1, totalSteps), $"page {pm.Index + 1}");
                    var low = new PageLowering(pm, prof, o.Mode, diag, glyphCache, o.PrecomposeTranslucent, job);
                    low.Run();
                    list.Add((pm, low, low.Composites()));
                }
                lowered[prof.Target] = list;
            }

            // ---- synthetic pages: composites, images, references ----
            job.Report("render", 0);
            var syn = new SyntheticPages(file);
            var compositeJobs = new List<(string target, PageModel page, CompositeRegion region, int after, int before)>();
            var truncCache = new Dictionary<string, int>();
            int Truncated(PageModel pm, TruncPoint pt, bool after)
            {
                if (pt == null) return syn.AddPageCopy(pm.PageDict, pm.Content);
                var key = new StringBuilder($"{pm.Index}:{after}");
                for (var q = pt; q != null; q = q.Parent) key.Append($"|{q.Form?.Ref}:{q.Start}:{q.End}");
                if (!truncCache.TryGetValue(key.ToString(), out var si))
                    truncCache[key.ToString()] = si = TruncatedPage(syn, pm, pt, after);
                return si;
            }
            foreach (var kv in lowered)
                foreach (var (pm, low, comps) in kv.Value)
                    foreach (var r in comps)
                        compositeJobs.Add((kv.Key, pm, r, Truncated(pm, r.After, true), r.Before != null ? Truncated(pm, r.Before, false) : -1));
            // images: union of uses across targets
            var imageUses = new Dictionary<string, (ImageRef img, double ratio)>();
            foreach (var kv in lowered)
                foreach (var (_, low, _) in kv.Value)
                    foreach (var u in low.Images)
                        imageUses[u.Key] = imageUses.TryGetValue(u.Key, out var prev)
                            ? (prev.img, Math.Max(prev.ratio, u.Value.Ratio)) : (u.Value.Image, u.Value.Ratio);
            int maxTex = 0;
            foreach (var p in o.Targets) maxTex = Math.Max(maxTex, p.MaxTextureSize);
            var imageJobs = new List<(string key, ImageRef img, int w, int h, int synIndex)>();
            foreach (var kv in imageUses)
            {
                var img = kv.Value.img;
                double ratio = Math.Min(1, Math.Max(kv.Value.ratio, 1e-6));
                int w = Math.Max(1, (int)Math.Ceiling(img.Width * ratio)), h = Math.Max(1, (int)Math.Ceiling(img.Height * ratio));
                double s = Math.Min(1, (double)maxTex / Math.Max(w, h));
                w = Math.Max(1, (int)(w * s)); h = Math.Max(1, (int)(h * s));
                imageJobs.Add((kv.Key, img, w, h, syn.AddPage(w, h, ImageResources(img), ImageContent(img, w, h))));
            }
            var refJobs = new List<(PageModel page, int synIndex)>();
            if (o.WriteReference)
                foreach (var pm in pages) refJobs.Add((pm, syn.AddPageCopy(pm.PageDict, pm.Content)));

            var textures = new Dictionary<string, RgbaImage>();
            var composites = new Dictionary<(string, CompositeRegion), (RgbaImage img, double dpi)>();
            var emptyComposites = new HashSet<CompositeRegion>();
            if (syn.Count > 0)
            {
                using (var doc = syn.Load())
                {
                    int done = 0, total = imageJobs.Count + compositeJobs.Count + refJobs.Count;
                    foreach (var (key, img, w, h, si) in imageJobs)
                    {
                        job.Report("render", (float)done++ / total, "images");
                        textures[key] = doc.Render(si, 0, 0, w, h, w, h, true);
                    }
                    foreach (var (target, pm, r, si, before) in compositeJobs)
                    {
                        job.Report("render", (float)done++ / total, "composites");
                        var prof = o.Targets.Find(p => p.Target == target);
                        double dpi = prof.CompositeDpi * pm.UserUnit;
                        double longest = Math.Max(r.Rect.Width, r.Rect.Height) * dpi / 72;
                        if (longest > prof.MaxTextureSize)
                        {
                            dpi = prof.MaxTextureSize * 72.0 / Math.Max(r.Rect.Width, r.Rect.Height);
                            diag.Warn("fallback.resolution", $"precomposited region reduced to {dpi:0} dpi ({target})", pm.Index + 1);
                        }
                        int pw = Math.Max(1, (int)Math.Ceiling(r.Rect.Width * dpi / 72)), ph = Math.Max(1, (int)Math.Ceiling(r.Rect.Height * dpi / 72));
                        var after = doc.Render(si, r.Rect.X0, r.Rect.Y0, r.Rect.X1, r.Rect.Y1, pw, ph, false);
                        if (before < 0) { composites[(target, r)] = (after, dpi); continue; }
                        // Delta composite: keep only the pixels changed by the precomposited operations, so
                        // native content underneath (text, vectors) stays visible wherever the effect did not paint.
                        var prev = doc.Render(before, r.Rect.X0, r.Rect.Y0, r.Rect.X1, r.Rect.Y1, pw, ph, false);
                        var delta = Delta(after, prev, out int cx0, out int cy0, out int cx1, out int cy1);
                        if (delta == null) { emptyComposites.Add(r); continue; }
                        double sxp = r.Rect.Width / pw, syp = r.Rect.Height / ph;
                        r.Rect = new Rect(r.Rect.X0 + cx0 * sxp, r.Rect.Y0 + cy0 * syp, r.Rect.X0 + cx1 * sxp, r.Rect.Y0 + cy1 * syp);
                        r.Partial = true;
                        composites[(target, r)] = (Crop(delta, cx0, cy0, cx1, cy1), dpi);
                    }
                    if (o.WriteReference)
                    {
                        Directory.CreateDirectory(Path.Combine(o.Output, "reference"));
                        foreach (var (pm, si) in refJobs)
                        {
                            job.Report("render", (float)done++ / total, "reference");
                            double dpi = Math.Min(o.ReferenceDpi, 8192 * 72.0 / Math.Max(pm.DisplayW, pm.DisplayH));
                            int pw = Math.Max(1, (int)Math.Round(pm.DisplayW * dpi / 72)), ph = Math.Max(1, (int)Math.Round(pm.DisplayH * dpi / 72));
                            var img = doc.Render(si, 0, 0, pm.DisplayW, pm.DisplayH, pw, ph, false);
                            File.WriteAllBytes(Path.Combine(o.Output, "reference", $"page-{pm.Index + 1:D4}.png"), Png.Encode(ToRgb(img), img.Width, img.Height, 3));
                        }
                    }
                }
            }

            // ---- write per-target outputs ----
            var targetReports = new Dictionary<string, Dictionary<string, object>>();
            var pageReports = new Dictionary<int, Dictionary<string, object>>();
            foreach (var pm in pages) pageReports[pm.Index] = new Dictionary<string, object> { ["page"] = pm.Index + 1, ["operations"] = pm.Ops.Count };
            foreach (var prof in o.Targets)
            {
                job.Report("write", 0, prof.Target);
                targetReports[prof.Target] = WriteTarget(o, prof, lowered[prof.Target], textures, composites, imageUses, pageReports, diag, emptyComposites);
            }

            // ---- status ----
            int errors = diag.Count(Severity.Error);
            var reviewCommon = new List<object>();
            if (ctx.Fonts.LicenseReview) reviewCommon.Add("font-license-review");
            if (diag.Has("font.substituted") || diag.Has("font.license-substituted")) reviewCommon.Add("substitute-fonts");
            var result = new CompileResult { Output = o.Output };
            foreach (var kv in targetReports)
            {
                var review = new List<object>(reviewCommon);
                var fails = (List<object>)kv.Value["budgetFailures"];
                if (kv.Key == "quest" && fails.Count == 0) review.Add("device-test-required");
                string status = errors > 0 || fails.Count > 0 ? "fail" : review.Count > 0 ? "needs-review" : "pass";
                kv.Value["status"] = status;
                kv.Value["reviewReasons"] = review;
                result.TargetStatus[kv.Key] = status;
            }

            // ---- layout / manifest / diagnostics ----
            var layout = new List<object>();
            double maxW = 0, totalW = 0;
            var widths = new List<double>();
            foreach (var pm in pages) { double w = pm.DisplayW * pm.UserUnit / 72; widths.Add(w); maxW = Math.Max(maxW, w); totalW += w; }
            double gap = maxW * o.PageGap;
            totalW += gap * Math.Max(0, pages.Count - 1);
            double x = -totalW / 2;
            for (int i = 0; i < pages.Count; i++)
            {
                layout.Add(new Dictionary<string, object> { ["page"] = pages[i].Index + 1, ["position"] = new List<object> { x + widths[i] / 2, 0.0, 0.0 } });
                x += widths[i] + gap;
            }
            var pagesJson = new List<object>();
            foreach (var pm in pages)
            {
                var tmap = new Dictionary<string, object>();
                foreach (var prof in o.Targets) tmap[prof.Target] = $"targets/{prof.Target}/pages/page-{pm.Index + 1:D4}.json";
                pagesJson.Add(new Dictionary<string, object>
                {
                    ["page"] = pm.Index + 1,
                    ["mediaBox"] = R(pm.MediaBox), ["cropBox"] = R(pm.CropBox), ["rotation"] = pm.Rotation, ["userUnit"] = pm.UserUnit,
                    ["displaySizePt"] = new List<object> { pm.DisplayW, pm.DisplayH },
                    ["unitySize"] = new List<object> { pm.DisplayW * pm.UserUnit / 72, pm.DisplayH * pm.UserUnit / 72 },
                    ["targets"] = tmap,
                });
            }
            var assets = new Dictionary<string, object>();
            foreach (var f in Directory.GetFiles(o.Output, "*", SearchOption.AllDirectories))
            {
                var rel = f.Substring(o.Output.Length).TrimStart('\\', '/').Replace('\\', '/');
                if (rel == "manifest.json" || rel == "diagnostics.json") continue;
                assets[rel] = "sha256:" + Sha(File.ReadAllBytes(f));
            }
            var targetsJson = new Dictionary<string, object>();
            foreach (var kv in targetReports) targetsJson[kv.Key] = kv.Value;
            var manifest = new Dictionary<string, object>
            {
                ["format"] = PackageFormat,
                ["schemaVersion"] = 1,
                ["irSchemaVersion"] = 1,
                ["compilerVersion"] = Version,
                ["sourceHash"] = "sha256:" + sourceHash,
                ["sourceName"] = Path.GetFileNameWithoutExtension(o.Input),
                ["options"] = new Dictionary<string, object>
                {
                    ["mode"] = o.Mode, ["pages"] = o.Pages, ["worldScale"] = o.WorldScale,
                    ["fontRights"] = o.FontRightsDeclared ? "declared" : "verify",
                    ["translucency"] = o.PrecomposeTranslucent ? "precompose" : "native",
                },
                ["pageToUnity"] = "72 PDF points * UserUnit = 1 Unity unit; page centred at origin, front faces -Z",
                ["pages"] = pagesJson,
                ["layout"] = layout,
                ["targets"] = targetsJson,
                ["assets"] = assets,
                ["warningCount"] = diag.Count(Severity.Warning),
                ["errorCount"] = errors,
                ["licenseNotice"] = "Converting a PDF does not grant any right to reproduce or distribute the document, its images or its fonts.",
            };
            File.WriteAllText(Path.Combine(o.Output, "manifest.json"), JsonOut.Write(manifest), new UTF8Encoding(false));
            var msgs = new List<object>();
            foreach (var d in diag.Items)
            {
                var m = new Dictionary<string, object> { ["code"] = d.Code, ["severity"] = d.Severity.ToString().ToLowerInvariant(), ["message"] = d.Message };
                if (d.Page > 0) m["page"] = d.Page;
                if (d.Fallback != null) m["fallback"] = d.Fallback;
                if (d.Occurrences > 1) m["occurrences"] = d.Occurrences;
                msgs.Add(m);
            }
            var fontsJson = new List<object>();
            foreach (var f in ctx.Fonts.Resources.Values)
                fontsJson.Add(new Dictionary<string, object>
                {
                    ["ref"] = f.Ref, ["name"] = f.Name, ["subtype"] = f.Subtype, ["embedded"] = f.Embedded, ["substitute"] = f.Substitute,
                    ["license"] = new Dictionary<string, object> { ["fsType"] = f.FsType.HasValue ? (object)f.FsType.Value : null, ["status"] = f.LicenseStatus, ["program"] = f.Program },
                    ["glyphs"] = f.Glyphs.Count,
                });
            var pageList = new List<object>();
            foreach (var pm in pages) pageList.Add(pageReports[pm.Index]);
            var diagnostics = new Dictionary<string, object>
            {
                ["schemaVersion"] = 1,
                ["sourceHash"] = "sha256:" + sourceHash,
                ["compilerVersion"] = Version,
                ["targets"] = targetsJson,
                ["pages"] = pageList,
                ["fonts"] = fontsJson,
                ["messages"] = msgs,
            };
            File.WriteAllText(Path.Combine(o.Output, "diagnostics.json"), JsonOut.Write(diagnostics), new UTF8Encoding(false));
            result.Manifest = manifest;
            result.Warnings = diag.Count(Severity.Warning);
            result.Errors = errors;
            job.Report("done", 1);
            return result;
        }

        /// <summary>
        /// Synthetic page = original page with every level of the (nested) content cut at <paramref name="pt"/>:
        /// the deepest stream is cut after (or before) the operator; each enclosing level is cut before the Do
        /// that invoked it and ends with a Do of the truncated copy of the child form.
        /// </summary>
        static int TruncatedPage(SyntheticPages syn, PageModel pm, TruncPoint pt, bool after)
        {
            var level = pt;
            var content = Slice(level.Content, after ? level.End : level.Start);
            PdfRef childRef = null;
            string childName = null;
            int guard = 0;
            while (level.Form != null && level.Parent != null && guard++ < 64)
            {
                var dict = new Dictionary<string, object>();
                foreach (var kv in level.Form.Dict.Items)
                    if (kv.Key != "Length" && kv.Key != "Filter" && kv.Key != "DecodeParms" && kv.Key != "DL") dict[kv.Key] = kv.Value;
                if (childRef != null) dict["Resources"] = ResourcesWith(level.Resources, childName, childRef);
                else if (!dict.ContainsKey("Resources") && level.Resources != null) dict["Resources"] = (object)level.Resources.Ref ?? level.Resources;
                childRef = syn.AddStream(dict, content);
                childName = "P2UTrunc" + childRef.Num;
                var parent = level.Parent;
                var head = Slice(parent.Content, parent.Start);
                var tail = PdfUtil.Latin1.GetBytes($"\n/{childName} Do\n");
                content = new byte[head.Length + tail.Length];
                Buffer.BlockCopy(head, 0, content, 0, head.Length);
                Buffer.BlockCopy(tail, 0, content, head.Length, tail.Length);
                level = parent;
            }
            object res = childRef != null ? ResourcesWith(level.Resources, childName, childRef) : null;
            return syn.AddPageCopy(pm.PageDict, content, res);
        }

        static byte[] Slice(byte[] data, int end)
        {
            end = Math.Max(0, Math.Min(end, data?.Length ?? 0));
            var o = new byte[end];
            if (end > 0) Buffer.BlockCopy(data, 0, o, 0, end);
            return o;
        }

        static Dictionary<string, object> ResourcesWith(PdfDict res, string name, PdfRef form)
        {
            var d = new Dictionary<string, object>();
            if (res != null) foreach (var kv in res.Items) d[kv.Key] = kv.Value;
            var x = new Dictionary<string, object>();
            var orig = res?.GetDict("XObject");
            if (orig != null) foreach (var kv in orig.Items) x[kv.Key] = kv.Value;
            x[name] = form;
            d["XObject"] = x;
            return d;
        }

        static List<object> R(Rect r) => new List<object> { r.X0, r.Y0, r.X1, r.Y1 };

        static byte[] ToRgb(RgbaImage img)
        {
            var o = new byte[img.Width * img.Height * 3];
            for (int i = 0, j = 0; i < img.Pixels.Length; i += 4, j += 3) { o[j] = img.Pixels[i]; o[j + 1] = img.Pixels[i + 1]; o[j + 2] = img.Pixels[i + 2]; }
            return o;
        }

        static object ImageResources(ImageRef img)
        {
            if (img.XObject != null)
                return new Dictionary<string, object> { ["XObject"] = new Dictionary<string, object> { ["Im0"] = img.XObject } };
            if (img.Resources == null) return null;
            return (object)img.Resources.Ref ?? img.Resources;
        }

        static byte[] ImageContent(ImageRef img, int w, int h)
        {
            var sb = new StringBuilder("q ");
            if (img.Stencil) sb.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.####} {1:0.####} {2:0.####} rg ", img.R, img.G, img.B));
            sb.Append($"{w} 0 0 {h} 0 0 cm ");
            if (img.XObject != null)
            {
                sb.Append("/Im0 Do Q");
                return PdfUtil.Latin1.GetBytes(sb.ToString());
            }
            var head = PdfUtil.Latin1.GetBytes(sb.ToString());
            var tail = PdfUtil.Latin1.GetBytes(" Q");
            var o = new byte[head.Length + img.InlineImage.Length + tail.Length];
            Buffer.BlockCopy(head, 0, o, 0, head.Length);
            Buffer.BlockCopy(img.InlineImage, 0, o, head.Length, img.InlineImage.Length);
            Buffer.BlockCopy(tail, 0, o, head.Length + img.InlineImage.Length, tail.Length);
            return o;
        }

        // ------------------------------------------------------------------
        static PageModel Geometry(PdfDict page, int index, Diagnostics diag)
        {
            Rect Box(string key)
            {
                var a = (PdfFile.Inherited(page, key) as PdfArray)?.ToDoubles();
                return a != null && a.Length == 4 ? new Rect(a[0], a[1], a[2], a[3]) : default;
            }
            var media = Box("MediaBox");
            if (media.IsEmpty) media = new Rect(0, 0, 612, 792);
            var crop = Box("CropBox");
            crop = crop.IsEmpty ? media : crop.Intersect(media);
            if (crop.IsEmpty) { diag.Warn("page.box", "CropBox does not intersect MediaBox; MediaBox used", index + 1); crop = media; }
            int rot = (int)PdfUtil.ToDouble(PdfFile.Inherited(page, "Rotate"), 0);
            rot = ((rot % 360) + 360) % 360;
            if (rot % 90 != 0) { diag.Warn("page.rotate", $"invalid Rotate {rot}; ignored", index + 1); rot = 0; }
            double uu = page.GetNumber("UserUnit", 1);
            double x0 = crop.X0, y0 = crop.Y0, x1 = crop.X1, y1 = crop.Y1, w = x1 - x0, h = y1 - y0;
            Matrix m;
            double dw, dh;
            switch (rot)
            {
                case 90: m = new Matrix(0, 1, 1, 0, -y0, -x0); dw = h; dh = w; break;
                case 180: m = new Matrix(-1, 0, 0, 1, x1, -y0); dw = w; dh = h; break;
                case 270: m = new Matrix(0, -1, -1, 0, y1, x1); dw = h; dh = w; break;
                default: m = new Matrix(1, 0, 0, -1, -x0, y1); dw = w; dh = h; break;
            }
            var annots = page.GetArray("Annots");
            var pm = new PageModel
            {
                Index = index, PageDict = page, MediaBox = media, CropBox = crop, Rotation = rot, UserUnit = uu,
                DisplayW = dw, DisplayH = dh, PageToDisplay = m, Annotations = annots?.Count ?? 0,
            };
            if (pm.Annotations > 0) diag.Info("scope.annotations", $"{pm.Annotations} annotation(s) not rendered (out of scope)", index + 1);
            return pm;
        }

        public static List<int> ParsePages(string spec, int count)
        {
            var o = new SortedSet<int>();
            if (string.IsNullOrWhiteSpace(spec) || spec.Trim() == "all")
            {
                for (int i = 0; i < count; i++) o.Add(i);
                return new List<int>(o);
            }
            foreach (var raw in spec.Split(','))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                int lo, hi;
                var dash = part.IndexOf('-');
                if (dash >= 0)
                {
                    lo = dash == 0 ? 1 : int.Parse(part.Substring(0, dash));
                    hi = dash == part.Length - 1 ? count : int.Parse(part.Substring(dash + 1));
                }
                else lo = hi = int.Parse(part);
                if (lo < 1 || hi > count || lo > hi) throw new CompileException("options.pages", $"page range {part} outside 1-{count}");
                for (int i = lo - 1; i < hi; i++) o.Add(i);
            }
            return new List<int>(o);
        }

        static string Sha(byte[] d)
        {
            using (var s = SHA256.Create()) return BitConverter.ToString(s.ComputeHash(d)).Replace("-", "").ToLowerInvariant();
        }

        static void PrepareOutput(string path)
        {
            if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length > 0)
            {
                var man = Path.Combine(path, "manifest.json");
                bool ours = File.Exists(man) && File.ReadAllText(man).Contains("\"" + PackageFormat + "\"");
                if (!ours) throw new CompileException("output.not-empty", $"output directory {path} is not empty and is not a pdf2unity package");
                foreach (var name in new[] { "manifest.json", "diagnostics.json", "document.ir.json.gz" }) { var f = Path.Combine(path, name); if (File.Exists(f)) File.Delete(f); }
                foreach (var name in new[] { "resources", "targets", "reference" }) { var d = Path.Combine(path, name); if (Directory.Exists(d)) Directory.Delete(d, true); }
            }
            Directory.CreateDirectory(path);
        }

        // ------------------------------------------------------------------
        static Dictionary<string, object> WriteTarget(CompileOptions o, Profile prof, List<(PageModel page, PageLowering low, List<CompositeRegion> comps)> pages,
            Dictionary<string, RgbaImage> textures, Dictionary<(string, CompositeRegion), (RgbaImage img, double dpi)> composites,
            Dictionary<string, (ImageRef img, double ratio)> imageUses, Dictionary<int, Dictionary<string, object>> pageReports, Diagnostics diag,
            HashSet<CompositeRegion> emptyComposites)
        {
            string t = prof.Target;
            string tdir = Path.Combine(o.Output, "targets", t);
            foreach (var sub in new[] { "pages", "textures", "composites" }) Directory.CreateDirectory(Path.Combine(tdir, sub));
            // textures (resampled for this target)
            var texFiles = new Dictionary<string, (string file, int w, int h)>();
            double texBytes = 0;
            var neededRatio = new Dictionary<string, double>();
            foreach (var (_, low, _) in pages) foreach (var u in low.Images) neededRatio[u.Key] = Math.Max(neededRatio.TryGetValue(u.Key, out var r) ? r : 0, u.Value.Ratio);
            foreach (var kv in neededRatio)
            {
                var src = textures[kv.Key];
                var img = imageUses[kv.Key].img;
                double ratio = Math.Min(1, kv.Value);
                int w = Math.Max(1, (int)Math.Ceiling(img.Width * ratio)), h = Math.Max(1, (int)Math.Ceiling(img.Height * ratio));
                double s = Math.Min(1, (double)prof.MaxTextureSize / Math.Max(w, h));
                w = Math.Max(1, (int)(w * s)); h = Math.Max(1, (int)(h * s));
                var res = (w >= src.Width && h >= src.Height) ? src : Resample(src, Math.Min(w, src.Width), Math.Min(h, src.Height));
                var name = "i-" + Sha(Encoding.UTF8.GetBytes(kv.Key)).Substring(0, 24) + ".png";
                var rel = $"targets/{t}/textures/{name}";
                File.WriteAllBytes(Path.Combine(o.Output, rel), Png.Encode(res.Pixels, res.Width, res.Height, 4));
                texFiles[kv.Key] = (rel, res.Width, res.Height);
                texBytes += res.Width * res.Height * prof.BytesPerPixel * prof.MipFactor;
            }
            var failures = new List<object>();
            long totalTris = 0;
            var pageFiles = new List<object>();
            foreach (var (pm, low, allComps) in pages)
            {
                var comps = allComps.FindAll(r => !emptyComposites.Contains(r));
                int pno = pm.Index + 1;
                double k = pm.UserUnit / 72.0, W = pm.DisplayW, H = pm.DisplayH;
                var toUnity = pm.PageToDisplay.Then(new Matrix(k, 0, 0, -k, -W / 2 * k, H / 2 * k));
                // Merge native items and composites in painting order.
                var items = new List<DrawItem>();
                int dropped = 0;
                foreach (var it in low.Items)
                {
                    bool covered = false;
                    foreach (var r in comps)
                        if (!r.Partial && r.After == null && r.Rect.Contains(it.DisplayBox)) { covered = true; break; }
                    if (covered) dropped++;
                    else items.Add(it);
                }
                var compMeta = new List<object>();
                for (int ri = 0; ri < comps.Count; ri++)
                {
                    var r = comps[ri];
                    var (img, dpi) = composites[(t, r)];
                    var rel = $"targets/{t}/composites/page-{pno:D4}-r{ri:D2}.png";
                    File.WriteAllBytes(Path.Combine(o.Output, rel), r.Partial ? Png.Encode(img.Pixels, img.Width, img.Height, 4) : Png.Encode(ToRgb(img), img.Width, img.Height, 3));
                    texBytes += img.Width * img.Height * prof.BytesPerPixel * prof.MipFactor;
                    var mesh = new Mesh2();
                    // quad in display space -> page space via inverse of PageToDisplay
                    pm.PageToDisplay.TryInvert(out var inv);
                    foreach (var (dx, dy) in new[] { (r.Rect.X0, r.Rect.Y0), (r.Rect.X1, r.Rect.Y0), (r.Rect.X1, r.Rect.Y1), (r.Rect.X0, r.Rect.Y1) })
                    {
                        inv.Apply(dx, dy, out var px, out var py);
                        mesh.V.Add(px); mesh.V.Add(py);
                    }
                    mesh.I.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
                    var colors = new byte[16];
                    for (int c = 0; c < 16; c++) colors[c] = 255;
                    items.Add(new DrawItem
                    {
                        Textured = true, Texture = rel, Composite = true, CompositeAlpha = r.Partial, Mesh = mesh, Colors = colors,
                        Uv = new double[] { 0, 1, 1, 1, 1, 0, 0, 0 }, Order = r.Order, DisplayBox = r.Rect,
                    });
                    var reasons = new List<object>();
                    foreach (var rs in r.Reasons) reasons.Add(rs);
                    var ops = new List<object>();
                    for (int q = 0; q < Math.Min(64, r.OpIds.Count); q++) ops.Add(r.OpIds[q]);
                    compMeta.Add(new Dictionary<string, object>
                    {
                        ["file"] = rel, ["rectPt"] = R(r.Rect), ["dpi"] = Math.Round(dpi, 3), ["pixelSize"] = new List<object> { img.Width, img.Height },
                        ["reasons"] = reasons, ["operations"] = ops, ["order"] = r.Order, ["delta"] = r.Partial,
                    });
                }
                items.Sort((a, b) => a.Order.CompareTo(b.Order)); // List.Sort is unstable: break ties below
                StableSort(items);
                // Batch consecutive items with the same material.
                var batches = new List<(bool textured, string tex, bool composite, List<DrawItem> items)>();
                foreach (var it in items)
                {
                    var key = it.Textured ? (it.Composite ? it.Texture : texFiles.TryGetValue(it.Texture, out var tf) ? tf.file : null) : null;
                    if (it.Textured && key == null) continue;
                    var last = batches.Count > 0 ? batches[batches.Count - 1] : default;
                    if (batches.Count > 0 && last.textured == it.Textured && last.tex == key && !it.Composite && !last.composite) last.items.Add(it);
                    else batches.Add((it.Textured, key, it.Composite, new List<DrawItem> { it }));
                }
                var geomPath = $"targets/{t}/pages/page-{pno:D4}.geom";
                var batchJson = new List<object>();
                long tris = 0, verts = 0;
                double cover = 0;
                using (var fs = new FileStream(Path.Combine(o.Output, geomPath), FileMode.Create))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(Encoding.ASCII.GetBytes("P2UG"));
                    bw.Write(1u);
                    bw.Write((uint)batches.Count);
                    for (int bi = 0; bi < batches.Count; bi++)
                    {
                        var (textured, tex, composite, list) = batches[bi];
                        var pos = new List<float>();
                        var col = new List<byte>();
                        var uv = new List<float>();
                        var idx = new List<uint>();
                        double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
                        foreach (var it in list)
                        {
                            int baseV = pos.Count / 2;
                            var ux = new double[it.Mesh.VertexCount];
                            var uy = new double[it.Mesh.VertexCount];
                            for (int v = 0; v < it.Mesh.VertexCount; v++)
                            {
                                toUnity.Apply(it.Mesh.V[2 * v], it.Mesh.V[2 * v + 1], out ux[v], out uy[v]);
                                pos.Add((float)ux[v]); pos.Add((float)uy[v]);
                                bx0 = Math.Min(bx0, ux[v]); bx1 = Math.Max(bx1, ux[v]); by0 = Math.Min(by0, uy[v]); by1 = Math.Max(by1, uy[v]);
                                if (textured) { uv.Add((float)it.Uv[2 * v]); uv.Add((float)it.Uv[2 * v + 1]); }
                            }
                            col.AddRange(it.Colors);
                            for (int q = 0; q + 2 < it.Mesh.I.Count; q += 3)
                            {
                                int a = it.Mesh.I[q], b = it.Mesh.I[q + 1], c = it.Mesh.I[q + 2];
                                double cross = (ux[b] - ux[a]) * (uy[c] - uy[a]) - (uy[b] - uy[a]) * (ux[c] - ux[a]);
                                if (Math.Abs(cross) < 1e-14) continue;
                                cover += Math.Abs(cross) / 2;
                                if (cross > 0) { var tmp = b; b = c; c = tmp; } // clockwise seen from -Z
                                idx.Add((uint)(a + baseV)); idx.Add((uint)(b + baseV)); idx.Add((uint)(c + baseV));
                            }
                        }
                        bw.Write((uint)(pos.Count / 2));
                        bw.Write((uint)idx.Count);
                        bw.Write(textured ? 1u : 0u);
                        foreach (var f in pos) bw.Write(f);
                        bw.Write(col.ToArray());
                        if (textured) foreach (var f in uv) bw.Write(f);
                        foreach (var ix in idx) bw.Write(ix);
                        tris += idx.Count / 3;
                        verts += pos.Count / 2;
                        var entry = new Dictionary<string, object>
                        {
                            ["order"] = bi, ["kind"] = textured ? "textured" : "solid", ["composite"] = composite,
                            ["vertexCount"] = pos.Count / 2, ["indexCount"] = idx.Count,
                            ["bounds"] = new List<object> { bx0, by0, bx1, by1 },
                        };
                        if (textured) { entry["texture"] = tex; entry["alpha"] = !composite || list[0].CompositeAlpha; }
                        batchJson.Add(entry);
                    }
                }
                totalTris += tris;
                double area = pm.DisplayW * pm.UserUnit / 72 * pm.DisplayH * pm.UserUnit / 72;
                double overdraw = area > 0 ? cover / area : 0;
                var stats = new Dictionary<string, object>
                {
                    ["operations"] = pm.Ops.Count, ["drawItems"] = low.Items.Count, ["droppedUnderComposite"] = dropped,
                    ["unsupportedOperations"] = low.Unsupported.Count, ["batches"] = batches.Count, ["triangles"] = tris,
                    ["vertices"] = verts, ["fallbackRegions"] = comps.Count, ["overdraw"] = Math.Round(overdraw, 3),
                };
                if (prof.EnforceBudgets)
                {
                    if (tris > prof.MaxTrianglesPerPage) failures.Add($"page {pno}: {tris} triangles > {prof.MaxTrianglesPerPage}");
                    if (batches.Count > prof.MaxBatchesPerPage) failures.Add($"page {pno}: {batches.Count} batches > {prof.MaxBatchesPerPage}");
                    if (overdraw > prof.MaxOverdraw) failures.Add($"page {pno}: overdraw {overdraw:0.00} > {prof.MaxOverdraw}");
                }
                var targetsStats = pageReports[pm.Index].TryGetValue("targets", out var ts) ? (Dictionary<string, object>)ts : new Dictionary<string, object>();
                targetsStats[t] = stats;
                pageReports[pm.Index]["targets"] = targetsStats;
                var warnings = new SortedSet<string>();
                foreach (var d in diag.Items) if (d.Page == pno && d.Severity != Severity.Info) warnings.Add(d.Message);
                pageReports[pm.Index]["warnings"] = new List<object>(warnings);
                var pageJson = new Dictionary<string, object>
                {
                    ["page"] = pno,
                    ["unitySize"] = new List<object> { pm.DisplayW * pm.UserUnit / 72, pm.DisplayH * pm.UserUnit / 72 },
                    ["geometry"] = geomPath, ["batches"] = batchJson, ["composites"] = compMeta, ["stats"] = stats,
                };
                var rel2 = $"targets/{t}/pages/page-{pno:D4}.json";
                File.WriteAllText(Path.Combine(o.Output, rel2), JsonOut.Write(pageJson), new UTF8Encoding(false));
                pageFiles.Add(rel2);
            }
            double mb = texBytes / (1024 * 1024);
            if (prof.EnforceBudgets)
            {
                if (totalTris > prof.MaxTrianglesTotal) failures.Add($"document: {totalTris} triangles > {prof.MaxTrianglesTotal}");
                if (mb > prof.MaxTextureMemoryMB) failures.Add($"document: texture memory {mb:0.0} MB > {prof.MaxTextureMemoryMB} MB");
            }
            foreach (var f in failures) diag.Warn("target.budget", $"{t}: {f}");
            return new Dictionary<string, object>
            {
                ["profile"] = prof.Name, ["textureCompression"] = prof.Target == "quest" ? "astc6x6" : "high",
                ["maxTextureSize"] = prof.MaxTextureSize, ["pages"] = pageFiles, ["triangles"] = totalTris,
                ["textures"] = texFiles.Count, ["textureMemoryMB"] = Math.Round(mb, 2), ["budgetFailures"] = failures,
            };
        }

        /// <summary>Pixels of <paramref name="after"/> that differ from <paramref name="before"/> (dilated by 1px); null if none.</summary>
        static RgbaImage Delta(RgbaImage after, RgbaImage before, out int x0, out int y0, out int x1, out int y1)
        {
            int w = after.Width, h = after.Height;
            var changed = new bool[w * h];
            x0 = w; y0 = h; x1 = 0; y1 = 0;
            for (int i = 0; i < w * h; i++)
            {
                int a = i * 4;
                int d = Math.Max(Math.Abs(after.Pixels[a] - before.Pixels[a]), Math.Max(Math.Abs(after.Pixels[a + 1] - before.Pixels[a + 1]), Math.Abs(after.Pixels[a + 2] - before.Pixels[a + 2])));
                changed[i] = d > 2;
            }
            // 3x3 dilation, separable: horizontal pass then vertical pass.
            var rowDil = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                int r = y * w;
                for (int x = 0; x < w; x++)
                    rowDil[r + x] = changed[r + x] || (x > 0 && changed[r + x - 1]) || (x + 1 < w && changed[r + x + 1]);
            }
            var o = new RgbaImage(w, h);
            bool any = false;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int k = y * w + x;
                    if (!(rowDil[k] || (y > 0 && rowDil[k - w]) || (y + 1 < h && rowDil[k + w]))) continue;
                    any = true;
                    int i = k * 4;
                    o.Pixels[i] = after.Pixels[i]; o.Pixels[i + 1] = after.Pixels[i + 1]; o.Pixels[i + 2] = after.Pixels[i + 2]; o.Pixels[i + 3] = 255;
                    if (x < x0) x0 = x;
                    if (y < y0) y0 = y;
                    if (x + 1 > x1) x1 = x + 1;
                    if (y + 1 > y1) y1 = y + 1;
                }
            return any ? o : null;
        }

        static RgbaImage Crop(RgbaImage src, int x0, int y0, int x1, int y1)
        {
            int w = x1 - x0, h = y1 - y0;
            var o = new RgbaImage(w, h);
            for (int y = 0; y < h; y++) Buffer.BlockCopy(src.Pixels, ((y0 + y) * src.Width + x0) * 4, o.Pixels, y * w * 4, w * 4);
            return o;
        }

        static void StableSort(List<DrawItem> items)
        {
            // Insertion-stable ordering by Order, preserving the original sequence for equal keys.
            var indexed = new List<(DrawItem it, int i)>();
            for (int i = 0; i < items.Count; i++) indexed.Add((items[i], i));
            indexed.Sort((a, b) => a.it.Order != b.it.Order ? a.it.Order.CompareTo(b.it.Order) : a.i.CompareTo(b.i));
            for (int i = 0; i < items.Count; i++) items[i] = indexed[i].it;
        }

        /// <summary>Box-filter downscale of an RGBA image (alpha-weighted).</summary>
        static RgbaImage Resample(RgbaImage src, int w, int h)
        {
            var o = new RgbaImage(w, h);
            double sx = (double)src.Width / w, sy = (double)src.Height / h;
            for (int y = 0; y < h; y++)
            {
                int y0 = (int)(y * sy), y1 = Math.Max(y0 + 1, (int)((y + 1) * sy));
                for (int x = 0; x < w; x++)
                {
                    int x0 = (int)(x * sx), x1 = Math.Max(x0 + 1, (int)((x + 1) * sx));
                    double r = 0, g = 0, b = 0, a = 0;
                    int n = 0;
                    for (int yy = y0; yy < y1 && yy < src.Height; yy++)
                        for (int xx = x0; xx < x1 && xx < src.Width; xx++)
                        {
                            int i = (yy * src.Width + xx) * 4;
                            double al = src.Pixels[i + 3];
                            r += src.Pixels[i] * al; g += src.Pixels[i + 1] * al; b += src.Pixels[i + 2] * al; a += al;
                            n++;
                        }
                    int oi = (y * w + x) * 4;
                    if (a > 0) { o.Pixels[oi] = (byte)(r / a + 0.5); o.Pixels[oi + 1] = (byte)(g / a + 0.5); o.Pixels[oi + 2] = (byte)(b / a + 0.5); }
                    o.Pixels[oi + 3] = (byte)(n > 0 ? a / n + 0.5 : 0);
                }
            }
            return o;
        }
    }
}
