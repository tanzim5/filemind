import sqlite3
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np
from PIL import Image

from app import discover


class DiscoverTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.conn = sqlite3.connect(":memory:")
        self.conn.row_factory = sqlite3.Row
        self.addCleanup(self.conn.close)
        self.conn.execute("CREATE TABLE files (id INTEGER, path TEXT, name TEXT, kind TEXT, mtime REAL, thumb TEXT, vec BLOB, status TEXT, width INTEGER, height INTEGER)")
        self.patch_db = patch.object(discover.db, "conn", return_value=self.conn)
        self.patch_ready = patch.object(discover.embedder, "ready", return_value=False)
        self.patch_db.start()
        self.patch_ready.start()
        self.addCleanup(self.patch_db.stop)
        self.addCleanup(self.patch_ready.stop)
        discover._cache.update(key=None, groups=[], ids={})
        discover._preview.update(key=None, groups=[], ids={})

    def test_source_type_and_camera_metadata_groups(self):
        path = Path(self.tmp.name) / "IMG_1234.jpg"
        image = Image.new("RGB", (20, 20), "red")
        exif = image.getexif()
        exif[271] = "Canon"
        exif[272] = "EOS R5"
        image.save(path, exif=exif)
        self.conn.execute("INSERT INTO files VALUES (1, ?, ?, 'image', 1, '1.jpg', NULL, 'done', NULL, NULL)", (str(path), path.name))
        self.conn.execute("INSERT INTO files VALUES (2, ?, 'ChatGPT Image.png', 'image', 1, '2.jpg', NULL, 'done', NULL, NULL)", (str(path),))
        self.conn.commit()

        preview = {group["key"]: group for group in discover._quick_groups((2, 3, 2))}
        self.assertEqual(preview["source:chatgpt"]["count"], 1)
        self.assertEqual(discover.file_ids("type:image"), {1, 2})

        groups = {group["key"]: group for group in discover.groups()}
        self.assertEqual(groups["type:image"]["count"], 2)
        self.assertEqual(groups["source:camera"]["count"], 1)
        self.assertEqual(groups["source:chatgpt"]["count"], 1)
        self.assertEqual(groups["camera:Canon EOS R5"]["count"], 2)
        self.assertEqual(discover.file_ids("source:chatgpt"), {2})

    def test_visual_themes_choose_the_strongest_match(self):
        category_count = len(discover.CATEGORIES)
        for i in range(category_count * 2):
            vector = np.eye(category_count, dtype=np.float32)[i // 2]
            self.conn.execute("INSERT INTO files VALUES (?, ?, ?, 'image', 1, 'thumb.jpg', ?, 'done', NULL, NULL)",
                              (i + 1, str(Path(self.tmp.name) / f'{i}.png'), f'{i}.png', vector.tobytes()))
        self.conn.commit()
        query_index = {category[3]: i for i, category in enumerate(discover.CATEGORIES)}
        with patch.object(discover.embedder, "ready", return_value=True), patch.object(
                discover.embedder, "embed_query", side_effect=lambda query: np.eye(category_count, dtype=np.float32)[query_index[query]]):
            groups = discover.groups()
        theme_counts = {group["key"]: group["count"] for group in groups if group["key"].startswith("theme:")}
        self.assertEqual(theme_counts, {f"theme:{category[0]}": 2 for category in discover.CATEGORIES})


if __name__ == "__main__":
    unittest.main()


class DiscoverPersistenceTests(unittest.TestCase):
    def test_result_survives_a_restart_and_goes_stale_when_the_library_changes(self):
        import os, tempfile
        os.environ.setdefault("FILEMIND_DATA", tempfile.mkdtemp())
        from app import db as real_db
        if "tmp" not in str(real_db.DATA_DIR).lower():
            self.skipTest("needs its own data folder")
        real_db.init()
        c = real_db.conn()
        c.execute("DELETE FROM files"); c.execute("DELETE FROM discover_cache")
        c.execute("INSERT INTO files(id, root_id, path, name, ext, kind, size, mtime, status) VALUES(1,1,'a.png','a.png','.png','image',1,1,'done')")
        c.commit()
        from app import discover as d
        with patch.object(d.embedder, "ready", return_value=False):
            d._cache.update(key=None, groups=[], ids={}); d._building = False
            first = d.request_groups()
            for _ in range(100):                                     # the first build runs in the background
                if not d._building:
                    break
                __import__("time").sleep(0.05)
            d._cache.update(key=None, groups=[], ids={})             # a restart forgets the memory
            again = d.request_groups()
            self.assertFalse(again["building"])
            self.assertTrue(again["groups"])
            c.execute("INSERT INTO files(id, root_id, path, name, ext, kind, size, mtime, status) VALUES(2,1,'b.png','b.png','.png','image',1,2,'done')")
            c.commit()
            stale = d.request_groups()
            self.assertTrue(stale["building"] and stale.get("stale"))
