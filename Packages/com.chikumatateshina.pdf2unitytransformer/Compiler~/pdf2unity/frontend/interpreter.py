"""Content stream interpreter -> ordered IR operations (SPEC §3.1 steps 4-7, §3.2).

The interpreter keeps the full graphics state stack (`q`/`Q`), the CTM, the
clip chain and the text state. Every painting operator becomes exactly one
IR operation in PDF painting order. Constructs that cannot be drawn natively
become `unsupported` operations carrying the affected page-space bbox, so the
optimizer can precomposite precisely that area (FR-09).
"""

from __future__ import annotations

import pikepdf

from ..diagnostics import CompileError, Unsupported
from ..ir.model import (ClipIR, Glyph, GlyphRunIR, GroupIR, ImageIR, Operation, Paint, PathIR, ShadingIR,
                        StrokeStyle, UnsupportedIR)
from ..matrix import IDENTITY, mul, transform_bbox, translate
from .colorspace import PatternSpace
from .pdfutil import num, num_list, objid, objkey, pdf_string_bytes
from .shading import is_shading_pattern

NORMAL_BLEND = ("/Normal", "/Compatible")


class GState:
    __slots__ = ("ctm", "clip", "fill_cs", "fill", "fill_pattern", "stroke_cs", "stroke", "stroke_pattern",
                 "lw", "cap", "join", "ml", "dash", "dash_phase", "ca", "CA", "bm", "smask", "ais",
                 "font", "fs", "tc", "tw", "th", "tl", "tr", "trise", "op_fill", "op_stroke")

    def copy(self):
        g = GState.__new__(GState)
        for s in GState.__slots__:
            setattr(g, s, getattr(self, s))
        return g


def _rect_path(x0, y0, x1, y1):
    return [("M", x0, y0), ("L", x1, y0), ("L", x1, y1), ("L", x0, y1), ("Z",)]


def _path_bbox(cmds, m):
    xs, ys = [], []
    for c in cmds:
        if c[0] == "Z":
            continue
        pts = c[1:]
        for i in range(0, len(pts), 2):
            x, y = pts[i], pts[i + 1]
            xs.append(m[0] * x + m[2] * y + m[4])
            ys.append(m[1] * x + m[3] * y + m[5])
    if not xs:
        return None
    return (min(xs), min(ys), max(xs), max(ys))


def _union(a, b):
    if a is None:
        return b
    if b is None:
        return a
    return (min(a[0], b[0]), min(a[1], b[1]), max(a[2], b[2]), max(a[3], b[3]))


def _grow(b, d):
    return None if b is None else (b[0] - d, b[1] - d, b[2] + d, b[3] + d)


class PageInterpreter:
    def __init__(self, ctx, page_ir, page_obj):
        self.ctx = ctx
        self.page = page_ir
        self.page_obj = page_obj
        self.pno = page_ir.index + 1
        self.seq = 0
        self.n_ops = 0
        self.n_glyphs = 0
        self.form_stack = []
        self.group_stack = [None]
        self.t3_uncolored = [False]
        self.t3_depth = 0
        x0, y0, x1, y1 = page_ir.crop_box
        page_clip = ClipIR(id=0, parent=None, segments=_rect_path(x0, y0, x1, y1), ctm=IDENTITY,
                           fill_rule="nonzero")
        page_ir.clips.append(page_clip)
        self.page_bbox = page_ir.crop_box

    # ------------------------------------------------------------------
    def run(self):
        gs = GState()
        gs.ctm = IDENTITY
        gs.clip = 0
        cs = self.ctx.colorspaces
        gs.fill_cs = cs.gray
        gs.stroke_cs = cs.gray
        gs.fill = [0.0]
        gs.stroke = [0.0]
        gs.fill_pattern = None
        gs.stroke_pattern = None
        gs.lw, gs.cap, gs.join, gs.ml, gs.dash, gs.dash_phase = 1.0, 0, 0, 10.0, [], 0.0
        gs.ca = gs.CA = 1.0
        gs.bm = "/Normal"
        gs.smask = False
        gs.ais = False
        gs.font, gs.fs, gs.tc, gs.tw, gs.th, gs.tl, gs.tr, gs.trise = None, 0.0, 0.0, 0.0, 1.0, 0.0, 0, 0.0
        gs.op_fill = gs.op_stroke = False
        resources = self.page_obj.get("/Resources")
        if resources is None:
            resources = pikepdf.Dictionary()
        contents = self.page_obj.get("/Contents")
        if contents is None:
            return
        try:
            instrs = pikepdf.parse_content_stream(pikepdf.Page(self.page_obj))
        except pikepdf.PdfError as e:
            self.ctx.diag.error("content.parse", f"content stream unreadable: {e}", page=self.pno)
            self._unsupported(gs, "content.parse", str(e), self.page_bbox, "page")
            return
        self._exec(instrs, resources, gs, IDENTITY, f"page{self.pno}")

    # ------------------------------------------------------------------
    def _emit(self, gs, kind, payload, source):
        if kind != "unsupported":
            reason = None
            if gs.bm not in NORMAL_BLEND:
                reason = ("transparency.blend", f"blend mode {gs.bm[1:]}")
            elif gs.smask:
                reason = ("transparency.softmask", "soft mask in graphics state")
            elif gs.ais:
                reason = ("transparency.ais", "alpha-is-shape")
            if reason is not None:
                bbox = self._payload_bbox(gs, kind, payload)
                payload = UnsupportedIR(reason=reason[0], detail=reason[1], bbox=bbox or self.page_bbox)
                kind = "unsupported"
        if (gs.op_fill or gs.op_stroke) and kind != "unsupported":
            self.ctx.diag.warn("color.overprint", "overprint is not simulated (screen rendering semantics)",
                               page=self.pno)
        op = Operation(id=len(self.page.operations), sequence=self.seq, kind=kind, source=source, ctm=gs.ctm,
                       clip_ref=gs.clip, group_ref=self.group_stack[-1], payload=payload,
                       blend_mode=gs.bm[1:], overprint=bool(gs.op_fill or gs.op_stroke))
        self.seq += 1
        self.page.operations.append(op)
        if kind == "unsupported":
            self.ctx.diag.warn(payload.reason, payload.detail, page=self.pno, source=source,
                               fallback="precomposite" if self.ctx.options.mode != "strict" else None)
        return op

    def _unsupported(self, gs, code, detail, bbox, source):
        payload = UnsupportedIR(reason=code, detail=detail, bbox=bbox if bbox is not None else self.page_bbox)
        return self._emit(gs, "unsupported", payload, source)

    def _payload_bbox(self, gs, kind, payload):
        if kind == "path":
            b = _path_bbox(payload.segments, gs.ctm)
            if payload.stroke is not None and b is not None:
                from ..matrix import max_scale
                b = _grow(b, gs.lw * max_scale(gs.ctm) * max(1.0, min(gs.ml, 10.0)) / 2 + 1)
            return b
        if kind == "image":
            return transform_bbox(gs.ctm, (0, 0, 1, 1))
        if kind == "shading":
            if payload.segments:
                return _path_bbox(payload.segments, gs.ctm)
            return self.page_bbox
        if kind == "glyph_run":
            return getattr(payload, "_bbox", None)
        return None

    # ------------------------------------------------------------------
    def _style(self, gs):
        return StrokeStyle(width=gs.lw, cap=gs.cap, join=gs.join, miter_limit=gs.ml, dash=list(gs.dash),
                           dash_phase=gs.dash_phase)

    def _paint(self, gs, stroke: bool):
        cs = gs.stroke_cs if stroke else gs.fill_cs
        comps = gs.stroke if stroke else gs.fill
        alpha = gs.CA if stroke else gs.ca
        if isinstance(cs, PatternSpace):
            pat = gs.stroke_pattern if stroke else gs.fill_pattern
            if pat is None:
                raise Unsupported("pattern.missing", "pattern colour without pattern")
            kind, data = pat
            if kind == "tiling":
                raise Unsupported("pattern.tiling", "tiling pattern")
            shading_ref, pmatrix = data
            return Paint(kind="shading", alpha=alpha, colorspace="Pattern", shading_ref=shading_ref,
                         pattern_matrix=pmatrix)
        rgb = cs.to_rgb(comps)
        return Paint(kind="color", rgb=tuple(rgb), alpha=alpha, colorspace=cs.describe(),
                     components=[round(float(c), 6) for c in comps])

    def _set_pattern(self, gs, name, resources, base_matrix, stroke):
        pats = resources.get("/Pattern")
        if pats is None or name not in pats:
            raise Unsupported("pattern.missing", f"pattern {name} not found")
        pat = pats[name]
        if is_shading_pattern(pat):
            if "/ExtGState" in pat:
                self.ctx.diag.warn("pattern.extgstate", "ExtGState inside shading pattern ignored", page=self.pno)
            sh = self.ctx.shadings.load(pat["/Shading"], resources)
            pm = tuple(num_list(pat.get("/Matrix", [1, 0, 0, 1, 0, 0])))
            value = ("shading", (sh.ref, mul(pm, base_matrix)))
        else:
            value = ("tiling", None)
        if stroke:
            gs.stroke_pattern = value
        else:
            gs.fill_pattern = value

    # ------------------------------------------------------------------
    def _exec(self, instrs, resources, gs, base_matrix, src, depth=0):
        ctx = self.ctx
        stack = []
        path = []
        cur = None  # current point (user space)
        start = None
        pending_clip = None
        in_text = False
        tm = tlm = IDENTITY
        text_clip = None
        oc_stack = []
        hidden = 0
        for idx, ins in enumerate(instrs):
            self.n_ops += 1
            if self.n_ops % 4096 == 0:
                ctx.budget.check_time()
                if self.n_ops > ctx.limits.max_ops_per_page:
                    raise CompileError("limit.ops", f"page {self.pno} exceeds operator limit")
            op = str(ins.operator)
            o = ins.operands
            where = f"{src}#{idx}"
            try:
                # ---- graphics state ------------------------------------------------
                if op == "q":
                    stack.append(gs)
                    gs = gs.copy()
                elif op == "Q":
                    if stack:
                        gs = stack.pop()
                elif op == "cm":
                    gs.ctm = mul(tuple(num(v) for v in o[:6]), gs.ctm)
                elif op == "w":
                    gs.lw = abs(num(o[0]))
                elif op == "J":
                    gs.cap = int(num(o[0]))
                elif op == "j":
                    gs.join = int(num(o[0]))
                elif op == "M":
                    gs.ml = num(o[0], 10.0)
                elif op == "d":
                    gs.dash = [abs(num(v)) for v in o[0]]
                    gs.dash_phase = num(o[1])
                elif op == "gs":
                    self._extgstate(gs, resources, o[0])
                elif op in ("ri", "i"):
                    pass
                # ---- colour ------------------------------------------------------------
                elif op in ("CS", "cs"):
                    if self.t3_uncolored[-1]:
                        continue
                    space = ctx.colorspaces.resolve(o[0], resources)
                    if op == "CS":
                        gs.stroke_cs, gs.stroke, gs.stroke_pattern = space, space.initial_color(), None
                    else:
                        gs.fill_cs, gs.fill, gs.fill_pattern = space, space.initial_color(), None
                elif op in ("SC", "SCN", "sc", "scn"):
                    if self.t3_uncolored[-1]:
                        continue
                    stroke = op[0] == "S"
                    comps = [num(v) for v in o if not isinstance(v, pikepdf.Name)]
                    names = [v for v in o if isinstance(v, pikepdf.Name)]
                    if names:
                        try:
                            self._set_pattern(gs, names[-1], resources, base_matrix, stroke)
                        except Unsupported as e:
                            ctx.diag.warn(e.code, e.message, page=self.pno, source=where)
                            if stroke:
                                gs.stroke_pattern = ("tiling", None)
                            else:
                                gs.fill_pattern = ("tiling", None)
                    if comps:
                        if stroke:
                            gs.stroke = comps
                        else:
                            gs.fill = comps
                elif op in ("G", "g", "RG", "rg", "K", "k"):
                    if self.t3_uncolored[-1]:
                        continue
                    cs = ctx.colorspaces
                    space = {"G": cs.gray, "g": cs.gray, "RG": cs.rgb, "rg": cs.rgb}.get(op)
                    if space is None:
                        space = cs.device("/DeviceCMYK")
                    comps = [num(v) for v in o]
                    if op.isupper():
                        gs.stroke_cs, gs.stroke, gs.stroke_pattern = space, comps, None
                    else:
                        gs.fill_cs, gs.fill, gs.fill_pattern = space, comps, None
                # ---- path construction ---------------------------------------------
                elif op == "m":
                    x, y = num(o[0]), num(o[1])
                    path.append(("M", x, y))
                    cur = start = (x, y)
                elif op == "l":
                    x, y = num(o[0]), num(o[1])
                    if cur is None:
                        path.append(("M", x, y))
                        start = (x, y)
                    elif path and path[-1][0] == "Z":
                        path.append(("M", start[0], start[1]))
                    path.append(("L", x, y))
                    cur = (x, y)
                elif op in ("c", "v", "y"):
                    v = [num(t) for t in o]
                    if cur is None:
                        continue
                    if path and path[-1][0] == "Z":
                        path.append(("M", start[0], start[1]))
                    if op == "c":
                        seg = ("C", v[0], v[1], v[2], v[3], v[4], v[5])
                    elif op == "v":
                        seg = ("C", cur[0], cur[1], v[0], v[1], v[2], v[3])
                    else:
                        seg = ("C", v[0], v[1], v[2], v[3], v[2], v[3])
                    path.append(seg)
                    cur = (seg[5], seg[6])
                elif op == "h":
                    if cur is not None and path and path[-1][0] != "Z":
                        path.append(("Z",))
                        cur = start
                elif op == "re":
                    x, y, w, h = (num(t) for t in o[:4])
                    path.extend(_rect_path(x, y, x + w, y + h))
                    cur = start = (x, y)
                # ---- path painting ------------------------------------------------------
                elif op in ("S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "n"):
                    if op in ("s", "b", "b*") and path and path[-1][0] != "Z":
                        path.append(("Z",))
                    if path and not hidden and op != "n":
                        do_fill = op in ("f", "F", "f*", "B", "B*", "b", "b*")
                        do_stroke = op in ("S", "s", "B", "B*", "b", "b*")
                        rule = "evenodd" if op.endswith("*") else "nonzero"
                        self._paint_path(gs, path, rule if do_fill else None, do_stroke, where)
                    if pending_clip and path:
                        gs.clip = self._add_clip(gs, path, pending_clip)
                    elif pending_clip and not path:
                        gs.clip = self._add_clip(gs, _rect_path(0, 0, 0, 0), "nonzero")
                    pending_clip = None
                    path = []
                    cur = start = None
                elif op in ("W", "W*"):
                    pending_clip = "evenodd" if op == "W*" else "nonzero"
                # ---- XObjects / images / shading ----------------------------------
                elif op == "Do":
                    if not hidden:
                        self._do(gs, resources, o[0], where, depth)
                elif op == "INLINE IMAGE":
                    if not hidden:
                        stream = ctx.images.inline_to_stream(ins.iimage)
                        self._image(gs, stream, resources, where, inline=True)
                elif op == "sh":
                    if not hidden:
                        self._sh(gs, resources, o[0], where)
                # ---- text ------------------------------------------------------------------
                elif op == "BT":
                    in_text = True
                    tm = tlm = IDENTITY
                    text_clip = None
                elif op == "ET":
                    in_text = False
                    if text_clip is not None:
                        gs.clip = self._add_clip(gs, text_clip or _rect_path(0, 0, 0, 0), "nonzero", ctm=IDENTITY)
                        text_clip = None
                elif op == "Tf":
                    fonts = resources.get("/Font")
                    nm = o[0]
                    if fonts is None or nm not in fonts:
                        raise Unsupported("font.missing", f"font resource {nm} not found")
                    gs.font = ctx.fonts.load(fonts[nm])
                    gs.fs = num(o[1])
                elif op == "Tc":
                    gs.tc = num(o[0])
                elif op == "Tw":
                    gs.tw = num(o[0])
                elif op == "Tz":
                    gs.th = num(o[0]) / 100.0
                elif op == "TL":
                    gs.tl = num(o[0])
                elif op == "Tr":
                    gs.tr = int(num(o[0]))
                elif op == "Ts":
                    gs.trise = num(o[0])
                elif op in ("Td", "TD"):
                    tx, ty = num(o[0]), num(o[1])
                    if op == "TD":
                        gs.tl = -ty
                    tlm = mul(translate(tx, ty), tlm)
                    tm = tlm
                elif op == "Tm":
                    tlm = tm = tuple(num(v) for v in o[:6])
                elif op == "T*":
                    tlm = mul(translate(0, -gs.tl), tlm)
                    tm = tlm
                elif op in ("Tj", "'", '"', "TJ"):
                    if op == "'" or op == '"':
                        if op == '"':
                            gs.tw, gs.tc = num(o[0]), num(o[1])
                        tlm = mul(translate(0, -gs.tl), tlm)
                        tm = tlm
                    if gs.font is None:
                        raise Unsupported("font.missing", "text shown without a font")
                    items = list(o[0]) if op == "TJ" else [o[-1]]
                    tm, clip_parts = self._show_text(gs, tm, items, resources, where, depth, hidden)
                    if clip_parts is not None:
                        text_clip = (text_clip or []) + clip_parts
                # ---- marked content / optional content ------------------------------
                elif op in ("BMC", "BDC"):
                    vis = True
                    if op == "BDC" and str(o[0]) == "/OC":
                        prop = o[1]
                        if isinstance(prop, pikepdf.Name):
                            props = resources.get("/Properties")
                            prop = props.get(prop) if props is not None else None
                        if prop is not None:
                            vis = ctx.oc.visible(prop)
                    oc_stack.append(not vis)
                    if not vis:
                        hidden += 1
                elif op == "EMC":
                    if oc_stack and oc_stack.pop():
                        hidden -= 1
                elif op in ("MP", "DP", "BX", "EX"):
                    pass
                # ---- Type 3 glyph metrics ------------------------------------------
                elif op == "d0":
                    pass
                elif op == "d1":
                    self.t3_uncolored[-1] = True
                elif op in ("BI", "ID", "EI"):
                    pass
                else:
                    ctx.diag.info("content.unknown-op", f"unknown operator {op} ignored", page=self.pno)
            except Unsupported as e:
                bbox = None
                if op in ("Tj", "'", '"', "TJ"):
                    bbox = None
                self._unsupported(gs, e.code, e.message, bbox, where)
            except (CompileError, KeyboardInterrupt):
                raise
            except (pikepdf.PdfError, ValueError, TypeError, IndexError, KeyError, AttributeError,
                    ZeroDivisionError) as e:
                ctx.diag.warn("content.operator", f"operator {op} failed: {type(e).__name__}: {e}",
                              page=self.pno, source=where)
        if in_text and text_clip is not None:
            gs.clip = self._add_clip(gs, text_clip, "nonzero", ctm=IDENTITY)

    # ------------------------------------------------------------------
    def _extgstate(self, gs, resources, nm):
        egs = resources.get("/ExtGState")
        if egs is None or nm not in egs:
            raise Unsupported("gs.missing", f"ExtGState {nm} not found")
        d = egs[nm]
        for k, v in d.items():
            if k == "/LW":
                gs.lw = abs(num(v))
            elif k == "/LC":
                gs.cap = int(num(v))
            elif k == "/LJ":
                gs.join = int(num(v))
            elif k == "/ML":
                gs.ml = num(v, 10.0)
            elif k == "/D":
                gs.dash = [abs(num(x)) for x in v[0]]
                gs.dash_phase = num(v[1])
            elif k == "/CA":
                gs.CA = max(0.0, min(1.0, num(v, 1.0)))
            elif k == "/ca":
                gs.ca = max(0.0, min(1.0, num(v, 1.0)))
            elif k == "/BM":
                if isinstance(v, pikepdf.Array):
                    v = v[0] if len(v) else pikepdf.Name("/Normal")
                gs.bm = str(v)
            elif k == "/SMask":
                gs.smask = not (isinstance(v, pikepdf.Name) and str(v) == "/None")
            elif k == "/AIS":
                gs.ais = bool(v)
            elif k == "/OP":
                gs.op_stroke = bool(v)
                if "/op" not in d:
                    gs.op_fill = bool(v)
            elif k == "/op":
                gs.op_fill = bool(v)
            elif k == "/Font":
                gs.font = self.ctx.fonts.load(v[0])
                gs.fs = num(v[1])
            elif k == "/TK" and not bool(v):
                self.ctx.diag.info("text.knockout", "text knockout off is not simulated", page=self.pno)

    def _add_clip(self, gs, path, rule, ctm=None):
        clip = ClipIR(id=len(self.page.clips), parent=gs.clip, segments=list(path),
                      ctm=gs.ctm if ctm is None else ctm, fill_rule=rule)
        self.page.clips.append(clip)
        return clip.id

    def _paint_path(self, gs, path, fill_rule, do_stroke, where):
        try:
            fill = self._paint(gs, False) if fill_rule else None
            stroke = self._paint(gs, True) if do_stroke else None
        except Unsupported as e:
            b = _path_bbox(path, gs.ctm)
            if do_stroke:
                from ..matrix import max_scale
                b = _grow(b, gs.lw * max_scale(gs.ctm) * 5 + 1)
            self._unsupported(gs, e.code, e.message, b, where)
            return
        if fill is not None and fill.kind == "shading":
            # Shading pattern fill: draw the shading clipped to the path, then the stroke.
            payload = ShadingIR(shading_ref=fill.shading_ref, shading_matrix=fill.pattern_matrix,
                                segments=list(path), fill_rule=fill_rule, alpha=fill.alpha)
            self._emit(gs, "shading", payload, where)
            fill = None
            fill_rule = None
            if stroke is None:
                return
        if stroke is not None and stroke.kind == "shading":
            from ..matrix import max_scale
            self._unsupported(gs, "pattern.shading-stroke", "stroke with shading pattern",
                              _grow(_path_bbox(path, gs.ctm), gs.lw * max_scale(gs.ctm) * 5 + 1), where)
            stroke = None
            if fill is None:
                return
        payload = PathIR(segments=list(path), fill_rule=fill_rule, fill=fill, stroke=stroke,
                         stroke_style=self._style(gs) if stroke is not None else None)
        self._emit(gs, "path", payload, where)

    def _sh(self, gs, resources, nm, where):
        shs = resources.get("/Shading")
        if shs is None or nm not in shs:
            raise Unsupported("shading.missing", f"shading {nm} not found")
        obj = shs[nm]
        bbox = self.page_bbox
        b = obj.get("/BBox")
        if b is not None:
            bbox = transform_bbox(gs.ctm, num_list(b))
        try:
            sh = self.ctx.shadings.load(obj, resources)
        except Unsupported as e:
            self._unsupported(gs, e.code, e.message, bbox, where)
            return
        self._emit(gs, "shading", ShadingIR(shading_ref=sh.ref, shading_matrix=gs.ctm, alpha=gs.ca), where)

    def _do(self, gs, resources, nm, where, depth):
        xobjs = resources.get("/XObject")
        if xobjs is None or nm not in xobjs:
            raise Unsupported("xobject.missing", f"XObject {nm} not found")
        x = xobjs[nm]
        if "/OC" in x and not self.ctx.oc.visible(x["/OC"]):
            return
        st = str(x.get("/Subtype", ""))
        if st == "/Image":
            self._image(gs, x, resources, f"{where}:{objid(x)}")
        elif st == "/Form":
            self._form(gs, x, resources, f"{where}:{objid(x)}", depth)
        elif st == "/PS":
            self.ctx.diag.info("xobject.ps", "PostScript XObject ignored", page=self.pno)
        else:
            raise Unsupported("xobject.type", f"XObject subtype {st}")

    def _image(self, gs, stream, resources, where, inline=False):
        bbox = transform_bbox(gs.ctm, (0, 0, 1, 1))
        if "/OC" in stream and not self.ctx.oc.visible(stream["/OC"]):
            return
        try:
            res = self.ctx.images.load(stream, resources, where)
        except Unsupported as e:
            self._unsupported(gs, e.code, e.message, bbox, where)
            return
        fill = None
        if res.stencil:
            try:
                fill = self._paint(gs, False)
            except Unsupported as e:
                self._unsupported(gs, e.code, e.message, bbox, where)
                return
            if fill.kind != "color":
                self._unsupported(gs, "image.stencil-pattern", "stencil mask painted with a pattern", bbox, where)
                return
        payload = ImageIR(image_ref=res.ref, stencil=res.stencil, fill=fill, alpha=gs.ca,
                          interpolate=bool(stream.get("/Interpolate", False)))
        self._emit(gs, "image", payload, where)

    def _form(self, gs, x, resources, where, depth):
        key = objkey(x)
        bbox_user = num_list(x.get("/BBox", [0, 0, 0, 0]))
        matrix = tuple(num_list(x.get("/Matrix", [1, 0, 0, 1, 0, 0])))
        ctm = mul(matrix, gs.ctm)
        page_bbox = transform_bbox(ctm, bbox_user) if len(bbox_user) == 4 else self.page_bbox
        if depth + 1 > self.ctx.limits.max_form_depth or key in self.form_stack:
            self._unsupported(gs, "form.recursion", "recursive or too deeply nested form XObject", page_bbox, where)
            return
        group = x.get("/Group")
        is_tgroup = group is not None and str(group.get("/S", "")) == "/Transparency"
        child = gs.copy()
        child.ctm = ctm
        gid = None
        if is_tgroup:
            knockout = bool(group.get("/K", False))
            isolated = bool(group.get("/I", False))
            needs = gs.ca < 1.0 or gs.bm not in NORMAL_BLEND or gs.smask or knockout
            gid = len(self.page.groups)
            self.page.groups.append(GroupIR(id=gid, parent=self.group_stack[-1], isolated=isolated,
                                            knockout=knockout, opacity=gs.ca, blend_mode=gs.bm[1:],
                                            soft_mask=gs.smask, source=where, flattened=not needs))
            if needs:
                reason = "knockout group" if knockout else (
                    "group opacity" if gs.ca < 1.0 else "group blend/soft mask")
                self._unsupported(gs, "transparency.group", f"transparency group ({reason})", page_bbox, where)
                return
            # The group's content starts with Normal / no soft mask / alpha 1 (all already true).
            child.ca = child.CA = 1.0
        if len(bbox_user) == 4:
            child.clip = self._add_clip(child, _rect_path(*bbox_user), "nonzero")
        res = x.get("/Resources")
        if res is None:
            res = resources
        try:
            instrs = pikepdf.parse_content_stream(x)
        except pikepdf.PdfError as e:
            self._unsupported(gs, "content.parse", f"form content unreadable: {e}", page_bbox, where)
            return
        self.form_stack.append(key)
        self.group_stack.append(gid if gid is not None else self.group_stack[-1])
        try:
            self._exec(instrs, res, child, ctm, where, depth + 1)
        finally:
            self.form_stack.pop()
            self.group_stack.pop()

    # ------------------------------------------------------------------
    def _show_text(self, gs, tm, items, resources, where, depth, hidden):
        font = gs.font
        fs, th, tc, tw, rise = gs.fs, gs.th, gs.tc, gs.tw, gs.trise
        mode = gs.tr
        visible = mode not in (3, 7) and not hidden
        clip_mode = mode >= 4
        glyphs = []
        run_bbox = None
        clip_parts = [] if clip_mode else None
        size_m = (fs * th, 0.0, 0.0, fs, 0.0, rise)
        for item in items:
            if not isinstance(item, (pikepdf.String, bytes)):
                adj = num(item)
                if font.vertical:
                    tm = mul(translate(0, -adj / 1000.0 * fs), tm)
                else:
                    tm = mul(translate(-adj / 1000.0 * fs * th, 0), tm)
                continue
            data = pdf_string_bytes(item)
            for g in font.decode(data):
                self.n_glyphs += 1
                trm = mul(size_m, tm)
                if font.vertical:
                    gm = mul(translate(-g.vx, -g.vy), trm)
                elif g.x_scale != 1.0:
                    gm = mul((g.x_scale, 0, 0, 1, 0, 0), trm)
                else:
                    gm = trm
                if font.is_type3:
                    if visible and g.t3_name:
                        self._type3_glyph(gs, font, g.t3_name, trm, resources, where, depth)
                    if clip_mode:
                        self.ctx.diag.warn("text.type3-clip", "Type 3 text used as clip is ignored",
                                           page=self.pno)
                else:
                    gb = transform_bbox(mul(gm, gs.ctm), font.bbox)
                    run_bbox = _union(run_bbox, gb)
                    if g.key is not None:
                        glyphs.append(Glyph(key=g.key, matrix=gm))
                        if clip_mode and font.unsupported_reason is None:
                            outline = font.outline(g.key)
                            if outline:
                                full = mul(gm, gs.ctm)
                                clip_parts.extend(_transform_cmds(outline, full))
                if font.vertical:
                    ty = g.w1 * fs + tc + (tw if g.is_space else 0.0)
                    tm = mul(translate(0, ty), tm)
                else:
                    tx = (g.w0 * fs + tc + (tw if g.is_space else 0.0)) * th
                    tm = mul(translate(tx, 0), tm)
        if self.n_glyphs > self.ctx.limits.max_glyphs_per_page:
            raise CompileError("limit.glyphs", f"page {self.pno} exceeds glyph limit")
        if font.is_type3 or not visible or (not glyphs and run_bbox is None):
            return tm, clip_parts
        if font.unsupported_reason is not None:
            code, msg = font.unsupported_reason
            self._unsupported(gs, code, f"text in font {font.base_name}: {msg}", _grow(run_bbox, 1.0), where)
            return tm, clip_parts
        for g in glyphs:
            font.outline(g.key)  # materialise outlines in the font resource
        try:
            fill = self._paint(gs, False) if mode in (0, 2, 4, 6) else None
            stroke = self._paint(gs, True) if mode in (1, 2, 5, 6) else None
        except Unsupported as e:
            self._unsupported(gs, e.code, e.message, _grow(run_bbox, 1.0), where)
            return tm, clip_parts
        if (fill is not None and fill.kind != "color") or (stroke is not None and stroke.kind != "color"):
            self._unsupported(gs, "text.pattern", "text painted with a pattern", _grow(run_bbox, 1.0), where)
            return tm, clip_parts
        payload = GlyphRunIR(font_ref=font.resource.ref, glyphs=glyphs, text_rendering_mode=mode, fill=fill,
                             stroke=stroke, stroke_style=self._style(gs) if stroke is not None else None)
        payload._bbox = run_bbox
        self._emit(gs, "glyph_run", payload, where)
        return tm, clip_parts

    def _type3_glyph(self, gs, font, name, trm, resources, where, depth):
        procs = font.char_procs
        if procs is None or ("/" + name) not in procs:
            return
        if self.t3_depth > 4:
            raise Unsupported("font.type3-recursion", "Type 3 glyph recursion")
        proc = procs["/" + name]
        child = gs.copy()
        child.ctm = mul(mul(font.font_matrix, trm), gs.ctm)
        res = font.t3_resources if font.t3_resources is not None else resources
        instrs = pikepdf.parse_content_stream(proc)
        self.t3_uncolored.append(False)
        self.t3_depth += 1
        try:
            self._exec(instrs, res, child, child.ctm, f"{where}:t3/{name}", depth + 1)
        finally:
            self.t3_uncolored.pop()
            self.t3_depth -= 1


def _transform_cmds(cmds, m):
    out = []
    for c in cmds:
        if c[0] == "Z":
            out.append(c)
            continue
        pts = []
        v = c[1:]
        for i in range(0, len(v), 2):
            x, y = v[i], v[i + 1]
            pts.extend((m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5]))
        out.append((c[0], *pts))
    return out
