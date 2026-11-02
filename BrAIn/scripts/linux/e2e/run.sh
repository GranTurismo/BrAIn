#!/bin/bash
# Linux end-to-end tests. BrAIn runs inside a container with a headless X session (Xvfb + openbox); the tests drive it
# the way a user and an MCP client would (the MCP protocol, real X key/mouse events) and assert on what the app does and
# on what is really on screen. Nothing touches the host: no display, no home folder, no clipboard.
#
#   scripts/linux/e2e/run.sh [options] [test ...]
#
#   test             a test's number or part of its name, e.g.  02   palette   (default: all)
#   --app DIR        use an already published app folder (the output of `dotnet publish -r linux-<arch> ...`)
#                    instead of publishing one; must match --arch
#   --arch ARCH      arm64 | x64   (default: this machine's; the other one needs amd64/arm64 emulation in Docker)
#   --shots DIR      keep the screenshots the tests take (default: a temporary folder that is removed)
#   --list           list the tests and exit
#   -v               print the full output of every test, not only of failing ones
#
# Needs: docker, and (unless --app is given) the .NET SDK. The first run builds the test image (a few minutes).
# Exit status: the number of failed tests.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$(cd "$HERE/../../.." && pwd)"      # the folder with BrAIn.csproj

APP=""; ARCH=""; SHOTS=""; VERBOSE=0; LIST=0; PATTERNS=""
while [ $# -gt 0 ]; do
  case "$1" in
    --app) APP="$2"; shift 2 ;;
    --arch) ARCH="$2"; shift 2 ;;
    --shots) SHOTS="$2"; shift 2 ;;
    --list) LIST=1; shift ;;
    -v) VERBOSE=1; shift ;;
    -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
    *) PATTERNS="$PATTERNS $1"; shift ;;
  esac
done

if [ "$LIST" = 1 ]; then
  for f in "$HERE"/tests/*.py; do
    printf '%s  %s\n' "$(basename "$f" .py)" "$(sed -n 's/^"""\(.*\)$/\1/p' "$f" | head -1)"
  done
  exit 0
fi

if [ -z "$ARCH" ]; then
  case "$(uname -m)" in arm64|aarch64) ARCH=arm64 ;; *) ARCH=x64 ;; esac
fi
if [ "$ARCH" = x64 ]; then PLATFORM="linux/amd64"; IMAGE="brain-linux-test-x64"; else PLATFORM="linux/arm64"; IMAGE="brain-linux-test"; fi

command -v docker >/dev/null || { echo "docker is required"; exit 2; }

WANT="$(sed -n 's/^LABEL brain.rig="\(.*\)"/\1/p' "$PROJECT/scripts/linux/Dockerfile")"
HAVE="$(docker image inspect --format '{{ index .Config.Labels "brain.rig" }}' "$IMAGE" 2>/dev/null || true)"
if [ "$HAVE" != "$WANT" ]; then
  echo "== building the test image $IMAGE ($PLATFORM) — this takes a few minutes the first time"
  docker build --platform "$PLATFORM" -t "$IMAGE" -f "$PROJECT/scripts/linux/Dockerfile" "$PROJECT/scripts/linux" || exit 2
fi

CLEAN=""
if [ -z "$APP" ]; then
  APP="$(mktemp -d)"; CLEAN="$APP"
  echo "== publishing linux-$ARCH"
  (cd "$PROJECT" && dotnet publish BrAIn.csproj -c Release -r "linux-$ARCH" --self-contained true \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -o "$APP" >"$APP.log" 2>&1) \
    || { grep -E " error " "$APP.log" | sort -u | head; echo "publish failed (log: $APP.log)"; exit 2; }
fi
if [ -z "$SHOTS" ]; then SHOTS="$(mktemp -d)"; CLEAN="$CLEAN $SHOTS"; fi
mkdir -p "$SHOTS"

FAILED=0; RAN=0; SUMMARY=""
for f in "$HERE"/tests/*.py; do
  name="$(basename "$f" .py)"
  if [ -n "$PATTERNS" ]; then
    hit=0; for p in $PATTERNS; do case "$name" in *"$p"*) hit=1 ;; esac; done
    [ "$hit" = 1 ] || continue
  fi
  flags="$(sed -n 's/^# docker: //p' "$f" | head -1)"
  out="$(mktemp)"
  # shellcheck disable=SC2086
  docker run --rm --platform "$PLATFORM" $flags -v "$APP":/app:ro -v "$HERE":/e2e:ro -v "$SHOTS":/shots \
      "$IMAGE" bash /e2e/lib/inside.sh "tests/$name.py" >"$out" 2>&1
  rc=$?
  RAN=$((RAN + 1))
  pass=$(grep -c '^PASS' "$out"); fail=$(grep -c '^FAIL' "$out")
  if [ $rc -eq 0 ]; then
    printf 'ok    %-34s %s checks\n' "$name" "$pass"
    [ "$VERBOSE" = 1 ] && cat "$out"
  else
    FAILED=$((FAILED + 1))
    printf 'FAIL  %-34s %s passed, %s failed (exit %s)\n' "$name" "$pass" "$fail" "$rc"
    cat "$out"
  fi
  rm -f "$out"
done

echo "== $RAN test(s), $FAILED failed"
for d in $CLEAN; do case "$d" in "$SHOTS") ;; *) rm -rf "$d" "$d.log" ;; esac; done
[ -z "${KEEP_SHOTS:-}" ] && [ "$SHOTS" != "" ] && case "$SHOTS" in /tmp/*|/var/folders/*|/private/*) rm -rf "$SHOTS" ;; esac
exit "$FAILED"
