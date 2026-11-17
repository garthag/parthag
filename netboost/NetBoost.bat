@echo off
title NetBoost - Wi-Fi and Internet Optimizer
cd /d "%~dp0"
where python >nul 2>nul
if errorlevel 1 (
  echo Python is not installed. Get it from https://www.python.org/downloads/
  echo During setup tick "Add python.exe to PATH", then run this file again.
  pause
  exit /b
)
:menu
cls
echo ==============================================
echo   NetBoost - Wi-Fi and Internet Optimizer
echo ==============================================
echo   1. Full analysis (hardware, ping, DNS, speed test)
echo   2. Analyze AND apply optimizations (needs Run as administrator)
echo   3. Live connection monitor (ping / stability)
echo   4. Fast multi-connection download of a file
echo   5. Undo changes (restore DNS and power settings)
echo   6. Exit
echo.
set /p choice=Choose 1-6: 
if "%choice%"=="1" python netboost.py analyze
if "%choice%"=="2" python netboost.py analyze --apply
if "%choice%"=="3" python netboost.py monitor
if "%choice%"=="4" (
  set /p url=Paste the download link: 
  call python netboost.py download "%%url%%"
)
if "%choice%"=="5" python netboost.py --restore
if "%choice%"=="6" exit /b
echo.
pause
goto menu
