import os
import tempfile
import unittest
from unittest.mock import patch

_tmp = tempfile.mkdtemp()
os.environ.setdefault("FILEMIND_DATA", _tmp)
os.environ["FILEMIND_TOKEN"] = "secret"

from app import db, duplicates, main  # noqa: E402


@unittest.skipUnless(os.path.samefile(db.DATA_DIR, os.environ["FILEMIND_DATA"]) and "tmp" in str(db.DATA_DIR).lower(), "needs its own empty data folder")
class HardeningTests(unittest.TestCase):
    def setUp(self):
        db.init()
        self.c = db.conn()
        for t in ("files", "roots", "dup_cache"):
            self.c.execute(f"DELETE FROM {t}")
        self.c.execute("INSERT INTO roots(id, path, added) VALUES(1, 'C:/a', 0)")
        self.c.commit()

    def _add(self, i, path):
        self.c.execute("INSERT INTO files(id, root_id, path, name, ext, kind, size, mtime, status) VALUES(?,?,?,?,?,?,?,?,?)",
                       (i, 1, path, os.path.basename(path), ".txt", "text", 5, 1, "done"))
        self.c.commit()

    def _gen(self):
        return int(self.c.execute("SELECT value FROM meta WHERE key='index_gen'").fetchone()[0])

    def test_changing_the_index_bumps_the_generation(self):
        before = self._gen()
        self._add(1, "C:/a/x.txt")
        self.assertGreater(self._gen(), before)
        mid = self._gen()
        self.c.execute("UPDATE files SET sha256=x'00', hash_key='k' WHERE id=1"); self.c.commit()
        self.assertEqual(self._gen(), mid)                  # a cached fingerprint is not a change

    def test_second_scan_reuses_stored_hashes(self):
        a, b = os.path.join(_tmp, "a.txt"), os.path.join(_tmp, "b.txt")
        for p in (a, b):
            __import__("pathlib").Path(p).write_bytes(b"hello")
        self._add(1, a); self._add(2, b)
        self.assertEqual(sorted(duplicates.groups()["exact"][0]), [1, 2])
        with patch.object(duplicates, "_digest", side_effect=AssertionError("re-hashed")):
            self.assertEqual(len(duplicates.groups()["exact"]), 1)

    def test_duplicate_result_is_cached_and_flagged_stale(self):
        a, b = os.path.join(_tmp, "c.txt"), os.path.join(_tmp, "d.txt")
        for p in (a, b):
            __import__("pathlib").Path(p).write_bytes(b"same!")
        self._add(1, a); self._add(2, b)
        self.assertEqual(main.duplicate_candidates(cached_only=True)["scanned"], 0)
        first = main.duplicate_candidates()
        self.assertFalse(first["cached"])
        again = main.duplicate_candidates()
        self.assertTrue(again["cached"]); self.assertFalse(again["stale"])
        self._add(3, os.path.join(_tmp, "e.txt"))
        self.assertTrue(main.duplicate_candidates()["stale"])

    def test_deleting_through_actions_cleans_up_everything(self):
        p = os.path.join(_tmp, "gone.txt"); __import__("pathlib").Path(p).write_bytes(b"x")
        self._add(1, p)
        self.c.execute("INSERT OR IGNORE INTO file_tags(file_id, tag) VALUES(1, 'x')"); self.c.commit()
        with patch.object(main, "send2trash", lambda path: os.remove(path)):
            main.file_actions({"action": "delete", "file_ids": [1]})
        self.assertEqual(self.c.execute("SELECT COUNT(*) FROM file_tags").fetchone()[0], 0)
        self.assertEqual(self.c.execute("SELECT COUNT(*) FROM files").fetchone()[0], 0)

    def test_guard_rejects_other_hosts_and_missing_token(self):
        from fastapi.testclient import TestClient
        patch.object(main, "_TOKEN", "secret").start()
        self.addCleanup(patch.stopall)
        client = TestClient(main.app, base_url="http://127.0.0.1:1")
        self.assertEqual(client.get("/api/health").status_code, 200)
        self.assertEqual(client.post("/api/index/stop").status_code, 403)
        self.assertEqual(client.post("/api/index/stop", headers={"x-filemind-token": "secret"}).status_code, 200)
        evil = TestClient(main.app, base_url="http://evil.example")
        self.assertEqual(evil.get("/api/health").status_code, 400)
