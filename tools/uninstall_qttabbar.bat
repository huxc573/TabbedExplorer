@echo off
setlocal
echo This will uninstall QTTabBar (it steals Win+E from TabbedExplorer).
echo Explorer will be restarted at the end (screen blinks once).
echo.
pause
echo [1/3] 1.5.31 Beta
start /wait msiexec /x {5CA99FAF-49FA-4716-8AA2-194914C527A9} /passive /norestart
echo [2/3] 1.5.32 Beta
start /wait msiexec /x {FF85AD2C-ED2C-4B53-86A2-DC5EC6814D24} /passive /norestart
echo [3/3] 1.5.31 bundle
if exist "C:\ProgramData\Package Cache\{ef148259-fa72-4ce0-9ff8-bc41f34fb3c3}\QTTabBar Setup 1.5.31.exe" start /wait "" "C:\ProgramData\Package Cache\{ef148259-fa72-4ce0-9ff8-bc41f34fb3c3}\QTTabBar Setup 1.5.31.exe" /uninstall /passive
echo.
echo restarting explorer...
taskkill /f /im explorer.exe >nul 2>&1
timeout /t 2 >nul
start explorer.exe
echo done
pause
