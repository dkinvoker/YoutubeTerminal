#!/usr/bin/env bash
# Buduje minimalny ffmpeg do wbudowania w program (Linux, macOS): tylko to, czego używa odtwarzacz —
# dekodery H.264 i AAC, demuksery surowego H.264 i ADTS, skalowanie, przepróbkowanie i wyjście surowe.
# Bez bibliotek zewnętrznych i komponentów GPL (licencja LGPL 2.1+), ok. kilku MB.
#
#   build-ffmpeg.sh <plik-wynikowy>
set -euo pipefail

out="$1"
version="${FFMPEG_VERSION:-7.1.1}"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

git -c advice.detachedHead=false clone --quiet --depth 1 --branch "n$version" https://github.com/FFmpeg/FFmpeg.git "$work/src"
cd "$work/src"

# Parser HEVC jest potrzebny tylko pośrednio: w FFmpeg 7.1 dekoder H.264 (h2645_sei.c) wywołuje funkcje
# z aom_film_grain.c, a configure dołącza ten plik wyłącznie razem z HEVC SEI. Bez tego linkowanie kończy się
# błędem „undefined reference to ff_aom_uninit_film_grain_params”.
# Muxer „s16le” nazywa się w configure pcm_s16le.
./configure \
  --disable-everything --disable-autodetect --disable-network --disable-doc --disable-debug \
  --disable-ffplay --disable-ffprobe --disable-avdevice --disable-iconv \
  --enable-static --disable-shared --enable-pthreads \
  --enable-protocol=pipe,file \
  --enable-demuxer=h264,aac \
  --enable-parser=h264,aac,hevc \
  --enable-decoder=h264,aac \
  --enable-muxer=rawvideo,pcm_s16le \
  --enable-encoder=rawvideo,pcm_s16le \
  --enable-filter=scale,format,null,aresample,aformat,anull

make -j"$(getconf _NPROCESSORS_ONLN)" ffmpeg
strip ffmpeg
mkdir -p "$(dirname "$out")"
cp ffmpeg "$out"
chmod +x "$out"
"$out" -hide_banner -version | head -n 1
ls -l "$out"

# Kontrola: wszystko, czego używa odtwarzacz (patrz Playback/Ffmpeg.cs), musi być w binarce.
check() {
  local kind="$1" name="$2" list
  # Najpierw cała lista (grep -q w potoku mógłby przerwać ffmpeg sygnałem SIGPIPE i — przy pipefail — dać fałszywy błąd).
  list=$("$out" -hide_banner "-$kind" 2>/dev/null || true)
  if ! grep -Eq "[[:space:]]$name([[:space:]]|$)" <<< "$list"; then
    echo "Brak w ffmpeg: $kind $name" >&2
    exit 1
  fi
}
check demuxers h264
check demuxers aac
check decoders h264
check decoders aac
check muxers rawvideo
check muxers s16le
check encoders rawvideo
check encoders pcm_s16le
check filters scale
check filters aresample
check protocols pipe
echo "ffmpeg: komplet komponentów"
