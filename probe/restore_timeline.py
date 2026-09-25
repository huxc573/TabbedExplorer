# -*- coding: utf-8 -*-
"""只读：把 data/log.txt 里一轮「还原标签」的时间线拉成一张表（找还原为什么慢用）。

回答两个问题：
  · 从「用户按下那一开」到「第一个标签能用了」花了多少毫秒（各段分别多少）；
  · 后面每个标签各自又花了多少（间隔有多大），尾巴卡在哪一步。
输入 = 应用自己的日志（设置里 Debug 模式开着才有）。用法：
    python restore_timeline.py [log路径] [--from HH:MM:SS.mmm]
不带 --from 就只分析**最后一次**还原（从最后一处「转生完成 / 摆上 N 个标签占位」算起）。
"""

import io
import re
import sys

LOG = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("--") else \
    r"D:\Software\!Sync\TabbedExplorer\data\log.txt"

TS = re.compile(r"(?:20\d\d-\d\d-\d\d )?(\d\d):(\d\d):(\d\d)\.(\d+)")
KEY = re.compile(
    r"起 explorer /n|排入队列|起进程的格到点放行|发现 cab=|开始嵌入|收编耗时|现在在 |"
    r"摆上 \d+ 个标签占位|优先那个已落定|标签开始加载|转生完成|备用标签已就绪|备用标签排进队尾|"
    r"用掉预热好的备用标签")


def ms(l):
    m = TS.match(l)
    if not m:
        return None
    return ((int(m.group(1)) * 60 + int(m.group(2))) * 60 + int(m.group(3))) * 1000 \
        + int(m.group(4))


def main():
    lines = io.open(LOG, encoding="utf-8", errors="replace").read().splitlines()
    # 最后一次还原的起点 = 最后一处「摆上 N 个标签占位」
    start = None
    for i, l in enumerate(lines):
        if "摆上" in l and "个标签占位" in l:
            start = i
    if start is None:
        print("日志里没有「摆上 N 个标签占位」—— 这一轮没发生过还原？")
        return
    base = ms(lines[start])
    print("起点（摆占位的那一行）：%s" % lines[start][:120])
    print()
    first = None
    prev = base
    n = 0
    for l in lines[start:]:
        t = ms(l)
        if t is None or not KEY.search(l):
            continue
        s = l[13:150]
        d = t - base
        if first is None and ("现在在 " in s or "收编耗时" in s):
            first = d
        n += 1
        print("  +%6dms (Δ%+5d) %s" % (d, t - prev, s))
        prev = t
    print()
    print("事件行 %d 条；第一个标签可用 +%s ms；到备用标签就绪 +%d ms"
          % (n, first if first is not None else "?", prev - base))


main()
