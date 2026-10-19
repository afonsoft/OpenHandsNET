#!/usr/bin/env bash
# SPEC-17 RF-005: instalador do .NET SDK — dotnet-install script oficial.
# Uso: dotnet.sh <version|channel>   ex.: dotnet.sh 10   dotnet.sh 8.0
set -euo pipefail

REQUESTED="${1:-10}"
INSTALL_DIR="/usr/local/dotnet"

echo "[sdk:dotnet] installing .NET SDK ${REQUESTED}…"
curl -fsSL --proto '=https' --tlsv1.2 https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh

# Versão numérica simples → canal "<major>.0"; "x.y.z" → versão exata.
if [[ "$REQUESTED" =~ ^[0-9]+$ ]]; then
    CHANNEL="${REQUESTED}.0"
elif [[ "$REQUESTED" =~ ^[0-9]+\.[0-9]+$ ]]; then
    CHANNEL="$REQUESTED"
else
    CHANNEL=""  # versão exata (x.y.z ou superior)
fi

SUDO=""; [[ "$(id -u)" -ne 0 ]] && SUDO="sudo"
$SUDO mkdir -p "$INSTALL_DIR"
if [[ -n "$CHANNEL" ]]; then
    $SUDO /tmp/dotnet-install.sh --channel "$CHANNEL" --install-dir "$INSTALL_DIR"
else
    $SUDO /tmp/dotnet-install.sh --version "$REQUESTED" --install-dir "$INSTALL_DIR"
fi

# PATH global via profile.d + link (imediato para novos processos).
$SUDO ln -sf "$INSTALL_DIR/dotnet" /usr/local/bin/dotnet
$SUDO tee /etc/profile.d/ohn-dotnet.sh >/dev/null <<EOF
export DOTNET_ROOT="$INSTALL_DIR"
export PATH="$INSTALL_DIR:\$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
EOF
dotnet --version
echo "[sdk:dotnet] ok"
