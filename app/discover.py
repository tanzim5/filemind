"""Browsable, read-only groups derived from indexed files and their metadata."""
from collections import defaultdict
from functools import lru_cache
import json
import sqlite3
import threading
import time

import numpy as np
from PIL import Image

from . import db, embedder, names


CATEGORIES = (
    ("people", "People in frame", "Portraits and people", "photos of people, faces and portraits"),
    ("cinematic", "Cinematic images", "Film-like light and framing", "cinematic film still, dramatic lighting, movie scene"),
    ("illustration", "Illustrations", "Drawn and generated artwork", "digital illustration, concept art, painted artwork, drawing"),
    ("architecture", "Architecture", "Buildings and interiors", "architecture photography, modern building, interior design"),
    ("products", "Products", "Objects and product shots", "product photography, objects isolated on a background"),
    ("places", "Places & landscapes", "Scenes and locations", "landscape, mountain, city, beach or travel scene"),
    ("graphics", "Graphics & screens", "Designed visuals", "poster, typography, interface screenshot or graphic design"),
    ("night", "After dark", "Night scenes and glowing light", "night photography, neon lights, dark streets, city after dark"),
    ("nature", "Nature close up", "Plants and natural details", "plants, flowers, leaves, forests and natural textures"),
    ("fashion", "Fashion & beauty", "Clothes, style and makeup", "fashion editorial, clothing, makeup, beauty portrait"),
    ("motion", "Motion & sport", "Action and movement", "sports action, movement, running, dancing, vehicles in motion"),
    ("food", "Food & drink", "Meals and ingredients", "food photography, plated meal, drink, ingredients"),
    ("vehicles", "Vehicles", "Cars, bikes and transport", "cars, motorcycles, bicycles, trains and vehicles"),
    ("patterns", "Patterns & texture", "Repeating shapes and surfaces", "abstract pattern, geometric shapes, material texture, repetition"),
    ("interiors", "Inside spaces", "Rooms and interiors", "interior photography, rooms, furniture, indoor spaces"),
    ("animals", "Animals & wildlife", "Creatures and pets", "animals, pets, birds and wildlife in photographs or illustrations"),
    ("street", "Street life", "Everyday city moments", "street photography, pedestrians, urban life and public spaces"),
    ("water", "Water & coast", "Waves and reflections", "ocean, beach, rivers, rain and water reflections"),
    ("minimal", "Quiet minimalism", "Simple compositions", "minimal composition, negative space, restrained colors and simple objects"),
    ("sky", "Sky & weather", "Clouds and atmosphere", "dramatic clouds, sky, sunrise, sunset and atmospheric weather"),
    ("craft", "Made by hand", "Materials and craft", "hands making art, crafts, tools, textiles and handmade objects"),
)


@lru_cache(maxsize=4096)
def camera_name(path: str, mtime: float) -> str | None:
    try:
        with Image.open(path) as im:
            exif = im.getexif()
            make = str(exif.get(271, "")).strip().strip("\x00")
            model = str(exif.get(272, "")).strip().strip("\x00")
        if not model:
            return None
        if make and make.casefold() not in model.casefold():
            return f"{make} {model}"[:80]
        return model[:80]
    except (OSError, ValueError, TypeError):
        return None


def _camera(r):
    """The camera of a photo, read from the file once and kept in the index while the file is unchanged."""
    keys = r.keys()
    if "camera_key" not in keys:
        return camera_name(r["path"], r["mtime"] or 0)
    stamp = f"{r['size']}:{r['mtime']}"
    if r["camera_key"] == stamp:
        return r["camera"] or None
    name = camera_name(r["path"], r["mtime"] or 0)
    db.conn().execute("UPDATE files SET camera=?, camera_key=? WHERE id=?", (name or "", stamp, r["id"]))
    return name


_cache = {"key": None, "groups": [], "ids": {}}
_building = False
_build_lock = threading.Lock()
_preview = {"key": None, "groups": [], "ids": {}}


def _quick_groups(key):
    if _preview["key"] == key:
        return _preview["groups"]
    rows = db.conn().execute("SELECT id,name,path,width,height,kind,thumb FROM files WHERE status='done' ORDER BY mtime DESC").fetchall()
    by_group = defaultdict(list)
    for r in rows:
        by_group[f"type:{r['kind']}"].append(r)
        source = names.source_of_row(r)
        if source:
            by_group[f"source:{source}"].append(r)
    out = []
    for group_key, items in sorted(by_group.items(), key=lambda pair: (not pair[0].startswith("type:"), -len(pair[1]))):
        source = group_key.removeprefix("source:")
        kind = group_key.removeprefix("type:")
        title = {"image": "Photos", "video": "Videos", "audio": "Sounds"}.get(kind, kind.title()) if group_key.startswith("type:") else names.LABEL.get(source, (source,))[0]
        out.append({"key": group_key, "title": title, "detail": "Grouped by file type" if group_key.startswith("type:") else "Grouped by source",
                    "icon": {"image": "image", "video": "video", "audio": "music"}.get(kind, "sparkle"), "count": len(items),
                    "sample": [{"id": r["id"], "kind": r["kind"], "name": r["name"]} for r in items if r["thumb"]][:4]})
    _preview.update(key=key, groups=out, ids={k: {int(r["id"]) for r in v} for k, v in by_group.items()})
    return out


def _index_gen():
    try:
        return int(db.conn().execute("SELECT value FROM meta WHERE key='index_gen'").fetchone()[0])
    except (sqlite3.OperationalError, TypeError):
        return None


def _saved():
    """The last result kept in the index, or None. It survives a restart, so opening Discover costs nothing."""
    try:
        row = db.conn().execute("SELECT gen, model_ready, groups, ids FROM discover_cache WHERE id=1").fetchone()
    except sqlite3.OperationalError:
        return None
    if not row:
        return None
    try:
        return {"gen": row["gen"], "ready": row["model_ready"], "groups": json.loads(row["groups"]),
                "ids": {k: set(v) for k, v in json.loads(row["ids"]).items()}}
    except ValueError:
        return None


def _save(gen, groups_, ids):
    if gen is None:
        return
    try:
        c = db.conn()
        c.execute("INSERT OR REPLACE INTO discover_cache(id, gen, model_ready, created, groups, ids) VALUES (1, ?, ?, ?, ?, ?)",
                  (gen, int(bool(embedder.ready())), time.time(), json.dumps(groups_), json.dumps({k: sorted(v) for k, v in ids.items()})))
        c.commit()
    except sqlite3.OperationalError:
        pass


def request_groups():
    """Return immediately. The last result is kept in the index; it is rebuilt in the background only when the library changed."""
    global _building
    gen = _index_gen()
    saved = _saved() if gen is not None else None
    if saved and saved["gen"] == gen and saved["ready"] == int(bool(embedder.ready())):
        _cache.update(key=("saved", gen), groups=saved["groups"], ids=saved["ids"])
        return {"groups": saved["groups"], "building": False}
    if saved:                                           # out of date: show it now, refresh behind it
        _cache.update(groups=saved["groups"], ids=saved["ids"])
        with _build_lock:
            if not _building:
                _building = True
                threading.Thread(target=_build_async, daemon=True).start()
        return {"groups": saved["groups"], "building": True, "stale": True}
    r = db.conn().execute("SELECT COUNT(*) AS n, SUM(id) AS ids, SUM(CAST(mtime AS INTEGER)) AS modified FROM files WHERE status='done'").fetchone()
    key = (r["n"], r["ids"] or 0, r["modified"] or 0, embedder.ready())
    with _build_lock:
        if _cache["key"] == key:
            return {"groups": _cache["groups"], "building": False}
        if not _building:
            _building = True
            threading.Thread(target=_build_async, daemon=True).start()
    return {"groups": _cache["groups"] or _quick_groups(key), "building": True}


def _build_async():
    global _building
    try:
        groups()
    finally:
        with _build_lock:
            _building = False


def groups():
    gen = _index_gen()
    try:
        rows = db.conn().execute(
            "SELECT id,path,name,kind,mtime,thumb,vec,width,height,camera,camera_key,size FROM files WHERE status='done' ORDER BY mtime DESC"
        ).fetchall()
    except sqlite3.OperationalError:
        rows = db.conn().execute(
            "SELECT id,path,name,kind,mtime,thumb,vec FROM files WHERE status='done' ORDER BY mtime DESC"
        ).fetchall()
    key = (len(rows), sum(int(r["id"]) for r in rows), sum(int(r["mtime"] or 0) for r in rows), embedder.ready())
    if _cache["key"] == key:
        return _cache["groups"]

    by_group = defaultdict(list)
    for r in rows:
        fid = int(r["id"])
        by_group[f"type:{r['kind']}"].append(fid)
        source = names.source_of_row(r)
        if source:
            by_group[f"source:{source}"].append(fid)
        if r["kind"] == "image" and r["path"].lower().endswith((".jpg", ".jpeg", ".heic", ".heif", ".tif", ".tiff", ".webp")):
            camera = _camera(r)
            if camera:
                by_group[f"camera:{camera}"].append(fid)

    # The existing multimodal embeddings rank visual themes. They do not identify individuals.
    images = [r for r in rows if r["kind"] == "image" and r["vec"] is not None]
    if len(images) >= 8 and embedder.ready():
        mat = np.vstack([np.frombuffer(r["vec"], dtype=np.float32) for r in images])
        scores = np.vstack([mat @ embedder.embed_query(query) for _, _, _, query in CATEGORIES])
        # Normalize each prompt's score range, then assign an image only to its strongest
        # theme. The margin excludes images that are weak matches for every theme.
        normalized = (scores - scores.mean(axis=1, keepdims=True)) / (scores.std(axis=1, keepdims=True) + 1e-6)
        winner = normalized.argmax(axis=0)
        for category_index, (key_name, _, _, _) in enumerate(CATEGORIES):
            picked = np.argsort(-normalized[category_index])
            by_group[f"theme:{key_name}"] = [int(images[i]["id"]) for i in picked
                                               if winner[i] == category_index and normalized[category_index, i] >= .25]

    lookup = {int(r["id"]): r for r in rows}
    out = []
    def add(key_name, title, detail, icon, ids):
        if not ids:
            return
        samples = [{"id": fid, "kind": lookup[fid]["kind"], "name": lookup[fid]["name"]}
                   for fid in ids if lookup[fid]["thumb"]][:4]
        out.append({"key": key_name, "title": title, "detail": detail, "icon": icon,
                    "count": len(ids), "sample": samples})

    for key_name, title, detail, _ in CATEGORIES:
        add(f"theme:{key_name}", title, detail, "user" if key_name == "people" else "sparkle", by_group[f"theme:{key_name}"])
    for kind, title, icon in (("image", "Photos", "image"), ("video", "Videos", "video"), ("audio", "Sounds", "music")):
        add(f"type:{kind}", title, "Grouped by file type", icon, by_group[f"type:{kind}"])
    for key_name, ids in sorted(((k, v) for k, v in by_group.items() if k.startswith("source:")), key=lambda item: -len(item[1])):
        source = key_name[7:]
        add(key_name, names.LABEL.get(source, (source,))[0], "Grouped by source", "image" if source == "camera" else "sparkle", ids)
    for key_name, ids in sorted(((k, v) for k, v in by_group.items() if k.startswith("camera:")), key=lambda item: -len(item[1]))[:12]:
        add(key_name, key_name[7:], "Camera model in photo metadata", "image", ids)

    _cache.update(key=key, groups=out, ids=dict(by_group))
    db.conn().commit()
    _save(gen, out, by_group)
    return out


def file_ids(key: str) -> set[int]:
    if not _cache["ids"] and (saved := _saved()):       # after a restart the sets come from the index
        _cache["ids"] = saved["ids"]
    if key in _cache["ids"]:
        return set(_cache["ids"][key])
    if key in _preview["ids"]:
        return set(_preview["ids"][key])
    return set()
