"""Diagnostics collected across all compiler stages (FR-13, acceptance criterion 2)."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Optional

INFO = "info"
WARNING = "warning"
ERROR = "error"


class CompileError(Exception):
    """Fatal error: the document (or the requested mode) cannot be compiled."""

    def __init__(self, code: str, message: str):
        super().__init__(f"[{code}] {message}")
        self.code = code
        self.message = message


class Unsupported(Exception):
    """A single drawing construct cannot be represented natively.

    Raised inside the frontend/optimizer; the caller turns it into an
    `unsupported` IR operation that is later precomposited (Hybrid) or
    rejected (Strict).
    """

    def __init__(self, code: str, message: str):
        super().__init__(f"[{code}] {message}")
        self.code = code
        self.message = message


@dataclass
class Diagnostic:
    code: str
    severity: str
    message: str
    page: Optional[int] = None  # 1-based page number
    source: Optional[str] = None  # stream / object / operator location
    fallback: Optional[str] = None  # what was done instead ("precomposite", "skipped", ...)

    def to_json(self) -> dict:
        d = {"code": self.code, "severity": self.severity, "message": self.message}
        if self.page is not None:
            d["page"] = self.page
        if self.source:
            d["source"] = self.source
        if self.fallback:
            d["fallback"] = self.fallback
        return d


@dataclass
class Diagnostics:
    items: list[Diagnostic] = field(default_factory=list)
    # Deduplicate identical (code, page, message) messages; keep a counter instead.
    _seen: dict = field(default_factory=dict)

    def add(self, code, severity, message, page=None, source=None, fallback=None):
        key = (code, severity, message, page, fallback)
        if key in self._seen:
            self._seen[key] += 1
            return
        self._seen[key] = 1
        self.items.append(Diagnostic(code, severity, message, page, source, fallback))

    def info(self, code, message, **kw):
        self.add(code, INFO, message, **kw)

    def warn(self, code, message, **kw):
        self.add(code, WARNING, message, **kw)

    def error(self, code, message, **kw):
        self.add(code, ERROR, message, **kw)

    def count(self, severity: str) -> int:
        return sum(1 for d in self.items if d.severity == severity)

    def for_page(self, page: int) -> list[Diagnostic]:
        return [d for d in self.items if d.page == page]

    def has_code(self, code: str) -> bool:
        return any(d.code == code for d in self.items)

    def to_json(self) -> list[dict]:
        out = []
        for d in self.items:
            j = d.to_json()
            n = self._seen.get((d.code, d.severity, d.message, d.page, d.fallback), 1)
            if n > 1:
                j["occurrences"] = n
            out.append(j)
        return out
