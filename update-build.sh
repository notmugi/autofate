#!/usr/bin/env bash
# Rebuilds AutoFates, copies the packaged zip to the repo root, and syncs repo.json's
# version fields to the freshly built plugin version. Then you just commit + push.
#
# Usage: ./update-build.sh
set -euo pipefail

cd "$(dirname "$0")"

echo ">> Building Release..."
dotnet build AutoFates/AutoFates.csproj -c Release >/dev/null

PKG="AutoFates/bin/x64/Release/AutoFates"
if [ ! -f "$PKG/latest.zip" ]; then
  echo "ERROR: $PKG/latest.zip not found (build failed?)" >&2
  exit 1
fi

echo ">> Copying latest.zip to repo root..."
cp "$PKG/latest.zip" ./latest.zip

# Read the built version + api level from the generated manifest.
VERSION=$(grep -oP '"AssemblyVersion":\s*"\K[^"]+' "$PKG/AutoFates.json")
API=$(grep -oP '"DalamudApiLevel":\s*\K[0-9]+' "$PKG/AutoFates.json")
echo ">> Built version $VERSION (API $API)"

echo ">> Syncing repo.json version fields..."
# Update AssemblyVersion / TestingAssemblyVersion / API levels in repo.json.
sed -i -E "s/(\"AssemblyVersion\":\s*\")[^\"]+(\")/\1${VERSION}\2/" repo.json
sed -i -E "s/(\"TestingAssemblyVersion\":\s*\")[^\"]+(\")/\1${VERSION}\2/" repo.json
sed -i -E "s/(\"DalamudApiLevel\":\s*)[0-9]+/\1${API}/" repo.json
sed -i -E "s/(\"TestingDalamudApiLevel\":\s*)[0-9]+/\1${API}/" repo.json

echo ">> Done. Review changes, then:"
echo "     git commit -am \"Update build $VERSION\" && git push"
