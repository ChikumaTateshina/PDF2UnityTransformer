"""Image XObjects / inline images -> RGBA PNG resources (FR-06, SPEC §5.3).

Decoding: generic filters through qpdf (`read_bytes`), DCT/JPX through
Pillow, CCITT/JBIG2 through pikepdf.PdfImage. Colour conversion, Decode arrays,
stencil masks, explicit masks, colour-key masks, SMask (incl. Matte) are
applied here so the IR only contains display-ready RGBA pixels.
"""

from __future__ import annotations

import io
import os

import numpy as np
import pikepdf
from PIL import Image

from ..diagnostics import Unsupported
from ..ir.model import ImageResource
from .colorspace import DeviceGray, Indexed
from .pdfutil import num_list, objid, objkey, sha256

Image.MAX_IMAGE_PIXELS = None  # limits are enforced by the compiler budget instead


def _filters(stream):
    f = stream.get("/Filter")
    if f is None:
        return []
    if isinstance(f, pikepdf.Array):
        return [str(x) for x in f]
    return [str(f)]


def _unpack(data: bytes, w: int, h: int, n: int, bpc: int) -> np.ndarray:
    """Unpack packed samples into an int array (h, w, n)."""
    row_bits = w * n * bpc
    row_bytes = (row_bits + 7) // 8
    need = row_bytes * h
    if len(data) < need:
        data = data + bytes(need - len(data))
    buf = np.frombuffer(data[:need], dtype=np.uint8).reshape(h, row_bytes)
    if bpc == 8:
        out = buf[:, : w * n]
    elif bpc == 16:
        out = buf[:, : w * n * 2].reshape(h, w * n, 2)
        out = (out[..., 0].astype(np.uint16) << 8) | out[..., 1]
    elif bpc in (1, 2, 4):
        bits = np.unpackbits(buf, axis=1)[:, :row_bits]
        if bpc == 1:
            out = bits
        else:
            bits = bits.reshape(h, w * n, bpc)
            weights = (1 << np.arange(bpc - 1, -1, -1)).astype(np.uint8)
            out = (bits * weights).sum(axis=2)
    else:
        raise Unsupported("image.bpc", f"BitsPerComponent {bpc} not supported")
    return out.reshape(h, w, n).astype(np.int32)


def _apply_decode(samples: np.ndarray, decode, bpc: int) -> np.ndarray:
    maxv = float((1 << bpc) - 1)
    n = samples.shape[2]
    out = np.empty(samples.shape, dtype=np.float32)
    for i in range(n):
        dmin, dmax = decode[2 * i], decode[2 * i + 1]
        out[..., i] = dmin + samples[..., i].astype(np.float32) * ((dmax - dmin) / maxv)
    return out


class ImageDecoder:
    def __init__(self, ctx):
        self.ctx = ctx
        self.cache = {}
        self.out_dir = os.path.join(ctx.package_dir, "resources", "images")
        os.makedirs(self.out_dir, exist_ok=True)

    # ------------------------------------------------------------------
    def inline_to_stream(self, iimage) -> pikepdf.Stream:
        d = iimage.obj
        return pikepdf.Stream(self.ctx.pdf, bytes(iimage._data), d)

    def load(self, stream, resources, source: str) -> ImageResource:
        key = objkey(stream)
        if key[0] != "direct" and key in self.cache:
            return self.cache[key]
        res = self._decode_to_resource(stream, resources, source)
        if key[0] != "direct":
            self.cache[key] = res
        return res

    def _decode_to_resource(self, stream, resources, source) -> ImageResource:
        rgba, stencil, cs_name = self.decode_rgba(stream, resources)
        h, w = rgba.shape[:2]
        a = rgba[..., 3]
        has_alpha = bool((a < 255).any())
        soft_alpha = bool(((a > 0) & (a < 255)).mean() > 0.0005) and not stencil
        digest = sha256(f"{w}x{h}:{int(stencil)}:".encode() + rgba.tobytes())
        ref = "i-" + digest[:24]
        existing = self.ctx.ir.images.get(ref)
        if existing is not None:
            return existing
        rel = f"resources/images/{ref}.png"
        path = os.path.join(self.ctx.package_dir, rel)
        if not os.path.exists(path):
            Image.fromarray(rgba, "RGBA").save(path, format="PNG", compress_level=6)
        res = ImageResource(ref=ref, width=w, height=h, has_alpha=has_alpha, stencil=stencil,
                            colorspace=cs_name, file=rel, source=source, soft_alpha=soft_alpha)
        self.ctx.ir.images[ref] = res
        return res

    # ------------------------------------------------------------------
    def decode_rgba(self, stream, resources):
        """Return (rgba uint8 (h, w, 4), is_stencil, colourspace description)."""
        w = int(stream.get("/Width", 0))
        h = int(stream.get("/Height", 0))
        if w <= 0 or h <= 0:
            raise Unsupported("image.size", "image with zero size")
        if w * h > self.ctx.limits.max_image_pixels:
            raise Unsupported("image.too-large", f"image {w}x{h} exceeds pixel limit")
        stencil = bool(stream.get("/ImageMask", False))
        if stencil:
            alpha = self._stencil_alpha(stream, w, h)
            rgba = np.full((h, w, 4), 255, dtype=np.uint8)
            rgba[..., 3] = alpha
            return rgba, True, "stencil"

        rgb, raw_ints, cs = self._decode_color(stream, resources, w, h)
        alpha = None
        smask = stream.get("/SMask")
        mask = stream.get("/Mask")
        if isinstance(smask, pikepdf.Stream):
            alpha, matte = self._smask(smask, w, h)
            if matte is not None:
                rgb = self._unmatte(rgb, alpha, matte, cs)
        elif self._jpx_alpha is not None:
            alpha = self._jpx_alpha
        elif isinstance(mask, pikepdf.Stream):
            mw = int(mask.get("/Width", w))
            mh = int(mask.get("/Height", h))
            alpha = self._stencil_alpha(mask, mw, mh)
            if (mw, mh) != (w, h):
                alpha = np.asarray(Image.fromarray(alpha, "L").resize((w, h), Image.NEAREST))
        elif isinstance(mask, pikepdf.Array) and raw_ints is not None:
            ranges = [int(v) for v in mask]
            hit = np.ones((h, w), dtype=bool)
            for i in range(raw_ints.shape[2]):
                lo, hi = ranges[2 * i], ranges[2 * i + 1]
                hit &= (raw_ints[..., i] >= lo) & (raw_ints[..., i] <= hi)
            alpha = np.where(hit, 0, 255).astype(np.uint8)
        if alpha is None:
            alpha = np.full((h, w), 255, dtype=np.uint8)
        rgba = np.concatenate([rgb, alpha[..., None]], axis=2)
        return np.ascontiguousarray(rgba), False, cs.describe() if cs is not None else "JPX"

    def _read(self, stream):
        data = stream.read_bytes()
        self.ctx.budget.account_decoded(len(data))
        return data

    def _decode_color(self, stream, resources, w, h):
        self._jpx_alpha = None
        filters = _filters(stream)
        cs_obj = stream.get("/ColorSpace")
        bpc = int(stream.get("/BitsPerComponent", 8))
        cs = self.ctx.colorspaces.resolve(cs_obj, resources) if cs_obj is not None else None
        decode = num_list(stream.get("/Decode")) or None
        last = filters[-1] if filters else None

        if last in ("/DCTDecode", "/DCT", "/JPXDecode"):
            if len(filters) > 1:
                return self._decode_with_pdfimage(stream, cs, w, h)
            raw = bytes(stream.read_raw_bytes())
            self.ctx.budget.account_decoded(len(raw))
            im = Image.open(io.BytesIO(raw))
            im.load()
            if last == "/JPXDecode":
                if im.mode in ("RGBA", "LA") and int(stream.get("/SMaskInData", 0)) != 0:
                    self._jpx_alpha = np.asarray(im.getchannel("A"))
                if cs is None:
                    im = im.convert("RGB")
                    return np.asarray(im), None, None
                if im.mode in ("RGBA", "LA"):
                    im = im.convert("RGB" if im.mode == "RGBA" else "L")
            arr = np.asarray(im)
            if arr.ndim == 2:
                arr = arr[..., None]
            if (arr.shape[1], arr.shape[0]) != (w, h):
                self.ctx.diag.info("image.size-mismatch", "JPEG/JPX dimensions differ from the image dictionary")
            if cs is None or cs.n != arr.shape[2]:
                cs = {1: self.ctx.colorspaces.gray, 3: self.ctx.colorspaces.rgb,
                      4: self.ctx.colorspaces.cmyk}.get(arr.shape[2], cs)
            ints = arr.astype(np.int32)
            dec = decode or cs.default_decode(8)
            samples = _apply_decode(ints, dec, 8)
            return cs.convert_array(samples), ints, cs

        if last in ("/CCITTFaxDecode", "/CCF", "/JBIG2Decode"):
            return self._decode_with_pdfimage(stream, cs, w, h)

        if cs is None:
            raise Unsupported("image.colorspace", "image without ColorSpace")
        data = self._read(stream)
        ints = _unpack(data, w, h, cs.n, bpc)
        dec = decode or cs.default_decode(bpc)
        if isinstance(cs, Indexed):
            samples = _apply_decode(ints, dec, bpc)  # index values (Decode maps to 0..hival)
        else:
            samples = _apply_decode(ints, dec, bpc)
        return cs.convert_array(samples), ints, cs

    def _decode_with_pdfimage(self, stream, cs, w, h):
        try:
            im = pikepdf.PdfImage(stream).as_pil_image()
        except Exception as e:
            raise Unsupported("image.codec", f"image codec unavailable: {e}")
        if im.mode == "1":
            im = im.convert("L")
        if im.mode in ("L", "RGB"):
            arr = np.asarray(im)
            if arr.ndim == 2:
                arr = np.stack([arr] * 3, axis=-1)
            return arr, None, cs or DeviceGray()
        if im.mode == "CMYK":
            arr = np.asarray(im).astype(np.float32) / 255.0
            return self.ctx.colorspaces.cmyk.convert_array(arr), None, self.ctx.colorspaces.cmyk
        return np.asarray(im.convert("RGB")), None, cs

    def _stencil_alpha(self, stream, w, h):
        filters = _filters(stream)
        decode = num_list(stream.get("/Decode")) or [0.0, 1.0]
        invert = decode[0] > decode[1]
        if filters and filters[-1] in ("/CCITTFaxDecode", "/CCF", "/JBIG2Decode", "/DCTDecode"):
            try:
                im = pikepdf.PdfImage(stream).as_pil_image().convert("L")
            except Exception as e:
                raise Unsupported("image.codec", f"mask codec unavailable: {e}")
            bits = (np.asarray(im) >= 128).astype(np.int32)
        else:
            data = self._read(stream)
            bits = _unpack(data, w, h, 1, 1)[..., 0]
        painted = bits == (1 if invert else 0)
        return np.where(painted, 255, 0).astype(np.uint8)

    def _smask(self, smask, w, h):
        sw = int(smask.get("/Width", w))
        sh = int(smask.get("/Height", h))
        bpc = int(smask.get("/BitsPerComponent", 8))
        filters = _filters(smask)
        decode = num_list(smask.get("/Decode")) or [0.0, 1.0]
        if filters and filters[-1] in ("/DCTDecode", "/JPXDecode", "/CCITTFaxDecode", "/JBIG2Decode"):
            try:
                if filters[-1] in ("/DCTDecode", "/JPXDecode") and len(filters) == 1:
                    im = Image.open(io.BytesIO(bytes(smask.read_raw_bytes())))
                else:
                    im = pikepdf.PdfImage(smask).as_pil_image()
                g = np.asarray(im.convert("L")).astype(np.float32) / 255.0
            except Exception as e:
                raise Unsupported("image.codec", f"soft mask codec unavailable: {e}")
            g = decode[0] + g * (decode[1] - decode[0])
        else:
            data = self._read(smask)
            g = _apply_decode(_unpack(data, sw, sh, 1, bpc), decode, bpc)[..., 0]
        a = (np.clip(g, 0, 1) * 255 + 0.5).astype(np.uint8)
        if a.shape != (h, w):
            a = np.asarray(Image.fromarray(a, "L").resize((w, h), Image.BILINEAR))
        matte = num_list(smask.get("/Matte")) or None
        return a, matte

    def _unmatte(self, rgb, alpha, matte, cs):
        """Undo pre-multiplication against the Matte colour (PDF 11.6.5.3)."""
        if cs is None:
            return rgb
        m = np.array(cs.to_rgb(matte), dtype=np.float32) * 255
        a = alpha.astype(np.float32)[..., None] / 255.0
        c = rgb.astype(np.float32)
        with np.errstate(divide="ignore", invalid="ignore"):
            out = np.where(a > 0, m + (c - m) / np.maximum(a, 1e-6), 0)
        return np.clip(out + 0.5, 0, 255).astype(np.uint8)


def image_source(stream) -> str:
    return objid(stream)
