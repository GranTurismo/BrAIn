# docker: --user root
# as: keyring-user
"""A real desktop account: the browser importer, the password vault key in the Secret Service, and the account-password
(PAM) check in front of revealing a password.

Fake Chrome / Chromium / Opera / Snap / Flatpak / Firefox profiles (lib/mkfix.py) are imported through about:import:
v10 cookies (key "peanuts"), v11 cookies and passwords (key from the keyring item "Chrome Safe Storage"), a value that
cannot be decrypted (skipped, reported). The imported cookies must reach a real HTTP request, the vault key must live in
the keyring and not in a file, and revealing a saved password must need the right account password.
"""
from mcp import *

start_site()
home = os.environ['HOME']
print(sh('python3 /e2e/lib/mkfix.py'))


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
c.call('navigate', url='about:import')
wait_js(c, "String(document.querySelectorAll('.card').length)")
names = c.js("[...document.querySelectorAll('.bname')].map(e=>e.textContent).join(' | ')")
desk('18-import-scan')
check('Chrome is found', 'Google Chrome' in names, names)
check('Snap Chromium, Opera and Firefox are found too', all(n in names for n in ('Chromium (Snap)', 'Opera', 'Firefox')), names)

c.js("document.getElementById('go').click()")
res = wait_js(c, "(document.getElementById('res-chromium:chrome:Default')||{}).textContent||''", 'Imported', 60)
check('cookies and passwords are counted', 'cookies' in res and '2 passwords' in res, res)
check('a value that cannot be decrypted is reported as skipped', 'could not be decrypted' in c.js("document.getElementById('list').innerText"))
desk('18-import-done')

# the imported cookies reach a real request (v10, v11, plaintext; the v11 one is HttpOnly)
c.call('navigate', url=SITE + '/echo-cookies')
time.sleep(1.5)
hdr = c.js("document.getElementById('c').textContent")
check('v10 cookie sent', 'imp10=from-v10' in hdr, hdr)
check('v11 cookie sent (key from the keyring)', 'imp11=from-v11' in hdr, hdr)
check('plaintext cookie sent', 'impplain=from-plain' in hdr, hdr)

# the vault key is in the Secret Service, not on disk
look = sh('secret-tool lookup application BrAIn service "BrAIn Passwords" account store-key')
check('the vault key is stored in the keyring (44 chars of base64)', len(look) == 44, look[:6])
check('...and there is no key file', not os.path.exists(f'{home}/.local/share/BrAIn/pwkey.bin'))
check('the vault file exists', os.path.exists(f'{home}/.local/share/BrAIn/passwords.dat'))

# about:passwords: revealing needs the account password
c.call('navigate', url='about:passwords')
wait_js(c, "String(document.querySelectorAll('.row').length)", '2', 20)
listing = c.js("[...document.querySelectorAll('.row')].map(r=>r.querySelector('.tt').textContent+' '+r.querySelector('.uu').textContent).join(' | ')")
check('both imported logins are listed', 'example.com alice' in listing and 'example.org bob' in listing, listing)
idx = c.js("[...document.querySelectorAll('.row')].findIndex(r=>r.querySelector('.uu').textContent==='bob')")
c.js(f"document.querySelectorAll('.row')[{idx}].querySelector('.eye').click()")
wid = wait_until(lambda: sh("xdotool search --name 'BrAIn Passwords' | head -1"), 10)
check('the account-password dialog appears', bool(wid))
time.sleep(0.8)
desk('18-pam-dialog')
sh(f'xdotool windowfocus {wid}; sleep 0.3; xdotool type --delay 40 -- nope; xdotool key Return')
time.sleep(5)       # a failed PAM attempt is slow on purpose
shown = c.js(f"document.querySelectorAll('.row')[{idx}].querySelector('.pw').textContent")
check('a wrong password reveals nothing', 'ünï' not in shown, shown)
sh(f'xdotool windowfocus {wid}; sleep 0.3; xdotool type --delay 40 -- secret; xdotool key Return')
pw = wait_js(c, f"document.querySelectorAll('.row')[{idx}].querySelector('.pw').textContent", 'ünï', 15)
check('the right account password reveals the v11-encrypted password', pw == 'p@ss-v11 ünï', pw)
desk('18-revealed')
c.close()

noise = sh("grep -ciE 'CRITICAL|Gtk-WARNING|GLib-GObject-WARNING|libsecret-' /tmp/brain.err")
check('no GTK / GLib / libsecret warnings on stderr', noise == '0', sh("grep -iE 'CRITICAL|Gtk-WARNING|GLib-GObject-WARNING|libsecret-' /tmp/brain.err | head -3 | cut -c1-160"))
finish()
