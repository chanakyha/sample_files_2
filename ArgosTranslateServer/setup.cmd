@echo off
rem Double-click to install Argos Translate and download the language models (see setup.ps1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
pause
