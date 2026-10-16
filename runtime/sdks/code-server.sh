#!/usr/bin/env bash
# SPEC-40 RF-003: instalador lazy do code-server — release oficial pinned,
# descompactado sob /opt/ohn/code-server (writable pelo usuário openhands)
# e linkado em /opt/ohn/bin/code-server. Rode sob demanda: a imagem não
# carrega os ~200MB por default.
set -euo pipefail

VERSION="${CODE_SERVER_VERSION:-4.141.0}"
INSTALL_DIR="/opt/ohn/code-server"
BIN_DIR="/opt/ohn/bin"

arch="$(dpkg --print-architecture)"
case "$arch" in
    amd64) cs_arch=amd64 ;;
    arm64) cs_arch=arm64 ;;
    *) echo "[surface:code-server] unsupported arch: $arch" >&2; exit 1 ;;
esac

if [ -x "$BIN_DIR/code-server" ]; then
    echo "[surface:code-server] already installed"
    exit 0
fi

echo "[surface:code-server] installing ${VERSION} (${cs_arch})…"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
curl -fsSL \
    "https://github.com/coder/code-server/releases/download/v${VERSION}/code-server-${VERSION}-linux-${cs_arch}.tar.gz" \
    -o "$tmp/code-server.tar.gz"
mkdir -p "$INSTALL_DIR" "$BIN_DIR"
tar -xzf "$tmp/code-server.tar.gz" -C "$INSTALL_DIR" --strip-components=1
ln -sf "$INSTALL_DIR/bin/code-server" "$BIN_DIR/code-server"
echo "[surface:code-server] installed at $BIN_DIR/code-server"
