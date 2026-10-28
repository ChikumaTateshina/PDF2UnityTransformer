"""Asset Exchange Package writer (SPEC §7.2).

Layout:
    manifest.json                 schema, hashes, pages, layout, per-target status
    diagnostics.json              warnings/errors, per-page usage report (FR-13)
    document.ir.json.gz           Document IR (engine independent)
    resources/images/<ref>.png    decoded source images (RGBA, full resolution)
    targets/<t>/pages/page-NNNN.json   batch list (ordering, materials, bounds)
    targets/<t>/pages/page-NNNN.geom   binary geometry (see GEOM format below)
    targets/<t>/textures/<ref>.png     images resampled for the target
    targets/<t>/composites/page-NNNN-rKK.png  precomposited regions
    reference/page-NNNN.png       PDFium reference renders (not for the world)

GEOM format (little endian):
    char[4] "P2UG"; u32 version (1); u32 batchCount
    per batch: u32 vertexCount; u32 indexCount; u32 flags (bit0: has UV)
               f32[2V] positions (Unity units, page local XY)
               u8[4V]  colours (sRGB RGBA)
               f32[2V] uvs (if flag)
               u32[I]  indices (clockwise when seen from -Z, i.e. the page front)
"""

from __future__ import annotations

import hashlib
import json
import os
import struct

import numpy as np

GEOM_MAGIC = b"P2UG"
GEOM_VERSION = 1


def _triangle_cross(verts, tri):
    """Signed double areas, computed in float64 for both winding and statistics."""
    a, b, c = (verts[tri[:, k]].astype(np.float64) for k in range(3))
    return (b[:, 0] - a[:, 0]) * (c[:, 1] - a[:, 1]) - (b[:, 1] - a[:, 1]) * (c[:, 0] - a[:, 0])


def fix_winding(verts: np.ndarray, idx: np.ndarray) -> np.ndarray:
    """Make every triangle clockwise in Unity XY (front face towards -Z); drop degenerate ones."""
    if len(idx) == 0:
        return idx
    tri = idx.reshape(-1, 3)
    cross = _triangle_cross(verts, tri)
    keep = np.abs(cross) > 1e-14
    tri = tri[keep]
    flip = cross[keep] > 0
    tri[flip, 1], tri[flip, 2] = tri[flip, 2].copy(), tri[flip, 1].copy()
    return tri.reshape(-1).astype(np.uint32, copy=False)


def triangle_area(verts, idx) -> float:
    if len(idx) == 0:
        return 0.0
    cross = _triangle_cross(verts, idx.reshape(-1, 3))
    return float(np.abs(cross).sum() / 2)


def _write_array(stream, array, dtype):
    # The buffer protocol avoids allocating a second bytes object for the entire
    # array. Flattening also supports empty batches and non-contiguous inputs.
    data = np.ascontiguousarray(array, dtype=dtype).reshape(-1)
    stream.write(memoryview(data).cast("B"))


def write_geom(path: str, batches) -> None:
    with open(path, "wb") as f:
        f.write(GEOM_MAGIC)
        f.write(struct.pack("<II", GEOM_VERSION, len(batches)))
        for b in batches:
            has_uv = b.uvs is not None
            f.write(struct.pack("<III", len(b.verts), len(b.idx), 1 if has_uv else 0))
            _write_array(f, b.verts, "<f4")
            _write_array(f, b.colors, np.uint8)
            if has_uv:
                _write_array(f, b.uvs, "<f4")
            _write_array(f, b.idx, "<u4")


def read_geom(path: str):
    """Reader used by tests and the software preview rasteriser."""
    out = []
    with open(path, "rb") as f:
        if f.read(4) != GEOM_MAGIC:
            raise ValueError("bad geometry magic")
        ver, n = struct.unpack("<II", f.read(8))
        if ver != GEOM_VERSION:
            raise ValueError("unsupported geometry version")
        for _ in range(n):
            nv, ni, flags = struct.unpack("<III", f.read(12))
            verts = np.frombuffer(f.read(8 * nv), dtype="<f4").reshape(nv, 2)
            colors = np.frombuffer(f.read(4 * nv), dtype=np.uint8).reshape(nv, 4)
            uvs = np.frombuffer(f.read(8 * nv), dtype="<f4").reshape(nv, 2) if flags & 1 else None
            idx = np.frombuffer(f.read(4 * ni), dtype="<u4")
            out.append((verts, colors, uvs, idx))
    return out


def file_sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def write_json(path: str, data) -> None:
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, ensure_ascii=False, indent=2, sort_keys=True)
        f.write("\n")


def safe_rel(path: str) -> str:
    """Package-relative path with forward slashes; rejects traversal."""
    p = path.replace("\\", "/")
    if p.startswith("/") or ".." in p.split("/") or ":" in p:
        raise ValueError(f"unsafe package path {path}")
    return p


def r6(v):
    return [round(float(x), 6) for x in v]
