@echo off
chcp 936 >nul
title quickHide 启动/升级
:: 直接启动 dist 里最新的 quickHide-<版本>.exe：新实例会自动结束旧实例
for /f "delims=" %%f in ('dir /b /o-d "%~dp0dist\quickHide-*.exe" 2^>nul') do (
  start "" "%~dp0dist\%%f"
  echo 已启动: dist\%%f
  timeout /t 2 >nul
  exit /b
)
echo dist 目录里没有找到 quickHide-*.exe，请先运行 build.bat
timeout /t 3 >nul
