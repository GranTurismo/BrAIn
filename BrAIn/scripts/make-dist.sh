#!/bin/bash
# Build all distributables into dist/:
#   dist/BrAIn-Setup-<ver>-x64.exe   Windows installer (NSIS; brew install makensis)
#   dist/BrAIn-<ver>-macOS-<arch>.dmg  macOS drag-install image
# Requires the mac app to be built first (scripts/rebuild-mac.command).
set -euo pipefail
cd "$(dirname "$0")/.."
# Version comes from the csproj (<Version>) so the binary the updater reports
# always matches the installer name. Override with an explicit arg if needed.
VER="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' BrAIn.csproj | head -1)}"
[ -n "$VER" ] || { echo "no <Version> in BrAIn.csproj"; exit 1; }
for p in /usr/local/share/dotnet /opt/homebrew/bin "$HOME/.dotnet"; do
  [ -x "$p/dotnet" ] && export PATH="$p:$PATH"
done

echo "== windows publish"
rm -rf dist/win-publish   # a stale publish dir can come back incomplete (missing natives / icon)
dotnet publish BrAIn.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false \
  -o dist/win-publish

echo "== windows installer (nsis)"
command -v makensis >/dev/null || { echo "makensis missing: brew install makensis"; exit 1; }
(cd scripts && makensis -DVERSION="$VER" installer-win.nsi)

echo "== macOS dmg"
APP="dist/BrAIn.app"
[ -d "$APP" ] || { echo "$APP missing — run scripts/rebuild-mac.command first"; exit 1; }
ARCH=arm64; [ "$(uname -m)" = "x86_64" ] && ARCH=x64
DMG="dist/BrAIn-$VER-macOS-$ARCH.dmg"
RW=/tmp/brain-rw.$$
rm -f "$DMG"; hdiutil detach /Volumes/BrAIn 2>/dev/null || true
hdiutil create -size 150m -fs HFS+ -volname "BrAIn" -type SPARSE "$RW.dmg" >/dev/null
hdiutil attach "$RW.dmg.sparseimage" -nobrowse >/dev/null
cp -R "$APP" /Volumes/BrAIn/
ln -s /Applications /Volumes/BrAIn/Applications
sync
hdiutil detach /Volumes/BrAIn >/dev/null
hdiutil convert "$RW.dmg.sparseimage" -format UDZO -imagekey zlib-level=9 -o "$DMG" >/dev/null
rm -f "$RW.dmg.sparseimage"

echo "== done"
ls -lh dist/BrAIn-Setup-*.exe "$DMG"
