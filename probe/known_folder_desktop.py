"""只读：把「桌面 / 下载 / 文档 / 图片 / 音乐 / 视频」这批已知文件夹的真路径问出来，
顺便看 `shell:Desktop` / `shell:Downloads` 这类别名解析成什么（**不开任何窗口**）。

给 `PathRules` 挑值用：地址栏在「桌面」这种位置给的是**显示名**，而显示名交给 explorer 是错的
（`explorer.exe "桌面"` 会当成相对路径）。老办法是存 `shell:Desktop`，实测这条路打开慢得离谱
（那扇窗 39 秒才落定，把还原整条链拖住）—— 所以改成按 FOLDERID 问真路径。

用法: python known_folder_desktop.py
"""
import ctypes
from ctypes import wintypes

shell32 = ctypes.windll.shell32
ole32 = ctypes.windll.ole32

shell32.SHParseDisplayName.argtypes = [wintypes.LPCWSTR, ctypes.c_void_p,
                                       ctypes.POINTER(ctypes.c_void_p),
                                       wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
shell32.SHParseDisplayName.restype = ctypes.c_long
shell32.SHGetPathFromIDListW.argtypes = [ctypes.c_void_p, wintypes.LPWSTR]
shell32.SHGetPathFromIDListW.restype = wintypes.BOOL
shell32.SHGetKnownFolderPath.argtypes = [ctypes.c_void_p, wintypes.DWORD,
                                         ctypes.c_void_p, ctypes.POINTER(wintypes.LPWSTR)]
shell32.SHGetKnownFolderPath.restype = ctypes.c_long
ole32.CLSIDFromString.argtypes = [wintypes.LPCWSTR, ctypes.c_void_p]
ole32.CLSIDFromString.restype = ctypes.c_long
ole32.CoTaskMemFree.argtypes = [ctypes.c_void_p]

FOLDERS = [
    ('Desktop',     'B4BFCC3A-DB2C-424C-B029-7FE99A87C641'),
    ('Downloads',   '374DE290-123F-4565-9164-39C4925E467B'),
    ('Documents',   'FDD39AD0-238F-46AF-ADB4-6C85480369C7'),
    ('Pictures',    '33E28130-4E1E-4676-835A-98395C3BC3BB'),
    ('Music',       '4BD8D571-6D19-48D3-BE97-422220080E43'),
    ('Videos',      '35286A68-3C57-41A1-BBB1-0EAE73D76C95'),
]


class GUID(ctypes.Structure):
    _fields_ = [('Data1', ctypes.c_ulong), ('Data2', ctypes.c_ushort),
                ('Data3', ctypes.c_ushort), ('Data4', ctypes.c_byte * 8)]


def guid_of(s):
    g = GUID()
    ole32.CLSIDFromString(ctypes.c_wchar_p('{%s}' % s), ctypes.byref(g))
    return g


def resolve(text):
    pidl = ctypes.c_void_p()
    attr = wintypes.DWORD(0)
    hr = shell32.SHParseDisplayName(text, None, ctypes.byref(pidl), 0, ctypes.byref(attr))
    if hr != 0 or not pidl:
        return '(解析失败 0x%08X)' % (hr & 0xFFFFFFFF)
    try:
        buf = ctypes.create_unicode_buffer(1024)
        ok = shell32.SHGetPathFromIDListW(pidl, buf)
        return buf.value if ok else '(不是文件系统路径)'
    finally:
        ole32.CoTaskMemFree(pidl)


print('=== FOLDERID -> 真路径 ===')
for name, g in FOLDERS:
    out = wintypes.LPWSTR()
    hr = shell32.SHGetKnownFolderPath(ctypes.byref(guid_of(g)), 0, None, ctypes.byref(out))
    p = out.value if hr == 0 and out else '失败 0x%08X' % (hr & 0xFFFFFFFF)
    print('  %-10s %s  ->  %s' % (name, g, p))
    if out:
        ole32.CoTaskMemFree(out)

print()
print('=== 别名写法解析成什么 ===')
for c in ['shell:Desktop', 'shell:Downloads', 'shell:Personal', 'shell:MyPictures',
          'shell:MyMusic', 'shell:MyVideos', 'shell:MyComputerFolder',
          'shell:RecycleBinFolder', 'shell:NetworkPlacesFolder']:
    print('  %-26s -> %s' % (c, resolve(c)))
