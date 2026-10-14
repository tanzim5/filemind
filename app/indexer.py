"""Folder scanning + background embedding job."""
import collections
import os
import threading
import time
import traceback
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import numpy as np

from . import colors, embedder, extract, runtime
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
    "message": "", "device": "",
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
                c.execute("UPDATE files SET mtime=?, size=?, status='pending', vec=NULL, width=NULL, height=NULL, duration=NULL, fps=NULL, codec=NULL WHERE id=?",
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
        c.execute("UPDATE files SET status='pending', error=NULL WHERE root_id=? AND status='error' AND "
                  "(error LIKE '%CUDA%' OR error LIKE '%out of memory%')", (root_id,))   # blamed on the GPU, not the file
        c.commit()
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
        state.update(phase="embedding", device=embedder.DEVICE, message=f"Embedding files on {embedder.DEVICE or 'this computer'}…")
        if runtime.WHEELS and embedder.DEVICE == "CPU":
            print("File Mind: NVIDIA edition, but CUDA is not usable on this PC (driver or GPU too old?); using the CPU", flush=True)
        # CPU work (decoding, thumbnails, ffmpeg) runs on a few worker threads, a handful of files ahead of the model,
        # so the GPU is never left waiting. Database writes stay on this thread.
        def work(r):
            p = Path(r["path"])
            if not p.exists():
                return None
            thumb = extract.make_thumb(p, r["kind"])
            if r["kind"] in ("image", "video") and not thumb:
                raise ValueError("Could not be decoded (corrupt or unsupported)")   # hidden from the library
            return thumb, embedder.prepare(p, r["kind"], r["name"])

        ahead = collections.deque()
        nxt = 0
        pool = ThreadPoolExecutor(max_workers=3, thread_name_prefix="prep")
        try:
            while (nxt < len(pending) or ahead) and not state["stop"]:
                while nxt < len(pending) and len(ahead) < 16:
                    ahead.append((pending[nxt], pool.submit(work, pending[nxt])))
                    nxt += 1
                # take the next file, plus the images right behind it (up to 8): they go through the model together
                group = [ahead.popleft()]
                while ahead and group[0][0]["kind"] == "image" and ahead[0][0]["kind"] == "image" and len(group) < 8:
                    group.append(ahead.popleft())
                ready = []                                # (row, thumb, prepared)
                for r, fut in group:
                    state["current"] = r["name"]
                    try:
                        got = fut.result()
                        if got is None:
                            c.execute("DELETE FROM files WHERE id=?", (r["id"],))
                            state["done"] += 1
                        else:
                            ready.append((r, *got))
                    except Exception as e:                # keep going; one bad file must not kill the job
                        traceback.print_exc()
                        c.execute("UPDATE files SET status='error', error=? WHERE id=?", (str(e)[:300], r["id"]))
                        state["errors"] += 1
                        state["done"] += 1
                results = []                              # one (vector, mode) or an Exception per ready file
                if len(ready) > 1:
                    try:
                        results = embedder.embed_images([x[2] for x in ready])
                    except embedder.GPUError:
                        raise
                    except Exception:
                        traceback.print_exc()             # fall back to one at a time so one bad image can't sink the rest
                        results = []
                        for x in ready:
                            embedder.discard(x[2])
                        ready = [(r, t, embedder.prepare(Path(r["path"]), r["kind"], r["name"])) for r, t, _ in ready]
                if not results:
                    for r, thumb, prep in ready:
                        try:
                            results.append(embedder.embed_prepared(prep))
                        except embedder.GPUError:
                            raise
                        except Exception as e:
                            traceback.print_exc()
                            results.append(e)
                for (r, thumb, _), res in zip(ready, results):
                    if isinstance(res, Exception):
                        c.execute("UPDATE files SET status='error', error=? WHERE id=?", (str(res)[:300], r["id"]))
                        state["errors"] += 1
                    else:
                        vec, mode = res
                        dom = extract.dominant_color(thumb) if thumb else None
                        pal = _pal(thumb)
                        c.execute("UPDATE files SET vec=?, embed_mode=?, thumb=?, dom=?, pal=?, status='done', error=NULL WHERE id=?",
                                  (vec.astype(np.float32).tobytes(), mode, thumb, dom, pal, r["id"]))
                    state["done"] += 1
                if state["done"] // 20 != (state["done"] - len(group)) // 20:
                    c.commit()
                    invalidate()
        except embedder.GPUError as e:                    # the files stay 'pending': nothing is blamed on them
            print(f"File Mind: stopping, the GPU keeps failing: {e}", flush=True)
            state.update(phase="error", stop=True, message=(
                "The graphics card reported an error (" + str(e)[:80] + "). Your progress is saved. "
                "Close File Mind completely and open it again to continue."))
        finally:
            pool.shutdown(wait=True, cancel_futures=True)
            for _, fut in ahead:                       # prepared but never embedded (stopped): drop their temp files
                if fut.done() and not fut.cancelled() and not fut.exception() and fut.result():
                    embedder.discard(fut.result()[1])
        if state["phase"] == "error":
            pass
        elif state["stop"]:
            state["message"] = "Stopped. Progress is saved; press Index to resume."
        else:
            state["message"] = "Done."
        c.commit()
        invalidate()
        if state["phase"] != "error":
            state["phase"] = "idle"
    except Exception as e:
        traceback.print_exc()
        state.update(phase="error", message=f"Failed: {e}")
    finally:
        state["running"] = False
        state["current"] = ""
        if state["phase"] != "error":
            from . import media_meta
            threading.Thread(target=media_meta.backfill, daemon=True).start()
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
