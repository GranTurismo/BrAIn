#!/bin/bash
# Copies the newest installers from dist/ into ../website/downloads and writes the manifest the
# download section reads (downloads.json: version, file, size, sha256) plus SHA256SUMS.txt.
# Build the installers first:  bash scripts/make-dist.sh <version>
set -euo pipefail
cd "$(dirname "$0")/.."
SITE="../website/downloads"
mkdir -p "$SITE"

newest() { ls -t dist/$1 2>/dev/null | head -1; }
EXE="$(newest 'BrAIn-Setup-*-x64.exe')"
DMG="$(newest 'BrAIn-*-macOS-arm64.dmg')"
[ -n "$EXE" ] && [ -n "$DMG" ] || { echo "no installers in dist/ — run scripts/make-dist.sh first"; exit 1; }

VER="$(basename "$EXE" | sed -E 's/BrAIn-Setup-(.*)-x64\.exe/\1/')"
rm -f "$SITE"/BrAIn-*  # only the current release ships
cp "$EXE" "$DMG" "$SITE/"

( cd "$SITE" && shasum -a 256 BrAIn-* > SHA256SUMS.txt )

python3 - "$SITE" "$VER" <<'PY'
import hashlib, json, os, sys, datetime
site, ver = sys.argv[1], sys.argv[2]
def entry(name, os_, arch, label):
    p = os.path.join(site, name)
    h = hashlib.sha256(open(p, 'rb').read()).hexdigest()
    return {"os": os_, "arch": arch, "label": label, "file": name, "bytes": os.path.getsize(p), "sha256": h}
files = [f for f in os.listdir(site) if f.startswith("BrAIn-")]
exe = next(f for f in files if f.endswith(".exe")); dmg = next(f for f in files if f.endswith(".dmg"))
json.dump({
    "version": ver,
    "released": datetime.date.today().isoformat(),
    "downloads": [
        entry(dmg, "mac", "arm64", "macOS · Apple Silicon"),
        entry(exe, "win", "x64", "Windows 10/11 · x64"),
    ],
}, open(os.path.join(site, "downloads.json"), "w"), indent=2)
manifest = json.load(open(os.path.join(site, "downloads.json")))
# also as a script, so the page works when opened straight from disk (fetch() can't read file://)
open(os.path.join(site, "downloads.js"), "w").write("window.BRAIN_DOWNLOADS = " + json.dumps(manifest) + ";\n")
print("downloads.json + downloads.js written for", ver)
PY
ls -lh "$SITE"
