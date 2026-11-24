# 生成创意工坊预览图 tools/preview.png（640×640）。换成游戏内截图也可以，保持 1MB 以内。
#   python tools/make_preview.py
import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter

W = H = 640
HERE = os.path.dirname(os.path.abspath(__file__))
FONTS = os.path.join(os.environ.get('WINDIR', r'C:\Windows'), 'Fonts')

ACCENT = (92, 199, 255)
GOLD = (255, 209, 89)
GOOD = (115, 230, 128)
PURPLE = (217, 128, 255)
BAD = (255, 107, 97)


def font(name, size):
    return ImageFont.truetype(os.path.join(FONTS, name), size)


img = Image.new('RGB', (W, H))
px = img.load()
for y in range(H):
    for x in range(W):
        t = y / H
        r = int(10 + 8 * t + 10 * (x / W))
        g = int(13 + 12 * t)
        b = int(22 + 26 * t)
        px[x, y] = (r, g, b)

d = ImageDraw.Draw(img, 'RGBA')

# 星点
import random
rnd = random.Random(7)
for _ in range(90):
    x, y = rnd.randrange(W), rnd.randrange(H)
    a = rnd.randrange(60, 200)
    d.point((x, y), fill=(255, 255, 255, a))

# 两艘船的格子：左边我方（标注步骤），右边敌方
cell, gap = 62, 8
def grid(ox, oy, cols, rows, color):
    rects = {}
    for cx in range(cols):
        for cy in range(rows):
            x0 = ox + cx * (cell + gap)
            y0 = oy + cy * (cell + gap)
            d.rounded_rectangle((x0, y0, x0 + cell, y0 + cell), 8, fill=(255, 255, 255, 14), outline=color + (90,), width=2)
            rects[(cx, cy)] = (x0, y0, x0 + cell, y0 + cell)
    return rects

me = grid(64, 120, 3, 3, ACCENT)
enemy = grid(380, 120, 3, 3, BAD)

# 对撞线
for row in range(3):
    y = 120 + row * (cell + gap) + cell // 2
    d.line((64 + 3 * (cell + gap) - gap + 6, y, 380 - 6, y), fill=(255, 255, 255, 40), width=2)
d.text((300, 120 + 0 * 70 + 20), "+4", font=font('msyhbd.ttc', 20), fill=GOOD)
d.text((300, 120 + 1 * 70 + 20), "+2", font=font('msyhbd.ttc', 20), fill=GOOD)
d.text((302, 120 + 2 * 70 + 20), "0", font=font('msyhbd.ttc', 20), fill=(160, 168, 180))

# 手牌和装载连线
card = (120, 360, 176, 430)
d.rounded_rectangle(card, 8, fill=(40, 46, 60, 255), outline=GOLD + (255,), width=3)
d.text((card[0] + 16, card[1] + 14), "5", font=font('msyhbd.ttc', 30), fill=(255, 255, 255))

def center(r):
    return ((r[0] + r[2]) // 2, (r[1] + r[3]) // 2)

def badge(r, text, color):
    x0, y0 = r[0] - 12, r[1] - 12
    w = 30 if len(text) == 1 else 40
    d.rounded_rectangle((x0, y0, x0 + w, y0 + 28), 6, fill=(16, 18, 26, 240), outline=color + (255,), width=2)
    f = font('msyhbd.ttc', 16)
    tw = d.textlength(text, font=f)
    d.text((x0 + (w - tw) / 2, y0 + 3), text, font=f, fill=(255, 255, 255))

d.line(center(card) + center(me[(1, 1)]), fill=GOLD + (200,), width=4)
for k, color in (((1, 1), ACCENT), ((2, 0), ACCENT)):
    d.rounded_rectangle(me[k], 8, outline=color + (255,), width=4)
badge(me[(1, 1)], "1", ACCENT)
badge(me[(2, 0)], "2", ACCENT)
d.rounded_rectangle(me[(0, 2)], 8, outline=PURPLE + (255,), width=3)
d.rounded_rectangle(me[(2, 2)], 8, outline=PURPLE + (255,), width=3)
d.line(center(me[(0, 2)]) + center(me[(2, 2)]), fill=PURPLE + (200,), width=3)
badge(me[(0, 2)], "换3", PURPLE)

# 预测
pred = (236, 372, 590, 420)
d.rounded_rectangle(pred, 10, fill=(16, 20, 28, 220), outline=(56, 70, 90, 255), width=1)
d.text((252, 380), "预计第 3 回合获胜 · 剩 26 HP", font=font('msyhbd.ttc', 20), fill=GOOD)

# 标题
glow = Image.new('RGBA', (W, H), (0, 0, 0, 0))
gd = ImageDraw.Draw(glow)
title_font = font('msyhbd.ttc', 64)
title = "战术求解器"
tw = gd.textlength(title, font=title_font)
gd.text(((W - tw) / 2, 470), title, font=title_font, fill=ACCENT + (255,))
glow = glow.filter(ImageFilter.GaussianBlur(10))
img.paste(glow, (0, 0), glow)
d = ImageDraw.Draw(img, 'RGBA')
d.text(((W - tw) / 2, 470), title, font=title_font, fill=(235, 246, 255))
sub_font = font('segoeuib.ttf', 26)
sub = "T A C T I C S   S O L V E R"
sw = d.textlength(sub, font=sub_font)
d.text(((W - sw) / 2, 552), sub, font=sub_font, fill=ACCENT)
tag_font = font('msyh.ttc', 18)
tag = "真实引擎模拟 · 每一步怎么走 · 船怎么摆"
gw = d.textlength(tag, font=tag_font)
d.text(((W - gw) / 2, 592), tag, font=tag_font, fill=(160, 172, 190))

# 顶部小字
d.text((64, 60), "YOUR SHIP", font=font('segoeuib.ttf', 16), fill=ACCENT)
d.text((380, 60), "ENEMY", font=font('segoeuib.ttf', 16), fill=BAD)

out = os.path.join(HERE, 'preview.png')
img.save(out, optimize=True)
print(out, os.path.getsize(out) // 1024, 'KB')
