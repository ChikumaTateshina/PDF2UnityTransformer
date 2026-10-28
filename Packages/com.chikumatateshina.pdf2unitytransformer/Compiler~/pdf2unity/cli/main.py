"""Command line interface (SPEC §7.1).

    pdf-unity-compiler compile --input poster.pdf --pages all --mode hybrid \
        --target pc,quest --output ./build/poster

stdout carries machine readable JSON lines for the Unity process manager:
    {"event": "progress", "stage": "frontend", "current": 3, "total": 10}
    {"event": "result", "status": "ok", ...}
    {"event": "error", "code": "...", "message": "..."}
Exit codes: 0 ok, 2 compile error (reported), 3 invalid arguments, 4 internal error.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import traceback

from .. import __version__
from ..diagnostics import CompileError
from ..limits import Limits
from ..options import CompileOptions, Profile


def _emit(obj):
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def _progress(stage, current, total):
    _emit({"event": "progress", "stage": stage, "current": current, "total": total})


def build_parser():
    ap = argparse.ArgumentParser(prog="pdf-unity-compiler", description="PDF to Unity static asset compiler")
    ap.add_argument("--version", action="version", version=__version__)
    sub = ap.add_subparsers(dest="command", required=True)
    c = sub.add_parser("compile", help="compile a PDF into an Asset Exchange Package")
    c.add_argument("--input", required=True)
    c.add_argument("--output", required=True)
    c.add_argument("--pages", default="all", help="'all' or e.g. '1-3,5' (1-based)")
    c.add_argument("--mode", default="hybrid", choices=["strict", "hybrid", "page-fallback"])
    c.add_argument("--target", default="pc", help="comma separated: pc,quest")
    c.add_argument("--profile-pc", default="pc-standard")
    c.add_argument("--profile-quest", default="quest-standard")
    c.add_argument("--cmyk-profile", help="CMYK ICC profile used for DeviceCMYK")
    c.add_argument("--substitute-fonts", help="directory of licensed .ttf/.otf files or substitutes.json")
    c.add_argument("--font-rights", default="verify", choices=["verify", "declared"],
                   help="'declared': the author confirms the right to use embedded font outlines")
    c.add_argument("--translucency", default="native", choices=["native", "precompose"],
                   help="'precompose': rasterise translucent content so blending matches PDF (gamma space)")
    c.add_argument("--reference-dpi", type=float, default=110.0)
    c.add_argument("--no-reference", action="store_true")
    c.add_argument("--world-scale", type=float, default=1.0)
    c.add_argument("--timeout", type=float, default=1800.0)
    c.add_argument("--max-pages", type=int, default=2000)
    c.add_argument("--max-input-mb", type=float, default=512)
    i = sub.add_parser("inspect", help="print a summary of the IR of a compiled package")
    i.add_argument("package")
    return ap


def _options(a) -> CompileOptions:
    targets = [t.strip() for t in a.target.split(",") if t.strip()]
    profiles = {}
    if "pc" in targets:
        profiles["pc"] = Profile.load(a.profile_pc)
    if "quest" in targets:
        profiles["quest"] = Profile.load(a.profile_quest)
    cmyk = None
    if a.cmyk_profile:
        with open(a.cmyk_profile, "rb") as f:
            cmyk = f.read()
    limits = Limits(timeout_sec=a.timeout, max_pages=a.max_pages, max_input_bytes=int(a.max_input_mb * 1024 * 1024))
    return CompileOptions(input=os.path.abspath(a.input), output=os.path.abspath(a.output), pages=a.pages,
                          mode=a.mode, targets=targets, profiles=profiles, cmyk_profile=cmyk,
                          substitute_fonts=a.substitute_fonts, font_rights=a.font_rights,
                          reference_dpi=a.reference_dpi, write_reference=not a.no_reference,
                          world_scale=a.world_scale, translucency=a.translucency, limits=limits)


def _inspect(path):
    from ..ir.serialize import load_ir
    ir = load_ir(os.path.join(path, "document.ir.json.gz"))
    kinds = {}
    for p in ir.pages:
        for op in p.operations:
            kinds[op.kind] = kinds.get(op.kind, 0) + 1
    _emit({"event": "inspect", "pages": len(ir.pages), "operations": kinds, "fonts": len(ir.fonts),
           "images": len(ir.images), "shadings": len(ir.shadings)})


def main(argv=None):
    ap = build_parser()
    try:
        a = ap.parse_args(argv)
    except SystemExit as e:
        return 3 if e.code else 0
    try:
        if a.command == "inspect":
            _inspect(a.package)
            return 0
        from ..compile import compile_document
        opts = _options(a)
        res = compile_document(opts, progress=_progress)
        m = res["manifest"]
        _emit({"event": "result", "status": "ok", "output": res["output"],
               "targets": {t: v["status"] for t, v in m["targets"].items()},
               "warnings": m["warningCount"], "errors": m["errorCount"]})
        return 0
    except CompileError as e:
        _emit({"event": "error", "code": e.code, "message": e.message})
        return 2
    except Exception as e:  # pragma: no cover - reported to the Unity UI
        _emit({"event": "error", "code": "internal", "message": f"{type(e).__name__}: {e}",
               "trace": traceback.format_exc()[-4000:]})
        return 4


if __name__ == "__main__":
    sys.exit(main())
