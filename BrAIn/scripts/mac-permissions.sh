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
# Signing. Ad-hoc is the default; set BRAIN_SIGN_IDENTITY to a real identity
# (e.g. "Developer ID Application: Name (TEAMID)") for a distributable build.
# BrAIn.entitlements carries com.apple.web-browser.public-key-credential, which
# passkeys/WebAuthn need — but launchd rejects it under ad-hoc signing, so it
# is only applied when a real identity (and its provisioning) is used.
if [ -n "${BRAIN_SIGN_IDENTITY:-}" ]; then
  ENT="$(cd "$(dirname "$0")/.." && pwd)/BrAIn.entitlements"
  codesign --force --deep --sign "$BRAIN_SIGN_IDENTITY" --entitlements "$ENT" "$APP"
  echo "Done (signed: $BRAIN_SIGN_IDENTITY, passkey entitlement on)."
else
  codesign --force --deep --sign - "$APP"
  echo "Done (ad-hoc). Launch $APP — macOS will now prompt when a site asks for camera, mic or location."
fi
