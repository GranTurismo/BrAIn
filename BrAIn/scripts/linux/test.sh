#!/bin/bash
# BrAIn's Linux checks, run in the Docker test rig (README.md next to this file says what each one does).
#
#   scripts/linux/test.sh [selftest|package|update|all]
#
#   selftest   (default) publish, open the app on a virtual desktop and run `BrAIn --selftest-tabs`
#   package    build the release archive, install it, check the files and the menu entry, uninstall it
#   update     the update helper script on its own, then the real thing: install the archive and let the running app
#              update itself from a fake GitHub release (and refuse to, in the cases it must)
#   all        all three
#
# Environment:  BRAIN_TEST_IMAGE  the Docker image to use (default brain-linux-test; built from ./Dockerfile when missing)
#               BRAIN_TEST_ARCH   x64 or arm64: test that build instead of the engine's own (it then runs under emulation,
#                                 slowly, and needs an image of that architecture: use another BRAIN_TEST_IMAGE for it)
#               KEEP=1            keep the work folder (published app, archives, screenshots, logs); a failed run keeps it anyway
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
PROJ="$(cd "$HERE/../.." && pwd)"            # the project folder (BrAIn.csproj)
IMAGE="${BRAIN_TEST_IMAGE:-brain-linux-test}"
RIG=3                                        # bump together with `LABEL brain.rig` in the Dockerfile when the image's contents change
NEWVER=9.9.9                                 # the version the fake GitHub offers in the update test

WHAT="${1:-selftest}"
case "$WHAT" in
  selftest|package|update|all) ;;
  -h|--help) sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
  *) echo "usage: $0 [selftest|package|update|all]" >&2; exit 2 ;;
esac

for p in /usr/local/share/dotnet /opt/homebrew/bin "$HOME/.dotnet"; do
  [ -x "$p/dotnet" ] && export PATH="$p:$PATH"
done
command -v dotnet >/dev/null || { echo "dotnet SDK not found" >&2; exit 1; }
command -v docker >/dev/null || { echo "docker not found" >&2; exit 1; }
docker info >/dev/null 2>&1 || { echo "Docker is not running (OrbStack: 'orb start')" >&2; exit 1; }

# the container's architecture decides which build is run (an x64 build needs an x64 container, and vice versa)
ARCH="${BRAIN_TEST_ARCH:-}"
if [ -z "$ARCH" ]; then
  case "$(docker version --format '{{.Server.Arch}}')" in
    arm64|aarch64) ARCH=arm64 ;;
    amd64|x86_64) ARCH=x64 ;;
    *) echo "unsupported Docker architecture" >&2; exit 1 ;;
  esac
fi
case "$ARCH" in x64) PLATFORM=linux/amd64 ;; arm64) PLATFORM=linux/arm64 ;; *) echo "BRAIN_TEST_ARCH must be x64 or arm64" >&2; exit 2 ;; esac

WORK="$(mktemp -d "${TMPDIR:-/tmp}/brain-linux-test.XXXXXX")"
mkdir -p "$WORK/out"; chmod 777 "$WORK/out"   # the container's user writes reports and screenshots here
FAILED=0
cleanup() {
  if [ "$FAILED" = 0 ] && [ -z "${KEEP:-}" ]; then rm -rf "$WORK"; else echo "work folder kept: $WORK  (screenshots and logs in $WORK/out)"; fi
}
trap cleanup EXIT

have_image() {
  [ "$(docker image inspect --format '{{ index .Config.Labels "brain.rig" }}' "$IMAGE" 2>/dev/null || true)" = "$RIG" ]
}
if ! have_image; then
  echo "== building the $IMAGE image (a few minutes; only when it is missing or out of date)"
  docker build --platform "$PLATFORM" -t "$IMAGE" -f "$HERE/Dockerfile" "$HERE"
fi

publish() {   # <out dir>: the build the self-test runs
  echo "== publish linux-$ARCH"
  if ! dotnet publish "$PROJ/BrAIn.csproj" -c Release -r "linux-$ARCH" --self-contained true -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=false -o "$1" >"$WORK/publish.log" 2>&1; then
    grep -E " error " "$WORK/publish.log" | sort -u | head -20; echo "publish failed (log: $WORK/publish.log)"; FAILED=1; exit 1
  fi
}
archive() {   # <dist dir> [version]: the release archive for this architecture, made by the real dist script
  echo "== release archive ${2:-(version of BrAIn.csproj)}"
  if ! DIST="$1" bash "$PROJ/scripts/make-linux-dist.sh" "${2:-}" "$ARCH" >"$WORK/dist-$(basename "$1").log" 2>&1; then
    tail -20 "$WORK/dist-$(basename "$1").log"; echo "archive build failed"; FAILED=1; exit 1
  fi
}
rig() {       # <user> <script> [args]: run an inside-*.sh script in a fresh container (extra docker flags in $DOCKER_FLAGS)
  local user=$1; shift
  # shellcheck disable=SC2086
  docker run --rm --init --platform "$PLATFORM" --user "$user" ${DOCKER_FLAGS:-} -v "$HERE":/rig:ro -v "$WORK/out":/out "${MOUNTS[@]}" "$IMAGE" bash "/rig/$1" "${@:2}"
}
MOUNTS=()

run_selftest() {
  publish "$WORK/app"
  echo "== tab self-test in the container"
  MOUNTS=(-v "$WORK/app":/app:ro)
  rig tester inside-selftest.sh || FAILED=1
}
run_package() {
  [ -d "$WORK/dist" ] || archive "$WORK/dist"
  MOUNTS=(-v "$WORK/dist":/pkg:ro)
  echo "== install / uninstall, as a user"
  rig tester inside-package.sh user || FAILED=1
  echo "== install / uninstall, system-wide (root)"
  rig root inside-package.sh system || FAILED=1
}
run_update() {
  echo "== the update helper script on its own (taken from Browser/Updater.cs)"
  MOUNTS=(-v "$PROJ/Browser/Updater.cs":/Updater.cs:ro)
  rig tester inside-helper.sh || FAILED=1
  [ -d "$WORK/dist" ] || archive "$WORK/dist"
  archive "$WORK/dist-new" "$NEWVER"
  echo "== self-update against a fake GitHub release ($NEWVER)"
  MOUNTS=(-v "$WORK/dist":/pkg-old:ro -v "$WORK/dist-new":/pkg-new:ro)
  DOCKER_FLAGS="--add-host api.github.com:127.0.0.1 --add-host github.com:127.0.0.1" rig root inside-update.sh || FAILED=1
}

case "$WHAT" in
  selftest) run_selftest ;;
  package) run_package ;;
  update) run_update ;;
  all) run_selftest; run_package; run_update ;;
esac

echo
if [ "$FAILED" = 0 ]; then echo "ALL PASSED"; else echo "SOMETHING FAILED"; exit 1; fi
