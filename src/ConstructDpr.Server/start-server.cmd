@echo off
rem CONSTRUCT DPR server on a site-office PC. Phones and PCs on the same network connect to http://<this-PC-name>:8080
rem Data: the "data" folder next to this file (SQLite). Back it up regularly.
cd /d "%~dp0"
echo Starting CONSTRUCT DPR server on port 8080 ... (close this window to stop it)
ConstructDpr.Server.exe --urls http://0.0.0.0:8080
pause
