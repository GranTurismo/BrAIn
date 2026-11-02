"""Right-click on an image: Copy image / Save image / Copy image address, with the real GTK clipboard read back by a
second process. Also the clipboard tool."""
from mcp import *

start_site()
c = MCP(['--no-fullscreen'])
time.sleep(3.5)
c.call('navigate', url=SITE + '/img.html')
time.sleep(1.2)


def clip(kind):
    code = ("import gi;gi.require_version('Gtk','3.0');from gi.repository import Gtk,Gdk;"
            "cb=Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD);")
    if kind == 'image':
        code += "pb=cb.wait_for_image();print('NOIMAGE' if pb is None else f'{pb.get_width()}x{pb.get_height()}')"
    else:
        code += "print(cb.wait_for_text())"
    return sh(f'python3 -c "{code}" 2>&1 | tail -1')


MENU = """(function(){var im=document.getElementById('im'),r=im.getBoundingClientRect();
 im.dispatchEvent(new MouseEvent('contextmenu',{clientX:r.left+50,clientY:r.top+50,bubbles:true,cancelable:true}));
 var m=document.getElementById('__brain-chrome-host').shadowRoot.getElementById('pmenu');
 return JSON.stringify({shown:m.style.display,items:[].map.call(m.querySelectorAll('.mi'),function(e){return e.dataset.c})})})()"""


def pick(cmd):
    return c.js("""(function(){var it=document.getElementById('__brain-chrome-host').shadowRoot.querySelector('#pmenu .mi[data-c="%s"]');
      if(!it) return 'no such item'; it.dispatchEvent(new MouseEvent('mousedown',{bubbles:true,cancelable:true})); return 'picked'})()""" % cmd)


def toast():
    return c.js("(function(){var t=document.getElementById('__brain-chrome-host').shadowRoot.getElementById('mdtoast');return t?t.textContent:''})()")


menu = json.loads(c.js(MENU))
check('the page menu offers the image items', menu['shown'] == 'block' and {'cimg', 'simg', 'cimgurl'} <= set(menu['items']), menu['items'])
check('the clipboard has no image yet', clip('image') == 'NOIMAGE')
check('Copy image is picked', pick('cimg') == 'picked')
check('the copy is announced', wait_until(lambda: toast() == 'Image copied', 4), toast())
check('a PNG of the right size is on the real clipboard', clip('image') == '5x5', clip('image'))

time.sleep(3.2)   # the host rate-limits image requests
c.js(MENU)
pick('simg')
check('Save image reports where it went', wait_until(lambda: toast().startswith('Saved big.png'), 5), toast())
check('...and the file is in Downloads', os.path.getsize(os.path.expanduser('~/Downloads/big.png')) > 50 if os.path.exists(os.path.expanduser('~/Downloads/big.png')) else False)

c.js(MENU)
pick('cimgurl')
time.sleep(0.8)
check('Copy image address puts the URL on the clipboard', clip('text') == SITE + '/big.png', clip('text'))

check('clipboard tool: write', 'set' in c.call('clipboard', action='write', text='from the tool'))
check('...and read back', c.call('clipboard', action='read') == 'from the tool')
check('...and visible to another process', clip('text') == 'from the tool', clip('text'))
c.close()
finish()
