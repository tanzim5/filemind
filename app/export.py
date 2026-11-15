"""Exports made with FFmpeg: clips, conversions, smaller copies, soundtracks, GIFs and contact sheets.

An export always writes a new file. The original is only ever read. Jobs run one at a time in the background so a
long encode never competes with itself for the CPU; each reports progress and can be cancelled.
"""
import math
import os
import subprocess
import sys
import threading
import time
from pathlib import Path

from PIL import Image

from . import db, media_meta

VIDEO_FORMATS = ("mp4", "webm", "gif")
KEPT_VIDEO = ("mp4", "webm", "mov", "mkv", "m4v")     # containers a video may stay in when the format is "same"
AUDIO_FORMATS = ("mp3", "wav", "m4a", "flac")
IMAGE_FORMATS = ("jpg", "png", "webp")
FORMATS = VIDEO_FORMATS + AUDIO_FORMATS + IMAGE_FORMATS + ("sheet", "same")

_jobs: dict[int, dict] = {}
_procs: dict[int, subprocess.Popen] = {}
_lock = threading.Lock()
_queue: list[int] = []
_wake = threading.Event()
_next_id = 1
_worker: threading.Thread | None = None


# ---------------------------------------------------------------- what to make

def clean_spec(body: dict) -> dict:
    """Validates a request and fills the defaults. Raises ValueError with a message meant for the user."""
    fmt = str(body.get("format") or "same").lower()
    if fmt not in FORMATS:
        raise ValueError("Choose a format File Mind can make")

    def number(key, low, high):
        value = body.get(key)
        if value in (None, ""):
            return None
        try:
            value = float(value)
        except (TypeError, ValueError):
            raise ValueError(f"{key} must be a number") from None
        if not math.isfinite(value) or value < low or value > high:
            raise ValueError(f"{key} is out of range")
        return value

    start, end = number("start", 0, 1e6), number("end", 0, 1e6)
    if start is not None and end is not None and end - start < 0.05:
        raise ValueError("The end must come after the start")
    rotate = int(number("rotate", 0, 270) or 0)
    if rotate not in (0, 90, 180, 270):
        raise ValueError("Rotate by 90, 180 or 270 degrees")
    height = number("height", 120, 4320)
    return {
        "format": fmt, "start": start, "end": end,
        "height": int(height) if height else None,
        "target_mb": number("target_mb", 0.5, 100000),
        "rotate": rotate,
        "speed": number("speed", 0.25, 4) or 1.0,
        "vertical": bool(body.get("vertical")),        # crop the middle to 9:16
        "normalize": bool(body.get("normalize")),      # even out loudness
        "trim_silence": bool(body.get("trim_silence")),
        "mute": bool(body.get("mute")),
        "copy": bool(body.get("copy")),                # cut without re-encoding (fast, lands on keyframes)
    }


def target_format(spec: dict, kind: str, ext: str) -> str:
    """The real output format for one file: 'same' keeps the source's own type."""
    fmt = spec["format"]
    if fmt != "same":
        return fmt
    ext = ext.lower().lstrip(".")
    if kind == "video":
        return ext if ext in KEPT_VIDEO else "mp4"
    if kind == "audio":
        return ext if ext in AUDIO_FORMATS else "mp3"
    return ext if ext in IMAGE_FORMATS else "jpg"


def allowed(fmt: str, kind: str) -> bool:
    if kind == "video":
        return fmt in VIDEO_FORMATS + KEPT_VIDEO + AUDIO_FORMATS + ("sheet",)
    if kind == "audio":
        return fmt in AUDIO_FORMATS
    if kind == "image":
        return fmt in IMAGE_FORMATS
    return False


def can_copy(spec: dict, fmt: str, kind: str, ext: str) -> bool:
    """A straight cut needs the same container and nothing that changes the pictures or the sound."""
    return (spec["copy"] and kind in ("video", "audio") and fmt == ext.lower().lstrip(".")
            and not spec["height"] and not spec["target_mb"] and not spec["rotate"] and spec["speed"] == 1.0
            and not spec["vertical"] and not spec["normalize"] and not spec["trim_silence"] and not spec["mute"])


def output_seconds(spec: dict, duration: float | None) -> float | None:
    """How long the result will play, used for progress and for fitting a file size."""
    if not duration:
        return None
    start = min(spec["start"] or 0, duration)
    end = min(spec["end"], duration) if spec["end"] is not None else duration
    return max(0.05, (end - start) / spec["speed"])


def _atempo(speed: float) -> list[str]:
    # atempo accepts 0.5 to 2 per step, so larger changes are chained.
    steps = []
    while speed > 2.0:
        steps.append("atempo=2.0"); speed /= 2.0
    while speed < 0.5:
        steps.append("atempo=0.5"); speed /= 0.5
    if abs(speed - 1.0) > 1e-3:
        steps.append(f"atempo={speed:.4f}")
    return steps


def build_command(spec: dict, src: Path, dst: Path, fmt: str, kind: str, facts: dict, rate: float = 1.0) -> list[str]:
    """The FFmpeg command for one export. Pure, so it can be tested without running anything.
    `rate` scales the bitrate chosen for a size limit; a second attempt uses it when the first came out too big."""
    cmd = ["ffmpeg", "-y", "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1"]
    if spec["start"]:
        cmd += ["-ss", f"{spec['start']:.3f}"]
    cmd += ["-i", str(src)]
    if spec["end"] is not None:
        cmd += ["-t", f"{spec['end'] - (spec['start'] or 0):.3f}"]

    if can_copy(spec, fmt, kind, src.suffix):
        return cmd + ["-c", "copy", "-avoid_negative_ts", "make_zero", "-f", _muxer(fmt), str(dst)]

    if fmt == "sheet":
        seconds = output_seconds(spec, facts.get("duration")) or 16
        every = max(seconds / 16, 0.04)
        return cmd + ["-vf", f"fps=1/{every:.4f},scale=480:-2,tile=4x4:padding=6:margin=6:color=0x101014",
                      "-frames:v", "1", "-update", "1", "-q:v", "3", "-f", "image2", "-c:v", "mjpeg", str(dst)]

    vf, af = [], []
    if spec["rotate"] == 90:
        vf.append("transpose=1")
    elif spec["rotate"] == 270:
        vf.append("transpose=2")
    elif spec["rotate"] == 180:
        vf.append("hflip,vflip")
    if spec["vertical"]:
        vf.append("crop=trunc(ih*9/16/2)*2:ih")
    source_height = facts.get("width") if spec["rotate"] in (90, 270) else facts.get("height")
    if spec["height"] and (not source_height or spec["height"] < source_height):
        vf.append(f"scale=-2:{spec['height'] // 2 * 2}")
    if spec["speed"] != 1.0:
        vf.append(f"setpts=PTS/{spec['speed']:.4f}")
        af += _atempo(spec["speed"])
    if spec["trim_silence"]:
        quiet = "silenceremove=start_periods=1:start_threshold=-50dB:start_silence=0.1"
        af += [quiet, "areverse", quiet, "areverse"]
    if spec["normalize"]:
        af.append("loudnorm=I=-16:TP=-1.5:LRA=11")

    if fmt in AUDIO_FORMATS:
        cmd += ["-vn"]
        if af:
            cmd += ["-af", ",".join(af)]
        cmd += {"mp3": ["-c:a", "libmp3lame", "-q:a", "2"], "wav": ["-c:a", "pcm_s16le"],
                "m4a": ["-c:a", "aac", "-b:a", "192k"], "flac": ["-c:a", "flac"]}[fmt]
        return cmd + ["-f", _muxer(fmt), str(dst)]

    if fmt == "gif":
        height = min(spec["height"] or 480, 720)
        # A GIF picks its own size (12 frames a second, 480 px tall unless asked), then builds a palette from the clip.
        chain = [part for part in vf if not part.startswith("scale=")]
        chain.append(f"fps=12,scale=-2:{height // 2 * 2}:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4")
        return cmd + ["-an", "-filter_complex", ",".join(chain), "-f", "gif", str(dst)]

    # mp4, webm, or a container the source was already in (mov, mkv, m4v)
    if vf:
        cmd += ["-vf", ",".join(vf)]
    audio_k = 128
    seconds = output_seconds(spec, facts.get("duration"))
    video_k = None
    if spec["target_mb"] and seconds:
        total_k = spec["target_mb"] * 8192 / seconds
        video_k = max(80, int((total_k * 0.94 - (0 if spec["mute"] else audio_k)) * rate))
    if fmt != "webm":
        cmd += ["-c:v", "libx264", "-preset", "medium", "-pix_fmt", "yuv420p"]
        cmd += ["-b:v", f"{video_k}k", "-maxrate", f"{int(video_k * 1.25)}k", "-bufsize", f"{video_k * 2}k"] if video_k else ["-crf", "20"]
        if fmt != "mkv":
            cmd += ["-movflags", "+faststart"]
        audio = ["-c:a", "aac", "-b:a", f"{audio_k}k"]
    else:
        cmd += ["-c:v", "libvpx-vp9", "-row-mt", "1", "-deadline", "good", "-cpu-used", "3"]
        cmd += ["-b:v", f"{video_k}k"] if video_k else ["-crf", "32", "-b:v", "0"]
        audio = ["-c:a", "libopus", "-b:a", f"{audio_k}k"]
    if spec["mute"]:
        cmd += ["-an"]
    else:
        if af:
            cmd += ["-af", ",".join(af)]
        cmd += audio
    return cmd + ["-f", _muxer(fmt), str(dst)]


def _muxer(fmt: str) -> str:
    # The file is written under a temporary name, so FFmpeg is told the container instead of guessing from it.
    return {"mp4": "mp4", "webm": "webm", "gif": "gif", "mp3": "mp3", "wav": "wav", "m4a": "ipod", "flac": "flac",
            "m4v": "mp4", "mov": "mov", "mkv": "matroska", "ogg": "ogg", "opus": "opus", "aac": "adts"}.get(fmt, fmt)


# ---------------------------------------------------------------- where it goes

def _stamp(seconds: float) -> str:
    seconds = int(seconds)
    return f"{seconds // 60}m{seconds % 60:02d}s"


def output_name(spec: dict, stem: str, fmt: str) -> str:
    """A readable name that says what was done: 'holiday (0m42s-0m51s).mp4', 'holiday (720p).mp4'."""
    notes = []
    if spec["start"] is not None or spec["end"] is not None:
        notes.append(f"{_stamp(spec['start'] or 0)}-{_stamp(spec['end'])}" if spec["end"] is not None else f"from {_stamp(spec['start'] or 0)}")
    if fmt == "sheet":
        notes.append("contact sheet")
    else:
        if spec["height"]:
            notes.append(f"{spec['height']}p")
        if spec["target_mb"]:
            notes.append(f"{spec['target_mb']:g} MB")
        if spec["vertical"]:
            notes.append("vertical")
        if spec["speed"] != 1.0:
            notes.append(f"{spec['speed']:g}x")
    ext = "jpg" if fmt == "sheet" else fmt
    return f"{stem} ({', '.join(notes)}).{ext}" if notes else f"{stem}.{ext}"


def free_path(folder: Path, name: str, taken: set[str] | None = None) -> Path:
    """Never overwrites: adds ' 2', ' 3'… until the name is free, on disk and among jobs already queued."""
    taken = taken if taken is not None else set()
    stem, ext = os.path.splitext(name)
    candidate, n = folder / name, 1
    while candidate.exists() or str(candidate).lower() in taken:
        n += 1
        candidate = folder / f"{stem} {n}{ext}"
    taken.add(str(candidate).lower())
    return candidate


# ---------------------------------------------------------------- running

def submit(file_ids: list[int], spec: dict, destination: str | None) -> list[dict]:
    """Queues one export per file and returns the new jobs. Files that cannot be made into the format are refused
    up front, so nothing half-finishes."""
    global _next_id
    if not file_ids:
        raise ValueError("Choose at least one file")
    marks = ",".join("?" * len(file_ids))
    rows = db.conn().execute(f"SELECT id,path,name,kind,width,height,duration FROM files WHERE id IN ({marks})", file_ids).fetchall()
    found = {r["id"]: r for r in rows}
    if destination:
        folder = Path(destination)
        if not folder.is_dir():
            raise ValueError("That folder does not exist")
    else:
        folder = None
    plans = []
    with _lock:
        taken = {j["output"].lower() for j in _jobs.values() if j["status"] in ("queued", "running")}
    for fid in file_ids:
        row = found.get(fid)
        if row is None or not Path(row["path"]).is_file():
            raise ValueError("A chosen file has moved or is no longer in the library")
        src = Path(row["path"])
        fmt = target_format(spec, row["kind"], src.suffix)
        if not allowed(fmt, row["kind"]):
            raise ValueError(f"{row['name']} cannot be made into {fmt.upper()}")
        out = free_path(folder or src.parent, output_name(spec, src.stem, fmt), taken)
        plans.append((row, src, fmt, out))
    made = []
    with _lock:
        for row, src, fmt, out in plans:
            job = {"id": _next_id, "file_id": row["id"], "name": out.name, "output": str(out), "source": str(src),
                   "format": fmt, "kind": row["kind"], "status": "queued", "progress": 0.0, "error": None,
                   "created": time.time(), "spec": spec,
                   "facts": {"width": row["width"], "height": row["height"], "duration": row["duration"]}}
            _jobs[_next_id] = job
            _queue.append(_next_id)
            _next_id += 1
            made.append(public(job))
    _ensure_worker()
    _wake.set()
    return made


def public(job: dict) -> dict:
    return {k: job[k] for k in ("id", "file_id", "name", "output", "format", "status", "progress", "error")}


def jobs() -> list[dict]:
    with _lock:
        return [public(j) for j in sorted(_jobs.values(), key=lambda j: j["id"])]


def cancel(job_id: int) -> dict:
    with _lock:
        job = _jobs.get(job_id)
        if job is None:
            raise KeyError(job_id)
        if job["status"] == "queued":
            job["status"] = "cancelled"
            if job_id in _queue:
                _queue.remove(job_id)
        elif job["status"] == "running":
            job["status"] = "cancelling"
            proc = _procs.get(job_id)
            if proc is not None:
                try:
                    proc.terminate()
                except OSError:
                    pass
        return public(job)


def clear_finished() -> int:
    with _lock:
        gone = [i for i, j in _jobs.items() if j["status"] in ("done", "error", "cancelled")]
        for i in gone:
            del _jobs[i]
        return len(gone)


def _ensure_worker():
    global _worker
    with _lock:
        if _worker is None or not _worker.is_alive():
            _worker = threading.Thread(target=_work, daemon=True, name="export")
            _worker.start()


def _work():
    while True:
        with _lock:
            job_id = _queue.pop(0) if _queue else None
            job = _jobs.get(job_id) if job_id else None
            if job is not None:
                job["status"] = "running"
        if job is None:
            _wake.clear()
            with _lock:
                waiting = bool(_queue)
            if not waiting:
                _wake.wait()
            continue
        _run(job)


def _run(job: dict):
    out = Path(job["output"])
    part = out.with_name(out.name + ".part")
    try:
        if job["kind"] == "image":
            _image(job, part)
        else:
            facts = job["facts"]
            if not facts.get("duration"):
                facts = {**facts, **{k: v for k, v in media_meta.probe(Path(job["source"]), job["kind"]).items() if v}}
            seconds = output_seconds(job["spec"], facts.get("duration"))
            _ffmpeg(job, build_command(job["spec"], Path(job["source"]), part, job["format"], job["kind"], facts), seconds)
            # "Fit under 25 MB" is a promise. One-pass encoding can overshoot on busy pictures, so a result that came
            # out too big is made again, smaller by the amount it missed (twice at most).
            limit = (job["spec"]["target_mb"] or 0) * 1048576
            rate = 1.0
            for _ in range(2):
                with _lock:
                    stopping = job["status"] == "cancelling"
                if stopping or not limit or job["format"] in AUDIO_FORMATS + ("gif", "sheet") or not part.is_file() or part.stat().st_size <= limit:
                    break
                rate *= limit / part.stat().st_size * 0.95
                with _lock:
                    job["progress"] = 0.0
                _ffmpeg(job, build_command(job["spec"], Path(job["source"]), part, job["format"], job["kind"], facts, rate), seconds)
        with _lock:
            cancelled = job["status"] == "cancelling"
        if cancelled:
            raise InterruptedError()
        if not part.is_file() or part.stat().st_size == 0:
            raise RuntimeError("FFmpeg made an empty file")
        final = free_path(out.parent, out.name)       # the name may have been taken while this was encoding
        os.replace(part, final)
        with _lock:
            job.update(status="done", progress=1.0, output=str(final), name=final.name)
    except InterruptedError:
        part.unlink(missing_ok=True)
        with _lock:
            job["status"] = "cancelled"
    except Exception as error:
        part.unlink(missing_ok=True)
        with _lock:
            cancelled = job["status"] == "cancelling"
            job["status"] = "cancelled" if cancelled else "error"
            job["error"] = None if cancelled else (str(error).strip().splitlines() or ["Export failed"])[-1][:300]


def _ffmpeg(job: dict, cmd: list[str], seconds: float | None):
    flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, stdin=subprocess.DEVNULL,
                            text=True, encoding="utf-8", errors="replace", creationflags=flags)
    with _lock:
        _procs[job["id"]] = proc
        if job["status"] == "cancelling":      # cancelled between being picked up and FFmpeg starting
            proc.terminate()
    errors: list[str] = []
    reader = threading.Thread(target=lambda: errors.extend(proc.stderr.readlines()[-20:]), daemon=True)
    reader.start()
    try:
        for line in proc.stdout:
            if seconds and line.startswith(("out_time_us=", "out_time_ms=")):
                try:
                    done = int(line.split("=", 1)[1]) / 1e6
                except ValueError:
                    continue
                with _lock:
                    job["progress"] = max(job["progress"], min(0.99, done / seconds))
        proc.wait()
        reader.join(timeout=5)
    finally:
        proc.stdout.close(); proc.stderr.close()
        with _lock:
            _procs.pop(job["id"], None)
    if proc.returncode != 0:
        raise RuntimeError("".join(errors).strip() or f"FFmpeg stopped with code {proc.returncode}")


def _image(job: dict, part: Path):
    """Pictures are converted with Pillow: faster than FFmpeg for stills and it keeps the colour profile."""
    spec, fmt = job["spec"], job["format"]
    with Image.open(job["source"]) as im:
        im.seek(0)
        icc = im.info.get("icc_profile")
        if spec["rotate"]:
            im = im.rotate(-spec["rotate"], expand=True)
        if spec["vertical"]:
            w = int(im.height * 9 / 16)
            if w < im.width:
                left = (im.width - w) // 2
                im = im.crop((left, 0, left + w, im.height))
        if spec["height"] and spec["height"] < im.height:
            im = im.resize((max(1, round(im.width * spec["height"] / im.height)), spec["height"]), Image.LANCZOS)
        if fmt == "jpg":
            if im.mode in ("RGBA", "LA", "P"):
                rgba = im.convert("RGBA")
                flat = Image.new("RGB", rgba.size, (255, 255, 255))
                flat.paste(rgba, mask=rgba.split()[-1])
                im = flat
            im = im.convert("RGB")
        elif im.mode not in ("RGB", "RGBA", "L", "LA"):
            im = im.convert("RGBA")
        options = {"icc_profile": icc} if icc else {}
        if fmt == "jpg":
            quality = 90
            im.save(part, "JPEG", quality=quality, optimize=True, **options)
            # A size limit is met by stepping the quality down; below 30 the picture is not worth keeping.
            while spec["target_mb"] and part.stat().st_size > spec["target_mb"] * 1048576 and quality > 30:
                quality -= 10
                im.save(part, "JPEG", quality=quality, optimize=True, **options)
        elif fmt == "webp":
            im.save(part, "WEBP", quality=88, method=4, **options)
        else:
            im.save(part, "PNG", optimize=True, **options)
