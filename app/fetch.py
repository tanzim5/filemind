"""First run: download the model once from Hugging Face into the models folder.

Each file is written as *.part and renamed only when complete, so a cut connection never leaves
a half-written model behind; running again resumes from the bytes already on disk."""
import threading
from pathlib import Path

import requests

from . import embedder

BASE = f"https://huggingface.co/{embedder.REPO}/resolve/main/"
API = f"https://huggingface.co/api/models/{embedder.REPO}?blobs=true"
SKIP = {".gitattributes", "README.md"}

state = {"running": False, "done": 0, "total": 0, "file": "", "error": None}
_lock = threading.Lock()


def status():
    return {"ready": embedder.ready(), **state}


def start(on_done=None):
    with _lock:
        if state["running"] or embedder.ready():
            return
        state.update(running=True, error=None)
    threading.Thread(target=_run, args=(on_done,), daemon=True).start()


_http = requests.Session()                                              # one kept-alive connection: on slow networks
_http.headers["User-Agent"] = "FileMind"                               # each new handshake can cost seconds


def _get(url, headers=None):
    r = _http.get(url, headers=headers, stream=True, timeout=60)
    r.raise_for_status()
    return r


def _run(on_done):
    dest: Path = embedder._LOCAL
    try:
        files = [(s["rfilename"], s.get("size") or 0) for s in _get(API).json()["siblings"] if s["rfilename"] not in SKIP]
        state.update(total=sum(n for _, n in files), done=0)
        for name, size in sorted(files, key=lambda f: f[1]):          # small files first, the big one last
            out = dest / name
            out.parent.mkdir(parents=True, exist_ok=True)
            state["file"] = name
            if out.exists() and (not size or out.stat().st_size == size):
                state["done"] += size
                continue
            part = out.with_name(out.name + ".part")
            have = part.stat().st_size if part.exists() else 0
            if size and have >= size:                                  # finished but never renamed
                part.replace(out)
                state["done"] += size
                continue
            with _get(BASE + name, {"Range": f"bytes={have}-"} if have else None) as r:
                if have and r.status_code != 206:                           # server ignored the resume: start over
                    have = 0
                state["done"] += have
                with open(part, "ab" if have else "wb") as f:
                    for chunk in r.iter_content(1 << 20):
                        f.write(chunk)
                        state["done"] += len(chunk)
            part.replace(out)
        state["file"] = ""
        if on_done:
            on_done()
    except Exception as e:                                             # shown in the app; "Try again" resumes
        state["error"] = f"{type(e).__name__}: {e}"
    finally:
        state["running"] = False
