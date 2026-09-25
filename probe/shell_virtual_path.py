"""只读：验证「虚拟路径写法」能不能被 shell 解析成真文件夹（**不开任何窗口**）。

用 `SHParseDisplayName`（explorer.exe 命令行认的就是这套命名空间解析）+ `SHGetKnownFolderPath`
交叉核对。用来给 `PathRules.virtuals` / `PathRules.knownFolders` 挑一个**真能用**的写法，而不是靠猜
—— 实测踩到的坑：`shell:MyPictures` / `shell:MyVideos` 这类老令牌在 Win10 上压根解析不了
（0x80070003），而 `shell:::{GUID}` 解出来是个**虚拟项**（取不到文件系统路径，跟窗口在看的那个
文件夹不是一回事）⇒ 已知文件夹只能按 FOLDERID 问真路径。

用法: python shell_virtual_path.py
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

# FOLDERID 的定义（16 字节：Data1/Data2/Data3 + 8 字节 Data4）
FOLDERS = [
    ('Pictures', '33E28130-4E1E-4676-835A-98395C3BC3BB'),
    ('Videos',   '35286A68-3C57-41A1-BBB1-0EAE73D76C95'),
    ('Music',    '4BD8D571-6D19-48D3-BE97-422220080E43'),
    ('Documents','FDD39AD0-238F-46AF-ADB4-6C85480369C7'),
    ('Downloads','374DE290-123F-4565-9164-39C4925E467B'),
]


class GUID(ctypes.Structure):
    _fields_ = [('Data1', ctypes.c_ulong), ('Data2', ctypes.c_ushort),
                ('Data3', ctypes.c_ushort), ('Data4', ctypes.c_byte * 8)]


def guid_of(s):
    g = GUID()
    hr = ole32.CLSIDFromString(ctypes.c_wchar_p('{%s}' % s), ctypes.byref(g))
    if hr != 0:
        raise RuntimeError('CLSIDFromString 失败 %s' % s)
    return g


def resolve(text):
    pidl = ctypes.c_void_p()
    attr = wintypes.DWORD(0)
    hr = shell32.SHParseDisplayName(text, None, ctypes.byref(pidl), 0, ctypes.byref(attr))
    if hr != 0 or not pidl:
        return None, 'HRESULT=0x%08X' % (hr & 0xFFFFFFFF)
    try:
        buf = ctypes.create_unicode_buffer(1024)
        ok = shell32.SHGetPathFromIDListW(pidl, buf)
        return (buf.value if ok else '(不是文件系统路径)'), 'ok'
    finally:
        ole32.CoTaskMemFree(pidl)


print('=== SHGetKnownFolderPath（真路径） ===')
for name, g in FOLDERS:
    out = wintypes.LPWSTR()
    hr = shell32.SHGetKnownFolderPath(ctypes.byref(guid_of(g)), 0, None, ctypes.byref(out))
    p = out.value if hr == 0 and out else '失败 0x%08X' % (hr & 0xFFFFFFFF)
    print('  %-10s %s  ->  %s' % (name, g, p))
    if out:
        ole32.CoTaskMemFree(out)

print()
print('=== SHParseDisplayName：各种写法能不能解析 ===')
cands = ['shell:MyPictures', 'shell:Pictures', 'shell:MyVideos', 'shell:MyMusic',
         'shell:Personal', 'shell:Downloads', 'shell:Desktop', 'shell:MyComputerFolder',
         'shell:RecycleBinFolder', 'shell:NetworkPlacesFolder',
         '::{33E28130-4E1E-4676-835A-98395C3BC3BB}',
         'shell:::{33E28130-4E1E-4676-835A-98395C3BC3BB}']
for c in cands:
    path, why = resolve(c)
    print('  %-48s -> %-30s %s' % (c, path, '' if why == 'ok' else ('[' + why + ']')))
