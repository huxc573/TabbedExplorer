from PIL import Image, ImageDraw, ImageFont
cands = ["E81C","E7A7","E734","E735","E72C","E823","E7A8","E10E","E895","E72D",
         "E8FD","E8F4","E71E","E1CB","E8A9","E700","E713","E10C","E12B","E72E"]
f = ImageFont.truetype(r'C:/Windows/Fonts/segmdl2.ttf', 44)
lab = ImageFont.truetype(r'C:/Windows/Fonts/consola.ttf', 12)
cols, cell = 5, 96
rows = (len(cands)+cols-1)//cols
im = Image.new('RGB', (cols*cell, rows*cell), (255,255,255))
d = ImageDraw.Draw(im)
for i,c in enumerate(cands):
    cx, cy = (i%cols)*cell, (i//cols)*cell
    d.rectangle([cx,cy,cx+cell-1,cy+cell-1], outline=(200,200,200))
    ch = chr(int(c,16))
    d.text((cx+cell//2, cy+40), ch, font=f, fill=(0,0,0), anchor="mm")
    d.text((cx+cell//2, cy+cell-12), c, font=lab, fill=(120,120,120), anchor="mm")
im.save('probe/_glyphs.png')
print("saved", im.size)
