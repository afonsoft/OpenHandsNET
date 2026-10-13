#!/usr/bin/env bash
# SPEC-17 RF-005: Node.js — NodeSource para majors suportadas.
# Uso: node.sh <major>   ex.: node.sh 22
set -euo pipefail

REQUESTED="${1:-22}"
SUDO=""; [ "$(id -u)" -ne 0 ] && SUDO="sudo"

echo "[sdk:node] installing Node.js ${REQUESTED}.x…"
if [ "$REQUESTED" != "22" ]; then
    # Rebuild da fonte apt para a major pedida (a imagem já traz 22.x).
    $SUDO curl -fsSL "https://deb.nodesource.com/setup_${REQUESTED}.x" | $SUDO bash -
fi
$SUDO apt-get update
$SUDO apt-get install -y --no-install-recommends nodejs
$SUDO rm -rf /var/lib/apt/lists/*
node --version
echo "[sdk:node] ok"
