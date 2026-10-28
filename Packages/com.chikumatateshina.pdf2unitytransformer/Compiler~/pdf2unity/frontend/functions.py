"""PDF functions (ISO 32000-1 §7.10): sampled (0), exponential (2), stitching (3),
PostScript calculator (4). Used by tint transforms and shadings."""

from __future__ import annotations

import math

import pikepdf

from ..diagnostics import Unsupported
from .pdfutil import num, num_list


def _clip(v, lo, hi):
    return lo if v < lo else hi if v > hi else v


class PdfFunction:
    def __init__(self, domain, range_):
        self.domain = domain
        self.range = range_

    @property
    def n_in(self):
        return len(self.domain) // 2

    def _clip_in(self, xs):
        return [_clip(x, self.domain[2 * i], self.domain[2 * i + 1]) for i, x in enumerate(xs)]

    def _clip_out(self, ys):
        if not self.range:
            return ys
        return [_clip(y, self.range[2 * i], self.range[2 * i + 1]) for i, y in enumerate(ys)]

    def __call__(self, xs):
        return self._clip_out(self.evaluate(self._clip_in(list(xs))))

    def evaluate(self, xs):  # pragma: no cover - abstract
        raise NotImplementedError


class ExponentialFunction(PdfFunction):
    def __init__(self, d, domain, range_):
        super().__init__(domain, range_)
        self.c0 = num_list(d.get("/C0", [0.0]))
        self.c1 = num_list(d.get("/C1", [1.0]))
        self.n = num(d.get("/N", 1.0))

    def evaluate(self, xs):
        x = xs[0]
        try:
            xn = x ** self.n
        except (ValueError, ZeroDivisionError):
            xn = 0.0
        if isinstance(xn, complex):
            xn = 0.0
        return [a + xn * (b - a) for a, b in zip(self.c0, self.c1)]


class StitchingFunction(PdfFunction):
    def __init__(self, d, domain, range_, loader):
        super().__init__(domain, range_)
        self.functions = [loader(f) for f in d["/Functions"]]
        self.bounds = num_list(d.get("/Bounds", []))
        self.encode = num_list(d.get("/Encode", []))

    def evaluate(self, xs):
        x = xs[0]
        d0, d1 = self.domain[0], self.domain[1]
        k = len(self.functions)
        i = 0
        while i < k - 1 and x >= self.bounds[i]:
            i += 1
        lo = d0 if i == 0 else self.bounds[i - 1]
        hi = d1 if i == k - 1 else self.bounds[i]
        e0, e1 = self.encode[2 * i], self.encode[2 * i + 1]
        t = e0 if hi == lo else e0 + (x - lo) * (e1 - e0) / (hi - lo)
        return self.functions[i]([t])


class SampledFunction(PdfFunction):
    def __init__(self, stream, domain, range_, budget):
        super().__init__(domain, range_)
        self.size = [int(v) for v in stream["/Size"]]
        self.bps = int(stream["/BitsPerSample"])
        m = len(self.size)
        self.encode = num_list(stream.get("/Encode", [v for s in self.size for v in (0, s - 1)]))
        self.decode = num_list(stream.get("/Decode", range_))
        self.n_out = len(range_) // 2
        data = stream.read_bytes()
        budget.account_decoded(len(data))
        total = self.n_out
        for s in self.size:
            total *= s
        self.samples = self._unpack(data, total)
        self.strides = []
        acc = self.n_out
        for s in self.size:
            self.strides.append(acc)
            acc *= s
        self.m = m

    def _unpack(self, data, count):
        bps = self.bps
        out = []
        if bps == 8:
            out = list(data[:count])
        elif bps == 16:
            out = [(data[2 * i] << 8) | data[2 * i + 1] for i in range(min(count, len(data) // 2))]
        else:
            acc = 0
            nbits = 0
            mask = (1 << bps) - 1
            it = iter(data)
            while len(out) < count:
                while nbits < bps:
                    try:
                        acc = (acc << 8) | next(it)
                    except StopIteration:
                        acc <<= 8
                    nbits += 8
                nbits -= bps
                out.append((acc >> nbits) & mask)
        if len(out) < count:
            out.extend([0] * (count - len(out)))
        return out

    def evaluate(self, xs):
        maxv = (1 << self.bps) - 1
        # Multilinear interpolation over the m input dimensions.
        coords = []
        for i, x in enumerate(xs):
            d0, d1 = self.domain[2 * i], self.domain[2 * i + 1]
            e0, e1 = self.encode[2 * i], self.encode[2 * i + 1]
            e = e0 if d1 == d0 else e0 + (x - d0) * (e1 - e0) / (d1 - d0)
            e = _clip(e, 0, self.size[i] - 1)
            i0 = int(math.floor(e))
            i1 = min(i0 + 1, self.size[i] - 1)
            coords.append((i0, i1, e - i0))
        out = [0.0] * self.n_out
        for corner in range(1 << self.m):
            w = 1.0
            offset = 0
            for i, (i0, i1, f) in enumerate(coords):
                if corner >> i & 1:
                    w *= f
                    offset += i1 * self.strides[i]
                else:
                    w *= 1 - f
                    offset += i0 * self.strides[i]
            if w == 0:
                continue
            for j in range(self.n_out):
                out[j] += w * self.samples[offset + j]
        res = []
        for j in range(self.n_out):
            dlo, dhi = self.decode[2 * j], self.decode[2 * j + 1]
            res.append(dlo + out[j] * (dhi - dlo) / maxv)
        return res


# ---- Type 4 PostScript calculator -------------------------------------------

_PS_OPS = {
    "abs", "add", "atan", "ceiling", "cos", "cvi", "cvr", "div", "exp", "floor", "idiv", "ln",
    "log", "mod", "mul", "neg", "round", "sin", "sqrt", "sub", "truncate", "and", "bitshift",
    "eq", "false", "ge", "gt", "le", "lt", "ne", "not", "or", "true", "xor", "copy", "dup",
    "exch", "index", "pop", "roll", "if", "ifelse",
}


def _ps_tokenize(src: str):
    tokens = []
    i = 0
    n = len(src)
    while i < n:
        ch = src[i]
        if ch in " \t\r\n\f\0":
            i += 1
        elif ch == "%":
            while i < n and src[i] not in "\r\n":
                i += 1
        elif ch in "{}":
            tokens.append(ch)
            i += 1
        else:
            j = i
            while j < n and src[j] not in " \t\r\n\f\0{}%":
                j += 1
            tokens.append(src[i:j])
            i = j
    return tokens


def _ps_parse(tokens, pos):
    """Parse a `{ ... }` procedure starting after its `{`."""
    proc = []
    while pos < len(tokens):
        t = tokens[pos]
        if t == "{":
            sub, pos = _ps_parse(tokens, pos + 1)
            proc.append(sub)
            continue
        if t == "}":
            return proc, pos + 1
        if t in _PS_OPS:
            proc.append(("op", t))
        else:
            try:
                proc.append(float(t) if any(c in t for c in ".eE") else int(t))
            except ValueError:
                raise Unsupported("function.ps", f"unknown PostScript token {t!r}")
        pos += 1
    raise Unsupported("function.ps", "unterminated PostScript procedure")


class PostScriptFunction(PdfFunction):
    MAX_STEPS = 100_000

    def __init__(self, stream, domain, range_, budget):
        super().__init__(domain, range_)
        data = stream.read_bytes()
        budget.account_decoded(len(data))
        tokens = _ps_tokenize(data.decode("latin-1"))
        if not tokens or tokens[0] != "{":
            raise Unsupported("function.ps", "PostScript function must start with '{'")
        self.proc, _ = _ps_parse(tokens, 1)

    def evaluate(self, xs):
        stack = list(xs)
        self._steps = 0
        self._run(self.proc, stack)
        n_out = len(self.range) // 2
        return [float(v) for v in stack[-n_out:]] if n_out else [float(v) for v in stack]

    def _run(self, proc, st):
        for item in proc:
            self._steps += 1
            if self._steps > self.MAX_STEPS:
                raise Unsupported("function.ps", "PostScript function step limit exceeded")
            if isinstance(item, list):
                st.append(item)
                continue
            if not isinstance(item, tuple):
                st.append(item)
                continue
            op = item[1]
            if op == "if":
                p = st.pop()
                if st.pop():
                    self._run(p, st)
            elif op == "ifelse":
                p2 = st.pop()
                p1 = st.pop()
                self._run(p1 if st.pop() else p2, st)
            else:
                _ps_exec(op, st)


def _ps_exec(op, st):
    if op == "add":
        b = st.pop(); st.append(st.pop() + b)
    elif op == "sub":
        b = st.pop(); st.append(st.pop() - b)
    elif op == "mul":
        b = st.pop(); st.append(st.pop() * b)
    elif op == "div":
        b = st.pop(); a = st.pop(); st.append(a / b if b else 0.0)
    elif op == "idiv":
        b = int(st.pop()); a = int(st.pop()); st.append(int(a / b) if b else 0)
    elif op == "mod":
        b = int(st.pop()); a = int(st.pop()); st.append(int(math.fmod(a, b)) if b else 0)
    elif op == "neg":
        st.append(-st.pop())
    elif op == "abs":
        st.append(abs(st.pop()))
    elif op == "sqrt":
        st.append(math.sqrt(max(0.0, st.pop())))
    elif op == "sin":
        st.append(math.sin(math.radians(st.pop())))
    elif op == "cos":
        st.append(math.cos(math.radians(st.pop())))
    elif op == "atan":
        b = st.pop(); a = st.pop(); st.append(math.degrees(math.atan2(a, b)) % 360)
    elif op == "exp":
        b = st.pop(); a = st.pop()
        try:
            r = a ** b
            st.append(0.0 if isinstance(r, complex) else r)
        except (ZeroDivisionError, OverflowError):
            st.append(0.0)
    elif op == "ln":
        v = st.pop(); st.append(math.log(v) if v > 0 else -1e30)
    elif op == "log":
        v = st.pop(); st.append(math.log10(v) if v > 0 else -1e30)
    elif op == "floor":
        st.append(math.floor(st.pop()))
    elif op == "ceiling":
        st.append(math.ceil(st.pop()))
    elif op == "round":
        st.append(math.floor(st.pop() + 0.5))
    elif op == "truncate":
        st.append(math.trunc(st.pop()))
    elif op == "cvi":
        st.append(int(st.pop()))
    elif op == "cvr":
        st.append(float(st.pop()))
    elif op in ("eq", "ne", "gt", "ge", "lt", "le"):
        b = st.pop(); a = st.pop()
        st.append({"eq": a == b, "ne": a != b, "gt": a > b, "ge": a >= b, "lt": a < b, "le": a <= b}[op])
    elif op == "and":
        b = st.pop(); a = st.pop(); st.append(a and b if isinstance(a, bool) else int(a) & int(b))
    elif op == "or":
        b = st.pop(); a = st.pop(); st.append(a or b if isinstance(a, bool) else int(a) | int(b))
    elif op == "xor":
        b = st.pop(); a = st.pop(); st.append(a != b if isinstance(a, bool) else int(a) ^ int(b))
    elif op == "not":
        a = st.pop(); st.append((not a) if isinstance(a, bool) else ~int(a))
    elif op == "bitshift":
        s = int(st.pop()); a = int(st.pop()); st.append(a << s if s >= 0 else a >> -s)
    elif op == "true":
        st.append(True)
    elif op == "false":
        st.append(False)
    elif op == "dup":
        st.append(st[-1])
    elif op == "pop":
        st.pop()
    elif op == "exch":
        st[-1], st[-2] = st[-2], st[-1]
    elif op == "copy":
        n = int(st.pop())
        if n > 0:
            st.extend(st[-n:])
    elif op == "index":
        n = int(st.pop()); st.append(st[-1 - n])
    elif op == "roll":
        j = int(st.pop()); n = int(st.pop())
        if n > 0:
            j %= n
            seg = st[-n:]
            st[-n:] = seg[-j:] + seg[:-j] if j else seg
    else:
        raise Unsupported("function.ps", f"PostScript operator {op} not supported")


class ArrayFunction:
    """An array of 1-in/1-out functions treated as one n-out function."""

    def __init__(self, funcs):
        self.funcs = funcs

    def __call__(self, xs):
        return [f(xs)[0] for f in self.funcs]


def load_function(obj, budget):
    if isinstance(obj, pikepdf.Array):
        return ArrayFunction([load_function(f, budget) for f in obj])
    ftype = int(obj.get("/FunctionType", -1))
    domain = num_list(obj.get("/Domain", [0, 1]))
    range_ = num_list(obj.get("/Range", []))
    if ftype == 0:
        return SampledFunction(obj, domain, range_, budget)
    if ftype == 2:
        return ExponentialFunction(obj, domain, range_)
    if ftype == 3:
        return StitchingFunction(obj, domain, range_, lambda f: load_function(f, budget))
    if ftype == 4:
        return PostScriptFunction(obj, domain, range_, budget)
    raise Unsupported("function.type", f"function type {ftype} not supported")
