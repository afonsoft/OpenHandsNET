#!/usr/bin/env bash
# SPEC-113 RF-001/002: surface "desktop" — Xvfb + openbox + x11vnc +
# websockify/noVNC na porta 6080, supervisionada pelo sandbox-agent
# (SurfaceSupervisor: respawn com backoff se este script sair).
#
# Instalação lazy (padrão code-server.sh): a imagem não carrega a stack
# gráfica por default — pacotes entram via apt na primeira ativação da
# env SANDBOX_DESKTOP=1. sudo NOPASSWD de /opt/ohn/sdks/*.sh cobre isso.
set -euo pipefail

SUDO=""; [[ "$(id -u)" -ne 0 ]] && SUDO="sudo"
DISPLAY_NUM="${SANDBOX_DESKTOP_DISPLAY:-:0}"
VNC_PORT="${SANDBOX_DESKTOP_VNC_PORT:-5900}"
HTTP_PORT="${SANDBOX_DESKTOP_PORT:-6080}"
GEOMETRY="${SANDBOX_DESKTOP_GEOMETRY:-1280x800x24}"

if ! command -v x11vnc >/dev/null 2>&1 \
    || ! command -v websockify >/dev/null 2>&1 \
    || [[ ! -d /usr/share/novnc ]]; then
    echo "[surface:desktop] installing xvfb/x11vnc/novnc/websockify…"
    $SUDO apt-get update
    $SUDO apt-get install -y --no-install-recommends \
        xvfb x11vnc openbox xterm novnc websockify fonts-liberation \
        x11-utils
    $SUDO rm -rf /var/lib/apt/lists/*
fi

export DISPLAY="$DISPLAY_NUM"
pids=()

Xvfb "$DISPLAY_NUM" -screen 0 "$GEOMETRY" -ac +extension RANDR & pids+=($!)
# Espera o socket do X subir antes de abrir clientes.
for _ in $(seq 1 50); do
    xdpyinfo -display "$DISPLAY" >/dev/null 2>&1 && break || sleep 0.1
done
openbox & pids+=($!)
x11vnc -display "$DISPLAY" -rfbport "$VNC_PORT" -localhost \
    -forever -shared -nopw -quiet & pids+=($!)
websockify --web=/usr/share/novnc "0.0.0.0:${HTTP_PORT}" \
    "localhost:${VNC_PORT}" & pids+=($!)

# App inicial na tela: Chrome for Testing (sdk chrome.sh) se presente,
# senão um xterm — a tela nunca abre vazia.
if [[ -x /usr/local/bin/chrome ]]; then
    (chrome --no-first-run --no-default-browser-check \
        --start-maximized about:blank >/dev/null 2>&1 &) &
elif command -v xterm >/dev/null 2>&1; then
    (xterm >/dev/null 2>&1 &) &
fi

echo "[surface:desktop] noVNC on :${HTTP_PORT} (vnc :${VNC_PORT}, display ${DISPLAY})"

# Sai quando qualquer filho morrer — o supervisor respawna tudo junto.
wait -n "${pids[@]}"
exit 1
