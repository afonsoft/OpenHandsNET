#!/usr/bin/env python3
"""Guard de placeholders i18n (SPEC-92).

Falha quando:
  - um valor de locale usa placeholder de chave simples {nome} — L.T só
    interpola {{nome}}; {nome} renderizaria literal na tela;
  - o conjunto de {{placeholders}} de uma chave difere entre locales
    (tradução que remove {count} é o drift clássico);
  - uma chave interpolada é consumida via L["..."] (sem interpolação) —
    renderizaria {{nome}} cru;
  - um call site L.T("key", ...) não passa todos os placeholders.

Uso: python3 tools/check-i18n-placeholders.py
"""

import json
import re
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import _drift  # noqa: E402

NAME = "check-i18n-placeholders"
ROOT = Path(__file__).resolve().parent.parent
I18N = ROOT / "src/OpenHandsNET.Client/wwwroot/i18n"
CLIENT = ROOT / "src/OpenHandsNET.Client"

SINGLE_BRACE = re.compile(r"(?<!\{)\{(\w+)\}(?!\})")
BASE_LOCALE = "en-US.json"
DOUBLE_BRACE = re.compile(r"\{\{(\w+)\}\}")


def load_interpolated_keys():
    """Chaves cujo valor (en-US) contém placeholders {{nome}}."""
    en = json.loads((I18N / BASE_LOCALE).read_text(encoding="utf-8"))
    return {k: set(DOUBLE_BRACE.findall(str(v))) for k, v in en.items()
            if DOUBLE_BRACE.search(str(v))}


# Chaves onde {x} é literal intencional (ex.: path de URL instrucional
# "POST /api/v1/orgs/{org}/...") — nunca passa por L.T.
SINGLE_BRACE_LITERAL_KEYS = {"admin.chat_connect_hint"}


def check_single_brace(errors):
    """Nenhum valor de locale pode usar {nome} — sempre {{nome}}."""
    for path in sorted(I18N.glob("*.json")):
        if path.name == "locales.json":
            continue
        data = json.loads(path.read_text(encoding="utf-8"))
        for key, value in data.items():
            if key in SINGLE_BRACE_LITERAL_KEYS:
                continue
            bad = SINGLE_BRACE.findall(str(value))
            if bad:
                errors.append((
                    f"{path.name}:{key}:single-brace",
                    f"{path.name}: '{key}' usa placeholder de chave "
                    f"simples {bad} — use {{{{...}}}} "
                    "(L.T não interpola {x})."))


def check_placeholder_parity(errors):
    """Mesmo conjunto de {{nome}} em todas as traduções de uma chave."""
    files = [p for p in sorted(I18N.glob("*.json"))
             if p.name not in ("locales.json", BASE_LOCALE)]
    en = json.loads((I18N / BASE_LOCALE).read_text(encoding="utf-8"))
    for path in files:
        data = json.loads(path.read_text(encoding="utf-8"))
        for key, value in data.items():
            ref = set(DOUBLE_BRACE.findall(str(en.get(key, ""))))
            got = set(DOUBLE_BRACE.findall(str(value)))
            if ref != got:
                errors.append((
                    f"{path.name}:{key}:arity",
                    f"{path.name}: '{key}' placeholders {sorted(got)} "
                    f"≠ en-US {sorted(ref)}"))


def lt_args(source, lt_pos):
    """Extrai os nomes de arg de um call L.T(...) começando em `lt_pos`."""
    i = source.index("(", lt_pos)
    depth = 0
    for j in range(i, len(source)):
        if source[j] == "(":
            depth += 1
        elif source[j] == ")":
            depth -= 1
            if depth == 0:
                return set(re.findall(
                    r'\(\s*"(\w+)"\s*,', source[i:j + 1]))
    return set()


def check_call_sites(interp_keys, errors):
    """Valida usos de L[...] e L.T(...) em .razor/.cs contra as chaves."""
    files = list(CLIENT.rglob("*.razor")) + list(CLIENT.rglob("*.cs"))
    for path in files:
        source = path.read_text(encoding="utf-8")
        rel = path.relative_to(ROOT)
        for m in re.finditer(r'L\["([^"]+)"\]', source):
            if m.group(1) in interp_keys:
                line = source[: m.start()].count("\n") + 1
                errors.append((
                    f"{rel}:L[{m.group(1)}]",
                    f"{rel}:{line}: L[\"{m.group(1)}\"] tem "
                    "placeholders {{x}} mas é lida sem interpolação — "
                    "use L.T(...)."))
        for m in re.finditer(r'L\.T\("([^"]+)"\s*,', source):
            key = m.group(1)
            if key not in interp_keys:
                continue
            args = lt_args(source, m.start())
            missing = interp_keys[key] - args
            if missing:
                line = source[: m.start()].count("\n") + 1
                errors.append((
                    f"{rel}:L.T({key})",
                    f"{rel}:{line}: L.T(\"{key}\") sem arg(s) "
                    f"{sorted(missing)} — placeholder renderizaria cru."))


def main():
    # errors: (signature sem linha, display arquivo:linha)
    errors: list[tuple[str, str]] = []
    check_single_brace(errors)
    check_placeholder_parity(errors)
    check_call_sites(load_interpolated_keys(), errors)
    signatures = [sig for sig, _ in errors]
    if _drift.maybe_write_baseline(NAME, signatures):
        return
    new, stale = _drift.diff(NAME, signatures)
    if new:
        print("i18n placeholder guard FALHOU:")
        for sig, disp in errors:
            if new[sig] > 0:
                new[sig] -= 1
                print(" -", disp)
        _drift.report(NAME, Counter(), stale)
        sys.exit(1)
    _drift.report(NAME, new, stale)
    print("OK: placeholders i18n consistentes ({{nome}} + args completos)")


if __name__ == "__main__":
    main()
