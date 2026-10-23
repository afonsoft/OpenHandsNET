#!/usr/bin/env python3
"""Guard: chaves i18n em paridade com o baseline en-US (SPEC-92).

- MISSING: chave do en-US ausente no locale → erro sempre.
- EXTRA:   chave órfã no locale (ausente no en-US) → warning por
  default, erro com --strict.
- locales.json: todo `code` tem um `<code>.json` e vice-versa.

Uso: python3 tools/check-i18n-parity.py [--strict]
Exit 0 = paridade (ou só warnings); 1 = drift.
"""
import glob
import json
import os
import sys
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _drift  # noqa: E402

NAME = "check-i18n-parity"
I18N_DIR = os.path.join(os.path.dirname(__file__), "..",
                        "src/OpenHandsNET.Client/wwwroot/i18n")
BASELINE = "en-US.json"


def main() -> int:
    strict = "--strict" in sys.argv
    base_path = os.path.join(I18N_DIR, BASELINE)
    baseline = set(json.load(open(base_path, encoding="utf-8")))
    errors, warnings = [], []

    files = {os.path.basename(p) for p in
             glob.glob(os.path.join(I18N_DIR, "*.json"))
             if os.path.basename(p) != "locales.json"}

    for name in sorted(files):
        if name == BASELINE:
            continue
        keys = set(json.load(
            open(os.path.join(I18N_DIR, name), encoding="utf-8")))
        for k in sorted(baseline - keys):
            errors.append(f"MISSING {name}: {k}")
        for k in sorted(keys - baseline):
            (errors if strict else warnings).append(
                f"EXTRA   {name}: {k}")

    # locales.json <-> arquivos
    locales_path = os.path.join(I18N_DIR, "locales.json")
    declared = {e["code"] for e in
                json.load(open(locales_path, encoding="utf-8"))
                ["locales"]}
    on_disk = {n[:-5] for n in files}
    for code in sorted(declared - on_disk):
        errors.append(f"MISSING file: locales.json declara {code} "
                      "sem {code}.json")
    for code in sorted(on_disk - declared):
        errors.append(f"EXTRA file: {code}.json sem entrada em "
                      "locales.json")

    for w in warnings:
        print(f"warn: {w}")
    # ratchet: a linha de erro já é signature estável (sem números)
    signatures = errors
    if _drift.maybe_write_baseline(NAME, signatures):
        return 0
    new, stale = _drift.diff(NAME, signatures)
    if new:
        print("i18n parity violations:")
        _drift.report(NAME, new, stale)
        return 1
    _drift.report(NAME, new, stale)
    print(f"OK: {len(baseline)} keys em paridade em "
          f"{len(files)} locales")
    return 0


if __name__ == "__main__":
    sys.exit(main())
