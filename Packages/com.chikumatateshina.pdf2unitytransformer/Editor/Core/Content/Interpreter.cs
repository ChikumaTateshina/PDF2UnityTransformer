using System;
using System.Collections.Generic;
using System.IO;
using PDF2Unity.Core.Fonts;
using PDF2Unity.Core.Graphics;
using PDF2Unity.Core.Pdf;

namespace PDF2Unity.Core.Content
{
    /// <summary>Shared per-document state of the frontend.</summary>
    public sealed class FrontendContext
    {
        public PdfFile File;
        public Diagnostics Diag;
        public Limits Limits;
        public Job Job;
        public ColorSpaceFactory ColorSpaces;
        public FontLoader Fonts;
        public OptionalContent OC;
        public readonly Dictionary<object, ShadingResource> Shadings = new Dictionary<object, ShadingResource>();
    }

    sealed class GState
    {
        public Matrix Ctm;
        public int Clip;
        public ColorSpace FillCs, StrokeCs;
        public double[] Fill, Stroke;
        public (bool tiling, ShadingResource sh, Matrix m)? FillPattern, StrokePattern;
        public double Lw = 1, Ml = 10, DashPhase;
        public int Cap, Join;
        public double[] Dash = new double[0];
        public double Ca = 1, CA = 1;
        public string Bm = "Normal";
        public bool SMask, Ais, OpFill, OpStroke;
        public PdfFont Font;
        public double Fs, Tc, Tw, Th = 1, Tl, Trise;
        public int Tr;

        public GState Copy() => (GState)MemberwiseClone();
    }

    /// <summary>
    /// Content stream interpreter (SPEC §3.1/3.2). Produces drawing operations in painting order.
    /// Every operation records the byte offset of the end of the top-level page instruction that
    /// contains it, so precomposition can render the page content exactly up to that point.
    /// </summary>
    public sealed class Interpreter
    {
        readonly FrontendContext ctx;
        readonly PageModel page;
        readonly int pno;
        TruncPoint currentPoint;
        int nOps;
        readonly Stack<object> formStack = new Stack<object>();
        readonly Stack<bool> t3Uncolored = new Stack<bool>();
        int t3Depth;
        readonly Rect pageBox;

        public Interpreter(FrontendContext c, PageModel p)
        {
            ctx = c;
            page = p;
            pno = p.Index + 1;
            pageBox = p.CropBox;
            t3Uncolored.Push(false);
            var r = p.CropBox;
            p.Clips.Add(new ClipNode { Parent = -1, Path = RectPath(r.X0, r.Y0, r.X1, r.Y1), Ctm = Matrix.Identity, Rule = "nonzero" });
        }

        public static List<PathCmd> RectPath(double x0, double y0, double x1, double y1) => new List<PathCmd>
        {
            PathCmd.Move(x0, y0), PathCmd.Line(x1, y0), PathCmd.Line(x1, y1), PathCmd.Line(x0, y1), PathCmd.Close(),
        };

        public void Run()
        {
            var gs = new GState
            {
                Ctm = Matrix.Identity, Clip = 0, FillCs = ctx.ColorSpaces.Gray, StrokeCs = ctx.ColorSpaces.Gray,
                Fill = new double[] { 0 }, Stroke = new double[] { 0 },
            };
            var resources = page.PageDict.GetDict("Resources") ?? (PdfFile.Inherited(page.PageDict, "Resources") as PdfDict) ?? new PdfDict();
            page.Content = ConcatContents(page.PageDict);
            List<Instruction> instrs;
            try { instrs = ContentParser.Parse(page.Content, ctx.Limits.MaxOpsPerPage); }
            catch (CompileException) { throw; }
            catch (Exception e)
            {
                ctx.Diag.Error("content.parse", $"content stream unreadable: {e.Message}", pno);
                return;
            }
            Exec(instrs, resources, gs, Matrix.Identity, $"page{pno}", 0, null, null, page.Content, null);
        }

        byte[] ConcatContents(PdfDict pageDict)
        {
            var c = pageDict.Get("Contents");
            var ms = new MemoryStream();
            void Add(PdfStream s)
            {
                try
                {
                    var d = s.Decode(ctx.Limits);
                    ms.Write(d, 0, d.Length);
                    ms.WriteByte(10);
                }
                catch (UnsupportedFilterException) { ctx.Diag.Warn("content.filter", "content stream with unsupported filter skipped", pno); }
            }
            if (c is PdfStream st) Add(st);
            else if (c is PdfArray a) for (int i = 0; i < a.Count; i++) if (a[i] is PdfStream s2) Add(s2);
            return ms.ToArray();
        }

        // ------------------------------------------------------------------
        DrawOp Emit(GState gs, DrawOp op)
        {
            op.Ctm = gs.Ctm;
            if (op.Kind != OpKind.Unsupported)
            {
                string code = null, detail = null;
                if (gs.Bm != "Normal" && gs.Bm != "Compatible") { code = "transparency.blend"; detail = $"blend mode {gs.Bm}"; }
                else if (gs.SMask) { code = "transparency.softmask"; detail = "soft mask in graphics state"; }
                else if (gs.Ais) { code = "transparency.ais"; detail = "alpha-is-shape"; }
                if (code != null)
                {
                    var bb = OpBBox(op);
                    op = new DrawOp { Kind = OpKind.Unsupported, Reason = code, Detail = detail, BBox = bb.IsEmpty ? pageBox : bb, Source = op.Source };
                }
                else if (gs.OpFill || gs.OpStroke)
                    ctx.Diag.Warn("color.overprint", "overprint is not simulated (screen rendering semantics)", pno);
            }
            op.Id = page.Ops.Count;
            op.Ctm = gs.Ctm;
            op.Clip = gs.Clip;
            op.Point = currentPoint;
            page.Ops.Add(op);
            if (op.Kind == OpKind.Unsupported) ctx.Diag.Warn(op.Reason, op.Detail, pno, "precomposite");
            return op;
        }

        void Unsupported(GState gs, string code, string detail, Rect bbox, string src) =>
            Emit(gs, new DrawOp { Kind = OpKind.Unsupported, Reason = code, Detail = detail, BBox = bbox.IsEmpty ? pageBox : bbox, Source = src });

        Rect OpBBox(DrawOp op)
        {
            switch (op.Kind)
            {
                case OpKind.Path:
                    {
                        var b = PathBBox(op.Path, op.Ctm);
                        if (op.Stroke != null) b = b.Grow(op.Style.Width * op.Ctm.MaxScale * Math.Max(1, Math.Min(op.Style.MiterLimit, 10)) / 2 + 1);
                        return b;
                    }
                case OpKind.Image: return op.Ctm.TransformRect(new Rect(0, 0, 1, 1));
                case OpKind.Shading: return op.Path != null ? PathBBox(op.Path, op.Ctm) : pageBox;
                case OpKind.Glyphs: return op.BBox;
            }
            return pageBox;
        }

        static Rect PathBBox(List<PathCmd> path, Matrix m)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            void P(double x, double y)
            {
                m.Apply(x, y, out var px, out var py);
                if (px < x0) x0 = px;
                if (py < y0) y0 = py;
                if (px > x1) x1 = px;
                if (py > y1) y1 = py;
            }
            foreach (var c in path)
            {
                if (c.Op == PathOp.Close) continue;
                if (c.Op == PathOp.Cubic) { P(c.X1, c.Y1); P(c.X2, c.Y2); }
                P(c.X3, c.Y3);
            }
            return x0 == double.MaxValue ? default : new Rect(x0, y0, x1, y1);
        }

        Paint MakePaint(GState gs, bool stroke)
        {
            var cs = stroke ? gs.StrokeCs : gs.FillCs;
            var comps = stroke ? gs.Stroke : gs.Fill;
            double alpha = stroke ? gs.CA : gs.Ca;
            if (cs is PatternSpace)
            {
                var pat = stroke ? gs.StrokePattern : gs.FillPattern;
                if (pat == null) throw new UnsupportedException("pattern.missing", "pattern colour without pattern");
                if (pat.Value.tiling) throw new UnsupportedException("pattern.tiling", "tiling pattern");
                return new Paint { Shading = pat.Value.sh, PatternMatrix = pat.Value.m, Alpha = alpha };
            }
            cs.ToRgb(comps, out var r, out var g, out var b);
            return new Paint { R = r, G = g, B = b, Alpha = alpha };
        }

        void SetPattern(GState gs, string name, PdfDict resources, Matrix baseMatrix, bool stroke)
        {
            var pat = resources?.GetDict("Pattern")?.GetDict(name);
            if (pat == null) throw new UnsupportedException("pattern.missing", $"pattern {name} not found");
            (bool, ShadingResource, Matrix) v;
            if (pat.GetInt("PatternType") == 2)
            {
                var sh = Shadings.Load(ctx, pat.Get("Shading"), resources);
                var pm = Matrix.FromArray(pat.GetArray("Matrix")?.ToDoubles());
                v = (false, sh, pm.Then(baseMatrix));
            }
            else v = (true, null, Matrix.Identity);
            if (stroke) gs.StrokePattern = v; else gs.FillPattern = v;
        }

        static double Num(object o, double def = 0) => PdfUtil.ToDouble(o, def);
        static double Arg(List<object> o, int i) => i < o.Count ? Num(o[i]) : 0;

        // ------------------------------------------------------------------
        /// <param name="parent">truncation point of the instruction that invoked this stream (null for the page)</param>
        /// <param name="fixedPoint">if set (Type 3 glyphs), ops inherit this point instead of per-instruction points</param>
        void Exec(List<Instruction> instrs, PdfDict resources, GState gs, Matrix baseMatrix, string src, int depth,
                  TruncPoint parent, PdfStream form, byte[] content, TruncPoint fixedPoint)
        {
            var stack = new Stack<GState>();
            var path = new List<PathCmd>();
            bool haveCur = false;
            double cx = 0, cy = 0, sx = 0, sy = 0;
            string pendingClip = null;
            var tm = Matrix.Identity;
            var tlm = Matrix.Identity;
            List<PathCmd> textClip = null;
            var ocStack = new Stack<bool>();
            int hidden = 0;
            for (int idx = 0; idx < instrs.Count; idx++)
            {
                var ins = instrs[idx];
                currentPoint = fixedPoint ?? new TruncPoint
                {
                    Parent = parent, Form = form, Content = content, Resources = resources, Start = ins.Start, End = ins.End,
                };
                if ((++nOps & 4095) == 0) ctx.Job?.Check();
                var o = ins.Operands;
                string where = $"{src}#{idx}";
                try
                {
                    switch (ins.Op)
                    {
                        // ---- graphics state ----
                        case "q": stack.Push(gs); gs = gs.Copy(); break;
                        case "Q": if (stack.Count > 0) gs = stack.Pop(); break;
                        case "cm": if (o.Count >= 6) gs.Ctm = new Matrix(Arg(o, 0), Arg(o, 1), Arg(o, 2), Arg(o, 3), Arg(o, 4), Arg(o, 5)).Then(gs.Ctm); break;
                        case "w": gs.Lw = Math.Abs(Arg(o, 0)); break;
                        case "J": gs.Cap = (int)Arg(o, 0); break;
                        case "j": gs.Join = (int)Arg(o, 0); break;
                        case "M": gs.Ml = Arg(o, 0); break;
                        case "d":
                            if (o.Count >= 2 && o[0] is PdfArray da)
                            {
                                var dv = da.ToDoubles();
                                for (int i = 0; i < dv.Length; i++) dv[i] = Math.Abs(dv[i]);
                                gs.Dash = dv;
                                gs.DashPhase = Num(o[1]);
                            }
                            break;
                        case "gs": if (o.Count > 0 && o[0] is PdfName gn) ExtGState(gs, resources, gn.Value); break;
                        case "ri": case "i": break;
                        // ---- colour ----
                        case "CS": case "cs":
                            {
                                if (t3Uncolored.Peek() || o.Count == 0) break;
                                var space = ctx.ColorSpaces.Resolve(o[0], resources);
                                if (ins.Op == "CS") { gs.StrokeCs = space; gs.Stroke = space.Initial(); gs.StrokePattern = null; }
                                else { gs.FillCs = space; gs.Fill = space.Initial(); gs.FillPattern = null; }
                            }
                            break;
                        case "SC": case "SCN": case "sc": case "scn":
                            {
                                if (t3Uncolored.Peek()) break;
                                bool stroke = ins.Op[0] == 'S';
                                var comps = new List<double>();
                                string pname = null;
                                foreach (var v in o) { if (v is PdfName pn) pname = pn.Value; else comps.Add(Num(v)); }
                                if (pname != null)
                                {
                                    try { SetPattern(gs, pname, resources, baseMatrix, stroke); }
                                    catch (UnsupportedException e)
                                    {
                                        ctx.Diag.Warn(e.Code, e.Message, pno);
                                        if (stroke) gs.StrokePattern = (true, null, Matrix.Identity);
                                        else gs.FillPattern = (true, null, Matrix.Identity);
                                    }
                                }
                                if (comps.Count > 0) { if (stroke) gs.Stroke = comps.ToArray(); else gs.Fill = comps.ToArray(); }
                            }
                            break;
                        case "G": case "g": case "RG": case "rg": case "K": case "k":
                            {
                                if (t3Uncolored.Peek()) break;
                                ColorSpace space = ins.Op == "G" || ins.Op == "g" ? ctx.ColorSpaces.Gray
                                    : ins.Op == "RG" || ins.Op == "rg" ? (ColorSpace)ctx.ColorSpaces.Rgb : ctx.ColorSpaces.Cmyk;
                                var comps = new double[o.Count];
                                for (int i = 0; i < o.Count; i++) comps[i] = Num(o[i]);
                                if (char.IsUpper(ins.Op[0])) { gs.StrokeCs = space; gs.Stroke = comps; gs.StrokePattern = null; }
                                else { gs.FillCs = space; gs.Fill = comps; gs.FillPattern = null; }
                            }
                            break;
                        // ---- path construction ----
                        case "m":
                            cx = sx = Arg(o, 0); cy = sy = Arg(o, 1);
                            path.Add(PathCmd.Move(cx, cy));
                            haveCur = true;
                            break;
                        case "l":
                            {
                                double x = Arg(o, 0), y = Arg(o, 1);
                                if (!haveCur) { path.Add(PathCmd.Move(x, y)); sx = x; sy = y; haveCur = true; }
                                else if (path.Count > 0 && path[path.Count - 1].Op == PathOp.Close) path.Add(PathCmd.Move(sx, sy));
                                path.Add(PathCmd.Line(x, y));
                                cx = x; cy = y;
                            }
                            break;
                        case "c": case "v": case "y":
                            {
                                if (!haveCur) break;
                                if (path.Count > 0 && path[path.Count - 1].Op == PathOp.Close) path.Add(PathCmd.Move(sx, sy));
                                PathCmd seg;
                                if (ins.Op == "c") seg = PathCmd.Cubic(Arg(o, 0), Arg(o, 1), Arg(o, 2), Arg(o, 3), Arg(o, 4), Arg(o, 5));
                                else if (ins.Op == "v") seg = PathCmd.Cubic(cx, cy, Arg(o, 0), Arg(o, 1), Arg(o, 2), Arg(o, 3));
                                else seg = PathCmd.Cubic(Arg(o, 0), Arg(o, 1), Arg(o, 2), Arg(o, 3), Arg(o, 2), Arg(o, 3));
                                path.Add(seg);
                                cx = seg.X3; cy = seg.Y3;
                            }
                            break;
                        case "h":
                            if (haveCur && path.Count > 0 && path[path.Count - 1].Op != PathOp.Close) { path.Add(PathCmd.Close()); cx = sx; cy = sy; }
                            break;
                        case "re":
                            {
                                double x = Arg(o, 0), y = Arg(o, 1), w = Arg(o, 2), h = Arg(o, 3);
                                path.AddRange(RectPath(x, y, x + w, y + h));
                                cx = sx = x; cy = sy = y;
                                haveCur = true;
                            }
                            break;
                        // ---- painting ----
                        case "S": case "s": case "f": case "F": case "f*": case "B": case "B*": case "b": case "b*": case "n":
                            {
                                var op = ins.Op;
                                if ((op == "s" || op == "b" || op == "b*") && path.Count > 0 && path[path.Count - 1].Op != PathOp.Close) path.Add(PathCmd.Close());
                                if (path.Count > 0 && hidden == 0 && op != "n")
                                {
                                    bool doFill = op == "f" || op == "F" || op == "f*" || op == "B" || op == "B*" || op == "b" || op == "b*";
                                    bool doStroke = op == "S" || op == "s" || op == "B" || op == "B*" || op == "b" || op == "b*";
                                    string rule = op.EndsWith("*") ? "evenodd" : "nonzero";
                                    PaintPath(gs, path, doFill ? rule : null, doStroke, where);
                                }
                                if (pendingClip != null) gs.Clip = AddClip(gs, path.Count > 0 ? path : RectPath(0, 0, 0, 0), pendingClip, gs.Ctm);
                                pendingClip = null;
                                path = new List<PathCmd>();
                                haveCur = false;
                            }
                            break;
                        case "W": pendingClip = "nonzero"; break;
                        case "W*": pendingClip = "evenodd"; break;
                        // ---- XObjects / images / shading ----
                        case "Do": if (hidden == 0 && o.Count > 0 && o[0] is PdfName xn) DoXObject(gs, resources, xn.Value, where, depth); break;
                        case "BI": if (hidden == 0) InlineImage(gs, resources, ins, where); break;
                        case "sh": if (hidden == 0 && o.Count > 0 && o[0] is PdfName sn) Sh(gs, resources, sn.Value, where); break;
                        // ---- text ----
                        case "BT": tm = tlm = Matrix.Identity; textClip = null; break;
                        case "ET":
                            if (textClip != null)
                            {
                                gs.Clip = AddClip(gs, textClip.Count > 0 ? textClip : RectPath(0, 0, 0, 0), "nonzero", Matrix.Identity);
                                textClip = null;
                            }
                            break;
                        case "Tf":
                            {
                                var fdict = resources?.GetDict("Font")?.GetDict((o.Count > 0 ? o[0] as PdfName : null)?.Value ?? "");
                                if (fdict == null) throw new UnsupportedException("font.missing", "font resource not found");
                                gs.Font = ctx.Fonts.Load(fdict);
                                gs.Fs = Arg(o, 1);
                            }
                            break;
                        case "Tc": gs.Tc = Arg(o, 0); break;
                        case "Tw": gs.Tw = Arg(o, 0); break;
                        case "Tz": gs.Th = Arg(o, 0) / 100; break;
                        case "TL": gs.Tl = Arg(o, 0); break;
                        case "Tr": gs.Tr = (int)Arg(o, 0); break;
                        case "Ts": gs.Trise = Arg(o, 0); break;
                        case "Td": case "TD":
                            if (ins.Op == "TD") gs.Tl = -Arg(o, 1);
                            tlm = Matrix.Translate(Arg(o, 0), Arg(o, 1)).Then(tlm);
                            tm = tlm;
                            break;
                        case "Tm": tlm = tm = new Matrix(Arg(o, 0), Arg(o, 1), Arg(o, 2), Arg(o, 3), Arg(o, 4), Arg(o, 5)); break;
                        case "T*": tlm = Matrix.Translate(0, -gs.Tl).Then(tlm); tm = tlm; break;
                        case "Tj": case "'": case "\"": case "TJ":
                            {
                                if (ins.Op == "'" || ins.Op == "\"")
                                {
                                    if (ins.Op == "\"") { gs.Tw = Arg(o, 0); gs.Tc = Arg(o, 1); }
                                    tlm = Matrix.Translate(0, -gs.Tl).Then(tlm);
                                    tm = tlm;
                                }
                                if (gs.Font == null) throw new UnsupportedException("font.missing", "text shown without a font");
                                var items = new List<object>();
                                if (ins.Op == "TJ" && o.Count > 0 && o[0] is PdfArray ta) for (int i = 0; i < ta.Count; i++) items.Add(ta[i]);
                                else if (o.Count > 0) items.Add(o[o.Count - 1]);
                                var parts = ShowText(gs, ref tm, items, resources, where, depth, hidden);
                                if (parts != null) { textClip = textClip ?? new List<PathCmd>(); textClip.AddRange(parts); }
                            }
                            break;
                        // ---- marked / optional content ----
                        case "BMC": case "BDC":
                            {
                                bool vis = true;
                                if (ins.Op == "BDC" && o.Count >= 2 && o[0] is PdfName tag && tag.Value == "OC")
                                {
                                    var prop = o[1] as PdfDict;
                                    if (o[1] is PdfName pn) prop = resources?.GetDict("Properties")?.GetDict(pn.Value);
                                    if (prop != null) vis = ctx.OC.Visible(prop);
                                }
                                ocStack.Push(!vis);
                                if (!vis) hidden++;
                            }
                            break;
                        case "EMC": if (ocStack.Count > 0 && ocStack.Pop()) hidden--; break;
                        case "d1": if (t3Uncolored.Count > 0) { t3Uncolored.Pop(); t3Uncolored.Push(true); } break;
                        case "d0": case "MP": case "DP": case "BX": case "EX": case "ID": case "EI": break;
                        default:
                            ctx.Diag.Info("content.unknown-op", $"unknown operator {ins.Op} ignored", pno);
                            break;
                    }
                }
                catch (UnsupportedException e)
                {
                    Unsupported(gs, e.Code, e.Message, default, where);
                }
                catch (CompileException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    ctx.Diag.Warn("content.operator", $"operator {ins.Op} failed: {e.GetType().Name}: {e.Message}", pno);
                }
            }
            if (textClip != null) gs.Clip = AddClip(gs, textClip, "nonzero", Matrix.Identity);
        }

        // ------------------------------------------------------------------
        void ExtGState(GState gs, PdfDict resources, string name)
        {
            var d = resources?.GetDict("ExtGState")?.GetDict(name);
            if (d == null) throw new UnsupportedException("gs.missing", $"ExtGState {name} not found");
            foreach (var kv in d.Items)
            {
                var v = d.Get(kv.Key);
                switch (kv.Key)
                {
                    case "LW": gs.Lw = Math.Abs(Num(v, 1)); break;
                    case "LC": gs.Cap = (int)Num(v); break;
                    case "LJ": gs.Join = (int)Num(v); break;
                    case "ML": gs.Ml = Num(v, 10); break;
                    case "D":
                        if (v is PdfArray da && da.Count >= 2 && da[0] is PdfArray dd)
                        {
                            var dv = dd.ToDoubles();
                            for (int i = 0; i < dv.Length; i++) dv[i] = Math.Abs(dv[i]);
                            gs.Dash = dv;
                            gs.DashPhase = da.Num(1);
                        }
                        break;
                    case "CA": gs.CA = Math.Max(0, Math.Min(1, Num(v, 1))); break;
                    case "ca": gs.Ca = Math.Max(0, Math.Min(1, Num(v, 1))); break;
                    case "BM":
                        if (v is PdfArray ba) v = ba.Count > 0 ? ba[0] : new PdfName("Normal");
                        gs.Bm = (v as PdfName)?.Value ?? "Normal";
                        break;
                    case "SMask": gs.SMask = !(v is PdfName sn && sn.Value == "None"); break;
                    case "AIS": gs.Ais = v is bool ab && ab; break;
                    case "OP": gs.OpStroke = v is bool o1 && o1; if (!d.Has("op")) gs.OpFill = gs.OpStroke; break;
                    case "op": gs.OpFill = v is bool o2 && o2; break;
                    case "Font":
                        if (v is PdfArray fa && fa.Count >= 2 && fa.Dict(0) != null) { gs.Font = ctx.Fonts.Load(fa.Dict(0)); gs.Fs = fa.Num(1); }
                        break;
                }
            }
        }

        int AddClip(GState gs, List<PathCmd> path, string rule, Matrix ctm)
        {
            page.Clips.Add(new ClipNode { Parent = gs.Clip, Path = new List<PathCmd>(path), Ctm = ctm, Rule = rule });
            return page.Clips.Count - 1;
        }

        void PaintPath(GState gs, List<PathCmd> path, string fillRule, bool doStroke, string where)
        {
            Paint fill = null, stroke = null;
            var style = new StrokeStyle { Width = gs.Lw, Cap = gs.Cap, Join = gs.Join, MiterLimit = gs.Ml, Dash = gs.Dash, DashPhase = gs.DashPhase };
            try
            {
                if (fillRule != null) fill = MakePaint(gs, false);
                if (doStroke) stroke = MakePaint(gs, true);
            }
            catch (UnsupportedException e)
            {
                var b = PathBBox(path, gs.Ctm);
                if (doStroke) b = b.Grow(gs.Lw * gs.Ctm.MaxScale * 5 + 1);
                Unsupported(gs, e.Code, e.Message, b, where);
                return;
            }
            if (fill != null && fill.IsShading)
            {
                Emit(gs, new DrawOp { Kind = OpKind.Shading, Shading = fill.Shading, ShadingMatrix = fill.PatternMatrix, Path = new List<PathCmd>(path), FillRule = fillRule, Alpha = fill.Alpha, Source = where });
                fill = null;
                fillRule = null;
                if (stroke == null) return;
            }
            if (stroke != null && stroke.IsShading)
            {
                Unsupported(gs, "pattern.shading-stroke", "stroke with shading pattern", PathBBox(path, gs.Ctm).Grow(gs.Lw * gs.Ctm.MaxScale * 5 + 1), where);
                stroke = null;
                if (fill == null) return;
            }
            Emit(gs, new DrawOp { Kind = OpKind.Path, Path = new List<PathCmd>(path), FillRule = fillRule, Fill = fill, Stroke = stroke, Style = style, Source = where });
        }

        void Sh(GState gs, PdfDict resources, string name, string where)
        {
            var obj = resources?.GetDict("Shading")?.Get(name);
            if (obj == null) throw new UnsupportedException("shading.missing", $"shading {name} not found");
            var d = obj as PdfDict ?? (obj as PdfStream)?.Dict;
            var bbox = pageBox;
            var b = d?.GetArray("BBox")?.ToDoubles();
            if (b != null && b.Length == 4) bbox = gs.Ctm.TransformRect(new Rect(b[0], b[1], b[2], b[3]));
            ShadingResource sh;
            try { sh = Shadings.Load(ctx, obj, resources); }
            catch (UnsupportedException e) { Unsupported(gs, e.Code, e.Message, bbox, where); return; }
            Emit(gs, new DrawOp { Kind = OpKind.Shading, Shading = sh, ShadingMatrix = gs.Ctm, Alpha = gs.Ca, Source = where });
        }

        void DoXObject(GState gs, PdfDict resources, string name, string where, int depth)
        {
            var x = resources?.GetDict("XObject")?.GetStream(name);
            if (x == null) throw new UnsupportedException("xobject.missing", $"XObject {name} not found");
            var oc = x.Dict.GetDict("OC");
            if (oc != null && !ctx.OC.Visible(oc)) return;
            var st = x.Dict.GetName("Subtype", "");
            if (st == "Image") Image(gs, x, resources, where);
            else if (st == "Form") Form(gs, x, resources, where, depth, name);
            else if (st == "PS") ctx.Diag.Info("xobject.ps", "PostScript XObject ignored", pno);
            else throw new UnsupportedException("xobject.type", $"XObject subtype {st}");
        }

        void Image(GState gs, PdfStream x, PdfDict resources, string where)
        {
            var d = x.Dict;
            var img = new ImageRef
            {
                XObject = x.Ref,
                Width = d.GetInt("Width"),
                Height = d.GetInt("Height"),
                Stencil = d.GetBool("ImageMask"),
                Interpolate = d.GetBool("Interpolate"),
                Resources = resources,
            };
            if (x.Ref == null)
            {
                Unsupported(gs, "image.direct", "direct image stream", gs.Ctm.TransformRect(new Rect(0, 0, 1, 1)), where);
                return;
            }
            AddImage(gs, img, x.Ref.ToString(), where);
        }

        void InlineImage(GState gs, PdfDict resources, Instruction ins, string where)
        {
            var d = ins.InlineDict;
            var img = new ImageRef
            {
                Width = d.GetInt("Width"),
                Height = d.GetInt("Height"),
                Stencil = d.GetBool("ImageMask"),
                Interpolate = d.GetBool("Interpolate"),
                Resources = resources,
                InlineImage = RebuildInline(d, ins.InlineData),
            };
            AddImage(gs, img, "inline:" + Hash(img.InlineImage), where);
        }

        static byte[] RebuildInline(PdfDict d, byte[] data)
        {
            var w = new PdfWriter();
            w.Raw("BI ");
            foreach (var kv in d.Items)
            {
                w.Raw("/" + kv.Key + " ");
                w.Value(kv.Value);
                w.Raw(" ");
            }
            w.Raw("ID ");
            w.Raw(data);
            w.Raw("\nEI");
            return w.ToArray();
        }

        static string Hash(byte[] b)
        {
            using (var s = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(s.ComputeHash(b)).Replace("-", "").Substring(0, 24);
        }

        void AddImage(GState gs, ImageRef img, string key, string where)
        {
            var bbox = gs.Ctm.TransformRect(new Rect(0, 0, 1, 1));
            if (img.Width <= 0 || img.Height <= 0) return;
            if ((long)img.Width * img.Height > ctx.Limits.MaxImagePixels)
            {
                Unsupported(gs, "image.too-large", $"image {img.Width}x{img.Height} exceeds pixel limit", bbox, where);
                return;
            }
            if (img.Stencil)
            {
                Paint fill;
                try { fill = MakePaint(gs, false); }
                catch (UnsupportedException e) { Unsupported(gs, e.Code, e.Message, bbox, where); return; }
                if (fill.IsShading) { Unsupported(gs, "image.stencil-pattern", "stencil mask painted with a pattern", bbox, where); return; }
                img.R = fill.R; img.G = fill.G; img.B = fill.B;
                key += $"|{Math.Round(fill.R, 3)},{Math.Round(fill.G, 3)},{Math.Round(fill.B, 3)}";
            }
            img.Key = key;
            Emit(gs, new DrawOp { Kind = OpKind.Image, Image = img, Alpha = gs.Ca, Source = where });
        }

        void Form(GState gs, PdfStream x, PdfDict resources, string where, int depth, string doName)
        {
            var d = x.Dict;
            var bb = d.GetArray("BBox")?.ToDoubles();
            var matrix = Matrix.FromArray(d.GetArray("Matrix")?.ToDoubles());
            var ctm = matrix.Then(gs.Ctm);
            var pbox = bb != null && bb.Length == 4 ? ctm.TransformRect(new Rect(bb[0], bb[1], bb[2], bb[3])) : pageBox;
            object key = x.Key;
            if (depth + 1 > ctx.Limits.MaxFormDepth || formStack.Contains(key))
            {
                Unsupported(gs, "form.recursion", "recursive or too deeply nested form XObject", pbox, where);
                return;
            }
            var group = d.GetDict("Group");
            bool tgroup = group != null && group.GetName("S") == "Transparency";
            var child = gs.Copy();
            child.Ctm = ctm;
            if (tgroup)
            {
                bool knockout = group.GetBool("K");
                bool needs = gs.Ca < 1 || (gs.Bm != "Normal" && gs.Bm != "Compatible") || gs.SMask || knockout;
                if (needs)
                {
                    string reason = knockout ? "knockout group" : gs.Ca < 1 ? "group opacity" : "group blend/soft mask";
                    Unsupported(gs, "transparency.group", $"transparency group ({reason})", pbox, where);
                    return;
                }
                child.Ca = child.CA = 1;
            }
            if (bb != null && bb.Length == 4) child.Clip = AddClip(child, RectPath(bb[0], bb[1], bb[2], bb[3]), "nonzero", ctm);
            var res = d.GetDict("Resources") ?? resources;
            List<Instruction> instrs;
            byte[] data;
            try
            {
                data = x.Decode(ctx.Limits);
                instrs = ContentParser.Parse(data, ctx.Limits.MaxOpsPerPage);
            }
            catch (CompileException) { throw; }
            catch (Exception e)
            {
                Unsupported(gs, "content.parse", $"form content unreadable: {e.Message}", pbox, where);
                return;
            }
            var saved = currentPoint;
            var parentPoint = saved?.WithDo(doName);
            // Inside a Type 3 glyph the whole glyph run is atomic: keep the fixed point.
            bool atomic = t3Depth > 0;
            formStack.Push(key);
            try { Exec(instrs, res, child, ctm, where + ":" + x.Ref, depth + 1, parentPoint, x, data, atomic ? saved : null); }
            finally
            {
                formStack.Pop();
                currentPoint = saved;
            }
        }

        // ------------------------------------------------------------------
        List<PathCmd> ShowText(GState gs, ref Matrix tm, List<object> items, PdfDict resources, string where, int depth, int hidden)
        {
            var font = gs.Font;
            double fs = gs.Fs, th = gs.Th, tc = gs.Tc, tw = gs.Tw;
            int mode = gs.Tr;
            bool visible = mode != 3 && mode != 7 && hidden == 0;
            bool clipMode = mode >= 4;
            var glyphs = new List<Glyph>();
            Rect runBox = default;
            List<PathCmd> clipParts = clipMode ? new List<PathCmd>() : null;
            var sizeM = new Matrix(fs * th, 0, 0, fs, 0, gs.Trise);
            foreach (var item in items)
            {
                if (!(item is PdfString str))
                {
                    double adj = PdfUtil.ToDouble(item);
                    tm = font.Vertical ? Matrix.Translate(0, -adj / 1000 * fs).Then(tm) : Matrix.Translate(-adj / 1000 * fs * th, 0).Then(tm);
                    continue;
                }
                foreach (var g in font.Decode(str.Bytes))
                {
                    var trm = sizeM.Then(tm);
                    Matrix gm = font.Vertical ? Matrix.Translate(-g.Vx, -g.Vy).Then(trm)
                        : g.XScale != 1 ? new Matrix(g.XScale, 0, 0, 1, 0, 0).Then(trm) : trm;
                    if (font.IsType3)
                    {
                        if (visible && g.T3Name != null) Type3Glyph(gs, font, g.T3Name, trm, resources, where, depth);
                        if (clipMode) ctx.Diag.Warn("text.type3-clip", "Type 3 text used as clip is ignored", pno);
                    }
                    else
                    {
                        runBox = runBox.Union(gm.Then(gs.Ctm).TransformRect(font.BBox));
                        if (g.Key != null)
                        {
                            glyphs.Add(new Glyph { Key = g.Key, M = gm });
                            if (clipMode && font.Unsupported == null)
                            {
                                var outline = font.Outline(g.Key);
                                if (outline != null)
                                {
                                    var full = gm.Then(gs.Ctm);
                                    foreach (var c in outline) clipParts.Add(c.Transform(full));
                                }
                            }
                        }
                    }
                    if (font.Vertical) tm = Matrix.Translate(0, g.W1 * fs + tc + (g.IsSpace ? tw : 0)).Then(tm);
                    else tm = Matrix.Translate((g.W0 * fs + tc + (g.IsSpace ? tw : 0)) * th, 0).Then(tm);
                }
            }
            if (font.IsType3 || !visible || (glyphs.Count == 0 && runBox.IsEmpty)) return clipParts;
            if (font.Unsupported != null)
            {
                Unsupported(gs, font.Unsupported.Value.code, $"text in font {font.BaseName}: {font.Unsupported.Value.message}", runBox.Grow(1), where);
                return clipParts;
            }
            foreach (var g in glyphs) font.Outline(g.Key);
            Paint fill = null, stroke = null;
            try
            {
                if (mode == 0 || mode == 2 || mode == 4 || mode == 6) fill = MakePaint(gs, false);
                if (mode == 1 || mode == 2 || mode == 5 || mode == 6) stroke = MakePaint(gs, true);
            }
            catch (UnsupportedException e)
            {
                Unsupported(gs, e.Code, e.Message, runBox.Grow(1), where);
                return clipParts;
            }
            if ((fill != null && fill.IsShading) || (stroke != null && stroke.IsShading))
            {
                Unsupported(gs, "text.pattern", "text painted with a pattern", runBox.Grow(1), where);
                return clipParts;
            }
            var style = new StrokeStyle { Width = gs.Lw, Cap = gs.Cap, Join = gs.Join, MiterLimit = gs.Ml, Dash = gs.Dash, DashPhase = gs.DashPhase };
            Emit(gs, new DrawOp { Kind = OpKind.Glyphs, Font = font, Glyphs = glyphs, TextMode = mode, Fill = fill, Stroke = stroke, Style = style, BBox = runBox, Source = where });
            return clipParts;
        }

        void Type3Glyph(GState gs, PdfFont font, string name, Matrix trm, PdfDict resources, string where, int depth)
        {
            var proc = font.CharProcs?.GetStream(name);
            if (proc == null) return;
            if (t3Depth > 4) throw new UnsupportedException("font.type3-recursion", "Type 3 glyph recursion");
            var child = gs.Copy();
            child.Ctm = font.FontMatrix.Then(trm).Then(gs.Ctm);
            var res = font.T3Resources ?? resources;
            var instrs = ContentParser.Parse(proc.Decode(ctx.Limits), ctx.Limits.MaxOpsPerPage);
            var saved = currentPoint;
            t3Uncolored.Push(false);
            t3Depth++;
            try { Exec(instrs, res, child, child.Ctm, where + ":t3/" + name, depth + 1, null, null, null, saved); }
            finally
            {
                t3Uncolored.Pop();
                t3Depth--;
                currentPoint = saved;
            }
        }
    }
}
