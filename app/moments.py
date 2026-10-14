"""Optional, on-demand time-stamped media index. Ordinary library indexing stays unchanged."""
import subprocess
import threading
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
