"""End-to-end compile: PDF -> IR -> per-target batches -> Asset Exchange Package."""

from __future__ import annotations

import math
import os
import shutil

import numpy as np
from PIL import Image

from . import PACKAGE_SCHEMA_VERSION, __version__
from .diagnostics import CompileError, Diagnostics
from .frontend.document import build_document_ir
from .ir.serialize import save_ir
from .optimizer.lower import Batch, GlyphCache, PageLowering
from .options import CompileOptions
from .package.writer import (file_sha256, fix_winding, r6, safe_rel, triangle_area, write_geom, write_json)
from .precompose.pdfium_renderer import PdfiumRenderer

PACKAGE_FORMAT = "pdf2unity-package"
_MANAGED = ("manifest.json", "diagnostics.json", "document.ir.json.gz", "resources", "targets", "reference")


def prepare_output(path: str) -> str:
    """Create/clean the output directory. Only directories produced by this compiler are reused."""
    path = os.path.abspath(path)
    if os.path.isdir(path) and os.listdir(path):
        man = os.path.join(path, "manifest.json")
        ours = False
        if os.path.isfile(man):
            import json
            try:
                with open(man, "r", encoding="utf-8") as f:
                    ours = json.load(f).get("format") == PACKAGE_FORMAT
            except (OSError, ValueError):
                ours = False
        if not ours:
            raise CompileError("output.not-empty",
                               f"output directory {path} is not empty and is not a pdf2unity package")
        for name in _MANAGED:
            p = os.path.join(path, name)
            if os.path.isdir(p):
                shutil.rmtree(p)
            elif os.path.isfile(p):
                os.remove(p)
    os.makedirs(path, exist_ok=True)
    return path


def _composite_batch(page, region, rp, rel_tex):
    W, H = page.display_size
    k = page.user_unit / 72.0
    x0, y0, x1, y1 = region.rect
    xs = np.array([x0, x1, x1, x0], dtype=np.float64)
    ys = np.array([y0, y0, y1, y1], dtype=np.float64)
    verts = np.stack([(xs - W / 2) * k, (H / 2 - ys) * k], axis=1).astype(np.float32)
    uvs = np.array([[0, 1], [1, 1], [1, 0], [0, 0]], dtype=np.float32)
    colors = np.full((4, 4), 255, dtype=np.uint8)
    idx = np.array([0, 1, 2, 0, 2, 3], dtype=np.uint32)
    return Batch(kind="textured", texture=rel_tex, composite=True, verts=verts, colors=colors, uvs=uvs, idx=idx,
                 op_range=(region.op_ids[0], region.op_ids[-1]))


def _status(errors: int, review: list, fail: list):
    if errors or fail:
        return "fail"
    if review:
        return "needs-review"
    return "pass"


def compile_document(options: CompileOptions, progress=None) -> dict:
    options.validate()
    diag = Diagnostics()
    out = prepare_output(options.output)
    ir, ctx = build_document_ir(options, diag, out, progress)
    save_ir(ir, os.path.join(out, "document.ir.json.gz"))

    renderer = PdfiumRenderer(options.input)
    try:
        for page in ir.pages:
            pw, ph = renderer.page_size(page.index)
            dw, dh = page.display_size
            if abs(pw - dw) > 0.5 or abs(ph - dh) > 0.5:
                diag.warn("page.size-mismatch", f"reference renderer page size {pw:.1f}x{ph:.1f} differs from "
                          f"{dw:.1f}x{dh:.1f}", page=page.index + 1)
        glyph_cache = GlyphCache(ir)
        target_reports = {}
        page_reports = {p.index: {"page": p.index + 1, "operations": len(p.operations)} for p in ir.pages}
        for t in options.targets:
            target_reports[t] = _compile_target(t, options, ir, renderer, diag, glyph_cache, out, page_reports,
                                                progress)
        if options.write_reference:
            os.makedirs(os.path.join(out, "reference"), exist_ok=True)
            for k, page in enumerate(ir.pages):
                img = renderer.render_page(page.index, options.reference_dpi)
                img.save(os.path.join(out, "reference", f"page-{page.index + 1:04d}.png"), compress_level=6)
                if progress:
                    progress("reference", k + 1, len(ir.pages))
    finally:
        renderer.close()
        ctx.pdf.close()

    # ---- status per target -----------------------------------------------------
    errors = diag.count("error")
    review_common = []
    if ctx.license_review:
        review_common.append("font-license-review")
    if diag.has_code("font.substituted") or diag.has_code("font.license-substituted"):
        review_common.append("substitute-fonts")
    if diag.has_code("color.icc-fallback"):
        review_common.append("icc-fallback")
    if diag.has_code("color.cmyk-naive"):
        review_common.append("cmyk-approximation")
    for t, rep in target_reports.items():
        review = list(review_common)
        if t == "quest" and not rep["budgetFailures"]:
            review.append("device-test-required")
        rep["status"] = _status(errors, review, rep["budgetFailures"])
        rep["reviewReasons"] = review

    # ---- layout of Document.prefab --------------------------------------------
    layout = []
    widths = [p.display_size[0] * p.user_unit / 72.0 for p in ir.pages]
    gap = (max(widths) if widths else 0) * options.page_gap
    total = sum(widths) + gap * max(0, len(widths) - 1)
    x = -total / 2
    for p, w in zip(ir.pages, widths):
        layout.append({"page": p.index + 1, "position": [round(x + w / 2, 6), 0.0, 0.0]})
        x += w + gap

    assets = {}
    for root, _, files in os.walk(out):
        for fn in files:
            full = os.path.join(root, fn)
            rel = safe_rel(os.path.relpath(full, out))
            if rel in ("manifest.json", "diagnostics.json"):
                continue
            assets[rel] = "sha256:" + file_sha256(full)

    pages_json = []
    for p in ir.pages:
        pages_json.append({
            "page": p.index + 1,
            "mediaBox": r6(p.media_box),
            "cropBox": r6(p.crop_box),
            "rotation": p.rotation,
            "userUnit": p.user_unit,
            "displaySizePt": r6(p.display_size),
            "unitySize": r6([p.display_size[0] * p.user_unit / 72.0, p.display_size[1] * p.user_unit / 72.0]),
            "targets": {t: f"targets/{t}/pages/page-{p.index + 1:04d}.json" for t in options.targets},
        })
    doc_name = os.path.splitext(os.path.basename(options.input))[0]
    manifest = {
        "format": PACKAGE_FORMAT,
        "schemaVersion": PACKAGE_SCHEMA_VERSION,
        "irSchemaVersion": ir.schema_version,
        "compilerVersion": __version__,
        "sourceHash": ir.source_hash,
        "sourceName": doc_name,
        "options": {
            "mode": options.mode,
            "pages": options.pages,
            "targets": options.targets,
            "profiles": {t: options.profiles[t].name for t in options.targets},
            "fontRights": options.font_rights,
            "worldScale": options.world_scale,
            "translucency": options.translucency,
        },
        "pageToUnity": "72 PDF points * UserUnit = 1 Unity unit; page centred at origin, front faces -Z",
        "pages": pages_json,
        "layout": layout,
        "targets": {t: {k: v for k, v in rep.items() if k != "pages"} for t, rep in target_reports.items()},
        "assets": dict(sorted(assets.items())),
        "warningCount": diag.count("warning"),
        "errorCount": errors,
        "licenseNotice": "Converting a PDF does not grant any right to reproduce or distribute the document, "
                         "its images or its fonts. Rights must be confirmed by the author.",
    }
    write_json(os.path.join(out, "manifest.json"), manifest)
    diagnostics = {
        "schemaVersion": 1,
        "sourceHash": ir.source_hash,
        "compilerVersion": __version__,
        "targets": {t: {"status": rep["status"], "reviewReasons": rep["reviewReasons"],
                        "budgetFailures": rep["budgetFailures"]} for t, rep in target_reports.items()},
        "pages": [page_reports[p.index] for p in ir.pages],
        "fonts": [{"ref": f.ref, "name": f.name, "subtype": f.subtype, "embedded": f.embedded,
                   "license": f.license, "substitute": f.substitute, "glyphs": len(f.glyphs)}
                  for f in sorted(ir.fonts.values(), key=lambda f: f.ref)],
        "messages": diag.to_json(),
    }
    write_json(os.path.join(out, "diagnostics.json"), diagnostics)
    return {"output": out, "manifest": manifest, "diagnostics": diagnostics}


def _compile_target(t, options, ir, renderer, diag, glyph_cache, out, page_reports, progress):
    prof = options.profiles[t]
    tdir = os.path.join(out, "targets", t)
    for sub in ("pages", "textures", "composites"):
        os.makedirs(os.path.join(tdir, sub), exist_ok=True)
    budgets = prof.budgets or {}
    bpp = float(budgets.get("bytesPerPixel", 4.0))
    mip = float(budgets.get("mipFactor", 1.333))
    image_ratio = {}
    page_files = []
    rendered_pages = []
    total_tris = 0
    failures = []
    for k, page in enumerate(ir.pages):
        rp = PageLowering(ir, page, prof, options.mode, diag, glyph_cache,
                          precompose_translucent=options.translucency == "precompose").run()
        for ref, ratio in rp.image_needs.items():
            image_ratio[ref] = max(image_ratio.get(ref, 0.0), ratio)
        # precomposite regions
        comp_meta = []
        for ri, region in enumerate(rp.composites):
            w_pt = region.rect[2] - region.rect[0]
            h_pt = region.rect[3] - region.rect[1]
            dpi = prof.composite_dpi * page.user_unit
            longest = max(w_pt, h_pt) * dpi / 72.0
            if longest > prof.max_texture_size:
                dpi = prof.max_texture_size * 72.0 / max(w_pt, h_pt)
                diag.warn("fallback.resolution", f"precomposited region reduced to {dpi:.0f} dpi to fit "
                          f"{prof.max_texture_size}px ({t})", page=page.index + 1)
            img = renderer.render_region(page.index, region.rect, dpi)
            rel = safe_rel(f"targets/{t}/composites/page-{page.index + 1:04d}-r{ri:02d}.png")
            img.save(os.path.join(out, rel), compress_level=6)
            region.file, region.dpi, region.pixel_size = rel, dpi, img.size
            rp.batches.append(_composite_batch(page, region, rp, rel))
            comp_meta.append({"file": rel, "rectPt": r6(region.rect), "dpi": round(dpi, 3),
                              "pixelSize": list(img.size), "reasons": region.reasons,
                              "operations": region.op_ids[:64]})
        for b in rp.batches:
            b.idx = fix_winding(b.verts, b.idx)
        rp.batches = [b for b in rp.batches if len(b.idx)]
        rendered_pages.append((page, rp, comp_meta))
        if progress:
            progress(f"lower:{t}", k + 1, len(ir.pages))

    # ---- textures (resampled per target) -------------------------------------
    tex_info = {}
    for ref in sorted(image_ratio):
        res = ir.images[ref]
        ratio = min(1.0, image_ratio[ref])
        w = max(1, int(math.ceil(res.width * ratio)))
        h = max(1, int(math.ceil(res.height * ratio)))
        m = max(w, h)
        if m > prof.max_texture_size:
            s = prof.max_texture_size / m
            w, h = max(1, int(w * s)), max(1, int(h * s))
        src = os.path.join(out, res.file)
        rel = safe_rel(f"targets/{t}/textures/{ref}.png")
        dst = os.path.join(out, rel)
        if (w, h) == (res.width, res.height):
            shutil.copyfile(src, dst)
        else:
            with Image.open(src) as im:
                im.convert("RGBA").resize((w, h), Image.LANCZOS).save(dst, compress_level=6)
            diag.info("image.resampled", f"image {ref[:10]} resampled {res.width}x{res.height} -> {w}x{h} ({t})")
        tex_info[ref] = {"file": rel, "size": [w, h], "hasAlpha": res.has_alpha or res.stencil,
                         "stencil": res.stencil}

    tex_bytes = 0
    for ti in tex_info.values():
        tex_bytes += ti["size"][0] * ti["size"][1] * bpp * mip
    for page, rp, comp_meta in rendered_pages:
        for c in comp_meta:
            tex_bytes += c["pixelSize"][0] * c["pixelSize"][1] * bpp * mip
        tris = sum(len(b.idx) // 3 for b in rp.batches)
        verts = sum(len(b.verts) for b in rp.batches)
        total_tris += tris
        area = rp.unity_size[0] * rp.unity_size[1]
        cover = sum(triangle_area(b.verts, b.idx) for b in rp.batches)
        overdraw = cover / area if area > 0 else 0.0
        pno = page.index + 1
        stats = dict(rp.stats)
        stats.update({"batches": len(rp.batches), "triangles": tris, "vertices": verts,
                      "fallbackRegions": len(comp_meta), "overdraw": round(overdraw, 3)})
        if budgets.get("enforce"):
            if tris > budgets.get("maxTrianglesPerPage", math.inf):
                failures.append(f"page {pno}: {tris} triangles > {budgets['maxTrianglesPerPage']}")
            if len(rp.batches) > budgets.get("maxBatchesPerPage", math.inf):
                failures.append(f"page {pno}: {len(rp.batches)} batches > {budgets['maxBatchesPerPage']}")
            if overdraw > budgets.get("maxOverdraw", math.inf):
                failures.append(f"page {pno}: overdraw {overdraw:.2f} > {budgets['maxOverdraw']}")
        page_reports[page.index].setdefault("targets", {})[t] = stats
        page_reports[page.index]["warnings"] = sorted({d.message for d in diag.for_page(pno)
                                                       if d.severity != "info"})[:50]
        batches_json = []
        for order, b in enumerate(rp.batches):
            entry = {"order": order, "kind": b.kind, "composite": b.composite, "vertexCount": len(b.verts),
                     "indexCount": len(b.idx), "opRange": list(b.op_range),
                     "bounds": r6([float(b.verts[:, 0].min()), float(b.verts[:, 1].min()),
                                   float(b.verts[:, 0].max()), float(b.verts[:, 1].max())])}
            if b.kind == "textured":
                if b.composite:
                    entry["texture"] = b.texture
                    entry["alpha"] = False
                else:
                    ti = tex_info[b.texture]
                    entry["texture"] = ti["file"]
                    entry["alpha"] = True
                    entry["image"] = b.texture
            batches_json.append(entry)
        geom_rel = safe_rel(f"targets/{t}/pages/page-{pno:04d}.geom")
        write_geom(os.path.join(out, geom_rel), rp.batches)
        page_json = {
            "page": pno,
            "unitySize": r6(rp.unity_size),
            "geometry": geom_rel,
            "batches": batches_json,
            "composites": comp_meta,
            "stats": stats,
        }
        rel = safe_rel(f"targets/{t}/pages/page-{pno:04d}.json")
        write_json(os.path.join(out, rel), page_json)
        page_files.append(rel)
    mb = tex_bytes / (1024 * 1024)
    if budgets.get("enforce"):
        if total_tris > budgets.get("maxTrianglesTotal", math.inf):
            failures.append(f"document: {total_tris} triangles > {budgets['maxTrianglesTotal']}")
        if mb > budgets.get("maxTextureMemoryMB", math.inf):
            failures.append(f"document: texture memory {mb:.1f} MB > {budgets['maxTextureMemoryMB']} MB")
    for f in failures:
        diag.warn("target.budget", f"{t}: {f}")
    return {
        "profile": prof.name,
        "textureCompression": prof.texture_compression,
        "maxTextureSize": prof.max_texture_size,
        "pages": page_files,
        "triangles": total_tris,
        "textures": len(tex_info),
        "textureMemoryMB": round(mb, 2),
        "budgetFailures": failures,
    }
