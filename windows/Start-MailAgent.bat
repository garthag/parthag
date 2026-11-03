@echo off
rem Run the desktop app straight from the source code (needs Python 3.10+ from python.org).
cd /d "%~dp0\.."
py -m pip install -q -r requirements.txt -r windows\requirements-windows.txt || goto :err
start "" pyw -m mail_agent desktop %*
exit /b 0
:err
echo Install failed. Is Python installed? Get it from https://www.python.org/downloads/ (tick "Add to PATH").
pause
