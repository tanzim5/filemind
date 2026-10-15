"""File Mind desktop launcher: starts the local server and shows it in its own window.

Used as FileMind.exe in the portable build. Everything (data, model) lives next to the executable,
so the folder can be moved or deleted as a whole. Run from source with:  python filemind.py"""
import os
import secrets
import socket
import subprocess
import sys
import threading
import time
import urllib.request
from pathlib import Path

FROZEN = getattr(sys, "frozen", False)
if FROZEN and sys.platform == "darwin":                           # inside FileMind.app: keep user data outside the bundle
    HOME = Path.home() / "Library" / "Application Support" / "File Mind"
    HOME.mkdir(parents=True, exist_ok=True)
else:
    HOME = Path(sys.executable).parent if FROZEN else Path(__file__).resolve().parent
BUNDLE = Path(getattr(sys, "_MEIPASS", HOME))

os.environ.setdefault("FILEMIND_DATA", str(HOME / "data"))
if sys.stdout is None or sys.stderr is None or (FROZEN and not sys.stdout.isatty()):   # windowed app: log to a file
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
    port, serve, window_opened = PORT, True, False
    if not _free(PORT):
        if _ours(PORT):
            serve = False                                         # already running: just open another window
        else:
            with socket.socket() as s:
                s.bind(("127.0.0.1", 0)); port = s.getsockname()[1]
    token_path = Path(os.environ["FILEMIND_DATA"]) / f"desktop-session-{port}"
    if serve:
        import uvicorn
        from fastapi import Header, HTTPException
        if "--background" in sys.argv:
            os.environ["FILEMIND_LAZY_START"] = "1"
        from app.main import app
        server = uvicorn.Server(uvicorn.Config(app, host="127.0.0.1", port=port, log_level="warning"))
        token = secrets.token_urlsafe(32)
        token_path.parent.mkdir(parents=True, exist_ok=True)
        token_path.write_text(token, encoding="ascii")
        try:
            token_path.chmod(0o600)
        except OSError:
            pass

        def quit_background(x_filemind_desktop: str = Header(default="")):
            if not secrets.compare_digest(x_filemind_desktop, token):
                raise HTTPException(status_code=403)
            threading.Timer(0.2, lambda: setattr(server, "should_exit", True)).start()
            return {"ok": True}

        app.add_api_route("/api/desktop/quit", quit_background, methods=["POST"], include_in_schema=False)
        app.router.routes.insert(-1, app.router.routes.pop())  # ahead of the catch-all static mount
        threading.Thread(target=server.run, daemon=True).start()
        for _ in range(300):
            if _ours(port):
                break
            time.sleep(0.1)
    url = f"http://127.0.0.1:{port}"
    if "--browser" not in sys.argv and "--no-window" not in sys.argv and "--background" not in sys.argv:
        try:
            _window(url)
            window_opened = True
            if not serve:
                from app.autostart import preferences
                if not preferences()["background"]:
                    try:
                        request = urllib.request.Request(url + "/api/desktop/quit", data=b"",
                            headers={"X-FileMind-Desktop": token_path.read_text(encoding="ascii")}, method="POST")
                        urllib.request.urlopen(request, timeout=2).close()
                    except (OSError, ValueError) as e:
                        print(f"Could not stop background server: {e}")
                os._exit(0)                                       # another process owns the local server
            from app.autostart import preferences
            if not preferences()["background"]:
                os._exit(0)                                       # default: closing the window stops this server
        except Exception as e:                                    # no pywebview / no GUI toolkit (common on Linux)
            print(f"No app window available ({type(e).__name__}: {e}); opening your browser instead.")
    if not window_opened and "--no-window" not in sys.argv and "--background" not in sys.argv:
        import webbrowser
        webbrowser.open(url)
    if serve:
        print(f"File Mind is running at {url}  (press Ctrl+C here to quit)")
        try:
            while not server.should_exit:
                time.sleep(0.5)
        except KeyboardInterrupt:
            pass
        token_path.unlink(missing_ok=True)
    os._exit(0)


def _window(url):
    import webview

    class Api:                                                    # window.pywebview.api.* in the page
        def drag_capabilities(self):
            return {"native_files": os.name == "nt"}

        def drag_files(self, file_ids):
            from app.desktop_drag import drag_files
            return drag_files(win, file_ids)

        def copy_frame(self, png_data_url):
            from app.frame_clipboard import copy_png_frame
            return copy_png_frame(win, png_data_url)

        def pick_folder(self):
            r = win.create_file_dialog(webview.FileDialog.FOLDER)
            return r[0] if r else None

        def open_url(self, u):
            if str(u).startswith("https://"):
                import webbrowser
                webbrowser.open(u)

        def desktop_preferences(self):
            from app.autostart import preferences
            return preferences()

        def set_desktop_preferences(self, background=None, startup=None):
            from app.autostart import update
            return update(background=background, startup=startup)

    png = BUNDLE / "app" / "static" / "icon.png"
    ico = BUNDLE / "app" / "static" / "favicon.ico"
    if os.name == "nt":                                           # own taskbar group, not "python.exe"
        try:
            import ctypes
            ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("tanzim.FileMind")
        except Exception:
            pass
    win = webview.create_window("File Mind", url, width=1480, height=940, min_size=(900, 600),
                                background_color="#0b0b0d", js_api=Api(), minimized="--hidden" in sys.argv)
    win.events.shown += lambda: _set_icon(win, png, ico)
    webview.start(private_mode=False, storage_path=str(Path(os.environ["FILEMIND_DATA"]) / "webview"),
                  icon=str(png) if sys.platform.startswith("linux") else None)


def _set_icon(win, png, ico):
    """Window/taskbar/Dock icon. Without this a source checkout shows Python's icon (the frozen exe has its own)."""
    try:
        if os.name == "nt":
            import ctypes
            u = ctypes.windll.user32
            u.LoadImageW.restype = ctypes.c_void_p
            u.SendMessageW.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_void_p, ctypes.c_void_p]
            hwnd = int(win.native.Handle.ToInt64())
            for which, size in ((1, 32), (0, 16)):                # ICON_BIG, ICON_SMALL
                h = u.LoadImageW(None, str(ico), 1, size, size, 0x10)   # IMAGE_ICON, LR_LOADFROMFILE
                u.SendMessageW(hwnd, 0x80, which, h)              # WM_SETICON
        elif sys.platform == "darwin":
            from AppKit import NSApplication, NSImage
            from PyObjCTools import AppHelper
            AppHelper.callAfter(lambda: NSApplication.sharedApplication().setApplicationIconImage_(
                NSImage.alloc().initByReferencingFile_(str(png))))
    except Exception as e:
        print(f"Could not set the window icon: {type(e).__name__}: {e}")


if __name__ == "__main__":
    try:
        main()
    except Exception:
        import traceback
        traceback.print_exc()                                     # lands in data/filemind.log
        raise
