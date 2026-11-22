"""Optional, on-demand time-stamped media index. Ordinary library indexing stays unchanged."""
import collections
import subprocess
import threading
import time
from pathlib import Path

import numpy as np

from . import db, embedder, extract, media_meta

_jobs = {}
_lock = threading.Lock()


def _file(fid):
    row = db.conn().execute("SELECT id,path,name,kind,size,mtime FROM files WHERE id=? AND status='done'", (fid,)).fetchone()
    if row is None or row["kind"] not in ("video", "audio") or not Path(row["path"]).is_file():
        raise FileNotFoundError("Choose an indexed video or audio file")
    return row


def status(fid):
    row = _file(fid)
    run = db.conn().execute("SELECT * FROM moment_runs WHERE file_id=? AND size=? AND mtime=?",
                            (fid, row["size"], row["mtime"])).fetchone()
    segments = db.conn().execute("SELECT start,end FROM moments WHERE file_id=? AND size=? AND mtime=? ORDER BY start",
                                 (fid, row["size"], row["mtime"])).fetchall()
    with _lock:
        job = dict(_jobs.get(fid) or {})
    return {"status": "running" if job.get("running") else run["status"] if run else "new",
            "done": len(segments), "total": job.get("total", len(segments)),
            "error": job.get("error") or (run["error"] if run else None),
            "segments": [{"start": s["start"], "end": s["end"]} for s in segments]}


def start(fid):
    row = _file(fid)
    with _lock:
        if _jobs.get(fid, {}).get("running"):
            return {"status": "running", "done": 0, "total": _jobs[fid].get("total", 0), "segments": []}
        _jobs[fid] = {"running": True, "total": 0}
    thread = threading.Thread(target=_analyze, args=(fid, dict(row)), daemon=True, name=f"moments-{fid}")
    thread.start()
    return {"status": "running", "done": 0, "total": 0, "segments": []}


def _clip(path, kind, start_at, duration, fid, index):
    if kind == "audio":
        output = extract.TMP_DIR / f"moment_{fid}_{index}.wav"
        command = ["ffmpeg", "-y", "-loglevel", "error", "-ss", str(start_at), "-i", str(path),
                   "-t", str(duration), "-ac", "1", "-ar", "16000", str(output)]
        try:
            subprocess.run(command, capture_output=True, timeout=90, check=True)
            return embedder._norm(embedder._encode(embedder.get_model(),
                {"text": "<|audio|>", "audio": str(output)}, normalize_embeddings=True))
        finally:
            output.unlink(missing_ok=True)
    frames = []
    try:
        for offset in (0.25, 0.75):
            output = extract.TMP_DIR / f"moment_{fid}_{index}_{len(frames)}.jpg"
            at = start_at + duration * offset
            subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-ss", f"{at:.2f}", "-i", str(path),
                            "-frames:v", "1", "-vf", "scale=-2:448", str(output)],
                           capture_output=True, timeout=60, check=True)
            if output.is_file():
                frames.append(output)
        if not frames:
            raise ValueError("Could not read frames from this segment")
        item = {"text": " ".join("<|image|>" for _ in frames), "image": [str(frame) for frame in frames]}
        return embedder._norm(embedder._encode(embedder.get_model(), item, normalize_embeddings=True))
    finally:
        for frame in frames:
            frame.unlink(missing_ok=True)


def _analyze(fid, row):
    c = db.conn()
    try:
        c.execute("DELETE FROM moments WHERE file_id=?", (fid,))
        c.execute("INSERT OR REPLACE INTO moment_runs VALUES(?,?,?,?,NULL)",
                  (fid, row["size"], row["mtime"], "running"))
        c.commit()
        facts = media_meta.probe(Path(row["path"]), row["kind"])
        duration = facts["duration"]
        if not duration:
            raise ValueError("Could not read media duration")
        step = max(12 if row["kind"] == "video" else 20, duration / 100)
        total = max(1, int(np.ceil(duration / step)))
        with _lock:
            _jobs[fid]["total"] = total
        for index in range(total):
            begin = index * step
            end = min(duration, begin + step)
            vector = _clip(Path(row["path"]), row["kind"], begin, end - begin, fid, index)
            c.execute("INSERT OR REPLACE INTO moments VALUES(?,?,?,?,?,?)",
                      (fid, begin, end, np.asarray(vector, dtype=np.float32).tobytes(), row["size"], row["mtime"]))
            c.commit()
        c.execute("UPDATE moment_runs SET status='ready',error=NULL WHERE file_id=?", (fid,))
        c.commit()
    except Exception as error:
        c.execute("UPDATE moment_runs SET status='error',error=? WHERE file_id=?", (str(error)[:240], fid))
        c.commit()
        with _lock:
            _jobs[fid]["error"] = str(error)[:240]
    finally:
        with _lock:
            _jobs[fid]["running"] = False


# ---- analysing many videos in the background, one at a time, politely ----
_queue: collections.deque = collections.deque()
_worker = None
_qstats = {"total": 0, "done": 0, "current": None}


def queue(ids=None, all_media=False) -> dict:
    """Adds videos (and sounds) to the background analysis. Files already analysed, or already waiting, are skipped."""
    global _worker
    c = db.conn()
    if all_media:
        ids = [r["id"] for r in c.execute("SELECT id FROM files WHERE status='done' AND kind IN ('video','audio') AND seq_json IS NULL ORDER BY mtime DESC, id")]
    added = 0
    with _lock:
        waiting = {fid for fid, _ in _queue}
        for fid in ids or []:
            if fid in waiting:
                continue
            row = c.execute("SELECT id,path,name,kind,size,mtime FROM files WHERE id=? AND status='done' "
                            "AND kind IN ('video','audio') AND seq_json IS NULL", (fid,)).fetchone()
            if row is None:
                continue
            run = c.execute("SELECT status FROM moment_runs WHERE file_id=? AND size=? AND mtime=?", (fid, row["size"], row["mtime"])).fetchone()
            if run and run["status"] == "ready":
                continue
            _queue.append((fid, dict(row)))
            added += 1
        _qstats["total"] += added
        if added and (_worker is None or not _worker.is_alive()):
            _worker = threading.Thread(target=_work, daemon=True, name="moments-queue")
            _worker.start()
    return queue_status()


def queue_status() -> dict:
    with _lock:
        return {"pending": len(_queue), "total": _qstats["total"], "done": _qstats["done"], "current": _qstats["current"],
                "running": bool(_queue) or _qstats["current"] is not None}


def _work():
    from . import indexer
    while True:
        with _lock:
            if not _queue:
                _qstats.update(total=0, done=0, current=None)       # idle: the next batch starts its count again
                return
            fid, row = _queue.popleft()
            _qstats["current"] = row["name"]
            _jobs[fid] = {"running": True, "total": 0}
        while indexer.state.get("running"):                         # reading folders comes first
            time.sleep(2)
        try:
            _analyze(fid, row)
        except Exception:
            pass
        with _lock:
            _qstats["done"] += 1
            _qstats["current"] = None


FRAME_DIR = db.DATA_DIR / "momentframes"


def frame_at(fid: int, t: float) -> Path | None:
    """One picture from a video at a moment, made on request and kept (small)."""
    row = _file(fid)
    if row["kind"] != "video":
        return None
    FRAME_DIR.mkdir(parents=True, exist_ok=True)
    out = FRAME_DIR / f"{fid}_{int(max(t, 0))}.jpg"
    if not out.exists():
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-ss", f"{max(t, 0):.2f}", "-i", str(row["path"]),
                        "-frames:v", "1", "-vf", "scale=-2:300", str(out)], capture_output=True, timeout=60)
    return out if out.exists() else None


def search(query, limit=40):
    if not isinstance(query, str) or not query.strip():
        return []
    vector = embedder.embed_query(query.strip()[:300])
    c = db.conn()
    rows = c.execute("SELECT m.file_id,m.start,m.end,m.vec,f.name,f.kind,f.size,f.mtime "
                     "FROM moments m JOIN files f ON f.id=m.file_id "
                     "JOIN moment_runs run ON run.file_id=m.file_id "
                     "WHERE run.status='ready' AND m.size=f.size AND m.mtime=f.mtime "
                     "AND run.size=f.size AND run.mtime=f.mtime").fetchall()
    scored = []
    for row in rows:
        candidate = np.frombuffer(row["vec"], dtype=np.float32)
        if candidate.shape == vector.shape:
            scored.append((float(candidate @ vector), row))
    scored.sort(key=lambda item: -item[0])
    return [{"file_id": row["file_id"], "start": row["start"], "end": row["end"],
             "name": row["name"], "kind": row["kind"], "score": round(score, 4)}
            for score, row in scored[:max(1, min(limit, 100))]]
