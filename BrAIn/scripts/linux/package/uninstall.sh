#!/bin/sh
# BrAIn for Linux: uninstaller. Removes the program folder this script sits in, the `brain` command, the
# menu entry and the icons that install.sh added. Run it as the same user (or root, for --system installs).
#
#   uninstall.sh            keep your settings, history and saved passwords (~/.local/share/BrAIn)
#   uninstall.sh --purge    delete those too, and BrAIn's cache (~/.cache/BrAIn)
set -eu

die() { echo "uninstall.sh: $*" >&2; exit 1; }

PURGE=0
for a in "$@"; do
  case "$a" in
    --purge) PURGE=1 ;;
    -h|--help) sed -n '2,7p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "unknown option: $a (see --help)" ;;
  esac
done

DIR=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
[ -f "$DIR/BrAIn" ] && [ -f "$DIR/release.txt" ] || die "$DIR does not look like a BrAIn install folder"
HOME_DIR=${HOME:-/nonexistent}

gone() { echo "removed $1"; }
# the program path as install.sh wrote it into the menu entry's Exec= (quoted when it has anything unusual in it)
exec_field() {
  case $1 in
    *[!A-Za-z0-9_./+:@,=-]*)
      printf '"%s"' "$(printf '%s' "$1" | sed -e 's/\\/\\\\\\\\/g' -e 's/"/\\\\"/g' -e 's/`/\\\\`/g' -e 's/\$/\\\\$/g' -e 's/%/%%/g')" ;;
    *) printf '%s' "$1" ;;
  esac
}
cannot() { echo "uninstall.sh: could not remove $1 (needs root?)" >&2; }

# the `brain` command, wherever install.sh put it: only a link that points at THIS program
for bin in "$HOME_DIR/.local/bin/brain" /usr/local/bin/brain; do
  if [ -L "$bin" ] && [ "$(readlink -f -- "$bin")" = "$DIR/BrAIn" ]; then
    if rm -f -- "$bin" 2>/dev/null; then gone "$bin"; else cannot "$bin"; fi
  fi
done

# the menu entry and the icons, per user and system-wide; an entry must point at this folder to be removed
USERDATA=${XDG_DATA_HOME:-$HOME_DIR/.local/share}
for data in "$USERDATA" /usr/share; do
  desk=$data/applications/brain.desktop
  ours=0
  if [ -f "$desk" ] && grep -qF -- "$(exec_field "$DIR/BrAIn")" "$desk"; then
    ours=1
    if rm -f -- "$desk" 2>/dev/null; then gone "$desk"; else cannot "$desk"; fi
  fi
  # icons of an install whose entry is already gone are cleared too, but only in the user's own folder
  if [ "$data" = "$USERDATA" ] && [ ! -f "$desk" ]; then ours=1; fi
  if [ "$ours" = 1 ]; then
    for ico in "$data"/icons/hicolor/*/apps/brain-browser.png; do
      [ -f "$ico" ] || continue
      rm -f -- "$ico" 2>/dev/null || cannot "$ico"
    done
    if command -v update-desktop-database >/dev/null 2>&1 && [ -d "$data/applications" ]; then
      update-desktop-database -q "$data/applications" 2>/dev/null || true
    fi
    if command -v gtk-update-icon-cache >/dev/null 2>&1 && [ -f "$data/icons/hicolor/index.theme" ]; then
      gtk-update-icon-cache -q -t -f "$data/icons/hicolor" 2>/dev/null || true
    fi
  fi
done

if [ "$PURGE" = 1 ]; then
  for d in "${XDG_DATA_HOME:-$HOME_DIR/.local/share}/BrAIn" "${XDG_CACHE_HOME:-$HOME_DIR/.cache}/BrAIn"; do
    if [ -d "$d" ]; then rm -rf -- "$d" && gone "$d"; fi
  done
fi

# the program folder last (this script is inside it): only what the release put there, so a folder that
# happens to hold other files of yours keeps them
rest=0
while IFS= read -r name; do
  case $name in ''|'#'*) continue ;; esac
  case $name in */*|.|..) continue ;; esac
  [ "$name" = release.txt ] && continue
  rm -rf -- "${DIR:?}/$name" 2>/dev/null || rest=1
done < "$DIR/release.txt"
rm -f -- "$DIR/release.txt" 2>/dev/null || rest=1
if rmdir -- "$DIR" 2>/dev/null; then
  gone "$DIR"
else
  echo "uninstall.sh: $DIR was left in place (it holds other files, or needs root to delete)" >&2
  [ "$rest" = 0 ] || exit 1
fi
if [ "$PURGE" = 0 ] && [ -d "${XDG_DATA_HOME:-$HOME_DIR/.local/share}/BrAIn" ]; then
  echo "your settings, history and passwords are kept in ${XDG_DATA_HOME:-$HOME_DIR/.local/share}/BrAIn (uninstall.sh --purge deletes them)"
fi
