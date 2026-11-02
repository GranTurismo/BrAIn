#!/bin/bash
# Runs in the test rig: unpack the release archive from /pkg, install it, check what landed where, uninstall.
# (The CI workflow runs it on its runner: RIG=<scripts/linux> PKG=<dist> with HOME pointing at a scratch folder.)
#   inside-package.sh user      as the normal user: ./install.sh, uninstall.sh, odd paths, refusals
#   inside-package.sh system    as root: ./install.sh --system and its uninstall
# RIG (these scripts) and PKG (the archives) are /rig and /pkg in the container; the CI workflow runs the script on the runner itself
RIG=${RIG:-/rig}; PKG=${PKG:-/pkg}
. "$RIG/lib.sh"
MODE=${1:-user}
case "$(uname -m)" in x86_64) A=x64 ;; *) A=arm64 ;; esac
set -- "$PKG"/BrAIn-*-linux-"$A".tar.gz; ARCHIVE=$1; set --
NAME=$(basename "$ARCHIVE" .tar.gz)               # BrAIn-<version>-linux-<arch>
VER=${NAME#BrAIn-}; VER=${VER%%-linux-*}
SIZES="16 22 24 32 48 64 128 256 512"
W=$(mktemp -d); cd "$W" || exit 1

section "the archive ($NAME.tar.gz)"
mkdir "$W/x"; tar xzf "$ARCHIVE" -C "$W/x" 2>"$W/tar.err"
expect_eq "unpacks without tar warnings (no macOS metadata)" "" "$(cat "$W/tar.err")"
expect_eq "holds exactly one folder, named like the archive" "$NAME" "$(ls -A "$W/x")"
cd "$W/x/$NAME" || exit 1
for f in BrAIn Photino.Native.so install.sh uninstall.sh; do check "$f is executable" test -x "$f"; done
for f in brain.desktop release.txt README.txt Assets/icon.png; do check "$f is there" test -f "$f"; done
for s in $SIZES; do check "icon ${s}x${s}" test -s "icons/hicolor/${s}x${s}/apps/brain-browser.png"; done
expect_eq "release.txt lists exactly the folder's entries" \
  "$(ls -A | LC_ALL=C sort)" "$(grep -v '^#' release.txt | LC_ALL=C sort)"
expect_eq "the program reports the archive's version" "$VER.0" "$(./BrAIn --version 2>&1)"
check "every entry is owned by root (no builder's user name in the archive)" bash -c "! tar tvzf '$ARCHIVE' | grep -Ev ' (root|0)/(root|0) ' | grep ."
check "the desktop entry template has no MimeType (BrAIn is not a link handler)" bash -c "! grep -q '^MimeType' brain.desktop"
check "the desktop entry names the window class the app really has (BrAIn)" grep -qx 'StartupWMClass=BrAIn' brain.desktop

# what ended up where, for the given install flavour
verify_install() { # <program dir> <bin dir> <data dir (share)>
  local P=$1 BIN=$2 DATA=$3 D
  D=$DATA/applications/brain.desktop
  check "program installed"                 test -x "$P/BrAIn"
  check "native library installed"          test -f "$P/Photino.Native.so"
  check "window icon installed"             test -f "$P/Assets/icon.png"
  check "release.txt installed"             test -f "$P/release.txt"
  check "uninstall.sh installed"            test -x "$P/uninstall.sh"
  check "no .new / .old left beside it"     bash -c "! ls -d '$P.new' '$P.old' 2>/dev/null | grep ."
  expect_eq "brain command is a link to the program" "$P/BrAIn" "$(readlink -f "$BIN/brain")"
  check "installed program runs"            "$P/BrAIn" --version
  check "menu entry written"                test -f "$D"
  check "menu entry starts the installed program" grep -qx "Exec=$P/BrAIn %u" "$D"
  check "menu entry has no MimeType"        bash -c "! grep -q '^MimeType' '$D'"
  check "menu entry has no leftover template marker" bash -c "! grep -q '@EXEC@' '$D'"
  check "menu entry is a valid desktop file" desktop-file-validate "$D"
  for s in $SIZES; do check "icon ${s}x${s} installed" test -s "$DATA/icons/hicolor/${s}x${s}/apps/brain-browser.png"; done
}
verify_gone() { # <program dir> <bin dir> <data dir (share)>
  local P=$1 BIN=$2 DATA=$3 s
  check "program folder removed"   test ! -e "$P"
  check "brain command removed"    test ! -e "$BIN/brain" -a ! -L "$BIN/brain"
  check "menu entry removed"       test ! -e "$DATA/applications/brain.desktop"
  local left=0; for s in $SIZES; do [ -e "$DATA/icons/hicolor/${s}x${s}/apps/brain-browser.png" ] && left=1; done
  expect_eq "icons removed" 0 "$left"
}

if [ "$MODE" = system ]; then
  section "--system install as root"
  ./install.sh --system >"$W/inst.out" 2>&1; expect_eq "install.sh --system succeeds" 0 "$?"
  verify_install /opt/brain-browser /usr/local/bin /usr/share
  check "icon cache refreshed (gtk-update-icon-cache)" test -s /usr/share/icons/hicolor/icon-theme.cache
  expect_eq "program folder is readable by everybody" "755" "$(stat -c %a /opt/brain-browser)"
  expect_eq "icons are world-readable" "644" "$(stat -c %a /usr/share/icons/hicolor/48x48/apps/brain-browser.png)"
  section "--system uninstall"
  /opt/brain-browser/uninstall.sh >"$W/uninst.out" 2>&1; expect_eq "uninstall.sh succeeds" 0 "$?"
  verify_gone /opt/brain-browser /usr/local/bin /usr/share
  section "refusals"
  ./install.sh >/dev/null 2>"$W/err"; expect_eq "plain install as root is refused" 1 "$?"
  check "...with an explanation" grep -q -- '--system' "$W/err"
  finish; exit $?
fi

section "per-user install"
./install.sh >"$W/inst.out" 2>&1; expect_eq "install.sh succeeds" 0 "$?"
check "says where the program went" grep -q "program     $HOME/.local/share/brain-browser" "$W/inst.out"
verify_install "$HOME/.local/share/brain-browser" "$HOME/.local/bin" "$HOME/.local/share"
expect_eq "program folder is 755" "755" "$(stat -c %a "$HOME/.local/share/brain-browser")"

section "installing again (upgrade in place)"
echo stale > "$HOME/.local/share/brain-browser/stale-file"
./install.sh >"$W/inst2.out" 2>&1; expect_eq "second install.sh succeeds" 0 "$?"
check "replaces the folder (stale file gone)" test ! -e "$HOME/.local/share/brain-browser/stale-file"
verify_install "$HOME/.local/share/brain-browser" "$HOME/.local/bin" "$HOME/.local/share"
"$HOME/.local/share/brain-browser/install.sh" >"$W/inst3.out" 2>&1; expect_eq "install.sh from the installed copy succeeds" 0 "$?"
check "...and says it is already there" grep -q "already in" "$W/inst3.out"
verify_install "$HOME/.local/share/brain-browser" "$HOME/.local/bin" "$HOME/.local/share"

section "uninstall (keeps the user's data)"
mkdir -p "$HOME/.local/share/BrAIn"; echo '{"welcomed":true}' > "$HOME/.local/share/BrAIn/prefs.json"
"$HOME/.local/share/brain-browser/uninstall.sh" >"$W/uninst.out" 2>&1; expect_eq "uninstall.sh succeeds" 0 "$?"
verify_gone "$HOME/.local/share/brain-browser" "$HOME/.local/bin" "$HOME/.local/share"
check "settings and data are kept" test -f "$HOME/.local/share/BrAIn/prefs.json"
check "...and it says so" grep -q "are kept in" "$W/uninst.out"

section "uninstall --purge"
./install.sh >/dev/null 2>&1
mkdir -p "$HOME/.cache/BrAIn/update"
"$HOME/.local/share/brain-browser/uninstall.sh" --purge >"$W/purge.out" 2>&1; expect_eq "uninstall.sh --purge succeeds" 0 "$?"
check "data folder deleted" test ! -e "$HOME/.local/share/BrAIn"
check "cache folder deleted" test ! -e "$HOME/.cache/BrAIn"

section "a path with spaces, quotes, \$, backticks, backslashes and %"
ODD="$W/odd dir \"q\" \$HOME \`bt\` back\\slash 100%"
mkdir -p "$ODD"
XDG_DATA_HOME="$ODD/data" ./install.sh --prefix "$ODD/prog" >"$W/odd.out" 2>&1; expect_eq "install.sh --prefix with an odd path succeeds" 0 "$?"
check "program installed there" test -x "$ODD/prog/BrAIn"
check "menu entry valid" desktop-file-validate "$ODD/data/applications/brain.desktop"
python3 - "$ODD/data/applications/brain.desktop" "$ODD/prog/BrAIn" <<'PY' >"$W/py.out" 2>&1
import sys, gi
from gi.repository import GLib
kf = GLib.KeyFile(); kf.load_from_file(sys.argv[1], GLib.KeyFileFlags.NONE)
argv = GLib.shell_parse_argv(kf.get_string('Desktop Entry', 'Exec'))[1]
prog = argv[0].replace('%%', '%')
print('Exec parses to:', argv)
sys.exit(0 if prog == sys.argv[2] and argv[1:] == ['%u'] else 1)
PY
expect_eq "the desktop entry's Exec, parsed the way a launcher does, is the program path" 0 "$?"
XDG_DATA_HOME="$ODD/data" "$ODD/prog/uninstall.sh" >/dev/null 2>&1
check "odd-path install uninstalled" test ! -e "$ODD/prog"
check "...with its menu entry" test ! -e "$ODD/data/applications/brain.desktop"
rm -rf "$HOME/.local/bin/brain" "$ODD"

section "refusals"
mkdir -p "$W/precious"; echo keep > "$W/precious/file"
./install.sh --prefix "$W/precious" >/dev/null 2>"$W/err"; expect_eq "--prefix onto a folder that is not a BrAIn install is refused" 1 "$?"
expect_eq "...and the folder is untouched" keep "$(cat "$W/precious/file")"
./install.sh --system >/dev/null 2>"$W/err"; expect_eq "--system without root is refused" 1 "$?"
check "...with the sudo hint" grep -q sudo "$W/err"
./install.sh --bogus >/dev/null 2>&1; expect_eq "an unknown option is refused" 1 "$?"
( cd / && "$W/x/$NAME/install.sh" --help >/dev/null 2>&1 ); expect_eq "--help works from anywhere" 0 "$?"
mkdir -p "$W/bare"; cp install.sh "$W/bare/"
"$W/bare/install.sh" >/dev/null 2>"$W/err"; expect_eq "install.sh away from the archive's files is refused" 1 "$?"

section "uninstall leaves files that are not BrAIn's"
./install.sh >/dev/null 2>&1
echo mine > "$HOME/.local/share/brain-browser/my-notes.txt"
"$HOME/.local/share/brain-browser/uninstall.sh" >"$W/uninst2.out" 2>&1; expect_eq "uninstall.sh reports the leftover (exit 0)" 0 "$?"
expect_eq "the user's own file is still there" mine "$(cat "$HOME/.local/share/brain-browser/my-notes.txt")"
check "the program itself is gone" test ! -e "$HOME/.local/share/brain-browser/BrAIn"
check "...and the message says why the folder stayed" grep -q "left in place" "$W/uninst2.out"
rm -rf "$HOME/.local/share/brain-browser" "$HOME/.local/bin/brain" "$HOME/.local/share/applications/brain.desktop"

finish
