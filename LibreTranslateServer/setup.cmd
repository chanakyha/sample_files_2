@echo off
rem Double-click to install LibreTranslate (see setup.ps1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
pause
