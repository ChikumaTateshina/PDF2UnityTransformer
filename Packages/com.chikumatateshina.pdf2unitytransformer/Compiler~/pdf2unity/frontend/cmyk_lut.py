"""DeviceCMYK -> sRGB lookup table matching the reference renderer.

Without a user supplied CMYK ICC profile, DeviceCMYK has no defined
appearance. To stay consistent with the PDFium reference images (SPEC §5.3:
compare under identical colour management), a 17^4 grid of CMYK patches is
rendered once by PDFium and cached per PDFium version; colours are then
interpolated quadrilinearly. Falls back to the naive formula if this fails.
"""

from __future__ import annotations

import os
import tempfile

import numpy as np

N = 17


def _cache_path():
    import pypdfium2
    ver = getattr(pypdfium2, "V_PDFIUM", None) or getattr(pypdfium2, "PDFIUM_INFO", "unknown")
    base = os.environ.get("PDF2UNITY_CACHE") or os.path.join(tempfile.gettempdir(), "pdf2unity-cache")
    os.makedirs(base, exist_ok=True)
    return os.path.join(base, f"cmyk-lut-{str(ver).replace(' ', '_')[:40]}.npy")


def _build() -> np.ndarray:
    import pikepdf
    import zlib
    from ..precompose.pdfium_renderer import PdfiumRenderer

    side = N * N  # 289 x 289 patches of 1pt
    parts = []
    for c in range(N):
        for m in range(N):
            for y in range(N):
                for k in range(N):
                    i = ((c * N + m) * N + y) * N + k
                    px, py = i % side, i // side
                    parts.append(f"{c / (N - 1):.4f} {m / (N - 1):.4f} {y / (N - 1):.4f} {k / (N - 1):.4f} k "
                                 f"{px} {side - 1 - py} 1 1 re f")
    pdf = pikepdf.new()
    pdf.add_blank_page(page_size=(side, side))
    pdf.pages[0].obj.Contents = pikepdf.Stream(pdf, zlib.compress("\n".join(parts).encode()),
                                               Filter=pikepdf.Name.FlateDecode)
    fd, path = tempfile.mkstemp(suffix=".pdf")
    os.close(fd)
    try:
        pdf.save(path)
        r = PdfiumRenderer(path)
        # Sample patch centres at 4x so anti-aliasing does not mix neighbours.
        img = np.asarray(r.render_region(0, (0, 0, side, side), 288.0)).astype(np.float32)
        r.close()
    finally:
        os.remove(path)
    ys, xs = np.divmod(np.arange(N ** 4), side)
    rgb = img[ys * 4 + 2, xs * 4 + 2] / 255.0
    return rgb.reshape(N, N, N, N, 3)


_LUT = None


def get_lut():
    global _LUT
    if _LUT is None:
        path = _cache_path()
        try:
            _LUT = np.load(path)
        except (OSError, ValueError):
            _LUT = _build()
            try:
                np.save(path, _LUT)
            except OSError:
                pass
    return _LUT


def lookup(lut: np.ndarray, cmyk: np.ndarray) -> np.ndarray:
    """cmyk float (n, 4) in 0..1 -> rgb float (n, 3), quadrilinear interpolation."""
    f = np.clip(cmyk, 0, 1) * (N - 1)
    i0 = np.minimum(np.floor(f).astype(np.int64), N - 2)
    t = f - i0
    out = np.zeros((len(cmyk), 3), dtype=np.float64)
    for corner in range(16):
        bits = [(corner >> d) & 1 for d in range(4)]
        w = np.ones(len(cmyk))
        idx = []
        for d in range(4):
            w = w * (t[:, d] if bits[d] else 1 - t[:, d])
            idx.append(i0[:, d] + bits[d])
        out += w[:, None] * lut[idx[0], idx[1], idx[2], idx[3]]
    return out
