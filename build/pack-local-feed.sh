#!/bin/sh
set -eu

# Core と Modules のパッケージをローカルフィードに生成する。

script_directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
engine_directory=$(dirname -- "$script_directory")

# パッケージとして消費する側の nuget.config がこの 2 つを指す。
feed="$engine_directory/.artifacts/local-feed"
package_directory="$engine_directory/.artifacts/local-packages"

report=$(mktemp)
trap 'rm -f "$report"' EXIT

dotnet msbuild "$script_directory/PackageReport.proj" -t:Report -p:ReportPath="$report" -v:m -nologo

# 同じ版の古い内容が再利用されないよう、フィードと展開キャッシュを作り直す。
rm -rf "$feed"
mkdir -p "$feed"
if [ -d "$package_directory" ]; then
  find "$package_directory" -mindepth 1 -maxdepth 1 -name 'emptyengine.*' -exec rm -rf {} +
fi

# iOS 用アセンブリの梱包パスが bin 配下を参照するため、出力レイアウトを変えない。
count=0
while IFS='	' read -r id version project rest; do
  [ -n "$id" ] || continue
  echo "==> $id $version"
  dotnet pack "$project" -c Release -o "$feed" --nologo
  count=$((count + 1))
done < "$report"

echo "$count 件を $feed へ焼きました。"
