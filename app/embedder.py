"""Wrapper around google/embeddinggemma-2 (text, image, video, audio -> one 768d space)."""
import importlib.util
import os
import platform
import sys
import threading
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
_lock = threading.Lock()


def ready() -> bool:
    return model_ready() and runtime.ready()


def model_ready() -> bool:
    return (_LOCAL / "model.safetensors").exists() and (_LOCAL / "modules.json").exists()


def get_model():
    global _model
    with _lock:
        if _model is None:
            if not ready():
                raise RuntimeError("The AI model isn't downloaded yet")
            if BACKEND == "mlx":
                from .mlx_backend import MLXEmbedder
                try:
                    _model = MLXEmbedder(MODEL_ID)
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


def embed_document(path: Path, kind: str, name: str) -> tuple[np.ndarray, str]:
    """Returns (vector, mode) where mode is 'content' or 'filename'."""
    m = get_model()

    if kind == "image":
        return embed_image_file(str(path)), "content"

    if kind == "video":
        frames = extract.video_frames(path)
        try:
            if frames:
                text = " ".join("<|image|>" for _ in frames)
                v = m.encode({"text": text, "image": frames}, normalize_embeddings=True)
                return _norm(v), "content"
        finally:
            extract.cleanup_tmp(path)

    if kind == "audio":
        clip = extract.audio_clip(path)
        try:
            if clip:
                v = m.encode({"text": "<|audio|>", "audio": clip}, normalize_embeddings=True)
                return _norm(v), "content"
        finally:
            extract.cleanup_tmp(path)

    if kind in ("text", "code", "doc"):
        text = extract.extract_text(path, kind)
        if text.strip():
            doc = f"title: {name} | text: {text}"
            return _norm(m.encode(doc, normalize_embeddings=True)), "content"

    # Fallback: embed the filename only (zips, installers, empty docs, ...)
    doc = f"title: {name} | text: {Path(name).stem.replace('_', ' ').replace('-', ' ')}"
    return _norm(m.encode(doc, normalize_embeddings=True)), "filename"
