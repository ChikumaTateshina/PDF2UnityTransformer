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
        public double TransparentCutoff;      // translucent objects with opacity <= this are not drawn (0 = off)
        public string ImageQuality = "light"; // light (visually lossless reduction, JPEG, atlases) | small (download size first) | faithful
        public double CurveDetail = 1;        // curve tolerance multiplier: < 1 finer, > 1 fewer triangles
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

        /// <summary>Reads the PDF and interprets the selected pages (shared by <see cref="Compile"/> and the preview).</summary>
        internal static List<PageModel> Frontend(CompileOptions o, Job job, Diagnostics diag, out PdfFile file, out FrontendContext ctx, out string sourceHash,
            Action beforeParse = null)
        {
            var limits = o.Limits;
            job.Report("frontend", 0);
            var info = new FileInfo(o.Input);
            if (!info.Exists) throw new CompileException("input.missing", "PDF file not found");
            if (info.Length > limits.MaxInputBytes) throw new CompileException("input.too-large", $"input is {info.Length} bytes");
            var bytes = File.ReadAllBytes(o.Input);
            sourceHash = Sha(bytes);
            beforeParse?.Invoke();
            file = new PdfFile(bytes, limits);
            if (file.Reconstructed) diag.Warn("input.repaired", "cross-reference table was damaged and has been reconstructed");
            var allPages = file.Pages();
            if (allPages.Count == 0) throw new CompileException("input.corrupt", "document has no pages");
            if (allPages.Count > limits.MaxPages) throw new CompileException("input.pages", $"{allPages.Count} pages exceed the limit");
            var indices = ParsePages(o.Pages, allPages.Count);

            var cmyk = CmykTable.Get(o.CacheDir);
            ctx = new FrontendContext
            {
                File = file, Diag = diag, Limits = limits, Job = job,
                ColorSpaces = new ColorSpaceFactory(cmyk, diag, limits),
                Fonts = new FontLoader(diag, limits, new FontOptions { SubstituteDir = o.SubstituteFontDir, RightsDeclared = o.FontRightsDeclared }),
                OC = new OptionalContent(file, diag),
            };
            if (file.Root.Has("AcroForm")) diag.Info("scope.forms", "interactive form fields are out of scope");

            var pages = new List<PageModel>();
            for (int k = 0; k < indices.Count; k++)
            {
                job.Report("frontend", (float)k / indices.Count, $"{k + 1}/{indices.Count}");
                var pm = Geometry(allPages[indices[k]], indices[k], diag);
                new Interpreter(ctx, pm).Run();
                pages.Add(pm);
            }
            return pages;
        }

        public static CompileResult Compile(CompileOptions o, Job job)
        {
            var diag = new Diagnostics();
            var pages = Frontend(o, job, diag, out var file, out var ctx, out var sourceHash, () => PrepareOutput(o.Output));

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
                    var low = new PageLowering(pm, prof, o.Mode, diag, glyphCache, o.PrecomposeTranslucent, job, o.CurveDetail, o.TransparentCutoff);
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
                        composites[(target, r)] = (ImageOps.Crop(delta, cx0, cy0, cx1, cy1), dpi);
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
                    ["imageQuality"] = o.ImageQuality,
                    ["curveDetail"] = o.CurveDetail,
                    ["transparentCutoff"] = o.TransparentCutoff,
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
            bool light = o.ImageQuality != "faithful", small = o.ImageQuality == "small";
            double capDpi = small ? prof.SmallImageDpi : prof.LightImageDpi;
            string tdir = Path.Combine(o.Output, "targets", t);
            foreach (var sub in new[] { "pages", "textures", "composites" }) Directory.CreateDirectory(Path.Combine(tdir, sub));
            // ---- textures: images (resampled for this target) and composites ----
            var sources = new List<TexSource>();
            var neededRatio = new Dictionary<string, double>();
            foreach (var (_, low, _) in pages) foreach (var u in low.Images) neededRatio[u.Key] = Math.Max(neededRatio.TryGetValue(u.Key, out var r) ? r : 0, u.Value.Ratio);
            long pixelsBefore = 0, pixelsAfter = 0;
            int reduced = 0;
            foreach (var kv in neededRatio)
            {
                var src = textures[kv.Key];
                var img = imageUses[kv.Key].img;
                double ratio = Math.Min(1, kv.Value);
                int w = Math.Max(1, (int)Math.Ceiling(img.Width * ratio)), h = Math.Max(1, (int)Math.Ceiling(img.Height * ratio));
                double s = Math.Min(1, (double)prof.MaxTextureSize / Math.Max(w, h));
                w = Math.Max(1, (int)(w * s)); h = Math.Max(1, (int)(h * s));
                long full = (long)Math.Min(w, src.Width) * Math.Min(h, src.Height);
                pixelsBefore += full;
                if (light && capDpi < prof.ImageDpi)
                {
                    double ls = Math.Min(1, Math.Max(kv.Value, 1e-6) * capDpi / prof.ImageDpi) / Math.Max(ratio, 1e-6);
                    w = Math.Max(1, (int)Math.Ceiling(w * Math.Min(1, ls))); h = Math.Max(1, (int)Math.Ceiling(h * Math.Min(1, ls)));
                }
                var res = (w >= src.Width && h >= src.Height) ? src : ImageOps.Resample(src, Math.Min(w, src.Width), Math.Min(h, src.Height));
                if (light) res = ImageOps.VisualReduce(res);
                res = ImageOps.Multiple4(res);
                if ((long)res.Width * res.Height < full * 0.9) reduced++;
                pixelsAfter += (long)res.Width * res.Height;
                sources.Add(new TexSource { Key = "i:" + kv.Key, Name = "i-" + Sha(Encoding.UTF8.GetBytes(kv.Key)).Substring(0, 24), Image = res });
            }
            var compKeys = new Dictionary<CompositeRegion, string>();
            var compAlpha = new Dictionary<CompositeRegion, bool>();
            foreach (var (pm, _, allComps) in pages)
            {
                int ri = 0;
                foreach (var r in allComps)
                {
                    if (emptyComposites.Contains(r)) continue;
                    var (img, dpi) = composites[(t, r)];
                    if (small && dpi > capDpi * 1.05)
                        img = ImageOps.Resample(img, Math.Max(4, (int)Math.Ceiling(img.Width * capDpi / dpi)), Math.Max(4, (int)Math.Ceiling(img.Height * capDpi / dpi)));
                    // Light mode: a delta composite whose effect changed (almost) every pixel becomes opaque. The few
                    // unchanged pixels already hold the rendered page there, so filling them changes nothing visible.
                    bool alpha = r.Partial && (!light || TransparentFraction(img) > 0.005);
                    if (r.Partial && !alpha) img = Opaque(img);
                    if (light && !alpha) img = ImageOps.VisualReduce(img);
                    var key = $"c:{pm.Index + 1}:{ri}";
                    compKeys[r] = key;
                    compAlpha[r] = alpha;
                    sources.Add(new TexSource { Key = key, Name = $"page-{pm.Index + 1:D4}-r{ri:D2}", Image = ImageOps.Multiple4(img), Composite = true, Partial = alpha });
                    ri++;
                }
            }
            if (reduced > 0)
                diag.Info("image.reduced", $"{t}: {reduced} image(s) downscaled by the light image mode ({pixelsBefore / 1e6:0.0} -> {pixelsAfter / 1e6:0.0} Mpx)");
            var slots = WriteTextures(o, prof, light, sources, out double texBytes, out int texFileCount, out int atlasCount);
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
                    var slot = slots[compKeys[r]];
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
                        Textured = true, Texture = compKeys[r], Composite = true, CompositeAlpha = compAlpha[r], Mesh = mesh, Colors = colors,
                        Uv = new double[] { 0, 1, 1, 1, 1, 0, 0, 0 }, Order = r.Order, DisplayBox = r.Rect,
                    });
                    var reasons = new List<object>();
                    foreach (var rs in r.Reasons) reasons.Add(rs);
                    var ops = new List<object>();
                    for (int q = 0; q < Math.Min(64, r.OpIds.Count); q++) ops.Add(r.OpIds[q]);
                    compMeta.Add(new Dictionary<string, object>
                    {
                        ["file"] = slot.File, ["atlas"] = slot.Atlas, ["rectPt"] = R(r.Rect), ["dpi"] = Math.Round(dpi, 3), ["pixelSize"] = new List<object> { slot.Width, slot.Height },
                        ["reasons"] = reasons, ["operations"] = ops, ["order"] = r.Order, ["delta"] = r.Partial,
                    });
                }
                items.Sort((a, b) => a.Order.CompareTo(b.Order)); // List.Sort is unstable: break ties below
                StableSort(items);
                // Batch consecutive items with the same material.
                var batches = new List<Batch>();
                foreach (var it in items)
                {
                    TexSlot slot = null;
                    if (it.Textured && !slots.TryGetValue(it.Composite ? it.Texture : "i:" + it.Texture, out slot)) continue;
                    var last = batches.Count > 0 ? batches[batches.Count - 1] : null;
                    if (last != null)
                    {
                        if (slot == null && last.Slot == null) { last.Items.Add(it); continue; }
                        if (slot != null && last.Slot != null && last.Slot.File == slot.File && (slot.Atlas || !it.Composite && !last.Composite)) { last.Items.Add(it); continue; }
                    }
                    batches.Add(new Batch { Slot = slot, Composite = it.Composite && slot != null && !slot.Atlas, Items = new List<DrawItem> { it } });
                }
                // A vector run next to an atlas batch joins it (sampling the atlas' white block): one batch less
                // (two when it bridges two batches of the same atlas) for 8 bytes of UV per vertex. Very long runs
                // stay separate, so the extra mesh memory per saved batch stays small.
                for (int bi = 0; bi < batches.Count; bi++)
                {
                    var s = batches[bi];
                    if (s.Slot != null) continue;
                    int sv = 0;
                    foreach (var it in s.Items) sv += it.Mesh.VertexCount;
                    if (sv > MaxBridgeVertices) continue;
                    var prev = bi > 0 && batches[bi - 1].Slot?.Atlas == true ? batches[bi - 1] : null;
                    var next = bi + 1 < batches.Count && batches[bi + 1].Slot?.Atlas == true ? batches[bi + 1] : null;
                    if (prev != null)
                    {
                        prev.Items.AddRange(s.Items);
                        batches.RemoveAt(bi);
                        if (next != null && next.Slot.File == prev.Slot.File) { prev.Items.AddRange(next.Items); batches.RemoveAt(bi); }
                        bi--;
                    }
                    else if (next != null)
                    {
                        next.Items.InsertRange(0, s.Items);
                        batches.RemoveAt(bi);
                        bi--;
                    }
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
                        var batch = batches[bi];
                        bool textured = batch.Slot != null, composite = batch.Composite;
                        var list = batch.Items;
                        var pos = new List<float>();
                        var col = new List<byte>();
                        var uv = new List<float>();
                        var idx = new List<uint>();
                        double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
                        foreach (var it in list)
                        {
                            var itemSlot = it.Textured ? slots[it.Composite ? it.Texture : "i:" + it.Texture] : null;
                            int baseV = pos.Count / 2;
                            var ux = new double[it.Mesh.VertexCount];
                            var uy = new double[it.Mesh.VertexCount];
                            for (int v = 0; v < it.Mesh.VertexCount; v++)
                            {
                                toUnity.Apply(it.Mesh.V[2 * v], it.Mesh.V[2 * v + 1], out ux[v], out uy[v]);
                                pos.Add((float)ux[v]); pos.Add((float)uy[v]);
                                bx0 = Math.Min(bx0, ux[v]); bx1 = Math.Max(bx1, ux[v]); by0 = Math.Min(by0, uy[v]); by1 = Math.Max(by1, uy[v]);
                                if (!textured) continue;
                                if (itemSlot == null) { uv.Add((float)batch.Slot.WhiteU); uv.Add((float)batch.Slot.WhiteV); }
                                else
                                {
                                    uv.Add((float)(itemSlot.U0 + it.Uv[2 * v] * (itemSlot.U1 - itemSlot.U0)));
                                    uv.Add((float)(itemSlot.V0 + it.Uv[2 * v + 1] * (itemSlot.V1 - itemSlot.V0)));
                                }
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
                        if (textured)
                        {
                            entry["texture"] = batch.Slot.File;
                            entry["alpha"] = !composite || list[0].CompositeAlpha;
                            if (batch.Slot.Atlas) entry["atlas"] = true;
                            if (batch.Slot.Dxt1) entry["opaque"] = true;
                        }
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
                ["imageQuality"] = small ? "small" : light ? "light" : "faithful",
                ["maxTextureSize"] = prof.MaxTextureSize, ["pages"] = pageFiles, ["triangles"] = totalTris,
                ["textures"] = texFileCount, ["atlases"] = atlasCount, ["textureMemoryMB"] = Math.Round(mb, 2), ["budgetFailures"] = failures,
            };
        }

        sealed class TexSource
        {
            public string Key, Name;
            public RgbaImage Image;
            public bool Composite, Partial;
        }

        /// <summary>
        /// PC: whether DXT1 (half the memory of BC7) is visually lossless for an opaque image: graphics with sharp
        /// multi-colour edges are not, smooth or two-colour content (starfields, gradients, text on plain colour) is.
        /// </summary>
        static bool Dxt1Invisible(Profile prof, RgbaImage img)
        {
            if (prof.Target == "quest" || ImageOps.HasAlpha(img)) return false;
            var (mean, p999) = ImageOps.Dxt1Error(img);
            return mean <= Dxt1MaxMean && p999 <= Dxt1MaxP999;
        }

        const double Dxt1MaxMean = 2.0;
        const int Dxt1MaxP999 = 24;

        sealed class TexSlot
        {
            public string File;
            public bool Atlas, Dxt1;             // Dxt1: opaque PNG that may use DXT1 on PC
            public int Width, Height;            // pixels of this image (inside the atlas when Atlas)
            public double U0 = 0, V0 = 0, U1 = 1, V1 = 1; // image UV (0..1) -> texture UV
            public double WhiteU, WhiteV;        // atlas only: UV of the white block (vector items)
        }

        sealed class Batch
        {
            public TexSlot Slot;   // null = vertex colour only
            public bool Composite; // standalone composite texture
            public List<DrawItem> Items;
        }

        const int AtlasPad = 8, AtlasWhite = 32, JpegQuality = 92, MaxBridgeVertices = 65536;

        // PC: opaque textures use DXT1 (0.5 B/px) in light mode; everything else BC7/DXT5 (1 B/px). Quest: ASTC 6x6.
        static double TexBytesPerPixel(Profile prof, bool light, bool opaque) => prof.Target == "quest" ? prof.BytesPerPixel : light && opaque ? 0.5 : prof.BytesPerPixel;

        enum TexClass { Jpeg, OpaquePng, AlphaPng }

        /// <summary>
        /// Writes the target's textures. Unity only block-compresses textures whose sides are powers of two (with
        /// mipmaps and no NPOT scaling), so:
        /// Faithful: one PNG per image/composite, scaled up to a power of two by the importer.
        /// Light: everything is packed into power-of-two atlases with a white block for vector items, one set per format:
        /// photographic opaque content as JPEG (4:4:4, q92; DXT1 on PC), other opaque content for which DXT1 is
        /// invisible as PNG (DXT1), the rest as PNG with alpha (BC7).
        /// </summary>
        static Dictionary<string, TexSlot> WriteTextures(CompileOptions o, Profile prof, bool light, List<TexSource> sources,
            out double texBytes, out int fileCount, out int atlasCount)
        {
            string t = prof.Target;
            var slots = new Dictionary<string, TexSlot>();
            double bytes = 0;
            int files = 0;
            void Emit(string rel, int w, int h, bool opaque, byte[] encoded)
            {
                File.WriteAllBytes(Path.Combine(o.Output, rel), encoded);
                bytes += (double)ImageOps.Pot(w) * ImageOps.Pot(h) * TexBytesPerPixel(prof, light, opaque) * prof.MipFactor;
                files++;
            }
            byte[] EncodePng(RgbaImage img, bool rgb) => rgb ? Png.Encode(ToRgb(img), img.Width, img.Height, 3) : Png.Encode(img.Pixels, img.Width, img.Height, 4);
            // JPEG only pays off (and only stays invisible) for photographic content: graphics compress well as PNG.
            TexClass Classify(RgbaImage img)
            {
                if (ImageOps.HasAlpha(img)) return TexClass.AlphaPng;
                var jpg = Jpeg.Encode(img.Pixels, img.Width, img.Height, JpegQuality);
                if (jpg.Length * 2 < EncodePng(img, true).Length) return TexClass.Jpeg;
                return Dxt1Invisible(prof, img) ? TexClass.OpaquePng : TexClass.AlphaPng;
            }
            // Identical pixels (the same picture embedded several times) share one texture.
            var byPixels = new Dictionary<string, TexSource>();
            var aliases = new List<(string key, TexSource of)>();
            var unique = new List<TexSource>();
            foreach (var s in sources)
            {
                var h = $"{s.Image.Width}x{s.Image.Height}:{s.Composite && !s.Partial}:{Sha(s.Image.Pixels)}";
                if (byPixels.TryGetValue(h, out var first)) { aliases.Add((s.Key, first)); continue; }
                byPixels[h] = s;
                unique.Add(s);
            }
            int atlases = 0;
            if (!light)
            {
                foreach (var s in unique)
                {
                    var rel = $"targets/{t}/{(s.Composite ? "composites" : "textures")}/{s.Name}.png";
                    Emit(rel, s.Image.Width, s.Image.Height, false, EncodePng(s.Image, s.Composite && !s.Partial));
                    slots[s.Key] = new TexSlot { File = rel, Width = s.Image.Width, Height = s.Image.Height };
                }
            }
            else
            {
                var groups = new Dictionary<TexClass, List<RgbaImage>>();
                var members = new Dictionary<TexClass, List<TexSource>>();
                foreach (var s in unique)
                {
                    var img = s.Image;
                    // Every image must fit an atlas with its padding, beside the white block.
                    int room = prof.MaxTextureSize - 2 * AtlasPad, beside = room - (AtlasWhite + AtlasPad + 7) / 8 * 8;
                    double k = Math.Min(1, Math.Min((double)room / img.Width, (double)room / img.Height));
                    if (img.Width * k > beside && img.Height * k > beside) k = Math.Min(k, (double)beside / Math.Min(img.Width, img.Height));
                    if (k < 1)
                    {
                        img = ImageOps.Multiple4(ImageOps.Resample(img, Math.Max(4, (int)(img.Width * k)), Math.Max(4, (int)(img.Height * k))));
                    }
                    var c = Classify(img);
                    if (!groups.ContainsKey(c)) { groups[c] = new List<RgbaImage>(); members[c] = new List<TexSource>(); }
                    groups[c].Add(img);
                    members[c].Add(s);
                }
                foreach (var g in groups)
                {
                    var list = g.Value;
                    var sizes = new List<(int w, int h)>();
                    foreach (var img in list) sizes.Add((img.Width, img.Height));
                    var dims = ImageOps.Pack(sizes, prof.MaxTextureSize, AtlasPad, AtlasWhite, out var placed);
                    for (int a = 0; a < dims.Count; a++)
                    {
                        var (aw, ah) = dims[a];
                        var atlasImg = new RgbaImage(aw, ah);
                        for (int y = 0; y < AtlasWhite; y++)
                            for (int x = 0; x < AtlasWhite; x++)
                            {
                                int i = (y * aw + x) * 4;
                                atlasImg.Pixels[i] = atlasImg.Pixels[i + 1] = atlasImg.Pixels[i + 2] = atlasImg.Pixels[i + 3] = 255;
                            }
                        var ids = new StringBuilder(g.Key.ToString());
                        for (int k = 0; k < list.Count; k++)
                        {
                            if (placed[k].Atlas != a) continue;
                            ImageOps.Blit(list[k], atlasImg, placed[k].X, placed[k].Y, AtlasPad);
                            ids.Append('|').Append(members[g.Key][k].Key);
                        }
                        bool jpeg = g.Key == TexClass.Jpeg, opaque = g.Key != TexClass.AlphaPng;
                        var rel = $"targets/{t}/textures/atlas-{Sha(Encoding.UTF8.GetBytes(ids.ToString())).Substring(0, 16)}.{(jpeg ? "jpg" : "png")}";
                        Emit(rel, aw, ah, opaque, jpeg ? Jpeg.Encode(atlasImg.Pixels, aw, ah, JpegQuality) : EncodePng(atlasImg, opaque));
                        atlases++;
                        for (int k = 0; k < list.Count; k++)
                        {
                            if (placed[k].Atlas != a) continue;
                            var p = placed[k];
                            int w = list[k].Width, h = list[k].Height;
                            // Image row 0 (top, image-space v = 1) sits at atlas row p.Y; Unity's v runs bottom-up.
                            slots[members[g.Key][k].Key] = new TexSlot
                            {
                                File = rel, Atlas = true, Dxt1 = g.Key == TexClass.OpaquePng, Width = w, Height = h,
                                U0 = (double)p.X / aw, U1 = (double)(p.X + w) / aw,
                                V0 = 1 - (double)(p.Y + h) / ah, V1 = 1 - (double)p.Y / ah,
                                WhiteU = AtlasWhite / 2.0 / aw, WhiteV = 1 - AtlasWhite / 2.0 / ah,
                            };
                        }
                    }
                }
            }
            foreach (var (key, of) in aliases) slots[key] = slots[of.Key];
            texBytes = bytes;
            fileCount = files;
            atlasCount = atlases;
            return slots;
        }

        /// <summary>
        /// <paramref name="after"/> with alpha 255 where it differs from <paramref name="before"/> (dilated by 1px) and 0
        /// elsewhere (colour kept, so filtering at the edges blends the right colour); null if nothing changed.
        /// </summary>
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
                    int i = k * 4;
                    o.Pixels[i] = after.Pixels[i]; o.Pixels[i + 1] = after.Pixels[i + 1]; o.Pixels[i + 2] = after.Pixels[i + 2];
                    if (!(rowDil[k] || (y > 0 && rowDil[k - w]) || (y + 1 < h && rowDil[k + w]))) continue;
                    any = true;
                    o.Pixels[i + 3] = 255;
                    if (x < x0) x0 = x;
                    if (y < y0) y0 = y;
                    if (x + 1 > x1) x1 = x + 1;
                    if (y + 1 > y1) y1 = y + 1;
                }
            return any ? o : null;
        }

        static double TransparentFraction(RgbaImage img)
        {
            long n = 0;
            for (int i = 3; i < img.Pixels.Length; i += 4) if (img.Pixels[i] != 255) n++;
            return (double)n / Math.Max(1, img.Width * img.Height);
        }

        static RgbaImage Opaque(RgbaImage img)
        {
            var o = new RgbaImage(img.Width, img.Height);
            Buffer.BlockCopy(img.Pixels, 0, o.Pixels, 0, img.Pixels.Length);
            for (int i = 3; i < o.Pixels.Length; i += 4) o.Pixels[i] = 255;
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
    }
}
