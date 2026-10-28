"""Resource limits for untrusted input (Requirements §5 safety, SPEC §11)."""

from __future__ import annotations

import time
from dataclasses import dataclass

from .diagnostics import CompileError


@dataclass
class Limits:
    max_input_bytes: int = 512 * 1024 * 1024
    max_pages: int = 2000
    max_stream_bytes: int = 256 * 1024 * 1024  # decoded size of one stream
    max_total_decoded_bytes: int = 4 * 1024 * 1024 * 1024
    max_image_pixels: int = 120_000_000
    max_form_depth: int = 32
    max_ops_per_page: int = 3_000_000
    max_glyphs_per_page: int = 1_000_000
    timeout_sec: float = 1800.0


class Budget:
    """Tracks the deadline and cumulative decoded bytes for one compile."""

    def __init__(self, limits: Limits):
        self.limits = limits
        self.deadline = time.monotonic() + limits.timeout_sec
        self.decoded_bytes = 0

    def check_time(self):
        if time.monotonic() > self.deadline:
            raise CompileError("limit.timeout", f"compile exceeded {self.limits.timeout_sec:.0f}s")

    def account_decoded(self, n: int):
        if n > self.limits.max_stream_bytes:
            raise CompileError("limit.stream", f"decoded stream of {n} bytes exceeds limit")
        self.decoded_bytes += n
        if self.decoded_bytes > self.limits.max_total_decoded_bytes:
            raise CompileError("limit.decoded", "total decoded data exceeds limit")
