#!/usr/bin/env python3
"""Guard: form accessibility in .razor files (SPEC-92).

Checks, per file:
1. Every `for="X"` resolves to an `id="X"` in the same file.
2. No duplicate `id="..."` literals in a file.
3. Every <input>, <select>, <textarea> has an accessible name source:
   - aria-label / aria-labelledby, or
   - id referenced by a `for=`, or
   - wrapped inside a <label> element, or
   - type="hidden".
   Every <button> has aria-label/aria-labelledby/title or non-empty
   inner text.
4. No <label for> / id pair where the id appears inside a @foreach/@for
   loop while the label is outside it (duplicate-id risk).

Exits non-zero and prints violations.
"""
import re
import sys
import glob
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import _drift  # noqa: E402

NAME = "check-form-a11y"

FORM_TAGS = re.compile(r'<(input|select|textarea)\b')
BUTTON_TAG = re.compile(r'<button\b')
ATTR = re.compile(r'(\w[\w-]*)\s*=\s*"([^"]*)"')
LABEL_OPEN = re.compile(r'<label\b[^>]*>')
LOOP_OPEN = re.compile(r'@(?:foreach|for)\b[^{]*\{')


def tag_attrs(src, start):
    """Return (attrs_str, end_index_of_'>'), quote-aware."""
    i = start
    q = None
    while i < len(src):
        c = src[i]
        if q:
            if c == q:
                q = None
        elif c in '"\'':
            q = c
        elif c == '>':
            return src[start:i], i
        i += 1
    return src[start:], len(src)


def brace_spans(src, start):
    """Given index of '{', return (start,end) span of balanced braces."""
    depth = 0
    i = start
    q = None
    while i < len(src):
        c = src[i]
        if q:
            if c == q:
                q = None
        elif c in '"\'':
            q = c
        elif c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0:
                return (start, i)
        i += 1
    return (start, len(src))


def loop_spans(src):
    spans = []
    for m in LOOP_OPEN.finditer(src):
        b = src.find('{', m.end() - 1)
        if b != -1:
            spans.append(brace_spans(src, b))
    return spans


def in_spans(pos, spans):
    return any(a <= pos <= b for a, b in spans)


def label_ranges(src):
    """Return list of (start,end) ranges covering <label ..>...</label>."""
    ranges = []
    stack = []
    for m in re.finditer(r'<label\b[^>]*>|</label>', src):
        if m.group(0).startswith('</'):
            if stack:
                ranges.append((stack.pop(), m.end()))
        else:
            stack.append(m.start())
    return ranges


def button_has_text(src, attrs_end):
    """Non-empty inner content before </button> (ignoring markup-only
    children like <svg>/<i>)."""
    close = src.find('</button>', attrs_end)
    if close == -1:
        return False
    inner = src[attrs_end + 1:close]
    # remove child tags entirely; leftover text = accessible name
    text = re.sub(r'<[^>]+>', '', inner).strip()
    text = re.sub(r'@\w+[({]', '', text).strip()
    return len(text) > 0


def check_controls(f: str, src: str, fors: list[str],
                   lranges: list[tuple[int, int]],
                   errors: list[tuple[str, str]]) -> None:
    """Check 3 — inputs/selects/textareas com nome acessível."""
    for m in FORM_TAGS.finditer(src):
        attrs, _ = tag_attrs(src, m.end())
        a = dict(ATTR.findall(attrs))
        if a.get('type') == 'hidden':
            continue
        if 'aria-label' in a or 'aria-labelledby' in a:
            continue
        cid = a.get('id')
        if cid and cid in fors:
            continue
        if in_spans(m.start(), lranges):
            continue
        line = src[:m.start()].count('\n') + 1
        errors.append((f"{f}:unnamed-<{m.group(1)}>",
                       f"{f}:{line}: <{m.group(1)}> without "
                       "accessible name"))


def check_buttons(f: str, src: str,
                  errors: list[tuple[str, str]]) -> None:
    """Check 3b — buttons precisam de nome: aria-label/labelledby/
    title ou texto."""
    for m in BUTTON_TAG.finditer(src):
        attrs, attrs_end = tag_attrs(src, m.end())
        a = dict(ATTR.findall(attrs))
        if 'aria-label' in a or 'aria-labelledby' in a or 'title' in a:
            continue
        if button_has_text(src, attrs_end):
            continue
        line = src[:m.start()].count('\n') + 1
        errors.append((f"{f}:unnamed-<button>",
                       f"{f}:{line}: <button> without accessible name"))


def check_loop_ids(f: str, src: str, spans: list[tuple[int, int]],
                   errors: list[tuple[str, str]]) -> None:
    """Check 4 — id dentro de loop referenciado por label fora."""
    for m in re.finditer(r'\bid="([A-Za-z0-9_-]+)"', src):
        if in_spans(m.start(), spans):
            for fm in re.finditer(
                    r'\bfor="' + re.escape(m.group(1)) + r'"', src):
                if not in_spans(fm.start(), spans):
                    line = src[:m.start()].count('\n') + 1
                    errors.append((f"{f}:loop-id={m.group(1)}",
                                   f"{f}:{line}: id \"{m.group(1)}\" "
                                   "inside loop referenced by label "
                                   "outside loop"))


def check_file(f: str, errors: list[tuple[str, str]]) -> None:
    """Roda os 4 checks de a11y num .razor e acumula violações."""
    src = open(f, encoding='utf-8').read()
    ids = re.findall(r'\bid="([A-Za-z0-9_-]+)"', src)
    fors = re.findall(r'\bfor="([A-Za-z0-9_-]+)"', src)
    spans = loop_spans(src)
    lranges = label_ranges(src)

    # 1. for -> id
    for fr in fors:
        if fr not in ids:
            errors.append((f"{f}:for={fr}",
                           f"{f}: for=\"{fr}\" has no matching id"))

    # 2. duplicate ids (literal duplicates only)
    seen = {}
    for i in ids:
        seen[i] = seen.get(i, 0) + 1
    for i, c in seen.items():
        if c > 1:
            errors.append((f"{f}:dup-id={i}",
                           f"{f}: duplicate id \"{i}\" x{c}"))

    check_controls(f, src, fors, lranges, errors)
    check_buttons(f, src, errors)
    check_loop_ids(f, src, spans, errors)


def main():
    # violations: (signature livre-de-linha, display arquivo:linha)
    errors: list[tuple[str, str]] = []
    files = sorted(glob.glob(
        'src/OpenHandsNET.Client/**/*.razor', recursive=True))
    for f in files:
        check_file(f, errors)

    signatures = [sig for sig, _ in errors]
    if _drift.maybe_write_baseline(NAME, signatures):
        return
    new, stale = _drift.diff(NAME, signatures)
    if new:
        print("Form a11y violations:")
        for sig, disp in errors:
            if new[sig] > 0:
                new[sig] -= 1
                print("  -", disp)
        _drift.report(NAME, Counter(), stale)  # new já impresso com linhas
        sys.exit(1)
    _drift.report(NAME, new, stale)
    print(f"OK: {len(files)} razor files checked")


if __name__ == '__main__':
    main()
