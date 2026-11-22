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
    cluster INTEGER,
    width INTEGER,
    height INTEGER,
    duration REAL,
    fps REAL,
    codec TEXT
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
    for name, sql_type in (("width", "INTEGER"), ("height", "INTEGER"), ("duration", "REAL"),
                           ("fps", "REAL"), ("codec", "TEXT")):
        if name not in cols:
            conn().execute(f"ALTER TABLE files ADD COLUMN {name} {sql_type}")
    conn().executescript("""
        CREATE TABLE IF NOT EXISTS smart_spaces (
            project_id INTEGER PRIMARY KEY,
            spec TEXT NOT NULL,
            query_vec BLOB
        );
        CREATE TABLE IF NOT EXISTS moments (
            file_id INTEGER NOT NULL,
            start REAL NOT NULL,
            end REAL NOT NULL,
            vec BLOB NOT NULL,
            size INTEGER NOT NULL,
            mtime REAL NOT NULL,
            PRIMARY KEY(file_id, start)
        );
        CREATE INDEX IF NOT EXISTS idx_moments_file ON moments(file_id);
        CREATE TABLE IF NOT EXISTS tags (
            name TEXT PRIMARY KEY COLLATE NOCASE,
            color TEXT NOT NULL DEFAULT '#8B5CF6',
            created REAL NOT NULL DEFAULT (strftime('%s','now'))
        );
        CREATE TABLE IF NOT EXISTS file_tags (
            file_id INTEGER NOT NULL,
            tag TEXT NOT NULL COLLATE NOCASE,
            PRIMARY KEY(file_id, tag)
        );
        CREATE INDEX IF NOT EXISTS idx_file_tags_tag ON file_tags(tag);
        CREATE TABLE IF NOT EXISTS space_dismissals (
            project_id INTEGER NOT NULL,
            file_id INTEGER NOT NULL,
            PRIMARY KEY(project_id, file_id)
        );
        CREATE TABLE IF NOT EXISTS moment_runs (
            file_id INTEGER PRIMARY KEY,
            size INTEGER NOT NULL,
            mtime REAL NOT NULL,
            status TEXT NOT NULL,
            error TEXT
        );
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE IF NOT EXISTS file_labels (
            file_id INTEGER PRIMARY KEY,
            label TEXT NOT NULL CHECK (label IN ('red','orange','yellow','green','blue','purple','grey'))
        );
        CREATE INDEX IF NOT EXISTS idx_file_labels_label ON file_labels(label);
        INSERT OR IGNORE INTO meta(key, value) VALUES ('index_gen', '0');
        CREATE TABLE IF NOT EXISTS dup_cache (id INTEGER PRIMARY KEY CHECK (id = 1), gen INTEGER, created REAL, result TEXT);
        CREATE INDEX IF NOT EXISTS idx_files_status_mtime ON files(status, mtime);
        CREATE INDEX IF NOT EXISTS idx_files_root_status ON files(root_id, status);
        CREATE INDEX IF NOT EXISTS idx_files_kind_status ON files(kind, status);
        -- Any change to what is indexed bumps a counter, so cached results (duplicates) know when they are out of date.
        CREATE TRIGGER IF NOT EXISTS files_gen_ins AFTER INSERT ON files
            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'index_gen'; END;
        CREATE TRIGGER IF NOT EXISTS files_gen_del AFTER DELETE ON files
            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'index_gen'; END;
        CREATE TRIGGER IF NOT EXISTS files_gen_upd AFTER UPDATE OF path, size, mtime, thumb, status ON files
            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'index_gen'; END;
    """)
    # Cached fingerprints, reused while the file is unchanged (hash_key = "size:mtime").
    for name, sql_type in (("sha256", "BLOB"), ("hash_key", "TEXT"), ("dh", "TEXT")):
        if name not in cols:
            conn().execute(f"ALTER TABLE files ADD COLUMN {name} {sql_type}")
    conn().commit()
