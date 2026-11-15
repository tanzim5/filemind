import sqlite3
import unittest
from unittest.mock import patch

import numpy as np
from fastapi import HTTPException

from app import db, main, space_suggest


def unit(*xs):
    v = np.array(xs, dtype=np.float32)
    return v / np.linalg.norm(v)


# Files 1-3 point along x (the space), 4 is very close to them, 5 is a little further, 6 is unrelated, 7 is off to one side.
VECS = {1: unit(1, 0.05, 0), 2: unit(1, -0.05, 0), 3: unit(1, 0, 0.05), 4: unit(1, 0.1, 0.1), 5: unit(1, 0.5, 0.2),
        6: unit(0, 1, 0), 7: unit(0.3, 1, 0)}
IDS = np.array(list(VECS))
MAT = np.vstack(list(VECS.values()))


class SuggestTests(unittest.TestCase):
    def test_centroid_is_the_normalised_mean(self):
        c = space_suggest.centroid(IDS, MAT, [1, 2])
        self.assertAlmostEqual(float(np.linalg.norm(c)), 1.0, places=5)
        np.testing.assert_allclose(c, unit(1, 0, 0), atol=1e-5)
        self.assertIsNone(space_suggest.centroid(IDS, MAT, [99]))

    def test_nearest_first_and_members_are_left_out(self):
        out = space_suggest.suggest(IDS, MAT, [1, 2, 3], min_similarity=0.5)
        self.assertEqual([f for f, _ in out][:2], [4, 5])
        self.assertTrue(all(f not in (1, 2, 3) for f, _ in out))
        self.assertTrue(all(out[i][1] >= out[i + 1][1] for i in range(len(out) - 1)))

    def test_unrelated_files_fall_below_the_threshold(self):
        out = space_suggest.suggest(IDS, MAT, [1, 2, 3])
        self.assertEqual([f for f, _ in out], [4, 5])              # 6 and 7 are not offered at the default threshold
        self.assertEqual(space_suggest.suggest(IDS, MAT, [1, 2, 3], min_similarity=0.999), [])

    def test_dismissed_files_are_skipped_and_limit_applies(self):
        self.assertEqual([f for f, _ in space_suggest.suggest(IDS, MAT, [1, 2, 3], dismissed=[4])], [5])
        self.assertEqual(len(space_suggest.suggest(IDS, MAT, [1, 2, 3], limit=1)), 1)

    def test_too_few_members_or_empty_library_give_nothing(self):
        self.assertEqual(space_suggest.suggest(IDS, MAT, [1]), [])
        self.assertEqual(space_suggest.suggest(np.array([], dtype=int), np.zeros((0, 3), dtype=np.float32), [1, 2]), [])


class EndpointTests(unittest.TestCase):
    def setUp(self):
        self.conn = sqlite3.connect(":memory:")
        self.conn.row_factory = sqlite3.Row
        self.addCleanup(self.conn.close)
        self.conn.executescript(db.SCHEMA)
        self.conn.executescript("ALTER TABLE files ADD COLUMN dom TEXT; ALTER TABLE files ADD COLUMN pal TEXT; ALTER TABLE files ADD COLUMN snippet TEXT;"
                                "CREATE TABLE smart_spaces(project_id INTEGER PRIMARY KEY, spec TEXT, query_vec BLOB);")
        for fid in VECS:
            self.conn.execute("INSERT INTO files(id,root_id,path,name,ext,kind,size,mtime,status,embed_mode) VALUES(?,?,?,?,?,?,?,?,?,?)",
                              (fid, 1, f"/x/{fid}.png", f"{fid}.png", ".png", "image", 1, fid, "done", "content"))
        self.conn.execute("INSERT INTO projects(id,name,created) VALUES(1,'Space',0)")
        self.conn.execute("INSERT INTO projects(id,name,created) VALUES(2,'Live',0)")
        self.conn.execute("INSERT INTO smart_spaces VALUES(2,'{}',NULL)")
        for fid in (1, 2, 3):
            self.conn.execute("INSERT INTO project_files VALUES(1,?)", (fid,))
        self.conn.commit()
        for p in (patch.object(main.db, "conn", lambda: self.conn),
                  patch.object(main.indexer, "matrix", lambda: (IDS, MAT)),
                  patch.object(main, "_thumb_dims", lambda t: None),
                  patch.object(main, "_file_tags", lambda: {})):
            p.start()
            self.addCleanup(p.stop)

    def ids(self, pid=1, **kw):
        return [f["id"] for f in main.project_suggestions(pid, **kw)["files"]]

    def test_suggestions_are_scored_and_exclude_members(self):
        res = main.project_suggestions(1)["files"]
        self.assertEqual([f["id"] for f in res], [4, 5])
        self.assertTrue(all("score" in f for f in res))

    def test_dismiss_hides_a_file_for_that_space_only(self):
        main.project_suggestions_dismiss(1, {"file_ids": [4]})
        self.assertEqual(self.ids(), [5])
        main.project_suggestions_dismiss(1, {"file_ids": [4]})             # twice is fine
        self.assertEqual(self.ids(), [5])
        self.conn.execute("INSERT INTO projects(id,name,created) VALUES(3,'Other',0)")
        for fid in (1, 2, 3):
            self.conn.execute("INSERT INTO project_files VALUES(3,?)", (fid,))
        self.conn.commit()
        self.assertEqual(self.ids(3), [4, 5])

    def test_accepting_removes_the_file_from_suggestions(self):
        main.project_add(1, {"file_ids": [4]})
        self.assertEqual(self.ids(), [5])

    def test_live_and_missing_spaces(self):
        self.assertEqual(self.ids(2), [])
        with self.assertRaises(HTTPException):
            main.project_suggestions(99)
        with self.assertRaises(HTTPException):
            main.project_suggestions_dismiss(99, {"file_ids": [1]})

    def test_deleting_a_space_forgets_its_dismissals(self):
        main.project_suggestions_dismiss(1, {"file_ids": [4]})
        main.delete_project(1)
        self.assertEqual(self.conn.execute("SELECT COUNT(*) FROM space_dismissals").fetchone()[0], 0)


if __name__ == "__main__":
    unittest.main()
