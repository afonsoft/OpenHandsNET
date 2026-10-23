#!/usr/bin/env python3
"""Drift guard: fail if a class used in .razor markup has no rule in the
generated Tailwind CSS or the authored app.css (SPEC-92).

Usage: python3 tools/check-css-classes.py
Exit 0 when every used class is defined (utility or component layer),
exit 1 listing the used-but-undefined classes otherwise.
"""

from __future__ import annotations

import glob
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import _drift  # noqa: E402

NAME = "check-css-classes"
ROOT = Path(__file__).resolve().parent.parent
CLIENT = ROOT / "src" / "OpenHandsNET.Client"
CSS_SOURCES = [
    CLIENT / "wwwroot" / "tailwind.css",  # generated
    CLIENT / "wwwroot" / "css" / "app.css",  # authored component layer
]

# Class tokens emitted at runtime by JS/CSS rather than authored
# utilities (kept small on purpose).
ALLOWLIST = {
    "dark",  # <html data-theme> toggled by JS
}

CLASS_ATTR = re.compile(r'class\s*=\s*"([^"@]*)"|class\s*=\s*\'([^\']*)\'')
TOKEN_OK = re.compile(r"^[A-Za-z0-9_\-./%\[\]()#@!,]+$")
SELECTOR_CLASS = re.compile(r"\.(\\.|[^\\.\s,{>+~:*#()\[\]])+")


def used_tokens() -> set[str]:
    tokens: set[str] = set()
    for path in glob.glob(str(CLIENT / "**" / "*.razor"), recursive=True):
        text = Path(path).read_text(encoding="utf-8")
        for match in CLASS_ATTR.finditer(text):
            for token in (match.group(1) or match.group(2) or "").split():
                if TOKEN_OK.match(token) and not token.startswith("@"):
                    tokens.add(token)
    return tokens


def defined_tokens(css: str) -> set[str]:
    """Class names that resolve to a selector in the CSS."""
    defined: set[str] = set()
    for match in SELECTOR_CLASS.finditer(css):
        name = re.sub(r"\\(.)", r"\1", match.group(0))[1:]
        base = name.split(":")[-1].lstrip("!")
        if base:
            defined.add(base)
    return defined


def main() -> int:
    defined: set[str] = set()
    missing_sources = [
        str(src) for src in CSS_SOURCES if not src.exists()]
    if missing_sources:
        print(f"error: CSS sources not found: {missing_sources} "
              "— run the Tailwind build first")
        return 2
    for src in CSS_SOURCES:
        defined |= defined_tokens(src.read_text(encoding="utf-8"))

    missing = sorted(
        token.split(":")[-1].lstrip("!") for token in used_tokens() - ALLOWLIST
        if token.split(":")[-1].lstrip("!") not in defined
    )

    if _drift.maybe_write_baseline(NAME, missing):
        return 0
    new, stale = _drift.diff(NAME, missing)
    if new:
        print("Classes used in .razor but not defined in tailwind.css/app.css:")
        _drift.report(NAME, new, stale)
        return 1
    _drift.report(NAME, new, stale)
    print("OK: every class used in .razor resolves to a CSS rule")
    return 0


if __name__ == "__main__":
    sys.exit(main())
