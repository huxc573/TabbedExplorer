# -*- coding: utf-8 -*-
PC = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"
DEV = r"\?\usb#vid_18d1&pid_4ee2&mi_00#7&2ebcb680&0&0000#{6ac27878-a6fa-4155-ba85-f98f491d4f33}"
DEV_FULL = PC + "\\" + DEV
p = "此电脑\motorola edge 20 pro\内部共享存储空间\!573\APPS\Eta\projects\los-enhance\refs\official-rom"

i = p.index("\\")
devName = p[i+1:]
j = devName.index("\\")
devName = devName[:j]
rest = p[i+1+j:]
print("设备段(显示名) =", devName)
print("rest           =", rest)
out = DEV_FULL + rest
expect = PC + "\\" + DEV + rest
print()
print("修复后输出 =", out)
print("实测OK     =", expect)
print("逐字符相等 =", out == expect)
