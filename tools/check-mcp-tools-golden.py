#!/usr/bin/env python3
"""Guard: golden snapshot do catálogo `tools/list` do servidor MCP
(SPEC-129 RF-001).

Executa `sandcastle-mcp.dll` com um request JSON-RPC `tools/list` no
stdin (não precisa de SC_API_KEY — tools/call é quem exige credencial)
e compara o catálogo normalizado com `tools/drift-baseline/mcp-tools.json`.

- Drift (tool adicionada/removida/renomeada, schema alterado) → exit 1.
- Baseline ausente → exit 1 com instrução de `--write-baseline`.
- `--write-baseline` regenera o snapshot (usar no PR que muda o catálogo).

Uso: python3 tools/check-mcp-tools-golden.py [--write-baseline]
Exit 0 = catálogo idêntico ao baseline; 1 = drift/falha.
"""
import json
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DLL = ROOT / "src/Sandcastle.Mcp/bin/Release/net10.0/sandcastle-mcp.dll"
BASELINE = ROOT / "tools/drift-baseline/mcp-tools.json"
REQUEST = '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
TIMEOUT = 90


def ensure_built() -> None:
    if DLL.exists():
        return
    build = subprocess.run(
        ["dotnet", "build", "src/Sandcastle.Mcp", "-c", "Release",
         "--nologo", "-v", "q"],
        cwd=ROOT, capture_output=True, text=True, timeout=300)
    if build.returncode != 0 or not DLL.exists():
        print("FAIL: build do Sandcastle.Mcp falhou")
        print(build.stdout[-2000:])
        sys.exit(1)


def fetch_tools() -> list:
    proc = subprocess.run(
        ["dotnet", str(DLL)],
        input=REQUEST + "\n",
        capture_output=True, text=True, timeout=TIMEOUT, cwd=ROOT)
    if proc.returncode != 0:
        print(f"FAIL: mcp serve exit {proc.returncode}: "
              f"{proc.stderr[-500:]}")
        sys.exit(1)
    lines = [l for l in proc.stdout.splitlines() if l.strip()]
    if not lines:
        print("FAIL: stdout vazio — tools/list não respondeu")
        sys.exit(1)
    try:
        tools = json.loads(lines[0])["result"]["tools"]
    except (KeyError, json.JSONDecodeError) as exc:
        print(f"FAIL: resposta inválida: {exc} — {lines[0][:200]}")
        sys.exit(1)
    # Normaliza: ordena por nome — posição no source não é contrato.
    return sorted(tools, key=lambda t: t["name"])


def canonical(tools: list) -> str:
    return json.dumps(tools, sort_keys=True, indent=2,
                      ensure_ascii=False) + "\n"


def main() -> int:
    write = "--write-baseline" in sys.argv
    ensure_built()
    tools = fetch_tools()
    snapshot = canonical(tools)

    if write:
        BASELINE.write_text(snapshot, encoding="utf-8")
        print(f"OK: baseline atualizado — {len(tools)} tools "
              f"→ {BASELINE.relative_to(ROOT)}")
        return 0

    if not BASELINE.exists():
        print(f"FAIL: baseline ausente — rode "
              f"`python3 {os.path.relpath(__file__, ROOT)} "
              f"--write-baseline`")
        return 1

    golden = BASELINE.read_text(encoding="utf-8")
    if snapshot == golden:
        print(f"OK: tools/list idêntico ao baseline ({len(tools)} tools)")
        return 0

    current = {t["name"] for t in json.loads(snapshot)}
    frozen = {t["name"] for t in json.loads(golden)}
    for name in sorted(current - frozen):
        print(f"NEW     {name}")
    for name in sorted(frozen - current):
        print(f"REMOVED {name}")
    if current == frozen:
        print("SCHEMA  drift — mesmo conjunto, schema/description mudou")
    print("FAIL: tools/list divergiu do baseline — atualize com "
          "--write-baseline se a mudança é intencional")
    return 1


if __name__ == "__main__":
    sys.exit(main())
