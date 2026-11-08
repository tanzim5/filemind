"""Wrapper around google/embeddinggemma-2 (text, image, video, audio -> one 768d space)."""
import os
import threading
import time
from pathlib import Path

import numpy as np

from . import extract, runtime

# Weights live in ./models (or FILEMIND_MODELS, set by the portable app) so File Mind is fully
# self-contained. They are fetched once by fetch.py; after that nothing touches the network.
_MODELS = Path(os.environ.get("FILEMIND_MODELS") or Path(__file__).resolve().parent.parent / "models")
_LOCAL = _MODELS / "embeddinggemma-2"
MODEL_ID = str(_LOCAL)
REPO = "google/embeddinggemma-2"
os.environ.setdefault("HF_HUB_OFFLINE", "1")
os.environ.setdefault("TRANSFORMERS_OFFLINE", "1")
_model = None
DEVICE = ""            # what the model runs on, once loaded: shown in the app
ON_CPU = False         # True once loaded without a usable GPU: lighter settings keep indexing practical

# On a CPU the vision encoder dominates the cost of every image and video frame. The model card's default budget is
# 280 image tokens; 140 measured 2.3x faster on a 14-thread CPU with vectors 0.95-0.98 similar and identical top
# results, so a CPU-only PC reads a photo library in hours instead of days. Videos also sample fewer frames.
CPU_IMAGE_TOKENS = int(os.environ.get("FILEMIND_CPU_IMAGE_TOKENS") or 140)
CPU_VIDEO_FRAMES = int(os.environ.get("FILEMIND_CPU_VIDEO_FRAMES") or 4)
_lock = threading.Lock()


def ready() -> bool:
    return model_ready() and runtime.ready()


def model_ready() -> bool:
    return (_LOCAL / "model.safetensors").exists() and (_LOCAL / "modules.json").exists()


def get_model():
    global _model, DEVICE, ON_CPU
    with _lock:
        if _model is None:
            if not ready():
                raise RuntimeError("The AI model isn't downloaded yet")
            import torch
            from sentence_transformers import SentenceTransformer
            cuda = _cuda_works(torch)
            use_bf16 = cuda and torch.cuda.is_bf16_supported()
            dtype = torch.bfloat16 if use_bf16 else torch.float32  # never float16 (NaNs)
            device = "cuda" if cuda else "cpu"
            DEVICE = f"{torch.cuda.get_device_name(0)} (CUDA)" if cuda else "CPU"
            print(f"File Mind: the model runs on {DEVICE}", flush=True)
            m = SentenceTransformer(MODEL_ID, device=device, model_kwargs={"torch_dtype": dtype})
            ON_CPU = not cuda
            if ON_CPU:
                _set_image_tokens(m, CPU_IMAGE_TOKENS)
            _model = m
        return _model


def _set_image_tokens(m, tokens: int):
    """How many soft tokens the vision encoder spends on each image (and each video frame)."""
    proc = getattr(m[0], "processor", None)
    ip = getattr(proc, "image_processor", None)
    if ip is None or not hasattr(ip, "max_soft_tokens"):
        print("File Mind: this model has no image token setting; using its defaults", flush=True)
        return
    ip.max_soft_tokens = tokens
    for o in (ip, proc):
        if hasattr(o, "image_seq_length"):
            o.image_seq_length = tokens
    print(f"File Mind: CPU mode, {tokens} tokens per image", flush=True)


def video_frame_count() -> int:
    return CPU_VIDEO_FRAMES if ON_CPU and not os.environ.get("FILEMIND_VIDEO_FRAMES") else extract.VIDEO_FRAMES


def _cuda_works(torch) -> bool:
    """CUDA is usable: a GPU is present AND this PyTorch has kernels for it (old cards say available, then fail)."""
    try:
        return torch.cuda.is_available() and (torch.ones(1, device="cuda") + 1).item() == 2
    except Exception:
        return False


def _norm(v) -> np.ndarray:
    v = np.asarray(v, dtype=np.float32).reshape(-1)
    n = np.linalg.norm(v)
    return v / n if n > 0 else v


_queries: dict[str, np.ndarray] = {}         # recent query vectors: retyping or re-running a search costs nothing


def embed_query(text: str) -> np.ndarray:
    hit = _queries.pop(text, None)
    if hit is None:
        hit = _norm(get_model().encode(text, prompt_name="SearchQuery", normalize_embeddings=True))
        if len(_queries) >= 512:
            _queries.pop(next(iter(_queries)))
    _queries[text] = hit                        # most recent last
    return hit.copy()


def _prep_image(path: str) -> Path:
    """Normalise to a plain RGB jpg: handles animated GIFs, CMYK, palette PNGs and huge images."""
    import hashlib
    from PIL import Image
    out = extract.TMP_DIR / f"img_{hashlib.sha1(path.encode('utf-8', 'ignore')).hexdigest()[:12]}.jpg"
    with Image.open(path) as im:
        im.seek(0)
        im = im.convert("RGB")
        if max(im.size) > 1600:
            im.thumbnail((1600, 1600))
        im.save(out, "JPEG", quality=92)
    return out


def embed_image_file(path: str) -> np.ndarray:
    m = get_model()
    prepped = _prep_image(path)
    try:
        return _norm(m.encode({"text": "<|image|>", "image": [str(prepped)]}, normalize_embeddings=True))
    finally:
        prepped.unlink(missing_ok=True)


def embed_reference(path: Path, kind: str, text: str = "") -> np.ndarray:
    """One joint image/video/audio-and-text query in EmbeddingGemma 2's shared space."""
    prepared = prepare(path, kind, path.name)
    try:
        prompt = "task: search result | query: " + text.strip()
        if kind == "image":
            item = {"text": prompt + " <|image|>", "image": [prepared["image"]]}
        elif kind == "video" and prepared.get("frames"):
            item = {"text": prompt + " " + " ".join("<|image|>" for _ in prepared["frames"]), "image": prepared["frames"]}
        elif kind == "audio" and prepared.get("clip"):
            item = {"text": prompt + " <|audio|>", "audio": prepared["clip"]}
        else:
            raise ValueError("This file could not be prepared as a reference")
        return _norm(_encode(get_model(), item, normalize_embeddings=True))
    finally:
        discard(prepared)


class GPUError(RuntimeError):
    """The graphics card reported an error that did not go away when retried."""


def is_gpu_error(e: BaseException) -> bool:
    t = f"{type(e).__name__} {e}".lower()
    return "cuda" in t or "cublas" in t or "cudnn" in t or "out of memory" in t


def _encode(m, *a, **kw):
    """model.encode, retried once if the GPU hiccups (a failed CUDA call can be transient). A second failure raises
    GPUError, so a broken GPU is reported once instead of being blamed on every file."""
    try:
        return m.encode(*a, **kw)
    except Exception as e:
        if not is_gpu_error(e):
            raise
        print(f"File Mind: GPU error, retrying once: {e}", flush=True)
        try:
            import torch
            time.sleep(2)
            torch.cuda.empty_cache()
            return m.encode(*a, **kw)
        except Exception as e2:
            raise GPUError(str(e2)) from e2


def prepare(path: Path, kind: str, name: str) -> dict:
    """All the CPU work for one file (decoding, resizing, ffmpeg, reading text). Safe to run on a worker thread,
    so the model is never left waiting for the next file."""
    p = {"path": path, "kind": kind, "name": name}
    if kind == "image":
        p["image"] = str(_prep_image(str(path)))
    elif kind == "video":
        p["frames"] = extract.video_frames(path, video_frame_count())
    elif kind == "audio":
        p["clip"] = extract.audio_clip(path)
    elif kind in ("text", "code", "doc"):
        p["text"] = extract.extract_text(path, kind)
    return p


def discard(p: dict):
    """Drop a prepared file's temporary files (when it will not be embedded after all)."""
    if p.get("image"):
        Path(p["image"]).unlink(missing_ok=True)
    extract.cleanup_tmp(p["path"])


def embed_prepared(p: dict) -> tuple[np.ndarray, str]:
    """Runs the model on a prepared file. Returns (vector, mode) where mode is 'content' or 'filename'."""
    m = get_model()
    name, kind = p["name"], p["kind"]
    try:
        if kind == "image":
            return _norm(_encode(m, {"text": "<|image|>", "image": [p["image"]]}, normalize_embeddings=True)), "content"
        if kind == "video" and p.get("frames"):
            text = " ".join("<|image|>" for _ in p["frames"])
            return _norm(_encode(m, {"text": text, "image": p["frames"]}, normalize_embeddings=True)), "content"
        if kind == "audio" and p.get("clip"):
            return _norm(_encode(m, {"text": "<|audio|>", "audio": p["clip"]}, normalize_embeddings=True)), "content"
        if p.get("text", "").strip():
            doc = f"title: {name} | text: {p['text']}"
            return _norm(_encode(m, doc, normalize_embeddings=True)), "content"
        # Fallback: embed the filename only (zips, installers, empty docs, ...)
        doc = f"title: {name} | text: {Path(name).stem.replace('_', ' ').replace('-', ' ')}"
        return _norm(_encode(m, doc, normalize_embeddings=True)), "filename"
    finally:
        discard(p)


def embed_images(preps: list[dict]) -> list[tuple[np.ndarray, str]]:
    """Several prepared images through the model in one go: much better use of a GPU than one at a time."""
    m = get_model()
    try:
        items = [{"text": "<|image|>", "image": [p["image"]]} for p in preps]
        v = np.asarray(_encode(m, items, batch_size=len(items), normalize_embeddings=True))
        return [(_norm(x), "content") for x in v]
    finally:
        for p in preps:
            discard(p)


def embed_document(path: Path, kind: str, name: str) -> tuple[np.ndarray, str]:
    return embed_prepared(prepare(path, kind, name))
