"""Colour spaces -> display sRGB (FR-08, SPEC §5.3).

Every colour space can convert a single colour (`to_rgb`) and an image sample
array (`convert_array`). ICC profiles go through LittleCMS (Pillow ImageCms).
DeviceCMYK uses an optional user supplied CMYK profile, otherwise the PDF
reference's naive conversion (reported as a warning once per document).
"""

from __future__ import annotations

import io
import math

import numpy as np
import pikepdf

from ..diagnostics import Unsupported
from .functions import load_function
from .pdfutil import num_list, sha256

try:
    from PIL import Image, ImageCms

    _SRGB = ImageCms.createProfile("sRGB")
except Exception:  # pragma: no cover - Pillow without LittleCMS
    ImageCms = None
    _SRGB = None


def _clamp01(v):
    return 0.0 if v < 0 else 1.0 if v > 1 else v


class ColorSpace:
    family = "?"
    n = 1

    def to_rgb(self, comps):  # -> (r, g, b) in 0..1
        raise NotImplementedError

    def initial_color(self):
        return [0.0] * self.n

    def default_decode(self, bpc):
        return [0.0, 1.0] * self.n

    def convert_array(self, arr: np.ndarray) -> np.ndarray:
        """arr: float32 (H, W, n) already Decode-mapped. Returns uint8 (H, W, 3)."""
        h, w, _ = arr.shape
        flat = arr.reshape(-1, self.n)
        uniq, inv = np.unique(np.round(flat, 4), axis=0, return_inverse=True)
        if len(uniq) > 65536:
            raise Unsupported("color.image", f"{self.family} image with too many distinct colours")
        lut = np.array([self.to_rgb(list(u)) for u in uniq], dtype=np.float32)
        return (np.clip(lut[inv.reshape(-1)], 0, 1) * 255 + 0.5).astype(np.uint8).reshape(h, w, 3)

    def describe(self):
        return self.family


class DeviceGray(ColorSpace):
    family = "DeviceGray"
    n = 1

    def to_rgb(self, c):
        g = _clamp01(c[0])
        return (g, g, g)

    def convert_array(self, arr):
        g = (np.clip(arr[..., 0], 0, 1) * 255 + 0.5).astype(np.uint8)
        return np.stack([g, g, g], axis=-1)


class DeviceRGB(ColorSpace):
    family = "DeviceRGB"
    n = 3

    def to_rgb(self, c):
        return (_clamp01(c[0]), _clamp01(c[1]), _clamp01(c[2]))

    def convert_array(self, arr):
        return (np.clip(arr[..., :3], 0, 1) * 255 + 0.5).astype(np.uint8)


class _IccTransform:
    """Cached LittleCMS transform from a source profile to sRGB."""

    _cache: dict = {}

    @classmethod
    def get(cls, profile_bytes: bytes, mode: str):
        key = (sha256(profile_bytes), mode)
        if key not in cls._cache:
            if ImageCms is None:
                cls._cache[key] = None
            else:
                try:
                    src = ImageCms.ImageCmsProfile(io.BytesIO(profile_bytes))
                    cls._cache[key] = ImageCms.buildTransform(
                        src, _SRGB, mode, "RGB", renderingIntent=ImageCms.Intent.RELATIVE_COLORIMETRIC
                        if hasattr(ImageCms, "Intent") else 1)
                except Exception:
                    cls._cache[key] = None
        return cls._cache[key]


def _icc_convert(transform, mode, arr_u8):
    im = Image.fromarray(arr_u8 if mode != "L" else arr_u8[..., 0], mode=mode)
    out = ImageCms.applyTransform(im, transform)
    return np.asarray(out.convert("RGB"))


class DeviceCMYK(ColorSpace):
    family = "DeviceCMYK"
    n = 4

    def __init__(self, profile: bytes | None = None, lut=None):
        self.profile = profile
        self.transform = _IccTransform.get(profile, "CMYK") if profile else None
        self.lut = lut if self.transform is None else None
        self._single = {}

    @property
    def method(self):
        return "icc" if self.transform is not None else "reference-lut" if self.lut is not None else "naive"

    def to_rgb(self, c):
        c = [_clamp01(v) for v in c[:4]]
        if self.transform is not None:
            key = tuple(round(v, 4) for v in c)
            if key not in self._single:
                px = np.array([[[int(v * 255 + 0.5) for v in key]]], dtype=np.uint8)
                rgb = _icc_convert(self.transform, "CMYK", px)[0, 0]
                self._single[key] = tuple(float(v) / 255 for v in rgb)
            return self._single[key]
        if self.lut is not None:
            from .cmyk_lut import lookup
            return tuple(float(v) for v in lookup(self.lut, np.array([c], dtype=np.float64))[0])
        cc, m, y, k = c
        return ((1 - cc) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k))

    def convert_array(self, arr):
        u8 = (np.clip(arr[..., :4], 0, 1) * 255 + 0.5).astype(np.uint8)
        if self.transform is not None:
            return _icc_convert(self.transform, "CMYK", u8)
        if self.lut is not None:
            from .cmyk_lut import lookup
            h, w = arr.shape[:2]
            flat = u8.reshape(-1, 4)
            uniq, inv = np.unique(flat, axis=0, return_inverse=True)
            rgb = lookup(self.lut, uniq.astype(np.float64) / 255.0)[inv.reshape(-1)]
            return (np.clip(rgb, 0, 1) * 255 + 0.5).astype(np.uint8).reshape(h, w, 3)
        f = arr[..., :4].clip(0, 1)
        k = 1 - f[..., 3:4]
        rgb = (1 - f[..., :3]) * k
        return (rgb * 255 + 0.5).astype(np.uint8)


class ICCBased(ColorSpace):
    family = "ICCBased"

    def __init__(self, profile: bytes, n: int, alternate: ColorSpace, ranges):
        self.profile = profile
        self.n = n
        self.alternate = alternate
        self.ranges = ranges or [0.0, 1.0] * n
        mode = {1: "L", 3: "RGB", 4: "CMYK"}.get(n)
        self.mode = mode
        self.transform = _IccTransform.get(profile, mode) if mode else None
        self.hash = sha256(profile)[:16]
        self._single = {}

    def default_decode(self, bpc):
        return list(self.ranges)

    def describe(self):
        return f"ICCBased(N={self.n},{self.hash})" + ("" if self.transform else "->alternate")

    def to_rgb(self, c):
        if self.transform is None or self.n not in (1, 3, 4):
            return self.alternate.to_rgb(c)
        key = tuple(round(_clamp01(v), 4) for v in c[: self.n])
        if key not in self._single:
            px = np.array([[[int(v * 255 + 0.5) for v in key]]], dtype=np.uint8)
            rgb = _icc_convert(self.transform, self.mode, px)[0, 0]
            self._single[key] = tuple(float(v) / 255 for v in rgb)
        return self._single[key]

    def convert_array(self, arr):
        if self.transform is None or self.n not in (1, 3, 4):
            return self.alternate.convert_array(arr)
        u8 = (np.clip(arr[..., : self.n], 0, 1) * 255 + 0.5).astype(np.uint8)
        return _icc_convert(self.transform, self.mode, u8)


class Indexed(ColorSpace):
    family = "Indexed"
    n = 1

    def __init__(self, base: ColorSpace, hival: int, lookup: bytes):
        self.base = base
        self.hival = max(0, min(255, hival))
        need = (self.hival + 1) * base.n
        lookup = lookup[:need] + bytes(max(0, need - len(lookup)))
        comps = np.frombuffer(lookup, dtype=np.uint8).reshape(self.hival + 1, base.n).astype(np.float32) / 255.0
        if isinstance(base, Lab):
            comps = comps * np.array(base.lookup_scale(), dtype=np.float32) + np.array(base.lookup_offset(), np.float32)
        self.palette = base.convert_array(comps.reshape(1, -1, base.n)).reshape(-1, 3)

    def default_decode(self, bpc):
        return [0.0, float((1 << bpc) - 1)]

    def initial_color(self):
        return [0.0]

    def to_rgb(self, c):
        i = int(max(0, min(self.hival, round(c[0]))))
        return tuple(float(v) / 255 for v in self.palette[i])

    def convert_array(self, arr):
        idx = np.clip(np.round(arr[..., 0]), 0, self.hival).astype(np.int32)
        return self.palette[idx]

    def describe(self):
        return f"Indexed({self.base.describe()})"


class TintTransformSpace(ColorSpace):
    """Separation / DeviceN: components -> tint transform -> alternate space."""

    def __init__(self, family, names, alternate, fn):
        self.family = family
        self.names = names
        self.n = len(names)
        self.alternate = alternate
        self.fn = fn
        self.is_none = all(nm == "/None" for nm in names)
        self._cache = {}

    def initial_color(self):
        return [1.0] * self.n

    def to_rgb(self, c):
        key = tuple(round(v, 5) for v in c[: self.n])
        if key not in self._cache:
            alt = self.fn(list(key))
            self._cache[key] = self.alternate.to_rgb(alt)
        return self._cache[key]

    def convert_array(self, arr):
        if self.n == 1:
            # 1-D LUT over 1024 tint values is exact enough for 8/16 bit samples.
            lut = np.array([self.to_rgb([i / 1023.0]) for i in range(1024)], dtype=np.float32)
            idx = np.clip(np.round(arr[..., 0] * 1023), 0, 1023).astype(np.int32)
            return (lut[idx] * 255 + 0.5).astype(np.uint8)
        return super().convert_array(arr)

    def describe(self):
        return f"{self.family}({','.join(self.names)}->{self.alternate.describe()})"


class Lab(ColorSpace):
    family = "Lab"
    n = 3

    def __init__(self, white, rng):
        self.white = white or [0.9505, 1.0, 1.089]
        self.rng = rng or [-100.0, 100.0, -100.0, 100.0]

    def default_decode(self, bpc):
        return [0.0, 100.0] + list(self.rng)

    def lookup_scale(self):
        return [100.0, self.rng[1] - self.rng[0], self.rng[3] - self.rng[2]]

    def lookup_offset(self):
        return [0.0, self.rng[0], self.rng[2]]

    def to_rgb(self, c):
        L, a, b = c[0], c[1], c[2]
        fy = (L + 16) / 116
        fx = fy + a / 500
        fz = fy - b / 200

        def g(t):
            return t ** 3 if t > 6 / 29 else 3 * (6 / 29) ** 2 * (t - 4 / 29)

        X, Y, Z = self.white[0] * g(fx), self.white[1] * g(fy), self.white[2] * g(fz)
        # Normalise the source white to D50 and use the Bradford-adapted D50->sRGB matrix.
        X *= 0.9642 / self.white[0]
        Y *= 1.0 / self.white[1]
        Z *= 0.8249 / self.white[2]
        r = 3.1338561 * X - 1.6168667 * Y - 0.4906146 * Z
        gg = -0.9787684 * X + 1.9161415 * Y + 0.0334540 * Z
        bb = 0.0719453 * X - 0.2289914 * Y + 1.4052427 * Z
        return tuple(_clamp01(_srgb_encode(v)) for v in (r, gg, bb))


def _srgb_encode(v):
    if v <= 0.0031308:
        return 12.92 * max(v, 0.0)
    return 1.055 * v ** (1 / 2.4) - 0.055


class PatternSpace(ColorSpace):
    family = "Pattern"
    n = 1

    def __init__(self, base: ColorSpace | None):
        self.base = base  # underlying space for uncoloured tiling patterns

    def to_rgb(self, c):
        return self.base.to_rgb(c) if self.base else (0.0, 0.0, 0.0)


class ColorSpaceFactory:
    """Resolves colour space objects with caching per indirect object."""

    def __init__(self, ctx):
        self.ctx = ctx
        lut = None
        if not ctx.options.cmyk_profile:
            try:
                from .cmyk_lut import get_lut
                lut = get_lut()
            except Exception as e:  # pragma: no cover - PDFium unavailable
                ctx.diag.warn("color.cmyk-lut", f"reference CMYK table unavailable ({e})")
        self.cmyk = DeviceCMYK(ctx.options.cmyk_profile, lut)
        self.gray = DeviceGray()
        self.rgb = DeviceRGB()
        self._cache = {}

    def device(self, name: str) -> ColorSpace:
        if name in ("/DeviceGray", "/G", "/CalGray"):
            return self.gray
        if name in ("/DeviceRGB", "/RGB", "/CalRGB"):
            return self.rgb
        if name in ("/DeviceCMYK", "/CMYK"):
            if self.cmyk.method == "naive":
                self.ctx.diag.warn("color.cmyk-naive", "DeviceCMYK converted with the naive formula "
                                   "(set a CMYK ICC profile for accurate output)")
            elif self.cmyk.method == "reference-lut":
                self.ctx.diag.info("color.cmyk-reference", "DeviceCMYK converted with the reference "
                                   "renderer's CMYK table (no output intent / ICC profile given)")
            return self.cmyk
        if name == "/Pattern":
            return PatternSpace(None)
        if name in ("/Indexed", "/I"):
            raise Unsupported("color.space", "Indexed without parameters")
        raise Unsupported("color.space", f"unknown colour space {name}")

    def resolve(self, obj, resources=None, depth=0) -> ColorSpace:
        if depth > 8:
            raise Unsupported("color.space", "colour space nesting too deep")
        if isinstance(obj, pikepdf.Name):
            nm = str(obj)
            if nm in ("/DeviceGray", "/DeviceRGB", "/DeviceCMYK", "/Pattern", "/G", "/RGB", "/CMYK",
                      "/CalGray", "/CalRGB"):
                return self.device(nm)
            cs_dict = resources.get("/ColorSpace") if resources is not None else None
            if cs_dict is not None and nm in cs_dict:
                return self.resolve(cs_dict[nm], resources, depth + 1)
            raise Unsupported("color.space", f"undefined colour space {nm}")
        if isinstance(obj, pikepdf.Array):
            try:
                key = obj.objgen if obj.is_indirect else None
            except Exception:
                key = None
            if key and key in self._cache:
                return self._cache[key]
            cs = self._resolve_array(obj, resources, depth)
            if key:
                self._cache[key] = cs
            return cs
        raise Unsupported("color.space", f"invalid colour space object {type(obj).__name__}")

    def _resolve_array(self, arr, resources, depth):
        fam = str(arr[0])
        if fam in ("/DeviceGray", "/DeviceRGB", "/DeviceCMYK", "/Pattern") and len(arr) == 1:
            return self.device(fam)
        if fam == "/CalGray":
            return self.gray
        if fam == "/CalRGB":
            self.ctx.diag.info("color.calrgb", "CalRGB treated as sRGB")
            return self.rgb
        if fam == "/Lab":
            d = arr[1]
            return Lab(num_list(d.get("/WhitePoint")), num_list(d.get("/Range")) or None)
        if fam == "/ICCBased":
            stream = arr[1]
            n = int(stream.get("/N", 3))
            alt_obj = stream.get("/Alternate")
            if alt_obj is not None:
                alt = self.resolve(alt_obj, resources, depth + 1)
            else:
                alt = {1: self.gray, 3: self.rgb, 4: self.cmyk}.get(n, self.rgb)
            data = stream.read_bytes()
            self.ctx.budget.account_decoded(len(data))
            cs = ICCBased(data, n, alt, num_list(stream.get("/Range")) or None)
            if cs.transform is None:
                self.ctx.diag.warn("color.icc-fallback", f"ICC profile {cs.hash} not usable; using alternate "
                                   f"{alt.describe()}")
            return cs
        if fam in ("/Indexed", "/I"):
            base = self.resolve(arr[1], resources, depth + 1)
            hival = int(arr[2])
            lk = arr[3]
            lookup = lk.read_bytes() if isinstance(lk, pikepdf.Stream) else bytes(lk)
            return Indexed(base, hival, lookup)
        if fam == "/Separation":
            nm = str(arr[1])
            alt = self.resolve(arr[2], resources, depth + 1)
            fn = load_function(arr[3], self.ctx.budget)
            if nm not in ("/All", "/None"):
                self.ctx.diag.info("color.spot", f"spot colour {nm} shown via its alternate space")
            return TintTransformSpace("Separation", [nm], alt, fn)
        if fam == "/DeviceN":
            names = [str(v) for v in arr[1]]
            alt = self.resolve(arr[2], resources, depth + 1)
            fn = load_function(arr[3], self.ctx.budget)
            return TintTransformSpace("DeviceN", names, alt, fn)
        if fam == "/Pattern":
            base = self.resolve(arr[1], resources, depth + 1) if len(arr) > 1 else None
            return PatternSpace(base)
        raise Unsupported("color.space", f"colour space family {fam} not supported")


def luminance(rgb) -> float:
    r, g, b = rgb
    return 0.3 * r + 0.59 * g + 0.11 * b


def finite(v: float) -> float:
    return v if math.isfinite(v) else 0.0
