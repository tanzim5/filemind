"""Wrapper around google/embeddinggemma-2 (text, image, video, audio -> one 768d space)."""
import importlib.util
import os
import platform
import sys
import threading
import time
from pathlib import Path

import numpy as np

from . import extract, runtime

# Which engine runs the model: "mlx" on Apple Silicon (GPU, 5-bit weights), otherwise "torch" (CUDA if present, else
# CPU). FILEMIND_BACKEND=torch forces PyTorch on a Mac.
BACKEND = (os.environ.get("FILEMIND_BACKEND") or
           ("mlx" if sys.platform == "darwin" and platform.machine() == "arm64"
            and (getattr(sys, "frozen", False) or importlib.util.find_spec("mlx_vlm") is not None) else "torch"))

# Weights live in ./models (or FILEMIND_MODELS, set by the portable app) so File Mind is fully
# self-contained. They are fetched once by fetch.py; after that nothing touches the network.
_MODELS = Path(os.environ.get("FILEMIND_MODELS") or Path(__file__).resolve().parent.parent / "models")
_LOCAL = _MODELS / ("embeddinggemma-2-mlx" if BACKEND == "mlx" else "embeddinggemma-2")
MODEL_ID = str(_LOCAL)
REPO = "mlx-community/embeddinggemma-2-5bit" if BACKEND == "mlx" else "google/embeddinggemma-2"
os.environ.setdefault("HF_HUB_OFFLINE", "1")
os.environ.setdefault("TRANSFORMERS_OFFLINE", "1")
_model = None
DEVICE = ""            # what the model runs on, once loaded: shown in the app
_lock = threading.Lock()


def ready() -> bool:
    return model_ready() and runtime.ready()


def model_ready() -> bool:
    return (_LOCAL / "model.safetensors").exists() and (_LOCAL / "modules.json").exists()


def get_model():
    global _model, DEVICE
    with _lock:
        if _model is None:
            if not ready():
                raise RuntimeError("The AI model isn't downloaded yet")
            if BACKEND == "mlx":
                from .mlx_backend import MLXEmbedder
                try:
                    _model = MLXEmbedder(MODEL_ID)
                    DEVICE = "Apple GPU (MLX)"
                except Exception as e:
                    raise RuntimeError(f"Could not start the Apple GPU (MLX) engine: {e}. "
                                       "Set FILEMIND_BACKEND=torch to use PyTorch instead.") from e
                return _model
            import torch
            from sentence_transformers import SentenceTransformer
            cuda = _cuda_works(torch)
            use_bf16 = cuda and torch.cuda.is_bf16_supported()
            dtype = torch.bfloat16 if use_bf16 else torch.float32  # never float16 (NaNs)
            device = "cuda" if cuda else "cpu"
            DEVICE = f"{torch.cuda.get_device_name(0)} (CUDA)" if cuda else "CPU"
            print(f"File Mind: the model runs on {DEVICE}", flush=True)
            _model = SentenceTransformer(MODEL_ID, device=device,
                                         model_kwargs={"torch_dtype": dtype})
        return _model


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


def embed_query(text: str) -> np.ndarray:
    m = get_model()
    return _norm(m.encode(text, prompt_name="SearchQuery", normalize_embeddings=True))


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
        p["frames"] = extract.video_frames(path)
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
        if BACKEND == "mlx":                                       # the MLX engine takes one multimodal input at a time
            return [(_norm(_encode(m, i, normalize_embeddings=True)), "content") for i in items]
        v = np.asarray(_encode(m, items, batch_size=len(items), normalize_embeddings=True))
        return [(_norm(x), "content") for x in v]
    finally:
        for p in preps:
            discard(p)


def embed_document(path: Path, kind: str, name: str) -> tuple[np.ndarray, str]:
    return embed_prepared(prepare(path, kind, name))
