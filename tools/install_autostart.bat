@echo off
rem 开机自启：**常驻后台、无窗口**。
rem 一般不用手动跑这个脚本：程序里「设置 → 开机自启」就是同一件事（写同一个注册表值）。
rem --tray 表示起来只装托盘图标 + Win+E 钩子、不显窗口；按 Win+E 才出窗口。
rem --embed 现在是默认值（嵌入真 explorer 窗口那条路），写出来只为跟 --tray 一起看得明白；
rem 老路（AppContext + IExplorerBrowser）实测硬崩 0x80131506，要回去得显式加 --classic。
rem 想取消：uninstall_autostart.bat
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v TabbedExplorer /t REG_SZ /d "\"%~dp0..\TabbedExplorer.exe\" --embed --tray" /f
if errorlevel 1 (echo FAILED) else (echo OK - 已设为开机常驻后台，按 Win+E 打开)
pause
