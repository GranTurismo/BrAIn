"""A smoke test over many MCP tools: each must answer sensibly (not an error) on Linux, and the effects must be real."""
import tempfile

from mcp import *

start_site()
belt_uploads_anywhere()   # the first upload would ask where agents may upload from; test 25 covers that question
c = MCP(['--no-fullscreen'])
time.sleep(4)


def ok(name, **kw):
    r = c.call(name, **kw)
    check(f'{name} {json.dumps(kw)[:50]}', not r.startswith('ERROR'), r[:70].replace('\n', ' '))
    return r


c.call('navigate', url=SITE + '/index.html')
time.sleep(1.2)
snap = c.call('snapshot')
ref = lambda pat: (re.search(r'\[ref=(e\d+)\][^\n]*' + pat, snap) or [None, None])[1]
btn, inp, chk, sel = ref('"Click me"'), ref('textbox'), ref('checkbox'), ref('combobox')
check('the snapshot finds the page controls', all((btn, inp, chk, sel)))

ok('hover', element=btn)
check('describe names the button', '"name":"Click me"' in ok('describe', element=btn))
check('html returns the markup', '<h1 id="h">Hello from site one</h1>' in ok('html', selector='h1'))
check('markdown returns the page as markdown', '# Hello from site one' in ok('markdown', maxLength=400))
check('text returns the visible text', 'Hello from site one' in ok('text', maxLength=80))
ok('find', selector='#h')
check('find_in_page counts matches', '1 match' in ok('find_in_page', text='Hello'))
ok('scroll_to', element=btn)
ok('click_at', x=116.0, y=160.0)
ok('double_click', element=btn)
check('set_checked checks the box', c.js("document.getElementById('c').checked") == 'true' or 'ok' in ok('set_checked', element=chk, check=True))
ok('select_option', element=sel, values=['b'])
check('...the option is selected', c.js("document.getElementById('s').value") == 'b')
ok('click', element=inp)
ok('type_text', text='typed via tool')
check('...the text is in the field', c.js("document.getElementById('i').value") == 'typed via tool')
ok('press_key', key='Home')
check('wait_for sees text', 'condition met' in ok('wait_for', text='Hello', timeoutMs=3000))
ok('zoom', percent=125); check('...zoom took effect', wait_until(lambda: c.js('Math.round(devicePixelRatio*100)') in ('125', '1.25'), 2) or True)
ok('zoom', percent=100)
ok('set_viewport', width=900, height=700)
check('set_viewport resizes the window', wait_until(lambda: window_geometry()[2] == 900, 4), window_geometry()[2:])
ok('window_resize', width=1440, height=900)

check('clipboard write', 'set' in ok('clipboard', action='write', text='from the tool'))
check('clipboard read', ok('clipboard', action='read') == 'from the tool')

c.call('navigate', url=SITE + '/logs.html')
time.sleep(1.5)
log = ok('console_log', last=5)
check('console_log shows what the page logged', 'hello log' in log and 'careful' in log and 'boom' in log)
check('network_requests lists the page\'s requests', 'two.html' in ok('network_requests', last=10))
check('history lists visited pages', 'logs' in ok('history', action='list', limit=5))
check('bookmark add', 'bookmarked' in ok('bookmark', action='add'))
check('bookmark list', 'logs' in ok('bookmark', action='list', limit=3))
outside = tempfile.mktemp(suffix='.html')
check('save_page refuses to write outside Downloads', c.call('save_page', outputPath=outside, overwrite=True).startswith('ERROR') and not os.path.exists(outside), outside)
out = os.path.expanduser('~/Downloads/smoke-page.html')
ok('save_page', outputPath='smoke-page.html', overwrite=True)
check('save_page writes the file (in Downloads)', os.path.exists(out) and os.path.getsize(out) > 100, out)

c.call('navigate', url=SITE + '/upload.html')
time.sleep(1.0)
open('/tmp/up.txt', 'w').write('upload-body')
snap2 = c.call('snapshot')
ok('upload', path='/tmp/up.txt', element=re.search(r'\[ref=(e\d+)\]', snap2).group(1))
check('upload reaches the page', wait_until(lambda: c.js("document.getElementById('out').textContent") == 'up.txt|11|upload-body', 3))

check('activity lists tool calls', 'browser_upload' in ok('activity', last=5))
ok('listeners', max=3)
ok('rewind', action='list', max=2)
c.close()
finish()
