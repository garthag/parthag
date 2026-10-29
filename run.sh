#!/usr/bin/env bash
# StockAI - install (first run) and start on macOS / Linux
set -e
cd "$(dirname "$0")"
if [ ! -x .venv/bin/python ]; then
  python3 -m venv .venv
  .venv/bin/python -m pip install --upgrade pip
  .venv/bin/python -m pip install -r requirements.txt
fi
IP=$( (hostname -I 2>/dev/null || ipconfig getifaddr en0 2>/dev/null) | awk '{print $1}')
echo "On this computer:       http://localhost:8501"
[ -n "$IP" ] && echo "On your phone (Wi-Fi):  http://$IP:8501"
exec .venv/bin/python -m streamlit run app.py --server.address 0.0.0.0
