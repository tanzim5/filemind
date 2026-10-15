import json
import sqlite3
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np
from PIL import Image
from fastapi.testclient import TestClient

from app import db, duplicates, main


class LibraryToolTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.conn = sqlite3.connect(":memory:")
        self.conn.row_factory = sqlite3.Row
        self.addCleanup(self.conn.close)
        self.conn.executescript(db.SCHEMA)
        self.conn.executescript("ALTER TABLE files ADD COLUMN dom TEXT; ALTER TABLE files ADD COLUMN pal TEXT; ALTER TABLE files ADD COLUMN snippet TEXT;")
        self.conn.executescript("""
            CREATE TABLE smart_spaces(project_id INTEGER PRIMARY KEY, spec TEXT, query_vec BLOB);
            CREATE TABLE moments(file_id INTEGER, start REAL, end REAL, vec BLOB, size INTEGER, mtime REAL);
            CREATE TABLE moment_runs(file_id INTEGER PRIMARY KEY, size INTEGER, mtime REAL, status TEXT, error TEXT);
        """)
        for fid, color, width, height in ((1, "red", 100, 50), (2, "red", 100, 50), (3, "blue", 40, 80)):
            path = self.root / f"{fid}.png"
            Image.new("RGB", (width, height), color).save(path)
            self.conn.execute("INSERT INTO files(id,root_id,path,name,ext,kind,size,mtime,status,embed_mode,width,height) "
                              "VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
                              (fid, 1, str(path), path.name, ".png", "image", path.stat().st_size, fid, "done", "content", width, height))
        self.conn.commit()
        self.patcher = patch.object(main.db, "conn", lambda: self.conn)
        self.patcher.start()
        self.addCleanup(self.patcher.stop)

    def test_media_filters_and_live_space_pick_up_new_files(self):
        self.conn.execute("INSERT INTO projects(id,name,created) VALUES(1,'Wide images',0)")
        self.conn.execute("INSERT INTO smart_spaces VALUES(1,?,NULL)", (json.dumps({"kind": "image", "min_width": 90}),))
        self.conn.commit()
        self.assertEqual(main.files(project=1, ids_only=True)["ids"], [2, 1])
        self.assertEqual(main.files(aspect="portrait", ids_only=True)["ids"], [3])
        path = self.root / "4.png"
        Image.new("RGB", (120, 60), "green").save(path)
        self.conn.execute("INSERT INTO files(id,root_id,path,name,ext,kind,size,mtime,status,embed_mode,width,height) "
                          "VALUES(4,1,?,? ,'.png','image',?,4,'done','content',120,60)",
                          (str(path), path.name, path.stat().st_size))
        self.conn.commit()
        self.assertEqual(main.files(project=1, ids_only=True)["ids"], [4, 2, 1])

    def test_semantic_search_respects_media_filters(self):
        keep = main._search_filter({"filters": {"min_width": 90, "aspect": "landscape"}})
        rows = self.conn.execute("SELECT * FROM files ORDER BY id").fetchall()
        self.assertEqual([row["id"] for row in rows if keep(row)], [1, 2])
        keep = main._search_filter({"filters": {"min_duration": 10}})
        self.assertFalse(any(keep(row) for row in rows))

    def test_rank_batches_metadata_and_returns_full_file_details(self):
        vectors = np.eye(3, dtype=np.float32)
        with patch.object(main.indexer, "matrix", return_value=(np.array([1, 2, 3]), vectors)):
            result = main._rank(vectors[0], None, ["image"], 2,
                                keep=main._search_filter({"filters": {"min_width": 90}}))
        self.assertEqual([item["id"] for item in result], [1, 2])
        self.assertIn("path", result[0])

    def test_exact_duplicates_are_reported_without_modifying_files(self):
        with patch.object(duplicates.extract, "THUMB_DIR", self.root):
            result = duplicates.groups()
        self.assertTrue(any(set(group) == {1, 2} for group in result["exact"]))
        self.assertEqual(len(list(self.root.glob("*.png"))), 3)
        self.assertEqual(duplicates.groups(focus=1)["exact"], [[1, 2]])

    def test_invalid_live_space_does_not_create_project(self):
        with self.assertRaises(Exception):
            main.create_project({"name": "Bad", "smart": {"qs": [42]}})
        self.assertEqual(self.conn.execute("SELECT COUNT(*) FROM projects").fetchone()[0], 0)

    def test_regroup_can_restore_previous_spaces(self):
        self.conn.execute("INSERT INTO projects(id,name,created) VALUES(9,'Original Space',1)")
        self.conn.execute("INSERT INTO project_files VALUES(9,1)")
        self.conn.commit()
        for fid in (4, 5):
            path = self.root / f"{fid}.png"
            Image.new("RGB", (20, 20), "green").save(path)
            self.conn.execute("INSERT INTO files(id,root_id,path,name,ext,kind,size,mtime,status,embed_mode,thumb) "
                              "VALUES(?,?,?,?,?,?,?,?,?,?,?)", (fid, 1, str(path), path.name, ".png", "image", 50, fid, "done", "content", path.name))
        self.conn.commit()
        result = main.regroup_projects({"replace_ids": [9], "groups": [{"name": "Useful Group", "file_ids": [1, 2, 3, 4, 5]}]})
        self.assertEqual(len(result["created"]), 1)
        self.assertEqual(main.latest_regroup()["count"], 1)
        self.assertEqual(self.conn.execute("SELECT COUNT(*) FROM projects").fetchone()[0], 1)
        restored = main.restore_regroup(result["backup_id"])
        self.assertEqual(restored["restored"], 1)
        self.assertIsNone(main.latest_regroup())
        self.assertEqual(self.conn.execute("SELECT name FROM projects WHERE id=9").fetchone()[0], "Original Space")
        self.assertEqual(self.conn.execute("SELECT file_id FROM project_files WHERE project_id=9").fetchone()[0], 1)

    def test_moment_search_route_is_not_treated_as_a_file_id(self):
        with patch.object(main.moments_mod, "search", return_value=[]):
            response = TestClient(main.app).post("/api/moments/search", json={"q": "street at night"})
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.json(), {"moments": []})


if __name__ == "__main__":
    unittest.main()
