#!/bin/bash
# Runs in the test rig as root: sets the stage for the self-update test, then hands over to update-user.sh as the
# normal user. Root is needed to put a certificate in the system trust store, to listen on port 443, and to make
# the root-owned install folders some of the scenarios use.
#
#   /pkg-old   the archive that gets installed (the "running" version)
#   /pkg-new   the archive the fake GitHub offers as the new release (a build with a higher version number)
#
# The app is run unmodified and talks to its real feed address, https://api.github.com/..., which the container maps
# (docker run --add-host) to a local server that holds a certificate the container's trust store accepts.
. /rig/lib.sh

set -- /pkg-old/BrAIn-*-linux-*.tar.gz; OLD=$1
set -- /pkg-new/BrAIn-*-linux-*.tar.gz; NEW=$1
FAKE=/srv/fake-github

echo "setting the stage"
[ "$(getent hosts api.github.com | awk '{print $1}')" = 127.0.0.1 ] || { echo "api.github.com does not point at this container (docker run --add-host api.github.com:127.0.0.1 --add-host github.com:127.0.0.1)"; exit 2; }
mkdir -p "$FAKE/files"; chmod 777 "$FAKE" "$FAKE/files"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$FAKE/key.pem" -out "$FAKE/cert.pem" -days 2 \
  -subj "/CN=api.github.com" -addext "subjectAltName=DNS:api.github.com,DNS:github.com" 2>/dev/null || { echo "openssl failed"; exit 2; }
cp "$FAKE/cert.pem" /usr/local/share/ca-certificates/fake-github.crt
update-ca-certificates >/dev/null 2>&1
python3 /rig/fake-github.py "$FAKE" "$FAKE/cert.pem" "$FAKE/key.pem" >"$FAKE/server.log" 2>&1 &
python3 /rig/set-release.py "$FAKE" v0.0.1 "$NEW" >/dev/null
chmod -R a+rwX "$FAKE"            # the test (as the user) replaces latest.json and the archive as it goes
for _ in $(seq 50); do curl -sf https://api.github.com/repos/GranTurismo/BrAIn/releases/latest >/dev/null 2>&1 && break; sleep 0.2; done
curl -sf https://api.github.com/repos/GranTurismo/BrAIn/releases/latest >/dev/null || { echo "the fake GitHub does not answer"; cat "$FAKE/server.log"; exit 2; }

# folders that belong to somebody else, for the scenarios that must not (or cannot) be updated in place
rm -rf /tmp/stage && mkdir -p /tmp/stage && tar xzf "$OLD" -C /tmp/stage
FOLDER=$(ls -d /tmp/stage/BrAIn-*)
mkdir -p /srv/shared && chmod 1777 /srv/shared                     # like /tmp: anybody may add entries, only the owner may rename or delete one
cp -a "$FOLDER" /srv/shared/brain-browser                          # owned by root
( cd "$FOLDER" && ./install.sh --system >/dev/null 2>&1 )          # /opt/brain-browser, root-owned, /opt is not writable by the user
chmod 755 /home/tester

export OLD NEW FAKE
exec runuser -u tester -- env HOME=/home/tester DISPLAY="${DISPLAY:-:99}" OLD="$OLD" NEW="$NEW" FAKE="$FAKE" bash /rig/update-user.sh
