@echo off
rem Compile-only helper, launched by Task Scheduler (no taskkill / no pause / no start).
rem Builds to TabbedExplorer.new.exe first, then swaps it in, so a failed build
rem never destroys an existing exe.
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto nocsc

if exist "TabbedExplorer.new.exe" del /q "TabbedExplorer.new.exe"

"%CSC%" /nologo /noconfig /optimize+ /target:winexe /codepage:65001 /out:TabbedExplorer.new.exe /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System\v4.0_4.0.0.0__b77a5c561934e089\System.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Core\v4.0_4.0.0.0__b77a5c561934e089\System.Core.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Windows.Forms\v4.0_4.0.0.0__b77a5c561934e089\System.Windows.Forms.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Drawing\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Drawing.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.IO.Compression\v4.0_4.0.0.0__b77a5c561934e089\System.IO.Compression.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.IO.Compression.FileSystem\v4.0_4.0.0.0__b77a5c561934e089\System.IO.Compression.FileSystem.dll" src\*.cs > build.log 2>&1
if errorlevel 1 goto failed
if not exist "TabbedExplorer.new.exe" goto failed

move /y "TabbedExplorer.new.exe" "TabbedExplorer.exe" >> build.log 2>&1
if errorlevel 1 goto locked

echo BUILD_OK>> build.log
exit /b 0

:locked
echo BUILD_FAILED_EXE_LOCKED>> build.log
exit /b 2

:failed
echo BUILD_FAILED>> build.log
exit /b 1

:nocsc
echo BUILD_FAILED_NO_CSC> build.log
exit /b 1
