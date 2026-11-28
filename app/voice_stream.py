"""Live dictation: the app streams microphone audio here, and the words appear while the person is still speaking.

A small model re-reads the growing audio every ~0.4 s (the words that two readings agree on become "stable", the rest is
"tentative"). When speech ends, a bigger model reads the utterance once for the final text, and the question parser turns it
into filters. Nothing leaves this PC."""
import asyncio
import json
import logging
import threading
import time

import numpy as np
import requests

from . import embedder, transcribe

log = logging.getLogger("filemind.voice")

SR = 16000
PARTIAL_EVERY = 0.4            # seconds between live readings
MIN_NEW_AUDIO = 0.3            # seconds of new sound before another reading is worth it
END_SILENCE = 0.7              # seconds of quiet after speech that end the utterance
NO_SPEECH_GIVE_UP = 6.0        # seconds without any speech before giving up
MAX_SECONDS = 15.0             # one question is never longer
BUFFER_CAP = 20 * SR
PROMPT = "Searching my files: photos, videos, screenshots, songs, 4K, sunset, interview, Midjourney, ChatGPT, Veo, Kling, Runway, Sora."
HOTWORDS = "Midjourney ChatGPT Veo Kling Runway Sora ComfyUI 4K 1080p"

PARTIAL_REPO, PARTIAL_DIR = transcribe.REPO, transcribe.MODEL_DIR                          # "base": quick, for the live words
FINAL_REPO, FINAL_DIR = transcribe.DICTATION_REPO, transcribe.DICTATION_DIR                # "small": careful, for the final text
FILES = transcribe.FILES


def common_prefix(a: list[str], b: list[str]) -> int:
    n = 0
    for x, y in zip(a, b):
        if x.lower().strip(".,!?") != y.lower().strip(".,!?"):
            break
        n += 1
    return n


class Utterance:
    """One spoken question being heard. The recogniser and the speech detector are passed in, so this is easy to test."""

    def __init__(self, partial_fn, vad_fn, final_fn=None):
        self.partial_fn, self.vad_fn, self.final_fn = partial_fn, vad_fn, final_fn
        self.buf = np.zeros(0, np.float32)
        self.heard = False
        self.speech_now = False
        self.first_speech = 0
        self.last_speech = 0
        self._prev: list[str] = []
        self._stable = 0
        self._decoded_upto = 0
        self.stable_text = ""
        self.tentative_text = ""

    @property
    def seconds(self) -> float:
        return len(self.buf) / SR

    def feed(self, pcm: bytes):
        if not pcm:
            return
        chunk = np.frombuffer(pcm[: len(pcm) // 2 * 2], dtype="<i2").astype(np.float32) / 32768.0
        self.buf = np.concatenate([self.buf, chunk])[-BUFFER_CAP:]

    def probe(self) -> tuple[bool, bool]:
        """Looks at the last 1.5 s: (speaking right now, speech has ended)."""
        window = self.buf[-int(1.5 * SR):]
        base = len(self.buf) - len(window)
        segments = self.vad_fn(window) if len(window) >= SR // 4 else []
        if segments:
            seg_start, seg_end = base + int(segments[0]["start"]), base + int(segments[-1]["end"])
            if seg_end - seg_start >= 0.15 * SR or self.heard:
                if not self.heard:
                    self.heard, self.first_speech = True, seg_start
                self.last_speech = max(self.last_speech, seg_end)
        now = bool(segments) and (len(self.buf) - (base + int(segments[-1]["end"]))) < 0.3 * SR
        self.speech_now = now
        ended = self.heard and (len(self.buf) - self.last_speech) >= END_SILENCE * SR
        return now, ended

    def gave_up(self) -> bool:
        return not self.heard and self.seconds >= NO_SPEECH_GIVE_UP

    def due(self) -> bool:
        return self.heard and (len(self.buf) - self._decoded_upto) >= MIN_NEW_AUDIO * SR

    def read_partial(self):
        """Re-reads what was said so far; returns (stable, tentative) or None when nothing changed."""
        self._decoded_upto = len(self.buf)
        audio = self.buf[max(0, self.first_speech - SR // 5):]
        words = self.partial_fn(audio).split()
        self._stable = min(max(self._stable, common_prefix(self._prev, words)), len(words))
        self._prev = words
        stable, tentative = " ".join(words[: self._stable]), " ".join(words[self._stable:])
        if (stable, tentative) == (self.stable_text, self.tentative_text):
            return None
        self.stable_text, self.tentative_text = stable, tentative
        return stable, tentative

    def final_text(self) -> str:
        if not self.heard:
            return ""
        audio = self.buf[max(0, self.first_speech - SR // 5): self.last_speech + SR // 3]
        if self.final_fn is not None and len(audio) > SR // 4:
            try:
                text = self.final_fn(audio)
                if text.strip():
                    return text.strip()
            except Exception:
                log.exception("final reading failed; using the live words")
        return " ".join(w for w in (self.stable_text + " " + self.tentative_text).split()).strip() or self.partial_fn(audio).strip()


# ---- the models -------------------------------------------------------------------------------------------------
_models: dict = {}
_models_lock = threading.Lock()
_use_lock = threading.Lock()
_idle_timer: threading.Timer | None = None
_active = False

state = {"running": False, "done": 0, "total": 640_000_000, "error": ""}


def ready_partial() -> bool:
    return all((PARTIAL_DIR / n).exists() for n in FILES)


def ready_final() -> bool:
    return all((FINAL_DIR / n).exists() for n in FILES)


def status() -> dict:
    return {**state, "ready": ready_partial(), "final_ready": ready_final()}


def prepare():
    """Downloads the two voice models in the background; safe to call repeatedly."""
    if state["running"] or (ready_partial() and ready_final()):
        return status()
    state.update(running=True, done=0, error="")

    def run():
        try:
            with requests.Session() as session:
                session.headers["User-Agent"] = "FileMind"
                for repo, folder in ((PARTIAL_REPO, PARTIAL_DIR), (FINAL_REPO, FINAL_DIR)):
                    folder.mkdir(parents=True, exist_ok=True)
                    for name in FILES:
                        out = folder / name
                        if out.exists():
                            continue
                        part = out.with_name(name + ".part")
                        with session.get(f"https://huggingface.co/{repo}/resolve/main/{name}", stream=True, timeout=90) as r:
                            r.raise_for_status()
                            with part.open("wb") as dest:
                                for chunk in r.iter_content(1 << 20):
                                    state["done"] += len(chunk)
                                    dest.write(chunk)
                        part.replace(out)
        except Exception as exc:                      # network trouble is reported to the app, never raised into the server
            state["error"] = str(exc)
        finally:
            state["running"] = False
    threading.Thread(target=run, daemon=True).start()
    return status()


def _load(which: str):
    with _models_lock:
        if which not in _models:
            from faster_whisper import WhisperModel
            folder = PARTIAL_DIR if which == "partial" else FINAL_DIR
            cuda = transcribe._gpu()
            _models[which] = WhisperModel(str(folder), device="cuda" if cuda else "cpu",
                                          compute_type="float16" if cuda else "int8", local_files_only=True)
        return _models[which]


def unload():
    """Frees the models' memory (about a gigabyte) once nobody has spoken for a while."""
    with _models_lock:
        _models.clear()


def _touch():
    global _idle_timer
    if _idle_timer is not None:
        _idle_timer.cancel()
    _idle_timer = threading.Timer(300, unload)
    _idle_timer.daemon = True
    _idle_timer.start()


class Recogniser:
    """Wraps the two Whisper models as the plain functions Utterance needs."""

    def __init__(self):
        self.language = None

    def partial(self, audio: np.ndarray) -> str:
        with _use_lock:
            model = _load("partial")
            segments, info = model.transcribe(audio, beam_size=1, without_timestamps=True, condition_on_previous_text=False,
                                              temperature=0.0, vad_filter=False, language=self.language, initial_prompt=PROMPT,
                                              hotwords=HOTWORDS)
            text = " ".join(s.text.strip() for s in segments).strip()
            if self.language is None and text:
                self.language = info.language
            _touch()
            return text

    def final(self, audio: np.ndarray) -> str:
        if not ready_final():
            return self.partial(audio)
        with _use_lock:
            model = _load("final")
            segments, _ = model.transcribe(audio, beam_size=5, without_timestamps=True, condition_on_previous_text=False,
                                           temperature=0.0, vad_filter=False, language=self.language, initial_prompt=PROMPT,
                                           hotwords=HOTWORDS)
            _touch()
            return " ".join(s.text.strip() for s in segments).strip()


def speech_segments(audio: np.ndarray):
    from faster_whisper.vad import VadOptions, get_speech_timestamps
    return get_speech_timestamps(audio, VadOptions(threshold=0.5, min_silence_duration_ms=250), sampling_rate=SR)


# ---- the WebSocket conversation -----------------------------------------------------------------------------------
async def run(ws, understand_fn, make_recogniser=Recogniser, vad_fn=speech_segments):
    """Protocol. App -> engine: binary = little-endian int16 mono 16 kHz audio; text {"type": "start"|"stop"|"cancel", ...}.
    Engine -> app: partial {stable, tentative}, speech {on}, final {text, understood}, error {code, message}."""
    global _active
    if _active:
        await ws.send_json({"type": "error", "code": "busy", "message": "Already listening."})
        return
    if not ready_partial():
        await ws.send_json({"type": "error", "code": "model_missing", "message": "The voice model needs a one-time download."})
        return
    _active = True
    try:
        rec = make_recogniser()
        utt = Utterance(rec.partial, vad_fn, rec.final)
        ctx = {"now": time.time(), "utc_offset_min": 0, "day_first": True}
        flags = {"stop": False, "cancel": False}

        async def receiver():
            while True:
                msg = await ws.receive()
                if msg.get("type") == "websocket.disconnect":
                    flags["cancel"] = True
                    return
                if msg.get("bytes"):
                    utt.feed(msg["bytes"])
                elif msg.get("text"):
                    try:
                        data = json.loads(msg["text"])
                    except ValueError:
                        continue
                    kind = data.get("type")
                    if kind == "start":
                        ctx.update({k: data[k] for k in ("now", "utc_offset_min", "day_first") if k in data})
                        if data.get("lang"):
                            rec.language = data["lang"]
                    elif kind == "stop":
                        flags["stop"] = True
                    elif kind == "cancel":
                        flags["cancel"] = True
                        return

        rx = asyncio.create_task(receiver())
        last_speaking, last_read = False, 0.0
        started = time.monotonic()
        try:
            while not flags["cancel"]:
                await asyncio.sleep(0.1)
                speaking, ended = await asyncio.to_thread(utt.probe)
                if speaking != last_speaking:
                    last_speaking = speaking
                    await ws.send_json({"type": "speech", "on": speaking})
                if utt.due() and time.monotonic() - last_read >= PARTIAL_EVERY:
                    t0 = time.monotonic()
                    got = await asyncio.to_thread(utt.read_partial)
                    last_read = time.monotonic()
                    log.info("voice partial in %.0f ms", (last_read - t0) * 1000)
                    if got:
                        await ws.send_json({"type": "partial", "stable": got[0], "tentative": got[1]})
                if ended or flags["stop"] or utt.gave_up() or utt.seconds >= MAX_SECONDS or time.monotonic() - started > MAX_SECONDS + 5:
                    break
            if flags["cancel"]:
                return
            t0 = time.monotonic()
            text = await asyncio.to_thread(utt.final_text)
            log.info("voice final in %.0f ms", (time.monotonic() - t0) * 1000)
            understood = understand_fn(text, ctx) if text else None
            await ws.send_json({"type": "final", "text": text, "understood": understood})
        finally:
            rx.cancel()
    except Exception as exc:                          # a failed reading must not kill the engine
        log.exception("voice stream failed")
        try:
            await ws.send_json({"type": "error", "code": "internal", "message": type(exc).__name__})
        except Exception:
            pass
    finally:
        _active = False
