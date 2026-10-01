#!/bin/sh
set -eu

# Basis Universal v2.50 のエンコーダを指定アーキテクチャ向けにビルドする。
# 使い方: build-encoder.sh [ARCH]。省略時は実行ホストのアーキテクチャ。

arch=${1:-$(uname -m)}
tool_directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
work="$tool_directory/../../.artifacts/basis-universal-encoder"
source_directory="$work/basis_universal"
object_directory="$work/obj-$arch"

case "$arch" in
  arm64) rid=osx-arm64 ;;
  x86_64) rid=osx-x64 ;;
  *) echo "Unsupported architecture: $arch" >&2; exit 2 ;;
esac
output="$tool_directory/$rid/basisu"

if ! command -v clang++ >/dev/null 2>&1; then
  echo "clang++ is required (install the Xcode command line tools)." >&2
  exit 1
fi

mkdir -p "$work" "$object_directory"
if [ ! -d "$source_directory/.git" ]; then
  git clone --depth 1 --branch v2_50 https://github.com/BinomialLLC/basis_universal.git "$source_directory"
fi

# エンコーダの依存シンボルを解決するため、KTX2 の Zstd 対応を有効にする。
cxxflags="-arch $arch -std=c++17 -O3 -DNDEBUG -fno-strict-aliasing -fvisibility=hidden -mmacosx-version-min=11.0 \
-DBASISU_SUPPORT_SSE=0 -DBASISU_SUPPORT_ASTCENC=0 -DBASISU_DISABLE_ANDROID_ASTC_DECOMP=0 \
-DBASISD_SUPPORT_KTX2_ZSTD=1 -D_LARGEFILE64_SOURCE=1 -D_FILE_OFFSET_BITS=64 -w"

sources="encoder/basisu_backend.cpp encoder/basisu_basis_file.cpp encoder/basisu_comp.cpp \
encoder/basisu_enc.cpp encoder/basisu_etc.cpp encoder/basisu_frontend.cpp encoder/basisu_gpu_texture.cpp \
encoder/basisu_pvrtc1_4.cpp encoder/basisu_resampler.cpp encoder/basisu_resample_filters.cpp \
encoder/basisu_ssim.cpp encoder/basisu_uastc_enc.cpp encoder/basisu_bc7e_scalar.cpp \
encoder/basisu_dds_export.cpp encoder/basisu_bc7enc.cpp encoder/jpgd.cpp encoder/basisu_kernels_sse.cpp \
encoder/basisu_bc15_spmd.cpp encoder/basisu_bc15_spmd_sse.cpp encoder/basisu_opencl.cpp \
encoder/pvpngreader.cpp encoder/basisu_uastc_hdr_4x4_enc.cpp encoder/basisu_astc_hdr_6x6_enc.cpp \
encoder/basisu_astc_hdr_common.cpp encoder/basisu_astc_ldr_common.cpp encoder/basisu_astc_ldr_encode.cpp \
encoder/basisu_astc_ldr_fencode.cpp encoder/basisu_xbc7_encode.cpp encoder/basisu_tinyexr.cpp \
transcoder/basisu_transcoder.cpp encoder/3rdparty/android_astc_decomp.cpp \
basisu_tool.cpp basisu_text_image.cpp"

echo "Compiling the Basis Universal v2.50 encoder for $rid ..."
running=0
for source in $sources; do
  object="$object_directory/$(echo "$source" | tr '/' '_').o"
  clang++ $cxxflags -c "$source_directory/$source" -o "$object" &
  running=$((running + 1))
  if [ "$running" -ge 8 ]; then wait; running=0; fi
done
wait
clang -arch "$arch" -O3 -DNDEBUG -w -mmacosx-version-min=11.0 \
  -c "$source_directory/zstd/zstd.c" -o "$object_directory/zstd.o"

mkdir -p "$tool_directory/$rid"
clang++ -arch "$arch" -mmacosx-version-min=11.0 -Wl,-dead_strip -o "$output" "$object_directory"/*.o
strip -S -x "$output"
chmod 755 "$output"
"$output" -version | head -2
echo "Wrote $output"
