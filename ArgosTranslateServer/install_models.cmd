@echo off
rem Downloads the language models from config.json (see install_models.py)
"%~dp0.venv\Scripts\python.exe" -u "%~dp0install_models.py" %*
pause
