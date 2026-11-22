"""Numbered frame sequences (render_0001.png ... render_1200.png) kept as ONE item in the library instead of thousands.

The index holds a single row for a sequence: kind 'video', `path` = its first frame, `seq_json` = what is needed to find
every frame again. It is searched through a few sample frames and played through a small preview video made on demand."""
import hashlib
import json
import os
import re
import subprocess
import threading
from pathlib import Path

from .db import DATA_DIR

SEQ_EXT = {".png", ".jpg", ".jpeg", ".tif", ".tiff", ".exr", ".dpx", ".tga", ".bmp", ".webp"}
FFMPEG_ONLY = {".exr", ".dpx"}                    # formats PIL cannot read: ffmpeg makes their thumbnails and frames
MIN_FRAMES = 24                                   # fewer than this is a handful of pictures, not an animation
MIN_DENSITY = 0.8                                 # at least this share of the numbers between first and last must exist
DEFAULT_FPS = 24.0
PROXY_DIR = DATA_DIR / "proxies"
_NUM = re.compile(r"^(?P<prefix>.*?)(?P<sep>[._\- ]?)(?P<num>\d{3,8})$")
_NOT_SEQUENCES = ("camera", "screenshot", "whatsapp")      # IMG_0001.jpg, Screenshot 0001.png...: photos, not frames


def frame_name(info: dict, n: int) -> str:
    num = f"{n:0{info['pad']}d}" if info["pad"] else str(n)
    return f"{info['prefix']}{info['sep']}{num}{info['ext']}"


def pattern(info: dict) -> str:
    return f"{info['prefix']}{info['sep']}{'#' * (info['pad'] or 1)}{info['ext']}"


def detect(filenames) -> list[dict]:
    """Groups of numbered files that look like one animation. Pure: names only, nothing is read from disk."""
    from . import names
    buckets: dict[tuple, dict[int, str]] = {}
    for fn in filenames:
        stem, ext = os.path.splitext(fn)
        if ext.lower() not in SEQ_EXT:
            continue
        m = _NUM.match(stem)
        if not m:
            continue
        buckets.setdefault((m["prefix"], m["sep"], ext), {})[int(m["num"])] = m["num"]
    found = []
    for (prefix, sep, ext), nums in buckets.items():
        if len(nums) < MIN_FRAMES:
            continue
        widths = {len(t) for t in nums.values()}
        padded = any(t.startswith("0") for t in nums.values())
        if padded and len(widths) != 1:
            continue                                   # mixed zero padding: not one numbering
        pad = widths.pop() if len(widths) == 1 else 0  # one width (0001.., or 1000..1200) is kept; 998..1002 is plain numbers
        info = {"prefix": prefix, "sep": sep, "pad": pad, "ext": ext}
        have = sorted(n for n, t in nums.items() if frame_name(info, n) == f"{prefix}{sep}{t}{ext}")
        if len(have) < MIN_FRAMES:
            continue
        first, last = have[0], have[-1]
        if len(have) / (last - first + 1) < MIN_DENSITY:
            continue
        if names.source_of(frame_name(info, first)) in _NOT_SEQUENCES:
            continue
        gaps = set(range(first, last + 1)) - set(have)
        info.update(first=first, last=last, count=len(have), missing=sorted(gaps)[:500], missing_count=len(gaps))
        found.append(info)
    return found


def frame_paths(info: dict) -> list[str]:
    """Every frame that exists, in order."""
    skip = set(info.get("missing") or [])
    return [os.path.join(info["dir"], frame_name(info, n)) for n in range(info["first"], info["last"] + 1) if n not in skip]


def verify(info: dict) -> bool:
    """Three sample frames must share one size, or this is a folder of unrelated numbered pictures."""
    frames = frame_paths(info)
    if info["ext"].lower() in FFMPEG_ONLY or len(frames) < 3:
        return True
    try:
        from PIL import Image
        sizes = set()
        for f in (frames[0], frames[len(frames) // 2], frames[-1]):
            with Image.open(f) as im:
                sizes.add(im.size)
        return len(sizes) == 1
    except Exception:
        return False


def to_jpg(frame: str, out: Path, height: int = 448) -> bool:
    """One frame as a small jpg. PIL for ordinary pictures, ffmpeg for EXR and DPX."""
    try:
        if os.path.splitext(frame)[1].lower() in FFMPEG_ONLY:
            subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", frame, "-frames:v", "1", "-vf", f"scale=-2:{height}", str(out)],
                           capture_output=True, timeout=60)
            return out.exists()
        from PIL import Image
        with Image.open(frame) as im:
            im = im.convert("RGB")
            im.thumbnail((height * 4, height))
            im.save(out, "JPEG", quality=80)
        return True
    except Exception:
        return False


def sample(info: dict, k: int) -> list[str]:
    frames = frame_paths(info)
    if len(frames) <= k:
        return frames
    return [frames[int(len(frames) * (i + 0.5) / k)] for i in range(k)]


def make_thumb(info: dict, out: Path) -> str | None:
    """The middle frame is the thumbnail. Replaces an older one: the frames may have changed."""
    out.unlink(missing_ok=True)
    mid = sample(info, 1)
    return out.name if mid and to_jpg(mid[0], out, 480) else None


def sample_jpgs(info: dict, k: int, tmp_dir: Path) -> list[str]:
    """Evenly spaced frames as small jpgs in a temp folder, for the model to look at."""
    tmp_dir.mkdir(parents=True, exist_ok=True)
    out = []
    for i, f in enumerate(sample(info, k)):
        dest = tmp_dir / f"{i}.jpg"
        if to_jpg(f, dest):
            out.append(str(dest))
    return out


def load(text: str | None) -> dict | None:
    try:
        return json.loads(text) if text else None
    except ValueError:
        return None


def brief(info: dict, fps: float | None) -> dict:
    """What the apps need to show a sequence."""
    return {"count": info["count"], "first": info["first"], "last": info["last"], "missing": info.get("missing_count", 0),
            "pattern": pattern(info), "fps": fps or DEFAULT_FPS, "dir": info["dir"]}


# ---- the preview video ----
_locks: dict[str, threading.Lock] = {}


def proxy_path(info: dict, fps: float) -> Path:
    key = f"{info['dir']}|{info['prefix']}|{info['ext']}|{info['first']}|{info['last']}|{info['count']}|{info.get('stamp', '')}|{fps:.3f}"
    return PROXY_DIR / (hashlib.sha1(key.encode("utf-8", "ignore")).hexdigest()[:16] + ".mp4")


def build_proxy(info: dict, fps: float | None = None) -> Path:
    """A 720p H.264 video of the sequence, made once and reused until the sequence or its speed changes.
    Gaps hold the previous frame, so the timing stays right."""
    fps = fps or DEFAULT_FPS
    out = proxy_path(info, fps)
    if out.exists():
        return out
    lock = _locks.setdefault(str(out), threading.Lock())
    with lock:
        if out.exists():
            return out
        PROXY_DIR.mkdir(parents=True, exist_ok=True)
        listing = out.with_suffix(".txt")
        skip = set(info.get("missing") or [])
        have = [n for n in range(info["first"], info["last"] + 1) if n not in skip]
        lines = ["ffconcat version 1.0"]
        for i, n in enumerate(have):
            nxt = have[i + 1] if i + 1 < len(have) else n + 1
            path = os.path.join(info["dir"], frame_name(info, n)).replace("\\", "/").replace("'", "'\\''")
            lines += [f"file '{path}'", f"duration {(nxt - n) / fps:.6f}"]
        listing.write_text("\n".join(lines), encoding="utf-8")
        tmp = out.with_suffix(".part.mp4")
        try:
            run = subprocess.run(
                ["ffmpeg", "-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", str(listing),
                 "-vf", "scale=-2:'min(720,ih)':flags=bicubic,format=yuv420p", "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
                 "-r", f"{fps:g}", "-movflags", "+faststart", str(tmp)],
                capture_output=True, text=True, timeout=1800)
            if run.returncode != 0 or not tmp.exists():
                raise RuntimeError((run.stderr or "ffmpeg failed")[-300:])
            tmp.replace(out)
        finally:
            listing.unlink(missing_ok=True)
            tmp.unlink(missing_ok=True)
    return out
