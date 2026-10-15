"""Read-only duplicate candidates; deletion always remains a separate user action."""
import hashlib
from collections import defaultdict
from pathlib import Path

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


def groups(limit=60, focus=None):
    rows = db.conn().execute("SELECT id,path,size,kind,thumb FROM files WHERE status='done' ORDER BY mtime DESC").fetchall()
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
                    if abs(ratio - other_ratio) <= .08 and (bits ^ other_bits).bit_count() <= 5:
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
                by_hash[_digest(row["path"])].append(row["id"])
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
            bits, ratio = _dhash(extract.THUMB_DIR / row["thumb"])
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
            if abs(left_ratio - right_ratio) <= 0.08 and (left_bits ^ right_bits).bit_count() <= 5:
                parent[find(right_id)] = find(left_id)
    by_root = defaultdict(list)
    for fid in parent:
        by_root[find(fid)].append(fid)
    near = [ids for ids in by_root.values() if len(ids) > 1]
    exact.sort(key=len, reverse=True)
    near.sort(key=len, reverse=True)
    return {"exact": exact[:limit], "visual": near[:limit],
            "total_exact": len(exact), "total_visual": len(near)}


def _safe_digest(path):
    try:
        return _digest(path)
    except OSError:
        return None
