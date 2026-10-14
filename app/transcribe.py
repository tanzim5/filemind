"""On-demand, local multilingual speech transcription for indexed audio files."""
import json
import threading
from pathlib import Path

import requests

from . import db, embedder

REPO = "Systran/faster-whisper-base"
MODEL_DIR = embedder._MODELS / "faster-whisper-base"
FILES = ("config.json", "model.bin", "tokenizer.json", "vocabulary.txt")
_lock = threading.Lock()
_engine_lock = threading.Lock()
_jobs = {}
_model = None


def _row(fid):
    row = db.conn().execute("SELECT path, kind, size, mtime FROM files WHERE id=?", (fid,)).fetchone()
    if row is None or row["kind"] != "audio" or not Path(row["path"]).is_file():
        raise FileNotFoundError("Audio file is missing from the library")
    return row


def _cache_path(fid, row):
    folder = db.DATA_DIR / "transcripts"
    folder.mkdir(parents=True, exist_ok=True)
    return folder / f"{fid}_{row['size']}_{int(row['mtime'])}.json"


def status(fid):
    row = _row(fid)
    cache = _cache_path(fid, row)
    if cache.exists():
        return {"status": "done", **json.loads(cache.read_text(encoding="utf-8"))}
    with _lock:
        return dict(_jobs.get(fid, {"status": "idle"}))


def start(fid):
    row = _row(fid)
    cache = _cache_path(fid, row)
    if cache.exists():
        return status(fid)
    with _lock:
        if _jobs.get(fid, {}).get("status") in ("downloading", "transcribing"):
            return dict(_jobs[fid])
        _jobs[fid] = {"status": "downloading", "progress": 0}
    threading.Thread(target=_run, args=(fid, str(row["path"]), cache), daemon=True).start()
    return {"status": "downloading", "progress": 0}


def _ensure_model(fid):
    MODEL_DIR.mkdir(parents=True, exist_ok=True)
    missing = [name for name in FILES if not (MODEL_DIR / name).exists()]
    if not missing:
        return
    with requests.Session() as session:
        session.headers["User-Agent"] = "FileMind"
        for index, name in enumerate(missing):
            out = MODEL_DIR / name
            part = out.with_name(name + ".part")
            with session.get(f"https://huggingface.co/{REPO}/resolve/main/{name}", stream=True, timeout=90) as response:
                response.raise_for_status()
                with part.open("wb") as dest:
                    for chunk in response.iter_content(1 << 20):
                        dest.write(chunk)
            part.replace(out)
            with _lock:
                _jobs[fid] = {"status": "downloading", "progress": round((index + 1) / len(missing), 2)}


def _run(fid, path, cache):
    global _model
    try:
        with _engine_lock:
            _ensure_model(fid)
            with _lock:
                _jobs[fid] = {"status": "transcribing"}
            if _model is None:
                from faster_whisper import WhisperModel
                _model = WhisperModel(str(MODEL_DIR), device="cpu", compute_type="int8", local_files_only=True)
            segments, info = _model.transcribe(path, beam_size=5, vad_filter=True, task="transcribe")
            lines = [{"start": round(segment.start, 2), "end": round(segment.end, 2), "text": segment.text.strip()}
                     for segment in segments if segment.text.strip()]
            result = {"language": info.language, "segments": lines, "text": " ".join(line["text"] for line in lines)}
            cache.write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")
        with _lock:
            _jobs[fid] = {"status": "done", **result}
    except Exception as exc:
        with _lock:
            _jobs[fid] = {"status": "error", "error": f"{type(exc).__name__}: {exc}"}
