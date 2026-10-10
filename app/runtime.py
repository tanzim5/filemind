"""NVIDIA edition: PyTorch with CUDA (~2.8 GB) is too big to ship inside the app, so it is fetched on first run.

The NVIDIA build carries runtime.json listing the exact wheels and their SHA-256. The launcher points
FILEMIND_RUNTIME_SPEC at it, FILEMIND_RUNTIME at a folder beside the exe, and puts that folder's
site-packages on sys.path. fetch.py downloads and unpacks the wheels there. Every other way of running
File Mind (the CPU build, running from source) has no spec and needs nothing from here."""
import hashlib
import json
import os
from pathlib import Path

_SPEC = os.environ.get("FILEMIND_RUNTIME_SPEC")
WHEELS = json.loads(Path(_SPEC).read_text(encoding="utf-8"))["wheels"] if _SPEC else []
DIR = Path(os.environ["FILEMIND_RUNTIME"]) if WHEELS else None
SITE = DIR / "site-packages" if DIR else None
_KEY = hashlib.sha256(json.dumps(WHEELS, sort_keys=True).encode()).hexdigest()[:16]   # changes if the wheels do


def ready() -> bool:
    if not WHEELS:
        return True
    try:
        return (DIR / ".ready").read_text().strip() == _KEY
    except OSError:
        return False


def mark_ready():
    (DIR / ".ready").write_text(_KEY)
