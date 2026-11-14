#!/usr/bin/env python3
"""Draws the dmg window background (640x400 and @2x 1280x800) next to this script.
Needs Pillow (build tool only, never shipped). Usage: python make-dmg-background.py"""
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
BASE, LIGHT, DARK, ARROW, TEXT = "#d4d0c8", "#ffffff", "#808080", "#404040", "#404040"
CAPTION = "Drag Quartermaster to Applications"


def find_font(size):
    windir = os.environ.get("WINDIR", "")
    cands = [os.path.join(windir, "Fonts", n) for n in ("tahoma.ttf", "arial.ttf")] if windir else []
    cands += ["/System/Library/Fonts/Helvetica.ttc", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"]
    for c in cands:
        if os.path.isfile(c):
            try:
                return ImageFont.truetype(c, size)
            except OSError:
                pass
    return ImageFont.load_default(size)


def draw(scale):
    w, h = 640 * scale, 400 * scale
    img = Image.new("RGB", (w, h), BASE)
    d = ImageDraw.Draw(img)
    s = scale
    # subtle classic bevel frame: light top/left, dark bottom/right
    d.line([(0, 0), (w - 1, 0)], fill=LIGHT, width=s)
    d.line([(0, 0), (0, h - 1)], fill=LIGHT, width=s)
    d.line([(0, h - s), (w - 1, h - s)], fill=DARK, width=s)
    d.line([(w - s, 0), (w - s, h - 1)], fill=DARK, width=s)
    # arrow x 230..410 at y 200 (icon spots at 160 and 480 stay clear)
    y = 200 * s
    d.rectangle([230 * s, y - 6 * s, 390 * s, y + 6 * s], fill=ARROW)
    d.polygon([(388 * s, y - 22 * s), (410 * s, y), (388 * s, y + 22 * s)], fill=ARROW)
    # caption centred at y~300
    f = find_font(18 * s)
    d.text((w // 2, 300 * s), CAPTION, font=f, fill=TEXT, anchor="mm")
    return img


if __name__ == "__main__":
    draw(1).save(os.path.join(HERE, "dmg-background.png"))
    draw(2).save(os.path.join(HERE, "dmg-background@2x.png"))
    print("wrote dmg-background.png and dmg-background@2x.png")
