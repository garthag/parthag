@echo off
REM StockAI - start the app and open it in its own window
setlocal
cd /d "%~dp0"
if not exist .venv\Scripts\python.exe (
    echo StockAI is not installed yet. Running installer...
    call install_windows.bat
)

REM Show the address phones on the same Wi-Fi can use
set IP=YOUR-PC-IP
for /f "tokens=2 delims=:" %%a in ('ipconfig ^| findstr /c:"IPv4"') do (
    set IP=%%a
    goto :gotip
)
:gotip
set IP=%IP: =%
echo.
echo  StockAI is starting...
echo  On this PC:            http://localhost:8501
echo  On your phone (Wi-Fi): http://%IP%:8501
echo  Keep this window open while you use the app. Close it to stop StockAI.
echo.

start "" /b cmd /c "ping -n 7 127.0.0.1 >nul & (start msedge --app=http://localhost:8501 || start http://localhost:8501)"
.venv\Scripts\python.exe -m streamlit run app.py --server.address 0.0.0.0
