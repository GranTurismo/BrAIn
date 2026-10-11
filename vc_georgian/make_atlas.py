import argparse, json
from PIL import Image, ImageDraw, ImageFont

KEYMAP = dict(zip("abgdevzTiklmnopJrstufqRySCcZwWxjh", "აბგდევზთიკლმნოპჟრსტუფქღყშჩცძწჭხჯჰ"))

ap = argparse.ArgumentParser()
ap.add_argument("--font", default="/System/Library/Fonts/SFGeorgian.ttf")
ap.add_argument("--cols", type=int, default=16)
ap.add_argument("--rows", type=int, default=16)
ap.add_argument("--cell", type=int, default=32)
ap.add_argument("--first", type=lambda x: int(x, 0), default=0x20, help="char code of slot 0")
ap.add_argument("--size", type=int, default=0, help="font px size (0 = auto)")
ap.add_argument("--base", help="existing atlas PNG to draw over (same grid)")
ap.add_argument("--out", default="font_georgian.png")
a = ap.parse_args()

W, H = a.cols * a.cell, a.rows * a.cell
img = Image.open(a.base).convert("RGBA").resize((W, H)) if a.base else Image.new("RGBA", (W, H), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
font = ImageFont.truetype(a.font, a.size or int(a.cell * 0.7))
for k, g in KEYMAP.items():
    slot = ord(k) - a.first
    x, y = (slot % a.cols) * a.cell, (slot // a.cols) * a.cell
    d.rectangle([x, y, x + a.cell - 1, y + a.cell - 1], fill=(0, 0, 0, 0))
    d.text((x + a.cell // 2, y + a.cell // 2), g, font=font, fill=(255, 255, 255, 255), anchor="mm")
img.save(a.out)
json.dump(KEYMAP, open("keymap.json", "w"), ensure_ascii=False, indent=1)
print("saved", a.out, W, H)
