# Helpers for the inside-*.sh scripts that run in the test rig. Sourced, never executed.

PASS=0; FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok    $*"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL  $*"; }
# check "what" command args...   ok when the command succeeds
check() { local what=$1; shift; if "$@" >/dev/null 2>&1; then ok "$what"; else bad "$what"; fi; }
# expect_eq "what" expected actual
expect_eq() { if [ "$2" = "$3" ]; then ok "$1"; else bad "$1 (expected '$2', got '$3')"; fi; }
section() { echo; echo "== $*"; }
# the last line of a script that counts: prints the totals, exits non-zero when anything failed
finish() { echo; echo "$PASS passed, $FAIL failed"; [ "$FAIL" -eq 0 ]; }

# A virtual desktop: Xvfb on $DISPLAY (:99) and the openbox window manager. Returns when X answers.
start_desktop() {
  Xvfb "${DISPLAY:-:99}" -screen 0 "${RES:-1600x1000x24}" -nolisten tcp >/tmp/xvfb.log 2>&1 &
  local i
  for i in $(seq 50); do xdpyinfo >/dev/null 2>&1 && break; sleep 0.2; done
  openbox >/tmp/openbox.log 2>&1 &
  sleep 0.7
}

# Skip the first-run welcome film, which would cover the UI the tests look at.
skip_welcome() { mkdir -p ~/.local/share/BrAIn; echo '{"welcomed":true}' > ~/.local/share/BrAIn/prefs.json; }
