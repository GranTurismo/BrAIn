#!/usr/bin/env bash
# Adds the usage-description strings macOS needs before it will show the
# camera / microphone / location permission prompts, then re-signs the app.
# Usage: scripts/mac-permissions.sh [path/to/BrAIn.app]   (default: dist/BrAIn.app)
set -euo pipefail
APP="${1:-dist/BrAIn.app}"
PLIST="$APP/Contents/Info.plist"
[ -f "$PLIST" ] || { echo "Not found: $PLIST" >&2; exit 1; }
add() {
  /usr/libexec/PlistBuddy -c "Delete :$1" "$PLIST" 2>/dev/null || true
  /usr/libexec/PlistBuddy -c "Add :$1 string $2" "$PLIST"
}
add NSCameraUsageDescription "Websites you visit in BrAIn can ask to use your camera."
add NSMicrophoneUsageDescription "Websites you visit in BrAIn can ask to use your microphone."
add NSLocationWhenInUseUsageDescription "Websites you visit in BrAIn can ask for your location."
# Signing. Ad-hoc is the default (no passkeys); set BRAIN_SIGN_IDENTITY to a real identity
# (e.g. "Developer ID Application: Name (TEAMID)" or "Apple Development: Name (ID)") for a build
# that can show the system passkey sheet on any site. That also needs:
#   BRAIN_PROVISIONING_PROFILE  a macOS .provisionprofile for App ID ai.brain.browser that carries
#                               com.apple.developer.web-browser.public-key-credential (Apple grants it on request)
# BrAIn.entitlements holds restricted entitlements: launchd rejects them under ad-hoc signing or
# without the matching profile, so they are applied only on this path.
if [ -n "${BRAIN_SIGN_IDENTITY:-}" ]; then
  [ -f "${BRAIN_PROVISIONING_PROFILE:-}" ] || { echo "Set BRAIN_PROVISIONING_PROFILE to the macOS provisioning profile (see comments above)." >&2; exit 1; }
  ENT="$(cd "$(dirname "$0")/.." && pwd)/BrAIn.entitlements"
  cp "$BRAIN_PROVISIONING_PROFILE" "$APP/Contents/embedded.provisionprofile"
  OPTS=""
  case "$BRAIN_SIGN_IDENTITY" in "Developer ID"*) OPTS="--options runtime --timestamp";; esac
  # sign nested code first, then the bundle (--deep is deprecated for this)
  find "$APP/Contents" -type f \( -name '*.dylib' -o -perm -111 \) ! -path "$APP/Contents/MacOS/BrAIn" -print0 2>/dev/null |
    while IFS= read -r -d '' f; do codesign --force --sign "$BRAIN_SIGN_IDENTITY" $OPTS "$f" 2>/dev/null || true; done
  codesign --force --sign "$BRAIN_SIGN_IDENTITY" $OPTS --entitlements "$ENT" "$APP"
  codesign --verify --strict "$APP" && echo "Done (signed: $BRAIN_SIGN_IDENTITY, passkey entitlement on)."
else
  codesign --force --deep --sign - "$APP"
  echo "Done (ad-hoc). Launch $APP — macOS will now prompt when a site asks for camera, mic or location."
fi
