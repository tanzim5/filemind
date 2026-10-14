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


def groups(limit=60):
    rows = db.conn().execute("SELECT id,path,size,kind,thumb FROM files WHERE status='done' ORDER BY mtime DESC").fetchall()
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
