#!/usr/bin/env bash
# Pobiera oficjalny yt-dlp dla danej platformy i sprawdza sumę SHA-256 z SHA2-256SUMS wydania.
#
#   fetch-yt-dlp.sh <nazwa-zasobu> <plik-wynikowy>
#   np. fetch-yt-dlp.sh yt-dlp_linux tools/linux-x64/yt-dlp
set -euo pipefail

asset="$1"
out="$2"
base="https://github.com/yt-dlp/yt-dlp/releases/latest/download"

sha() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -d' ' -f1; }

mkdir -p "$(dirname "$out")"
curl -fsSL --retry 3 -o "$out" "$base/$asset"
curl -fsSL --retry 3 -o "$out.sums" "$base/SHA2-256SUMS"
expected=$(awk -v a="$asset" '$2 == a { print $1 }' "$out.sums")
actual=$(sha "$out")
rm -f "$out.sums"
if [ -z "$expected" ] || [ "$expected" != "$actual" ]; then
  echo "Suma SHA-256 yt-dlp się nie zgadza: oczekiwano '$expected', jest '$actual'" >&2
  exit 1
fi
chmod +x "$out"
echo "yt-dlp ($asset): $actual"
