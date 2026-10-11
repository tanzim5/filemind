#!/usr/bin/env sh
# File Mind on macOS and Linux. The first run sets everything up in .venv; later runs just start it.
#   ./run.sh             app window (falls back to your browser)
#   ./run.sh --browser   always use your browser
set -e
cd "$(dirname "$0")"

if [ "$(uname -s)" = Darwin ]; then
  [ "$(uname -m)" = arm64 ] || { echo "File Mind on a Mac needs Apple Silicon (M1 or newer): the AI libraries it uses have no Intel Mac builds."; exit 1; }
  [ "$(sw_vers -productVersion | cut -d. -f1)" -ge 14 ] || { echo "File Mind on a Mac needs macOS 14 (Sonoma) or newer."; exit 1; }
fi

# Prefer a Python the AI libraries have wheels for: 3.12 is the tested one, then 3.13, 3.11, 3.10.
PY="${PYTHON:-}"
if [ -z "$PY" ]; then
  for c in python3.12 python3.13 python3.11 python3.10 python3; do
    command -v "$c" >/dev/null 2>&1 && { PY="$c"; break; }
  done
fi
PY="${PY:-python3}"

if [ ! -f .venv/.ready ]; then
  command -v "$PY" >/dev/null 2>&1 || { echo "File Mind needs Python 3.10 or newer: https://www.python.org/downloads/"; exit 1; }
  "$PY" -c 'import sys; sys.exit(sys.version_info < (3, 10))' || { echo "File Mind needs Python 3.10 or newer (found $("$PY" --version 2>&1))."; exit 1; }
  case "$("$PY" -c 'import sys; print(sys.version_info[1])')" in 14|15|16) echo "Note: Python 3.14+ may lack wheels for some AI libraries; if setup fails, install Python 3.12 and run: PYTHON=python3.12 ./run.sh";; esac
  echo "First run: setting up File Mind. This downloads a few GB of Python packages and takes a few minutes."
  "$PY" -m venv .venv
  .venv/bin/python -m pip install --upgrade pip
  if [ "$(uname -s)" = Linux ] && ! command -v nvidia-smi >/dev/null 2>&1; then
    # No NVIDIA GPU: the CPU-only PyTorch is ~3 GB smaller and just as fast here.
    .venv/bin/python -m pip install torch torchvision --index-url https://download.pytorch.org/whl/cpu
  fi
  .venv/bin/python -m pip install -r requirements.txt
  touch .venv/.ready
fi

if ! command -v ffmpeg >/dev/null 2>&1 || ! command -v ffprobe >/dev/null 2>&1; then
  echo "Note: ffmpeg isn't installed, so videos, sounds and voice search won't work (photos will)."
  echo "  macOS: brew install ffmpeg    Debian/Ubuntu: sudo apt install ffmpeg    Fedora: sudo dnf install ffmpeg"
fi
exec .venv/bin/python filemind.py "$@"
