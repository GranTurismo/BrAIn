#!/bin/bash
# The self-update test, as the normal user (started by inside-update.sh). Real app, real X server, real keyboard
# input: the update is started the way a person would, from the command palette (Ctrl+K, "update", Enter).
. /rig/lib.sh

LOG=$HOME/.local/share/BrAIn/brain.log           # the app's own log (BRAIN_LOG=1): the phases of the updater show up in it
UPDLOG=$HOME/.local/share/BrAIn/update.log       # what the detached helper did
CACHE=$HOME/.cache/BrAIn/update
P=$HOME/.local/share/brain-browser
ARCH=arm64; case "$(uname -m)" in x86_64) ARCH=x64 ;; esac
OLD_VER=$(basename "$OLD" .tar.gz); OLD_VER=${OLD_VER#BrAIn-}; OLD_VER=${OLD_VER%%-linux-*}
export BRAIN_LOG=1

mark() { if [ -f "$LOG" ]; then wc -l < "$LOG"; else echo 0; fi; }   # lines in the log now; waits below only look at later ones
wait_log() {                                              # wait_log <pattern> <seconds> <after-line>
  local i
  for ((i = 0; i < $2 * 5; i++)); do
    tail -n +$(($3 + 1)) "$LOG" 2>/dev/null | grep -q -- "$1" && return 0
    sleep 0.2
  done
  return 1
}
wait_cond() { local i; for ((i = 0; i < $1 * 5; i++)); do eval "$2" && return 0; sleep 0.2; done; return 1; }   # wait_cond <seconds> <shell test>
alive() { kill -0 "$1" 2>/dev/null && [ "$(awk '{print $3}' "/proc/$1/stat" 2>/dev/null)" != Z ]; }
pids_of() { pgrep -f "^$1\$" || true; }                   # processes whose whole command line is this path
shot() { import -window root "/out/update-$1.png" 2>/dev/null; }
tree_hash() { (cd "$1" && find . -type f -print0 | sort -z | xargs -0 sha256sum | sha256sum | cut -c1-16); }
launch() { "$1" </dev/null >/tmp/app.out 2>/tmp/app.err & APP_PID=$!; }   # like a desktop launch: stdin is not a pipe, so the app stays up
stop_app() {   # ask nicely, then insist
  local p
  for p in $(pids_of "$1"); do kill "$p" 2>/dev/null; done
  wait_cond 8 "[ -z \"\$(pids_of '$1')\" ]" || { for p in $(pids_of "$1"); do kill -KILL "$p" 2>/dev/null; done; sleep 1; }
}
# what a person does: open the command palette (Ctrl+K), type "update", pick the update row (the first row is "Search the web
# for ...") and press Enter. The row checks, downloads or installs, whichever is next.
update_keys() {
  xdotool key --clearmodifiers ctrl+k; sleep 0.9
  xdotool type --delay 90 update; sleep 0.8
  xdotool key Down; sleep 0.3
  xdotool key Return
}
release() { python3 /rig/set-release.py "$FAKE" "$@" >/dev/null; }

start_desktop; skip_welcome

section "setup"
check "the fake GitHub answers over TLS the container trusts" curl -sf https://api.github.com/repos/GranTurismo/BrAIn/releases/latest
mkdir -p /tmp/old && tar xzf "$OLD" -C /tmp/old
INSTALLER=$(echo /tmp/old/*/install.sh)
"$INSTALLER" >/tmp/install.out 2>&1; expect_eq "installed $OLD_VER with install.sh" 0 "$?"
expect_eq "the installed program is $OLD_VER" "$OLD_VER.0" "$("$P/BrAIn" --version 2>&1)"

# ---------------------------------------------------------------------------------------------------------------
section "A. update from the toolbar: download, unpack, swap, relaunch"
release v9.9.9 "$NEW"
m=$(mark); launch "$P/BrAIn"; OLDPID=$APP_PID
check "the app sees 9.9.9 is available" wait_log "update: available 9.9.9" 60 "$m"
sleep 2; shot A1-before
update_keys
check "it downloads, verifies and starts installing" wait_log "update: installing 9.9.9" 90 "$m"
shot A2-installing
check "the old process exits" wait_cond 60 "! alive $OLDPID"
wait_cond 40 "[ -n \"\$(pids_of '$P/BrAIn' | grep -v '^$OLDPID\$')\" ]"
NEWPID=$(pids_of "$P/BrAIn" | grep -v "^$OLDPID\$" | head -1)
check "a new process runs from the same path" test -n "$NEWPID"
expect_eq "the program on disk is now 9.9.9" "9.9.9.0" "$("$P/BrAIn" --version 2>&1)"
expect_eq "release.txt is the new release's" "# BrAIn 9.9.9 linux-$ARCH" "$(head -1 "$P/release.txt" | cut -d: -f1)"
check "the previous version was cleaned up (no .old / .new / unpack folders)" wait_cond 30 "[ \"\$(ls -A ~/.local/share | grep -c brain-browser)\" = 1 ]"
check "the download and the helper script were cleaned up" wait_cond 30 "[ -z \"\$(ls -A '$CACHE' 2>/dev/null)\" ]"
check "the helper logged the swap" grep -q "swapped in the new version" "$UPDLOG"
check "...and the clean-up" grep -q "removed the previous version" "$UPDLOG"
check "the relaunched app is the new version (it reports up to date at 9.9.9)" wait_log "update: uptodate 9.9.9" 60 "$m"
check "the relaunched app is still running" alive "$NEWPID"
shot A3-after
check "the program, command link and menu entry are where they were" bash -c "[ \"\$(readlink -f ~/.local/bin/brain)\" = '$P/BrAIn' ] && grep -qx 'Exec=$P/BrAIn %u' ~/.local/share/applications/brain.desktop"
OTHER=x64; [ "$ARCH" = x64 ] && OTHER=arm64
expect_eq "this platform's archive was downloaded exactly once" 1 "$(grep -c "releases/download/v9.9.9/BrAIn-9.9.9-linux-$ARCH.tar.gz" "$FAKE/requests.log")"
expect_eq "no other platform's file was asked for" 0 "$(grep -cE "download/.*(macOS|\.exe|linux-$OTHER)" "$FAKE/requests.log")"
APP_PID=$NEWPID

# ---------------------------------------------------------------------------------------------------------------
section "B. a damaged download: nothing is touched, the app tells you"
head -c $(($(stat -c %s "$NEW") / 2)) "$NEW" > /tmp/truncated.tar.gz
release v9.9.10 /tmp/truncated.tar.gz
before=$(tree_hash "$P"); m=$(mark)
update_keys
check "9.9.10 is offered" wait_log "update: available 9.9.10" 40 "$m"
update_keys
check "it is downloaded and verified (the digest matches the damaged file)" wait_log "update: installing 9.9.10" 90 "$m"
check "unpacking fails and the updater reports an error" wait_log "update: error 9.9.10" 60 "$m"
sleep 1; shot B1-damaged
check "the app is still running" alive "$APP_PID"
expect_eq "the install folder is byte for byte what it was" "$before" "$(tree_hash "$P")"
check "nothing was left beside it" bash -c "[ \"\$(ls -A ~/.local/share | grep -c brain-browser)\" = 1 ]"
check "the damaged download was deleted" test -z "$(ls -A "$CACHE" 2>/dev/null)"

section "B2. an archive whose program does not start"
python3 - "$NEW" /tmp/broken.tar.gz <<'PY'
import io, sys, tarfile
with tarfile.open(sys.argv[1]) as a, tarfile.open(sys.argv[2], 'w:gz') as b:
    for m in a:
        if m.isfile() and m.name.endswith('/BrAIn'):
            data = b'#!/bin/sh\nexit 3\n'; m.size = len(data); b.addfile(m, io.BytesIO(data))
        elif m.isfile(): b.addfile(m, a.extractfile(m))
        else: b.addfile(m)
PY
release v9.9.11 /tmp/broken.tar.gz
m=$(mark)
update_keys
check "9.9.11 is offered" wait_log "update: available 9.9.11" 40 "$m"
update_keys
check "the new program is run once before anything is swapped, and refused" wait_log "update: error 9.9.11" 90 "$m"
sleep 1; shot B2-broken
check "the app is still running" alive "$APP_PID"
expect_eq "the install folder is byte for byte what it was" "$before" "$(tree_hash "$P")"
check "nothing was left beside it" bash -c "[ \"\$(ls -A ~/.local/share | grep -c brain-browser)\" = 1 ]"
stop_app "$P/BrAIn"

# ---------------------------------------------------------------------------------------------------------------
section "C. the swap itself fails: the old version is kept and started again"
S=/srv/shared/brain-browser     # root's folder in a sticky directory: the user may add entries there but not rename this one
release v9.9.12 "$NEW"
expect_eq "the folder belongs to somebody else" root "$(stat -c %U "$S")"
sbefore=$(tree_hash "$S"); m=$(mark)
launch "$S/BrAIn"; STKPID=$APP_PID
check "the updater accepts it (manifest ok, the shared directory is writable)" wait_log "update: available 9.9.12" 60 "$m"
sleep 2; update_keys
check "it unpacks and hands over to the helper" wait_log "update: installing 9.9.12" 90 "$m"
check "the app exits" wait_cond 60 "! alive $STKPID"
wait_cond 40 "[ -n \"\$(pids_of '$S/BrAIn' | grep -v '^$STKPID\$')\" ]"
check "the old version was started again from the same path" test -n "$(pids_of "$S/BrAIn" | grep -v "^$STKPID\$")"
check "the helper says why it kept it" wait_cond 20 "grep -q 'could not move the installed version aside' '$UPDLOG'"
expect_eq "the folder is byte for byte what it was" "$sbefore" "$(tree_hash "$S")"
check "the staged copy was removed" wait_cond 20 "[ ! -e '$S.new' ]"
stop_app "$S/BrAIn"

# ---------------------------------------------------------------------------------------------------------------
section "D. installs the updater must leave alone"
release v9.9.13 "$NEW"
m=$(mark)
launch /opt/brain-browser/BrAIn
check "system-wide install (/opt, root's): updates are off" wait_log "update: disabled 9.9.13" 60 "$m"
sleep 2; update_keys; sleep 2; shot D1-system-wide
stop_app /opt/brain-browser/BrAIn

mkdir -p "$HOME/d2" && cp -a "$P" "$HOME/d2/brain-browser" && echo notes > "$HOME/d2/brain-browser/notes.txt"
m=$(mark)
launch "$HOME/d2/brain-browser/BrAIn"
check "a folder holding a file that is not BrAIn's: updates are off" wait_log "update: disabled 9.9.13" 60 "$m"
sleep 2; update_keys; sleep 2; shot D2-foreign-file
stop_app "$HOME/d2/brain-browser/BrAIn"
rm -f "$HOME/d2/brain-browser/notes.txt" "$HOME/d2/brain-browser/release.txt"
m=$(mark)
launch "$HOME/d2/brain-browser/BrAIn"
check "a folder that is not a release (no release.txt, like a dotnet publish): updates are off" wait_log "update: disabled 9.9.13" 60 "$m"
stop_app "$HOME/d2/brain-browser/BrAIn"

finish
