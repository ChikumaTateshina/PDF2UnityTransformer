"""PDFium based rasteriser: reference images (FR-13) and region precomposition (FR-09).

PDFium renders from the original drawing commands of the page (no OCR, no
image analysis). Regions are given in *display space* (points, origin at the
top-left of the visible page after CropBox/Rotate), which is exactly PDFium's
page coordinate system for FPDF_RenderPageBitmapWithMatrix.
Annotations and form widgets are not drawn (out of scope).
"""

from __future__ import annotations

import ctypes
import math

import numpy as np
import pypdfium2 as pdfium
import pypdfium2.raw as pdfium_c
from PIL import Image


class PdfiumRenderer:
    def __init__(self, path: str):
        self.doc = pdfium.PdfDocument(path)
        self._pages = {}

    def close(self):
        for p in self._pages.values():
            p.close()
        self.doc.close()

    def page(self, index):
        p = self._pages.get(index)
        if p is None:
            p = self.doc[index]
            self._pages[index] = p
        return p

    def page_size(self, index):
        """Display size in points (rotation applied), as PDFium sees it."""
        p = self.page(index)
        return float(p.get_width()), float(p.get_height())

    def render_region(self, index: int, rect, dpi: float) -> Image.Image:
        x0, y0, x1, y1 = rect
        s = dpi / 72.0
        w = max(1, int(math.ceil((x1 - x0) * s)))
        h = max(1, int(math.ceil((y1 - y0) * s)))
        bitmap = pdfium.PdfBitmap.new_native(w, h, pdfium_c.FPDFBitmap_BGR)
        bitmap.fill_rect((255, 255, 255, 255), 0, 0, w, h)
        matrix = pdfium_c.FS_MATRIX(s, 0.0, 0.0, s, -x0 * s, -y0 * s)
        clip = pdfium_c.FS_RECTF(0.0, 0.0, float(w), float(h))
        page = self.page(index)
        pdfium_c.FPDF_RenderPageBitmapWithMatrix(bitmap.raw, page.raw, ctypes.byref(matrix), ctypes.byref(clip), 0)
        arr = bitmap.to_numpy()
        img = Image.fromarray(np.ascontiguousarray(arr[..., ::-1]), "RGB")
        bitmap.close()
        return img

    def render_page(self, index: int, dpi: float, max_px: int = 8192) -> Image.Image:
        w, h = self.page_size(index)
        dpi = min(dpi, max_px * 72.0 / max(w, h, 1e-9))
        return self.render_region(index, (0.0, 0.0, w, h), dpi)
