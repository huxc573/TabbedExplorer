@echo off
rem 开机自启：**常驻后台、无窗口**。
rem --embed 必须带（不带会走老路 AppContext + IExplorerBrowser，实测硬崩 0x80131506）。
rem --tray 表示起来只装托盘图标 + Win+E 钩子、不显窗口；按 Win+E 才出窗口。
rem 想取消：uninstall_autostart.bat
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v TabbedExplorer /t REG_SZ /d "\"%~dp0TabbedExplorer.exe\" --embed --tray" /f
if errorlevel 1 (echo FAILED) else (echo OK - 已设为开机常驻后台，按 Win+E 打开)
pause
