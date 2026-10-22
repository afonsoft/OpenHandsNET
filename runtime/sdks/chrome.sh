#!/usr/bin/env bash
# SPEC-86/17: Chrome for Testing (arch-aware via @puppeteer/browsers) +
# deps de runtime + chrome-devtools-mcp global — habilita o MCP de browser
# no sandbox (blueprint sdk: ["chrome"], opcional "chrome:stable|beta|dev|canary").
# Uso: chrome.sh [channel]   ex.: chrome.sh stable
set -euo pipefail

CHANNEL="${1:-stable}"
SUDO=""; [[ "$(id -u)" -ne 0 ]] && SUDO="sudo"

# Redes de container (bridge/NAT) são IPv4-only na prática — força
# IPv4-first no npm/npx para evitar ENETUNREACH em AAAA sem rota.
export NODE_OPTIONS="${NODE_OPTIONS:+${NODE_OPTIONS} }--dns-result-order=ipv4first"

# @puppeteer/browsers 3.x e chrome-devtools-mcp exigem Node >= 22.12.
node -e 'const [maj, min] = process.versions.node.split(".").map(Number);
process.exit(maj > 22 || (maj === 22 && min >= 12) ? 0 : 1);' \
    || { echo "[sdk:chrome] Node >= 22.12 required — found $(node -v)" >&2; exit 1; }

echo "[sdk:chrome] installing system deps…"
$SUDO apt-get update
$SUDO apt-get install -y --no-install-recommends \
    ca-certificates fonts-liberation unzip \
    libasound2t64 libatk-bridge2.0-0t64 libatk1.0-0t64 libcairo2 \
    libcups2t64 libdbus-1-3 libexpat1 libfontconfig1 libgbm1 libgcc-s1 \
    libglib2.0-0t64 libgtk-3-0t64 libnspr4 libnss3 libpango-1.0-0 \
    libpangocairo-1.0-0 libstdc++6 libx11-6 libx11-xcb1 libxcb1 \
    libxcomposite1 libxcursor1 libxdamage1 libxext6 libxfixes3 libxi6 \
    libxrandr2 libxrender1 libxss1 libxtst6 lsb-release wget xdg-utils
$SUDO rm -rf /var/lib/apt/lists/*

echo "[sdk:chrome] installing Chrome for Testing (${CHANNEL})…"
$SUDO npx -y "@puppeteer/browsers" install "chrome@${CHANNEL}" --path /opt/chrome
# Resolve o binário independente do layout/arquitetura e expõe um path
# estável para --executable-path do chrome-devtools-mcp.
CHROME_BIN="$(find /opt/chrome -type f -name chrome | head -1)"
[[ -n "$CHROME_BIN" ]] || { echo "[sdk:chrome] binary not found under /opt/chrome" >&2; exit 1; }
$SUDO ln -sf "$CHROME_BIN" /usr/local/bin/chrome

echo "[sdk:chrome] installing chrome-devtools-mcp (global)…"
$SUDO npm install -g chrome-devtools-mcp@latest

chrome --version
npm ls -g chrome-devtools-mcp --depth=0 >/dev/null
echo "[sdk:chrome] ok"