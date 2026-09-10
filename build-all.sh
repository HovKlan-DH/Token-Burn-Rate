#!/usr/bin/env bash
# Publishes the self-contained build for every supported OS. This is the unpacked folder
# Velopack's `vpk pack` takes as input (see build-and-release.yml) - it is not the single
# downloadable file users get; that is the Setup.exe/.AppImage/.pkg vpk produces from it.
set -euo pipefail

RIDS=(win-x64 linux-x64 osx-arm64 osx-x64)

for rid in "${RIDS[@]}"; do
  echo "==> $rid"
  dotnet publish Token-Burn-Rate.csproj -c Release -r "$rid" -o "publish/$rid" --nologo
done

echo
echo "Published to publish/<rid>/ - run 'vpk pack' per OS to produce an installer, or just" \
     "run the platform's Token-Burn-Rate executable directly from its publish folder."
