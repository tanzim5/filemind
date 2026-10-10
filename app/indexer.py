"""Folder scanning + background embedding job."""
import os
import threading
import time
import traceback
from pathlib import Path

import numpy as np

from . import colors, embedder, extract
from .db import conn

SKIP_DIRS = {"node_modules", ".git", "__pycache__", "$RECYCLE.BIN", "System Volume Information",
             ".venv", "venv"}
SKIP_FILES = {"desktop.ini", "thumbs.db", ".ds_store"}
SKIP_EXT = {".crdownload", ".part", ".tmp", ".partial"}   # unfinished downloads

MEDIA_KINDS = ("image", "video", "audio")   # only things with something to look at or listen to
MIN_BYTES = 4096                              # skip empty / stub files

state = {
    "running": False, "stop": False, "root_id": None, "root": None,
    "phase": "idle", "total": 0, "done": 0, "errors": 0, "current": "", "started": 0.0,
    "message": "",
}
_thread: threading.Thread | None = None

# cache of all vectors for search
_cache = {"ids": None, "mat": None, "dirty": True}
_cache_lock = threading.Lock()


def invalidate():
    _cache["dirty"] = True


def matrix():
    with _cache_lock:
        if _cache["dirty"] or _cache["mat"] is None:
            rows = conn().execute(
                "SELECT id, vec FROM files WHERE status='done' AND vec IS NOT NULL").fetchall()
            if rows:
                _cache["ids"] = np.array([r["id"] for r in rows])
                _cache["mat"] = np.vstack([np.frombuffer(r["vec"], dtype=np.float32) for r in rows])
            else:
                _cache["ids"] = np.array([], dtype=int)
                _cache["mat"] = np.zeros((0, 768), dtype=np.float32)
            _cache["dirty"] = False
        return _cache["ids"], _cache["mat"]


def scan(root_id: int, root: str) -> int:
    """Walk the folder, register new/changed files, drop vanished ones. Returns file count."""
    c = conn()
    seen = set()
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for fn in filenames:
            low = fn.lower()
            ext = os.path.splitext(fn)[1].lower()
            if low in SKIP_FILES or ext in SKIP_EXT:
                continue
            p = os.path.join(dirpath, fn)
            try:
                st = os.stat(p)
            except OSError:
                continue
            kind = extract.kind_of(ext)
            if kind not in MEDIA_KINDS or st.st_size < MIN_BYTES:
                continue                      # zips, installers, docs, code... are not indexed
            seen.add(p)
            row = c.execute("SELECT id, mtime, size, status FROM files WHERE path=?", (p,)).fetchone()
            if row is None:
                c.execute(
                    "INSERT INTO files(root_id,path,name,ext,kind,size,mtime,status) VALUES(?,?,?,?,?,?,?,'pending')",
                    (root_id, p, fn, ext, kind, st.st_size, st.st_mtime))
            elif row["mtime"] != st.st_mtime or row["size"] != st.st_size:
                c.execute("UPDATE files SET mtime=?, size=?, status='pending', vec=NULL WHERE id=?",
                          (st.st_mtime, st.st_size, row["id"]))
    existing = c.execute("SELECT id, path FROM files WHERE root_id=?", (root_id,)).fetchall()
    gone = [r["id"] for r in existing if r["path"] not in seen]
    for fid in gone:
        c.execute("DELETE FROM files WHERE id=?", (fid,))
        c.execute("DELETE FROM project_files WHERE file_id=?", (fid,))
        c.execute("DELETE FROM proposed_names WHERE file_id=?", (fid,))
    c.commit()
    invalidate()
    return len(seen)


def _run(root_id: int, root: str):
    c = conn()
    try:
        state.update(phase="scanning", message="Scanning folder…", done=0, errors=0, total=0)
        scan(root_id, root)
        pending = c.execute(
            "SELECT id, path, name, kind FROM files WHERE root_id=? AND status='pending' "
            "ORDER BY CASE kind WHEN 'image' THEN 0 WHEN 'audio' THEN 1 ELSE 2 END, id",
            (root_id,)).fetchall()
        # regenerate thumbnails made by older versions (different size)
        for r in c.execute("SELECT id, path, kind, thumb FROM files WHERE root_id=? AND status='done' "
                           "AND kind IN ('image','video')", (root_id,)).fetchall():
            p = Path(r["path"])
            if p.exists() and r["thumb"] != extract._thumb_path(p).name:
                t = extract.make_thumb(p, r["kind"])
                if t:
                    c.execute("UPDATE files SET thumb=? WHERE id=?", (t, r["id"]))
        c.commit()
        if not pending:                       # nothing new: finish quietly, no model load, no banner
            state.update(phase="idle", message="Up to date.")
            return
        state.update(phase="loading", total=len(pending), message="Loading the embedding model…")
        embedder.get_model()
        state.update(phase="embedding", message="Embedding files…")
        for i, r in enumerate(pending):
            if state["stop"]:
                state["message"] = "Stopped. Progress is saved; press Index to resume."
                break
            state["current"] = r["name"]
            p = Path(r["path"])
            try:
                if not p.exists():
                    c.execute("DELETE FROM files WHERE id=?", (r["id"],))
                    state["done"] += 1
                    continue
                thumb = extract.make_thumb(p, r["kind"])
                if r["kind"] in ("image", "video") and not thumb:
                    raise ValueError("Could not be decoded (corrupt or unsupported)")   # hidden from the library
                vec, mode = embedder.embed_document(p, r["kind"], r["name"])
                dom = extract.dominant_color(thumb) if thumb else None
                pal = _pal(thumb)
                c.execute("UPDATE files SET vec=?, embed_mode=?, thumb=?, dom=?, pal=?, status='done', error=NULL WHERE id=?",
                          (vec.astype(np.float32).tobytes(), mode, thumb, dom, pal, r["id"]))
            except Exception as e:  # keep going; one bad file must not kill the job
                traceback.print_exc()
                c.execute("UPDATE files SET status='error', error=? WHERE id=?", (str(e)[:300], r["id"]))
                state["errors"] += 1
            state["done"] += 1
            if state["done"] % 20 == 0:
                c.commit()
                invalidate()
        else:
            state["message"] = "Done."
        c.commit()
        invalidate()
        state["phase"] = "idle"
    except Exception as e:
        traceback.print_exc()
        state.update(phase="error", message=f"Failed: {e}")
    finally:
        state["running"] = False
        state["current"] = ""
        if _queue and not state["stop"]:
            start(*_queue.pop(0))


def _pal(thumb):
    if not thumb:
        return None
    try:
        return colors.palette(extract.THUMB_DIR / thumb)
    except Exception:
        return "{}"


def backfill_colors():
    """Fills in colours for files indexed before colours existed (reads small thumbnails only)."""
    c = conn()
    from .db import DATA_DIR
    ver = DATA_DIR / "palette_version"
    if not ver.exists() or ver.read_text().strip() != colors.PALETTE_VERSION:   # colour rules changed: recompute
        c.execute("UPDATE files SET pal=NULL")
        c.commit()
        ver.write_text(colors.PALETTE_VERSION)
    rows = c.execute("SELECT id, thumb, dom FROM files WHERE status='done' AND thumb IS NOT NULL AND thumb!='' "
                     "AND (dom IS NULL OR pal IS NULL)").fetchall()
    for i, r in enumerate(rows):
        dom = r["dom"] if r["dom"] is not None else (extract.dominant_color(r["thumb"]) or "")
        c.execute("UPDATE files SET dom=?, pal=? WHERE id=?", (dom, _pal(r["thumb"]), r["id"]))
        if i % 200 == 0:
            c.commit()
    c.commit()


_queue: list[tuple[int, str]] = []


def start(root_id: int, root: str) -> bool:
    """Starts reading a folder, or queues it behind the one being read."""
    global _thread
    if state["running"]:
        if (root_id, root) not in _queue and state["root_id"] != root_id:
            _queue.append((root_id, root))
        return False
    state.update(running=True, stop=False, root_id=root_id, root=root, started=time.time())
    _thread = threading.Thread(target=_run, args=(root_id, root), daemon=True)
    _thread.start()
    return True


def stop():
    state["stop"] = True
