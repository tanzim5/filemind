"""Search ideas drawn from the library itself.

A pool of everyday descriptions is embedded once (cached on disk). Each is scored against every
indexed file: an idea is good when a handful of files match it far better than the rest of the
library does. Picks are kept diverse (ideas that find the same files are not shown together).
"Related" ideas rank the pool against the centre of a set of results."""
import hashlib
import threading

import numpy as np

from . import embedder, indexer
from .db import DATA_DIR

POOL = [
    # people
    "a man's face close-up", "portrait of a woman", "person smiling at the camera", "a man without a shirt",
    "someone talking to the camera", "a crowd of people", "a child playing", "hands holding something",
    "person walking down a street", "athlete running", "dancer in motion", "a couple together", "man in a suit",
    "fashion model posing", "group photo of friends", "a selfie", "close-up of eyes", "side profile of a face",
    "person wearing sunglasses", "traditional clothing", "gym workout", "person sitting at a desk",
    # products
    "sneakers floating in the air", "shoe product shot", "product on a green background", "perfume bottle",
    "watch close-up", "phone on a table", "food on a plate", "a drink in a glass", "car on the road",
    "packaging design", "jewelry", "cosmetics and makeup", "headphones", "a handbag", "car interior", "motorcycle",
    # places
    "aerial view of a city", "city street at night", "beach and ocean waves", "mountain landscape", "forest trees",
    "desert dunes", "interior of a room", "office space", "studio with lights", "stage with spotlights",
    "rainy street", "sunset sky", "snow", "underwater", "space and stars", "modern architecture", "temple",
    "busy market", "a kitchen", "rooftop view", "empty dark room",
    # look and style
    "cinematic film still", "black and white portrait", "neon lights", "3D render", "anime illustration",
    "logo design", "poster with big text", "screenshot of an app", "website design", "chart or infographic",
    "abstract shapes", "smoke and particles", "an explosion", "motion blur", "dark background with a spotlight",
    "minimal white background", "glowing light effects", "fire and flames", "water splash", "vintage photo",
    "pencil sketch", "comic book style", "low angle shot", "wide establishing shot", "reflections on a wet floor",
    "dramatic lighting on a face", "pastel colours", "gold and luxury",
    # animals
    "a dog", "a cat", "a horse", "a bird flying",
    # text and screens
    "a page of text", "handwritten notes", "a slide from a presentation", "code on a screen",
    "a chat screenshot", "a map", "a timeline or storyboard", "a grid of thumbnails",
    # events
    "a wedding", "a concert", "a sports match", "red carpet event", "a festival", "a movie poster", "cricket",
    # sound
    "someone talking about pricing", "a voiceover narration", "background music", "dramatic orchestral music",
    "upbeat electronic beat", "a sound effect", "a phone call recording", "a song with vocals",
    "calm piano music", "playful cartoon music", "an interview", "laughter",
]

_lock = threading.Lock()
_pool = {"vecs": None}
_cache = {"key": None, "items": None}


def _pool_vecs():
    with _lock:
        if _pool["vecs"] is None:
            h = hashlib.sha1(("\n".join(POOL) + "|" + str(embedder.MODEL_ID)).encode()).hexdigest()[:12]
            f = DATA_DIR / f"ideas_{h}.npy"
            if f.exists():
                _pool["vecs"] = np.load(f)
            else:
                m = embedder.get_model()
                v = m.encode(POOL, prompt_name="SearchQuery", normalize_embeddings=True, batch_size=32)
                _pool["vecs"] = np.asarray(v, dtype=np.float32)
                np.save(f, _pool["vecs"])
        return _pool["vecs"]


def ready() -> bool:
    return _pool["vecs"] is not None


def ideas(n: int = 30):
    """Ranked, diverse ideas: [{q, top: [file ids], strength}]"""
    ids, mat = indexer.matrix()
    if len(ids) < 8:
        return []
    key = (len(ids), int(ids[-1]))
    if _cache["key"] == key:
        return _cache["items"][:n]
    P = _pool_vecs()
    sims = P @ mat.T                                  # pool × files
    k = min(10, sims.shape[1])
    top_idx = np.argpartition(-sims, k - 1, axis=1)[:, :k]
    top_vals = np.take_along_axis(sims, top_idx, axis=1)
    order_in = np.argsort(-top_vals, axis=1)
    top_idx = np.take_along_axis(top_idx, order_in, axis=1)
    top_vals = np.take_along_axis(top_vals, order_in, axis=1)
    med = np.median(sims, axis=1)
    spread = sims.std(axis=1) + 1e-6
    strength = (top_vals.mean(axis=1) - med) / spread    # how far the best matches stand out
    picked, sets = [], []
    for i in np.argsort(-strength):
        s = set(int(x) for x in top_idx[i])
        if any(len(s & o) >= 4 for o in sets):
            continue                                      # finds the same files as an idea already shown
        sets.append(s)
        picked.append({"q": POOL[i], "top": [int(ids[j]) for j in top_idx[i][:3]], "strength": round(float(strength[i]), 3)})
        if len(picked) >= 40:
            break
    _cache.update(key=key, items=picked)
    return picked[:n]


def related(file_ids: list[int], exclude: str = "", n: int = 6):
    """Ideas closest to the centre of a set of files (e.g. the current results)."""
    ids, mat = indexer.matrix()
    if not len(ids) or not file_ids:
        return []
    pos = {int(f): i for i, f in enumerate(ids)}
    rows = [pos[f] for f in file_ids[:40] if f in pos]
    if not rows:
        return []
    c = mat[rows].mean(axis=0)
    c /= np.linalg.norm(c) or 1
    P = _pool_vecs()
    sc = P @ c
    ex = exclude.strip().lower()
    out = []
    for i in np.argsort(-sc):
        if POOL[i].lower() == ex:
            continue
        top = mat @ P[i]
        out.append({"q": POOL[i], "top": [int(ids[j]) for j in np.argsort(-top)[:3]]})
        if len(out) >= n:
            break
    return out
