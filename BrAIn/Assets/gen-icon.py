#!/usr/bin/env python3
"""BrAIn app icon — light pastel tile + white line-art brain mesh.
Lobed brain silhouette (perturbed circle) enclosing a small neural mesh:
a center node wired to a ring of nodes. Full-bleed rounded tile, renders
2048px, builds icon.iconset -> icon.icns and multi-size PNG-in-ICO.
Usage: python3 gen-icon.py
"""
import math, os, shutil, subprocess, struct
from PIL import Image, ImageChops, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
S = 2048
CORNER = int(S * 0.225)
W = 58           # contour stroke width
WI = 40          # inner mesh stroke width


def tile_mask():
    m = Image.new('L', (S, S), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, S - 1, S - 1],
                                        radius=CORNER, fill=255)
    return m


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def lgrad(size, c1, c2, c3=None):
    g = Image.new('RGB', (size, size))
    px = g.load()
    for y in range(size):
        for x in range(size):
            t = (x / size + y / size) / 2
            if c3 and t > .5:
                px[x, y] = lerp(c2, c3, min(1, (t - .5) * 2))
            else:
                px[x, y] = lerp(c1, c2, t * 2 if c3 else t)
    return g


def cap(d, x, y, w):
    d.ellipse([(x - w / 2, y - w / 2), (x + w / 2, y + w / 2)], fill=255)


def render():
    mask = tile_mask()

    # ---- tile: soft periwinkle -> cyan diagonal gradient ----
    tile = lgrad(S, (168, 172, 255), (94, 120, 250), (56, 200, 225))
    # slight top-left lift for gloss
    lift = Image.new('L', (S, S), 0)
    ImageDraw.Draw(lift).ellipse([(-S * .2, -S * .45), (S * .75, S * .35)], fill=30)
    tile = ImageChops.add(tile, Image.merge('RGB',
                                          (lift.filter(ImageFilter.GaussianBlur(160)),) * 3))

    # ---- the mark: lobed brain contour + neural mesh ----
    cx, cy = S / 2, S / 2 + S * .01
    st = Image.new('L', (S, S), 0)
    d = ImageDraw.Draw(st)

    # contour: circle perturbed by ~9 lobes -> bumpy brain/cloud silhouette
    pts = []
    R = S * .245
    for i in range(361):
        ph = math.radians(i)
        r = R * (1 + .115 * math.sin(9 * ph + 1.1))
        pts.append((cx + r * math.cos(ph), cy + r * math.sin(ph)))
    d.line(pts, fill=255, width=W, joint='curve')

    # neural mesh: center node + 5 ring nodes, wired center->ring + ring edges
    ringR = S * .115
    ring = [(cx + ringR * math.cos(math.radians(-90 + i * 72)),
             cy + ringR * math.sin(math.radians(-90 + i * 72))) for i in range(5)]
    for i in range(5):
        d.line([ring[i], ring[(i + 1) % 5]], fill=255, width=WI)
    for x, y in ring:
        d.line([(cx, cy), (x, y)], fill=255, width=WI)
    nodeR = S * .034
    for x, y in ring:
        cap(d, x, y, nodeR * 2)
    cap(d, cx, cy, nodeR * 2.3)

    # soft indigo shadow under the mark, then the white stroke itself
    sh = Image.new('L', (S, S), 0)
    sh.paste(st, (0, int(S * .012)))
    shadow = Image.new('RGB', (S, S), (70, 80, 190))
    tile = ImageChops.subtract(
        tile, ImageChops.multiply(shadow, Image.merge(
            'RGB', (sh.filter(ImageFilter.GaussianBlur(22)),) * 3)
            .point(lambda v: v * .35)))
    tile = ImageChops.screen(tile, Image.composite(
        Image.new('RGB', (S, S), (255, 255, 255)), Image.new('RGB', (S, S)), st))

    out = Image.new('RGBA', (S, S))
    out.paste(tile, (0, 0))
    out.putalpha(mask)
    return out


def build(master):
    out1024 = master.resize((1024, 1024), Image.LANCZOS)
    out1024.save(os.path.join(HERE, 'icon-src.png'))

    iset = os.path.join(HERE, 'icon.iconset')
    shutil.rmtree(iset, ignore_errors=True)
    os.makedirs(iset)
    for n, px in [('16x16', 16), ('16x16@2x', 32), ('32x32', 32), ('32x32@2x', 64),
                  ('128x128', 128), ('128x128@2x', 256), ('256x256', 256),
                  ('256x256@2x', 512), ('512x512', 512), ('512x512@2x', 1024)]:
        out1024.resize((px, px), Image.LANCZOS).save(
            os.path.join(iset, 'icon_%s.png' % n))
    icns = os.path.join(HERE, '..', 'dist', 'BrAIn.app', 'Contents',
                        'Resources', 'icon.icns')
    subprocess.run(['iconutil', '-c', 'icns', iset, '-o', icns], check=True)
    shutil.rmtree(iset)
    print('icns ->', icns)

    sizes = [16, 24, 32, 48, 64, 128, 256]
    pngs = []
    import io
    for px in sizes:
        buf = io.BytesIO()
        out1024.resize((px, px), Image.LANCZOS).save(buf, 'PNG')
        pngs.append(buf.getvalue())
    ico = io.BytesIO()
    ico.write(struct.pack('<HHH', 0, 1, len(sizes)))
    off = 6 + 16 * len(sizes)
    for px, data in zip(sizes, pngs):
        ico.write(struct.pack('<BBBBHHII',
                              px if px < 256 else 0, px if px < 256 else 0,
                              0, 0, 1, 32, len(data), off))
        off += len(data)
    for data in pngs:
        ico.write(data)
    with open(os.path.join(HERE, 'icon.ico'), 'wb') as f:
        f.write(ico.getvalue())
    print('ico bytes:', ico.getbuffer().nbytes)


if __name__ == '__main__':
    build(render())
