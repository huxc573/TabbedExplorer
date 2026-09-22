"""量 MDL2 字形的实际墨迹大小 —— 用于决定窗口按钮（最小化/最大化/关闭）该用多大字号。

背景：这几个字形（E921/E922/E923/E8BB）在 MDL2 里是**填满 em 框**的，
而工具按钮那几个（E81C 历史 / E7A7 恢复 / E734 星）留了边距。
所以同样 12px 下，窗口按钮看着明显比工具按钮大一圈 —— 用户报的「图标太大了，和其它不统一」。
这里把 ink bbox 量出来，好按「跟工具按钮一样大」来挑字号（不靠眼睛猜）。
"""
from PIL import Image, ImageDraw, ImageFont
import glob, os, sys

FONT = r'C:/Windows/Fonts/segmdl2.ttf'
TOOLS = [("E81C", "历史"), ("E7A7", "恢复"), ("E734", "星")]
WBUT = [("E921", "最小化"), ("E922", "最大化"), ("E923", "还原"), ("E8BB", "关闭")]


def ink(glyph, size):
    f = ImageFont.truetype(FONT, size)
    im = Image.new('L', (size * 4, size * 4), 0)
    d = ImageDraw.Draw(im)
    d.text((size * 2, size * 2), glyph, font=f, fill=255, anchor="mm")
    bb = im.getbbox()
    if not bb:
        return (0, 0)
    return (bb[2] - bb[0], bb[3] - bb[1])


out = []
for size in (9, 10, 11, 12, 13, 14):
    row = {"size": size}
    for cod, name in TOOLS:
        row[name] = ink(chr(int(cod, 16)), size)
    for cod, name in WBUT:
        row[name] = ink(chr(int(cod, 16)), size)
    out.append(row)

hdr = ["字号"] + [n for _, n in TOOLS] + [n for _, n in WBUT]
print(" | ".join("%-8s" % h for h in hdr))
for r in out:
    cells = ["%-8s" % r["size"]]
    for _, n in TOOLS + WBUT:
        w, h = r[n]
        cells.append("%-8s" % ("%dx%d" % (w, h)))
    print(" | ".join(cells))

# 工具按钮 12px 那几个的平均高度 = 我们想要的「统一」基准
base = sum(r[n][1] for r in out if r["size"] == 12 for _, n in TOOLS) / len(TOOLS)
print("\n12px 工具按钮平均高 = %.1f px" % base)
for r in out:
    avg = sum(r[n][1] for _, n in WBUT) / len(WBUT)
    print("  %2dpx 窗口按钮平均高 = %5.1f px  (比工具高 %+.1f)" % (r["size"], avg, avg - base))
