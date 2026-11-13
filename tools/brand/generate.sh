#!/usr/bin/env bash
# SPEC-139 — regenera todos os rasters de marca a partir dos SVG-fonte
# em docs/brand/ (source of truth). NUNCA editar os PNGs/ICO à mão.
#
# Uso:   tools/brand/generate.sh
# Deps:  python3 + cairosvg (pip install cairosvg);
#        ImageMagick `convert` OU Pillow para o favicon.ico multi-size.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
BRAND="$ROOT/docs/brand"
WWW="$ROOT/src/Sandcastle.Client/wwwroot"
ICONS="$WWW/assets/icons"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

render() { # <svg> <size-px> <out.png> — square output
    python3 - "$1" "$2" "$3" <<'PY'
import sys
import cairosvg

src, size, out = sys.argv[1], int(sys.argv[2]), sys.argv[3]
cairosvg.svg2png(url=src, write_to=out, output_width=size, output_height=size)
PY
}

render_w() { # <svg> <width-px> <out.png> — height follows the viewBox ratio
    python3 - "$1" "$2" "$3" <<'PY'
import sys
import cairosvg

src, width, out = sys.argv[1], int(sys.argv[2]), sys.argv[3]
cairosvg.svg2png(url=src, write_to=out, output_width=width)
PY
}

echo ">> PWA icons (icon-app.svg / icon-maskable.svg)"
render "$BRAND/icon-app.svg"      192 "$ICONS/icon-192.png"
render "$BRAND/icon-app.svg"      512 "$ICONS/icon-512.png"
render "$BRAND/icon-maskable.svg" 512 "$ICONS/icon-maskable-512.png"

echo ">> assets auxiliares (logo.png / splash-dark.png)"
render "$BRAND/icon-app.svg"      256 "$WWW/assets/logo.png"
render "$BRAND/icon-maskable.svg" 500 "$WWW/assets/splash-dark.png"

echo ">> favicon.ico (16 + 32)"
render "$WWW/favicon.svg" 16 "$TMP/favicon-16.png"
render "$WWW/favicon.svg" 32 "$TMP/favicon-32.png"
if command -v convert >/dev/null 2>&1; then
    convert "$TMP/favicon-16.png" "$TMP/favicon-32.png" "$WWW/favicon.ico"
else
    python3 - "$TMP" "$WWW/favicon.ico" <<'PY'
import sys
from PIL import Image

tmp, out = sys.argv[1], sys.argv[2]
im32 = Image.open(f"{tmp}/favicon-32.png")
im16 = Image.open(f"{tmp}/favicon-16.png")
im32.save(out, format="ICO", sizes=[(32, 32)], append_images=[im16])
PY
fi

echo ">> social preview (1280x640)"
render_w "$BRAND/social-preview.svg" 1280 "$BRAND/social-preview.png"

echo ">> done. verifique com: identify $ICONS/*.png $WWW/favicon.ico $BRAND/social-preview.png"
