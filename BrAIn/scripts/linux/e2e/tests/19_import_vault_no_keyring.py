"""A minimal session: no Secret Service and no usable account password. The vault key falls back to a 0600 file, revealing a
password falls back to a plain Allow / Cancel dialog, and the importer works through the MCP tool (v11 values, whose key
would live in a keyring, are skipped with the reason)."""
import stat

from mcp import *

start_site()
home = os.environ['HOME']
os.environ['FIX_KEYRING'] = '0'
print(sh('FIX_KEYRING=0 python3 /e2e/lib/mkfix.py'))
check('there is no session bus in this run', not os.environ.get('DBUS_SESSION_BUS_ADDRESS'))


def wait_js(c, js, want=None, secs=40):
    end = time.time() + secs
    while time.time() < end:
        v = c.js(js)
        if (want is None and v not in ('', 'null', 'false', '0')) or (want is not None and want in v):
            return v
        time.sleep(0.5)
    return c.js(js)


c = MCP(['--no-fullscreen'])
time.sleep(4)
scan = c.call('import', action='scan')
check('the import tool lists Chrome and Firefox', 'chromium:chrome:Default' in scan and 'firefox:' in scan, scan[:200])
r = c.call('import', action='run', source='chromium:chrome:Default')
check('importing Chrome brings bookmarks and history', 'imported 2 new bookmark(s) and 2 new history' in r, r)
r = c.call('import', action='run', source='chromium:opera:Default')
check('Opera (a flat profile layout) imports too', 'new history' in r and 'new bookmark' in r, r)

c.call('navigate', url='about:import')
wait_js(c, "String(document.querySelectorAll('.card').length)")
c.js("document.getElementById('go').click()")
res = wait_js(c, "(document.getElementById('res-chromium:chrome:Default')||{}).innerText||''", 'Imported', 60)
check('v10 passwords import without a keyring', '1 passwords' in res and '2 passwords' not in res, res)
check('v11 values are skipped, and the page says why', 'keyring' in res and 'skipped' in res.lower(), res)
desk('19-no-keyring')

kf = f'{home}/.local/share/BrAIn/pwkey.bin'
check('the vault key is a file', os.path.exists(kf))
if os.path.exists(kf):
    check('...with mode 0600 and 32 bytes', stat.S_IMODE(os.stat(kf).st_mode) == 0o600 and os.path.getsize(kf) == 32)

c.call('navigate', url='about:passwords')
wait_js(c, "String(document.querySelectorAll('.row').length)", '1', 20)
c.js("document.querySelector('.row .eye').click()")
wid = wait_until(lambda: sh("xdotool search --name 'BrAIn Passwords' | head -1"), 10)
check('a plain Allow / Cancel prompt appears (no account password to check)', bool(wid))
time.sleep(0.6)
desk('19-allow-cancel')
sh(f'xdotool windowfocus {wid}; sleep 0.3; xdotool key Escape')
time.sleep(1)
check('Cancel reveals nothing', 'p@ss' not in c.js("document.querySelector('.row .pw').textContent"))
c.js("document.querySelector('.row .eye').click()")
wid = wait_until(lambda: sh("xdotool search --name 'BrAIn Passwords' | head -1"), 10)
sh(f'xdotool windowfocus {wid}; sleep 0.3; xdotool key Tab; sleep 0.2; xdotool key Return')
pw = wait_js(c, "document.querySelector('.row .pw').textContent", 'p@ss', 15)
check('Allow reveals the v10 password', pw == 'p@ss-v10', pw)
c.close()
finish()
