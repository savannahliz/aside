#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
build_label="${ASIDE_BUILD_LABEL:-1.1.2}"
output_dir="dist/windows-x64-${build_label}"
dotnet_bin="${DOTNET_BIN:-.tools/dotnet/dotnet}"
if [ ! -x "$dotnet_bin" ]; then dotnet_bin="$(command -v dotnet)"; fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
"$dotnet_bin" run --project windows/Tests/CoreChecks.csproj -c Release
"$dotnet_bin" publish windows/QuietPin.Windows.csproj -c Release -r win-x64 --self-contained true -o "$output_dir"
cp "$output_dir/Aside.exe" "dist/Aside-${build_label}-Windows-x64.exe"
cp packaging/Windows使用说明.txt "$output_dir/使用说明.txt"
ditto -c -k --keepParent "$output_dir" "dist/Aside-${build_label}-Windows-x64.zip"
