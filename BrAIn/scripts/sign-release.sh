#!/bin/bash
# Signs release files so the in-app updater will install them.   sign-release.sh <version> <file>...
#
# For every <file> this writes <file>.sig next to it (base64 of an ECDSA P-256 / SHA-256 signature, DER form). Upload each .sig
# to the GitHub release together with its file. What is signed is exactly what Browser/UpdateTrust.cs checks:
#     BrAIn-update-v1 \n <version> \n <file name> \n <sha256 of the file> \n
# so a signature is good for one version, one name, one content — not for "anything on that release page".
#
# The private key is NOT in this repository and must never be: ~/.brain-release/update-signing.pem (override: BRAIN_UPDATE_KEY).
# Only its public half is in the app (UpdateTrust.Keys). Lose the key and installed copies can no longer be updated in place
# (they would need a manual reinstall); leak it and anyone can ship an "update" — keep a backup, keep it private.
set -euo pipefail

KEY="${BRAIN_UPDATE_KEY:-$HOME/.brain-release/update-signing.pem}"
[ $# -ge 2 ] || { echo "usage: $0 <version> <file>..." >&2; exit 2; }
VERSION="${1#v}"; shift
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "version must look like 1.8.4 (got '$VERSION')" >&2; exit 2; }
[ -r "$KEY" ] || { echo "signing key not found: $KEY" >&2; exit 1; }

sha256() { if command -v shasum >/dev/null 2>&1; then shasum -a 256 "$1" | cut -d' ' -f1; else sha256sum "$1" | cut -d' ' -f1; fi; }
PUB="$(mktemp)"; MSG="$(mktemp)"; SIG="$(mktemp)"; trap 'rm -f "$PUB" "$MSG" "$SIG"' EXIT
openssl pkey -in "$KEY" -pubout -out "$PUB"

for f in "$@"; do
  [ -f "$f" ] || { echo "no such file: $f" >&2; exit 1; }
  name="$(basename "$f")"
  [[ "$name" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]{0,119}$ ]] || { echo "unsafe release file name: $name" >&2; exit 1; }
  printf 'BrAIn-update-v1\n%s\n%s\n%s\n' "$VERSION" "$name" "$(sha256 "$f")" > "$MSG"
  openssl dgst -sha256 -sign "$KEY" -out "$SIG" "$MSG"
  openssl dgst -sha256 -verify "$PUB" -signature "$SIG" "$MSG" >/dev/null   # never ship a signature we cannot check ourselves
  base64 < "$SIG" | tr -d '\n' > "$f.sig"
  echo "signed $name -> $name.sig"
done
