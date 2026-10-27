"""File classification, text extraction and thumbnails."""
import hashlib
import os
import subprocess
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

from PIL import Image

from .db import DATA_DIR

THUMB_DIR = DATA_DIR / "thumbs"
THUMB_DIR.mkdir(exist_ok=True)
TMP_DIR = DATA_DIR / "tmp"
TMP_DIR.mkdir(exist_ok=True)

IMAGE = {".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff"}
VIDEO = {".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".wmv", ".flv"}
AUDIO = {".mp3", ".wav", ".m4a", ".flac", ".ogg", ".aac", ".opus", ".wma"}
CODE = {".py", ".js", ".ts", ".tsx", ".jsx", ".html", ".css", ".json", ".yaml", ".yml",
        ".toml", ".sql", ".sh", ".bat", ".ps1", ".c", ".cpp", ".h", ".cs", ".java",
        ".go", ".rs", ".php", ".rb", ".xml", ".ini", ".cfg", ".ipynb"}
TEXT = {".txt", ".md", ".csv", ".log", ".rtf", ".srt", ".vtt", ".tsv"}
DOC = {".pdf", ".docx"}

MAX_TEXT_CHARS = 6000          # ~1.5k tokens, well inside the 8k window
VIDEO_FRAMES = int(os.environ.get("FILEMIND_VIDEO_FRAMES") or 8)               # frames sampled per video
AUDIO_SECONDS = 120            # audio clip length embedded


def kind_of(ext: str) -> str:
    ext = ext.lower()
    if ext in IMAGE:
        return "image"
    if ext in VIDEO:
        return "video"
    if ext in AUDIO:
        return "audio"
    if ext in CODE:
        return "code"
    if ext in TEXT:
        return "text"
    if ext in DOC:
        return "doc"
    return "other"


def extract_text(path: Path, kind: str) -> str:
    """Returns a snippet of readable text, or '' if none."""
    ext = path.suffix.lower()
    try:
        if ext == ".pdf":
            import fitz  # pymupdf
            parts, total = [], 0
            with fitz.open(path) as doc:
                for page in doc:
                    t = page.get_text()
                    parts.append(t)
                    total += len(t)
                    if total >= MAX_TEXT_CHARS or page.number >= 6:
                        break
            return " ".join("".join(parts).split())[:MAX_TEXT_CHARS]
        if ext == ".docx":
            import docx
            d = docx.Document(str(path))
            text = "\n".join(p.text for p in d.paragraphs)
            return " ".join(text.split())[:MAX_TEXT_CHARS]
        if kind in ("text", "code"):
            with open(path, "r", encoding="utf-8", errors="ignore") as f:
                return f.read(MAX_TEXT_CHARS * 2)[:MAX_TEXT_CHARS]
    except Exception:
        return ""
    return ""


def _thumb_path(path: Path) -> Path:
    h = hashlib.sha1((str(path) + "|480").encode("utf-8", "ignore")).hexdigest()
    return THUMB_DIR / f"{h}.jpg"


def make_thumb(path: Path, kind: str) -> str | None:
    out = _thumb_path(path)
    if out.exists():
        return out.name
    try:
        if kind == "image":
            with Image.open(path) as im:
                im.seek(0)
                im = im.convert("RGB")
                im.thumbnail((480, 480))
                im.save(out, "JPEG", quality=80)
            return out.name
        if kind == "video":
            r = subprocess.run(
                ["ffmpeg", "-y", "-loglevel", "error", "-ss", "1", "-i", str(path),
                 "-frames:v", "1", "-vf", "scale=480:-2", str(out)],
                capture_output=True, timeout=60)
            if not out.exists():  # clip shorter than 1s
                subprocess.run(
                    ["ffmpeg", "-y", "-loglevel", "error", "-i", str(path),
                     "-frames:v", "1", "-vf", "scale=480:-2", str(out)],
                    capture_output=True, timeout=60)
            return out.name if out.exists() else None
        if path.suffix.lower() == ".pdf":
            import fitz  # pymupdf
            with fitz.open(path) as doc:
                page = doc[0]
                zoom = 480 / max(page.rect.width, 1)
                pix = page.get_pixmap(matrix=fitz.Matrix(zoom, zoom), alpha=False)
                pix.save(str(out), jpg_quality=82)
            return out.name
        if kind in ("text", "code", "doc"):
            return _text_card(path, kind, out)
    except Exception:
        return None
    return None


def _text_card(path: Path, kind: str, out: Path) -> str | None:
    """A small page-like card showing the start of a text, code or Word file."""
    from PIL import ImageDraw, ImageFont
    if path.suffix.lower() == ".docx":
        import docx
        raw = "\n".join(p.text for p in docx.Document(str(path)).paragraphs)
    else:
        with open(path, "r", encoding="utf-8", errors="ignore") as f:
            raw = f.read(4000)
    lines = [ln.rstrip().replace("	", "    ") for ln in raw.splitlines()]
    while lines and not lines[0].strip():
        lines.pop(0)
    if not any(ln.strip() for ln in lines):
        return None
    dark = kind == "code"
    bg, fg, sub = ((30, 34, 41), (206, 212, 222), (110, 118, 130)) if dark else ((247, 245, 240), (52, 56, 64), (150, 150, 150))
    w, h, pad = 420, 540, 26
    im = Image.new("RGB", (w, h), bg)
    d = ImageDraw.Draw(im)
    mono = kind != "doc" and path.suffix.lower() not in (".md", ".txt", ".rtf")
    try:
        font = ImageFont.truetype("consola.ttf" if mono else "segoeui.ttf", 15 if mono else 16)
    except Exception:
        font = ImageFont.load_default()
    y, step = pad, 21
    for ln in lines:
        if y > h - pad - step:
            break
        if not ln.strip():
            y += step // 2
            continue
        cols = 40 if mono else 46
        for part in (ln[i:i + cols] for i in range(0, min(len(ln), cols), cols)):
            if y > h - pad - step:
                break
            d.text((pad, y), part, font=font, fill=sub if part.lstrip().startswith(("#", "//")) and dark else fg)
            y += step
    im.save(out, "JPEG", quality=85)
    return out.name


def dominant_color(thumb_name: str) -> str | None:
    """Most characteristic colour of a thumbnail as '#rrggbb' (favours vivid colours over big flat blacks/whites)."""
    import colorsys
    try:
        with Image.open(THUMB_DIR / thumb_name) as im:
            im = im.convert("RGB").resize((48, 48))
            q = im.quantize(colors=8, method=Image.Quantize.MEDIANCUT)
            pal = q.getpalette()[:24]
            best, best_score = None, -1.0
            for count, idx in q.getcolors():
                r, g, b = pal[idx * 3:idx * 3 + 3]
                h, l, s = colorsys.rgb_to_hls(r / 255, g / 255, b / 255)
                score = count * (0.35 + s) * (0.6 if l < 0.1 or l > 0.95 else 1.0)
                if score > best_score:
                    best, best_score = (r, g, b), score
            return "#%02x%02x%02x" % best
    except Exception:
        return None


def video_frames(path: Path, n: int = VIDEO_FRAMES) -> list[str]:
    """Evenly sampled frames as jpg paths (temp dir)."""
    h = hashlib.sha1(str(path).encode("utf-8", "ignore")).hexdigest()[:12]
    d = TMP_DIR / h
    d.mkdir(exist_ok=True)
    try:
        probe = subprocess.run(
            ["ffprobe", "-v", "error", "-show_entries", "format=duration",
             "-of", "default=nw=1:nk=1", str(path)],
            capture_output=True, text=True, timeout=30)
        dur = float(probe.stdout.strip() or 0)
    except Exception:
        dur = 0
    if dur <= 0:
        dur = 1
    def grab(i):                                  # frames are independent, so fetch several at once
        t = dur * (i + 0.5) / n
        f = d / f"{i}.jpg"
        try:
            subprocess.run(
                ["ffmpeg", "-y", "-loglevel", "error", "-ss", f"{t:.2f}", "-i", str(path),
                 "-frames:v", "1", "-vf", "scale=-2:448", str(f)],
                capture_output=True, timeout=60)
        except Exception:
            pass
        return str(f) if f.exists() else None

    with ThreadPoolExecutor(max_workers=min(4, n)) as ex:
        frames = [f for f in ex.map(grab, range(n)) if f]
    return frames


def audio_clip(path: Path, seconds: int = AUDIO_SECONDS) -> str | None:
    """Mono 16 kHz wav of the first N seconds (what the model expects)."""
    h = hashlib.sha1(str(path).encode("utf-8", "ignore")).hexdigest()[:12]
    out = TMP_DIR / f"{h}.wav"
    try:
        subprocess.run(
            ["ffmpeg", "-y", "-loglevel", "error", "-i", str(path), "-t", str(seconds),
             "-ac", "1", "-ar", "16000", str(out)],
            capture_output=True, timeout=120)
        return str(out) if out.exists() else None
    except Exception:
        return None


def cleanup_tmp(path: Path):
    h = hashlib.sha1(str(path).encode("utf-8", "ignore")).hexdigest()[:12]
    d = TMP_DIR / h
    if d.exists():
        for f in d.iterdir():
            f.unlink(missing_ok=True)
        d.rmdir()
    (TMP_DIR / f"{h}.wav").unlink(missing_ok=True)
