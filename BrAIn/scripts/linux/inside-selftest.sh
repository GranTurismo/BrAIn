#!/bin/bash
# Runs in the test rig: a virtual desktop, then `BrAIn --selftest-tabs` from /app (the published build).
# The report goes to stdout and /out. Exit code = the app's (the number of failed checks).
. /rig/lib.sh
start_desktop
skip_welcome
cd /app || exit 2
timeout 300 ./BrAIn --selftest-tabs --selftest-quiet "$@" </dev/null >/out/selftest.out 2>/out/selftest.err
rc=$?
echo "exit code: $rc"
echo "--- self-test report"
cat ~/.local/share/BrAIn/selftest-tabs.txt 2>/dev/null | tee /out/selftest-tabs.txt
if [ "$rc" -ne 0 ]; then echo "--- stderr (tail)"; grep -v "update:" /out/selftest.err | tail -15; fi
import -window root /out/selftest-end.png 2>/dev/null || true
exit "$rc"
