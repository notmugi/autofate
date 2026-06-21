#!/usr/bin/env bash
# Rebuilds AutoFates, copies the packaged zip to the repo root, and syncs repo.json's
# version fields to the freshly built plugin version. Then you just commit + push.
#
# Usage: ./update-build.sh
set -euo pipefail

cd "$(dirname "$0")"

echo ">> Cleaning stale build output (forces a real recompile so the DLL/manifest match the"
echo "   version in the csproj — incremental builds can skip recompiling after a version bump)..."
rm -f AutoFates/bin/x64/Release/AutoFates.dll \
      AutoFates/bin/x64/Release/AutoFates/AutoFates.dll \
      AutoFates/obj/x64/Release/AutoFates.dll 2>/dev/null || true

echo ">> Building Release (clean)..."
dotnet build AutoFates/AutoFates.csproj -c Release -t:Rebuild >/dev/null

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

# --- HARD VERIFICATION: repo.json, the zip's packaged manifest, and the actual DLL must ALL
# --- report the same version. Dalamud rejects a repo install if "distributed != repo" version,
# --- which is exactly the failure we keep hitting from stale incremental builds.
echo ">> Verifying version consistency..."
ZIP_VER=$(unzip -p ./latest.zip AutoFates.json | grep -oP '"AssemblyVersion":\s*"\K[^"]+')
REPO_VER=$(grep -oP '"AssemblyVersion":\s*"\K[^"]+' repo.json)
# DLL assembly version (strip dots->compare the printable version string embedded in the DLL).
DLL_VER=$(unzip -p ./latest.zip AutoFates.dll > /tmp/_af_check.dll && \
          grep -aoP "${VERSION//./\\.}" /tmp/_af_check.dll | head -1 || true)
rm -f /tmp/_af_check.dll

echo "   csproj/built manifest : $VERSION"
echo "   latest.zip manifest   : $ZIP_VER"
echo "   repo.json             : $REPO_VER"
echo "   DLL embedded version  : ${DLL_VER:-<not found>}"

if [ "$VERSION" != "$ZIP_VER" ] || [ "$VERSION" != "$REPO_VER" ] || [ "$DLL_VER" != "$VERSION" ]; then
  echo "" >&2
  echo "ERROR: version mismatch — refusing to continue. Dalamud will reject this install." >&2
  echo "       Fix the versions and re-run. (This usually means a stale build; the clean" >&2
  echo "       rebuild above should prevent it, so investigate if you see this.)" >&2
  exit 1
fi

echo ">> All versions match ($VERSION). Review changes, then:"
echo "     git commit -am \"Update build $VERSION\" && git push"
