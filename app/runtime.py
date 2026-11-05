"""The Windows build ships without PyTorch and fetches the right one on first run.

One download serves every PC: runtime.json (inside the build) lists two sets of wheels, the CUDA build of PyTorch
for a usable NVIDIA GPU (~2.8 GB) and the small CPU build (~120 MB) for everything else, including AMD and Intel
graphics. The launcher points FILEMIND_RUNTIME_SPEC at runtime.json and FILEMIND_RUNTIME at a folder outside the
app, and puts this module's SITE on sys.path before anything imports torch; fetch.py downloads and unpacks the
wheels there. Each accelerator gets its own folder, so switching back and forth never downloads twice.

Running from source has no spec and needs nothing from here (pip installed PyTorch already)."""
import ctypes
import hashlib
import json
import os
from pathlib import Path

_SPEC_PATH = os.environ.get("FILEMIND_RUNTIME_SPEC")
_SPEC = json.loads(Path(_SPEC_PATH).read_text(encoding="utf-8")) if _SPEC_PATH else {}


def detect_accelerator(need: dict | None = None) -> tuple[str, str]:
    """('cuda' | 'cpu', why). Asks nvcuda.dll, which every NVIDIA driver installs: no toolkit or nvidia-smi needed.
    FILEMIND_ACCEL=cpu|cuda (set by the native app's engine setting) overrides the check."""
    forced = (os.environ.get("FILEMIND_ACCEL") or "").strip().lower()
    if forced in ("cpu", "cuda"):
        return forced, "chosen in settings"
    need = need or {"min_driver": 12080, "min_compute": 7.0}
    if os.name != "nt":
        return "cpu", "not Windows"
    try:
        nv = ctypes.WinDLL("nvcuda.dll")
    except OSError:
        return "cpu", "no NVIDIA GPU found (AMD and Intel graphics run on the CPU)"
    try:
        if nv.cuInit(0) != 0:
            return "cpu", "the NVIDIA driver did not start"
        version, count = ctypes.c_int(), ctypes.c_int()
        nv.cuDriverGetVersion(ctypes.byref(version))
        nv.cuDeviceGetCount(ctypes.byref(count))
        best = 0.0
        for i in range(count.value):
            dev, major, minor = ctypes.c_int(), ctypes.c_int(), ctypes.c_int()
            if nv.cuDeviceGet(ctypes.byref(dev), i) != 0:
                continue
            nv.cuDeviceGetAttribute(ctypes.byref(major), 75, dev)   # compute capability major
            nv.cuDeviceGetAttribute(ctypes.byref(minor), 76, dev)   # ... minor
            best = max(best, major.value + minor.value / 10)
    except Exception as e:                                          # never let the check stop the app
        return "cpu", f"the GPU check failed ({e})"
    if count.value == 0:
        return "cpu", "the NVIDIA driver reports no GPU"
    if version.value < need["min_driver"]:
        v = version.value
        return "cpu", f"the NVIDIA driver is too old (CUDA {v // 1000}.{v % 1000 // 10}); update it to use the GPU"
    if best < need["min_compute"]:
        return "cpu", f"the NVIDIA GPU is too old for PyTorch (compute {best:.1f})"
    return "cuda", "NVIDIA GPU"


if _SPEC:
    ACCEL, REASON = detect_accelerator(_SPEC.get("cuda_requires"))
    WHEELS = _SPEC["accelerators"][ACCEL]
    DIR = Path(os.environ["FILEMIND_RUNTIME"]) / ACCEL
    SITE = DIR / "site-packages"
else:
    ACCEL, REASON, WHEELS, DIR, SITE = "", "", [], None, None
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
