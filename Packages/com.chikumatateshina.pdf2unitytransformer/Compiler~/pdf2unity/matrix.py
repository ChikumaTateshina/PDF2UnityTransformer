"""2D affine matrices in PDF convention: [a b c d e f], row vector * M."""

from __future__ import annotations

import math

Matrix = tuple  # (a, b, c, d, e, f)

IDENTITY: Matrix = (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)


def mul(m1: Matrix, m2: Matrix) -> Matrix:
    """Return m1 x m2 (apply m1 first, then m2)."""
    a1, b1, c1, d1, e1, f1 = m1
    a2, b2, c2, d2, e2, f2 = m2
    return (
        a1 * a2 + b1 * c2,
        a1 * b2 + b1 * d2,
        c1 * a2 + d1 * c2,
        c1 * b2 + d1 * d2,
        e1 * a2 + f1 * c2 + e2,
        e1 * b2 + f1 * d2 + f2,
    )


def apply(m: Matrix, x: float, y: float) -> tuple[float, float]:
    return (m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5])


def det(m: Matrix) -> float:
    return m[0] * m[3] - m[1] * m[2]


def invert(m: Matrix) -> Matrix:
    d = det(m)
    if abs(d) < 1e-300:
        raise ZeroDivisionError("singular matrix")
    a, b, c, dd, e, f = m
    ia, ib, ic, id_ = dd / d, -b / d, -c / d, a / d
    return (ia, ib, ic, id_, -(e * ia + f * ic), -(e * ib + f * id_))


def translate(tx: float, ty: float) -> Matrix:
    return (1.0, 0.0, 0.0, 1.0, tx, ty)


def scale(sx: float, sy: float) -> Matrix:
    return (sx, 0.0, 0.0, sy, 0.0, 0.0)


def mean_scale(m: Matrix) -> float:
    """Geometric mean scale factor sqrt(|det|)."""
    return math.sqrt(abs(det(m)))


def max_scale(m: Matrix) -> float:
    """Largest singular value: the maximum stretch applied to any direction."""
    a, b, c, d = m[0], m[1], m[2], m[3]
    s1 = a * a + b * b + c * c + d * d
    s2 = math.sqrt(max(0.0, (a * a + b * b - c * c - d * d) ** 2 + 4 * (a * c + b * d) ** 2))
    return math.sqrt(max(0.0, (s1 + s2) / 2))


def is_axis_aligned(m: Matrix, eps: float = 1e-9) -> bool:
    return (abs(m[1]) < eps and abs(m[2]) < eps) or (abs(m[0]) < eps and abs(m[3]) < eps)


def transform_bbox(m: Matrix, bbox) -> tuple[float, float, float, float]:
    x0, y0, x1, y1 = bbox
    pts = [apply(m, x0, y0), apply(m, x1, y0), apply(m, x0, y1), apply(m, x1, y1)]
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    return (min(xs), min(ys), max(xs), max(ys))


def rounded(m: Matrix, nd: int = 6) -> list:
    return [round(v, nd) for v in m]
