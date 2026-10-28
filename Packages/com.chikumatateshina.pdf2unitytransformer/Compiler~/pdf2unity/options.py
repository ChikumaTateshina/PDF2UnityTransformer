"""Compile options and quality profiles (FR-01)."""

from __future__ import annotations

import json
import os
from dataclasses import dataclass, field

from .diagnostics import CompileError
from .limits import Limits

MODES = ("strict", "hybrid", "page-fallback")
TARGETS = ("pc", "quest")
PROFILE_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "profiles")


@dataclass
class Profile:
    name: str
    target: str
    curve_tolerance_pt: float = 0.05  # max chord error in page space (points)
    min_stroke_width_pt: float = 0.24  # hairlines / thin strokes are widened to this
    composite_dpi: float = 300.0
    image_dpi: float = 300.0  # images are downsampled above this effective resolution
    max_texture_size: int = 4096
    texture_compression: str = "high"  # hint for the Unity importer
    budgets: dict = field(default_factory=dict)

    @staticmethod
    def load(name_or_path: str) -> "Profile":
        path = name_or_path
        if not os.path.isfile(path):
            path = os.path.join(PROFILE_DIR, f"{name_or_path}.json")
        if not os.path.isfile(path):
            raise CompileError("options.profile", f"quality profile {name_or_path} not found")
        with open(path, "r", encoding="utf-8") as f:
            d = json.load(f)
        return Profile(**d)


@dataclass
class CompileOptions:
    input: str
    output: str
    pages: str = "all"
    mode: str = "hybrid"
    targets: list = field(default_factory=lambda: ["pc"])
    profiles: dict = field(default_factory=dict)  # target -> Profile
    cmyk_profile: bytes | None = None
    substitute_fonts: str | None = None
    font_rights: str = "verify"  # verify | declared
    reference_dpi: float = 110.0
    world_scale: float = 1.0
    page_gap: float = 0.25  # fraction of page width between pages in Document.prefab
    write_reference: bool = True
    translucency: str = "native"  # native | precompose (see docs: linear-space blending)
    limits: Limits = field(default_factory=Limits)

    def validate(self):
        if self.mode not in MODES:
            raise CompileError("options.mode", f"mode must be one of {MODES}")
        for t in self.targets:
            if t not in TARGETS:
                raise CompileError("options.target", f"unknown target {t}")
        if self.translucency not in ("native", "precompose"):
            raise CompileError("options.translucency", "translucency must be native or precompose")
        if self.font_rights not in ("verify", "declared"):
            raise CompileError("options.font-rights", "font rights must be verify or declared")
        for t in self.targets:
            if t not in self.profiles:
                self.profiles[t] = Profile.load("pc-standard" if t == "pc" else "quest-standard")


def parse_page_range(spec: str, count: int) -> list[int]:
    """'all' | '1-3,5' (1-based, inclusive) -> sorted unique 0-based indices."""
    if spec in ("all", "", None):
        return list(range(count))
    out = set()
    for part in spec.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part:
            a, b = part.split("-", 1)
            lo = int(a) if a else 1
            hi = int(b) if b else count
        else:
            lo = hi = int(part)
        if lo < 1 or hi > count or lo > hi:
            raise CompileError("options.pages", f"page range {part} outside 1-{count}")
        out.update(range(lo - 1, hi))
    return sorted(out)
