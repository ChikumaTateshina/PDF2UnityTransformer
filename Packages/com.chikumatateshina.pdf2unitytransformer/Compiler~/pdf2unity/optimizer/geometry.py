"""Vector geometry: flattening, dashing, fill rules, stroking, clipping and
triangulation (SPEC §5.1).

Boolean operations and stroke offsetting use Clipper (integer coordinates);
triangulation uses earcut on Clipper's simple, non-overlapping output, so the
resulting triangles never overlap. That matters because all content is alpha
blended in painting order: an overlap would double-blend translucent fills.
"""

from __future__ import annotations

import math

import mapbox_earcut as earcut
import numpy as np
import pyclipper

from ..matrix import IDENTITY, apply, max_scale

PAGE_SCALE = 1000.0  # clipper integer units per point in page space

FILL_TYPES = {"nonzero": pyclipper.PFT_NONZERO, "evenodd": pyclipper.PFT_EVENODD}
JOINS = {0: pyclipper.JT_MITER, 1: pyclipper.JT_ROUND, 2: pyclipper.JT_SQUARE}
CAPS = {0: pyclipper.ET_OPENBUTT, 1: pyclipper.ET_OPENROUND, 2: pyclipper.ET_OPENSQUARE}

EMPTY = (np.zeros((0, 2), np.float64), np.zeros((0,), np.uint32))


# ---------------------------------------------------------------------------
# Flattening


def flatten(cmds, m=IDENTITY, tol=0.05):
    """Path commands -> [(points ndarray (n,2), closed)] in the space of `m`."""
    out = []
    pts = []
    closed = False
    cx = cy = 0.0
    sx = sy = 0.0
    a, b, c, d, e, f = m

    def tp(x, y):
        return (a * x + c * y + e, b * x + d * y + f)

    for cmd in cmds:
        op = cmd[0]
        if op == "M":
            if len(pts) > 0:
                out.append((pts, closed))
            cx, cy = tp(cmd[1], cmd[2])
            sx, sy = cx, cy
            pts = [(cx, cy)]
            closed = False
        elif op == "L":
            if not pts:
                pts = [(cx, cy)]
            cx, cy = tp(cmd[1], cmd[2])
            pts.append((cx, cy))
        elif op == "C":
            if not pts:
                pts = [(cx, cy)]
            x1, y1 = tp(cmd[1], cmd[2])
            x2, y2 = tp(cmd[3], cmd[4])
            x3, y3 = tp(cmd[5], cmd[6])
            ddx = max(abs(cx - 2 * x1 + x2), abs(x1 - 2 * x2 + x3))
            ddy = max(abs(cy - 2 * y1 + y2), abs(y1 - 2 * y2 + y3))
            dd = math.hypot(ddx, ddy)
            n = int(math.ceil(math.sqrt(0.75 * dd / tol))) if dd > 0 else 1
            n = max(1, min(n, 512))
            for i in range(1, n + 1):
                t = i / n
                u = 1 - t
                w0, w1, w2, w3 = u * u * u, 3 * u * u * t, 3 * u * t * t, t * t * t
                pts.append((w0 * cx + w1 * x1 + w2 * x2 + w3 * x3, w0 * cy + w1 * y1 + w2 * y2 + w3 * y3))
            cx, cy = x3, y3
        elif op == "Z":
            if pts:
                closed = True
                out.append((pts, True))
                pts = []
            cx, cy = sx, sy
    if len(pts) > 0:
        out.append((pts, closed))
    return [(np.asarray(p, dtype=np.float64), cl) for p, cl in out]


# ---------------------------------------------------------------------------
# Dashes


def dash_polyline(pts: np.ndarray, closed: bool, dashes, phase: float):
    """Split one polyline into dash pieces (open polylines)."""
    if closed and len(pts) > 1 and not np.allclose(pts[0], pts[-1]):
        pts = np.vstack([pts, pts[:1]])
    if len(pts) < 2:
        return [pts] if len(pts) == 1 else []
    total = sum(dashes)
    if total <= 0:
        return [pts]
    pattern = list(dashes) if len(dashes) % 2 == 0 else list(dashes) * 2
    period = sum(pattern)
    phase = phase % period
    i = 0
    while phase >= pattern[i]:
        phase -= pattern[i]
        i = (i + 1) % len(pattern)
    remaining = pattern[i] - phase
    on = i % 2 == 0
    pieces = []
    cur = [tuple(pts[0])] if on else []
    for k in range(len(pts) - 1):
        p0 = pts[k]
        p1 = pts[k + 1]
        seg = float(np.hypot(*(p1 - p0)))
        pos = 0.0
        while seg - pos > remaining:
            pos += remaining
            t = pos / seg
            q = (p0[0] + (p1[0] - p0[0]) * t, p0[1] + (p1[1] - p0[1]) * t)
            if on:
                cur.append(q)
                pieces.append(np.asarray(cur))
                cur = []
            else:
                cur = [q]
            on = not on
            i = (i + 1) % len(pattern)
            remaining = pattern[i]
        remaining -= seg - pos
        if on:
            cur.append((p1[0], p1[1]))
    if on and cur:
        pieces.append(np.asarray(cur))
    return pieces


# ---------------------------------------------------------------------------
# Clipper helpers


def to_int(pts: np.ndarray, s: float):
    return np.rint(pts * s).astype(np.int64).tolist()


def _valid(paths):
    return [p for p in paths if len(p) >= 3]


def simplify(paths_int, rule: str):
    """Resolve the fill rule: returns simple non-overlapping polygons (int)."""
    paths_int = _valid(paths_int)
    if not paths_int:
        return []
    pc = pyclipper.Pyclipper()
    try:
        pc.AddPaths(paths_int, pyclipper.PT_SUBJECT, True)
    except pyclipper.ClipperException:
        return []
    ft = FILL_TYPES[rule]
    return pc.Execute(pyclipper.CT_UNION, ft, ft)


def intersect_tree(subject_int, rule: str, clip_int=None):
    """Subject (with fill rule) intersected with an already simplified clip set -> PolyTree."""
    subject_int = _valid(subject_int)
    if not subject_int:
        return None
    pc = pyclipper.Pyclipper()
    try:
        pc.AddPaths(subject_int, pyclipper.PT_SUBJECT, True)
        if clip_int is not None:
            if not clip_int:
                return None
            pc.AddPaths(clip_int, pyclipper.PT_CLIP, True)
    except pyclipper.ClipperException:
        return None
    ft = FILL_TYPES[rule]
    if clip_int is None:
        return pc.Execute2(pyclipper.CT_UNION, ft, ft)
    return pc.Execute2(pyclipper.CT_INTERSECTION, ft, pyclipper.PFT_NONZERO)


def intersect_paths(subject_int, rule, clip_int):
    subject_int = _valid(subject_int)
    if not subject_int or not clip_int:
        return []
    pc = pyclipper.Pyclipper()
    try:
        pc.AddPaths(subject_int, pyclipper.PT_SUBJECT, True)
        pc.AddPaths(clip_int, pyclipper.PT_CLIP, True)
    except pyclipper.ClipperException:
        return []
    return pc.Execute(pyclipper.CT_INTERSECTION, FILL_TYPES[rule], pyclipper.PFT_NONZERO)


def boolean(subject_int, clip_int, op):
    subject_int = _valid(subject_int)
    clip_int = _valid(clip_int)
    pc = pyclipper.Pyclipper()
    try:
        if subject_int:
            pc.AddPaths(subject_int, pyclipper.PT_SUBJECT, True)
        if clip_int:
            pc.AddPaths(clip_int, pyclipper.PT_CLIP, True)
    except pyclipper.ClipperException:
        return []
    if not subject_int and not clip_int:
        return []
    return pc.Execute(op, pyclipper.PFT_NONZERO, pyclipper.PFT_NONZERO)


def tree_to_tris(tree, s: float):
    """Triangulate a PolyTree (outer contours with holes) -> (verts float (n,2), indices)."""
    if tree is None:
        return EMPTY
    vs = []
    ix = []
    base = 0
    stack = list(tree.Childs)
    while stack:
        node = stack.pop()
        if len(node.Contour) < 3:
            continue
        rings = [node.Contour] + [h.Contour for h in node.Childs if len(h.Contour) >= 3]
        for h in node.Childs:
            stack.extend(h.Childs)
        arr = np.asarray([p for r in rings for p in r], dtype=np.float64) / s
        ends = np.cumsum([len(r) for r in rings]).astype(np.uint32)
        tri = earcut.triangulate_float64(arr, ends)
        if len(tri):
            vs.append(arr)
            ix.append(tri.astype(np.uint32) + base)
            base += len(arr)
    if not vs:
        return EMPTY
    return np.vstack(vs), np.concatenate(ix)


def polys_to_tris(polys_int, s: float):
    """Triangulate simple polygons produced by Clipper (outer + hole orientation)."""
    if not polys_int:
        return EMPTY
    pc = pyclipper.Pyclipper()
    try:
        pc.AddPaths(_valid(polys_int), pyclipper.PT_SUBJECT, True)
    except pyclipper.ClipperException:
        return EMPTY
    tree = pc.Execute2(pyclipper.CT_UNION, pyclipper.PFT_NONZERO, pyclipper.PFT_NONZERO)
    return tree_to_tris(tree, s)


def bbox_of_int(polys_int, s=PAGE_SCALE):
    if not polys_int:
        return None
    arr = np.asarray([p for poly in polys_int for p in poly], dtype=np.float64) / s
    return (float(arr[:, 0].min()), float(arr[:, 1].min()), float(arr[:, 0].max()), float(arr[:, 1].max()))


# ---------------------------------------------------------------------------
# Stroking


def stroke_outline(cmds, ctm, style, tol_page: float, min_width_page: float):
    """Stroke path `cmds` (user space) -> simple polygons in page space (int, PAGE_SCALE).

    The stroke is offset in user space and then transformed, so non-uniform
    CTMs produce the correct (elliptical) pen shape.
    """
    s = max(max_scale(ctm), 1e-9)
    tol_user = tol_page / s
    width = style.width
    det = abs(ctm[0] * ctm[3] - ctm[1] * ctm[2])
    mean = math.sqrt(det) if det > 0 else s
    if width * mean < min_width_page:
        width = min_width_page / max(mean, 1e-9)
    subs = flatten(cmds, IDENTITY, tol_user)
    if style.dash and sum(style.dash) > 0:
        pieces = []
        for pts, closed in subs:
            pieces.extend((p, False) for p in dash_polyline(pts, closed, style.dash, style.dash_phase))
        subs = pieces
    su = PAGE_SCALE * s  # int units per user unit (>= 1/1000 pt resolution in page space)
    co = pyclipper.PyclipperOffset(max(2.0, style.miter_limit), max(1.0, tol_page * PAGE_SCALE))
    join = JOINS.get(style.join, pyclipper.JT_MITER)
    added = False
    for pts, closed in subs:
        ip = to_int(pts, su)
        # drop consecutive duplicates
        dedup = [ip[0]] if ip else []
        for p in ip[1:]:
            if p != dedup[-1]:
                dedup.append(p)
        if not dedup:
            continue
        if closed and len(dedup) >= 3:
            if dedup[0] == dedup[-1]:
                dedup = dedup[:-1]
            co.AddPath(dedup, join, pyclipper.ET_CLOSEDLINE)
            added = True
        elif len(dedup) == 1 or (closed and len(dedup) < 3):
            if style.cap in (1, 2):
                co.AddPath([dedup[0]], join, CAPS[style.cap])
                added = True
        else:
            co.AddPath(dedup, join, CAPS.get(style.cap, pyclipper.ET_OPENBUTT))
            added = True
    if not added:
        return []
    res = co.Execute(width / 2 * su)
    out = []
    for poly in res:
        arr = np.asarray(poly, dtype=np.float64) / su
        x = ctm[0] * arr[:, 0] + ctm[2] * arr[:, 1] + ctm[4]
        y = ctm[1] * arr[:, 0] + ctm[3] * arr[:, 1] + ctm[5]
        out.append(np.rint(np.stack([x, y], axis=1) * PAGE_SCALE).astype(np.int64).tolist())
    return simplify(out, "nonzero")


def fill_outline(cmds, ctm, rule: str, tol_page: float):
    """Fill path `cmds` (user space) -> int polygons (page space, unsimplified)."""
    subs = flatten(cmds, ctm, tol_page)
    return [to_int(p, PAGE_SCALE) for p, _ in subs if len(p) >= 3]


def transform_points(m, pts: np.ndarray) -> np.ndarray:
    x = m[0] * pts[:, 0] + m[2] * pts[:, 1] + m[4]
    y = m[1] * pts[:, 0] + m[3] * pts[:, 1] + m[5]
    return np.stack([x, y], axis=1)


def circle(cx, cy, r, tol):
    n = max(8, min(720, int(math.ceil(math.pi / math.acos(max(-1.0, 1 - tol / max(r, 1e-9)))))))
    t = np.linspace(0, 2 * math.pi, n, endpoint=False)
    return np.stack([cx + r * np.cos(t), cy + r * np.sin(t)], axis=1)


def bbox_contains(outer, inner) -> bool:
    return outer[0] <= inner[0] and outer[1] <= inner[1] and outer[2] >= inner[2] and outer[3] >= inner[3]


def bbox_intersects(a, b) -> bool:
    return a[0] < b[2] and b[0] < a[2] and a[1] < b[3] and b[1] < a[3]


def bbox_intersection(a, b):
    r = (max(a[0], b[0]), max(a[1], b[1]), min(a[2], b[2]), min(a[3], b[3]))
    return r if r[0] < r[2] and r[1] < r[3] else None


def apply_pt(m, p):
    return apply(m, p[0], p[1])
