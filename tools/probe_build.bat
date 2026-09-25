@echo off
rem Compile-only helper for the standalone probes under probe\ (launched by Task Scheduler).
rem csc 在本机命令通道是黑名单，只能走计划任务（见 ENV 记事）。
rem 产物与日志都落在 probe\ 下，不会碰到主 exe。
setlocal
cd /d "%~dp0.."
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto nocsc

if exist "probe\ieb_probe.exe" del /q "probe\ieb_probe.exe"

"%CSC%" /nologo /noconfig /optimize+ /target:winexe /codepage:65001 /out:probe\ieb_probe.exe /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System\v4.0_4.0.0.0__b77a5c561934e089\System.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Core\v4.0_4.0.0.0__b77a5c561934e089\System.Core.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Windows.Forms\v4.0_4.0.0.0__b77a5c561934e089\System.Windows.Forms.dll" /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Drawing\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Drawing.dll" probe\ieb_probe.cs > probe\ieb_probe.build.log 2>&1
if errorlevel 1 goto failed
if not exist "probe\ieb_probe.exe" goto failed

echo BUILD_OK>> probe\ieb_probe.build.log
exit /b 0

:failed
echo BUILD_FAILED>> probe\ieb_probe.build.log
exit /b 1

:nocsc
echo BUILD_FAILED_NO_CSC> probe\ieb_probe.build.log
exit /b 1
