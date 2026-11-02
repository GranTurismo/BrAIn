"""Real keyboard input: shortcuts, non-Latin text (Georgian, accents) in the page and the omnibox, and the print dialog."""
import threading

from mcp import *

start_site()
c = MCP(['--no-fullscreen'])
time.sleep(4)
c.call('navigate', url=SITE + '/index.html')
time.sleep(1.2)
sh('xdotool mousemove 700 500 click 1')    # the page owns the keyboard

# ---- tab shortcuts, with real X key events
sh('xdotool key ctrl+t'); time.sleep(1.2)
check('Ctrl+T opens a new tab', len(c.tabs()) == 2, len(c.tabs()))
sh('xdotool key ctrl+l'); time.sleep(0.5)
sh(f'xdotool type --delay 40 "{SITE}/two.html"'); time.sleep(0.5); sh('xdotool key Return')
check('typing a URL in the omnibox + Enter navigates', wait_until(lambda: 'Page Two' in ''.join(r for a, i, r in c.tabs() if a), 5), c.tabs())
sh('xdotool key ctrl+w'); time.sleep(1.2)
check('Ctrl+W closes the tab', len(c.tabs()) == 1, len(c.tabs()))

# ---- text input with non-Latin characters
c.js("document.getElementById('i').focus(); 1")
sh('xdotool type --delay 40 "გამარჯობა ñé ü"'); time.sleep(0.8)
check('Georgian and accented text reaches a page input', c.js("document.getElementById('i').value") == 'გამარჯობა ñé ü', c.js("document.getElementById('i').value"))
sh('xdotool key ctrl+l'); time.sleep(0.5)
sh('xdotool type --delay 40 "ქართული"'); time.sleep(1.0)
desk('16-omnibox-georgian')
x, y, w, h = window_geometry()
check('...and the omnibox (its dropdown offers a search)', strip_stddev() > 8)
sh('xdotool key Escape'); time.sleep(0.8)

# ---- print dialog
def print_page():
    try:
        c.js('setTimeout(()=>window.print(),50); 1')
    except Exception:
        pass


threading.Thread(target=print_page, daemon=True).start()
dlg = wait_until(lambda: sh("xdotool search --onlyvisible --name '^Print$' | head -1"), 6)
check('window.print() opens the print dialog', bool(dlg))
desk('16-print')
sh('xdotool key Escape'); time.sleep(1.0)
check('the dialog closes with Esc', wait_until(lambda: not sh("xdotool search --onlyvisible --name '^Print$' | head -1"), 4))
check('the app is still responsive', c.js('document.title') == 'Site One')
c.close()
finish()
