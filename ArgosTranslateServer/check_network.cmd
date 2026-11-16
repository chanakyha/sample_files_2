@echo off
rem Checks whether this network can download what the server needs (no installation needed).
rem Options: --proxy http://proxy.company.com:8080
where py >nul 2>nul
if %errorlevel%==0 (py -3 "%~dp0check_network.py" %*) else (python "%~dp0check_network.py" %*)
pause
