#!/usr/bin/env python3
"""Regenerates chrome.js's in-document start-page CSS from the NTP-CSS block
in newtab.html, so the real start page and the overlay can't drift apart.
Run from anywhere after editing that block:  python3 Assets/gen-ntp.py"""
import re, os
here = os.path.dirname(os.path.abspath(__file__))
nt = open(os.path.join(here, 'newtab.html')).read()
blk = nt[nt.index('/*NTP-CSS-START*/') + len('/*NTP-CSS-START*/'):nt.index('/*NTP-CSS-END*/')]
blk = re.sub(r'/\*.*?\*/', '', blk, flags=re.S)  # drop comments
out = []
for l in blk.split('\n'):
    t = l.strip()
    if not t or t.startswith('/*') or t.startswith('*'): continue
    if t.startswith('@keyframes rise') or t.startswith('html.anim'): continue
    out.append(t)
css = ' '.join(out)
rules = re.findall(r'[^{}]+\{[^{}]*\}', css)
js = "\n".join("            '" + r.strip().replace("\\", "\\\\").replace("'", "\\'") + "' +" for r in rules)
p = os.path.join(here, 'chrome.js')
s = open(p).read()
a = s.index('// <ntp-generated>') + len('// <ntp-generated>')
b = s.index('// </ntp-generated>')
s = s[:a] + "\n" + js + "\n            " + s[b:]
open(p, 'w').write(s)
print('regenerated', len(rules), 'rules')
