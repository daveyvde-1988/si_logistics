@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
echo.
echo The window will stay open. If you see an error, send me a screenshot of the whole window.
pause
