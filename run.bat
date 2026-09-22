@echo off
cd /d "%~dp0"
if not exist "TabbedExplorer.exe" goto nobuild
start "" "TabbedExplorer.exe"
exit /b 0

:nobuild
echo TabbedExplorer.exe not found - run build.bat first
pause
exit /b 1
