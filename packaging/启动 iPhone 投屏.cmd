@echo off
setlocal
cd /d "%~dp0"
if not exist "iPhone-PC.exe" (
  echo 找不到 iPhone-PC.exe。请先完整解压压缩包，再从解压后的文件夹启动。
  pause
  exit /b 1
)
start "" "%~dp0iPhone-PC.exe"
