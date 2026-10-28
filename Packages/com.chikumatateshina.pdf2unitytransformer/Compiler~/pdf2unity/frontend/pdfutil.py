"""Small helpers around pikepdf objects."""

from __future__ import annotations

import hashlib

import pikepdf


def num(v, default=0.0) -> float:
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def num_list(arr) -> list:
    return [num(v) for v in arr] if arr is not None else []


def name(v) -> str | None:
    if isinstance(v, pikepdf.Name):
        return str(v)
    return None


def objid(obj) -> str:
    """Stable identifier of an indirect object, used for diagnostics and caching."""
    try:
        if obj.is_indirect:
            n, g = obj.objgen
            return f"{n} {g} R"
    except Exception:
        pass
    return f"direct@{id(obj):x}"


def objkey(obj):
    try:
        if obj.is_indirect:
            return obj.objgen
    except Exception:
        pass
    return ("direct", id(obj))


def resolve_inherited(page_obj, key):
    node = page_obj
    depth = 0
    while node is not None and depth < 64:
        if key in node:
            return node[key]
        node = node.get("/Parent")
        depth += 1
    return None


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def pdf_string_bytes(s) -> bytes:
    if isinstance(s, pikepdf.String):
        return bytes(s)
    if isinstance(s, (bytes, bytearray)):
        return bytes(s)
    return str(s).encode("latin-1", "replace")
