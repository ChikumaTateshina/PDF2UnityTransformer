"""Document IR (SPEC-PU-001 §4). Unity independent, versioned, serialisable.

Coordinates:
  * `ctm` maps the operation's user space to *page space* (= PDF default user
    space of the page, in points, before CropBox/Rotate/UserUnit).
  * `PageIR.page_to_display` maps page space to *display space*: origin at the
    top-left of the visible (cropped, rotated) page, x right, y down, points.
    Unity placement (`PageToUnity`) is derived from it in the optimizer.
  * Path segments are stored in the operation's user space.
  * Glyph outlines are stored in text space for a font size of 1
    (glyph space already multiplied by FontMatrix).
"""

from __future__ import annotations

from dataclasses import dataclass, field, fields, is_dataclass
from typing import Any, Optional

# Path command tuples: ("M", x, y) ("L", x, y) ("C", x1, y1, x2, y2, x3, y3) ("Z",)
PathCmds = list


@dataclass
class Paint:
    kind: str  # "color" | "shading" | "tiling"
    rgb: Optional[tuple] = None  # display sRGB 0..1 (kind == color)
    alpha: float = 1.0
    colorspace: Optional[str] = None  # source colour space family (diagnostic)
    components: Optional[list] = None  # source components
    shading_ref: Optional[str] = None  # resources.shadings key
    pattern_matrix: Optional[tuple] = None  # pattern space -> page space


@dataclass
class StrokeStyle:
    width: float = 1.0
    cap: int = 0  # 0 butt, 1 round, 2 square
    join: int = 0  # 0 miter, 1 round, 2 bevel
    miter_limit: float = 10.0
    dash: list = field(default_factory=list)
    dash_phase: float = 0.0


@dataclass
class PathIR:
    segments: PathCmds
    fill_rule: Optional[str] = None  # "nonzero" | "evenodd" | None (no fill)
    fill: Optional[Paint] = None
    stroke: Optional[Paint] = None
    stroke_style: Optional[StrokeStyle] = None


@dataclass
class Glyph:
    key: str  # resources.fonts[font_ref].glyphs key
    matrix: tuple  # text space (size 1) -> user space


@dataclass
class GlyphRunIR:
    font_ref: str
    glyphs: list  # list[Glyph]
    text_rendering_mode: int = 0
    fill: Optional[Paint] = None
    stroke: Optional[Paint] = None
    stroke_style: Optional[StrokeStyle] = None


@dataclass
class ImageIR:
    image_ref: str  # resources.images key
    stencil: bool = False  # image mask painted with `fill`
    fill: Optional[Paint] = None
    alpha: float = 1.0
    interpolate: bool = False


@dataclass
class ShadingIR:
    shading_ref: str
    shading_matrix: tuple  # shading space -> page space
    # For pattern fills the painted area is a path; for `sh` it is the clip only.
    segments: Optional[PathCmds] = None
    fill_rule: Optional[str] = None
    alpha: float = 1.0


@dataclass
class UnsupportedIR:
    reason: str  # diagnostic code
    detail: str
    bbox: tuple  # page space bbox of the affected area (before clipping)


@dataclass
class Operation:
    id: int
    sequence: int
    kind: str  # "path" | "glyph_run" | "image" | "shading" | "unsupported"
    source: str
    ctm: tuple
    clip_ref: int
    group_ref: Optional[int]
    payload: Any
    blend_mode: str = "Normal"
    overprint: bool = False


@dataclass
class ClipIR:
    id: int
    parent: Optional[int]
    segments: PathCmds  # in the user space given by `ctm`
    ctm: tuple
    fill_rule: str


@dataclass
class GroupIR:
    id: int
    parent: Optional[int]
    isolated: bool
    knockout: bool
    opacity: float
    blend_mode: str
    soft_mask: bool
    source: str
    flattened: bool  # True when the group is provably equivalent to direct drawing


@dataclass
class PageIR:
    index: int  # 0-based
    media_box: tuple
    crop_box: tuple  # effective visible box (CropBox intersect MediaBox), page space
    rotation: int
    user_unit: float
    display_size: tuple  # (width, height) in points, after rotation
    page_to_display: tuple
    operations: list = field(default_factory=list)
    clips: list = field(default_factory=list)  # ClipIR, clips[0] is the page box
    groups: list = field(default_factory=list)
    annotations: int = 0


@dataclass
class FontResource:
    ref: str
    name: str
    subtype: str
    embedded: bool
    program_hash: Optional[str]
    license: dict  # {"fsType": int|None, "status": "installable|restricted|preview-print|editable|unknown"}
    glyphs: dict = field(default_factory=dict)  # key -> PathCmds (text space, size 1)
    substitute: Optional[str] = None


@dataclass
class ImageResource:
    ref: str  # content hash
    width: int
    height: int
    has_alpha: bool
    stencil: bool
    colorspace: str
    file: str  # relative path inside the package (RGBA PNG)
    source: str
    soft_alpha: bool = False  # has alpha values strictly between 0 and 1


@dataclass
class ShadingResource:
    ref: str
    shading_type: int  # 2 axial, 3 radial
    coords: list
    domain: list
    extend: list
    ramp: list  # sampled sRGB colours over the domain, [[r,g,b], ...]
    background: Optional[list]
    bbox: Optional[list]
    colorspace: str


@dataclass
class DocumentIR:
    schema_version: int
    source_hash: str
    compiler_version: str
    pages: list = field(default_factory=list)
    fonts: dict = field(default_factory=dict)
    images: dict = field(default_factory=dict)
    shadings: dict = field(default_factory=dict)


# --------------------------------------------------------------------------
# (De)serialisation. Dataclasses are tagged with their type name so that the
# IR can be reloaded without the PDF (e.g. by an alternative backend).

_TYPES = {
    cls.__name__: cls
    for cls in (
        Paint, StrokeStyle, PathIR, Glyph, GlyphRunIR, ImageIR, ShadingIR, UnsupportedIR,
        Operation, ClipIR, GroupIR, PageIR, FontResource, ImageResource, ShadingResource,
        DocumentIR,
    )
}


def _round(v):
    if isinstance(v, float):
        r = round(v, 6)
        return 0.0 if r == 0 else r
    return v


def to_jsonable(obj):
    if is_dataclass(obj):
        d = {"$t": type(obj).__name__}
        for f in fields(obj):
            d[f.name] = to_jsonable(getattr(obj, f.name))
        return d
    if isinstance(obj, dict):
        return {str(k): to_jsonable(v) for k, v in sorted(obj.items())}
    if isinstance(obj, (list, tuple)):
        return [to_jsonable(v) for v in obj]
    return _round(obj)


def from_jsonable(obj):
    if isinstance(obj, dict):
        t = obj.get("$t")
        if t is not None:
            cls = _TYPES[t]
            kw = {k: from_jsonable(v) for k, v in obj.items() if k != "$t"}
            for f in fields(cls):
                # Tuples are serialised as lists; restore the tuple fields we rely on.
                if f.name in ("ctm", "matrix", "bbox", "media_box", "crop_box", "display_size",
                              "page_to_display", "rgb", "pattern_matrix", "shading_matrix") \
                        and isinstance(kw.get(f.name), list):
                    kw[f.name] = tuple(kw[f.name])
                if f.name == "segments" and isinstance(kw.get(f.name), list):
                    kw[f.name] = [tuple(s) for s in kw[f.name]]
            if cls is FontResource:
                kw["glyphs"] = {k: [tuple(s) for s in v] for k, v in kw["glyphs"].items()}
            return cls(**kw)
        return {k: from_jsonable(v) for k, v in obj.items()}
    if isinstance(obj, list):
        return [from_jsonable(v) for v in obj]
    return obj
