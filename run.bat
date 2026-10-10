@echo off
cd /d "%~dp0"
set HF_HUB_OFFLINE=1
set TRANSFORMERS_OFFLINE=1
set HF_HUB_DISABLE_SYMLINKS_WARNING=1
echo File Mind is starting on http://127.0.0.1:8765  (close this window to quit)
start "" msedge --app=http://127.0.0.1:8765 --window-size=1480,940
.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8765
