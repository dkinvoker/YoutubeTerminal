#!/bin/sh
# Deinstalator YouTube Terminal (Linux, macOS) / YouTube Terminal uninstaller.
# Usuwa program, wypakowane yt-dlp i ffmpeg (z cache i plikami tymczasowymi), ustawienia i bibliotekę.
# Removes the program, the extracted yt-dlp and ffmpeg (with cache and temp files), settings and library.
#
#   ./uninstall.sh          tryb interaktywny / interactive
#   ./uninstall.sh -q       bez pytań, usuwa wszystko / no questions, remove everything
#   ./uninstall.sh --keep   bez pytań, zachowuje bibliotekę i ustawienia / keep library and settings

SELF="$0"
APPDIR=$(cd "$(dirname "$0")" && pwd)
EXE="$APPDIR/__EXE_NAME__"

if [ -n "$YOUTUBETERMINAL_HOME" ]; then
  DATA="$YOUTUBETERMINAL_HOME"
elif [ "$(uname -s)" = "Darwin" ]; then
  DATA="$HOME/Library/Application Support/YoutubeTerminal"
else
  DATA="${XDG_DATA_HOME:-$HOME/.local/share}/YoutubeTerminal"
fi

case "${LC_ALL:-${LC_MESSAGES:-${LANG:-}}}" in
  pl*) PL=1 ;;
  *) PL=0 ;;
esac
say() { if [ "$PL" = 1 ]; then printf '%s\n' "$1"; else printf '%s\n' "$2"; fi; }

QUIET=0
KEEP=0
case "$1" in
  -q|--quiet|/q) QUIET=1 ;;
  --keep|-k|/keep) QUIET=1; KEEP=1 ;;
esac

echo
say "  Deinstalacja YouTube Terminal" "  Uninstalling YouTube Terminal"
echo
say "  Zostaną usunięte:" "  This will remove:"
[ -f "$EXE" ] && say "    - program:  $EXE" "    - program:  $EXE"
say "    - dane:     $DATA" "    - data:     $DATA"
say "                (yt-dlp, ffmpeg, cache, pliki tymczasowe, log)" "                (yt-dlp, ffmpeg, cache, temp files, log)"
say "    - ten skrypt" "    - this script"
echo

if [ "$QUIET" = 0 ]; then
  if [ "$PL" = 1 ]; then printf '  Kontynuować? [t/N] '; else printf '  Continue? [y/N] '; fi
  read -r answer
  case "$answer" in
    t|T|y|Y|tak|yes) ;;
    *) say "  Anulowano. Nic nie zostało usunięte." "  Cancelled. Nothing was removed."; exit 1 ;;
  esac
  if [ -f "$DATA/library.json" ]; then
    if [ "$PL" = 1 ]; then printf '  Zachować subskrypcje, historię i ustawienia? [t/N] '; else printf '  Keep subscriptions, history and settings? [y/N] '; fi
    read -r answer
    case "$answer" in
      t|T|y|Y|tak|yes) KEEP=1 ;;
    esac
  fi
fi

echo
say "  Zamykanie uruchomionych procesów programu, yt-dlp i ffmpeg..." "  Stopping running program, yt-dlp and ffmpeg processes..."
if command -v pkill >/dev/null 2>&1; then
  pkill -f "$DATA/bin/" >/dev/null 2>&1
  [ -f "$EXE" ] && pkill -f "^$EXE" >/dev/null 2>&1
  sleep 1
fi

if [ -d "$DATA" ]; then
  if [ "$KEEP" = 1 ]; then
    for f in "$DATA"/* "$DATA"/.[!.]*; do
      [ -e "$f" ] || continue
      case "$(basename "$f")" in
        settings.json|library.json) ;;
        *) rm -rf "$f" ;;
      esac
    done
    say "  Usunięto yt-dlp, ffmpeg, cache i pliki tymczasowe." "  Removed yt-dlp, ffmpeg, cache and temp files."
    say "  Zachowano ustawienia i bibliotekę w: $DATA" "  Kept settings and library in: $DATA"
  else
    rm -rf "$DATA"
    if [ -d "$DATA" ]; then
      say "  [!] Nie udało się usunąć całego folderu danych: $DATA" "  [!] Could not remove the whole data folder: $DATA"
    else
      say "  Usunięto dane aplikacji." "  Removed the app data."
    fi
  fi
else
  say "  Brak folderu danych — pomijam." "  No data folder — skipping."
fi

if [ -f "$EXE" ]; then
  rm -f "$EXE"
  if [ -f "$EXE" ]; then
    say "  [!] Nie udało się usunąć programu: $EXE" "  [!] Could not remove the program: $EXE"
  else
    say "  Usunięto program." "  Removed the program."
  fi
fi

rm -f "$SELF"
echo
say "  Deinstalacja zakończona." "  Uninstall complete."
exit 0
