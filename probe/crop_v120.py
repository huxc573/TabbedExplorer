# -*- coding: utf-8 -*-
"""只读探针：把 top_band_check.png 裁两块出来看细节。

① 整条标签条（含标题栏那一行）—— 看外壳是不是连成一片纯黑、加号是不是跟着标签；
② 标签条最右边一小块 —— 看设置按钮那颗齿轮画出来没有。
"""
import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
src = os.path.join(HERE, "top_band_check.png")
img = Image.open(src)
W, H = img.size
print("窗口抓图尺寸:", W, H)

# 150% DPI：标题栏 45px + 标签条 51px + 上下边框 6px ≈ 102
band_h = 104

top = img.crop((0, 0, W, band_h))
top = top.resize((W // 2, band_h // 2 * 2), Image.LANCZOS)
top.save(os.path.join(HERE, "v120_tabbar.png"))
print("saved v120_tabbar.png", top.size)

gear = img.crop((W - 200, 40, W, band_h))
gear = gear.resize((gear.width * 3, gear.height * 3), Image.LANCZOS)
gear.save(os.path.join(HERE, "v120_gear_3x.png"))
print("saved v120_gear_3x.png", gear.size)

# 加号应该在最后一个标签右边：扫标签条那一行找非黑像素的分布
row = 74          # 标签条中线（45..96 之间）
runs = []
prev = False
start = 0
for x in range(W):
    c = img.getpixel((x, row))
    lit = (c != (0, 0, 0))
    if lit and not prev:
        start = x
    if not lit and prev:
        runs.append((start, x - 1))
    prev = lit
if prev:
    runs.append((start, W - 1))
print("标签条中线 x=%d 上的非黑区间（设备px）:" % row)
for a, b in runs:
    print("   %5d .. %5d   (逻辑 %.0f..%.0f)" % (a, b, a / 1.5, b / 1.5))
