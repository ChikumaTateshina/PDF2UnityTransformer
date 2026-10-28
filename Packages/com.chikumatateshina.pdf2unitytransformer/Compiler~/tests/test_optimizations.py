"""Focused regressions: python -m unittest discover -s <Compiler~>/tests."""

import os
from pathlib import Path
import struct
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from pdf2unity.compile import compile_document
from pdf2unity.ir.model import ClipIR
from pdf2unity.matrix import IDENTITY
from pdf2unity.optimizer.lower import ClipResolver
from pdf2unity.package.writer import fix_winding, read_geom, triangle_area, write_geom


def rectangle(size):
    return [("M", 0, 0), ("L", size, 0), ("L", size, size), ("L", 0, size), ("Z",)]


class ClipTests(unittest.TestCase):
    def test_deep_chain_and_cached_ancestors(self):
        clips = [ClipIR(i, i - 1 if i else None, rectangle(10), IDENTITY, "nonzero")
                 for i in range(sys.getrecursionlimit() + 10)]
        resolver = ClipResolver(SimpleNamespace(clips=clips), 0.05)
        shape = resolver.get(len(clips) - 1)
        self.assertEqual(shape.rect, (0, 0, 10, 10))
        self.assertEqual(len(resolver.cache), len(clips))
        self.assertIs(shape, resolver.get(len(clips) - 1))

    def test_empty_parent_skips_descendant_geometry(self):
        clips = [ClipIR(0, None, [], IDENTITY, "nonzero"),
                 ClipIR(1, 0, rectangle(10), IDENTITY, "nonzero")]
        resolver = ClipResolver(SimpleNamespace(clips=clips), 0.05)
        parent = resolver.get(0)
        with patch("pdf2unity.optimizer.lower.G.fill_outline") as flatten:
            self.assertIs(parent, resolver.get(1))
            flatten.assert_not_called()

    def test_ancestor_intersection_is_preserved(self):
        triangle = [("M", 0, 0), ("L", 10, 0), ("L", 0, 10), ("Z",)]
        clips = [ClipIR(0, None, triangle, IDENTITY, "nonzero"),
                 ClipIR(1, 0, rectangle(5), IDENTITY, "nonzero")]
        resolver = ClipResolver(SimpleNamespace(clips=clips), 0.05)
        shape = resolver.get(1)
        self.assertEqual(shape.rect, (0, 0, 5, 5))
        self.assertFalse(shape.empty)


class GeometryTests(unittest.TestCase):
    def test_winding_drops_degenerates_without_mutating_input(self):
        verts = np.array([[0, 0], [1, 0], [0, 1]], dtype=np.float32)
        idx = np.array([0, 1, 2, 0, 2, 1, 0, 0, 1], dtype=np.uint32)
        before = idx.copy()
        result = fix_winding(verts, idx)
        np.testing.assert_array_equal(result, [0, 2, 1, 0, 2, 1])
        np.testing.assert_array_equal(idx, before)
        self.assertEqual(triangle_area(verts, result), 1.0)

    def test_binary_compatibility_for_strided_and_empty_arrays(self):
        verts = np.arange(12, dtype=np.float64).reshape(3, 4)[:, ::2]
        colors = np.arange(24, dtype=np.uint8).reshape(3, 8)[:, ::2]
        idx = np.array([0, 1, 2], dtype=np.uint64)
        batch = SimpleNamespace(verts=verts, colors=colors, uvs=verts, idx=idx)
        empty = SimpleNamespace(verts=np.empty((0, 2)), colors=np.empty((0, 4)),
                                uvs=None, idx=np.empty(0))
        expected = b"P2UG" + struct.pack("<IIIII", 1, 2, 3, 3, 1)
        for arr, dtype in ((verts, "<f4"), (colors, "u1"), (verts, "<f4"), (idx, "<u4")):
            expected += np.ascontiguousarray(arr, dtype=dtype).tobytes()
        expected += struct.pack("<III", 0, 0, 0)
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "page.geom")
            write_geom(path, [batch, empty])
            self.assertEqual(Path(path).read_bytes(), expected)
            loaded = read_geom(path)
        np.testing.assert_array_equal(loaded[0][0], verts)
        self.assertEqual(len(loaded[1][0]), 0)


class CleanupTests(unittest.TestCase):
    def test_pdf_closed_when_ir_save_or_renderer_initialization_fails(self):
        for stage in ("save", "renderer"):
            with self.subTest(stage=stage):
                ctx = SimpleNamespace(pdf=Mock())
                options = SimpleNamespace(validate=Mock(), output="unused", input="unused")
                with patch("pdf2unity.compile.prepare_output", return_value="unused"), \
                     patch("pdf2unity.compile.build_document_ir", return_value=(Mock(), ctx)), \
                     patch("pdf2unity.compile.save_ir", side_effect=OSError("save") if stage == "save" else None), \
                     patch("pdf2unity.compile.PdfiumRenderer", side_effect=OSError("renderer")):
                    with self.assertRaises(OSError):
                        compile_document(options)
                ctx.pdf.close.assert_called_once()


if __name__ == "__main__":
    unittest.main()
