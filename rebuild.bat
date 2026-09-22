@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto nocsc

echo Stopping any running TabbedExplorer...
taskkill /f /im TabbedExplorer.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul

echo Building TabbedExplorer...
echo.

"%CSC%" /nologo /noconfig /optimize+ /target:winexe /codepage:65001 /out:TabbedExplorer.exe /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System\v4.0_4.0.0.0__b77a5c561934e089\System.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Core\v4.0_4.0.0.0__b77a5c561934e089\System.Core.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Windows.Forms\v4.0_4.0.0.0__b77a5c561934e089\System.Windows.Forms.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Drawing\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Drawing.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.IO.Compression\v4.0_4.0.0.0__b77a5c561934e089\System.IO.Compression.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.IO.Compression.FileSystem\v4.0_4.0.0.0__b77a5c561934e089\System.IO.Compression.FileSystem.dll" src\*.cs > build.log 2>&1
set "RC=%ERRORLEVEL%"

type build.log
echo.

if not "%RC%"=="0" goto failed
if not exist "TabbedExplorer.exe" goto failed

echo BUILD OK
echo Starting TabbedExplorer... (tray icon, then press Win+E)
start "" "%~dp0TabbedExplorer.exe"
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
