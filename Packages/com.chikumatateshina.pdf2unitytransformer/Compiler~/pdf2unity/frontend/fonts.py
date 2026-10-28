"""Fonts: character code -> glyph id/name -> outline (FR-05, SPEC §5.2).

Glyphs are identified by the font program's glyph id or glyph name, never by
Unicode: `ToUnicode` is only consulted when a non-embedded font has to be
replaced by a user-supplied substitute font.
"""

from __future__ import annotations

import io
import json
import os
import re
from dataclasses import dataclass, field

import pikepdf
from fontTools import agl
from fontTools.cffLib import CFFFontSet
from fontTools.encodings.MacRoman import MacRoman
from fontTools.encodings.StandardEncoding import StandardEncoding
from fontTools.pens.basePen import BasePen
from fontTools.t1Lib import T1Font, deHexString, findEncryptedChunks, isHex
from fontTools.ttLib import TTFont

from ..diagnostics import CompileError, Unsupported
from ..ir.model import FontResource
from .pdfutil import num, num_list, objid, objkey, sha256


def _win_ansi():
    names = [None] * 256
    for c in range(256):
        try:
            ch = bytes([c]).decode("cp1252")
        except UnicodeDecodeError:
            continue
        names[c] = agl.UV2AGL.get(ord(ch)) or (f"uni{ord(ch):04X}" if c >= 32 else None)
    names[0x20] = "space"
    names[0xA0] = "space"
    names[0xAD] = "hyphen"
    return names


ENCODINGS = {
    "/StandardEncoding": list(StandardEncoding),
    "/MacRomanEncoding": list(MacRoman),
    "/WinAnsiEncoding": _win_ansi(),
}
for _k, _v in ENCODINGS.items():
    ENCODINGS[_k] = [None if (n in (None, ".notdef")) else n for n in _v]


def glyph_name_to_unicode(name: str) -> str | None:
    if not name:
        return None
    try:
        s = agl.toUnicode(name)
    except Exception:
        s = ""
    if s:
        return s
    m = re.fullmatch(r"(?:uni|u)([0-9A-Fa-f]{4,6})", name)
    if m:
        return chr(int(m.group(1), 16))
    return None


class _CmdPen(BasePen):
    """Collects outlines as IR path commands, scaled into text space."""

    def __init__(self, glyphset, sx, sy=None, tx=0.0, ty=0.0):
        super().__init__(glyphset)
        self.sx = sx
        self.sy = sx if sy is None else sy
        self.tx = tx
        self.ty = ty
        self.cmds = []
        self._open = False

    def _p(self, pt):
        return (pt[0] * self.sx + self.tx, pt[1] * self.sy + self.ty)

    def _moveTo(self, pt):
        x, y = self._p(pt)
        self.cmds.append(("M", x, y))

    def _lineTo(self, pt):
        x, y = self._p(pt)
        self.cmds.append(("L", x, y))

    def _curveToOne(self, p1, p2, p3):
        a, b, c = self._p(p1), self._p(p2), self._p(p3)
        self.cmds.append(("C", a[0], a[1], b[0], b[1], c[0], c[1]))

    def _qCurveToOne(self, p1, p2):
        p0 = self._p(self._getCurrentPoint())
        q1, q2 = self._p(p1), self._p(p2)
        c1 = (p0[0] + 2 / 3 * (q1[0] - p0[0]), p0[1] + 2 / 3 * (q1[1] - p0[1]))
        c2 = (q2[0] + 2 / 3 * (q1[0] - q2[0]), q2[1] + 2 / 3 * (q1[1] - q2[1]))
        self.cmds.append(("C", c1[0], c1[1], c2[0], c2[1], q2[0], q2[1]))

    def _closePath(self):
        self.cmds.append(("Z",))

    def _endPath(self):
        self.cmds.append(("Z",))


def _license_status(fs_type: int | None) -> str:
    if fs_type is None:
        return "unknown"
    if fs_type & 0x0200:
        return "bitmap-only"
    usage = fs_type & 0x000F
    if usage == 0:
        return "installable"
    if usage & 0x8:
        return "editable"
    if usage & 0x4:
        return "preview-print"
    if usage & 0x2:
        return "restricted"
    return "unknown"


# ---------------------------------------------------------------------------
# Font programs


class FontProgram:
    """Wraps a parsed font program and produces outlines by gid or glyph name."""

    kind = "?"
    fs_type = None

    def outline_by_gid(self, gid: int):
        raise NotImplementedError

    def outline_by_name(self, name: str):
        raise NotImplementedError

    def advance_by_gid(self, gid):
        return None


class TrueTypeProgram(FontProgram):
    kind = "TrueType"

    def __init__(self, data: bytes):
        self.tt = TTFont(io.BytesIO(data), lazy=True, fontNumber=0)
        self.order = self.tt.getGlyphOrder()
        self.gs = self.tt.getGlyphSet()
        self.upem = self.tt["head"].unitsPerEm or 1000
        self.is_cff = "CFF " in self.tt or "CFF2" in self.tt
        self.kind = "OpenType-CFF" if self.is_cff else "TrueType"
        try:
            self.fs_type = int(self.tt["OS/2"].fsType) if "OS/2" in self.tt else None
        except Exception:
            self.fs_type = None
        self.cmaps = {}
        if "cmap" in self.tt:
            for t in self.tt["cmap"].tables:
                try:
                    self.cmaps.setdefault((t.platformID, t.platEncID), t.cmap)
                except Exception:
                    pass
        self.name_to_gid = {n: i for i, n in enumerate(self.order)}

    @property
    def num_glyphs(self):
        return len(self.order)

    def _draw(self, gname):
        if gname not in self.gs:
            return None
        pen = _CmdPen(self.gs, 1.0 / self.upem)
        self.gs[gname].draw(pen)
        return pen.cmds

    def outline_by_gid(self, gid):
        if 0 <= gid < len(self.order):
            return self._draw(self.order[gid])
        return None

    def outline_by_name(self, name):
        gid = self.name_to_gid.get(name)
        return None if gid is None else self.outline_by_gid(gid)

    def advance_by_gid(self, gid):
        try:
            return self.tt["hmtx"][self.order[gid]][0] / self.upem
        except Exception:
            return None

    def gid_for_unicode(self, cp: int):
        for key in ((3, 10), (3, 1), (0, 4), (0, 3), (0, 1), (0, 0)):
            m = self.cmaps.get(key)
            if m and cp in m:
                return self.name_to_gid.get(m[cp])
        return None


class CFFProgram(FontProgram):
    kind = "CFF"

    def __init__(self, data: bytes):
        cff = CFFFontSet()
        cff.decompile(io.BytesIO(data), None)
        self.font = cff[cff.fontNames[0]]
        self.cs = self.font.CharStrings
        self.order = self.font.charset
        self.cid_keyed = hasattr(self.font, "ROS")
        fm = getattr(self.font, "FontMatrix", None) or [0.001, 0, 0, 0.001, 0, 0]
        self.fm = [float(v) for v in fm]
        enc = getattr(self.font, "Encoding", None)
        self.builtin_encoding = None
        if isinstance(enc, list):
            self.builtin_encoding = [None if n in (None, ".notdef") else n for n in enc]
        elif enc == "StandardEncoding":
            self.builtin_encoding = ENCODINGS["/StandardEncoding"]

    @property
    def num_glyphs(self):
        return len(self.order)

    def _draw(self, gname):
        if gname not in self.cs:
            return None
        a, b, c, d, e, f = self.fm
        if abs(b) > 1e-12 or abs(c) > 1e-12:
            raise Unsupported("font.matrix", "skewed CFF FontMatrix")
        pen = _CmdPen(None, a, d, e, f)
        self.cs[gname].draw(pen)
        return pen.cmds

    def outline_by_gid(self, gid):
        if 0 <= gid < len(self.order):
            return self._draw(self.order[gid])
        return None

    def outline_by_name(self, name):
        return self._draw(name)

    def outline_by_cid(self, cid):
        if self.cid_keyed:
            return self._draw(f"cid{cid:05d}")
        return self.outline_by_gid(cid)


class Type1Program(FontProgram):
    kind = "Type1"

    def __init__(self, data: bytes):
        chunks = findEncryptedChunks(data)
        parts = []
        for enc, chunk in chunks:
            parts.append(deHexString(chunk) if enc and isHex(chunk[:4]) else chunk)
        t1 = T1Font.__new__(T1Font)
        t1.data = b"".join(parts)
        t1.encoding = "ascii"
        t1.parse()
        self.font = t1.font
        self.cs = self.font["CharStrings"]
        fm = self.font.get("FontMatrix", [0.001, 0, 0, 0.001, 0, 0])
        self.fm = [float(v) for v in fm]
        enc = self.font.get("Encoding")
        self.builtin_encoding = None
        if isinstance(enc, list):
            self.builtin_encoding = [None if n in (None, ".notdef") else n for n in enc]
        elif enc == "StandardEncoding":
            self.builtin_encoding = ENCODINGS["/StandardEncoding"]

    def outline_by_name(self, name):
        if name not in self.cs:
            return None
        a, b, c, d, e, f = self.fm
        pen = _CmdPen(None, a, d, e, f)
        self.cs[name].draw(pen)
        return pen.cmds

    def outline_by_gid(self, gid):
        return None


# ---------------------------------------------------------------------------
# CMaps


@dataclass
class CMap:
    codespaces: list = field(default_factory=list)  # (nbytes, lo, hi)
    ranges: list = field(default_factory=list)  # (nbytes, lo, hi, cid)
    chars: dict = field(default_factory=dict)  # (nbytes, code) -> cid
    identity: bool = False
    vertical: bool = False

    def split(self, data: bytes):
        """Yield (code, nbytes) according to the codespace ranges."""
        if self.identity and not self.codespaces:
            for i in range(0, len(data) - 1, 2):
                yield (data[i] << 8 | data[i + 1], 2)
            return
        i = 0
        n = len(data)
        spaces = self.codespaces or [(2, 0, 0xFFFF)]
        while i < n:
            matched = False
            for nb in (1, 2, 3, 4):
                if i + nb > n:
                    break
                code = int.from_bytes(data[i:i + nb], "big")
                for (snb, lo, hi) in spaces:
                    if snb == nb and lo <= code <= hi:
                        yield (code, nb)
                        i += nb
                        matched = True
                        break
                if matched:
                    break
            if not matched:
                # PDF 9.7.6.3: use the shortest codespace length, map to CID 0.
                nb = min(s[0] for s in spaces)
                yield (-1, nb)
                i += nb

    def cid(self, code, nbytes):
        if code < 0:
            return 0
        if (nbytes, code) in self.chars:
            return self.chars[(nbytes, code)]
        for (nb, lo, hi, base) in self.ranges:
            if nb == nbytes and lo <= code <= hi:
                return base + code - lo
        if self.identity:
            return code
        return 0


_HEX = re.compile(rb"<([0-9A-Fa-f\s]*)>")


def _hexval(tok: bytes):
    h = re.sub(rb"\s", b"", tok)
    return int(h, 16) if h else 0, max(1, len(h) // 2)


def parse_cmap_stream(data: bytes, budget) -> CMap:
    cm = CMap()
    if re.search(rb"/WMode\s+1", data):
        cm.vertical = True
    m = re.search(rb"/(Identity-[HV])\s+usecmap", data)
    if m:
        cm.identity = True
        cm.vertical = cm.vertical or m.group(1).endswith(b"V")
    for block in re.findall(rb"begincodespacerange(.*?)endcodespacerange", data, re.S):
        toks = _HEX.findall(block)
        for a, b in zip(toks[0::2], toks[1::2]):
            lo, nb = _hexval(a)
            hi, _ = _hexval(b)
            cm.codespaces.append((nb, lo, hi))
    for block in re.findall(rb"begincidrange(.*?)endcidrange", data, re.S):
        for a, b, c in re.findall(rb"<([0-9A-Fa-f\s]*)>\s*<([0-9A-Fa-f\s]*)>\s*(\d+)", block):
            lo, nb = _hexval(a)
            hi, _ = _hexval(b)
            cm.ranges.append((nb, lo, hi, int(c)))
    for block in re.findall(rb"begincidchar(.*?)endcidchar", data, re.S):
        for a, c in re.findall(rb"<([0-9A-Fa-f\s]*)>\s*(\d+)", block):
            code, nb = _hexval(a)
            cm.chars[(nb, code)] = int(c)
    if not cm.codespaces and not cm.identity:
        cm.codespaces = [(2, 0, 0xFFFF)]
    return cm


def parse_to_unicode(data: bytes) -> dict:
    """code -> unicode string. Only used for substitute fonts."""
    out = {}
    for block in re.findall(rb"beginbfchar(.*?)endbfchar", data, re.S):
        for a, b in re.findall(rb"<([0-9A-Fa-f\s]*)>\s*<([0-9A-Fa-f\s]*)>", block):
            code, _ = _hexval(a)
            out[code] = _utf16(b)
    for block in re.findall(rb"beginbfrange(.*?)endbfrange", data, re.S):
        for a, b, rest in re.findall(rb"<([0-9A-Fa-f\s]*)>\s*<([0-9A-Fa-f\s]*)>\s*(\[[^\]]*\]|<[0-9A-Fa-f\s]*>)",
                                     block):
            lo, _ = _hexval(a)
            hi, _ = _hexval(b)
            if hi - lo > 65535:
                continue
            if rest.startswith(b"["):
                for i, t in enumerate(_HEX.findall(rest)):
                    out[lo + i] = _utf16(t)
            else:
                base = _utf16(rest[1:-1])
                if base:
                    for i in range(hi - lo + 1):
                        out[lo + i] = base[:-1] + chr(ord(base[-1]) + i)
    return out


def _utf16(hexbytes: bytes) -> str:
    h = re.sub(rb"\s", b"", hexbytes)
    try:
        return bytes.fromhex(h.decode()).decode("utf-16-be", "replace")
    except ValueError:
        return ""


# ---------------------------------------------------------------------------
# PDF font objects


@dataclass
class DecodedGlyph:
    code: int
    key: str | None  # glyph key inside the font resource (None: nothing to draw)
    w0: float  # horizontal displacement, text space units for size 1
    w1: float  # vertical displacement (vertical writing)
    vx: float  # position vector for vertical writing
    vy: float
    is_space: bool
    t3_name: str | None = None
    x_scale: float = 1.0  # substitute fonts are stretched to the PDF widths


class PdfFont:
    def __init__(self, loader, fdict):
        self.loader = loader
        self.ctx = loader.ctx
        self.fdict = fdict
        self.subtype = str(fdict.get("/Subtype", "/Type1"))
        self.base_name = str(fdict.get("/BaseFont", "/Untitled"))[1:]
        self.is_type3 = self.subtype == "/Type3"
        self.composite = self.subtype == "/Type0"
        self.vertical = False
        self.program = None
        self.resource: FontResource | None = None
        self.unsupported_reason = None
        self.bbox = (0.0, -0.2, 1.0, 0.9)
        self.substitute = None
        self._outline_cache = {}
        self.descriptor = None
        try:
            self._init()
        except (Unsupported, CompileError) as e:
            if isinstance(e, CompileError):
                raise
            self.unsupported_reason = (e.code, e.message)
        except Exception as e:  # malformed font program
            self.unsupported_reason = ("font.parse", f"{type(e).__name__}: {e}")
        # Unsupported fonts are reported by the interpreter when text is actually shown.

    # -- setup ------------------------------------------------------------
    def _init(self):
        fd = self.fdict
        if self.is_type3:
            self._init_type3()
            return
        if self.composite:
            desc = fd["/DescendantFonts"][0]
            self.cid_font = desc
            self.cid_subtype = str(desc.get("/Subtype"))
            self.descriptor = desc.get("/FontDescriptor")
            self._init_cmap(fd.get("/Encoding"))
            self._init_cid_widths(desc)
            c2g = desc.get("/CIDToGIDMap")
            self.cid_to_gid = None
            if isinstance(c2g, pikepdf.Stream):
                data = c2g.read_bytes()
                self.cid_to_gid = data
        else:
            self.descriptor = fd.get("/FontDescriptor")
            self._init_simple_widths(fd)
        if self.descriptor is not None:
            bb = num_list(self.descriptor.get("/FontBBox", []))
            if len(bb) == 4 and bb[2] > bb[0] and bb[3] > bb[1]:
                self.bbox = tuple(v / 1000.0 for v in bb)
        self._load_program()
        if not self.composite:
            self._init_simple_encoding()

    def _init_type3(self):
        fd = self.fdict
        self.font_matrix = tuple(num_list(fd.get("/FontMatrix", [0.001, 0, 0, 0.001, 0, 0])))
        self.char_procs = fd.get("/CharProcs")
        self.t3_resources = fd.get("/Resources")
        self.first_char = int(fd.get("/FirstChar", 0))
        self.widths = num_list(fd.get("/Widths", []))
        self.encoding = self._encoding_names(fd.get("/Encoding"), [None] * 256)
        bb = num_list(fd.get("/FontBBox", []))
        if len(bb) == 4:
            from ..matrix import transform_bbox
            self.bbox = transform_bbox(self.font_matrix, bb)

    def _init_cmap(self, enc):
        if isinstance(enc, pikepdf.Name):
            nm = str(enc)
            if nm in ("/Identity-H", "/Identity-V"):
                self.cmap = CMap(identity=True, vertical=nm.endswith("V"))
            else:
                raise Unsupported("font.cmap", f"predefined CMap {nm} not bundled")
        elif isinstance(enc, pikepdf.Stream):
            data = enc.read_bytes()
            self.ctx.budget.account_decoded(len(data))
            self.cmap = parse_cmap_stream(data, self.ctx.budget)
            if "/UseCMap" in enc:
                use = enc["/UseCMap"]
                if isinstance(use, pikepdf.Name) and str(use) in ("/Identity-H", "/Identity-V"):
                    self.cmap.identity = True
                else:
                    raise Unsupported("font.cmap", "UseCMap chains are not supported")
            wm = enc.get("/WMode")
            if wm is not None and int(wm) == 1:
                self.cmap.vertical = True
        else:
            raise Unsupported("font.cmap", "missing CMap")
        self.vertical = self.cmap.vertical

    def _init_cid_widths(self, desc):
        self.dw = num(desc.get("/DW", 1000))
        self.cid_widths = {}
        self.cid_ranges = []
        w = desc.get("/W")
        if w is not None:
            items = list(w)
            i = 0
            while i < len(items):
                first = int(items[i])
                nxt = items[i + 1] if i + 1 < len(items) else None
                if isinstance(nxt, pikepdf.Array):
                    for j, v in enumerate(nxt):
                        self.cid_widths[first + j] = num(v)
                    i += 2
                else:
                    if i + 2 >= len(items):
                        break
                    self.cid_ranges.append((first, int(items[i + 1]), num(items[i + 2])))
                    i += 3
        dw2 = num_list(desc.get("/DW2", [880, -1000]))
        self.dw2 = dw2 if len(dw2) == 2 else [880, -1000]
        self.cid_w2 = {}
        w2 = desc.get("/W2")
        if w2 is not None:
            items = list(w2)
            i = 0
            while i < len(items):
                first = int(items[i])
                nxt = items[i + 1] if i + 1 < len(items) else None
                if isinstance(nxt, pikepdf.Array):
                    vals = num_list(nxt)
                    for j in range(0, len(vals) - 2, 3):
                        self.cid_w2[first + j // 3] = (vals[j], vals[j + 1], vals[j + 2])
                    i += 2
                else:
                    if i + 4 >= len(items):
                        break
                    last = int(items[i + 1])
                    v = (num(items[i + 2]), num(items[i + 3]), num(items[i + 4]))
                    for c in range(first, min(last, first + 65535) + 1):
                        self.cid_w2[c] = v
                    i += 5

    def _init_simple_widths(self, fd):
        self.first_char = int(fd.get("/FirstChar", 0))
        self.widths = num_list(fd.get("/Widths", []))
        self.missing_width = num(self.descriptor.get("/MissingWidth", 0)) if self.descriptor is not None else 0.0
        self.has_widths = bool(self.widths)

    def _font_file(self):
        d = self.descriptor
        if d is None:
            return None, None
        for key in ("/FontFile2", "/FontFile3", "/FontFile"):
            s = d.get(key)
            if isinstance(s, pikepdf.Stream):
                sub = str(s.get("/Subtype", "")) if key == "/FontFile3" else ""
                return key + sub, s
        return None, None

    def _load_program(self):
        kind, stream = self._font_file()
        if stream is None:
            self._load_substitute()
            return
        data = stream.read_bytes()
        self.ctx.budget.account_decoded(len(data))
        self.program_hash = sha256(data)
        cached = self.loader.programs.get(self.program_hash)
        if cached is None:
            if kind == "/FontFile2" or kind == "/FontFile3/OpenType":
                cached = TrueTypeProgram(data)
            elif kind in ("/FontFile3/Type1C", "/FontFile3/CIDFontType0C"):
                cached = CFFProgram(data)
            elif kind == "/FontFile":
                cached = Type1Program(data)
            else:
                raise Unsupported("font.program", f"font file {kind} not supported")
            self.loader.programs[self.program_hash] = cached
        self.program = cached
        status = _license_status(cached.fs_type)
        ref = "f-" + self.program_hash[:20]
        self.resource = self.loader.register(ref, self.base_name, self.subtype, True, self.program_hash,
                                             {"fsType": cached.fs_type, "status": status,
                                              "program": cached.kind})
        self._check_license(status)

    def _check_license(self, status):
        policy = self.ctx.options.font_rights
        if policy == "declared":
            return
        if status in ("restricted", "bitmap-only"):
            sub = self.loader.find_substitute(self.base_name, self.descriptor)
            if sub:
                self.ctx.diag.warn("font.license-substituted",
                                   f"font {self.base_name} has restricted embedding permission; "
                                   f"replaced by substitute {os.path.basename(sub)} (quality may degrade)")
                self.program = None
                self.resource = None
                self._load_substitute(force_path=sub)
                return
            raise CompileError("font.license-restricted",
                               f"embedded font {self.base_name} prohibits outline use (fsType={status}). "
                               f"Provide a licensed substitute font or declare rights with --font-rights declared")
        if status != "installable":
            self.ctx.diag.warn("font.license-review",
                               f"font {self.base_name}: embedding permission '{status}'; confirm the right to "
                               f"redistribute derived glyph geometry")
            self.ctx.license_review = True

    def _load_substitute(self, force_path=None):
        path = force_path or self.loader.find_substitute(self.base_name, self.descriptor)
        if not path:
            raise Unsupported("font.not-embedded", "font program not embedded and no substitute configured")
        prog = self.loader.programs.get(path)
        if prog is None:
            with open(path, "rb") as f:
                prog = TrueTypeProgram(f.read())
            self.loader.programs[path] = prog
        self.program = prog
        self.substitute = path
        ref = "f-sub-" + sha256((path + "|" + self.base_name).encode("utf-8"))[:16]
        self.resource = self.loader.register(ref, self.base_name, self.subtype, False, None,
                                             {"fsType": prog.fs_type, "status": "user-supplied-substitute",
                                              "program": prog.kind})
        self.resource.substitute = os.path.basename(path)
        self.ctx.diag.warn("font.substituted", f"font {self.base_name} replaced by substitute "
                           f"{os.path.basename(path)}; glyph shapes differ from the original")
        if not self.composite and not self.fdict.get("/Widths"):
            self.ctx.diag.warn("font.metrics-missing",
                               f"font {self.base_name} has no /Widths; glyph advances of the substitute are used, "
                               f"so line lengths can differ from the original")
        tu = self.fdict.get("/ToUnicode")
        self.to_unicode = {}
        if isinstance(tu, pikepdf.Stream):
            self.to_unicode = parse_to_unicode(tu.read_bytes())
        if self.composite and not self.to_unicode:
            raise Unsupported("font.not-embedded", "non-embedded CID font without ToUnicode cannot be substituted")

    def _encoding_names(self, enc, base):
        names = list(base)
        if isinstance(enc, pikepdf.Name):
            names = list(ENCODINGS.get(str(enc), base))
        elif isinstance(enc, pikepdf.Dictionary):
            be = enc.get("/BaseEncoding")
            if be is not None and str(be) in ENCODINGS:
                names = list(ENCODINGS[str(be)])
            diffs = enc.get("/Differences")
            if diffs is not None:
                code = 0
                for item in diffs:
                    if isinstance(item, pikepdf.Name):
                        if 0 <= code < 256:
                            names[code] = str(item)[1:]
                        code += 1
                    else:
                        code = int(item)
        return names

    def _init_simple_encoding(self):
        flags = int(self.descriptor.get("/Flags", 0)) if self.descriptor is not None else 0
        self.symbolic = bool(flags & 4)
        enc = self.fdict.get("/Encoding")
        prog = self.program
        builtin = getattr(prog, "builtin_encoding", None)
        if isinstance(prog, TrueTypeProgram) and not prog.is_cff:
            base = ENCODINGS["/StandardEncoding"] if not self.symbolic else [None] * 256
        else:
            base = builtin or ENCODINGS["/StandardEncoding"]
        self.encoding = self._encoding_names(enc, base)
        self.has_differences = isinstance(enc, pikepdf.Dictionary) and "/Differences" in enc
        self.explicit_encoding = enc is not None

    # -- decoding -----------------------------------------------------------
    def decode(self, data: bytes):
        if self.composite:
            yield from self._decode_composite(data)
        else:
            for b in data:
                yield self._decode_simple(b)

    def _simple_width(self, code):
        i = code - self.first_char
        if 0 <= i < len(self.widths):
            return self.widths[i] / 1000.0
        return None

    def _decode_simple(self, code):
        if self.is_type3:
            i = code - self.first_char
            w = self.widths[i] if 0 <= i < len(self.widths) else 0.0
            nm = self.encoding[code]
            # Widths are in glyph space; FontMatrix maps them to text space.
            return DecodedGlyph(code, None, w * self.font_matrix[0], 0, 0, 0, code == 32, t3_name=nm)
        key = self._simple_key(code)
        w = self._simple_width(code)
        x_scale = 1.0
        if w is None:
            w = self.missing_width / 1000.0 if self.missing_width else None
        adv = self._program_advance(key)
        if w is None:
            if adv is not None:
                w = adv
            elif self.program is None:
                # Unknown metrics of a font that will be precomposited: over-estimate
                # (1 em per glyph) so the raster region safely covers the text.
                w = 1.0
            else:
                w = 0.0
        elif self.substitute and adv:
            x_scale = w / adv if w > 0 else 1.0
        return DecodedGlyph(code, key, w, 0, 0, 0, code == 32, x_scale=x_scale)

    def _program_advance(self, key):
        if key is None or self.program is None:
            return None
        if key.startswith("g") and isinstance(self.program, TrueTypeProgram):
            return self.program.advance_by_gid(int(key[1:]))
        return None

    def _simple_key(self, code):
        prog = self.program
        if prog is None:
            return None
        name = self.encoding[code] if 0 <= code < 256 else None
        if isinstance(prog, TrueTypeProgram):
            if self.substitute:
                u = None
                if code in getattr(self, "to_unicode", {}):
                    u = self.to_unicode[code]
                elif name:
                    u = glyph_name_to_unicode(name)
                gid = prog.gid_for_unicode(ord(u[0])) if u else None
                return f"g{gid}" if gid is not None else None
            if prog.is_cff:
                if name and name in prog.name_to_gid:
                    return f"g{prog.name_to_gid[name]}"
            if self.has_differences and name:
                if name in prog.name_to_gid and name != ".notdef":
                    return f"g{prog.name_to_gid[name]}"
            if (self.symbolic or not self.explicit_encoding) and (3, 0) in prog.cmaps:
                m = prog.cmaps[(3, 0)]
                for c in (code, 0xF000 | code, 0xF100 | code, 0xF200 | code):
                    if c in m:
                        return f"g{prog.name_to_gid[m[c]]}"
            if name and not self.symbolic:
                u = glyph_name_to_unicode(name)
                if u:
                    gid = prog.gid_for_unicode(ord(u[0]))
                    if gid is not None:
                        return f"g{gid}"
                if name in prog.name_to_gid:
                    return f"g{prog.name_to_gid[name]}"
            if (1, 0) in prog.cmaps and code in prog.cmaps[(1, 0)]:
                return f"g{prog.name_to_gid[prog.cmaps[(1, 0)][code]]}"
            if (3, 0) in prog.cmaps:
                m = prog.cmaps[(3, 0)]
                for c in (code, 0xF000 | code):
                    if c in m:
                        return f"g{prog.name_to_gid[m[c]]}"
            if code < prog.num_glyphs and not prog.cmaps:
                return f"g{code}"
            return None
        if name is None:
            return None
        return f"n{name}"

    def _decode_composite(self, data):
        cm = self.cmap
        for code, nb in cm.split(data):
            cid = cm.cid(code, nb)
            w = self.cid_widths.get(cid)
            if w is None:
                w = self.dw
                for (a, b, v) in self.cid_ranges:
                    if a <= cid <= b:
                        w = v
                        break
            key = self._cid_key(cid, code)
            is_space = nb == 1 and code == 32
            if self.vertical:
                w1y, vx, vy = self.cid_w2.get(cid, (self.dw2[1], w / 2, self.dw2[0]))
                yield DecodedGlyph(code, key, w / 1000.0, w1y / 1000.0, vx / 1000.0, vy / 1000.0, is_space)
            else:
                x_scale = 1.0
                if self.substitute and key:
                    adv = self._program_advance(key)
                    if adv:
                        x_scale = (w / 1000.0) / adv
                yield DecodedGlyph(code, key, w / 1000.0, 0, 0, 0, is_space, x_scale=x_scale)

    def _cid_key(self, cid, code):
        prog = self.program
        if prog is None:
            return None
        if self.substitute:
            u = self.to_unicode.get(code)
            gid = prog.gid_for_unicode(ord(u[0])) if u else None
            return f"g{gid}" if gid is not None else None
        if isinstance(prog, CFFProgram):
            return f"c{cid}" if prog.cid_keyed else f"g{cid}"
        gid = cid
        if self.cid_to_gid is not None:
            i = cid * 2
            gid = (self.cid_to_gid[i] << 8 | self.cid_to_gid[i + 1]) if i + 1 < len(self.cid_to_gid) else 0
        if isinstance(prog, TrueTypeProgram) and prog.is_cff and gid >= prog.num_glyphs:
            return None
        return f"g{gid}"

    # -- outlines -----------------------------------------------------------
    def outline(self, key):
        """Return IR path commands for `key` (text space, size 1); cached in the resource."""
        if key is None or self.resource is None:
            return None
        glyphs = self.resource.glyphs
        if key in glyphs:
            return glyphs[key]
        if key in self._outline_cache:
            return self._outline_cache[key]
        prog = self.program
        cmds = None
        try:
            if key[0] == "g":
                cmds = prog.outline_by_gid(int(key[1:]))
            elif key[0] == "c":
                cmds = prog.outline_by_cid(int(key[1:]))
            elif key[0] == "n":
                cmds = prog.outline_by_name(key[1:])
        except Unsupported:
            raise
        except Exception as e:
            self.ctx.diag.warn("font.glyph", f"font {self.base_name}: glyph {key} unreadable ({e})")
            cmds = None
        cmds = cmds or []
        self._outline_cache[key] = cmds
        glyphs[key] = cmds
        return cmds


class FontLoader:
    def __init__(self, ctx):
        self.ctx = ctx
        self.fonts = {}
        self.programs = {}
        self.resources = ctx.ir.fonts
        self.substitutes = self._load_substitutes(ctx.options.substitute_fonts)

    def load(self, fdict) -> PdfFont:
        key = objkey(fdict)
        f = self.fonts.get(key)
        if f is None:
            f = PdfFont(self, fdict)
            self.fonts[key] = f
        return f

    def register(self, ref, name, subtype, embedded, program_hash, license_):
        res = self.resources.get(ref)
        if res is None:
            res = FontResource(ref=ref, name=name, subtype=subtype, embedded=embedded,
                               program_hash=program_hash, license=license_)
            self.resources[ref] = res
        return res

    # Substitute fonts are configured by the user (licensed alternatives).
    @staticmethod
    def _load_substitutes(spec):
        if not spec:
            return []
        if os.path.isfile(spec) and spec.lower().endswith(".json"):
            with open(spec, "r", encoding="utf-8") as f:
                cfg = json.load(f)
            base = os.path.dirname(os.path.abspath(spec))
            out = []
            for rule in cfg.get("substitutes", []):
                p = rule["font"]
                if not os.path.isabs(p):
                    p = os.path.join(base, p)
                out.append((re.compile(rule.get("match", ".*"), re.I), rule.get("class"), p))
            return out
        if os.path.isdir(spec):
            out = []
            for fn in sorted(os.listdir(spec)):
                if fn.lower().endswith((".ttf", ".otf")):
                    stem = os.path.splitext(fn)[0]
                    out.append((re.compile(re.escape(stem), re.I), None, os.path.join(spec, fn)))
            return out
        raise CompileError("options.substitutes", f"substitute font spec {spec} not found")

    def find_substitute(self, base_name, descriptor):
        if not self.substitutes:
            return None
        name = re.sub(r"^[A-Z]{6}\+", "", base_name)
        flags = int(descriptor.get("/Flags", 0)) if descriptor is not None else 0
        cls = "serif" if flags & 2 else "mono" if flags & 1 else "sans"
        low = name.lower()
        if any(k in low for k in ("times", "mincho", "serif", "roman", "georgia", "minchō", "明朝")):
            cls = "serif"
        elif "courier" in low or "mono" in low:
            cls = "mono"
        for rx, rcls, path in self.substitutes:
            if rcls is None and rx.search(name):
                return path
        for rx, rcls, path in self.substitutes:
            if rcls is not None and rcls == cls and rx.search(name):
                return path
        for rx, rcls, path in self.substitutes:
            if rcls == cls and rx.pattern in (".*", ""):
                return path
        return None


def font_debug_name(fdict) -> str:
    return f"{fdict.get('/BaseFont', '?')} ({objid(fdict)})"
