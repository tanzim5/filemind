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


# --------------------------------------------------------------- dictation: speech -> a typed search
DICTATION_REPO = "Systran/faster-whisper-small"          # clearly better than base for short spoken queries
DICTATION_DIR = embedder._MODELS / "faster-whisper-small"
_dictation = None
_dictation_lock = threading.Lock()


def dictation_ready():
    return all((DICTATION_DIR / name).exists() for name in FILES)


# Download progress for the voice model, so Settings can fetch it ahead of the first voice search.
dictation_state = {"running": False, "done": 0, "total": 487_000_000, "error": ""}


def dictation_status():
    return {**dictation_state, "ready": dictation_ready()}


def prepare_dictation():
    """Downloads the voice model in the background; safe to call repeatedly."""
    if dictation_ready() or dictation_state["running"]:
        return dictation_status()
    dictation_state.update(running=True, done=0, error="")

    def run():
        try:
            _ensure_dictation()
        except Exception as exc:  # network trouble is reported, never raised into the server
            dictation_state["error"] = str(exc)
        finally:
            dictation_state["running"] = False
    threading.Thread(target=run, daemon=True).start()
    return dictation_status()


def _ensure_dictation():
    DICTATION_DIR.mkdir(parents=True, exist_ok=True)
    with requests.Session() as session:
        session.headers["User-Agent"] = "FileMind"
        for name in FILES:
            out = DICTATION_DIR / name
            if out.exists():
                continue
            part = out.with_name(name + ".part")
            with session.get(f"https://huggingface.co/{DICTATION_REPO}/resolve/main/{name}", stream=True, timeout=90) as r:
                r.raise_for_status()
                with part.open("wb") as dest:
                    for chunk in r.iter_content(1 << 20):
                        dictation_state["done"] += len(chunk)
                        dest.write(chunk)
            part.replace(out)


def dictate(wav_path: str) -> dict:
    """Short spoken query -> text. Greedy decoding with voice-activity trimming keeps it near real time."""
    global _dictation
    with _dictation_lock:
        if _dictation is None:
            _ensure_dictation()
            from faster_whisper import WhisperModel
            cuda = embedder.DEVICE != "CPU"
            _dictation = WhisperModel(str(DICTATION_DIR), device="cuda" if cuda else "cpu",
                                      compute_type="float16" if cuda else "int8", local_files_only=True)
        segments, info = _dictation.transcribe(wav_path, beam_size=1, vad_filter=True, without_timestamps=True,
                                               condition_on_previous_text=False)
        text = " ".join(s.text.strip() for s in segments).strip()
    return {"text": text, "language": info.language}
