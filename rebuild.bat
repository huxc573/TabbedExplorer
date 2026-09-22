@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto nocsc

rem 先走**优雅退出**：它会先把标签记忆落盘、再把各标签里嵌进来的 explorer 收干净。
rem 直接 taskkill /f 虽然也能腾出 exe，但那些跨进程嵌进来的 explorer 会漏成孤儿窗口。
echo Stopping any running TabbedExplorer...
if exist "%~dp0TabbedExplorer.exe" "%~dp0TabbedExplorer.exe" --quit
ping -n 3 127.0.0.1 >nul
tasklist /fi "imagename eq TabbedExplorer.exe" 2>nul | find /i "TabbedExplorer.exe" >nul
if not errorlevel 1 (
  echo   优雅退出没收干净，强杀
  taskkill /f /im TabbedExplorer.exe >nul 2>&1
  ping -n 2 127.0.0.1 >nul
)

echo Building TabbedExplorer...
echo.

"%CSC%" /nologo /noconfig /optimize+ /target:winexe /win32icon:app.ico /codepage:65001 /out:TabbedExplorer.exe /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System\v4.0_4.0.0.0__b77a5c561934e089\System.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Core\v4.0_4.0.0.0__b77a5c561934e089\System.Core.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Windows.Forms\v4.0_4.0.0.0__b77a5c561934e089\System.Windows.Forms.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Drawing\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Drawing.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.IO.Compression\v4.0_4.0.0.0__b77a5c561934e089\System.IO.Compression.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.IO.Compression.FileSystem\v4.0_4.0.0.0__b77a5c561934e089\System.IO.Compression.FileSystem.dll" src\*.cs > build.log 2>&1
set "RC=%ERRORLEVEL%"

type build.log
echo.

if not "%RC%"=="0" goto failed
if not exist "TabbedExplorer.exe" goto failed

echo BUILD OK
rem --open：编译完直接把窗口摆出来（不带就只驻托盘、什么都不显示，很容易误会「没启动成功」）。
rem --embed 必须带（不带会走老路 AppContext + IExplorerBrowser，实测硬崩 0x80131506）。
echo Starting TabbedExplorer... (window opens now, tray icon too)
start "" "%~dp0TabbedExplorer.exe" --embed --open
ping -n 3 127.0.0.1 >nul
exit /b 0

:nocsc
echo ERROR: csc.exe not found
pause
exit /b 1

:failed
echo BUILD FAILED - copy the error lines above and send them back
pause
exit /b 1
