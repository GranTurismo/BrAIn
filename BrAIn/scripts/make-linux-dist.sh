#!/bin/bash
# Build the Linux release archives into dist/ (make-dist.sh runs this too):
#   dist/BrAIn-<ver>-linux-x64.tar.gz
#   dist/BrAIn-<ver>-linux-arm64.tar.gz
# Cross-publishes with the .NET SDK, so it runs on the Mac or on Linux. Needs one of: sips (macOS), ImageMagick
# (magick / convert) or python3 + Pillow, to make the menu icons.
#
# usage: scripts/make-linux-dist.sh [version [x64] [arm64]]     (default: the <Version> of BrAIn.csproj, both)
#
# Each archive holds ONE folder, BrAIn-<ver>-linux-<arch>/ (not "BrAIn/": that is also the name of the data
# folder under ~/.local/share, and a versioned name keeps two unpacked releases from mixing):
#   BrAIn  Photino.Native.so  Assets/icon.png  icons/hicolor/<n>x<n>/apps/brain-browser.png
#   openssl/libssl.so.3  openssl/libcrypto.so.3  openssl/LICENSE.txt   (OpenSSL 3, a fallback: third_party/openssl/README.md)
#   brain.desktop  install.sh  uninstall.sh  README.txt  release.txt
# release.txt lists those entries: the self-updater (Browser/Updater.cs) only swaps a folder that holds nothing
# but what is listed there. The archive name must stay BrAIn-<ver>-linux-<x64|arm64>.tar.gz: the updater, the
# website's download cards and the macOS/Windows updaters (which pick their own files by extension) rely on it.
#
# Environment: DIST=<dir> to build somewhere other than dist/ (the tests do).
set -euo pipefail
cd "$(dirname "$0")/.."
VER="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' BrAIn.csproj | head -1)}"
[ -n "$VER" ] || { echo "no <Version> in BrAIn.csproj"; exit 1; }
shift || true
ARCHES=("$@")
[ "${#ARCHES[@]}" -gt 0 ] || ARCHES=(x64 arm64)
DIST="${DIST:-dist}"
PKG=scripts/linux/package
for p in /usr/local/share/dotnet /opt/homebrew/bin "$HOME/.dotnet"; do
  [ -x "$p/dotnet" ] && export PATH="$p:$PATH"
done
command -v dotnet >/dev/null || { echo "dotnet SDK not found"; exit 1; }

# ---- icons: PNGs of the usual sizes, from the 1024 px master (the 256 px window icon when it is missing)
ICON_SRC=Assets/icon-src.png; [ -f "$ICON_SRC" ] || ICON_SRC=Assets/icon.png
ICON_SIZES="16 22 24 32 48 64 128 256 512"
resize() { # <size> <out.png>
  if command -v sips >/dev/null; then sips -z "$1" "$1" "$ICON_SRC" --out "$2" >/dev/null
  elif command -v magick >/dev/null; then magick "$ICON_SRC" -filter Lanczos -resize "${1}x${1}" "$2"
  elif command -v convert >/dev/null; then convert "$ICON_SRC" -filter Lanczos -resize "${1}x${1}" "$2"
  elif python3 -c 'import PIL' 2>/dev/null; then
    python3 -c 'import sys; from PIL import Image; Image.open(sys.argv[1]).convert("RGBA").resize((int(sys.argv[3]),)*2, Image.LANCZOS).save(sys.argv[2])' "$ICON_SRC" "$2" "$1"
  else echo "need sips, ImageMagick or python3 + Pillow to scale the icon"; exit 1; fi
}

# ---- tar with root ownership and no macOS metadata, whichever tar this is (bsdtar on the Mac, GNU tar on Linux)
pack() { # <stage dir> <folder> <out.tar.gz>
  export COPYFILE_DISABLE=1
  if tar --version 2>/dev/null | grep -i bsdtar >/dev/null; then
    tar -cf - -C "$1" --uid 0 --gid 0 --uname root --gname root --no-xattrs "$2" | gzip -9 -n > "$3"
  else
    tar -cf - -C "$1" --owner=root --group=root --sort=name "$2" | gzip -9 -n > "$3"
  fi
}

mkdir -p "$DIST"
for ARCH in "${ARCHES[@]}"; do
  case "$ARCH" in x64|arm64) ;; *) echo "unknown architecture '$ARCH' (x64 or arm64)"; exit 1 ;; esac
  NAME="BrAIn-$VER-linux-$ARCH"
  PUB="$DIST/linux-$ARCH-publish"
  STAGE="$DIST/linux-stage-$ARCH"
  echo "== linux-$ARCH publish ($VER)"
  rm -rf "$PUB" "$STAGE"   # a stale publish dir can come back incomplete
  # -p:Version: the binary reports exactly the version in the archive name, whichever way VER was given
  dotnet publish BrAIn.csproj -c Release -r "linux-$ARCH" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:Version="$VER" -o "$PUB"
  # macOS file systems are case-insensitive: ask the directory listing, not the file system, for the exact names
  for f in BrAIn Photino.Native.so; do
    # (the directory listing, not a glob, on purpose: a glob would find the wrong-case name too)
    # shellcheck disable=SC2010
    ls "$PUB" | grep -Fx "$f" >/dev/null || { echo "$PUB/$f missing (wrong case?)"; ls "$PUB"; exit 1; }
  done
  want="x86-64"; [ "$ARCH" = arm64 ] && want="aarch64"
  if command -v file >/dev/null; then
    kind=$(file "$PUB/BrAIn")
    case "$kind" in *"$want"*) ;; *) echo "$PUB/BrAIn is not a $ARCH build: $kind"; exit 1 ;; esac
  fi

  echo "== linux-$ARCH archive"
  D="$STAGE/$NAME"
  mkdir -p "$D/Assets"
  cp "$PUB/BrAIn" "$PUB/Photino.Native.so" "$D/"
  cp Assets/icon.png "$D/Assets/icon.png"
  for s in $ICON_SIZES; do
    mkdir -p "$D/icons/hicolor/${s}x${s}/apps"
    resize "$s" "$D/icons/hicolor/${s}x${s}/apps/brain-browser.png"
  done
  # OpenSSL 3, used only when the system has none .NET can use (Fedora 45+): see third_party/openssl/README.md
  SSL="third_party/openssl/linux-$ARCH"
  for f in libssl.so.3 libcrypto.so.3; do [ -s "$SSL/$f" ] || { echo "$SSL/$f missing"; exit 1; }; done
  if command -v file >/dev/null; then
    for f in libssl.so.3 libcrypto.so.3; do
      case "$(file "$SSL/$f")" in *"$want"*) ;; *) echo "$SSL/$f is not a $ARCH library"; exit 1 ;; esac
    done
  fi
  mkdir -p "$D/openssl"
  cp "$SSL/libssl.so.3" "$SSL/libcrypto.so.3" third_party/openssl/LICENSE.txt "$D/openssl/"
  cp "$PKG/brain.desktop" "$PKG/install.sh" "$PKG/uninstall.sh" "$PKG/README.txt" "$D/"
  {
    echo "# BrAIn $VER linux-$ARCH: the files this release put in this folder."
    echo "# BrAIn's self-updater only replaces a folder that holds nothing but the entries below,"
    echo "# so it can never delete files of yours that happen to live next to the program."
    { (cd "$D" && ls -A); echo release.txt; } | LC_ALL=C sort -u
  } > "$D/release.txt"
  find "$D" -type d -exec chmod 755 {} +
  find "$D" -type f -exec chmod 644 {} +
  chmod 755 "$D/BrAIn" "$D/Photino.Native.so" "$D/install.sh" "$D/uninstall.sh"

  OUT="$DIST/$NAME.tar.gz"
  rm -f "$OUT"
  pack "$STAGE" "$NAME" "$OUT"
  rm -rf "$STAGE"
  tar -tzf "$OUT" | grep -v '/icons/' || true
  shasum -a 256 "$OUT" 2>/dev/null || sha256sum "$OUT"
done

echo "== done"
ls -lh "$DIST"/BrAIn-"$VER"-linux-*.tar.gz
