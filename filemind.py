"""File Mind desktop launcher: starts the local server and shows it in its own window.

Used as FileMind.exe in the portable build. Everything (data, model) lives next to the executable,
so the folder can be moved or deleted as a whole. Run from source with:  python filemind.py"""
import os
import socket
import subprocess
import sys
import threading
import time
import urllib.request
from pathlib import Path

FROZEN = getattr(sys, "frozen", False)
HOME = Path(sys.executable).parent if FROZEN else Path(__file__).resolve().parent
BUNDLE = Path(getattr(sys, "_MEIPASS", HOME))

os.environ.setdefault("FILEMIND_DATA", str(HOME / "data"))
if sys.stdout is None or sys.stderr is None:                     # windowed exe: no console, so log to a file
    Path(os.environ["FILEMIND_DATA"]).mkdir(parents=True, exist_ok=True)
    sys.stdout = sys.stderr = open(Path(os.environ["FILEMIND_DATA"]) / "filemind.log", "a", encoding="utf-8", buffering=1)
    print(f"--- File Mind started {time.ctime()}")
os.environ.setdefault("FILEMIND_MODELS", str(HOME / "models"))
os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")
if (BUNDLE / "runtime.json").exists():                           # NVIDIA build: PyTorch+CUDA is fetched on first
    os.environ["FILEMIND_RUNTIME_SPEC"] = str(BUNDLE / "runtime.json")    # run into runtime/ (see app/runtime.py)
    os.environ.setdefault("FILEMIND_RUNTIME", str(HOME / "runtime"))
    _site = Path(os.environ["FILEMIND_RUNTIME"]) / "site-packages"
    _site.mkdir(parents=True, exist_ok=True)                     # must exist now, or the import system skips it
    sys.path.append(str(_site))
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


def _ours(port):
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/model", timeout=1) as r:
            return r.status == 200
    except Exception:
        return False


def _free(port):
    with socket.socket() as s:
        return s.connect_ex(("127.0.0.1", port)) != 0


def main():
    port, serve = PORT, True
    if not _free(PORT):
        if _ours(PORT):
            serve = False                                         # already running: just open another window
        else:
            with socket.socket() as s:
                s.bind(("127.0.0.1", 0)); port = s.getsockname()[1]
    if serve:
        import uvicorn
        from app.main import app
        server = uvicorn.Server(uvicorn.Config(app, host="127.0.0.1", port=port, log_level="warning"))
        threading.Thread(target=server.run, daemon=True).start()
        for _ in range(300):
            if _ours(port):
                break
            time.sleep(0.1)
    url = f"http://127.0.0.1:{port}"
    if "--browser" not in sys.argv and "--no-window" not in sys.argv:
        try:
            _window(url)
            os._exit(0)                                           # window closed: stop the server too
        except Exception as e:                                    # no pywebview / no GUI toolkit (common on Linux)
            print(f"No app window available ({type(e).__name__}: {e}); opening your browser instead.")
    if "--no-window" not in sys.argv:                             # --no-window: just serve (testing, remote use)
        import webbrowser
        webbrowser.open(url)
    if serve:
        print(f"File Mind is running at {url}  (press Ctrl+C here to quit)")
        try:
            while True:
                time.sleep(3600)
        except KeyboardInterrupt:
            pass
    os._exit(0)


def _window(url):
    import webview

    class Api:                                                    # window.pywebview.api.* in the page
        def pick_folder(self):
            r = win.create_file_dialog(webview.FileDialog.FOLDER)
            return r[0] if r else None

    win = webview.create_window("File Mind", url, width=1480, height=940, min_size=(900, 600),
                                background_color="#f5f6f9", js_api=Api())
    webview.start(private_mode=False, storage_path=str(Path(os.environ["FILEMIND_DATA"]) / "webview"))


if __name__ == "__main__":
    try:
        main()
    except Exception:
        import traceback
        traceback.print_exc()                                     # lands in data/filemind.log
        raise
