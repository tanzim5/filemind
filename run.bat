@echo off
rem File Mind on Windows from source. The first run sets everything up in .venv; later runs just start it.
rem (Most people should use the portable FileMind.exe from the Releases page instead.)
cd /d "%~dp0"
if not exist .venv\.ready (
  echo First run: setting up File Mind. This downloads a few GB of Python packages and takes a few minutes.
  python -m venv .venv || (echo File Mind needs Python 3.10 or newer: https://www.python.org/downloads/ & pause & exit /b 1)
  .venv\Scripts\python.exe -m pip install --upgrade pip
  .venv\Scripts\python.exe -m pip install -r requirements.txt || (pause & exit /b 1)
  echo ok> .venv\.ready
)
where ffmpeg >nul 2>nul || echo Note: ffmpeg isn't installed, so videos, sounds and voice search won't work. Install it with:  winget install Gyan.FFmpeg
.venv\Scripts\python.exe filemind.py %*
