"""DevTools network recorder (WebKit): fetch / XHR / WebSocket with headers, bodies and timing; the MCP tools that read it; HAR export; and the
recorder following the DevTools panel (F12) on and off.

The recorder is a document-start script that exists only while DevTools is open (or an agent asked for it), so this reloads the page after
switching it on, exactly what a user does ("open DevTools, reload")."""
import glob, json, os
from mcp import *
import netsite

BASE = netsite.start()
c = MCP(['--no-fullscreen'])
time.sleep(4)
c.call('navigate', url=BASE + '/')
wait_until(lambda: c.js('document.title') == 'netsite done', 8)

r1 = c.call('network_requests')
check('the first call lists what the page already loaded (timeline backfill)', '/app.js' in r1 or '/x.png' in r1, r1[:120])
check('...and says recording just started', 'Recording just started' in r1)
c.call('navigate', url=BASE + '/')            # reload with the recorder installed at document start
wait_until(lambda: c.js('document.title') == 'netsite done', 8)
time.sleep(1.0)
r2 = c.call('network_requests')
post = [l for l in r2.splitlines() if ' POST ' in l]
ws = [l for l in r2.splitlines() if ' websocket ' in l]
check('the POST fetch is listed with status 201', bool(post) and ' 201 ' in post[0], post[:1])
check('the websocket is listed', bool(ws), ws[:1])
check('scripts and images are listed too', '/app.js' in r2 and '/x.png' in r2)

d = c.call('network_request', id=post[0].split()[0]) if post else ''
check('request detail: payload', '"a":1' in d and 'Request body' in d, d[:160])
check('request detail: custom request header', 'x-custom: abc' in d)
check('request detail: response body', '"got"' in d and 'Response body' in d)
check('request detail: timing', 'Started:' in d and 'total' in d)
wd = c.call('network_request', id=ws[0].split()[0]) if ws else ''
check('websocket frames both ways', '↑ sent' in wd and '↓ received' in wd and 'echo:hello' in wd, wd[-200:])
failed = c.call('network_requests', failedOnly=True)
check('failedOnly: the 404 and the network error', ' 404 ' in failed and 'ERR' in failed, failed)
xhr = c.call('network_requests', type='xhr')
check('type filter: the XHR, with its header', 'via=xhr' in xhr, xhr)

har = c.call('network_har', inline=True, maxChars=300)
m = re.search(r'to (/.*?\.har)', har)
check('HAR saved to Downloads', bool(m) and os.path.exists(m.group(1)), har[:100])
if m:
    j = json.load(open(m.group(1))); es = j['log']['entries']
    check('HAR: valid, with bodies and timings', len(es) >= 6 and any(e['response']['content'].get('text') for e in es) and all('timings' in e for e in es), len(es))

# ---- the panel (F12) switches the recorder on and off too
c.call('network_record', action='stop')
check('agent recording stopped', 'off' in c.call('network_record', action='status'))
c.call('navigate', url=BASE + '/'); time.sleep(1.5)
sh('xdotool key F12'); time.sleep(2.5)
check('the DevTools panel turned the recorder on', 'on' in c.call('network_record', action='status'), c.call('network_record', action='status'))
check('...and the page has it', c.js('String(!!window.__brainNet)') == 'true')
sh('xdotool key F12'); time.sleep(1.5)
check('closing the panel turned it off again', 'off' in c.call('network_record', action='status'))
c.close()
finish()
