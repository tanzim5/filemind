"""Turns the staging layer (virtual projects + proposed names) into real file operations.

Nothing here runs unless the user presses Commit. Every move is journaled for undo.
"""
import os
import re
import shutil
import time
from pathlib import Path

from .db import conn

_BAD = re.compile(r'[<>:"/\\|?*\x00-\x1f]')


def safe_name(s: str) -> str:
    s = _BAD.sub("_", s).strip().rstrip(".")
    return s or "untitled"


def _unique(path: str, taken: set[str]) -> str:
    """Never overwrite: add ' (2)', ' (3)'... if the target exists or is already planned."""
    base, ext = os.path.splitext(path)
    cand, n = path, 2
    while cand.lower() in taken or os.path.exists(cand):
        cand = f"{base} ({n}){ext}"
        n += 1
    return cand


def build_plan(dest_root: str | None, project_ids: list[int] | None = None,
               apply_renames: bool = True, move_into_projects: bool = True) -> dict:
    """Dry run. Returns the list of operations that *would* happen."""
    c = conn()
    names = {r["file_id"]: r["new_name"] for r in c.execute("SELECT * FROM proposed_names")}
    files = {r["id"]: r for r in c.execute("SELECT id, path, name, ext FROM files")}
    ops, taken, warnings = [], set(), []

    def new_filename(f) -> str:
        if apply_renames and f["id"] in names:
            n = safe_name(names[f["id"]])
            if not os.path.splitext(n)[1] and f["ext"]:
                n += f["ext"]          # keep the original extension if user typed none
            return n
        return f["name"]

    handled = set()
    if move_into_projects and dest_root:
        q = "SELECT * FROM projects"
        projs = c.execute(q).fetchall()
        for p in projs:
            if project_ids is not None and p["id"] not in project_ids:
                continue
            folder = os.path.join(dest_root, safe_name(p["name"]))
            for pf in c.execute("SELECT file_id FROM project_files WHERE project_id=?", (p["id"],)):
                f = files.get(pf["file_id"])
                if not f or pf["file_id"] in handled:
                    continue
                target = os.path.join(folder, new_filename(f))
                if os.path.normcase(target) == os.path.normcase(f["path"]):
                    continue
                target = _unique(target, taken)
                taken.add(target.lower())
                handled.add(f["id"])
                ops.append({"file_id": f["id"], "old": f["path"], "new": target, "project": p["name"],
                            "kind": "move+rename" if new_filename(f) != f["name"] else "move"})

    if apply_renames:
        for fid, _ in names.items():
            f = files.get(fid)
            if not f or fid in handled:
                continue
            target = os.path.join(os.path.dirname(f["path"]), new_filename(f))
            if os.path.normcase(target) == os.path.normcase(f["path"]):
                continue
            target = _unique(target, taken)
            taken.add(target.lower())
            ops.append({"file_id": fid, "old": f["path"], "new": target, "project": None, "kind": "rename"})

    for o in ops:
        if not os.path.exists(o["old"]):
            warnings.append(f"Missing on disk, will be skipped: {o['old']}")
    return {"ops": ops, "warnings": warnings,
            "counts": {"move": sum(1 for o in ops if o["kind"].startswith("move")),
                       "rename": sum(1 for o in ops if o["kind"] == "rename"), "total": len(ops)}}


def run(plan_ops: list[dict], mode: str = "move") -> dict:
    """Executes ops. mode='move' relocates files; mode='copy' leaves originals untouched."""
    c = conn()
    cur = c.execute("INSERT INTO commits(ts, summary) VALUES(?,?)", (time.time(), ""))
    commit_id = cur.lastrowid
    done = failed = 0
    errors = []
    for o in plan_ops:
        old, new = o["old"], o["new"]
        try:
            if not os.path.exists(old):
                raise FileNotFoundError(old)
            os.makedirs(os.path.dirname(new), exist_ok=True)
            if os.path.exists(new):
                raise FileExistsError(new)       # last-second guard: never overwrite
            if mode == "copy":
                shutil.copy2(old, new)
            else:
                shutil.move(old, new)
                c.execute("UPDATE files SET path=?, name=? WHERE id=?",
                          (new, os.path.basename(new), o["file_id"]))
                c.execute("DELETE FROM proposed_names WHERE file_id=?", (o["file_id"],))
            c.execute("INSERT INTO commit_ops(commit_id,file_id,old_path,new_path) VALUES(?,?,?,?)",
                      (commit_id, o["file_id"], old, new))
            done += 1
        except Exception as e:
            failed += 1
            errors.append(f"{old}: {e}")
    c.execute("UPDATE commits SET summary=? WHERE id=?",
              (f"{mode}: {done} ok, {failed} failed", commit_id))
    c.commit()
    return {"commit_id": commit_id, "done": done, "failed": failed, "errors": errors[:50], "mode": mode}


def undo(commit_id: int) -> dict:
    c = conn()
    cm = c.execute("SELECT * FROM commits WHERE id=?", (commit_id,)).fetchone()
    if not cm or cm["undone"]:
        return {"error": "Nothing to undo"}
    mode = (cm["summary"] or "").split(":")[0]
    ok = bad = 0
    errors = []
    for op in c.execute("SELECT * FROM commit_ops WHERE commit_id=? ORDER BY id DESC", (commit_id,)):
        try:
            if not os.path.exists(op["new_path"]):
                raise FileNotFoundError(op["new_path"])
            if mode == "copy":
                os.remove(op["new_path"])
            else:
                if os.path.exists(op["old_path"]):
                    raise FileExistsError(op["old_path"])
                os.makedirs(os.path.dirname(op["old_path"]), exist_ok=True)
                shutil.move(op["new_path"], op["old_path"])
                if op["file_id"]:
                    c.execute("UPDATE files SET path=?, name=? WHERE id=?",
                              (op["old_path"], os.path.basename(op["old_path"]), op["file_id"]))
            ok += 1
        except Exception as e:
            bad += 1
            errors.append(str(e))
    # tidy up now-empty folders we created
    for op in c.execute("SELECT new_path FROM commit_ops WHERE commit_id=?", (commit_id,)):
        d = os.path.dirname(op["new_path"])
        for _ in range(2):                    # the space folder, then the destination folder
            try:
                if os.path.isdir(d) and not os.listdir(d):
                    os.rmdir(d)
                    d = os.path.dirname(d)
                else:
                    break
            except OSError:
                break
    c.execute("UPDATE commits SET undone=1 WHERE id=?", (commit_id,))
    c.commit()
    return {"restored": ok, "failed": bad, "errors": errors[:50]}
