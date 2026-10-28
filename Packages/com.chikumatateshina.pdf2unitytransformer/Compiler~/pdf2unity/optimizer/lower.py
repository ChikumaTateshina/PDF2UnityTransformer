"""IR -> ordered render batches for one target profile (SPEC §5, §6).

Ordering model (SPEC §5.5): every draw item keeps its PDF painting position.
Consecutive items that use the same material (vertex-coloured, or the same
texture) are merged into one batch *without reordering*; inside a batch the
triangle order equals the painting order, and GPUs blend primitives in
submission order. Batches get increasing Unity `sortingOrder` values.

Precomposition (FR-09, SPEC §5.4): `unsupported` operations mark page areas.
Overlapping areas are merged into rectangles, rasterised by PDFium from the
original drawing commands (including everything underneath them), and drawn
*after* all native batches, so the composite fully replaces that area.
Native items completely inside a composite rectangle are dropped.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field

import numpy as np

from ..diagnostics import CompileError
from ..ir.model import DocumentIR, PageIR
from ..matrix import invert, max_scale, mean_scale, mul, transform_bbox
from ..options import Profile
from . import geometry as G

AA_MARGIN_PT = 1.0  # composites are grown by this much to cover anti-aliasing


@dataclass
class DrawItem:
    kind: str  # "solid" | "textured"
    verts: np.ndarray  # (n, 2) page space
    idx: np.ndarray
    colors: np.ndarray  # (n, 4) uint8
    uvs: np.ndarray | None = None
    texture: str | None = None
    op_id: int = -1
    bbox: tuple | None = None  # display space


@dataclass
class CompositeRegion:
    rect: tuple  # display space points (x0, y0, x1, y1)
    reasons: list = field(default_factory=list)
    op_ids: list = field(default_factory=list)
    dpi: float = 0.0
    file: str | None = None
    pixel_size: tuple | None = None


@dataclass
class Batch:
    kind: str  # "solid" | "textured"
    texture: str | None  # image ref or composite id
    composite: bool
    verts: np.ndarray  # (n, 2) Unity units, float32
    colors: np.ndarray  # (n, 4) uint8
    uvs: np.ndarray | None
    idx: np.ndarray  # uint32
    op_range: tuple = (-1, -1)


@dataclass
class RenderPage:
    index: int
    display_size: tuple  # points
    unity_size: tuple  # Unity units
    page_to_unity: tuple
    batches: list = field(default_factory=list)
    composites: list = field(default_factory=list)
    stats: dict = field(default_factory=dict)
    image_needs: dict = field(default_factory=dict)  # image ref -> needed pixel size (max of w, h)


class ClipShape:
    __slots__ = ("rect", "polys", "bbox", "empty")

    def __init__(self, polys, rect=None):
        self.polys = polys
        self.rect = rect
        self.bbox = rect if rect is not None else G.bbox_of_int(polys)
        self.empty = self.bbox is None or not polys


def _as_rect(polys):
    if len(polys) != 1 or len(polys[0]) != 4:
        return None
    xs = sorted({p[0] for p in polys[0]})
    ys = sorted({p[1] for p in polys[0]})
    if len(xs) != 2 or len(ys) != 2:
        return None
    s = G.PAGE_SCALE
    return (xs[0] / s, ys[0] / s, xs[1] / s, ys[1] / s)


class ClipResolver:
    def __init__(self, page: PageIR, tol):
        self.page = page
        self.tol = tol
        self.cache = {}

    def get(self, cid) -> ClipShape:
        if cid in self.cache:
            return self.cache[cid]
        node = self.page.clips[cid]
        polys = G.simplify(G.fill_outline(node.segments, node.ctm, node.fill_rule, self.tol), node.fill_rule)
        rect = _as_rect(polys)
        if node.parent is not None:
            parent = self.get(node.parent)
            if parent.empty:
                shape = parent
            elif rect is not None and parent.rect is not None:
                r = G.bbox_intersection(rect, parent.rect)
                if r is None:
                    shape = ClipShape([], None)
                    shape.empty = True
                else:
                    shape = ClipShape([_rect_int(r)], r)
            else:
                res = G.intersect_paths(polys, "nonzero", parent.polys)
                shape = ClipShape(res, _as_rect(res))
        else:
            shape = ClipShape(polys, rect)
        self.cache[cid] = shape
        return shape


def _rect_int(r):
    s = G.PAGE_SCALE
    x0, y0, x1, y1 = (int(round(v * s)) for v in r)
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


def _rgba(rgb, alpha):
    return (int(rgb[0] * 255 + 0.5), int(rgb[1] * 255 + 0.5), int(rgb[2] * 255 + 0.5),
            int(max(0.0, min(1.0, alpha)) * 255 + 0.5))


class GlyphCache:
    """Tessellated glyph outlines in text space, keyed by tolerance bucket (FR-15)."""

    S = 1 << 16

    def __init__(self, ir: DocumentIR):
        self.ir = ir
        self.cache = {}

    def get(self, font_ref, key, tol_em):
        bucket = 2.0 ** math.floor(math.log2(max(tol_em, 1e-6)))
        ck = (font_ref, key, bucket)
        hit = self.cache.get(ck)
        if hit is not None:
            return hit
        cmds = self.ir.fonts[font_ref].glyphs.get(key) or []
        subs = G.flatten(cmds, (1, 0, 0, 1, 0, 0), bucket)
        s = self.S / max(bucket * 64, 1e-9) if bucket < 1 else self.S
        s = min(max(s, 1000.0), 1e7)
        ints = [G.to_int(p, s) for p, _ in subs if len(p) >= 3]
        polys = G.simplify(ints, "nonzero")
        verts, idx = G.polys_to_tris(polys, s)
        poly_f = [np.asarray(p, dtype=np.float64) / s for p in polys]
        if len(verts):
            bb = (float(verts[:, 0].min()), float(verts[:, 1].min()), float(verts[:, 0].max()),
                  float(verts[:, 1].max()))
        else:
            bb = None
        hit = (verts, idx, poly_f, bb)
        self.cache[ck] = hit
        return hit


class PageLowering:
    def __init__(self, ir: DocumentIR, page: PageIR, profile: Profile, mode: str, diag, glyph_cache,
                 precompose_translucent=False):
        self.ir = ir
        self.page = page
        self.profile = profile
        self.mode = mode
        self.diag = diag
        self.pno = page.index + 1
        self.tol = profile.curve_tolerance_pt / max(page.user_unit, 1e-6)
        self.min_w = profile.min_stroke_width_pt / max(page.user_unit, 1e-6)
        self.clips = ClipResolver(page, self.tol)
        self.glyphs = glyph_cache
        self.precompose_translucent = precompose_translucent
        self.items: list[DrawItem] = []
        self.unsupported: list[tuple] = []  # (display bbox, reason, op id)
        self.image_needs = {}
        W, H = page.display_size
        k = page.user_unit / 72.0
        self.page_to_unity = mul(page.page_to_display, (k, 0.0, 0.0, -k, -W / 2 * k, H / 2 * k))
        self.display_rect = (0.0, 0.0, W, H)

    # ------------------------------------------------------------------
    def run(self) -> RenderPage:
        for op in self.page.operations:
            fn = getattr(self, f"_op_{op.kind}")
            fn(op)
        regions = self._composite_regions()
        page = RenderPage(index=self.page.index, display_size=self.page.display_size,
                          unity_size=(self.page.display_size[0] * self.page.user_unit / 72.0,
                                      self.page.display_size[1] * self.page.user_unit / 72.0),
                          page_to_unity=self.page_to_unity, composites=regions,
                          image_needs=self.image_needs)
        items = [it for it in self.items if not any(G.bbox_contains(r.rect, it.bbox) for r in regions)]
        page.batches = self._batch(items)
        page.stats = {
            "operations": len(self.page.operations),
            "drawItems": len(items),
            "droppedUnderComposite": len(self.items) - len(items),
            "unsupportedOperations": len(self.unsupported),
        }
        return page

    # ------------------------------------------------------------------
    def _display_bbox(self, page_bbox):
        if page_bbox is None:
            return None
        return transform_bbox(self.page.page_to_display, page_bbox)

    def _add(self, kind, verts, idx, color, op, uvs=None, texture=None, colors=None):
        if len(idx) == 0:
            return
        if colors is None:
            colors = np.empty((len(verts), 4), dtype=np.uint8)
            colors[:] = color
        bb = (float(verts[:, 0].min()), float(verts[:, 1].min()), float(verts[:, 0].max()), float(verts[:, 1].max()))
        dbb = self._display_bbox(bb)
        if not G.bbox_intersects(dbb, self.display_rect):
            return
        self.items.append(DrawItem(kind, verts, idx, colors, uvs, texture, op.id, dbb))

    def _clip_tris(self, subject_int, rule, clip: ClipShape, bbox):
        if clip.empty or bbox is None or not G.bbox_intersects(bbox, clip.bbox):
            return G.EMPTY
        if clip.rect is not None and G.bbox_contains(clip.rect, bbox):
            tree = G.intersect_tree(subject_int, rule, None)
        else:
            tree = G.intersect_tree(subject_int, rule, clip.polys)
        return G.tree_to_tris(tree, G.PAGE_SCALE)

    def _mark_unsupported(self, op, bbox, reason):
        clip = self.clips.get(op.clip_ref)
        if clip.empty:
            return
        b = G.bbox_intersection(bbox, clip.bbox) if bbox is not None else clip.bbox
        if b is None:
            return
        db = self._display_bbox(b)
        db = G.bbox_intersection((db[0] - AA_MARGIN_PT, db[1] - AA_MARGIN_PT, db[2] + AA_MARGIN_PT,
                                  db[3] + AA_MARGIN_PT), self.display_rect)
        if db is None:
            return
        self.unsupported.append((db, reason, op.id))

    def _translucent(self, op, bbox) -> bool:
        """Linear-colour-space engines blend translucent colours differently from PDF (gamma space).
        Either precomposite such content (option) or report it once per page."""
        if bbox is None:
            return False
        if self.precompose_translucent:
            self._mark_unsupported(op, bbox, "transparency.linear-blend")
            return True
        self.diag.info("transparency.linear-blend",
                       "translucent content is blended by the GPU; in Linear colour space projects the result "
                       "differs slightly from PDF (use --translucency precompose for exact results)", page=self.pno)
        return False

    # ---- operations --------------------------------------------------
    def _op_unsupported(self, op):
        p = op.payload
        self._mark_unsupported(op, p.bbox, p.reason)

    def _op_path(self, op):
        p = op.payload
        clip = self.clips.get(op.clip_ref)
        if clip.empty:
            return
        if ((p.fill is not None and p.fill.alpha < 1) or (p.stroke is not None and p.stroke.alpha < 1)) and                 self._translucent(op, _grow(G.bbox_of_int(G.fill_outline(p.segments, op.ctm, "nonzero", 1.0)) or
                                            self.page.crop_box,
                                            (p.stroke_style.width * max_scale(op.ctm) * 5 + 1) if p.stroke else 0)):
            return
        if p.fill is not None and p.fill_rule:
            subj = G.fill_outline(p.segments, op.ctm, p.fill_rule, self.tol)
            bb = G.bbox_of_int(subj)
            v, i = self._clip_tris(subj, p.fill_rule, clip, bb)
            self._add("solid", v, i, _rgba(p.fill.rgb, p.fill.alpha), op)
        if p.stroke is not None:
            polys = G.stroke_outline(p.segments, op.ctm, p.stroke_style, self.tol, self.min_w)
            bb = G.bbox_of_int(polys)
            v, i = self._clip_tris(polys, "nonzero", clip, bb)
            self._add("solid", v, i, _rgba(p.stroke.rgb, p.stroke.alpha), op)

    def _op_glyph_run(self, op):
        p = op.payload
        clip = self.clips.get(op.clip_ref)
        if clip.empty:
            return
        mode = p.text_rendering_mode
        if (p.fill is not None and p.fill.alpha < 1) or (p.stroke is not None and p.stroke.alpha < 1):
            run_bb = None
            for g in p.glyphs:
                gb = self.glyphs.get(p.font_ref, g.key, 1.0)[3]
                if gb is not None:
                    tb = transform_bbox(mul(g.matrix, op.ctm), gb)
                    run_bb = tb if run_bb is None else (min(run_bb[0], tb[0]), min(run_bb[1], tb[1]),
                                                        max(run_bb[2], tb[2]), max(run_bb[3], tb[3]))
            if self._translucent(op, _grow(run_bb, 2.0) if run_bb else None):
                return
        if p.fill is not None and mode in (0, 2, 4, 6):
            vs, ix = [], []
            base = 0
            for g in p.glyphs:
                m = mul(g.matrix, op.ctm)
                sc = mean_scale(m)
                if sc <= 0:
                    continue
                verts, idx, polys, bb = self.glyphs.get(p.font_ref, g.key, self.tol / sc)
                if bb is None:
                    continue
                pbb = transform_bbox(m, bb)
                if not G.bbox_intersects(pbb, clip.bbox):
                    continue
                if clip.rect is not None and G.bbox_contains(clip.rect, pbb):
                    tv = G.transform_points(m, verts)
                    ti = idx
                else:
                    subj = [G.to_int(G.transform_points(m, q), G.PAGE_SCALE) for q in polys]
                    tv, ti = self._clip_tris(subj, "nonzero", clip, pbb)
                if len(ti):
                    vs.append(tv)
                    ix.append(ti + base)
                    base += len(tv)
            if vs:
                self._add("solid", np.vstack(vs), np.concatenate(ix), _rgba(p.fill.rgb, p.fill.alpha), op)
        if p.stroke is not None and mode in (1, 2, 5, 6):
            font = self.ir.fonts[p.font_ref]
            allp = []
            for g in p.glyphs:
                cmds = font.glyphs.get(g.key) or []
                if not cmds:
                    continue
                user = _xform_cmds(cmds, g.matrix)
                allp.extend(G.stroke_outline(user, op.ctm, p.stroke_style, self.tol, self.min_w))
            polys = G.simplify(allp, "nonzero")
            v, i = self._clip_tris(polys, "nonzero", clip, G.bbox_of_int(polys))
            self._add("solid", v, i, _rgba(p.stroke.rgb, p.stroke.alpha), op)

    def _op_image(self, op):
        p = op.payload
        clip = self.clips.get(op.clip_ref)
        if clip.empty:
            return
        m = op.ctm
        try:
            inv = invert(m)
        except ZeroDivisionError:
            return
        quad = np.array([[0, 0], [1, 0], [1, 1], [0, 1]], dtype=np.float64)
        pq = G.transform_points(m, quad)
        res_img = self.ir.images[p.image_ref]
        if (p.alpha < 1 or getattr(res_img, "soft_alpha", False)) and self._translucent(op, transform_bbox(m, (0, 0, 1, 1))):
            return
        subj = [G.to_int(pq, G.PAGE_SCALE)]
        bb = G.bbox_of_int(subj)
        v, i = self._clip_tris(subj, "nonzero", clip, bb)
        if len(i) == 0:
            return
        uv = np.clip(G.transform_points(inv, v), 0.0, 1.0)
        if p.stencil:
            color = _rgba(p.fill.rgb, p.fill.alpha * 1.0)
            color = (color[0], color[1], color[2], int(max(0, min(1, p.alpha)) * 255 + 0.5))
        else:
            color = (255, 255, 255, int(max(0, min(1, p.alpha)) * 255 + 0.5))
        self._add("textured", v, i, color, op, uvs=uv, texture=p.image_ref)
        # Effective display resolution needed for this use of the image (SPEC §6.4):
        # ratio = needed pixels / source pixels along the more demanding axis.
        res = self.ir.images[p.image_ref]
        k = self.profile.image_dpi / 72.0 * self.page.user_unit
        ratio = max(math.hypot(m[0], m[1]) * k / res.width, math.hypot(m[2], m[3]) * k / res.height)
        self.image_needs[p.image_ref] = max(self.image_needs.get(p.image_ref, 0.0), ratio)

    def _op_shading(self, op):
        p = op.payload
        sh = self.ir.shadings[p.shading_ref]
        clip = self.clips.get(op.clip_ref)
        if clip.empty:
            return
        if p.segments:
            subj = G.fill_outline(p.segments, op.ctm, p.fill_rule or "nonzero", self.tol)
            region = G.intersect_paths(subj, p.fill_rule or "nonzero", clip.polys)
        else:
            region = clip.polys
        if sh.bbox and len(sh.bbox) == 4:
            bpoly = G.transform_points(p.shading_matrix, np.array(
                [[sh.bbox[0], sh.bbox[1]], [sh.bbox[2], sh.bbox[1]], [sh.bbox[2], sh.bbox[3]],
                 [sh.bbox[0], sh.bbox[3]]], dtype=np.float64))
            region = G.intersect_paths(region, "nonzero", [G.to_int(bpoly, G.PAGE_SCALE)])
        if not region:
            return
        if p.alpha < 1 and self._translucent(op, G.bbox_of_int(region)):
            return
        if p.segments and sh.background:
            v, i = G.polys_to_tris(region, G.PAGE_SCALE)
            self._add("solid", v, i, _rgba(sh.background, p.alpha), op)
        try:
            if sh.shading_type == 2:
                v, i, c = shade_axial(sh, p.shading_matrix, region, self.tol, p.alpha)
            else:
                v, i, c = shade_radial(sh, p.shading_matrix, region, self.tol, p.alpha)
        except ZeroDivisionError:
            return
        self._add("solid", v, i, None, op, colors=c)

    # ------------------------------------------------------------------
    def _composite_regions(self):
        if not self.unsupported:
            return []
        if self.mode == "strict":
            reasons = sorted({r for _, r, _ in self.unsupported})
            raise CompileError("strict.unsupported",
                               f"page {self.pno}: {len(self.unsupported)} operation(s) cannot be reproduced "
                               f"natively ({', '.join(reasons)})")
        if self.mode == "page-fallback":
            r = CompositeRegion(rect=self.display_rect, reasons=sorted({x[1] for x in self.unsupported}),
                                op_ids=sorted(x[2] for x in self.unsupported))
            self.diag.warn("fallback.page", "page precomposited as a whole (page-fallback mode)", page=self.pno,
                           fallback="page-raster")
            return [r]
        regions = [CompositeRegion(rect=b, reasons=[r], op_ids=[i]) for b, r, i in self.unsupported]
        merged = True
        while merged:
            merged = False
            out = []
            for r in regions:
                for o in out:
                    if G.bbox_intersects(_grow(o.rect, 0.5), r.rect):
                        o.rect = (min(o.rect[0], r.rect[0]), min(o.rect[1], r.rect[1]),
                                  max(o.rect[2], r.rect[2]), max(o.rect[3], r.rect[3]))
                        o.reasons = sorted(set(o.reasons) | set(r.reasons))
                        o.op_ids = sorted(set(o.op_ids) | set(r.op_ids))
                        merged = True
                        break
                else:
                    out.append(r)
            regions = out
        regions.sort(key=lambda r: (r.op_ids[0], r.rect))
        W, H = self.page.display_size
        area = sum((r.rect[2] - r.rect[0]) * (r.rect[3] - r.rect[1]) for r in regions)
        self.diag.info("fallback.regions", f"{len(regions)} precomposited region(s) covering "
                       f"{100 * area / max(W * H, 1e-9):.1f}% of the page", page=self.pno,
                       fallback="region-raster")
        return regions

    def _batch(self, items):
        U = self.page_to_unity
        batches = []
        cur = None

        def flush():
            if cur is None:
                return
            vs, cs, uvs, ix, kind, tex, ops = cur
            verts = np.vstack(vs)
            batches.append(Batch(kind=kind, texture=tex, composite=False,
                                 verts=G.transform_points(U, verts).astype(np.float32),
                                 colors=np.vstack(cs), uvs=np.vstack(uvs).astype(np.float32) if uvs else None,
                                 idx=np.concatenate(ix).astype(np.uint32), op_range=(ops[0], ops[-1])))

        count = 0
        for it in items:
            if cur is not None and (cur[4] != it.kind or cur[5] != it.texture or count + len(it.verts) > 4_000_000):
                flush()
                cur = None
            if cur is None:
                cur = ([], [], [], [], it.kind, it.texture, [])
                count = 0
            cur[3].append(it.idx + count)
            cur[0].append(it.verts)
            cur[1].append(it.colors)
            if it.uvs is not None:
                cur[2].append(it.uvs)
            cur[6].append(it.op_id)
            count += len(it.verts)
        flush()
        return batches


def _grow(r, d):
    return (r[0] - d, r[1] - d, r[2] + d, r[3] + d)


def _xform_cmds(cmds, m):
    out = []
    for c in cmds:
        if c[0] == "Z":
            out.append(c)
            continue
        v = c[1:]
        pts = []
        for i in range(0, len(v), 2):
            x, y = v[i], v[i + 1]
            pts.extend((m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5]))
        out.append((c[0], *pts))
    return out


# ---------------------------------------------------------------------------
# Shadings


def _ramp_color(sh, s_clamped):
    """s in [0, 1] (shading parameter) -> ramp colour rows (n, 3) floats."""
    ramp = np.asarray(sh.ramp, dtype=np.float64)
    n = len(ramp) - 1
    f = np.clip(s_clamped, 0.0, 1.0) * n
    i0 = np.floor(f).astype(np.int64)
    i1 = np.minimum(i0 + 1, n)
    w = (f - i0)[:, None]
    return ramp[i0] * (1 - w) + ramp[i1] * w


def _colors_for(sh, s, alpha):
    rgb = _ramp_color(sh, s)
    out = np.empty((len(s), 4), dtype=np.uint8)
    out[:, :3] = np.clip(rgb * 255 + 0.5, 0, 255).astype(np.uint8)
    out[:, 3] = int(max(0, min(1, alpha)) * 255 + 0.5)
    return out


def _region_corners(region_int):
    bb = G.bbox_of_int(region_int)
    return np.array([[bb[0], bb[1]], [bb[2], bb[1]], [bb[2], bb[3]], [bb[0], bb[3]]], dtype=np.float64)


def shade_axial(sh, Ms, region, tol, alpha):
    x0, y0, x1, y1 = sh.coords
    inv = invert(Ms)
    corners = G.transform_points(inv, _region_corners(region))
    d = np.array([x1 - x0, y1 - y0])
    L2 = float(d @ d)
    if L2 <= 0:
        return G.EMPTY[0], G.EMPTY[1], np.zeros((0, 4), np.uint8)
    q = np.array([-d[1], d[0]]) / math.sqrt(L2)
    rel = corners - np.array([x0, y0])
    svals = rel @ d / L2
    pvals = rel @ q
    pmin, pmax = float(pvals.min()) - 1, float(pvals.max()) + 1
    smin, smax = float(svals.min()), float(svals.max())
    lo = smin if sh.extend[0] else max(smin, 0.0)
    hi = smax if sh.extend[1] else min(smax, 1.0)
    if lo >= hi:
        return G.EMPTY[0], G.EMPTY[1], np.zeros((0, 4), np.uint8)
    phys = math.hypot(*(np.array(G.transform_points(Ms, np.array([[x1, y1]]))[0]) -
                        np.array(G.transform_points(Ms, np.array([[x0, y0]]))[0])))
    n = int(min(255, max(1, math.ceil(phys / 1.5))))
    cuts = sorted({lo, hi} | {k / n for k in range(0, n + 1) if lo < k / n < hi})
    p0 = np.array([x0, y0])
    vs, ix, cs = [], [], []
    base = 0
    for sa, sb in zip(cuts[:-1], cuts[1:]):
        band = np.array([p0 + sa * d + pmin * q, p0 + sb * d + pmin * q, p0 + sb * d + pmax * q,
                         p0 + sa * d + pmax * q])
        poly = G.to_int(G.transform_points(Ms, band), G.PAGE_SCALE)
        tree = G.intersect_tree([poly], "nonzero", region)
        v, i = G.tree_to_tris(tree, G.PAGE_SCALE)
        if not len(i):
            continue
        sv = (G.transform_points(inv, v) - p0) @ d / L2
        sv = np.clip(sv, sa, sb)
        vs.append(v)
        ix.append(i + base)
        cs.append(_colors_for(sh, sv, alpha))
        base += len(v)
    if not vs:
        return G.EMPTY[0], G.EMPTY[1], np.zeros((0, 4), np.uint8)
    return np.vstack(vs), np.concatenate(ix), np.vstack(cs)


def shade_radial(sh, Ms, region, tol, alpha):
    x0, y0, r0, x1, y1, r1 = sh.coords
    inv = invert(Ms)
    corners = G.transform_points(inv, _region_corners(region))
    c0 = np.array([x0, y0])
    cd = np.array([x1 - x0, y1 - y0])
    dr = r1 - r0
    scale = mean_scale(Ms)

    def rad(s):
        return r0 + s * dr

    def covers(s):
        c = c0 + s * cd
        return rad(s) >= float(np.max(np.hypot(*(corners - c).T)))

    lo, hi = 0.0, 1.0
    if sh.extend[0]:
        if dr > 0:
            lo = -r0 / dr
        else:
            step = 1.0
            while not covers(-step) and step < 1e6:
                step *= 2
            lo = -step
    if sh.extend[1]:
        if dr < 0:
            hi = -r0 / dr
        else:
            step = 1.0
            while not covers(1 + step) and step < 1e6:
                step *= 2
            hi = 1 + step
    span_phys = (abs(dr) + float(np.hypot(*cd))) * (hi - lo) * scale
    n = int(min(512, max(8, math.ceil(span_phys / 1.5))))
    cuts = [lo + (hi - lo) * k / n for k in range(n + 1)]
    tol_sh = tol / max(scale, 1e-9)

    def disc(s):
        r = rad(s)
        if r <= 0:
            return []
        c = c0 + s * cd
        return [G.to_int(G.transform_points(Ms, G.circle(c[0], c[1], r, tol_sh)), G.PAGE_SCALE)]

    import pyclipper

    # Later (larger s) bands are painted on top; subtract their coverage from earlier
    # bands so the shading is a single non-overlapping layer (correct for alpha < 1).
    bands = []
    covered = []
    discs = [disc(s) for s in cuts]
    for k in range(n - 1, -1, -1):
        ring = G.boolean(discs[k + 1], discs[k], pyclipper.CT_XOR)
        if not ring:
            continue
        ring = G.intersect_paths(ring, "nonzero", region)
        if covered:
            ring = G.boolean(ring, covered, pyclipper.CT_DIFFERENCE)
        if not ring:
            continue
        covered = G.boolean(covered, ring, pyclipper.CT_UNION)
        bands.append((cuts[k], cuts[k + 1], ring))
    bands.reverse()
    vs, ix, cs = [], [], []
    base = 0
    for sa, sb, ring in bands:
        v, i = G.polys_to_tris(ring, G.PAGE_SCALE)
        if not len(i):
            continue
        sp = G.transform_points(inv, v)
        sv = _radial_s(sp, c0, cd, r0, dr, sa, sb)
        vs.append(v)
        ix.append(i + base)
        cs.append(_colors_for(sh, np.clip(sv, 0.0, 1.0), alpha))
        base += len(v)
    if not vs:
        return G.EMPTY[0], G.EMPTY[1], np.zeros((0, 4), np.uint8)
    return np.vstack(vs), np.concatenate(ix), np.vstack(cs)


def _radial_s(p, c0, cd, r0, dr, sa, sb):
    pd = p - c0
    a = float(cd @ cd - dr * dr)
    b = -2 * (pd @ cd + r0 * dr)
    c = np.einsum("ij,ij->i", pd, pd) - r0 * r0
    mid = (sa + sb) / 2
    if abs(a) < 1e-12:
        with np.errstate(divide="ignore", invalid="ignore"):
            s = np.where(np.abs(b) > 1e-12, -c / b, mid)
    else:
        disc = b * b - 4 * a * c
        sq = np.sqrt(np.maximum(disc, 0))
        s1 = (-b + sq) / (2 * a)
        s2 = (-b - sq) / (2 * a)
        hi_root = np.maximum(s1, s2)
        lo_root = np.minimum(s1, s2)
        s = np.where(r0 + hi_root * dr >= 0, hi_root, lo_root)
        s = np.where(disc < 0, mid, s)
    return np.clip(s, sa, sb)
