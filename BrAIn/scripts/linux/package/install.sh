#!/bin/sh
# BrAIn for Linux: installer. Run it from the unpacked archive.
#
#   ./install.sh                 install for this user (no root needed):
#                                  program     ~/.local/share/brain-browser
#                                  command     ~/.local/bin/brain
#                                  menu entry  ~/.local/share/applications/brain.desktop (+ icons)
#                                A per-user install updates itself (toolbar menu > Check for updates).
#   sudo ./install.sh --system   install for all users: /opt/brain-browser, /usr/local/bin/brain,
#                                /usr/share/applications. Update it by running install.sh from a newer archive.
#   ./install.sh --prefix DIR    put the program in DIR instead (a folder only BrAIn will live in).
#
# Run uninstall.sh (installed next to the program) to remove it again. Settings, history and saved
# passwords live in ~/.local/share/BrAIn and are only deleted with: uninstall.sh --purge
set -eu

die() { echo "install.sh: $*" >&2; exit 1; }
warn() { echo "install.sh: $*" >&2; }

MODE=user PREFIX=
while [ $# -gt 0 ]; do
  case "$1" in
    --system) MODE=system ;;
    --user) MODE=user ;;
    --prefix) [ $# -ge 2 ] || die "--prefix needs a folder"; PREFIX=$2; shift ;;
    --prefix=*) PREFIX=${1#--prefix=} ;;
    -h|--help) sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "unknown option: $1 (see --help)" ;;
  esac
  shift
done

SRC=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
[ -f "$SRC/BrAIn" ] && [ -f "$SRC/Photino.Native.so" ] && [ -f "$SRC/release.txt" ] \
  || die "run install.sh from the unpacked BrAIn archive (BrAIn, Photino.Native.so and release.txt must be next to it)"

if [ "$MODE" = system ]; then
  [ "$(id -u)" -eq 0 ] || die "--system needs root: run  sudo $0 --system"
  PROG=${PREFIX:-/opt/brain-browser}
  BIN=/usr/local/bin
  DATA=/usr/share
else
  [ "$(id -u)" -ne 0 ] || die "this would install into root's home. Run it as your normal user, or add --system for an all-users install"
  [ -n "${HOME:-}" ] || die "HOME is not set"
  DATA=${XDG_DATA_HOME:-$HOME/.local/share}
  PROG=${PREFIX:-$DATA/brain-browser}
  BIN=$HOME/.local/bin
fi
case "$PROG" in /*) ;; *) PROG=$(pwd -P)/$PROG ;; esac
APPS=$DATA/applications
ICONS=$DATA/icons/hicolor
VERSION=$(sed -n '1s/^# BrAIn \([^ ]*\) .*/\1/p' "$SRC/release.txt")

# ---------------------------------------------------------------- system libraries (a warning, not a stop)
have_lib() {
  PATH="$PATH:/sbin:/usr/sbin" ldconfig -p 2>/dev/null | grep -q "$1" && return 0
  for d in /lib /lib64 /usr/lib /usr/lib64 /usr/lib/*-linux-gnu /usr/local/lib; do [ -e "$d/$1" ] && return 0; done
  return 1
}
hard='' soft=''
for l in libwebkit2gtk-4.1.so.0 libgtk-3.so.0; do have_lib "$l" || hard="$hard $l"; done
# the .NET runtime inside BrAIn needs OpenSSL 3 (1.1 also works). Fedora 45+ ships OpenSSL 4 and keeps 3 in its own package;
# without a version .NET knows it aborts the process a few seconds after start. The archive carries a copy (openssl/) that
# BrAIn uses when the system has none, so this is only a problem for an archive without it.
if ! have_lib libssl.so.3 && ! have_lib libssl.so.1.1; then
  if [ -s "$SRC/openssl/libssl.so.3" ] && [ -s "$SRC/openssl/libcrypto.so.3" ]; then
    echo "note: this system has no OpenSSL 3; BrAIn will use the copy it ships in openssl/"
  else
    hard="$hard libssl.so.3(OpenSSL-3)"
  fi
fi
for l in libnotify.so.4 libsecret-1.so.0; do have_lib "$l" || soft="$soft $l"; done
if [ -n "$hard$soft" ]; then
  [ -n "$hard" ] && warn "BrAIn will not start without:$hard"
  [ -n "$soft" ] && warn "recommended (notifications, saved-password keyring):$soft"
  ID=; LIKE=
  [ -r /etc/os-release ] && { ID=$(. /etc/os-release; echo "${ID:-}"); LIKE=$(. /etc/os-release; echo "${ID_LIKE:-}"); }
  case " $ID $LIKE " in
    *" debian "*|*" ubuntu "*) warn "install them with:  sudo apt install libwebkit2gtk-4.1-0 libgtk-3-0 libnotify4 libsecret-1-0 libssl3" ;;
    *" fedora "*|*" rhel "*)   warn "install them with:  sudo dnf install webkit2gtk4.1 gtk3 libnotify libsecret openssl3-libs   (openssl-libs before Fedora 45)" ;;
    *" arch "*)                warn "install them with:  sudo pacman -S webkit2gtk-4.1 gtk3 libnotify libsecret   (and an OpenSSL that provides libssl.so.3)" ;;
    *" suse "*|*" opensuse "*) warn "install them with:  sudo zypper install libwebkit2gtk-4_1-0 libgtk-3-0 libnotify4 libsecret-1-0 libopenssl3" ;;
    *) warn "packages: WebKitGTK 4.1, GTK 3, libnotify, libsecret, OpenSSL 3 (names differ per distribution)" ;;
  esac
fi

# without a display (a server, a container) BrAIn starts its own invisible screen: that needs Xvfb (a note, not a warning)
if ! command -v Xvfb >/dev/null 2>&1; then
  ID=; LIKE=
  [ -r /etc/os-release ] && { ID=$(. /etc/os-release; echo "${ID:-}"); LIKE=$(. /etc/os-release; echo "${ID_LIKE:-}"); }
  case " $ID $LIKE " in
    *" fedora "*|*" rhel "*|*" suse "*|*" opensuse "*) x=xorg-x11-server-Xvfb ;;
    *" arch "*) x=xorg-server-xvfb ;;
    *) x=xvfb ;;
  esac
  echo "note: to use BrAIn on a machine without a display (brain start / brain view over SSH), also install $x"
fi

# ---------------------------------------------------------------- the program folder
HERE=$(cd -- "$PROG" 2>/dev/null && pwd -P || true)
if [ "$HERE" = "$SRC" ]; then
  echo "BrAIn is already in $PROG"
else
  if [ -e "$PROG" ] && { [ ! -f "$PROG/release.txt" ] || [ ! -f "$PROG/BrAIn" ]; }; then
    die "$PROG exists and is not a BrAIn install. Pick another folder with --prefix, or move it away"
  fi
  mkdir -p -- "$(dirname -- "$PROG")"
  rm -rf -- "$PROG.new" "$PROG.old"
  mkdir -- "$PROG.new"
  cp -R -- "$SRC"/. "$PROG.new"/
  chmod -R u+rwX,go+rX -- "$PROG.new"
  [ -e "$PROG" ] && mv -- "$PROG" "$PROG.old"
  if ! mv -- "$PROG.new" "$PROG"; then
    [ -e "$PROG.old" ] && mv -- "$PROG.old" "$PROG"
    die "could not put the new version in place"
  fi
  rm -rf -- "$PROG.old"
fi

# ---------------------------------------------------------------- command on the PATH
mkdir -p -- "$BIN"
if [ -L "$BIN/brain" ] && case $(readlink -- "$BIN/brain") in */BrAIn) true ;; *) false ;; esac; then
  ln -sfn -- "$PROG/BrAIn" "$BIN/brain"
elif [ -e "$BIN/brain" ] || [ -L "$BIN/brain" ]; then
  warn "$BIN/brain already exists and is not a link to BrAIn: left alone (the program is $PROG/BrAIn)"
else
  ln -s -- "$PROG/BrAIn" "$BIN/brain"
fi

# ---------------------------------------------------------------- menu entry + icons
# Exec= wants its program quoted when the path has anything unusual in it (see the Desktop Entry spec: quotes
# and backslashes are escaped twice, a literal % is written %%)
exec_field() {
  case $1 in
    *[!A-Za-z0-9_./+:@,=-]*)
      printf '"%s"' "$(printf '%s' "$1" | sed -e 's/\\/\\\\\\\\/g' -e 's/"/\\\\"/g' -e 's/`/\\\\`/g' -e 's/\$/\\\\$/g' -e 's/%/%%/g')" ;;
    *) printf '%s' "$1" ;;
  esac
}
EXEC=$(exec_field "$PROG/BrAIn")
mkdir -p -- "$APPS"
TMP=$APPS/.brain.desktop.$$
while IFS= read -r line || [ -n "$line" ]; do
  case $line in
    '#'*) ;;
    *@EXEC@*) printf '%s\n' "${line%%@EXEC@*}$EXEC${line#*@EXEC@}" ;;
    *) printf '%s\n' "$line" ;;
  esac
done < "$PROG/brain.desktop" > "$TMP"
chmod 644 -- "$TMP"
mv -f -- "$TMP" "$APPS/brain.desktop"
if command -v desktop-file-validate >/dev/null 2>&1; then
  desktop-file-validate "$APPS/brain.desktop" || warn "desktop-file-validate complained about $APPS/brain.desktop (it should still work)"
fi

for dir in "$PROG"/icons/hicolor/*/apps; do
  [ -f "$dir/brain-browser.png" ] || continue
  size=$(basename -- "$(dirname -- "$dir")")
  mkdir -p -- "$ICONS/$size/apps"
  cp -f -- "$dir/brain-browser.png" "$ICONS/$size/apps/brain-browser.png"
  chmod 644 -- "$ICONS/$size/apps/brain-browser.png"
done

if command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database -q "$APPS" 2>/dev/null || true; fi
# a per-user icon folder has no index.theme of its own, and needs no cache: GTK reads it as it is
if command -v gtk-update-icon-cache >/dev/null 2>&1 && [ -f "$ICONS/index.theme" ]; then
  gtk-update-icon-cache -q -t -f "$ICONS" 2>/dev/null || true
fi

# ---------------------------------------------------------------- summary
echo
echo "BrAIn ${VERSION:+$VERSION }is installed."
echo "  program     $PROG"
echo "  command     $BIN/brain"
echo "  menu entry  $APPS/brain.desktop"
case ":$PATH:" in *":$BIN:"*) ;; *) echo "  note: $BIN is not on your PATH; start BrAIn from the menu, or add it to PATH" ;; esac
echo
echo "To let an AI agent drive it, point your MCP client at the program, e.g."
echo "  { \"mcpServers\": { \"brain\": { \"command\": \"$PROG/BrAIn\" } } }"
echo
echo "Uninstall:  $PROG/uninstall.sh"
