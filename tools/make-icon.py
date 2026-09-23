# -*- coding: utf-8 -*-
"""生成 app.ico（程序自己的图标：曼尼拉文件夹 + 蓝色文件夹耳）。

为什么要脚本：图标是多尺寸的，手改一个尺寸其它尺寸就废了；而且 16px 能不能认出来
只能靠「缩小了看」验证（见文件末尾的预览输出）。

跑法：python tools/make-icon.py     （需要 Pillow）
产出：assets/app.ico（按尺寸内嵌 16/20/24/32/40/48/64/128/256）、assets/app-icon-1024.png（源图，便于以后再调）
"""
import os
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "assets")
os.makedirs(OUT, exist_ok=True)

S = 1024
MANILA = (246, 214, 124, 255)
HI     = (253, 236, 182, 255)
DK     = (226, 186, 92, 255)
EDGE   = (190, 146, 46, 255)
BLUE   = (76, 194, 255, 255)
BLUE_DK= (32, 124, 186, 255)

im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(im)

def rr(b, r, f, o=None, w=0):
    d.rounded_rectangle(b, radius=r, fill=f, outline=o, width=w)

# 蓝色文件夹耳 —— 小尺寸下唯一的记忆点
rr((72, 232, 560, 430), 48, BLUE, BLUE_DK, 14)
rr((72, 330, 952, 900), 64, DK)                 # 后片
rr((72, 398, 952, 952), 64, MANILA, EDGE, 18)   # 前片（描边让 16px 立得住）
rr((134, 452, 890, 548), 34, HI)                # 前片顶部高光

im.save(os.path.join(OUT, "app-icon-1024.png"))
im.resize((256, 256), Image.LANCZOS).save(
    os.path.join(OUT, "app.ico"), sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)])

# 预览：16/20/24/32/48/64 并排（托盘是 16~20，一定要看得清）
W = 16+20+24+32+48+64 + 6*22
prev = Image.new("RGBA", (W, 64), (70, 70, 70, 255))
x = 12
for s in (16, 20, 24, 32, 48, 64):
    prev.alpha_composite(im.resize((s, s), Image.LANCZOS), (x, max(0, (64 - s) // 2 - 8) if s < 48 else 0))
    x += s + 22
prev.resize((prev.width * 3, prev.height * 3), Image.NEAREST).save(os.path.join(ROOT, "probe", "app_icon_preview.png"))
print("ok: assets/app.ico + assets/app-icon-1024.png + probe/app_icon_preview.png")
