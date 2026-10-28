"""IR container: gzip-compressed canonical JSON (`document.ir.json.gz`).

The container is deterministic (sorted keys, fixed float rounding, mtime=0 in
the gzip header) so that identical inputs produce byte-identical files (FR-14).
"""

from __future__ import annotations

import gzip
import io
import json

from .. import IR_SCHEMA_VERSION
from .model import DocumentIR, from_jsonable, to_jsonable

MAGIC = "pdf2unity-ir"


def dumps(doc: DocumentIR) -> bytes:
    payload = {"format": MAGIC, "schemaVersion": IR_SCHEMA_VERSION, "document": to_jsonable(doc)}
    raw = json.dumps(payload, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    buf = io.BytesIO()
    with gzip.GzipFile(fileobj=buf, mode="wb", mtime=0, compresslevel=6) as gz:
        gz.write(raw)
    return buf.getvalue()


def save_ir(doc: DocumentIR, path: str) -> None:
    with open(path, "wb") as f:
        f.write(dumps(doc))


def load_ir(path: str) -> DocumentIR:
    with gzip.open(path, "rb") as f:
        payload = json.loads(f.read().decode("utf-8"))
    if payload.get("format") != MAGIC:
        raise ValueError("not a pdf2unity IR file")
    if payload.get("schemaVersion") != IR_SCHEMA_VERSION:
        raise ValueError(f"unsupported IR schema {payload.get('schemaVersion')}")
    return from_jsonable(payload["document"])
