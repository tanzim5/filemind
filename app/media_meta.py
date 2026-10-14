"""Small, local media facts used by filters; no model inference is required."""
import json
import subprocess
import time
from pathlib import Path

from PIL import Image

from . import db


def probe(path: Path, kind: str) -> dict:
    out = {"width": None, "height": None, "duration": None, "fps": None, "codec": None}
    try:
        if kind == "image":
            with Image.open(path) as image:
                out["width"], out["height"] = image.size
            return out
        if kind not in ("video", "audio"):
            return out
        result = subprocess.run(
            ["ffprobe", "-v", "error", "-show_entries",
             "format=duration:stream=codec_type,codec_name,width,height,r_frame_rate",
             "-of", "json", str(path)], capture_output=True, text=True, timeout=15, check=True)
        data = json.loads(result.stdout)
        out["duration"] = float(data.get("format", {}).get("duration") or 0) or None
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
                     "((kind='image' AND width IS NULL) OR (kind IN ('video','audio') AND duration IS NULL))").fetchall()
    for row in rows:
        while indexer.state["running"]:
            time.sleep(1)
        path = Path(row["path"])
        if not path.is_file():
            continue
        facts = probe(path, row["kind"])
        c.execute("UPDATE files SET width=?,height=?,duration=?,fps=?,codec=? WHERE id=? AND size=? AND mtime=?",
                  (facts["width"], facts["height"], facts["duration"], facts["fps"], facts["codec"],
                   row["id"], row["size"], row["mtime"]))
        c.commit()
