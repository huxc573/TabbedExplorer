# -*- coding: utf-8 -*-
"""只读：看 TB 日志里，用户手动在地址栏回车后，那次导航的完整轨迹。"""
import re, io, sys
p = r"D:\Software\!Sync\TabbedExplorer\data\log.txt"
lines = io.open(p, encoding="utf-8", errors="replace").read().splitlines()
# 找所有「现在在 此电脑\motorola」这类行，以及前后的上下文
for i, ln in enumerate(lines):
    if "motorola" in ln:
        lo = max(0, i - 6); hi = min(len(lines), i + 6)
        print("=" * 70)
        for j in range(lo, hi):
            mark = ">>" if j == i else "  "
            print(mark, lines[j])
