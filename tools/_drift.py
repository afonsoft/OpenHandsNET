#!/usr/bin/env python3
"""Drift-ratchet shared helpers (SPEC-92): cada guard produz
violações; o baseline em `tools/drift-baseline/<name>.txt` congela o
estado atual — só violações NOVAS falham; entradas resolvidas viram
warning "baseline stale" (`--write-baseline` regenera).

Por que ratchet: o primeiro run encontrou drift real histórico (55 a11y,
~1700 chaves i18n). Congelar em baseline deixa o guard bloqueante sem
exigir big-bang fix — correções de drift aposentam entradas do baseline.
"""

from __future__ import annotations

import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BASELINE_DIR = ROOT / "tools" / "drift-baseline"


def load(name: str) -> Counter[str]:
    path = BASELINE_DIR / f"{name}.txt"
    if not path.exists():
        return Counter()
    return Counter(l.strip() for l in
                   path.read_text(encoding="utf-8").splitlines()
                   if l.strip())


def diff(name: str, signatures: list[str]) -> tuple[Counter[str], Counter[str]]:
    """(new, stale): violações acima do baseline / entradas do baseline
    já resolvidas. Multiset — repetições contam."""
    base = load(name)
    cur = Counter(signatures)
    return cur - base, base - cur


def report(name: str, new: Counter[str], stale: Counter[str]) -> None:
    for sig, count in sorted(new.items()):
        suffix = f" (x{count})" if count > 1 else ""
        print(f"  - {sig}{suffix}")
    if stale:
        total = sum(stale.values())
        print(f"info: {total} entrada(s) do baseline já resolvidas — "
              f"rode `python3 tools/{name}.py --write-baseline` para "
              "aposentá-las.")


def maybe_write_baseline(name: str, signatures: list[str]) -> bool:
    """`--write-baseline` presente → grava e retorna True."""
    if "--write-baseline" not in sys.argv:
        return False
    BASELINE_DIR.mkdir(parents=True, exist_ok=True)
    path = BASELINE_DIR / f"{name}.txt"
    path.write_text(
        "\n".join(sorted(signatures)) + "\n", encoding="utf-8")
    print(f"baseline gravado: {path} ({len(signatures)} signatures)")
    return True
