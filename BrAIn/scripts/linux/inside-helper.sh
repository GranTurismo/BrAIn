#!/bin/bash
# Runs in the test rig: the self-update's detached helper script (Updater.LinuxHelper, taken out of Browser/Updater.cs,
# mounted at /Updater.cs) on its own, with stand-in "programs", including the failures the real app cannot be made to
# produce on demand: a rename that fails half way (a fake `mv` in front of the real one), a process that is only a
# zombie, a stale .old.
. /rig/lib.sh

T=/tmp/helper
HELPER=$T/brain-update.sh
mkdir -p "$T"
awk '/internal const string LinuxHelper = """/ {f = 1; next} f && /^ *""";/ {exit} f {sub(/^        /, ""); print}' /Updater.cs > "$T/helper.src"
[ -s "$T/helper.src" ] || { echo "could not find LinuxHelper in Browser/Updater.cs"; exit 2; }
sh -n "$T/helper.src" || { echo "the helper does not parse"; exit 2; }

# a fresh scene: ROOT is the installed version, NEW the unpacked update; each "program" records that it was started
scene() {
  rm -rf "$T/ROOT" "$T/ROOT.old" "$T/NEW" "$T/launched" "$T/update.log" "$T/fakebin"; mkdir -p "$T/ROOT" "$T/NEW"
  for v in ROOT:OLD NEW:NEW; do
    d=${v%%:*}; n=${v##*:}
    printf '#!/bin/sh\necho %s >> %s/launched\n' "$n" "$T" > "$T/$d/BrAIn"; chmod +x "$T/$d/BrAIn"
    echo "${n,,}" > "$T/$d/data"
  done
  echo archive > "$T/archive.tgz"
  cp "$T/helper.src" "$HELPER"
}
# a mv that fails when asked to move $FAIL_MV_FROM (and does the real thing otherwise)
fake_mv() { mkdir -p "$T/fakebin"; printf '#!/bin/sh\n[ "$1" = "%s" ] && { echo "mv: cannot move $1: Operation not permitted" >&2; exit 1; }\nexec /bin/mv "$@"\n' "$1" > "$T/fakebin/mv"; chmod +x "$T/fakebin/mv"; }
gone_pid() { sh -c 'exit 0' & local p=$!; wait "$p"; echo "$p"; }   # the pid of a process that no longer exists
# run the helper to the end (BRAIN_UPDATE_DETACHED=1: no setsid re-exec, so it is synchronous)
helper() { BRAIN_UPDATE_DETACHED=1 PATH="$T/fakebin:$PATH" sh "$HELPER" "$1" "$T/ROOT" "$T/NEW" BrAIn "$T/archive.tgz" "$T/update.log"; }
launched() { local i; for i in $(seq 40); do [ -s "$T/launched" ] && break; sleep 0.1; done; cat "$T/launched" 2>/dev/null | tr '\n' ' ' | sed 's/ $//'; }

section "the swap"
scene; helper "$(gone_pid)"
expect_eq "the installed folder now holds the new version" new "$(cat "$T/ROOT/data")"
expect_eq "the new version was started" NEW "$(launched)"
check "no .old, no .new left" bash -c "[ ! -e '$T/ROOT.old' ] && [ ! -e '$T/NEW' ]"
check "the downloaded archive was deleted" test ! -e "$T/archive.tgz"
check "the helper deleted itself" test ! -e "$HELPER"
check "the log says so" grep -q "swapped in the new version" "$T/update.log"

section "a stale .old from an earlier attempt"
scene; mkdir "$T/ROOT.old"; echo stale > "$T/ROOT.old/junk"; helper "$(gone_pid)"
expect_eq "the update still goes through" new "$(cat "$T/ROOT/data")"
check "and the stale folder is gone" test ! -e "$T/ROOT.old"

section "the new folder is missing"
scene; rm -rf "$T/NEW"; helper "$(gone_pid)"
expect_eq "the installed version is kept" old "$(cat "$T/ROOT/data")"
expect_eq "and started again" OLD "$(launched)"
check "the log says why" grep -q "the new version is missing" "$T/update.log"

section "the installed folder cannot be moved aside (first rename fails)"
scene; fake_mv "$T/ROOT"; helper "$(gone_pid)"
expect_eq "the installed version is untouched" old "$(cat "$T/ROOT/data")"
expect_eq "and started again" OLD "$(launched)"
check "the staged copy is removed" test ! -e "$T/NEW"
check "no .old" test ! -e "$T/ROOT.old"
check "the log says why" grep -q "could not move the installed version aside" "$T/update.log"

section "the new folder cannot be moved into place (second rename fails): rollback"
scene; fake_mv "$T/NEW"; helper "$(gone_pid)"
expect_eq "the previous version is back" old "$(cat "$T/ROOT/data")"
expect_eq "and started again" OLD "$(launched)"
check "the staged copy is removed" test ! -e "$T/NEW"
check "no .old left behind" test ! -e "$T/ROOT.old"
check "the log tells the story" bash -c "grep -q 'rolling back' '$T/update.log' && grep -q 'rolled back' '$T/update.log'"

section "it waits for the app to exit"
scene; sleep 4 & P=$!
( helper "$P" ) &
H=$!
sleep 2
expect_eq "nothing is touched while the app is running" old "$(cat "$T/ROOT/data")"
wait "$H"
expect_eq "it swaps once the app is gone" new "$(cat "$T/ROOT/data")"

section "a zombie (exited, never reaped by its parent) does not count as running"
scene
bash -c 'sh -c "echo \$\$ > '"$T"'/zpid; exit 0" & exec sleep 60' &
SLEEPER=$!
for _ in $(seq 30); do [ -s "$T/zpid" ] && break; sleep 0.1; done
Z=$(cat "$T/zpid"); sleep 0.5
check "(the stand-in really is a zombie)" bash -c "[ \"\$(awk '{print \$3}' /proc/$Z/stat)\" = Z ]"
start=$SECONDS; helper "$Z"
expect_eq "the update does not wait for it" new "$(cat "$T/ROOT/data")"
check "...and takes seconds, not minutes" test $((SECONDS - start)) -lt 10
kill "$SLEEPER" 2>/dev/null

section "started the way the app starts it (own session, no BRAIN_UPDATE_DETACHED)"
scene
( cd /; PATH="$T/fakebin:$PATH" sh "$HELPER" "$(gone_pid)" "$T/ROOT" "$T/NEW" BrAIn "$T/archive.tgz" "$T/update.log" </dev/null >/dev/null 2>&1 & )
for _ in $(seq 50); do grep -q '^.*done$' "$T/update.log" 2>/dev/null && break; sleep 0.1; done
expect_eq "the update goes through" new "$(cat "$T/ROOT/data")"
expect_eq "the new version was started" NEW "$(launched)"

finish
