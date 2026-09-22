@echo off
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto nocsc
echo Building TabbedExplorer...
echo.
"%CSC%" /nologo /noconfig /optimize+ /target:winexe /win32icon:app.ico /codepage:65001 /out:TabbedExplorer.exe /r:%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System\v4.0_4.0.0.0__b77a5c561934e089\System.dll /r:%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Core\v4.0_4.0.0.0__b77a5c561934e089\System.Core.dll /r:%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Windows.Forms\v4.0_4.0.0.0__b77a5c561934e089\System.Windows.Forms.dll /r:%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Drawing\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Drawing.dll /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.IO.Compression\v4.0_4.0.0.0__b77a5c561934e089\System.IO.Compression.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.IO.Compression.FileSystem\v4.0_4.0.0.0__b77a5c561934e089\System.IO.Compression.FileSystem.dll" src\*.cs > build.log 2>&1
type build.log
if errorlevel 1 goto failed
echo.
echo BUILD OK: %CD%\TabbedExplorer.exe
echo Now run run.bat
pause
exit /b 0

:nocsc
echo ERROR: csc.exe not found
pause
exit /b 1

:failed
echo.
echo BUILD FAILED - copy the error lines above and send them back
pause
exit /b 1
