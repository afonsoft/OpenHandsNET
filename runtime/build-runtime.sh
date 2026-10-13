#!/usr/bin/env bash
# SPEC-17: build manual da imagem runtime (publicação via registry = manual;
# workflow de CI dedicado exige revisão humana — não criado nesta spec).
#
#   ./runtime/build-runtime.sh [tag]
#
# Ex.: ./runtime/build-runtime.sh ghcr.io/openhandsnet/runtime:0.1.0
#      docker push ghcr.io/openhandsnet/runtime:0.1.0
set -euo pipefail

TAG="${1:-ghcr.io/openhandsnet/runtime:latest}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

docker buildx build \
    --platform linux/amd64,linux/arm64 \
    --tag "$TAG" \
    --file "$ROOT/runtime/Dockerfile" \
    "$ROOT"

echo "built: $TAG"
echo "push:  docker push $TAG"
