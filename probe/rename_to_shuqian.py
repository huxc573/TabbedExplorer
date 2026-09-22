# -*- coding: utf-8 -*-
"""一次性：把源码里面向用户的「收藏夹」文案改成「书签」。

2026-09-22 川要求「收藏夹改名为书签」。
代码标识符（FavStore / FavBar / favbar / favorites.json）一律不动 ——
所以这里只做**中文文案**的字节级替换，不碰任何 ASCII。

按字节替换：文件里有 BOM 的保留 BOM，纯 LF 原样保留（本仓库 src 全是 LF）。
每一处替换都断言命中次数，没命中就报出来（免得静默漏改）。
"""
import glob
import sys

# (文件, 旧, 新, 期望命中次数)  期望 -1 = 不校验
RULES = [
    ('*',      '收藏夹', '书签', -1),
    ('FavBar.cs', '把文件夹或文件拖到这条栏上就能收藏',
                  '把文件夹或文件拖到这条栏上就能加进书签', 1),
    ('FavBar.cs', '这个文件夹里还没有收藏。', '这个文件夹里还没有书签。', 1),
    ('FavBar.cs', '重命名收藏', '重命名字签', 1),
    ('FavManagerForm.cs', '没有匹配的收藏', '没有匹配的书签', 1),
    ('FavManagerForm.cs', '这个文件夹里还没有收藏（', '这个文件夹里还没有书签（', 1),
    ('FavStore.cs', '拖进来就算收藏', '拖进来就算书签', 1),
]

def main():
    files = {}
    for f in sorted(glob.glob('src/*.cs')):
        files[f.replace('\\', '/').split('/')[-1]] = f

    total = 0
    problems = []
    for name, old, new, expect in RULES:
        ob = old.encode('utf-8')
        nb = new.encode('utf-8')
        targets = files.values() if name == '*' else [files[name]] if name in files else []
        if not targets:
            problems.append('找不到文件 ' + name)
            continue
        hit = 0
        for path in targets:
            b = open(path, 'rb').read()
            c = b.count(ob)
            if c:
                open(path, 'wb').write(b.replace(ob, nb))
                hit += c
        total += hit
        tag = name if name != '*' else '全部文件'
        if expect >= 0 and hit != expect:
            problems.append('%s: %r 命中 %d 次（期望 %d）' % (tag, old, hit, expect))
        print('%-22s %-28s -> %-28s %d 处' % (tag, old, new, hit))

    print('---- 共替换 %d 处 ----' % total)
    if problems:
        print('!! 有问题的项：')
        for p in problems:
            print('   ' + p)
        return 1
    return 0

sys.exit(main())
