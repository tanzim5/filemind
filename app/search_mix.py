"""Searching with a picture and words together: one reference vector and one text vector, mixed."""
import numpy as np

DEFAULT_TEXT_WEIGHT = 0.5


def _unit(v) -> np.ndarray:
    v = np.asarray(v, dtype=np.float32).reshape(-1)
    n = float(np.linalg.norm(v))
    return v / n if n > 0 else v


def clamp_weight(w) -> float:
    """text_weight as a number between 0 and 1; anything missing or unreadable means the default (0.5)."""
    try:
        w = float(w)
    except (TypeError, ValueError):
        return DEFAULT_TEXT_WEIGHT
    if w != w:                                       # NaN
        return DEFAULT_TEXT_WEIGHT
    return min(1.0, max(0.0, w))


def combine(ref, text, text_weight=DEFAULT_TEXT_WEIGHT) -> np.ndarray:
    """Weighted sum of two normalised vectors, renormalised: (1 - w) * reference + w * text.
    Both inputs are normalised first so neither one wins just by being longer. If the two cancel out
    exactly, the reference alone is returned."""
    w = clamp_weight(text_weight)
    r, t = _unit(ref), _unit(text)
    mixed = (1.0 - w) * r + w * t
    if float(np.linalg.norm(mixed)) < 1e-6:
        return r
    return _unit(mixed)
