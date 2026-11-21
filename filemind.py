"""File Mind engine: the local server the native Windows app talks to (it starts this with --no-window --port N).

In the portable build this is the engine executable. Data and models live next to it, so the folder can be moved
or deleted as a whole. From a source checkout:  python filemind.py --port 8765"""
import os
import socket
import subprocess
import sys
import time
from pathlib import Path

FROZEN = getattr(sys, "frozen", False)
HOME = Path(sys.executable).parent if FROZEN else Path(__file__).resolve().parent
BUNDLE = Path(getattr(sys, "_MEIPASS", HOME))

os.environ.setdefault("FILEMIND_DATA", str(HOME / "data"))
if sys.stdout is None or sys.stderr is None or (FROZEN and not sys.stdout.isatty()):   # windowed exe: log to a file
    Path(os.environ["FILEMIND_DATA"]).mkdir(parents=True, exist_ok=True)
    _log = Path(os.environ["FILEMIND_DATA"]) / "filemind.log"
    if _log.exists() and _log.stat().st_size > 5_000_000:             # keep the log from growing forever
        _log.replace(_log.with_suffix(".log.1"))
    sys.stdout = sys.stderr = open(Path(os.environ["FILEMIND_DATA"]) / "filemind.log", "a", encoding="utf-8", buffering=1)
    print(f"--- File Mind started {time.ctime()}")
os.environ.setdefault("FILEMIND_MODELS", str(HOME / "models"))
os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")
if (BUNDLE / "runtime.json").exists():                           # Windows build: PyTorch (CUDA or CPU, by the GPU found)
    os.environ["FILEMIND_RUNTIME_SPEC"] = str(BUNDLE / "runtime.json")    # is fetched on first run (app/runtime.py)
    os.environ.setdefault("FILEMIND_RUNTIME", str(HOME / "runtime"))
    from app import runtime as _runtime                           # stdlib only: safe before torch is importable
    _runtime.SITE.mkdir(parents=True, exist_ok=True)              # must exist now, or the import system skips it
    sys.path.append(str(_runtime.SITE))
    print(f"File Mind: PyTorch for {_runtime.ACCEL.upper()} ({_runtime.REASON})", flush=True)
if (BUNDLE / "ffmpeg").is_dir():                                  # ffmpeg + ffprobe shipped inside the app
    os.environ["PATH"] = str(BUNDLE / "ffmpeg") + os.pathsep + os.environ.get("PATH", "")

if os.name == "nt" and FROZEN:                                    # no console window flashing for each ffmpeg call
    _Popen = subprocess.Popen

    class _Quiet(_Popen):
        def __init__(self, *a, **kw):
            if not kw.get("creationflags"):
                kw["creationflags"] = subprocess.CREATE_NO_WINDOW
            super().__init__(*a, **kw)
    subprocess.Popen = _Quiet

PORT = int(sys.argv[sys.argv.index("--port") + 1]) if "--port" in sys.argv else 8765


def main():
    import uvicorn
    from app.main import app
    with socket.socket() as s:
        if s.connect_ex(("127.0.0.1", PORT)) == 0:
            print(f"Port {PORT} is already in use.", file=sys.stderr)
            sys.exit(1)
    print(f"File Mind engine: http://127.0.0.1:{PORT}", flush=True)
    uvicorn.run(app, host="127.0.0.1", port=PORT, log_level="warning")


if __name__ == "__main__":
    try:
        main()
    except Exception:
        import traceback
        traceback.print_exc()                                     # lands in data/filemind.log
        raise
