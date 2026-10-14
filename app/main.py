import os
import json
import re
import subprocess
import sys
import time
from collections import Counter
from functools import lru_cache
from pathlib import Path

import numpy as np
from fastapi import Body, FastAPI, File, HTTPException, UploadFile
from fastapi.responses import FileResponse
from fastapi.staticfiles import StaticFiles

from . import colors as colors_mod
from . import names as names_mod
from . import commit as commit_mod
from . import db, embedder, extract, indexer
from . import suggest as suggest_mod
from . import tags as tags_mod
from . import fetch as fetch_mod
from . import discover as discover_mod

app = FastAPI(title="Local File Brain")
STATIC = Path(__file__).parent / "static"


@app.on_event("startup")
def _startup():
    db.init()
    c = db.conn()
    # Media only: forget index rows for zips, docs, code, etc. (index entries only; files on disk are never touched)
    c.execute("DELETE FROM files WHERE kind NOT IN ('image','video','audio')")
    c.execute("UPDATE files SET status='error', error='Could not be decoded' "
              "WHERE kind IN ('image','video') AND status='done' AND (thumb IS NULL OR thumb='')")
    c.execute("DELETE FROM project_files WHERE file_id NOT IN (SELECT id FROM files)")
    c.execute("DELETE FROM proposed_names WHERE file_id NOT IN (SELECT id FROM files)")
    c.commit()
    indexer.invalidate()
    import threading
    threading.Thread(target=indexer.backfill_colors, daemon=True).start()     # colours for older files
    threading.Thread(target=_warm_ideas, daemon=True).start()
    threading.Thread(target=_warm_dims, daemon=True).start()                  # thumbnail sizes, so the first grid is quick                 # search ideas from the library
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
         "dom": r["dom"] or None, "src": names_mod.source_of(r["name"])}
    if score is not None:
        d["score"] = round(float(score), 4)
    return d


_PICK_PS = r"""
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Application]::EnableVisualStyles()
$owner = New-Object System.Windows.Forms.Form -Property @{TopMost=$true; ShowInTaskbar=$false; Opacity=0; StartPosition='CenterScreen'; Width=1; Height=1}
$owner.Show()
$d = New-Object System.Windows.Forms.FolderBrowserDialog
$d.Description = 'Choose a folder for File Mind to read (it is only read, never changed)'
$d.ShowNewFolderButton = $false
if ($d.ShowDialog($owner) -eq 'OK') { [Console]::Out.Write($d.SelectedPath) }
$owner.Dispose()
"""


@app.post("/api/pick_folder")
def pick_folder():
    """Opens the system folder picker on this PC (the app is local) and returns the chosen path."""
    title = "Choose a folder for File Mind to read (it is only read, never changed)"
    if os.name == "nt":
        cmds = [["powershell", "-NoProfile", "-STA", "-Command", _PICK_PS]]
    elif sys.platform == "darwin":
        cmds = [["osascript", "-e", f'POSIX path of (choose folder with prompt "{title}")']]
    else:
        cmds = [["zenity", "--file-selection", "--directory", f"--title={title}"],
                ["kdialog", "--getexistingdirectory", str(Path.home()), "--title", title]]
    for cmd in cmds:
        try:
            r = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
        except FileNotFoundError:
            continue                                   # this dialog tool isn't installed; try the next
        except Exception as e:
            raise HTTPException(500, f"Could not open the folder picker: {e}")
        path = r.stdout.strip()
        return {"path": path.rstrip("/") or path or None}     # macOS adds a trailing slash; keep "/" itself
    raise HTTPException(501, "No folder picker found on this system (install zenity or kdialog). "
                             "You can paste the folder path instead.")


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
    c = db.conn()
    c.execute("INSERT OR IGNORE INTO roots(path, added) VALUES(?,?)", (path, time.time()))
    c.commit()
    rid = c.execute("SELECT id FROM roots WHERE path=?", (path,)).fetchone()["id"]
    indexer.start(rid, path)
    return {"id": rid, "path": path}


@app.delete("/api/roots/{rid}")
def del_root(rid: int):
    """Forget a folder from the index. Never deletes anything on disk."""
    c = db.conn()
    c.execute("DELETE FROM project_files WHERE file_id IN (SELECT id FROM files WHERE root_id=?)", (rid,))
    c.execute("DELETE FROM proposed_names WHERE file_id IN (SELECT id FROM files WHERE root_id=?)", (rid,))
    c.execute("DELETE FROM files WHERE root_id=?", (rid,))
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
@app.get("/api/files")
def files(root: int | None = None, kind: str | None = None, project: int | None = None,
          unassigned: bool = False, sort: str = "mtime", limit: int = 300, offset: int = 0,
          color: str | None = None, near: str | None = None, source: str | None = None,
          pattern: str | None = None, discover: str | None = None,
          after: float | None = None, before: float | None = None,
          min_size: int | None = None, max_size: int | None = None, ids_only: bool = False):
    where, args = ["f.status='done'"], []      # only fully read files; never half-processed cards
    if root:
        where.append("f.root_id=?"); args.append(root)
    if kind and kind != "all":
        where.append("f.kind=?"); args.append(kind)
    if project:
        where.append("f.id IN (SELECT file_id FROM project_files WHERE project_id=?)"); args.append(project)
    if unassigned:
        where.append("f.id NOT IN (SELECT file_id FROM project_files)")
    if after is not None:
        where.append("f.mtime>=?"); args.append(after)
    if before is not None:
        where.append("f.mtime<?"); args.append(before)
    if min_size is not None:
        where.append("f.size>=?"); args.append(min_size)
    if max_size is not None:
        where.append("f.size<=?"); args.append(max_size)
    order = {"mtime": "f.mtime DESC", "name": "f.name COLLATE NOCASE", "size": "f.size DESC"}.get(sort, "f.mtime DESC")
    c = db.conn()
    base = c.execute(f"SELECT f.id, f.name, f.dom, f.pal FROM files f WHERE {' AND '.join(where)} ORDER BY {order}", args).fetchall()
    meta = {r["id"]: r for r in base}
    ids = [r["id"] for r in base]
    if discover:
        discovered = discover_mod.file_ids(discover)
        ids = [i for i in ids if i in discovered]
    # filters that are computed (file-name source / pattern, colour) are applied here
    if source:
        ids = [i for i in ids if (names_mod.source_of(meta[i]["name"]) or "none") == source]
    if pattern:
        ids = [i for i in ids if names_mod.source_of(meta[i]["name"]) is None and names_mod.signature(meta[i]["name"]) == pattern]
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


@app.get("/api/discover")
def discover():
    return discover_mod.request_groups()


@app.get("/api/sources")
def sources():
    """Which AI tool / app each file name looks like it came from, with counts."""
    from collections import Counter
    cnt = Counter(names_mod.source_of(r["name"]) for r in db.conn().execute("SELECT name FROM files WHERE status='done'"))
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


@app.post("/api/ideas/related")
def ideas_related(body: dict = Body(...)):
    return {"ideas": _idea_kinds(suggest_mod.related([int(x) for x in body.get("ids", [])], body.get("q", ""), int(body.get("n", 6))))}


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
    for r in c.execute("SELECT id, name FROM files WHERE status='done'"):
        if names_mod.source_of(r["name"]) is None:
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
    for idx in (order if keep else order[: max(limit * 6, 600)]):
        fid = int(ids[idx])
        if exclude_id and fid == exclude_id:
            continue
        r = c.execute("SELECT * FROM files WHERE id=?", (fid,)).fetchone()
        if not r or (root and r["root_id"] != root) or (kinds and r["kind"] not in kinds):
            continue
        if keep and not keep(r):
            continue
        score, key = float(sims[idx]), float(rank_by[idx])
        if tokens and all(t in r["name"].lower() for t in tokens):
            key += 1.0   # exact filename hits float to the top
        out.append((key, score, r))
        if keep and len(out) >= limit * 3:
            break
    out.sort(key=lambda x: -x[0])
    return [_file_dict(r, s) for _, s, r in out[:limit]]


def _search_filter(body):
    """Colour, source and space filters can be combined with a search (they are tags too)."""
    color, source, project = body.get("color"), body.get("source"), body.get("project")
    if not (color or source or project):
        return None
    members = {r[0] for r in db.conn().execute("SELECT file_id FROM project_files WHERE project_id=?", (project,))} if project else None

    def keep(r):
        if color and colors_mod.shares(r["pal"]).get(color, 0) < colors_mod.MIN_SHARE:
            return False
        if source and (names_mod.source_of(r["name"]) or "none") != source:
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
    if body.get("similar_to"):
        fid = int(body["similar_to"])
        r = db.conn().execute("SELECT vec FROM files WHERE id=?", (fid,)).fetchone()
        if not r or r["vec"] is None:
            raise HTTPException(400, "File not indexed yet")
        q = np.frombuffer(r["vec"], dtype=np.float32)
        return {"files": _rank(q, root, kinds, limit + offset, exclude_id=fid, keep=keep)[offset:]}
    qs = [q.strip() for q in (body.get("qs") or [body.get("q") or ""]) if q and q.strip()]
    if not qs:
        return {"files": []}
    vecs, negs, understood, texts = [], [], {}, []
    for q in qs:
        if q.startswith("voice:"):
            v = _voice.get(q[6:])
            if v is None:
                raise HTTPException(400, "That voice search has expired — record it again")
            vecs.append(v); continue
        it = _interpret(q)
        if it["kind"] and not kinds:
            kinds = [it["kind"]]; understood["kind"] = it["kind"]
        if it["source"] and not body.get("source"):
            src = it["source"]; prev = keep
            keep = (lambda rr, src=src, prev=prev: (names_mod.source_of(rr["name"]) == src) and (prev is None or prev(rr)))
            understood["source"] = names_mod.LABEL[src][0]
        if it["without"]:
            negs.append(embedder.embed_query(it["without"])); understood.setdefault("without", []).append(it["without"])
        vecs.append(embedder.embed_query(it["text"])); texts.append(it["text"])
    qvec = vecs[0] if len(vecs) == 1 else np.vstack(vecs)
    neg = None
    if negs:
        neg = np.mean(negs, axis=0); neg /= np.linalg.norm(neg) or 1
    files = _rank(qvec, root, kinds, limit + offset, query_text=texts[0] if len(vecs) == 1 and texts else None, keep=keep, neg=neg)[offset:]
    return {"files": files, "understood": understood}


# --------------------------------------------------------------- reading a typed sentence
_KIND_WORDS = [("video", r"videos?|clips?|footage|reels?|movies?"), ("image", r"photos?|images?|pictures?|pics?|stills?|renders?"),
               ("audio", r"songs?|music|audio|sounds?|tracks?|voice ?overs?|recordings?")]
_LEAD = r"^(?:show me |find me |find |search for |look for |get me |i want |give me )?(?:all |some |any |the |my )?"


def _interpret(q: str) -> dict:
    """'videos of a man running without text from midjourney' -> kind video, source midjourney,
    leave out 'text', search 'a man running'. Anything not recognised stays in the search as is."""
    t, kind, source, without = q.strip(), None, None, None
    low = t.lower()
    for k, pat in _KIND_WORDS:
        m = re.match(_LEAD + rf"(?:{pat})\b(?:\s+(?:of|with|showing|where|that|about)\b)?\s*", low)
        if m and m.end() < len(low):
            kind, t, low = k, t[m.end():], low[m.end():]
            break
    for key, (label, _) in names_mod.LABEL.items():
        if key in ("camera", "screenshot", "creation", "flow"):      # ordinary words; too easy to misread
            continue
        names = {key, label.lower().split(" ")[0]}
        m = re.search(r"\s*\b(?:from|by|made (?:with|in|by)|generated (?:with|by|in)|in|on)\s+(" + "|".join(map(re.escape, names)) + r")\b", low)
        if m:
            source = key; t, low = t[:m.start()] + t[m.end():], low[:m.start()] + low[m.end():]
            break
    m = re.search(r"\s*\b(?:without|but not|except|excluding|no)\s+(?:any\s+)?(.+)$", low)
    if m and (m.start() > 0 or kind):
        without = t[m.start(1):].strip(" .,"); t = t[:m.start()]
    t = t.strip(" .,") or {"video": "a video", "image": "a photo", "audio": "a sound"}.get(kind, q)
    return {"text": t, "kind": kind, "source": source, "without": without}


# --------------------------------------------------------------- voice: the spoken clip itself is the query
_voice: dict[str, np.ndarray] = {}


@app.get("/api/model")
def model_status():
    return fetch_mod.status()


@app.post("/api/model/download")
def model_download():
    import threading
    fetch_mod.start(on_done=lambda: threading.Thread(target=_warm_ideas, daemon=True).start())
    return fetch_mod.status()


@app.post("/api/voice")
async def voice(file: UploadFile = File(...)):
    """EmbeddingGemma understands speech directly, so the recording is embedded as a search query —
    no transcription, nothing leaves this PC. Returns an id to search with and words it sounds close to."""
    import subprocess, uuid
    stamp = int(time.time() * 1000)
    raw, wav = extract.TMP_DIR / f"voice_{stamp}.webm", extract.TMP_DIR / f"voice_{stamp}.wav"
    raw.write_bytes(await file.read())
    try:
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(raw), "-t", "20", "-ac", "1", "-ar", "16000",
                        "-af", "silenceremove=start_periods=1:start_threshold=-45dB", str(wav)], capture_output=True, timeout=60)
        if not wav.exists() or wav.stat().st_size < 4000:
            raise HTTPException(400, "Didn't catch that — hold the mic and speak a little longer")
        m = embedder.get_model()
        v = embedder._norm(m.encode({"text": "task: search result | query: <|audio|>", "audio": str(wav)}, normalize_embeddings=True))
        secs = round((wav.stat().st_size - 44) / 32000, 1)
    finally:
        raw.unlink(missing_ok=True); wav.unlink(missing_ok=True)
    vid = uuid.uuid4().hex[:10]
    _voice[vid] = v
    while len(_voice) > 60:
        _voice.pop(next(iter(_voice)))
    sims = tags_mod._vecs() @ v
    hint = [tags_mod.TAGS[i] for i in np.argsort(-sims)[:3]]
    return {"id": vid, "secs": secs, "hint": hint}


@app.post("/api/search/image")
async def search_by_image(file: UploadFile = File(...), root: int | None = None, limit: int = 100):
    tmp = extract.TMP_DIR / f"query_{int(time.time()*1000)}{Path(file.filename).suffix}"
    tmp.write_bytes(await file.read())
    try:
        q = embedder.embed_image_file(str(tmp))
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
        except Exception:
            pass
    projs = [{"id": p["id"], "name": p["name"]} for p in c.execute(
        "SELECT p.* FROM projects p JOIN project_files pf ON pf.project_id=p.id WHERE pf.file_id=?", (fid,))]
    pn = c.execute("SELECT new_name FROM proposed_names WHERE file_id=?", (fid,)).fetchone()
    d = _file_dict(r)
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
    except Exception:
        pass
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
    r = db.conn().execute("SELECT path FROM files WHERE id=?", (fid,)).fetchone()
    if not r or not os.path.exists(r["path"]):
        raise HTTPException(404)
    return FileResponse(r["path"])


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
        n = c.execute("SELECT COUNT(*) FROM project_files WHERE project_id=?", (p["id"],)).fetchone()[0]
        cover = c.execute("SELECT f.id, f.kind FROM project_files pf JOIN files f ON f.id=pf.file_id "
                          "WHERE pf.project_id=? AND f.thumb IS NOT NULL AND f.thumb!='' ORDER BY f.mtime DESC LIMIT 3", (p["id"],)).fetchall()
        out.append({"id": p["id"], "name": p["name"], "count": n, "cover": [{"id": r["id"], "kind": r["kind"]} for r in cover]})
    return out


@app.post("/api/projects")
def create_project(body: dict = Body(...)):
    name = (body.get("name") or "").strip()
    if not name:
        raise HTTPException(400, "Name required")
    c = db.conn()
    cur = c.execute("INSERT INTO projects(name, created) VALUES(?,?)", (name, time.time()))
    pid = cur.lastrowid
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
    c.execute("DELETE FROM projects WHERE id=?", (pid,))
    c.commit()
    return {"ok": True}


@app.post("/api/projects/{pid}/add")
def project_add(pid: int, body: dict = Body(...)):
    c = db.conn()
    for fid in body["file_ids"]:
        c.execute("INSERT OR IGNORE INTO project_files VALUES(?,?)", (pid, fid))
    c.commit()
    return {"ok": True}


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


# ---------------------------------------------------------------- auto-grouping
_STOP = set("the a an of and or to in for on with copy final new image img screenshot untitled "
            "download file document pdf png jpg jpeg mp4 mov v1 v2 v3 de la le und der".split())


def _suggest_name(rows) -> str:
    toks = Counter()
    for r in rows:
        stem = os.path.splitext(r["name"])[0].lower()
        for t in set(re.split(r"[^a-z]+", stem)):
            if len(t) > 2 and t not in _STOP:
                toks[t] += 1
    common = [t for t, n in toks.most_common(3) if n >= max(2, len(rows) * 0.2)]
    kinds = Counter(r["kind"] for r in rows).most_common(1)[0][0]
    if common:
        return " ".join(w.capitalize() for w in common)
    return f"Group of {kinds}s"


@app.post("/api/cluster")
def cluster(body: dict = Body(...)):
    """Group similar files. `looseness` 0.2 (tight) .. 0.8 (broad). Nothing is saved until the user accepts."""
    from sklearn.cluster import AgglomerativeClustering, KMeans
    root = body.get("root")
    looseness = float(body.get("looseness", 0.5))
    only_unassigned = body.get("unassigned", True)
    ids, mat = indexer.matrix()
    c = db.conn()
    sel = []
    assigned = {r[0] for r in c.execute("SELECT DISTINCT file_id FROM project_files")} if only_unassigned else set()
    for i, fid in enumerate(ids):
        r = c.execute("SELECT root_id FROM files WHERE id=?", (int(fid),)).fetchone()
        if r and (not root or r["root_id"] == root) and int(fid) not in assigned:
            sel.append(i)
    if len(sel) < 4:
        return {"clusters": []}
    X = mat[sel]
    if len(sel) > 6000:
        k = max(8, int(len(sel) ** 0.5 / 1.5))
        labels = KMeans(n_clusters=k, n_init=3, random_state=0).fit_predict(X)
    else:
        # Cosine distances in this space are compressed, so a fixed cut-off is meaningless. Instead the
        # threshold is a low percentile of this collection's own pairwise distances (slider: ~0.5% .. ~10%).
        iu = np.triu_indices(len(X), 1)
        vals = 1.0 - (X @ X.T)[iu]
        if len(vals) > 400_000:
            vals = np.random.default_rng(0).choice(vals, 400_000, replace=False)
        thr = float(np.percentile(vals, 0.5 + looseness * 9.5))
        labels = AgglomerativeClustering(n_clusters=None, metric="cosine", linkage="average",
                                         distance_threshold=thr).fit_predict(X)
    groups: dict[int, list[int]] = {}
    for pos, lab in zip(sel, labels):
        groups.setdefault(int(lab), []).append(int(ids[pos]))
    out = []
    for lab, fids in groups.items():
        if len(fids) < 2:
            continue
        rows = [c.execute("SELECT * FROM files WHERE id=?", (f,)).fetchone() for f in fids]
        out.append({"label": lab, "name": _suggest_name(rows), "count": len(fids),
                    "file_ids": fids, "sample": [_file_dict(r) for r in rows[:8]]})
    out.sort(key=lambda g: -g["count"])
    leftover = len(sel) - sum(g["count"] for g in out)
    return {"clusters": out, "leftover": leftover}


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


@app.middleware("http")
async def _revalidate_ui(request, call_next):
    """The UI files change with updates: make the browser check for a newer copy instead of reusing a stale one."""
    resp = await call_next(request)
    if not request.url.path.startswith("/api/"):
        resp.headers["Cache-Control"] = "no-cache"
    return resp


app.mount("/", StaticFiles(directory=STATIC, html=True), name="static")
