#!/bin/bash
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: merge-ios-archives.sh BUILD_DIRECTORY OUTPUT" >&2
  exit 2
fi

build_directory=$1
output=$2
archives=()
while IFS= read -r -d '' archive; do
  archives+=("$archive")
done < <(find "$build_directory" -type f -name '*.a' -print0)

if [ "${#archives[@]}" -eq 0 ]; then
  echo "No Dawn archives were found under $build_directory" >&2
  exit 1
fi

rm -f "$output"
xcrun libtool -static -o "$output" "${archives[@]}"

while xcrun ar t "$output" | grep -qx 'Placeholder\.o'; do
  xcrun ar d "$output" Placeholder.o
done
xcrun ranlib "$output"
