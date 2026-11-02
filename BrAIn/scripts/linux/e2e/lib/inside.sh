#!/bin/bash
# Runs ONE end-to-end test inside the test container (called by ../run.sh):
# a headless X session (Xvfb + openbox), the app mounted at /app, this folder at /e2e.
#   inside.sh tests/NN_name.py
# A test can ask for extras with header comments:  "# needs: notifd"  (session bus + mock notification service)
# and run.sh passes docker flags from a "# docker: -e GDK_SCALE=2 -e RES=3200x2000x24" header.
TEST=$1

# "# as: keyring-user" — the test needs a real desktop account: a user with a known password (so PAM can check it) and an
# unlocked keyring on a session bus. Creating a user needs root, so run.sh starts these containers as root (`# docker: --user root`)
# and the test itself runs as that user.
if grep -q '^# as: keyring-user' "/e2e/$TEST"; then
  useradd -m -s /bin/bash t2 && echo 't2:secret' | chpasswd
  Xvfb :99 -screen 0 "${RES:-1600x1000x24}" -ac -nolisten tcp >/tmp/xvfb.log 2>&1 &
  sleep 1.3
  openbox >/tmp/openbox.log 2>&1 &
  sleep 0.7
  mkdir -p /shots; chmod 777 /shots
  runuser -u t2 -- bash -c '
    export HOME=/home/t2 DISPLAY=:99 LC_ALL=C.UTF-8 PYTHONPATH=/e2e/lib
    eval "$(dbus-launch --sh-syntax)"
    eval "$(echo -n kpass | gnome-keyring-daemon --unlock --components=secrets 2>&1 | grep =)"
    sleep 1
    mkdir -p ~/.local/share/BrAIn && echo "{\"welcomed\":true}" > ~/.local/share/BrAIn/prefs.json
    cd /e2e && python3 "/e2e/$0"
  ' "$TEST"
  rc=$?
  if [ $rc -ne 0 ]; then echo "--- app stderr (tail)"; tail -15 /tmp/brain.err 2>/dev/null; fi
  exit $rc
fi

Xvfb :99 -screen 0 "${RES:-1600x1000x24}" -nolisten tcp >/tmp/xvfb.log 2>&1 &
sleep 1.3
openbox >/tmp/openbox.log 2>&1 &
sleep 0.7
if grep -q '^# needs: notifd' "/e2e/$TEST"; then
  eval "$(dbus-launch --sh-syntax)"
  export DBUS_SESSION_BUS_ADDRESS
  rm -f /tmp/notif.log
  python3 /e2e/lib/notifd.py >/tmp/notifd.out 2>&1 &
  sleep 1.2
fi
mkdir -p /shots ~/.local/share/BrAIn
# without this the welcome film would cover the whole UI
echo '{"welcomed":true}' > ~/.local/share/BrAIn/prefs.json
export PYTHONPATH=/e2e/lib
cd /e2e
python3 "/e2e/$TEST"
rc=$?
if [ $rc -ne 0 ]; then echo "--- app stderr (tail)"; tail -15 /tmp/brain.err 2>/dev/null; fi
exit $rc
