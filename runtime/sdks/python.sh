#!/usr/bin/env bash
# SPEC-17 RF-005: Python via deadsnakes (Ubuntu) para versões != sistema.
# Uso: python.sh <3.x>   ex.: python.sh 3.13
set -euo pipefail

REQUESTED="${1:-3.13}"
SUDO=""; [ "$(id -u)" -ne 0 ] && SUDO="sudo"
SYSTEM_PY="$(python3 --version | awk '{print $2}' | cut -d. -f1,2)"

echo "[sdk:python] installing Python ${REQUESTED}…"
if [ "$REQUESTED" = "$SYSTEM_PY" ]; then
    echo "[sdk:python] system python $SYSTEM_PY já presente"
else
    $SUDO apt-get update
    $SUDO apt-get install -y --no-install-recommends software-properties-common
    $SUDO add-apt-repository -y ppa:deadsnakes/ppa
    $SUDO apt-get update
    $SUDO apt-get install -y --no-install-recommends \
        "python${REQUESTED}" "python${REQUESTED}-venv" "python${REQUESTED}-dev"
    $SUDO rm -rf /var/lib/apt/lists/*
    $SUDO update-alternatives --install /usr/bin/python3 python3 \
        "/usr/bin/python${REQUESTED}" 10 || true
fi
python3 --version
echo "[sdk:python] ok"
