#!/usr/bin/env bash
# SPEC-82 RF-003: guard leve de lifecycle de SPECs.
# Falha (exit 1) quando uma SPEC "ativa" em .specs/ tem status terminal
# (Done/Completed/Delivered) — sinal de que faltou arquivar em
# docs/specs/ e fechar a issue. Warning-only não é suficiente porque a
# recorrência já aconteceu (75–78 entregues ficaram ativas).
set -euo pipefail
cd "$(dirname "$0")/.."

stale=0
for f in .specs/*_spec.md; do
  [ -e "$f" ] || continue
  status=$(grep -m1 -oP '\| Status \| \K[^|]+' "$f" | xargs || true)
  case "$status" in
    Done|Completed|Delivered)
      echo "STALE  $f  status=$status (mover para docs/specs/ e fechar issue)"
      stale=$((stale + 1))
      ;;
  esac
done

if [ "$stale" -gt 0 ]; then
  echo "error: $stale spec(s) entregue(s) ainda em .specs/ — veja SPEC-82"
  exit 1
fi
echo "spec lifecycle ok — nenhuma spec entregue em .specs/"
