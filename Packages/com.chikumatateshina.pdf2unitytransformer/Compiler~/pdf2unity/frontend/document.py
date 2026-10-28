"""Document loading: input checks, page tree, page boxes, optional content (SPEC §3.1)."""

from __future__ import annotations

import os

import pikepdf

from .. import IR_SCHEMA_VERSION, __version__
from ..diagnostics import CompileError, Diagnostics
from ..ir.model import DocumentIR, PageIR
from ..limits import Budget
from ..options import CompileOptions, parse_page_range
from .colorspace import ColorSpaceFactory
from .fonts import FontLoader
from .images import ImageDecoder
from .pdfutil import num_list, objkey, resolve_inherited
from .shading import ShadingLoader


class OptionalContent:
    """Default visibility of optional content groups (/OCProperties /D)."""

    def __init__(self, pdf: pikepdf.Pdf, diag: Diagnostics):
        self.off = set()
        self.base_on = True
        root = pdf.Root
        ocp = root.get("/OCProperties")
        if ocp is None:
            return
        d = ocp.get("/D")
        if d is None:
            return
        self.base_on = str(d.get("/BaseState", "/ON")) != "/OFF"
        if self.base_on:
            for g in d.get("/OFF", []):
                self.off.add(objkey(g))
        else:
            all_groups = {objkey(g) for g in ocp.get("/OCGs", [])}
            on = {objkey(g) for g in d.get("/ON", [])}
            self.off = all_groups - on
        if "/AS" in d:
            diag.info("oc.usage", "optional content usage application dictionaries ignored; default state used")

    def visible(self, oc) -> bool:
        if oc is None:
            return True
        t = str(oc.get("/Type", "/OCG"))
        if t == "/OCMD":
            groups = oc.get("/OCGs")
            if groups is None:
                return True
            if isinstance(groups, pikepdf.Dictionary):
                groups = [groups]
            states = [objkey(g) not in self.off for g in groups]
            policy = str(oc.get("/P", "/AnyOn"))
            if not states:
                return True
            if policy == "/AllOn":
                return all(states)
            if policy == "/AnyOff":
                return not all(states)
            if policy == "/AllOff":
                return not any(states)
            return any(states)
        return objkey(oc) not in self.off


class FrontendContext:
    def __init__(self, pdf, options: CompileOptions, diag: Diagnostics, package_dir: str, source_hash: str):
        self.pdf = pdf
        self.options = options
        self.limits = options.limits
        self.budget = Budget(options.limits)
        self.diag = diag
        self.package_dir = package_dir
        self.license_review = False
        self.ir = DocumentIR(schema_version=IR_SCHEMA_VERSION, source_hash=f"sha256:{source_hash}",
                             compiler_version=__version__)
        self.colorspaces = ColorSpaceFactory(self)
        self.fonts = FontLoader(self)
        self.images = ImageDecoder(self)
        self.shadings = ShadingLoader(self)
        self.oc = OptionalContent(pdf, diag)


def check_input_file(path: str, limits) -> str:
    if not os.path.isfile(path):
        raise CompileError("input.missing", f"input {path} not found")
    size = os.path.getsize(path)
    if size > limits.max_input_bytes:
        raise CompileError("input.too-large", f"input is {size} bytes (limit {limits.max_input_bytes})")
    with open(path, "rb") as f:
        head = f.read(1024)
        if b"%PDF-" not in head:
            raise CompileError("input.not-pdf", "missing %PDF- header")
        f.seek(0)
        import hashlib
        h = hashlib.sha256()
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def open_pdf(path: str) -> pikepdf.Pdf:
    try:
        pdf = pikepdf.open(path)
    except pikepdf.PasswordError:
        raise CompileError("input.encrypted", "document requires a password; encrypted PDFs are not converted")
    except pikepdf.PdfError as e:
        raise CompileError("input.corrupt", f"PDF cannot be parsed: {e}")
    if pdf.is_encrypted:
        pdf.close()
        raise CompileError("input.encrypted",
                           "document is encrypted / permission restricted; the compiler does not bypass protection")
    return pdf


def _box(page_obj, key):
    v = resolve_inherited(page_obj, key)
    if v is None:
        return None
    b = num_list(v)
    if len(b) != 4:
        return None
    return (min(b[0], b[2]), min(b[1], b[3]), max(b[0], b[2]), max(b[1], b[3]))


def page_geometry(page_obj, index: int, diag: Diagnostics) -> PageIR:
    media = _box(page_obj, "/MediaBox") or (0.0, 0.0, 612.0, 792.0)
    crop = _box(page_obj, "/CropBox") or media
    crop = (max(crop[0], media[0]), max(crop[1], media[1]), min(crop[2], media[2]), min(crop[3], media[3]))
    if crop[2] <= crop[0] or crop[3] <= crop[1]:
        diag.warn("page.box", "CropBox does not intersect MediaBox; MediaBox used", page=index + 1)
        crop = media
    rot = resolve_inherited(page_obj, "/Rotate")
    rot = int(rot) % 360 if rot is not None else 0
    if rot % 90:
        diag.warn("page.rotate", f"invalid Rotate {rot}; ignored", page=index + 1)
        rot = 0
    uu = page_obj.get("/UserUnit")
    user_unit = float(uu) if uu is not None else 1.0
    x0, y0, x1, y1 = crop
    w, h = x1 - x0, y1 - y0
    if rot == 0:
        m, size = (1.0, 0.0, 0.0, -1.0, -x0, y1), (w, h)
    elif rot == 90:
        m, size = (0.0, 1.0, 1.0, 0.0, -y0, -x0), (h, w)
    elif rot == 180:
        m, size = (-1.0, 0.0, 0.0, 1.0, x1, -y0), (w, h)
    else:
        m, size = (0.0, -1.0, -1.0, 0.0, y1, x1), (h, w)
    annots = page_obj.get("/Annots")
    n_annots = len(annots) if isinstance(annots, pikepdf.Array) else 0
    return PageIR(index=index, media_box=media, crop_box=crop, rotation=rot, user_unit=user_unit,
                  display_size=size, page_to_display=m, annotations=n_annots)


def build_document_ir(options: CompileOptions, diag: Diagnostics, package_dir: str, progress=None):
    """Run the frontend over the selected pages. Returns (DocumentIR, context)."""
    from .interpreter import PageInterpreter

    source_hash = check_input_file(options.input, options.limits)
    pdf = open_pdf(options.input)
    n = len(pdf.pages)
    if n > options.limits.max_pages:
        raise CompileError("input.pages", f"{n} pages exceed the limit of {options.limits.max_pages}")
    indices = parse_page_range(options.pages, n)
    ctx = FrontendContext(pdf, options, diag, package_dir, source_hash)
    if pdf.Root.get("/AcroForm") is not None:
        diag.info("scope.forms", "interactive form fields are out of scope; widget appearances not converted")
    for k, i in enumerate(indices):
        ctx.budget.check_time()
        page_obj = pdf.pages[i].obj
        page_ir = page_geometry(page_obj, i, diag)
        if page_ir.annotations:
            diag.info("scope.annotations", f"{page_ir.annotations} annotation(s) not rendered (out of scope)",
                      page=i + 1)
        PageInterpreter(ctx, page_ir, page_obj).run()
        ctx.ir.pages.append(page_ir)
        if progress:
            progress("frontend", k + 1, len(indices))
    return ctx.ir, ctx

