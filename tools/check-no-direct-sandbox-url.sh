#!/usr/bin/env bash
# SPEC-115 RF-005: o client Blazor NUNCA conecta direto no sandbox Docker
# (VNC/terminal/surfaces). Tudo passa pela API: HTTP+WS proxy em
# /api/v1/sandboxes/{id}/surfaces/{port}/ e SignalR no ConversationHub.
# Este guard falha se código do client abrir conexão direta a host de
# sandbox (ws://, wss://, iframe/fetch para host dinâmico de container).
set -euo pipefail
cd "$(dirname "$0")/.."

fail=0

# 1) Nenhum ws(s):// literal no código do client (fora libs/obj).
hits=$(grep -rEn 'wss?://' \
    src/OpenHandsNET.Client \
    --include='*.razor' --include='*.cs' --include='*.js' \
    --exclude-dir=bin --exclude-dir=obj --exclude-dir=lib || true)
if [ -n "$hits" ]; then
    echo "FAIL: client abre WebSocket direto (use SignalR/proxy via API):"
    echo "$hits"
    fail=1
fi

# 2) Hostname de container Docker não pode ser montado no client.
hits=$(grep -rEn 'surfaces/[0-9]+\?token=|\.docker\.internal|containerId' \
    src/OpenHandsNET.Client \
    --include='*.razor' --include='*.cs' \
    --exclude-dir=bin --exclude-dir=obj || true)
if [ -n "$hits" ]; then
    echo "FAIL: client monta URL direta para host de sandbox:"
    echo "$hits"
    fail=1
fi

if [ "$fail" -ne 0 ]; then
    exit 1
fi
echo "check-no-direct-sandbox-url: OK — client só usa proxy same-origin/SignalR"
