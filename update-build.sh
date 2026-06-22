#!/usr/bin/env bash
# Dead-simple Autofate publisher.
#
#   ./update-build.sh                  -> clean rebuild, repackage, verify versions match (no push)
#   ./update-build.sh bump             -> also auto-bump the 4th version digit before building
#   ./update-build.sh --version X.Y.Z.W -> set an explicit version before building
#   ./update-build.sh push             -> rebuild, then commit + push
#   ./update-build.sh bump push        -> bump, rebuild, commit + push  (the usual one-liner)
#   ./update-build.sh --version 2.1.0.0 push -> set version, rebuild, commit + push
#
# The plugin version lives in Autofate/Autofate.csproj (<Version>). Everything else
# (latest.zip, repo.json) is synced to match it automatically.
set -euo pipefail
cd "$(dirname "$0")"

CSPROJ="Autofate/Autofate.csproj"
DO_BUMP=false
DO_PUSH=false
SET_VERSION=""
while [ $# -gt 0 ]; do
  case "$1" in
    bump) DO_BUMP=true ;;
    push) DO_PUSH=true ;;
    --version) shift; SET_VERSION="${1:-}" ;;
    --version=*) SET_VERSION="${1#*=}" ;;
    *) echo "Unknown arg: $1 (use 'bump', 'push', or '--version X.Y.Z.W')" >&2; exit 1 ;;
  esac
  shift
done

if [ -n "$SET_VERSION" ] && $DO_BUMP; then
  echo "ERROR: use either 'bump' or '--version', not both." >&2; exit 1
fi

# Explicit version: set <Version> to exactly what was passed (must be X.Y.Z.W).
if [ -n "$SET_VERSION" ]; then
  if ! [[ "$SET_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "ERROR: --version must be X.Y.Z.W (e.g. 2.1.0.0), got '$SET_VERSION'." >&2; exit 1
  fi
  CUR=$(grep -oP '<Version>\K[^<]+' "$CSPROJ")
  sed -i -E "s|<Version>[^<]+</Version>|<Version>${SET_VERSION}</Version>|" "$CSPROJ"
  echo ">> Version set: $CUR -> $SET_VERSION"
fi

# Optional: bump the 4th digit of <Version> (e.g. 2.0.0.4 -> 2.0.0.5).
if $DO_BUMP; then
  CUR=$(grep -oP '<Version>\K[^<]+' "$CSPROJ")
  IFS='.' read -r a b c d <<< "$CUR"
  NEW="$a.$b.$c.$((d + 1))"
  sed -i -E "s|<Version>[^<]+</Version>|<Version>${NEW}</Version>|" "$CSPROJ"
  echo ">> Version bumped: $CUR -> $NEW"
fi

# Clean rebuild. A clean build is required: incremental builds can skip recompiling after a
# version bump, leaving the DLL/manifest out of sync with the csproj (Dalamud then rejects it).
echo ">> Clean rebuild (Release)..."
rm -f Autofate/bin/x64/Release/Autofate.dll \
      Autofate/bin/x64/Release/Autofate/Autofate.dll \
      Autofate/obj/x64/Release/Autofate.dll 2>/dev/null || true
dotnet build "$CSPROJ" -c Release -t:Rebuild >/dev/null

PKG="Autofate/bin/x64/Release/Autofate"
[ -f "$PKG/latest.zip" ] || { echo "ERROR: build failed ($PKG/latest.zip missing)" >&2; exit 1; }

echo ">> Copying latest.zip to repo root..."
cp "$PKG/latest.zip" ./latest.zip

# Sync repo.json's version + API level to the freshly built manifest.
VERSION=$(grep -oP '"AssemblyVersion":\s*"\K[^"]+' "$PKG/Autofate.json")
API=$(grep -oP '"DalamudApiLevel":\s*\K[0-9]+' "$PKG/Autofate.json")
sed -i -E "s/(\"AssemblyVersion\":\s*\")[^\"]+(\")/\1${VERSION}\2/"        repo.json
sed -i -E "s/(\"TestingAssemblyVersion\":\s*\")[^\"]+(\")/\1${VERSION}\2/" repo.json
sed -i -E "s/(\"DalamudApiLevel\":\s*)[0-9]+/\1${API}/"                    repo.json
sed -i -E "s/(\"TestingDalamudApiLevel\":\s*)[0-9]+/\1${API}/"             repo.json

# Verify: built manifest, zipped manifest, repo.json, and the DLL must all report one version.
# (Dalamud rejects an install when "distributed != repo" version — the classic stale-build bug.)
ZIP_VER=$(unzip -p ./latest.zip Autofate.json | grep -oP '"AssemblyVersion":\s*"\K[^"]+')
REPO_VER=$(grep -oP '"AssemblyVersion":\s*"\K[^"]+' repo.json)
unzip -p ./latest.zip Autofate.dll > /tmp/_af_check.dll
DLL_VER=$(grep -aoP "${VERSION//./\\.}" /tmp/_af_check.dll | head -1 || true)
rm -f /tmp/_af_check.dll

echo ">> Versions — built:$VERSION zip:$ZIP_VER repo:$REPO_VER dll:${DLL_VER:-MISSING}"
if [ "$VERSION" != "$ZIP_VER" ] || [ "$VERSION" != "$REPO_VER" ] || [ "$DLL_VER" != "$VERSION" ]; then
  echo "ERROR: version mismatch — refusing to publish (Dalamud would reject it)." >&2
  exit 1
fi

if $DO_PUSH; then
  echo ">> Committing + pushing..."
  git add -A
  git commit -m "Update build $VERSION"
  git push
  echo ">> Pushed $VERSION. (raw.githubusercontent is CDN-cached ~5 min.)"
else
  echo ">> OK ($VERSION). To publish:  git commit -am \"Update build $VERSION\" && git push"
  echo "   (or re-run: ./update-build.sh push)"
fi
