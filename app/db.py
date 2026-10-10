import sqlite3
import threading
from pathlib import Path

import os

# FILEMIND_DATA lets a dev/test instance use its own index without touching the user's.
DATA_DIR = Path(os.environ.get("FILEMIND_DATA") or Path(__file__).resolve().parent.parent / "data")
DATA_DIR.mkdir(parents=True, exist_ok=True)
DB_PATH = DATA_DIR / "index.db"

_local = threading.local()

SCHEMA = """
CREATE TABLE IF NOT EXISTS roots (
    id INTEGER PRIMARY KEY,
    path TEXT UNIQUE NOT NULL,
    added REAL
);
CREATE TABLE IF NOT EXISTS files (
    id INTEGER PRIMARY KEY,
    root_id INTEGER NOT NULL,
    path TEXT UNIQUE NOT NULL,
    name TEXT NOT NULL,
    ext TEXT,
    kind TEXT,                -- image | video | audio | text | code | doc | other
    size INTEGER,
    mtime REAL,
    status TEXT DEFAULT 'pending',   -- pending | done | error | skipped
    error TEXT,
    embed_mode TEXT,          -- content | filename
    vec BLOB,                 -- float32, normalized
    thumb TEXT,
    cluster INTEGER
);
CREATE INDEX IF NOT EXISTS idx_files_root ON files(root_id);
CREATE INDEX IF NOT EXISTS idx_files_kind ON files(kind);

-- Staging layer: nothing here touches the real file system.
CREATE TABLE IF NOT EXISTS projects (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    created REAL
);
CREATE TABLE IF NOT EXISTS project_files (
    project_id INTEGER NOT NULL,
    file_id INTEGER NOT NULL,
    PRIMARY KEY (project_id, file_id)
);
CREATE TABLE IF NOT EXISTS proposed_names (
    file_id INTEGER PRIMARY KEY,
    new_name TEXT NOT NULL
);

-- Journal of real changes, used for undo.
CREATE TABLE IF NOT EXISTS commits (
    id INTEGER PRIMARY KEY,
    ts REAL,
    summary TEXT,
    undone INTEGER DEFAULT 0
);
CREATE TABLE IF NOT EXISTS commit_ops (
    id INTEGER PRIMARY KEY,
    commit_id INTEGER NOT NULL,
    file_id INTEGER,
    old_path TEXT NOT NULL,
    new_path TEXT NOT NULL
);
"""


def conn() -> sqlite3.Connection:
    c = getattr(_local, "c", None)
    if c is None:
        c = sqlite3.connect(DB_PATH, timeout=30)
        c.row_factory = sqlite3.Row
        c.execute("PRAGMA journal_mode=WAL")
        c.execute("PRAGMA foreign_keys=ON")
        _local.c = c
    return c


def init():
    conn().executescript(SCHEMA)
    cols = {r["name"] for r in conn().execute("PRAGMA table_info(files)")}
    if "snippet" not in cols:
        conn().execute("ALTER TABLE files ADD COLUMN snippet TEXT")
    if "dom" not in cols:
        conn().execute("ALTER TABLE files ADD COLUMN dom TEXT")      # dominant colour '#rrggbb'
    if "pal" not in cols:
        conn().execute("ALTER TABLE files ADD COLUMN pal TEXT")      # JSON {swatch: share of picture}
    conn().commit()
