"""Read-only duplicate candidates; deletion always remains a separate user action."""
import functools
import hashlib
import os
from collections import defaultdict

import numpy as np
from PIL import Image

from . import db, extract


def _digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
    return h.digest()


def _dhash(path):
    with Image.open(path) as image:
        width, height = image.size
        pixels = list(image.convert("L").resize((9, 8)).getdata())
    bits = sum((1 << i) for i in range(64) if pixels[(i // 8) * 9 + i % 8] > pixels[(i // 8) * 9 + i % 8 + 1])
    return bits, width / max(height, 1)


def _file_digest(row):
    """SHA-256 of a file, reused from the index while the file is unchanged (same size and modified time)."""
    key = f"{row['size']}:{row['mtime']}"
    if row["sha256"] is not None and row["hash_key"] == key:
        return bytes(row["sha256"])
    digest = _digest(row["path"])
    c = db.conn()
    c.execute("UPDATE files SET sha256=?, hash_key=? WHERE id=?", (digest, key, row["id"]))
    return digest


def _thumb_hash(row):
    """dHash of a thumbnail, remembered per thumbnail file."""
    cached = row["dh"]
    if cached and cached.split("|")[0] == row["thumb"]:
        _, bits, ratio = cached.split("|")
        return int(bits, 16), float(ratio)
    bits, ratio = _dhash(extract.THUMB_DIR / row["thumb"])
    db.conn().execute("UPDATE files SET dh=? WHERE id=?", (f"{row['thumb']}|{bits:x}|{ratio}", row["id"]))
    return bits, ratio


# ---- which copy to keep ----
LOSSLESS = {".png", ".tif", ".tiff", ".exr", ".psd", ".bmp", ".dpx"}
COSINE_MIN = 0.95            # look-alikes must also mean the same thing to the model, not just share a blurry shape


def sharpness(path) -> float | None:
    """How much fine detail a picture has: the variance of its Laplacian at a fixed size, so copies compare fairly."""
    try:
        with Image.open(path) as im:
            im.draft("L", (768, 768))
            g = np.asarray(im.convert("L").resize((min(768, im.width), max(1, int(im.height * min(768, im.width) / im.width)))), dtype=np.float32)
        lap = g[1:-1, 1:-1] * 4 - g[:-2, 1:-1] - g[2:, 1:-1] - g[1:-1, :-2] - g[1:-1, 2:]
        return float(lap.var())
    except Exception:
        return None


def _clean_name(name: str) -> bool:
    low = os.path.splitext(name.lower())[0]
    return not (" copy" in low or low.endswith(")") and "(" in low or low[-2:-1] in "-_" and low[-1:].isdigit() and "copy" in low)


def _better(a: dict, b: dict):
    """(winner, why) for two files, or (None, '') when nothing separates them. The order of these checks is the policy."""
    pa, pb = (a["width"] or 0) * (a["height"] or 0), (b["width"] or 0) * (b["height"] or 0)
    if pa != pb:
        w = a if pa > pb else b
        return w, f"Largest ({w['width']}×{w['height']})"
    sa, sb = a.get("sharp"), b.get("sharp")
    if sa and sb and max(sa, sb) > 1.1 * min(sa, sb):
        return (a if sa > sb else b), "Sharpest"
    la, lb = a["ext"] in LOSSLESS, b["ext"] in LOSSLESS
    if la != lb:
        return (a if la else b), "Lossless original"
    ca, cb = _clean_name(a["name"]), _clean_name(b["name"])
    if ca != cb:
        return (a if ca else b), "Original name"
    if a["mtime"] != b["mtime"]:
        return (a if a["mtime"] < b["mtime"] else b), "Oldest copy"
    if a["size"] != b["size"]:
        return (a if a["size"] > b["size"] else b), "Largest file"
    return None, ""


def best(rows: list[dict], exact: bool = False) -> tuple[int, str]:
    """Which file of a set to keep, and the reason in a few words. Deterministic: the same set always gives the same answer."""
    def cmp(a, b):
        w, _ = _better(a, b)
        return 0 if w is None else (-1 if w is a else 1)
    ranked = sorted(rows, key=functools.cmp_to_key(cmp))
    top = ranked[0]
    why = ""
    for other in ranked[1:]:                       # the reason is whatever beat the closest rival
        _, why = _better(top, other)
        if why:
            break
    if exact:
        why = ("Identical files · " + why) if why else "Identical files"
    return int(top["id"]), why or "Identical to the rest"


def describe(groups_: list[list[int]], exact: bool) -> list[dict]:
    """For each group (ids): the best file and why. Sharpness is measured only for look-alikes, and remembered while unchanged."""
    c = db.conn()
    out = []
    for ids in groups_:
        marks = ",".join("?" * len(ids))
        rows = [dict(r) for r in c.execute(
            f"SELECT id,name,ext,kind,width,height,size,mtime,path,sharp,sharp_key FROM files WHERE id IN ({marks})", ids)]
        if not rows:
            out.append({"best": ids[0], "reason": ""})
            continue
        if not exact:
            for r in rows:
                if r["kind"] != "image":
                    continue
                key = f"{r['size']}:{r['mtime']}"
                if r["sharp_key"] != key:
                    r["sharp"] = sharpness(r["path"])
                    c.execute("UPDATE files SET sharp=?, sharp_key=? WHERE id=?", (r["sharp"], key, r["id"]))
        top, why = best(rows, exact)
        out.append({"best": top, "reason": why})
    c.commit()
    return out


_vec_cache: dict[int, object] = {}


def _same_meaning(a: int, b: int) -> bool:
    """True when the model's vectors for two files agree (or one is missing, so nothing can be said against them)."""
    def vec(i):
        if i not in _vec_cache:
            r = db.conn().execute("SELECT vec FROM files WHERE id=?", (i,)).fetchone()
            _vec_cache[i] = np.frombuffer(r["vec"], dtype=np.float32) if r and r["vec"] is not None else None
        return _vec_cache[i]
    va, vb = vec(a), vec(b)
    if va is None or vb is None or va.shape != vb.shape:
        return True
    return float(va @ vb) >= COSINE_MIN


def groups(limit=60, focus=None):
    _vec_cache.clear()
    rows = db.conn().execute("SELECT id,path,size,mtime,kind,thumb,sha256,hash_key,dh FROM files WHERE status='done' AND seq_json IS NULL ORDER BY mtime DESC").fetchall()
    if focus is not None:
        chosen = next((row for row in rows if row["id"] == focus), None)
        if chosen is None:
            return {"exact": [], "visual": [], "total_exact": 0, "total_visual": 0}
        try:
            digest = _digest(chosen["path"])
            exact = [chosen["id"]] + [r["id"] for r in rows if r["id"] != focus and r["size"] == chosen["size"]
                                           and r["kind"] == chosen["kind"] and _safe_digest(r["path"]) == digest]
        except OSError:
            exact = [chosen["id"]]
        visual = [chosen["id"]]
        if chosen["kind"] in ("image", "video") and chosen["thumb"]:
            try:
                bits, ratio = _dhash(extract.THUMB_DIR / chosen["thumb"])
                for row in rows:
                    if row["id"] == focus or row["id"] in exact or row["kind"] != chosen["kind"] or not row["thumb"]:
                        continue
                    try:
                        other_bits, other_ratio = _dhash(extract.THUMB_DIR / row["thumb"])
                    except OSError:
                        continue
                    if abs(ratio - other_ratio) <= .08 and (bits ^ other_bits).bit_count() <= 5 and _same_meaning(focus, row["id"]):
                        visual.append(row["id"])
            except OSError:
                pass
        return {"exact": [exact] if len(exact) > 1 else [], "visual": [visual] if len(visual) > 1 else [],
                "total_exact": int(len(exact) > 1), "total_visual": int(len(visual) > 1)}
    by_size = defaultdict(list)
    for row in rows:
        by_size[row["size"]].append(row)
    exact, exact_ids = [], set()
    for candidates in by_size.values():
        if len(candidates) < 2:
            continue
        by_hash = defaultdict(list)
        for row in candidates:
            try:
                by_hash[_file_digest(row)].append(row["id"])
            except OSError:
                continue
        for ids in by_hash.values():
            if len(ids) > 1:
                exact.append(ids)
                exact_ids.update(ids)
    visual = []
    for row in rows:
        if row["id"] in exact_ids or row["kind"] not in ("image", "video") or not row["thumb"]:
            continue
        try:
            bits, ratio = _thumb_hash(row)
            visual.append((row["id"], bits, ratio))
        except OSError:
            continue
    # Compare only similarly shaped thumbnails. A conservative 64-bit distance
    # avoids turning a shared subject into a destructive "duplicate" claim.
    parent = {item[0]: item[0] for item in visual}

    def find(item):
        while parent[item] != item:
            parent[item] = parent[parent[item]]
            item = parent[item]
        return item

    for i, (left_id, left_bits, left_ratio) in enumerate(visual):
        for right_id, right_bits, right_ratio in visual[i + 1:]:
            if abs(left_ratio - right_ratio) <= 0.08 and (left_bits ^ right_bits).bit_count() <= 5 and _same_meaning(left_id, right_id):
                parent[find(right_id)] = find(left_id)
    by_root = defaultdict(list)
    for fid in parent:
        by_root[find(fid)].append(fid)
    near = [ids for ids in by_root.values() if len(ids) > 1]
    db.conn().commit()
    exact.sort(key=len, reverse=True)
    near.sort(key=len, reverse=True)
    return {"exact": exact[:limit], "visual": near[:limit],
            "total_exact": len(exact), "total_visual": len(near)}


def _safe_digest(path):
    try:
        return _digest(path)
    except OSError:
        return None
