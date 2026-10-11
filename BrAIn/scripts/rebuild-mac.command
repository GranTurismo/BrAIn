#!/bin/bash
# Double-click to rebuild dist/BrAIn.app from source and relaunch it.
# Output is also saved to scripts/rebuild.log.
cd "$(dirname "$0")/.." || exit 1          # -> the BrAIn project folder
LOG="scripts/rebuild.log"
exec > >(tee "$LOG") 2>&1
echo "== BrAIn rebuild $(date) =="

if ! command -v dotnet >/dev/null 2>&1; then
  for p in /usr/local/share/dotnet /opt/homebrew/bin "$HOME/.dotnet"; do [ -x "$p/dotnet" ] && export PATH="$p:$PATH"; done
fi
command -v dotnet >/dev/null 2>&1 || { echo "ERROR: dotnet SDK not found"; echo "BUILD FAILED"; exit 1; }
dotnet --version

RID=osx-arm64; [ "$(uname -m)" = "x86_64" ] && RID=osx-x64
APP="dist/BrAIn.app"
OUT="$(mktemp -d /tmp/brain-pub.XXXXXX)"

echo "-- publish ($RID)"
if ! dotnet publish BrAIn.csproj -c Release -r "$RID" --self-contained true \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -o "$OUT"; then
  echo "BUILD FAILED"; exit 1
fi

echo "-- stop running BrAIn"
pkill -x BrAIn 2>/dev/null; sleep 1

echo "-- install into $APP"
mkdir -p "$APP/Contents/MacOS"
rsync -a --exclude '*.pdb' --exclude 'runtimes' --exclude 'WebView2Loader.dll' "$OUT"/ "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/BrAIn"
xattr -cr "$APP" 2>/dev/null

echo "-- permissions + sign"
bash scripts/mac-permissions.sh "$APP" || { echo "SIGN FAILED"; exit 1; }

echo "-- launch"
open "$APP"
echo "BUILD OK"
