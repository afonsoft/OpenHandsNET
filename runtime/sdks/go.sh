#!/usr/bin/env bash
# SPEC-17 RF-005: Go via tarball oficial (pin de versão ou latest).
# Uso: go.sh <version|latest>   ex.: go.sh 1.24.4
set -euo pipefail

REQUESTED="${1:-latest}"
SUDO=""; [ "$(id -u)" -ne 0 ] && SUDO="sudo"
ARCH="$(dpkg --print-architecture)"   # amd64|arm64

if [ "$REQUESTED" = "latest" ]; then
    REQUESTED="$(curl -fsSL 'https://go.dev/dl/?mode=json' \
        | jq -r '.[0].version' | sed 's/^go//')"
fi

echo "[sdk:go] installing Go ${REQUESTED} (${ARCH})…"
curl -fsSL "https://go.dev/dl/go${REQUESTED}.linux-${ARCH}.tar.gz" -o /tmp/go.tgz
$SUDO rm -rf /usr/local/go
$SUDO tar -C /usr/local -xzf /tmp/go.tgz
$SUDO ln -sf /usr/local/go/bin/go /usr/local/bin/go
$SUDO tee /etc/profile.d/ohn-go.sh >/dev/null <<'EOF'
export PATH=/usr/local/go/bin:$HOME/go/bin:$PATH
EOF
go version
echo "[sdk:go] ok"
