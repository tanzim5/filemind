import asyncio
import hmac
import os
import json
import logging
import re
import sqlite3
import subprocess
import sys
import time
from collections import Counter
from functools import lru_cache
from pathlib import Path
from send2trash import send2trash

import numpy as np
from fastapi import Body, FastAPI, File, HTTPException, UploadFile, WebSocket
from fastapi.responses import FileResponse

from . import colors as colors_mod
from . import names as names_mod
from . import commit as commit_mod
from . import db, embedder, extract, indexer, media_meta
from . import understand as understand_mod
from . import voice_stream
from . import suggest as suggest_mod
from . import tags as tags_mod
from . import fetch as fetch_mod
from . import discover as discover_mod
from . import transcribe as transcribe_mod
from . import duplicates as duplicates_mod
from . import moments as moments_mod
from . import export as export_mod
from . import search_mix
from . import space_suggest, space_match
from . import sequences as sequences_mod

app = FastAPI(title="Local File Brain")


@app.on_event("startup")
def _startup():
    db.init()
    c = db.conn()
    # Forget index rows for kinds we do not read (zips, installers, code...). Index entries only; files on disk are never touched.
    c.execute("DELETE FROM files WHERE kind NOT IN ('image','video','audio','doc','text')")
    c.execute("UPDATE files SET status='error', error='Could not be decoded' "
              "WHERE kind IN ('image','video') AND status='done' AND (thumb IS NULL OR thumb='')")
    c.execute("DELETE FROM project_files WHERE file_id NOT IN (SELECT id FROM files)")
    c.execute("DELETE FROM proposed_names WHERE file_id NOT IN (SELECT id FROM files)")
    c.commit()
    _migrate_labels(c)
    _drop_files(c, "root_id NOT IN (SELECT id FROM roots)")     # left behind by a folder that was forgotten long ago
    _sweep_thumbs(c)
    indexer.invalidate()
    import threading
    threading.Thread(target=indexer.backfill_colors, daemon=True).start()     # colours for older files
    threading.Thread(target=_warm_ideas, daemon=True).start()
    threading.Thread(target=_warm_dims, daemon=True).start()                  # thumbnail sizes, so the first grid is quick                 # search ideas from the library
    threading.Thread(target=media_meta.backfill, daemon=True).start()
    # pick up anything new in the folders you added (cheap when nothing changed)
    for r in c.execute("SELECT id, path FROM roots ORDER BY id").fetchall():
        if os.path.isdir(r["path"]):
            indexer.start(r["id"], r["path"])


# ---------------------------------------------------------------- roots / indexing
_dims: dict[str, tuple[int, int] | None] = {}


def _thumb_dims(name):
    if not name:
        return None
    if name not in _dims:
        try:
            from PIL import Image
            with Image.open(extract.THUMB_DIR / name) as im:
                _dims[name] = im.size
        except Exception:
            _dims[name] = None
    return _dims[name]


def _file_dict(r, score=None):
    dm = _thumb_dims(r["thumb"])
    d = {"tw": dm[0] if dm else None, "th": dm[1] if dm else None, "id": r["id"], "name": r["name"], "path": r["path"], "ext": r["ext"], "kind": r["kind"],
         "size": r["size"], "mtime": r["mtime"], "has_thumb": bool(r["thumb"]),
         "status": r["status"], "mode": r["embed_mode"], "snippet": r["snippet"],
         "dom": r["dom"] or None, "src": names_mod.source_of_row(r)}
    d.update({key: r[key] for key in ("width", "height", "duration", "fps", "codec") if key in r.keys()})
    d["tags"] = _file_tags().get(r["id"], [])
    d["label"] = _file_labels().get(r["id"])
    if "seq_json" in r.keys() and r["seq_json"] and (info := sequences_mod.load(r["seq_json"])):
        d["seq"] = sequences_mod.brief(info, r["fps"] if "fps" in r.keys() else None)
    if score is not None:
        d["score"] = round(float(score), 4)
    return d


@app.get("/api/known_folders")
def known_folders():
    """Common library folders that exist on this PC, for one-click adding."""
    home = Path.home()
    out, seen = [], set()
    for label, names in [("Downloads", ["Downloads"]), ("Pictures", ["Pictures", "OneDrive/Pictures"]),
                         ("Videos", ["Videos", "OneDrive/Videos"]), ("Documents", ["Documents", "OneDrive/Documents"]),
                         ("Desktop", ["Desktop", "OneDrive/Desktop"]), ("Music", ["Music", "OneDrive/Music"])]:
        for n in names:
            p = home / n
            if p.is_dir() and str(p).lower() not in seen:
                seen.add(str(p).lower())
                out.append({"label": label, "path": str(p)})
                break
    return out


@app.get("/api/roots")
def roots():
    c = db.conn()
    out = []
    for r in c.execute("SELECT * FROM roots ORDER BY id"):
        s = c.execute("SELECT COUNT(*) n, SUM(status='done') d, SUM(status='error') e "
                      "FROM files WHERE root_id=?", (r["id"],)).fetchone()
        done, bad = s["d"] or 0, s["e"] or 0
        out.append({"id": r["id"], "path": r["path"], "files": (s["n"] or 0) - bad,   # unreadable files are hidden
                    "indexed": done, "errors": bad})
    return out


@app.post("/api/roots")
def add_root(body: dict = Body(...)):
    path = os.path.abspath(os.path.expandvars(os.path.expanduser(body.get("path", ""))))
    if not os.path.isdir(path):
        raise HTTPException(400, f"Not a folder: {path}")
    if Path(path).is_relative_to(db.DATA_DIR):
        raise HTTPException(400, "That folder holds File Mind's own data, so it can't be added.")
    c = db.conn()
    c.execute("INSERT OR IGNORE INTO roots(path, added) VALUES(?,?)", (path, time.time()))
    c.commit()
    rid = c.execute("SELECT id FROM roots WHERE path=?", (path,)).fetchone()["id"]
    indexer.start(rid, path)
    return {"id": rid, "path": path}


_FILE_LINKS = ("project_files", "proposed_names", "file_tags", "file_labels", "moments", "moment_runs", "space_dismissals")


def _drop_files(c, where: str, args: tuple = ()) -> int:
    """Takes files out of the index with everything attached to them and their cached thumbnails. Never touches the originals."""
    thumbs = [r["thumb"] for r in c.execute(f"SELECT thumb FROM files WHERE {where}", args) if r["thumb"]]
    n = c.execute(f"SELECT COUNT(*) AS n FROM files WHERE {where}", args).fetchone()["n"]
    if n == 0:
        return 0
    for table in _FILE_LINKS:
        c.execute(f"DELETE FROM {table} WHERE file_id IN (SELECT id FROM files WHERE {where})", args)
    c.execute(f"DELETE FROM files WHERE {where}", args)
    c.commit()
    for name in thumbs:
        (extract.THUMB_DIR / name).unlink(missing_ok=True)
    return n


def _sweep_thumbs(c) -> None:
    """Deletes cached thumbnails that no file in the index uses any more."""
    used = {r["thumb"] for r in c.execute("SELECT thumb FROM files WHERE thumb IS NOT NULL")}
    for f in extract.THUMB_DIR.glob("*.jpg"):
        if f.name not in used:
            f.unlink(missing_ok=True)


@app.delete("/api/roots/{rid}")
def del_root(rid: int):
    """Forget a folder: its files, thumbnails and tags leave the index. Never deletes anything on disk."""
    c = db.conn()
    _drop_files(c, "root_id=?", (rid,))
    c.execute("DELETE FROM roots WHERE id=?", (rid,))
    c.commit()
    indexer.invalidate()
    return {"ok": True}


@app.post("/api/roots/{rid}/index")
def reindex(rid: int):
    r = db.conn().execute("SELECT * FROM roots WHERE id=?", (rid,)).fetchone()
    if not r:
        raise HTTPException(404)
    return {"started": indexer.start(rid, r["path"])}


@app.post("/api/index/stop")
def stop_index():
    indexer.stop()
    return {"ok": True}


@app.get("/api/status")
def status():
    s = dict(indexer.state)
    s["elapsed"] = round(time.time() - s["started"], 1) if s["running"] else 0
    return s


# ---------------------------------------------------------------- browse / search
_TAKEN = "(CASE WHEN f.taken > 0 THEN f.taken ELSE f.mtime END)"     # when a file was taken, not copied


@app.get("/api/files")
def files(root: int | None = None, kind: str | None = None, project: int | None = None,
          unassigned: bool = False, sort: str = "mtime", limit: int = 300, offset: int = 0,
          color: str | None = None, near: str | None = None, source: str | None = None,
          pattern: str | None = None, discover: str | None = None,
          after: float | None = None, before: float | None = None,
          min_size: int | None = None, max_size: int | None = None,
          min_width: int | None = None, min_height: int | None = None, min_pixels: int | None = None,
          min_duration: float | None = None, max_duration: float | None = None,
          min_fps: float | None = None, aspect: str | None = None,
          codec: str | None = None, ids_only: bool = False, tag: str | None = None, label: str | None = None,
          media: bool = False):
    where, args = ["f.status='done'"], []      # only fully read files; never half-processed cards
    if media:                                   # the All page can show just pictures and videos
        where.append("f.kind IN ('image','video')")
    hide = _min_side_sql()
    if hide:
        where.append(hide)
    if root:
        where.append("f.root_id=?"); args.append(root)
    if kind and kind != "all":
        where.append("f.kind=?"); args.append(kind)
    smart_members = None
    if project:
        smart_members = _smart_space_ids(project)
        if smart_members is None:
            where.append("f.id IN (SELECT file_id FROM project_files WHERE project_id=?)"); args.append(project)
    if unassigned:
        where.append("f.id NOT IN (SELECT file_id FROM project_files)")
    if tag:
        where.append("f.id IN (SELECT file_id FROM file_tags WHERE tag=?)"); args.append(tag)
    if label:
        wanted = [x for x in label.split(",") if x in LABELS]
        if wanted:
            where.append(f"f.id IN (SELECT file_id FROM file_labels WHERE label IN ({','.join('?' * len(wanted))}))"); args += wanted
    if after is not None:
        where.append(f"{_TAKEN}>=?"); args.append(after)
    if before is not None:
        where.append(f"{_TAKEN}<?"); args.append(before)
    if min_size is not None:
        where.append("f.size>=?"); args.append(min_size)
    if max_size is not None:
        where.append("f.size<=?"); args.append(max_size)
    for column, value, operator in (("width", min_width, ">="), ("height", min_height, ">="),
                                    ("duration", min_duration, ">="), ("duration", max_duration, "<="),
                                    ("fps", min_fps, ">=")):
        if value is not None:
            where.append(f"f.{column}{operator}?"); args.append(value)
    if min_pixels is not None:
        where.append("f.width*f.height>=?"); args.append(min_pixels)
    if aspect:
        aspect_sql = {"portrait": "f.width<f.height", "landscape": "f.width>f.height",
                      "square": "f.width*1.1>=f.height AND f.height*1.1>=f.width",
                      "wide": "f.width>=f.height*1.7"}
        if aspect not in aspect_sql:
            raise HTTPException(400, "Unknown aspect ratio")
        where.append("(" + aspect_sql[aspect] + ")")
    if codec:
        where.append("f.codec=?"); args.append(codec.lower())
    order = {"mtime": "f.mtime DESC", "name": "f.name COLLATE NOCASE", "size": "f.size DESC",
             "label": "COALESCE((SELECT CASE label WHEN 'red' THEN 1 WHEN 'orange' THEN 2 WHEN 'yellow' THEN 3 WHEN 'green' THEN 4 "
                      "WHEN 'blue' THEN 5 WHEN 'purple' THEN 6 ELSE 7 END FROM file_labels WHERE file_id=f.id), 9), f.mtime DESC"}.get(sort, "f.mtime DESC")
    c = db.conn()
    base = c.execute(f"SELECT f.id, f.name, f.path, f.width, f.height, f.dom, f.pal FROM files f WHERE {' AND '.join(where)} ORDER BY {order}", args).fetchall()
    meta = {r["id"]: r for r in base}
    ids = [r["id"] for r in base]
    if smart_members is not None:
        ids = [i for i in ids if i in smart_members]
    if discover:
        discovered = discover_mod.file_ids(discover)
        ids = [i for i in ids if i in discovered]
    # filters that are computed (file-name source / pattern, colour) are applied here
    if source:
        ids = [i for i in ids if (names_mod.source_of_row(meta[i]) or "none") == source]
    if pattern:
        ids = [i for i in ids if names_mod.source_of_row(meta[i]) is None and names_mod.signature(meta[i]["name"]) == pattern]
    if color:
        share = {i: colors_mod.shares(meta[i]["pal"]).get(color, 0) for i in ids}
        ids = [i for i in ids if share[i] >= colors_mod.MIN_SHARE]
        if sort in ("mtime", "color"):
            ids.sort(key=lambda i: -share[i])          # the most green pictures first
    if near:
        ids = [i for i in ids if meta[i]["dom"]]
        ids.sort(key=lambda i: colors_mod.distance(meta[i]["dom"], near))
    elif sort == "color":
        ids.sort(key=lambda i: colors_mod.rainbow_key(meta[i]["dom"]))
    if ids_only:
        return {"ids": ids}
    total, page = len(ids), ids[offset:offset + limit]
    rows = {}
    if page:
        rows = {r["id"]: r for r in c.execute(f"SELECT * FROM files WHERE id IN ({','.join(str(int(i)) for i in page)})")}
    pn = {r["file_id"]: r["new_name"] for r in c.execute("SELECT * FROM proposed_names")}
    out = []
    for i in page:
        d = _file_dict(rows[i])
        d["proposed"] = pn.get(i)
        out.append(d)
    return {"total": total, "files": out}


# ---------------------------------------------------------------- colour labels (Finder-style) and display preferences
LABELS = ("red", "orange", "yellow", "green", "blue", "purple", "grey")
_LABEL_RGB = {"red": (239, 68, 68), "orange": (249, 115, 22), "yellow": (234, 179, 8), "green": (34, 197, 94),
              "blue": (59, 130, 246), "purple": (168, 85, 247), "grey": (156, 163, 175)}
_label_cache: dict | None = None


def _file_labels() -> dict:
    global _label_cache
    if _label_cache is None:
        try:
            _label_cache = {r[0]: r[1] for r in db.conn().execute("SELECT file_id, label FROM file_labels")}
        except sqlite3.OperationalError:
            return {}
    return _label_cache


def _migrate_labels(c) -> None:
    """One time: the tag list is retired. Each file that carried a tag of its own gets a colour label (the nearest to the
    tag's colour), and every old tag is saved to data/legacy-tags.json first. Favourites are untouched."""
    if c.execute("SELECT 1 FROM meta WHERE key='labels_migrated'").fetchone():
        return
    rows = c.execute("SELECT ft.file_id, ft.tag, t.color FROM file_tags ft LEFT JOIN tags t ON t.name=ft.tag "
                     "WHERE ft.tag != ? COLLATE NOCASE ORDER BY ft.file_id, ft.rowid", (FAVOURITE,)).fetchall()
    if rows:
        try:
            (db.DATA_DIR / "legacy-tags.json").write_text(json.dumps(
                [{"file_id": r[0], "tag": r[1], "color": r[2]} for r in rows], indent=1), encoding="utf-8")
        except OSError:
            logging.getLogger("filemind").warning("could not save legacy-tags.json", exc_info=True)

        def nearest(hex_):
            try:
                h = (hex_ or "#8B5CF6").lstrip("#"); rgb = tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))
            except ValueError:
                rgb = (139, 92, 246)
            return min(LABELS, key=lambda k: sum((a - b) ** 2 for a, b in zip(rgb, _LABEL_RGB[k])))
        seen = set()
        for fid, _tag, color in rows:
            if fid not in seen:
                seen.add(fid)
                c.execute("INSERT OR IGNORE INTO file_labels(file_id, label) VALUES(?, ?)", (fid, nearest(color)))
    c.execute("INSERT OR REPLACE INTO meta(key, value) VALUES('labels_migrated', '1')")
    c.commit()


@app.get("/api/labels")
def labels_list():
    counts = {r[0]: r[1] for r in db.conn().execute("SELECT label, COUNT(*) FROM file_labels GROUP BY label")}
    return {"labels": [{"name": k, "count": counts.get(k, 0)} for k in LABELS]}


@app.post("/api/labels")
def labels_apply(body: dict = Body(...)):
    """{ids, label}: give files a colour label, or clear it with label=null."""
    global _label_cache
    ids = [int(i) for i in body.get("ids") or []][:5000]
    label = body.get("label")
    if label is not None and label not in LABELS:
        raise HTTPException(400, "Choose one of: " + ", ".join(LABELS))
    c = db.conn()
    if label is None:
        c.executemany("DELETE FROM file_labels WHERE file_id=?", [(i,) for i in ids])
    else:
        c.executemany("INSERT OR REPLACE INTO file_labels(file_id, label) VALUES(?, ?)", [(i, label) for i in ids])
    c.commit(); _label_cache = None
    return labels_list()


def _min_side() -> tuple[int, str]:
    c = db.conn()
    try:
        n = c.execute("SELECT value FROM meta WHERE key='min_side'").fetchone()
        m = c.execute("SELECT value FROM meta WHERE key='min_side_mode'").fetchone()
    except sqlite3.OperationalError:
        return 0, "short"
    return (int(n[0]) if n else 0), (m[0] if m else "short")


def _min_side_sql() -> str:
    """Hide pictures and clips smaller than the chosen size. Files whose size is unknown stay visible; nothing is deleted."""
    n, mode = _min_side()
    if n <= 0:
        return ""
    side = "MIN(f.width, f.height)" if mode == "short" else "MAX(f.width, f.height)"
    return f"(f.kind NOT IN ('image','video') OR f.width IS NULL OR f.height IS NULL OR {side} >= {int(n)})"


@app.post("/api/prefs")
def set_prefs(body: dict = Body(...)):
    """{min_side: int, min_side_mode: 'short'|'long'}: display preferences the library views respect."""
    c = db.conn()
    if "min_side" in body:
        c.execute("INSERT OR REPLACE INTO meta(key, value) VALUES('min_side', ?)", (str(max(0, min(int(body["min_side"]), 20000))),))
    if body.get("min_side_mode") in ("short", "long"):
        c.execute("INSERT OR REPLACE INTO meta(key, value) VALUES('min_side_mode', ?)", (body["min_side_mode"],))
    c.commit()
    n, mode = _min_side()
    return {"min_side": n, "min_side_mode": mode}


# ---------------------------------------------------------------- tags and favourites
# A favourite is the built-in "Favourites" tag, so both share one store, one filter and one search path.
FAVOURITE = "Favourites"
_tag_cache: dict | None = None


def _file_tags() -> dict:
    global _tag_cache
    if _tag_cache is None:
        out: dict = {}
        for r in db.conn().execute("SELECT file_id, tag FROM file_tags ORDER BY tag"):
            out.setdefault(r[0], []).append(r[1])
        _tag_cache = out
    return _tag_cache


def _ensure_tag(name: str, color: str | None = None):
    db.conn().execute("INSERT OR IGNORE INTO tags(name, color) VALUES(?, ?)",
                      (name, color or ("#F59E0B" if name == FAVOURITE else "#8B5CF6")))


@app.get("/api/tags")
def tags_list():
    c = db.conn()
    _ensure_tag(FAVOURITE); c.commit()
    counts = {r[0].lower(): r[1] for r in c.execute("SELECT tag, COUNT(*) FROM file_tags GROUP BY tag COLLATE NOCASE")}
    rows = [{"name": r["name"], "color": r["color"], "count": counts.get(r["name"].lower(), 0)}
            for r in c.execute("SELECT name, color FROM tags ORDER BY created, name")]
    rows.sort(key=lambda t: (t["name"] != FAVOURITE,))
    return {"tags": rows}


@app.post("/api/tags")
def tags_apply(body: dict = Body(...)):
    """{ids, add: [names], remove: [names]}: tag or untag files; new tag names are created as they are used."""
    global _tag_cache
    ids = [int(i) for i in body.get("ids") or []]
    add = [str(t).strip()[:40] for t in body.get("add") or [] if str(t).strip()]
    remove = [str(t).strip() for t in body.get("remove") or [] if str(t).strip()]
    c = db.conn()
    for t in add:
        _ensure_tag(t)
        c.executemany("INSERT OR IGNORE INTO file_tags(file_id, tag) VALUES(?, ?)", [(i, t) for i in ids])
    for t in remove:
        c.executemany("DELETE FROM file_tags WHERE file_id=? AND tag=?", [(i, t) for i in ids])
    c.commit(); _tag_cache = None
    return tags_list()


@app.post("/api/tags/define")
def tags_define(body: dict = Body(...)):
    """{name, color, rename?}: create a tag, recolour it, or rename it everywhere."""
    global _tag_cache
    name = str(body.get("name") or "").strip()[:40]
    if not name:
        raise HTTPException(400, "A tag needs a name")
    c = db.conn()
    _ensure_tag(name, body.get("color"))
    if body.get("color"):
        c.execute("UPDATE tags SET color=? WHERE name=?", (body["color"], name))
    new = str(body.get("rename") or "").strip()[:40]
    if new and new != name and name != FAVOURITE:
        c.execute("UPDATE tags SET name=? WHERE name=?", (new, name))
        c.execute("UPDATE OR IGNORE file_tags SET tag=? WHERE tag=?", (new, name))
        c.execute("DELETE FROM file_tags WHERE tag=?", (name,))
    c.commit(); _tag_cache = None
    return tags_list()


@app.delete("/api/tags/{name}")
def tags_delete(name: str):
    global _tag_cache
    if name == FAVOURITE:
        raise HTTPException(400, "Favourites can't be deleted")
    c = db.conn()
    c.execute("DELETE FROM file_tags WHERE tag=?", (name,)); c.execute("DELETE FROM tags WHERE name=?", (name,))
    c.commit(); _tag_cache = None
    return tags_list()


_SCOPE_KEYS = ("root", "kind", "project", "unassigned", "color", "near", "source", "pattern", "discover", "tag", "label", "media")


@app.post("/api/facets")
def facets(body: dict = Body(...)):
    """Counts for every Refine option over what is on screen: the browsed scope (without the detail filters, so the
    other choices in a group stay visible) or, when searching, the ids of the current results. Options that match
    nothing or everything are left out by the client, so every chip that shows actually narrows the view."""
    ids = body.get("ids")
    if ids is None:
        ids = files(**{k: body[k] for k in _SCOPE_KEYS if body.get(k) is not None}, ids_only=True)["ids"]
    ids = [int(i) for i in ids][:200_000]
    total = len(ids)
    if not total:
        return {"total": 0, "facets": []}
    c = db.conn()
    rows = []
    for start in range(0, total, 900):
        chunk = ids[start:start + 900]
        rows += c.execute(f"SELECT kind,width,height,duration,fps,size,mtime FROM files WHERE id IN ({','.join('?' * len(chunk))})", chunk).fetchall()
    now = time.time()
    day = 86400
    tests = [
        ("Quality", "4k", "4K", "At least 3840 × 2160", lambda r: (r["width"] or 0) * (r["height"] or 0) >= 3840 * 2160),
        ("Quality", "hd", "HD or better", "At least 1920 × 1080", lambda r: (r["width"] or 0) * (r["height"] or 0) >= 1920 * 1080),
        ("Shape", "portrait", "Vertical", "Taller than wide", lambda r: r["width"] and r["height"] and r["width"] < r["height"]),
        ("Shape", "square", "Square", "About as wide as tall", lambda r: r["width"] and r["height"] and r["width"] * 1.1 >= r["height"] and r["height"] * 1.1 >= r["width"]),
        ("Shape", "wide", "Widescreen", "16:9 or wider", lambda r: r["width"] and r["height"] and r["width"] >= r["height"] * 1.7),
        ("Length", "short", "Under 30 s", "Clips and short sounds", lambda r: r["duration"] and r["duration"] <= 30),
        ("Length", "long", "Over 1 min", "Longer videos and audio", lambda r: r["duration"] and r["duration"] >= 60),
        ("Motion", "60fps", "60 fps", "Smooth, high frame rate", lambda r: (r["fps"] or 0) >= 50),
        ("File size", "big", "Over 20 MB", "Large files", lambda r: (r["size"] or 0) >= 20_000_000),
        ("File size", "huge", "Over 500 MB", "Very large files", lambda r: (r["size"] or 0) >= 500_000_000),
        ("Date", "today", "Today", "Changed in the last day", lambda r: (r["mtime"] or 0) >= now - day),
        ("Date", "week", "This week", "Last 7 days", lambda r: (r["mtime"] or 0) >= now - 7 * day),
        ("Date", "month", "This month", "Last 30 days", lambda r: (r["mtime"] or 0) >= now - 30 * day),
        ("Date", "year", "This year", "Last 365 days", lambda r: (r["mtime"] or 0) >= now - 365 * day),
    ]
    out = [{"group": g, "key": k, "label": lab, "hint": hint, "count": sum(1 for r in rows if t(r))}
           for g, k, lab, hint, t in tests]
    return {"total": total, "facets": out}


def _smart_space_ids(pid: int) -> set[int] | None:
    row = db.conn().execute("SELECT spec,query_vec FROM smart_spaces WHERE project_id=?", (pid,)).fetchone()
    if row is None:
        return None
    spec = json.loads(row["spec"])
    params = {key: spec[key] for key in ("root", "kind", "color", "source", "after", "before",
                                        "min_size", "max_size", "min_width", "min_height",
                                        "min_pixels", "min_duration", "max_duration", "min_fps", "aspect", "codec") if spec.get(key) is not None}
    members = files(**params, ids_only=True)["ids"]
    if row["query_vec"] is None:
        return set(members)
    ids, mat = indexer.matrix()
    if not len(ids):
        return set()
    vec = np.frombuffer(row["query_vec"], dtype=np.float32)
    if mat.shape[1] != len(vec):
        return set()
    allowed = set(members)
    scores = mat @ vec
    order = np.argsort(-scores)
    return set([int(ids[pos]) for pos in order if int(ids[pos]) in allowed][:300])


@app.get("/api/discover")
def discover():
    return discover_mod.request_groups()


@app.get("/api/sources")
def sources():
    """Which AI tool / app each file name looks like it came from, with counts."""
    from collections import Counter
    return _source_counts(None)


@app.post("/api/sources")
def sources_in_view(body: dict = Body(...)):
    """The same counts, but only over what is on screen (the browsed scope, without the source filter itself)."""
    scope = {k: body[k] for k in _SCOPE_KEYS if body.get(k) is not None and k != "source"}
    media = bool(scope.pop("media", False))
    ids = files(**scope, ids_only=True)["ids"] if scope else None
    return _source_counts(ids, media)


def _source_counts(ids, media=False):
    from collections import Counter
    c = db.conn()
    cnt = Counter()
    if ids is None:
        rows = c.execute("SELECT name, path, width, height FROM files WHERE status='done'" + (" AND kind IN ('image','video')" if media else ""))
        cnt.update(names_mod.source_of_row(r) for r in rows)
    else:
        for start in range(0, len(ids), 900):
            chunk = [int(i) for i in ids[start:start + 900]]
            rows = c.execute(f"SELECT name, path, width, height FROM files WHERE id IN ({','.join('?' * len(chunk))})", chunk)
            cnt.update(names_mod.source_of_row(r) for r in rows)
    out = [{"key": k, "label": names_mod.LABEL[k][0], "color": names_mod.LABEL[k][1], "count": v} for k, v in cnt.most_common() if k]
    return {"sources": out, "unknown": cnt.get(None, 0)}


@app.get("/api/colors")
def color_counts():
    from collections import Counter
    cnt = Counter()
    for r in db.conn().execute("SELECT pal FROM files WHERE status='done' AND pal IS NOT NULL"):
        for k, v in colors_mod.shares(r["pal"]).items():
            if v >= colors_mod.MIN_SHARE:
                cnt[k] += 1
    return [{"key": k, "label": l, "swatch": s, "count": cnt.get(k, 0), "neutral": k in colors_mod.NEUTRAL}
            for k, l, s in colors_mod.SWATCHES]


# colour moods are not pixels: they are searched by meaning with the embedding model
MOODS = [("neon", "Neon glow", "neon glowing lights at night, cyberpunk colours"),
         ("pastel", "Pastel", "soft pastel colours, gentle and light"),
         ("moody", "Moody dark", "dark moody low-key lighting, deep shadows"),
         ("golden", "Golden hour", "warm golden hour sunlight, sunset glow"),
         ("bw", "Black & white", "black and white monochrome photograph"),
         ("vivid", "Vibrant", "vibrant saturated colourful image"),
         ("minimal", "Minimal white", "minimal clean white background, lots of empty space"),
         ("earthy", "Earthy", "earthy natural tones, browns, olive and sand"),
         ("teal_orange", "Teal & orange", "cinematic teal and orange colour grade"),
         ("dreamy", "Dreamy haze", "dreamy hazy soft focus, misty atmosphere")]


def _warm_dims():
    for r in db.conn().execute("SELECT thumb FROM files WHERE thumb IS NOT NULL AND thumb!=''").fetchall():
        _thumb_dims(r["thumb"])


def _warm_ideas():
    if not embedder.ready():          # first run: the model is fetched from the app, then this runs
        return
    try:
        suggest_mod.ideas()
        embedder.get_model()          # warm, so the first search is instant
        tags_mod.warm()
    except Exception:
        import traceback; traceback.print_exc()


@app.get("/api/ideas")
def ideas(n: int = 30):
    """Search ideas that this library can actually answer, strongest and most varied first."""
    return {"ideas": _idea_kinds(suggest_mod.ideas(n))}


def _idea_kinds(items):
    """Each idea carries its best matches as {id, kind} so the chip can show little thumbnails."""
    ids = {i for x in items for i in x["top"]}
    kinds = {r["id"]: r["kind"] for r in db.conn().execute(
        f"SELECT id, kind FROM files WHERE id IN ({','.join(map(str, ids)) or '0'})")}
    return [{**x, "top": [{"id": i, "kind": kinds.get(i, "image")} for i in x["top"]]} for x in items]


@app.get("/api/moods")
def moods():
    return [{"key": k, "label": l, "query": q} for k, l, q in MOODS]


@app.get("/api/name_groups")
def name_groups(min_count: int = 4):
    """Files whose names share a shape (e.g. 'tea chai india', 'ty #', 'download') and that no known tool claims."""
    from collections import defaultdict
    g = defaultdict(list)
    c = db.conn()
    for r in c.execute("SELECT id, name, path, width, height FROM files WHERE status='done'"):
        if names_mod.source_of_row(r) is None:
            g[names_mod.signature(r["name"])].append(r)
    out = []
    for sig, rs in sorted(g.items(), key=lambda kv: -len(kv[1])):
        if len(rs) < min_count or sig in ("#", "(unnamed)"):
            continue
        sample = [_file_dict(x) for x in c.execute(
            f"SELECT * FROM files WHERE id IN ({','.join(str(r['id']) for r in rs[:8])})")]
        out.append({"pattern": sig, "count": len(rs), "names": [r["name"] for r in rs[:3]],
                    "file_ids": [r["id"] for r in rs], "sample": sample})
        if len(out) >= 60:
            break
    return {"groups": out}


def _rank(qvec, root, kinds, limit, exclude_id=None, query_text=None, keep=None, neg=None):
    """qvec is one query vector, or several stacked (k x 768): then a file must fit every one of
    them, so the weakest match counts most (several search tags narrow the results)."""
    ids, mat = indexer.matrix()
    if len(ids) == 0:
        return []
    if qvec.ndim == 1:
        qvec = qvec[None, :]
    each = mat @ qvec.T                                       # files × tags
    z = (each - np.median(each, axis=0)) / (each.std(axis=0) + 1e-6)
    rank_by = 0.6 * z.min(axis=1) + 0.4 * z.mean(axis=1)      # every tag has to fit, each counts the same
    sims = each.mean(axis=1)                                  # what is shown as the match score
    if neg is not None:                                       # "without people": push those down
        nz = mat @ neg; nz = (nz - np.median(nz)) / (nz.std() + 1e-6)
        rank_by = rank_by - 0.8 * np.clip(nz - 0.5, 0, None)
    order = np.argsort(-rank_by)
    c = db.conn()
    tokens = [t for t in re.split(r"\W+", (query_text or "").lower()) if len(t) > 1]
    out = []
    # take a generous slice (everything when a filter may drop most of it), then filter + filename boost
    candidates = order if keep else order[: max(limit * 6, 600)]
    for start in range(0, len(candidates), 512):
        batch = candidates[start:start + 512]
        batch_ids = [int(ids[idx]) for idx in batch]
        rows = {r["id"]: r for r in c.execute(
            f"SELECT id,name,root_id,kind,size,width,height,duration,fps,mtime,taken,codec,pal FROM files "
            f"WHERE id IN ({','.join('?' for _ in batch_ids)})", batch_ids)}
        for idx, fid in zip(batch, batch_ids):
            if exclude_id and fid == exclude_id:
                continue
            r = rows.get(fid)
            if not r or (root and r["root_id"] != root) or (kinds and r["kind"] not in kinds):
                continue
            if keep and not keep(r):
                continue
            score, key = float(sims[idx]), float(rank_by[idx])
            if tokens and all(t in r["name"].lower() for t in tokens):
                key += 1.0   # exact filename hits float to the top
            out.append((key, score, fid))
            if keep and len(out) >= limit * 3:
                break
        if keep and len(out) >= limit * 3:
            break
    # Pictures whose own text (read by OCR) contains every word of the search float to the top, even when the model ranked them low.
    seen_ids = {fid for _, _, fid in out}
    for fid in _ocr_matches(tokens):
        if fid in seen_ids or (exclude_id and fid == exclude_id):
            continue
        r = c.execute("SELECT id,name,root_id,kind,size,width,height,duration,fps,mtime,taken,codec,pal FROM files WHERE id=? AND status='done'", (fid,)).fetchone()
        if not r or (root and r["root_id"] != root) or (kinds and r["kind"] not in kinds) or (keep and not keep(r)):
            continue
        out.append((1.5, 0.3, fid))
    out.sort(key=lambda x: -x[0])
    chosen = out[:limit]
    if not chosen:
        return []
    full = {r["id"]: r for r in c.execute(
        f"SELECT * FROM files WHERE id IN ({','.join('?' for _ in chosen)})", [fid for _, _, fid in chosen])}
    return [_file_dict(full[fid], score) for _, score, fid in chosen if fid in full]


def _ocr_matches(tokens) -> list[int]:
    """Ids of pictures whose recognised text contains every word (at least three letters each) of the search."""
    words = [t for t in tokens if len(t) >= 3][:6]
    if not words:
        return []
    try:
        rows = db.conn().execute("SELECT id FROM files WHERE ocr IS NOT NULL AND ocr != '' AND " + " AND ".join("ocr LIKE ?" for _ in words) + " LIMIT 300",
                                 [f"%{w}%" for w in words]).fetchall()
    except sqlite3.OperationalError:
        return []
    return [r[0] for r in rows]


def _when(r):
    """The day a file was taken (EXIF / creation time) when known, otherwise when it was modified."""
    taken = r["taken"] if "taken" in r.keys() else None
    return taken if taken and taken > 0 else r["mtime"]


def _search_filter(body):
    """Keep semantic results inside the same exact filters used by the library view."""
    color, source, project = body.get("color"), body.get("source"), body.get("project")
    filters = body.get("filters") or {}
    tagged = None
    if body.get("tag"):
        tagged = {r[0] for r in db.conn().execute("SELECT file_id FROM file_tags WHERE tag=?", (body["tag"],))}
    labelled = None
    if body.get("label"):
        wanted = [x for x in str(body["label"]).split(",") if x in LABELS]
        if wanted:
            labelled = {r[0] for r in db.conn().execute(
                f"SELECT file_id FROM file_labels WHERE label IN ({','.join('?' * len(wanted))})", wanted)}
    smallest, side = _min_side()
    if not (color or source or project or tagged is not None or labelled is not None or smallest or any(value is not None for value in filters.values())):
        return None
    members = None
    if project:
        members = _smart_space_ids(int(project))
        if members is None:
            members = {r[0] for r in db.conn().execute("SELECT file_id FROM project_files WHERE project_id=?", (project,))}

    def keep(r):
        if tagged is not None and r["id"] not in tagged:
            return False
        if labelled is not None and r["id"] not in labelled:
            return False
        if smallest and r["kind"] in ("image", "video") and r["width"] and r["height"] and \
                (min(r["width"], r["height"]) if side == "short" else max(r["width"], r["height"])) < smallest:
            return False
        for key, column, comparison in (("min_size", "size", lambda a, b: a >= b),
                                        ("max_size", "size", lambda a, b: a <= b),
                                        ("min_width", "width", lambda a, b: a >= b),
                                        ("min_height", "height", lambda a, b: a >= b),
                                        ("min_duration", "duration", lambda a, b: a >= b),
                                        ("max_duration", "duration", lambda a, b: a <= b),
                                        ("min_fps", "fps", lambda a, b: a >= b),
                                        ("after", "taken", lambda a, b: a >= b),
                                        ("before", "taken", lambda a, b: a < b)):
            value = filters.get(key)
            have = _when(r) if column == "taken" else r[column]
            if value is not None and (have is None or not comparison(have, value)):
                return False
        if filters.get("min_pixels") is not None and (r["width"] is None or r["height"] is None or r["width"] * r["height"] < filters["min_pixels"]):
            return False
        if filters.get("codec") and r["codec"] != filters["codec"].lower():
            return False
        shape = filters.get("aspect")
        if shape:
            w, h = r["width"], r["height"]
            if not w or not h or not {"portrait": w < h, "landscape": w > h,
                                      "square": .9 <= w / h <= 1.1, "wide": w >= h * 1.7}.get(shape, False):
                return False
        if color and colors_mod.shares(r["pal"]).get(color, 0) < colors_mod.MIN_SHARE:
            return False
        if source and (names_mod.source_of_row(r) or "none") != source:
            return False
        return members is None or r["id"] in members
    return keep


@app.post("/api/search")
def search(body: dict = Body(...)):
    kinds = body.get("kinds") or None
    root = body.get("root") or None
    limit = int(body.get("limit", 100))
    offset = int(body.get("offset", 0))
    keep = _search_filter(body)
    words = str(body.get("text") or "").strip()[:300]
    weight = body.get("text_weight")
    if body.get("reference_id"):
        fid = int(body["reference_id"])
        row = db.conn().execute("SELECT path,kind FROM files WHERE id=? AND status='done'", (fid,)).fetchone()
        if row is None or not Path(row["path"]).is_file():
            raise HTTPException(404, "Reference file is no longer indexed")
        try:
            q = embedder.embed_reference(Path(row["path"]), row["kind"], "")
        except (OSError, ValueError) as exc:
            raise HTTPException(400, str(exc)) from exc
        if words:
            q = search_mix.combine(q, embedder.embed_query(words), weight)
        return {"files": _rank(q, root, kinds, limit + offset, exclude_id=fid, keep=keep)[offset:]}
    if body.get("similar_to"):
        fid = int(body["similar_to"])
        r = db.conn().execute("SELECT vec FROM files WHERE id=?", (fid,)).fetchone()
        if not r or r["vec"] is None:
            raise HTTPException(400, "File not indexed yet")
        q = np.frombuffer(r["vec"], dtype=np.float32)
        if words:                                      # a reference plus words: "like this, but at night"
            q = search_mix.combine(q, embedder.embed_query(words), weight)
        return {"files": _rank(q, root, kinds, limit + offset, exclude_id=fid, keep=keep)[offset:]}
    qs = [q.strip() for q in (body.get("qs") or [body.get("q") or ""]) if q and q.strip()]
    if not qs:
        return {"files": []}
    its = [understand_mod.understand(q, **_ask_context(body)) for q in qs]
    merged, flt = dict(body), dict(body.get("filters") or {})
    for it in its:                                   # what was said joins what was already chosen; chosen filters win
        for k, v in it["filters"].items():
            flt.setdefault(k, v)
        if it["color"] and not merged.get("color"):
            merged["color"] = it["color"]
        if it["source"] and not merged.get("source"):
            merged["source"] = it["source"]
        if it["favourite"] and not merged.get("tag"):
            merged["tag"] = FAVOURITE
        if it["kinds"] and not kinds:
            kinds = it["kinds"]
    merged["filters"] = flt
    keep = _search_filter(merged)
    chips = [c for it in its for c in it["chips"]]
    if not any(it["text"] for it in its) and (kinds or flt or merged.get("color") or merged.get("source") or merged.get("tag")):
        # "files from 1 month ago", "red photos": nothing to look for by meaning, so these are just the matching files
        params = {k: v for k, v in flt.items() if k in _DETAIL_PARAMS}
        found = files(kind=kinds[0] if kinds and len(kinds) == 1 else None, color=merged.get("color"), source=merged.get("source"),
                      tag=merged.get("tag"), root=root, limit=limit + offset, **params)["files"][offset:]
        return {"files": found, "understood": chips}
    vecs, negs, texts = [], [], []
    for q, it in zip(qs, its):
        if it["without"]:
            negs.append(embedder.embed_query(it["without"]))
        text = it["text"] or {"video": "a video", "image": "a photo", "audio": "a sound"}.get(kinds[0] if kinds else "", q)
        vecs.append(embedder.embed_query(text)); texts.append(text)
    qvec = vecs[0] if len(vecs) == 1 else np.vstack(vecs)
    neg = None
    if negs:
        neg = np.mean(negs, axis=0); neg /= np.linalg.norm(neg) or 1
    found = _rank(qvec, root, kinds, limit + offset, query_text=texts[0] if len(vecs) == 1 and texts else None, keep=keep, neg=neg)[offset:]
    return {"files": found, "understood": chips}


_DETAIL_PARAMS = {"after", "before", "min_size", "max_size", "min_width", "min_height", "min_pixels", "min_duration", "max_duration", "min_fps", "aspect", "codec"}


def _ask_context(body: dict) -> dict:
    """The clock and region the person is in, so "yesterday" and "5/8" mean what they mean to them."""
    return {"now": float(body.get("now") or time.time()), "utc_offset_min": int(body.get("utc_offset_min") or 0),
            "day_first": bool(body.get("day_first", True))}


@app.post("/api/understand")
def understand_endpoint(body: dict = Body(...)):
    """Reads a typed or spoken question into exact filters plus the words left for the meaning search."""
    return understand_mod.understand(str(body.get("text") or "")[:400], **_ask_context(body))


@app.get("/api/model")
def model_status():
    return fetch_mod.status()


@app.post("/api/model/download")
def model_download():
    import threading
    fetch_mod.start(on_done=lambda: threading.Thread(target=_warm_ideas, daemon=True).start())
    return fetch_mod.status()


@app.get("/api/voice/model")
def voice_model():
    """The two voice models (quick for live words, careful for the final text): downloaded or not, and progress."""
    return voice_stream.status()


@app.post("/api/voice/model")
def voice_model_prepare():
    return voice_stream.prepare()


@app.websocket("/api/voice/stream")
async def voice_stream_ws(ws: WebSocket):
    """Live dictation. The HTTP guard does not see WebSockets, so the token is checked here, and a browser page
    (which always sends an Origin header) is refused."""
    token = ws.query_params.get("fm_token") or ws.headers.get("x-filemind-token") or ""
    host = ws.headers.get("host", "")
    if ws.headers.get("origin") or (host.rsplit(":", 1)[0] not in _allowed_hosts() and host not in _allowed_hosts()) \
            or (_TOKEN and not hmac.compare_digest(token, _TOKEN)):
        await ws.close(code=1008)
        return
    await ws.accept()
    await voice_stream.run(ws, lambda text, ctx: understand_mod.understand(text, **ctx))


@app.post("/api/index/speed")
def index_speed(body: dict = Body(...)):
    """How hard indexing works the PC: quiet, balanced or fast. Takes effect immediately, even mid-run."""
    try:
        return {"level": indexer.set_speed(str(body.get("level", "")))}
    except ValueError as e:
        raise HTTPException(400, str(e))


@app.post("/api/search/image")
async def search_by_image(file: UploadFile = File(...), root: int | None = None, limit: int = 100,
                          text: str = "", text_weight: float = search_mix.DEFAULT_TEXT_WEIGHT):
    tmp = extract.TMP_DIR / f"query_{int(time.time()*1000)}{Path(file.filename).suffix}"
    tmp.write_bytes(await file.read())
    try:
        kind = extract.kind_of(Path(file.filename).suffix.lower())
        if kind not in ("image", "video", "audio"):
            raise HTTPException(400, "Drop a picture, a video clip or a sound to find things that look or sound like it")
        q = embedder.embed_reference(tmp, kind, "")
        if text.strip():
            q = search_mix.combine(q, embedder.embed_query(text.strip()[:300]), text_weight)
    finally:
        tmp.unlink(missing_ok=True)
    return {"files": _rank(q, root, None, limit)}


@app.get("/api/stats")
def stats():
    c = db.conn()
    kinds = {r["kind"]: r["n"] for r in c.execute("SELECT kind, COUNT(*) n FROM files WHERE status='done' GROUP BY kind")}
    return {"total": sum(kinds.values()), "kinds": kinds,
            "unassigned": c.execute("SELECT COUNT(*) FROM files WHERE status='done' AND id NOT IN (SELECT file_id FROM project_files)").fetchone()[0],
            "renames": c.execute("SELECT COUNT(*) FROM proposed_names").fetchone()[0]}


@app.get("/api/duplicates")
def duplicate_candidates(focus: int | None = None, rescan: bool = False, cached_only: bool = False):
    """The last scan is kept in the index: opening the page costs nothing. rescan=true (or a library that changed
    since) recomputes, and files that have not changed reuse their stored fingerprints."""
    c = db.conn()
    gen = int(c.execute("SELECT value FROM meta WHERE key='index_gen'").fetchone()[0])
    cached = None if focus is not None else c.execute("SELECT gen, created, result FROM dup_cache WHERE id=1").fetchone()
    if cached and not rescan:
        out = _dup_payload(json.loads(cached["result"]))
        out.update(stale=cached["gen"] != gen, scanned=cached["created"], cached=True)
        return out
    if cached_only:
        return {"exact": [], "visual": [], "total_exact": 0, "total_visual": 0, "stale": False, "scanned": 0, "cached": True}
    result = duplicates_mod.groups(focus=focus)
    if focus is None:
        c.execute("INSERT OR REPLACE INTO dup_cache(id, gen, created, result) VALUES (1, ?, ?, ?)",
                  (gen, time.time(), json.dumps(result)))
        c.commit()
    out = _dup_payload(result)
    out.update(stale=False, scanned=time.time(), cached=False)
    return out


def _dup_payload(result):
    ids = {fid for category in ("exact", "visual") for group in result[category] for fid in group}
    c = db.conn()
    rows = {r["id"]: _file_dict(r) for r in c.execute(
        f"SELECT * FROM files WHERE id IN ({','.join('?' for _ in ids) or 'NULL'})", tuple(ids))}
    return {"exact": [[rows[fid] for fid in group if fid in rows] for group in result["exact"]],
            "visual": [[rows[fid] for fid in group if fid in rows] for group in result["visual"]],
            "exact_info": duplicates_mod.describe(result["exact"], True),
            "visual_info": duplicates_mod.describe(result["visual"], False),
            "total_exact": result["total_exact"], "total_visual": result["total_visual"]}


@app.post("/api/moments/search")
def search_moments(body: dict = Body(...)):
    hits = moments_mod.search(body.get("q", ""), int(body.get("limit", 40)))
    c = db.conn()
    for h in hits:                                  # the apps need the whole file to open it at that moment
        row = c.execute("SELECT * FROM files WHERE id=?", (h["file_id"],)).fetchone()
        if row is not None:
            h["file"] = _file_dict(row)
    return {"moments": [h for h in hits if "file" in h]}


@app.post("/api/moments/queue")
def moments_queue(body: dict = Body(...)):
    """{ids} or {all: true}: analyse these videos in the background so searches can find moments inside them."""
    ids = [int(i) for i in body.get("ids") or []][:5000]
    return moments_mod.queue(ids, bool(body.get("all")))


@app.get("/api/moments/queue")
def moments_queue_status():
    return moments_mod.queue_status()


@app.get("/api/moments/{fid}/frame")
def moment_frame(fid: int, t: float = 0):
    try:
        out = moments_mod.frame_at(fid, t)
    except FileNotFoundError as exc:
        raise HTTPException(404, str(exc)) from exc
    if out is None:
        raise HTTPException(404, "No picture for that moment.")
    return FileResponse(out, media_type="image/jpeg")


@app.get("/api/moments/{fid}")
def moment_status(fid: int):
    try:
        return moments_mod.status(fid)
    except FileNotFoundError as exc:
        raise HTTPException(404, str(exc)) from exc


@app.post("/api/moments/{fid}")
def analyze_moments(fid: int):
    try:
        return moments_mod.start(fid)
    except FileNotFoundError as exc:
        raise HTTPException(404, str(exc)) from exc


@app.get("/api/ocr/pending")
def ocr_pending(limit: int = 20, mode: str = "screens"):
    """Pictures still to be read for text. 'screens' means screenshots and PNGs; 'all' every picture; 'off' none."""
    if mode == "off":
        return {"files": []}
    where = "kind='image' AND status='done' AND seq_json IS NULL AND (ocr_key IS NULL OR ocr_key != (size || ':' || mtime))"
    limit = max(1, min(limit, 100))
    if mode == "all":
        rows = db.conn().execute(f"SELECT id, path FROM files WHERE {where} ORDER BY mtime DESC LIMIT ?", (limit,)).fetchall()
    else:                                           # screenshots: by name, by folder, or by an exact screen size
        rows = [r for r in db.conn().execute(f"SELECT id, name, path, width, height FROM files WHERE {where} ORDER BY mtime DESC LIMIT 3000")
                if names_mod.source_of_row(r) == "screenshot"][:limit]
    return {"files": [{"id": r["id"], "path": r["path"]} for r in rows]}


@app.post("/api/ocr")
def ocr_store(body: dict = Body(...)):
    """{id, text}: the text a picture contains (empty is fine: it will not be asked for again until the file changes)."""
    c = db.conn()
    r = c.execute("SELECT size, mtime FROM files WHERE id=?", (int(body.get("id") or 0),)).fetchone()
    if not r:
        raise HTTPException(404)
    text = " ".join(str(body.get("text") or "").split())[:20000]
    c.execute("UPDATE files SET ocr=?, ocr_key=? WHERE id=?", (text, f"{r['size']}:{r['mtime']}", int(body["id"])))
    c.commit()
    return {"ok": True}


@app.get("/api/info/{fid}")
def info(fid: int):
    c = db.conn()
    r = c.execute("SELECT * FROM files WHERE id=?", (fid,)).fetchone()
    if not r:
        raise HTTPException(404)
    colors = []
    if r["thumb"]:
        try:
            from PIL import Image
            with Image.open(extract.THUMB_DIR / r["thumb"]) as im:
                im = im.convert("RGB").resize((120, 120))
                q = im.quantize(colors=12, method=Image.Quantize.MEDIANCUT)
                pal = q.getpalette()
                rgb = [tuple(pal[i * 3:i * 3 + 3]) for n, i in q.getcolors() if n > 40]      # ignore specks
                rgb.sort(key=lambda c: 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2])  # dark to light
                colors = ["#%02x%02x%02x" % c for c in rgb]
        except Exception as e:
            print(f"File Mind: could not read the colours of file {fid}: {e}", flush=True)
    projs = [{"id": p["id"], "name": p["name"]} for p in c.execute(
        "SELECT p.* FROM projects p JOIN project_files pf ON pf.project_id=p.id WHERE pf.file_id=?", (fid,))]
    for p in c.execute("SELECT p.id,p.name FROM projects p JOIN smart_spaces s ON s.project_id=p.id"):
        if fid in _smart_space_ids(p["id"]):
            projs.append({"id": p["id"], "name": p["name"]})
    pn = c.execute("SELECT new_name FROM proposed_names WHERE file_id=?", (fid,)).fetchone()
    d = _file_dict(r)
    d["ocr"] = (r["ocr"] or "")[:4000] if "ocr" in r.keys() else ""
    d.update(colors=colors, projects=projs, proposed=pn["new_name"] if pn else None,
             folder=os.path.dirname(r["path"]), error=r["error"],
             dimensions=_original_dimensions(r["path"], r["kind"], r["mtime"] or 0),
             meta=names_mod.parse(r["name"]),
             tags=tags_mod.for_vector(np.frombuffer(r["vec"], dtype=np.float32)) if r["vec"] is not None else [],
             swatches=[{"key": k, "share": v, "swatch": dict((x, h) for x, _, h in colors_mod.SWATCHES)[k]}
                       for k, v in colors_mod.shares(r["pal"]).items() if v >= 0.06][:5])
    return d


@lru_cache(maxsize=4096)
def _original_dimensions(path: str, kind: str, mtime: float) -> list[int] | None:
    """Read dimensions from the original only when its details are opened."""
    try:
        if kind == "image":
            from PIL import Image
            with Image.open(path) as image:
                return list(image.size)
        if kind == "video":
            probe = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
                                    "stream=width,height", "-of", "json", path], capture_output=True,
                                   text=True, timeout=5, check=True)
            stream = json.loads(probe.stdout).get("streams", [{}])[0]
            if stream.get("width") and stream.get("height"):
                return [int(stream["width"]), int(stream["height"])]
    except (OSError, ValueError, IndexError, KeyError, subprocess.SubprocessError):
        pass
    return None


@app.get("/api/transcript/{fid}")
def transcript_status(fid: int):
    try:
        return transcribe_mod.status(fid)
    except FileNotFoundError as exc:
        raise HTTPException(404, str(exc)) from exc


@app.post("/api/transcript/{fid}")
def transcript_start(fid: int):
    try:
        return transcribe_mod.start(fid)
    except FileNotFoundError as exc:
        raise HTTPException(404, str(exc)) from exc


@app.get("/api/wave/{fid}")
def wave(fid: int):
    """Real waveform peaks (48 bars, 0..1) and duration for an audio file; cached on disk."""
    import json
    r = db.conn().execute("SELECT path, size, mtime FROM files WHERE id=?", (fid,)).fetchone()
    if not r or not os.path.exists(r["path"]):
        raise HTTPException(404)
    cache_dir = db.DATA_DIR / "waves"
    cache_dir.mkdir(exist_ok=True)
    cf = cache_dir / f"{fid}_{r['size']}_{int(r['mtime'])}.json"
    if cf.exists():
        return json.loads(cf.read_text())
    N = 48
    peaks, dur = [0.3] * N, 0.0
    try:
        p = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", r["path"]],
                           capture_output=True, text=True, timeout=30)
        dur = float(p.stdout.strip() or 0)
        raw = subprocess.run(["ffmpeg", "-v", "error", "-t", "1800", "-i", r["path"], "-ac", "1", "-ar", "2000", "-f", "s16le", "-"],
                             capture_output=True, timeout=120).stdout
        a = np.abs(np.frombuffer(raw, dtype=np.int16).astype(np.float32))
        if len(a) >= N:
            bins = np.array_split(a, N)
            pk = np.array([b.max() for b in bins])
            pk = (pk / (pk.max() or 1)) ** 0.6          # gentle curve so quiet parts stay visible
            peaks = [round(float(max(0.06, x)), 3) for x in pk]
    except Exception as e:
        print(f"File Mind: no waveform for file {fid}: {e}", flush=True)
    out = {"peaks": peaks, "duration": dur}
    cf.write_text(json.dumps(out))
    return out


@app.get("/api/thumb/{fid}")
def thumb(fid: int):
    r = db.conn().execute("SELECT thumb FROM files WHERE id=?", (fid,)).fetchone()
    if not r or not r["thumb"]:
        raise HTTPException(404)
    return FileResponse(extract.THUMB_DIR / r["thumb"], headers={"Cache-Control": "max-age=86400"})


@app.get("/api/file/{fid}")
def open_file(fid: int):
    r = db.conn().execute("SELECT path, seq_json, fps FROM files WHERE id=?", (fid,)).fetchone()
    if not r or not os.path.exists(r["path"]):
        raise HTTPException(404)
    if r["seq_json"]:                                   # a sequence plays as its preview video (made by /api/sequence/{id}/proxy)
        info = sequences_mod.load(r["seq_json"])
        video = sequences_mod.proxy_path(info, r["fps"] or sequences_mod.DEFAULT_FPS) if info else None
        if video is None or not video.exists():
            raise HTTPException(404, "The preview of this sequence has not been made yet.")
        return FileResponse(video, media_type="video/mp4")
    return FileResponse(r["path"])


@app.post("/api/sequence/{fid}/proxy")
def sequence_proxy(fid: int):
    """Makes the preview video of a sequence (a minute at most for a long one) so it can be played."""
    r = db.conn().execute("SELECT seq_json, fps FROM files WHERE id=?", (fid,)).fetchone()
    info = sequences_mod.load(r["seq_json"]) if r else None
    if not info:
        raise HTTPException(404, "That is not an image sequence.")
    try:
        sequences_mod.build_proxy(info, r["fps"] or sequences_mod.DEFAULT_FPS)
    except Exception as exc:
        logging.getLogger("filemind").warning("sequence preview failed", exc_info=True)
        raise HTTPException(500, f"The preview could not be made: {exc}") from exc
    return {"ok": True}


@app.post("/api/sequence/{fid}/fps")
def sequence_fps(fid: int, body: dict = Body(...)):
    """{fps}: the playback speed of a sequence (the preview is made again at that speed)."""
    fps = float(body.get("fps") or 0)
    if not 1 <= fps <= 240:
        raise HTTPException(400, "Choose a speed between 1 and 240 frames per second.")
    c = db.conn()
    r = c.execute("SELECT seq_json FROM files WHERE id=?", (fid,)).fetchone()
    info = sequences_mod.load(r["seq_json"]) if r else None
    if not info:
        raise HTTPException(404, "That is not an image sequence.")
    c.execute("UPDATE files SET fps=?, duration=? WHERE id=?", (fps, info["count"] / fps, fid))
    c.commit()
    return {"fps": fps}


@app.post("/api/file_actions")
def file_actions(body: dict = Body(...)):
    """Apply a deliberate file action to indexed originals; never overwrite a destination."""
    action = body.get("action")
    ids = body.get("file_ids")
    if action not in {"copy", "move", "delete"} or not isinstance(ids, list) or not ids or len(ids) > 500 or any(type(i) is not int for i in ids):
        raise HTTPException(400, "Choose files and a valid action.")
    ids = list(dict.fromkeys(ids))
    c = db.conn()
    files = [c.execute("SELECT id, path, seq_json FROM files WHERE id=?", (fid,)).fetchone() for fid in ids]
    if any(r is None or not Path(r["path"]).is_file() for r in files):
        raise HTTPException(404, "One or more files are no longer at their indexed locations.")

    def paths_of(r):                                    # a sequence is every one of its frames
        info = sequences_mod.load(r["seq_json"]) if r["seq_json"] else None
        return sequences_mod.frame_paths(info) if info else [r["path"]]
    if action == "delete":
        done = 0
        for r in files:
            try:
                for p in paths_of(r):
                    if os.path.exists(p):
                        send2trash(p)
            except OSError as exc:
                raise HTTPException(500, f"Moved {done} files to Trash; the next file failed: {exc}") from exc
            _drop_files(c, "id=?", (r["id"],))      # the row, its tags, moments, names and thumbnail
            done += 1
        return {"done": done, "action": action}
    destination = Path(str(body.get("destination") or "")).expanduser()
    if not destination.is_dir():
        raise HTTPException(400, "Choose an existing destination folder.")
    ops = []
    for r in files:
        if r["seq_json"]:                               # every frame, under its own name; the row itself is re-read afterwards
            ops += [{"old": p, "new": str(destination / Path(p).name), "file_id": 0} for p in paths_of(r)]
        else:
            ops.append({"old": r["path"], "new": str(destination / Path(r["path"]).name), "file_id": r["id"]})
    if len({os.path.normcase(op["new"]) for op in ops}) != len(ops) or any(os.path.exists(op["new"]) for op in ops):
        raise HTTPException(409, "A file with that name already exists in the destination. Nothing was changed.")
    result = commit_mod.run(ops, action)
    if action == "move":
        for r in files:
            if r["seq_json"]:
                _drop_files(c, "id=?", (r["id"],))      # the next folder scan registers it again at its new place
    return result


@app.post("/api/reveal/{fid}")
def reveal(fid: int):
    r = db.conn().execute("SELECT path FROM files WHERE id=?", (fid,)).fetchone()
    if not r:
        raise HTTPException(404)
    p = os.path.normpath(r["path"])
    if not os.path.exists(p):
        raise HTTPException(404, "That file is no longer at its old location.")
    if os.name == "nt":
        # Explorer wants the whole thing as ONE argument:  /select,"C:\path with spaces\file.png"
        subprocess.Popen(f'explorer /select,"{p}"')
        return {"ok": True, "where": "File Explorer"}
    if sys.platform == "darwin":
        subprocess.Popen(["open", "-R", p])
        return {"ok": True, "where": "Finder"}
    try:                                              # Linux: highlight it if the file manager supports that
        ok = subprocess.run(["dbus-send", "--session", "--print-reply", "--dest=org.freedesktop.FileManager1",
                             "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                             f"array:string:{Path(p).as_uri()}", "string:"], capture_output=True, timeout=3).returncode == 0
    except Exception:
        ok = False
    if not ok:
        subprocess.Popen(["xdg-open", os.path.dirname(p)])
    return {"ok": True, "where": "your file manager"}


# ---------------------------------------------------------------- projects (virtual)
@app.get("/api/projects")
def projects():
    c = db.conn()
    out = []
    for p in c.execute("SELECT * FROM projects ORDER BY name COLLATE NOCASE").fetchall():
        smart_ids = _smart_space_ids(p["id"])
        if smart_ids is None:
            n = c.execute("SELECT COUNT(*) FROM project_files WHERE project_id=?", (p["id"],)).fetchone()[0]
            cover = c.execute("SELECT f.id, f.kind FROM project_files pf JOIN files f ON f.id=pf.file_id "
                              "WHERE pf.project_id=? AND ((f.thumb IS NOT NULL AND f.thumb!='') OR f.kind='audio') ORDER BY f.mtime DESC LIMIT 3", (p["id"],)).fetchall()
        else:
            n = len(smart_ids)
            cover = c.execute(f"SELECT id,kind FROM files WHERE id IN ({','.join('?' for _ in smart_ids) or 'NULL'}) "
                              "AND ((thumb IS NOT NULL AND thumb!='') OR kind='audio') ORDER BY mtime DESC LIMIT 3", tuple(smart_ids)).fetchall()
        out.append({"id": p["id"], "name": p["name"], "count": n, "smart": smart_ids is not None,
                    "cover": [{"id": r["id"], "kind": r["kind"]} for r in cover]})
    return out


@app.post("/api/projects")
def create_project(body: dict = Body(...)):
    name = (body.get("name") or "").strip()
    if not name:
        raise HTTPException(400, "Name required")
    spec, vec = None, None
    if body.get("smart"):
        spec = body["smart"]
        if not isinstance(spec, dict):
            raise HTTPException(400, "Invalid live-space search")
        queries = spec.get("qs") or []
        if not isinstance(queries, list) or len(queries) > 5 or any(not isinstance(q, str) or len(q) > 300 for q in queries):
            raise HTTPException(400, "Live spaces need up to five text searches")
        if queries:
            vectors = [embedder.embed_query(q.strip()) for q in queries if q.strip()]
            if vectors:
                vec = embedder._norm(np.mean(vectors, axis=0)).astype(np.float32).tobytes()
        allowed = {"qs", "root", "kind", "color", "source", "after", "before", "min_size", "max_size",
                   "min_width", "min_height", "min_pixels", "min_duration", "max_duration", "min_fps", "aspect", "codec"}
        spec = {key: value for key, value in spec.items() if key in allowed and value is not None}
    c = db.conn()
    cur = c.execute("INSERT INTO projects(name, created) VALUES(?,?)", (name, time.time()))
    pid = cur.lastrowid
    if spec is not None:
        c.execute("INSERT INTO smart_spaces VALUES(?,?,?)", (pid, json.dumps(spec), vec))
    else:
        for fid in body.get("file_ids", []):
            c.execute("INSERT OR IGNORE INTO project_files VALUES(?,?)", (pid, fid))
    c.commit()
    return {"id": pid}


@app.patch("/api/projects/{pid}")
def rename_project(pid: int, body: dict = Body(...)):
    db.conn().execute("UPDATE projects SET name=? WHERE id=?", (body["name"].strip(), pid))
    db.conn().commit()
    return {"ok": True}


@app.delete("/api/projects/{pid}")
def delete_project(pid: int):
    c = db.conn()
    c.execute("DELETE FROM project_files WHERE project_id=?", (pid,))
    c.execute("DELETE FROM smart_spaces WHERE project_id=?", (pid,))
    _dismissals_table(c)
    c.execute("DELETE FROM space_dismissals WHERE project_id=?", (pid,))
    c.execute("DELETE FROM projects WHERE id=?", (pid,))
    c.commit()
    return {"ok": True}


@app.post("/api/projects/{pid}/add")
def project_add(pid: int, body: dict = Body(...)):
    c = db.conn()
    if c.execute("SELECT 1 FROM smart_spaces WHERE project_id=?", (pid,)).fetchone():
        raise HTTPException(400, "A live space updates from its search; add files to a regular space instead")
    for fid in body["file_ids"]:
        c.execute("INSERT OR IGNORE INTO project_files VALUES(?,?)", (pid, fid))
    c.commit()
    return {"ok": True}


def _dismissals_table(c):
    c.execute("CREATE TABLE IF NOT EXISTS space_dismissals (project_id INTEGER NOT NULL, file_id INTEGER NOT NULL, PRIMARY KEY(project_id, file_id))")


@app.get("/api/projects/{pid}/suggestions")
def project_suggestions(pid: int, limit: int = 40):
    """Files that probably belong in this space: nearest to the average of its members, not yet members, not
    dismissed for this space, and similar enough (space_suggest.MIN_SIMILARITY). Nothing is added here."""
    c = db.conn()
    if c.execute("SELECT 1 FROM projects WHERE id=?", (pid,)).fetchone() is None:
        raise HTTPException(404, "No such space")
    _dismissals_table(c)
    if c.execute("SELECT 1 FROM smart_spaces WHERE project_id=?", (pid,)).fetchone():
        return {"files": []}                                   # a live space fills itself from its search
    members = [r[0] for r in c.execute("SELECT file_id FROM project_files WHERE project_id=?", (pid,))]
    dismissed = [r[0] for r in c.execute("SELECT file_id FROM space_dismissals WHERE project_id=?", (pid,))]
    ids, mat = indexer.matrix()
    found = space_suggest.suggest(ids, mat, members, dismissed, max(1, min(int(limit), 200)))
    if not found:
        return {"files": []}
    rows = {r["id"]: r for r in c.execute(
        f"SELECT * FROM files WHERE status='done' AND id IN ({','.join('?' for _ in found)})", [fid for fid, _ in found])}
    return {"files": [_file_dict(rows[fid], score) for fid, score in found if fid in rows]}


@app.post("/api/projects/{pid}/suggestions/dismiss")
def project_suggestions_dismiss(pid: int, body: dict = Body(...)):
    c = db.conn()
    if c.execute("SELECT 1 FROM projects WHERE id=?", (pid,)).fetchone() is None:
        raise HTTPException(404, "No such space")
    _dismissals_table(c)
    for fid in body.get("file_ids", []):
        c.execute("INSERT OR IGNORE INTO space_dismissals VALUES(?,?)", (pid, int(fid)))
    c.commit()
    return {"ok": True}


@app.post("/api/spaces/match")
def spaces_match(body: dict = Body(...)):
    """Which files fit these short descriptions ("cats", "dogs at the beach")? Read only: nothing is created here.
    Returns every matching id (best first, at most space_match.MAX_FILES), a preview of the first few files and how many
    fit each description."""
    raw = body.get("descriptions")
    texts = [t.strip() for t in raw if isinstance(t, str) and t.strip()] if isinstance(raw, list) else []
    if not texts or len(texts) > 8 or any(len(t) > 200 for t in texts):
        raise HTTPException(400, "Give one to eight short descriptions")
    ids, mat = indexer.matrix()
    found, per = space_match.match(ids, mat, [embedder.embed_query(t) for t in texts])
    c = db.conn()
    wanted = [fid for fid, _ in found[:12]]
    rows = {r["id"]: r for r in c.execute(
        f"SELECT * FROM files WHERE id IN ({','.join('?' for _ in wanted) or 'NULL'})", wanted)}
    score = dict(found)
    return {"file_ids": [fid for fid, _ in found], "count": len(found),
            "per_description": per, "preview": [_file_dict(rows[f], score[f]) for f in wanted if f in rows]}


@app.post("/api/projects/{pid}/remove")
def project_remove(pid: int, body: dict = Body(...)):
    c = db.conn()
    for fid in body["file_ids"]:
        c.execute("DELETE FROM project_files WHERE project_id=? AND file_id=?", (pid, fid))
    c.commit()
    return {"ok": True}


# ---------------------------------------------------------------- proposed names
@app.put("/api/names")
def set_name(body: dict = Body(...)):
    c = db.conn()
    for fid in body["file_ids"]:
        c.execute("INSERT OR REPLACE INTO proposed_names VALUES(?,?)", (fid, body["new_name"]))
    c.commit()
    return {"ok": True}


@app.post("/api/names/clear")
def clear_names(body: dict = Body(...)):
    c = db.conn()
    for fid in body["file_ids"]:
        c.execute("DELETE FROM proposed_names WHERE file_id=?", (fid,))
    c.commit()
    return {"ok": True}


@app.post("/api/names/pattern")
def name_pattern(body: dict = Body(...)):
    """Bulk proposal. Pattern tokens: {name} original stem, {project}, {n} counter, {date} modified date."""
    c = db.conn()
    pattern = body["pattern"]
    ids = body["file_ids"]
    proj = body.get("project", "")
    start = int(body.get("start", 1))
    rows = {r["id"]: r for r in c.execute(
        f"SELECT * FROM files WHERE id IN ({','.join('?'*len(ids))})", ids)} if ids else {}
    n = start
    for fid in ids:
        r = rows.get(fid)
        if not r:
            continue
        stem = os.path.splitext(r["name"])[0]
        date = time.strftime("%Y-%m-%d", time.localtime(r["mtime"]))
        out = (pattern.replace("{name}", stem).replace("{project}", proj)
               .replace("{date}", date))
        out = re.sub(r"\{n(?::(\d+))?\}", lambda m: str(n).zfill(int(m.group(1) or 1)), out)
        c.execute("INSERT OR REPLACE INTO proposed_names VALUES(?,?)", (fid, out + r["ext"]))
        n += 1
    c.commit()
    return {"ok": True}


@app.post("/api/cluster")
def cluster(body: dict = Body(...)):
    """Propose broad, named visual and source collections; never create tiny unnamed clusters."""
    root = body.get("root")
    c = db.conn()
    eligible = {int(r["id"]): r for r in c.execute(
        "SELECT * FROM files WHERE status='done' AND (? IS NULL OR root_id=?)", (root, root))}
    if body.get("unassigned", False):
        assigned = {int(r[0]) for r in c.execute("SELECT DISTINCT file_id FROM project_files")}
        eligible = {fid: row for fid, row in eligible.items() if fid not in assigned}
    if len(eligible) < 5:
        return {"clusters": [], "leftover": len(eligible)}
    discover_mod.groups()
    theme_groups = [g for g in discover_mod._cache["groups"] if g["key"].startswith("theme:")]
    source_groups = [g for g in discover_mod._cache["groups"] if g["key"].startswith("source:")]
    media_groups = [g for g in discover_mod._cache["groups"] if g["key"] in ("type:video", "type:audio")]
    groups = theme_groups + source_groups[:14] + media_groups
    claimed = set()
    out = []
    for group in groups:
        fids = [fid for fid in discover_mod.file_ids(group["key"]) if fid in eligible and fid not in claimed]
        fids.sort(key=lambda fid: eligible[fid]["mtime"] or 0, reverse=True)
        with_cover = [fid for fid in fids if eligible[fid]["thumb"] or eligible[fid]["kind"] == "audio"]
        if len(fids) < 5 or not with_cover:
            continue
        claimed.update(fids)
        out.append({"name": group["title"], "count": len(fids), "file_ids": fids,
                    "sample": [_file_dict(eligible[fid]) for fid in with_cover[:8]]})
    out.sort(key=lambda g: -g["count"])
    return {"clusters": out[:40], "leftover": len(eligible) - len(claimed)}


@app.post("/api/projects/regroup")
def regroup_projects(body: dict = Body(...)):
    """Replace selected spaces atomically and keep a complete local undo snapshot."""
    groups = body.get("groups") or []
    replace_ids = [int(pid) for pid in body.get("replace_ids") or []]
    if len(groups) > 40 or any(not isinstance(g.get("name"), str) or not g["name"].strip()
                              or len(g.get("file_ids", [])) < 5 for g in groups):
        raise HTTPException(400, "Select up to 40 named groups of at least five files")
    c = db.conn()
    existing = {int(r[0]) for r in c.execute("SELECT id FROM projects")}
    if not set(replace_ids) <= existing:
        raise HTTPException(400, "One or more Spaces no longer exist")
    c.execute("CREATE TABLE IF NOT EXISTS regroup_backups (id INTEGER PRIMARY KEY, created REAL NOT NULL, payload TEXT NOT NULL)")
    snapshot = []
    for pid in replace_ids:
        p = c.execute("SELECT id,name,created FROM projects WHERE id=?", (pid,)).fetchone()
        smart = c.execute("SELECT 1 FROM smart_spaces WHERE project_id=?", (pid,)).fetchone()
        if smart:
            raise HTTPException(400, "Live Spaces cannot be replaced by auto-group")
        snapshot.append({"id": pid, "name": p["name"], "created": p["created"],
                         "file_ids": [int(r[0]) for r in c.execute("SELECT file_id FROM project_files WHERE project_id=?", (pid,))]})
    c.execute("INSERT INTO regroup_backups(created,payload) VALUES(?,?)", (time.time(), json.dumps({"old": snapshot, "new": []})))
    backup_id = c.execute("SELECT last_insert_rowid()").fetchone()[0]
    for pid in replace_ids:
        c.execute("DELETE FROM project_files WHERE project_id=?", (pid,))
        c.execute("DELETE FROM projects WHERE id=?", (pid,))
    created = []
    for group in groups:
        pid = c.execute("INSERT INTO projects(name,created) VALUES(?,?)", (group["name"].strip()[:80], time.time())).lastrowid
        for fid in set(group["file_ids"]):
            if c.execute("SELECT 1 FROM files WHERE id=?", (int(fid),)).fetchone():
                c.execute("INSERT OR IGNORE INTO project_files VALUES(?,?)", (pid, int(fid)))
        created.append(pid)
    c.execute("UPDATE regroup_backups SET payload=? WHERE id=?", (json.dumps({"old": snapshot, "new": created}), backup_id))
    c.commit()
    return {"created": created, "backup_id": backup_id, "replaced": len(replace_ids)}


@app.post("/api/projects/regroup/{backup_id}/restore")
def restore_regroup(backup_id: int):
    c = db.conn()
    backup = c.execute("SELECT payload FROM regroup_backups WHERE id=?", (backup_id,)).fetchone()
    if not backup:
        raise HTTPException(404, "Backup not found")
    snapshot = json.loads(backup["payload"])
    for pid in snapshot["new"]:
        c.execute("DELETE FROM project_files WHERE project_id=?", (int(pid),))
        c.execute("DELETE FROM projects WHERE id=?", (int(pid),))
    restored = 0
    for p in snapshot["old"]:
        if c.execute("SELECT 1 FROM projects WHERE id=?", (p["id"],)).fetchone():
            continue
        c.execute("INSERT INTO projects(id,name,created) VALUES(?,?,?)", (p["id"], p["name"], p["created"]))
        for fid in p["file_ids"]:
            c.execute("INSERT OR IGNORE INTO project_files VALUES(?,?)", (p["id"], fid))
        restored += 1
    snapshot["restored"] = True
    c.execute("UPDATE regroup_backups SET payload=? WHERE id=?", (json.dumps(snapshot), backup_id))
    c.commit()
    return {"restored": restored}


@app.get("/api/projects/regroup/latest")
def latest_regroup():
    c = db.conn()
    if not c.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='regroup_backups'").fetchone():
        return None
    row = c.execute("SELECT id,payload FROM regroup_backups ORDER BY id DESC LIMIT 1").fetchone()
    if not row:
        return None
    snapshot = json.loads(row["payload"])
    if snapshot.get("restored") or not snapshot["old"] or not snapshot["new"]:
        return None
    existing = {int(r[0]) for r in c.execute("SELECT id FROM projects")}
    if not all(pid in existing for pid in snapshot["new"]):
        return None
    return {"id": row["id"], "count": len(snapshot["old"])}


# ---------------------------------------------------------------- commit / undo
@app.post("/api/commit/plan")
def commit_plan(body: dict = Body(...)):
    return commit_mod.build_plan(body.get("dest_root"), body.get("project_ids"),
                                 body.get("apply_renames", True), body.get("move_into_projects", True))


@app.post("/api/commit/run")
def commit_run(body: dict = Body(...)):
    plan = commit_mod.build_plan(body.get("dest_root"), body.get("project_ids"),
                                 body.get("apply_renames", True), body.get("move_into_projects", True))
    return commit_mod.run(plan["ops"], body.get("mode", "move"))


@app.get("/api/commits")
def commits():
    return [dict(r) for r in db.conn().execute("SELECT * FROM commits ORDER BY id DESC LIMIT 30")]


@app.post("/api/commits/{cid}/undo")
def commit_undo(cid: int):
    return commit_mod.undo(cid)


# ---- Exports: new files made from library files with FFmpeg. Originals are only read. ----

@app.post("/api/export")
def export_start(body: dict = Body(...)):
    ids = body.get("file_ids")
    if not isinstance(ids, list) or not ids or len(ids) > 500 or any(type(i) is not int for i in ids):
        raise HTTPException(400, "Choose files to export.")
    try:
        spec = export_mod.clean_spec(body)
        return {"jobs": export_mod.submit(list(dict.fromkeys(ids)), spec, body.get("destination") or None)}
    except ValueError as error:
        raise HTTPException(400, str(error)) from error


@app.get("/api/export/jobs")
def export_jobs():
    return {"jobs": export_mod.jobs()}


@app.post("/api/export/jobs/{job_id}/cancel")
def export_cancel(job_id: int):
    try:
        return export_mod.cancel(job_id)
    except KeyError:
        raise HTTPException(404, "That export is no longer in the list.") from None


@app.post("/api/export/clear")
def export_clear():
    return {"cleared": export_mod.clear_finished()}


_TOKEN = os.environ.get("FILEMIND_TOKEN", "")
_HOSTS = {"127.0.0.1", "localhost", "[::1]"}


def _allowed_hosts():
    return _HOSTS | ({"testserver"} if os.environ.get("FILEMIND_TESTING") else set())


@app.middleware("http")
async def _guard(request, call_next):
    """The engine is for the app on this PC only. A web page you visit must not be able to drive it:
    requests need a local Host name (blocks DNS tricks) and, for anything that changes data, the token the app started us with."""
    from fastapi.responses import JSONResponse
    if request.headers.get("host", "").rsplit(":", 1)[0] not in _allowed_hosts() and request.headers.get("host", "") not in _allowed_hosts():
        return JSONResponse({"detail": "Not allowed."}, status_code=400)
    # A web page can load http://127.0.0.1:<port>/... too (images and video render cross-site), so reading needs the token as well.
    # Only /api/health is open. Browsers always say where a request comes from; this app never does.
    if request.headers.get("origin") or request.headers.get("sec-fetch-site") in ("cross-site", "same-site"):
        return JSONResponse({"detail": "Not allowed."}, status_code=403)
    if _TOKEN and request.url.path != "/api/health":
        supplied = request.headers.get("x-filemind-token") or request.query_params.get("fm_token") or ""
        if not hmac.compare_digest(supplied, _TOKEN):
            return JSONResponse({"detail": "Not allowed."}, status_code=403)
    return await call_next(request)


@app.get("/api/health")
def health():
    return {"ok": True}


