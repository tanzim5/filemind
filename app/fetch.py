"""First run: download what File Mind needs, once — the model from Hugging Face, and in the NVIDIA build
also PyTorch with CUDA from pytorch.org (see runtime.py).

Each file is written as *.part and renamed only when complete, so a cut connection never leaves
a half-written file behind; running again resumes from the bytes already on disk."""
import hashlib
import importlib
import shutil
import threading
import zipfile
from pathlib import Path

import requests

from . import embedder, runtime

BASE = f"https://huggingface.co/{embedder.REPO}/resolve/main/"
API = f"https://huggingface.co/api/models/{embedder.REPO}?blobs=true"
SKIP = {".gitattributes", "README.md"}

state = {"running": False, "done": 0, "total": 0, "file": "", "stage": "", "error": None}
_lock = threading.Lock()


def status():
    return {"ready": embedder.ready(), "gpu": bool(runtime.WHEELS), "device": embedder.DEVICE, **state}


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


def _download(url, out: Path, size: int):
    """Fetch url to out, resuming a partial *.part file; adds progress to state['done']."""
    out.parent.mkdir(parents=True, exist_ok=True)
    if out.exists() and (not size or out.stat().st_size == size):
        state["done"] += size
        return
    part = out.with_name(out.name + ".part")
    have = part.stat().st_size if part.exists() else 0
    if size and have >= size:                                          # finished but never renamed
        part.replace(out)
        state["done"] += size
        return
    with _get(url, {"Range": f"bytes={have}-"} if have else None) as r:
        if have and r.status_code != 206:                              # server ignored the resume: start over
            have = 0
        state["done"] += have
        with open(part, "ab" if have else "wb") as f:
            for chunk in r.iter_content(1 << 20):
                f.write(chunk)
                state["done"] += len(chunk)
    part.replace(out)


def _sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(1 << 22):
            h.update(chunk)
    return h.hexdigest()


def _run(on_done):
    try:
        wheels = [] if runtime.ready() else runtime.WHEELS
        files = [] if embedder.model_ready() else \
            [(s["rfilename"], s.get("size") or 0) for s in _get(API).json()["siblings"] if s["rfilename"] not in SKIP]
        state.update(total=sum(w["size"] for w in wheels) + sum(n for _, n in files), done=0)

        if wheels:                                                     # GPU support first: the biggest part
            downloads = runtime.DIR / "downloads"
            for w in wheels:
                out = downloads / w["file"]
                state.update(stage="download", file=w["file"])
                _download(w["url"], out, w["size"])
                state["stage"] = "verify"
                if _sha256(out) != w["sha256"]:
                    out.unlink()
                    raise RuntimeError(f"{w['file']} was damaged in transit; it will be downloaded again")
            state["stage"] = "unpack"
            shutil.rmtree(runtime.SITE, ignore_errors=True)
            for w in wheels:
                state["file"] = w["file"]
                with zipfile.ZipFile(downloads / w["file"]) as z:
                    z.extractall(runtime.SITE)
            runtime.mark_ready()
            shutil.rmtree(downloads, ignore_errors=True)
            importlib.invalidate_caches()                              # let this process import the new packages

        state["stage"] = "download"
        for name, size in sorted(files, key=lambda f: f[1]):           # small files first, the big one last
            state["file"] = name
            _download(BASE + name, embedder._LOCAL / name, size)
        state.update(file="", stage="")
        if on_done:
            on_done()
    except Exception as e:                                             # shown in the app; "Try again" resumes
        state["error"] = f"{type(e).__name__}: {e}"
    finally:
        state["running"] = False
