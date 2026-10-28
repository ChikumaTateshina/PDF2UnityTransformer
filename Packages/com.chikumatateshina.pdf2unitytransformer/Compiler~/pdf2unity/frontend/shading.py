"""Shadings (SPEC §5.4). Axial (2) and radial (3) shadings are sampled into an
sRGB colour ramp and drawn natively; other types are precomposited."""

from __future__ import annotations

import json

import pikepdf

from ..diagnostics import Unsupported
from ..ir.model import ShadingResource
from .functions import load_function
from .pdfutil import num_list, objkey, sha256

RAMP_SAMPLES = 256


class ShadingLoader:
    def __init__(self, ctx):
        self.ctx = ctx
        self.cache = {}

    def load(self, obj, resources) -> ShadingResource:
        key = objkey(obj)
        if key[0] != "direct" and key in self.cache:
            res = self.cache[key]
            if isinstance(res, Unsupported):
                raise res
            return res
        try:
            res = self._load(obj, resources)
        except Unsupported as e:
            if key[0] != "direct":
                self.cache[key] = e
            raise
        if key[0] != "direct":
            self.cache[key] = res
        return res

    def _load(self, sh, resources) -> ShadingResource:
        st = int(sh.get("/ShadingType", 0))
        if st not in (2, 3):
            raise Unsupported("shading.type", f"shading type {st} is precomposited")
        cs = self.ctx.colorspaces.resolve(sh["/ColorSpace"], resources)
        coords = num_list(sh.get("/Coords"))
        if len(coords) != (4 if st == 2 else 6):
            raise Unsupported("shading.coords", "invalid shading coordinates")
        domain = num_list(sh.get("/Domain", [0, 1]))
        extend = [bool(v) for v in sh.get("/Extend", [False, False])]
        fobj = sh.get("/Function")
        if fobj is None:
            raise Unsupported("shading.function", "shading without function")
        fn = load_function(fobj, self.ctx.budget)
        t0, t1 = domain[0], domain[1]
        ramp = []
        for i in range(RAMP_SAMPLES):
            t = t0 + (t1 - t0) * i / (RAMP_SAMPLES - 1)
            comps = fn([t])
            ramp.append([round(v, 5) for v in cs.to_rgb(comps)])
        bg = sh.get("/Background")
        background = [round(v, 5) for v in cs.to_rgb(num_list(bg))] if bg is not None else None
        bbox = num_list(sh.get("/BBox")) or None
        body = json.dumps([st, coords, domain, extend, ramp, background, bbox], sort_keys=True)
        ref = "s-" + sha256(body.encode())[:20]
        res = self.ctx.ir.shadings.get(ref)
        if res is None:
            res = ShadingResource(ref=ref, shading_type=st, coords=coords, domain=domain, extend=extend,
                                  ramp=ramp, background=background, bbox=bbox, colorspace=cs.describe())
            self.ctx.ir.shadings[ref] = res
        return res


def is_shading_pattern(pat) -> bool:
    return isinstance(pat, (pikepdf.Dictionary, pikepdf.Stream)) and int(pat.get("/PatternType", 0)) == 2
