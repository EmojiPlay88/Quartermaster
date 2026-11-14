"""Draws build/icon/quartermaster.png: 256x256, grey rounded square, dark Q monogram.
Build-time tool only (needs Pillow); the PNG is committed. Run: python make-icon.py"""
from pathlib import Path
from PIL import Image, ImageDraw

S, K = 256, 4  # final size, supersampling factor
big = S * K
img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle((8 * K, 8 * K, big - 8 * K, big - 8 * K), radius=44 * K, fill=(0xD4, 0xD0, 0xC8, 255))
dark = (0x2B, 0x2B, 0x2B, 255)
c = big / 2
r = 72 * K
w = 26 * K
d.ellipse((c - r, c - r, c + r, c + r), outline=dark, width=w)
# tail of the Q: a thick diagonal stroke crossing the lower right of the ring
d.line((c + 22 * K, c + 22 * K, c + 82 * K, c + 82 * K), fill=dark, width=w)
img = img.resize((S, S), Image.LANCZOS)
out = Path(__file__).with_name("quartermaster.png")
img.save(out, optimize=True)
print("wrote", out)
