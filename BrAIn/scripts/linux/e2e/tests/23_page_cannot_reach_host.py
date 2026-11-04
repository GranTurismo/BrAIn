"""A web page cannot use or watch BrAIn's host bridge.

BrAIn's own scripts (toolbar helpers, DevTools panel, password prompts) run inside every web page, in the page's own JavaScript world, so before
this a hostile page could call the same bridge and wrap the same entry points: replay requests on the host with the user's cookies and read the
answers (CORS / same-origin bypass), read the clipboard, list every open tab (private ones too), and read the browsing history, bookmarks and
downloads from the state the host pushes. Every message from BrAIn's scripts now carries a per-view secret that page scripts cannot get; nothing a
page has no business with is pushed into its view; edit-and-resend only works on the inspected site.

This opens a 'bank' in one tab and a hostile page (another site) in a third and checks the side effects, not just the replies."""
from mcp import *
import evilsite

BANK, EVIL = evilsite.start()
c = MCP(['--no-fullscreen'])
time.sleep(4)
c.call('navigate', url=BANK + '/login'); time.sleep(1.2)           # the user is logged in to the bank
c.call('new_tab', url=BANK + '/secret?private-tab-marker'); time.sleep(1.5)   # ... and has another tab open on it
n_before = len(c.tabs())
c.call('new_tab', url=EVIL + '/'); time.sleep(6)                    # ... then visits a hostile page
d = evilsite.COLLECTED[-1] if evilsite.COLLECTED else {}
txt = json.dumps(d.get('got', []))

check('the hostile page ran and reported', bool(d), 'no report from the page')
check('the page could not replace the host push entry point (sealed)', d.get('sealed') is True, d.get('sealed'))
check("BrAIn's private handle is not left in the page", d.get('cap') == 'undefined', d.get('cap'))
replayed = [h for h in evilsite.HITS if 'BrAIn-DevTools' in h[1]]
check('the host never replayed a request for the page (edit-and-resend)', not replayed, replayed)
check('no forged command opened a tab', len(c.tabs()) == n_before + 1, f'{n_before} tabs + the page = {len(c.tabs())}')
check('the page saw nothing of the other tab', 'private-tab-marker' not in txt and '8772/secret' not in txt, txt[:200])
check('...nor the history, bookmarks or session lists', '"hist"' not in txt and '"bmarks"' not in txt and 'sessall' not in txt)
check('...nor the bank cookie or the bank page', 'SECRET-TOKEN-123' not in txt and 'ACCOUNT BALANCE' not in txt)
check('the host still pushes the page what it needs (its own tab)', any('localhost:8773' in g for g in d.get('got', [])), txt[:160])

# the page's own DevTools must still work for the user: open it with a real key and read the cookies pane
sh('xdotool key F12'); time.sleep(2.5)
st = c.call('network_record', action='status')   # the panel opening switches the recorder on: proof it still reaches the host
check('DevTools still opens on a web page and talks to the host', 'on' in st, st)
sh('xdotool key F12')
c.close()
finish()
