"""Small, local media facts used by filters; no model inference is required."""
import json
import subprocess
import time
from pathlib import Path

from PIL import Image

from . import db


def _exif_taken(image) -> float | None:
    """When the picture was taken (EXIF DateTimeOriginal, local clock time), or None."""
    try:
        exif = image.getexif()
        raw = exif.get_ifd(0x8769).get(36867) or exif.get(36867) or exif.get(306)
        if raw:
            return time.mktime(time.strptime(str(raw).strip()[:19], "%Y:%m:%d %H:%M:%S"))
    except (OSError, ValueError, OverflowError, AttributeError, KeyError):
        pass
    return None


def _tag_taken(text) -> float | None:
    """ffprobe's creation_time ('2026-08-05T10:00:00.000000Z') as a timestamp, or None."""
    try:
        t = str(text).strip().replace("Z", "+00:00")
        from datetime import datetime, timezone
        d = datetime.fromisoformat(t)
        if d.tzinfo is None:
            d = d.replace(tzinfo=timezone.utc)
        ts = d.timestamp()
        return ts if ts > 86400 * 365 else None            # 1970/1904 placeholders mean "unknown"
    except (ValueError, OverflowError):
        return None


def probe(path: Path, kind: str) -> dict:
    out = {"width": None, "height": None, "duration": None, "fps": None, "codec": None, "taken": None}
    try:
        if kind == "image":
            with Image.open(path) as image:
                out["width"], out["height"] = image.size
                out["taken"] = _exif_taken(image)
            return out
        if kind not in ("video", "audio"):
            return out
        result = subprocess.run(
            ["ffprobe", "-v", "error", "-show_entries",
             "format=duration:format_tags=creation_time:stream=codec_type,codec_name,width,height,r_frame_rate",
             "-of", "json", str(path)], capture_output=True, text=True, timeout=15, check=True)
        data = json.loads(result.stdout)
        out["duration"] = float(data.get("format", {}).get("duration") or 0) or None
        out["taken"] = _tag_taken((data.get("format", {}).get("tags") or {}).get("creation_time"))
        stream = next((s for s in data.get("streams", []) if s.get("codec_type") == ("video" if kind == "video" else "audio")), None)
        if stream:
            out["codec"] = str(stream.get("codec_name") or "").lower() or None
            out["width"], out["height"] = stream.get("width"), stream.get("height")
            rate = str(stream.get("r_frame_rate") or "0/1").split("/")
            if kind == "video" and len(rate) == 2 and float(rate[1]):
                out["fps"] = round(float(rate[0]) / float(rate[1]), 3)
    except (OSError, ValueError, IndexError, KeyError, subprocess.SubprocessError):
        pass
    return out


def backfill():
    """Warm old-library metadata after startup, yielding to the primary indexer."""
    from . import indexer
    c = db.conn()
    rows = c.execute("SELECT id,path,kind,size,mtime FROM files WHERE status='done' AND "
                     "((kind='image' AND width IS NULL) OR (kind IN ('video','audio') AND duration IS NULL) "
                     "OR (taken IS NULL AND kind IN ('image','video','audio')))").fetchall()
    for row in rows:
        while indexer.state["running"]:
            time.sleep(1)
        path = Path(row["path"])
        if not path.is_file():
            continue
        facts = probe(path, row["kind"])
        # taken = 0 means "looked, nothing there", so it is not probed again on every start
        c.execute("UPDATE files SET width=COALESCE(?,width),height=COALESCE(?,height),duration=COALESCE(?,duration),"
                  "fps=COALESCE(?,fps),codec=COALESCE(?,codec),taken=? WHERE id=? AND size=? AND mtime=?",
                  (facts["width"], facts["height"], facts["duration"], facts["fps"], facts["codec"], facts["taken"] or 0,
                   row["id"], row["size"], row["mtime"]))
        c.commit()
