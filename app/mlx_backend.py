"""Apple Silicon backend: EmbeddingGemma 2 on the GPU through MLX (mlx-community/embeddinggemma-2-5bit).

It exposes the small slice of the SentenceTransformer API that embedder.py uses (`encode`), so the rest of
File Mind doesn't care which backend is running. PyTorch is still imported for the Transformers *processor*
(image resizing, audio features) but never runs the model.

All MLX work happens on one dedicated thread: MLX streams belong to the thread that created them, and File Mind
calls the embedder from the indexer thread and from web-request threads."""
import json
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import numpy as np

_pool = ThreadPoolExecutor(max_workers=1, thread_name_prefix="mlx")


class MLXEmbedder:
    def __init__(self, path: str):
        self.path = Path(path)
        _pool.submit(self._load).result()

    def _load(self):
        import os
        import mlx.core as mx
        if os.environ.get("FILEMIND_MLX_DEVICE") == "cpu":         # no usable GPU (VMs, CI); also handy for debugging
            mx.set_default_device(mx.cpu)
        from mlx_vlm.embedding_loader import load_embedding_model
        from transformers import AutoProcessor, AutoTokenizer
        self.mx = mx
        self.model = load_embedding_model(self.path)
        self.tokenizer = AutoTokenizer.from_pretrained(str(self.path))
        self.processor = AutoProcessor.from_pretrained(str(self.path))
        self.prompts = json.loads((self.path / "config_sentence_transformers.json").read_text("utf-8"))["prompts"]
        v = self._forward(self.tokenizer(["task: search result | query: hello"], padding=True, return_tensors="np"))
        if v.shape != (1, 768) or not np.isfinite(v).all():
            raise RuntimeError(f"MLX model produced an invalid embedding (shape {v.shape})")

    # -- the SentenceTransformer-style API --------------------------------------------------------------
    def encode(self, inputs, prompt_name=None, normalize_embeddings=True, batch_size=32, **_):
        """str -> (768,) vector; list[str] -> (n, 768); {"text", "image"/"audio"} -> (768,) vector."""
        return _pool.submit(self._encode, inputs, prompt_name, batch_size).result()

    def _encode(self, inputs, prompt_name, batch_size):
        if isinstance(inputs, dict):
            return self._multimodal(inputs)[0]
        single = isinstance(inputs, str)
        texts = [inputs] if single else list(inputs)
        prefix = self.prompts.get(prompt_name, "") if prompt_name else ""
        out = []
        for i in range(0, len(texts), max(1, batch_size)):
            chunk = [prefix + t for t in texts[i:i + batch_size]]
            out.append(self._forward(self.tokenizer(chunk, padding=True, truncation=True, max_length=2048,
                                                    return_tensors="np")))
        v = np.concatenate(out) if out else np.zeros((0, 768), np.float32)
        return v[0] if single else v

    # -- internals --------------------------------------------------------------------------------------
    def _multimodal(self, d: dict) -> np.ndarray:
        kw = {}
        if d.get("image"):
            from PIL import Image
            imgs = []
            for p in d["image"] if isinstance(d["image"], (list, tuple)) else [d["image"]]:
                with Image.open(p) as im:
                    imgs.append(im.convert("RGB"))
            kw["images"] = imgs
        if d.get("audio") is not None:
            import soundfile as sf
            a, sr = sf.read(str(d["audio"]), dtype="float32")
            if a.ndim > 1:
                a = a.mean(axis=1)
            if sr != 16000:                                       # extract.audio_clip already makes 16 kHz mono
                import librosa
                a = librosa.resample(a, orig_sr=sr, target_sr=16000)
            kw["audio"] = [a]
        return self._forward(self.processor(text=d.get("text", ""), return_tensors="np", **kw))

    def _forward(self, enc) -> np.ndarray:
        mx = self.mx
        arrays = {}
        for k, v in dict(enc).items():
            a = mx.array(np.asarray(v))
            arrays[k] = a.astype(mx.bfloat16) if a.dtype == mx.float32 else a    # the model is bf16; never fp16
        emb = self.model(**arrays).text_embeds
        emb = emb.astype(mx.float32)
        mx.eval(emb)
        v = np.array(emb)
        try:
            mx.clear_cache()                                      # give the GPU buffer cache back between batches
        except AttributeError:
            pass
        return v
