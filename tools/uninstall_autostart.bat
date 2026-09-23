@echo off
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v TabbedExplorer /f
echo autostart removed
pause
