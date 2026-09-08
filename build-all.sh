#!/usr/bin/env bash
# Builds the portable single-file executable for every supported OS.
set -euo pipefail

RIDS=(win-x64 linux-x64 osx-arm64 osx-x64)

for rid in "${RIDS[@]}"; do
  echo "==> $rid"
  dotnet publish TokenBurnRate.csproj -c Release -r "$rid" -o "publish/$rid" --nologo
done

echo
echo "Artifacts:"
for rid in "${RIDS[@]}"; do
  find "publish/$rid" -maxdepth 1 -type f -name 'TokenBurnRate*' -exec ls -lh {} \;
done
