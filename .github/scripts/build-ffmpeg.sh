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

git clone --quiet --depth 1 --branch "n$version" https://github.com/FFmpeg/FFmpeg.git "$work/src"
cd "$work/src"

./configure \
  --disable-everything --disable-autodetect --disable-network --disable-doc --disable-debug \
  --disable-ffplay --disable-ffprobe --disable-avdevice \
  --enable-static --disable-shared --enable-pthreads \
  --enable-protocol=pipe,file \
  --enable-demuxer=h264,aac \
  --enable-parser=h264,aac \
  --enable-decoder=h264,aac \
  --enable-muxer=rawvideo,s16le \
  --enable-encoder=rawvideo,pcm_s16le \
  --enable-filter=scale,format,null,aresample,aformat,anull

make -j"$(getconf _NPROCESSORS_ONLN)" ffmpeg
strip ffmpeg
mkdir -p "$(dirname "$out")"
cp ffmpeg "$out"
chmod +x "$out"
"$out" -hide_banner -version | head -n 1
ls -l "$out"
