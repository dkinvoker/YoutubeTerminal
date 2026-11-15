#!/usr/bin/env bash
# Wylicza następną wersję i notatki wydania z opisów commitów (Conventional Commits) od ostatniego tagu vX.Y.Z.
#
#   next-version.sh [auto|patch|minor|major|none] [plik-notatek]
#
# Wypisuje (dla $GITHUB_OUTPUT): version=X.Y.Z, tag=vX.Y.Z, release=true|false.
#   feat!: / fix!: / BREAKING CHANGE  → wersja główna
#   feat:                             → nowa funkcja (minor)
#   fix: / perf:                      → poprawka (patch)
#   inne (docs:, ci:, chore:, …)      → bez wydania
# Bez żadnego tagu pierwsze wydanie ma wersję z <Version> w pliku projektu.
set -euo pipefail

force="${1:-auto}"
notes="${2:-release-notes.md}"
csproj="src/YoutubeTerminal/YoutubeTerminal.csproj"

last_tag=$(git describe --tags --abbrev=0 --match 'v[0-9]*' 2>/dev/null || true)
if [ -n "$last_tag" ]; then
  base="${last_tag#v}"
  range="$last_tag..HEAD"
else
  base=$(sed -n 's:.*<Version>\([0-9][0-9.]*\)</Version>.*:\1:p' "$csproj" | head -n 1)
  base="${base:-1.0.0}"
  range="HEAD"
fi
IFS=. read -r major minor patch <<< "${base%%-*}"
major=${major:-1}; minor=${minor:-0}; patch=${patch:-0}

subjects=$(git log --no-merges --format='%s' "$range" || true)
bodies=$(git log --no-merges --format='%b' "$range" || true)

bump=none
if printf '%s\n' "$subjects" | grep -Eq '^[a-z]+(\([^)]*\))?!:' || printf '%s\n' "$bodies" | grep -q 'BREAKING CHANGE'; then
  bump=major
elif printf '%s\n' "$subjects" | grep -Eq '^feat(\([^)]*\))?:'; then
  bump=minor
elif printf '%s\n' "$subjects" | grep -Eq '^(fix|perf)(\([^)]*\))?:'; then
  bump=patch
fi
[ "$force" != "auto" ] && bump="$force"

release=true
if [ -z "$last_tag" ]; then
  # Pierwsze wydanie: wersja z pliku projektu (chyba że wymuszono „none”).
  [ "$bump" = "none" ] && [ "$force" = "none" ] && release=false
  version="$major.$minor.$patch"
else
  case "$bump" in
    major) version="$((major + 1)).0.0" ;;
    minor) version="$major.$((minor + 1)).0" ;;
    patch) version="$major.$minor.$((patch + 1))" ;;
    *) version="$major.$minor.$patch"; release=false ;;
  esac
fi

# Notatki wydania: commity pogrupowane według typu, bez prefiksu.
section() {
  local title="$1" pattern="$2" lines
  lines=$(printf '%s\n' "$subjects" | grep -E "$pattern" | sed -E 's/^[a-z]+(\(([^)]*)\))?!?: */- **\2:** /; s/- \*\*:\*\* /- /' || true)
  if [ -n "$lines" ]; then
    printf '### %s\n\n%s\n\n' "$title" "$lines"
  fi
}
{
  section "Breaking changes" '^[a-z]+(\([^)]*\))?!:'
  section "Features" '^feat(\([^)]*\))?:'
  section "Fixes" '^fix(\([^)]*\))?:'
  section "Performance" '^perf(\([^)]*\))?:'
} > "$notes"
if [ ! -s "$notes" ]; then
  if [ -z "$last_tag" ]; then echo "First release." > "$notes"; else echo "Maintenance release." > "$notes"; fi
fi
{
  echo
  echo "**Downloads:** Windows — \`YoutubeTerminal-win-x64.exe\` · Linux — \`YoutubeTerminal-linux-x64.tar.gz\` / \`-linux-arm64.tar.gz\` · macOS (Apple Silicon) — \`YoutubeTerminal-macos-arm64.tar.gz\`. Checksums: \`SHA256SUMS.txt\`."
} >> "$notes"

echo "version=$version"
echo "tag=v$version"
echo "release=$release"
