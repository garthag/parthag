@echo off
REM StockAI - one-time installer for Windows 10/11
setlocal
cd /d "%~dp0"
echo === StockAI installer ===

where python >nul 2>nul
if errorlevel 1 (
    echo Python not found. Installing Python 3.12 with winget...
    winget install -e --id Python.Python.3.12 --accept-package-agreements --accept-source-agreements
    if errorlevel 1 (
        echo Could not install Python automatically.
        echo Download it from https://www.python.org/downloads/ and tick "Add python.exe to PATH".
        pause & exit /b 1
    )
    echo Python installed. Close this window and run install_windows.bat again.
    pause & exit /b 0
)

if not exist .venv (
    echo Creating virtual environment...
    python -m venv .venv || (echo Failed to create venv & pause & exit /b 1)
)
echo Installing packages (first time takes a few minutes)...
call .venv\Scripts\python.exe -m pip install --upgrade pip
call .venv\Scripts\python.exe -m pip install -r requirements.txt || (echo Package install failed & pause & exit /b 1)

echo Creating desktop shortcut...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop')+'\StockAI.lnk');" ^
  "$s.TargetPath='%~dp0run_windows.bat'; $s.WorkingDirectory='%~dp0'; $s.IconLocation='shell32.dll,167'; $s.Save()"

echo.
echo Done! Double-click "StockAI" on your desktop (or run_windows.bat) to start the app.
pause
