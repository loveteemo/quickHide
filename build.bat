@echo off
rem Build quickHide.exe -- multi-file project (src/*.cs); output: dist\quickHide-<version>.exe
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo ERROR: .NET Framework 4.x csc.exe not found
  exit /b 1
)
cd /d "%~dp0"
for /f "tokens=6 delims= " %%v in ('findstr /C:"public const string Version" src\Program.cs') do set VER=%%v
set VER=%VER:"=%
set VER=%VER:;=%
echo Version: %VER%
if not exist dist mkdir dist
dir /b /s src\*.cs > build_sources.tmp
"%CSC%" /nologo /optimize /target:winexe /platform:anycpu /win32manifest:app.manifest /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll /r:System.Web.Extensions.dll /res:donate_wechat.png,QuickHide.donate_wechat.png /res:donate_alipay.jpg,QuickHide.donate_alipay.jpg /out:dist\quickHide-%VER%.exe @build_sources.tmp
set EXITCODE=%ERRORLEVEL%
del build_sources.tmp 2>nul
if not "%EXITCODE%"=="0" (
  echo BUILD FAILED
  exit /b %EXITCODE%
)
echo BUILD OK: %~dp0dist\quickHide-%VER%.exe
