"""The welcome film (first launch) runs smoothly, and a toolbar whose web process dies comes back.

Regression (1.8.0, first report from a real desktop: "crashes and lags on the welcome screen"):
 * the toolbar view is painted on the CPU on Linux (LinuxEngine.NewSettings), and the full-effects film — grain, a drifting
   glow, a full-window canvas, big blurred shadows — ran at ~10 fps with that web process pinned at 100%. The film now runs
   in "lite" mode on Linux (welcome.js LITE) and sheds its canvas when it still cannot keep up.
 * when the toolbar's web process died, the window stayed a blank, dead rectangle for good (only page views reloaded).
   The toolbar is reloaded now, and a film that was playing is not replayed on every launch.

Frame rates come from the toolbar's own probe (BRAIN_DEBUG_BAR=1 reports "FPS n" every 2 s). They are only meaningful on
the container's native CPU: do not run this test under x64-on-arm emulation.
"""
from mcp import *
import json, statistics

start_site()
PREFS = os.path.expanduser('~/.local/share/BrAIn/prefs.json')
ERR = '/tmp/brain.err'


def err():
    try:
        return open(ERR, errors='replace').read()
    except OSError:
        return ''


def fps_samples(since=0):
    """FPS values reported while the film was open, in order (a sample line is 'FPS 24.3 worst-frame=47ms (welcome open)')."""
    return [float(m.group(1)) for m in re.finditer(r'FPS ([\d.]+) worst-frame=\d+ms \(welcome open\)', err()[since:])]


# a fresh account: the runner pre-writes {"welcomed":true} so the film does not cover other tests; this one wants it
try:
    os.remove(PREFS)
except FileNotFoundError:
    pass

c = MCP(['--no-fullscreen'], env={'BRAIN_DEBUG_BAR': '1'})
check('the film opens on a fresh profile', wait_until(lambda: fps_samples(), 20, 0.5) != [], 'no "welcome open" frame sample')
desk('welcome-1')

# the first seconds build the film (and the page behind it): judge the steady state
time.sleep(22)
s = fps_samples()
steady = s[3:] if len(s) > 6 else s
med = statistics.median(steady) if steady else 0.0
check('the film keeps a usable frame rate (median >= 30 fps; the full-effects film managed ~18 here, ~10 at 1080p)', med >= 30,
      f'median {med:.1f} fps over {len(steady)} samples: {" ".join(f"{v:.0f}" for v in s)}')
check('the film is still playing (not dismissed, not crashed)', c.p.poll() is None and 'terminated' not in err())

# ---- the toolbar's web process dies while the film plays (memory pressure, a renderer fault)
mark = len(err())
os.system("pkill -9 -f WebKitWebProcess")
check('the toolbar is reloaded after its web process died',
      wait_until(lambda: 'toolbar web process terminated' in err()[mark:], 8), err()[mark:][-200:])
time.sleep(3)
desk('welcome-2-after-crash')
check('the app is still running', c.p.poll() is None)
# (a dead toolbar still shows its last frame, so "the strip is painted" proves nothing: ask the new document itself — its probe
#  reports FPS again, and without the film)
check('the reloaded toolbar document is running again (its frame probe reports, without the film)',
      wait_until(lambda: re.search(r'FPS [\d.]+ worst-frame=\d+ms\n', err()[mark:]), 8), 'no frame sample after the crash')
check('the film that was playing is not replayed on the next launch (counted as seen)',
      wait_until(lambda: os.path.exists(PREFS) and json.load(open(PREFS)).get('welcomed') is True, 5))
check('no film comes back after the reload', fps_samples(mark) == [], f'{len(fps_samples(mark))} "welcome open" samples after the crash')

# ---- and the browser works afterwards: the page views were killed too and come back by themselves
r = c.call('navigate', url=SITE + '/index.html')
check('a page loads afterwards', wait_until(lambda: c.js('document.title') == 'Site One', 10), r[:70])
check('toolbar still painted after that navigation', strip_stddev() > 8)

c.close()
finish()
