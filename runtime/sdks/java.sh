#!/usr/bin/env bash
# SPEC-17 RF-005: Java (OpenJDK via apt) — major no nome do pacote.
# Uso: java.sh <major>   ex.: java.sh 21
set -euo pipefail

REQUESTED="${1:-21}"
SUDO=""; [ "$(id -u)" -ne 0 ] && SUDO="sudo"

echo "[sdk:java] installing OpenJDK ${REQUESTED}…"
$SUDO apt-get update
$SUDO apt-get install -y --no-install-recommends "openjdk-${REQUESTED}-jdk" \
    || $SUDO apt-get install -y --no-install-recommends "openjdk-${REQUESTED}-jdk-headless"
$SUDO rm -rf /var/lib/apt/lists/*
java -version 2>&1 | head -1
echo "[sdk:java] ok"
