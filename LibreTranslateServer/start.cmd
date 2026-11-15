@echo off
rem Double-click to start the LibreTranslate server (see start.ps1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1" %*
pause
