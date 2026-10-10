#!/usr/bin/env sh
# File Mind on macOS and Linux. The first run sets everything up in .venv; later runs just start it.
#   ./run.sh             app window (falls back to your browser)
#   ./run.sh --browser   always use your browser
set -e
cd "$(dirname "$0")"
PY="${PYTHON:-python3}"

if [ ! -f .venv/.ready ]; then
  command -v "$PY" >/dev/null 2>&1 || { echo "File Mind needs Python 3.10 or newer: https://www.python.org/downloads/"; exit 1; }
  "$PY" -c 'import sys; sys.exit(sys.version_info < (3, 10))' || { echo "File Mind needs Python 3.10 or newer (found $("$PY" --version 2>&1))."; exit 1; }
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
