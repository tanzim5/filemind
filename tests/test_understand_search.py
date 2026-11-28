import os
import tempfile
import unittest
from datetime import datetime, timezone

_tmp = tempfile.mkdtemp()
os.environ.setdefault("FILEMIND_DATA", _tmp)

from app import db, main  # noqa: E402


def ts(y, m, d):
    return datetime(y, m, d, 9, tzinfo=timezone.utc).timestamp()


@unittest.skipUnless("tmp" in str(db.DATA_DIR).lower(), "needs its own empty data folder")
class QuestionSearchTests(unittest.TestCase):
    def setUp(self):
        db.init()
        c = db.conn()
        c.execute("DELETE FROM files")
        # id 1 was copied recently but taken on 5 Aug; id 2 was simply modified on 5 Aug; id 3 is from September
        rows = [(1, ts(2026, 11, 20), ts(2026, 8, 5)), (2, ts(2026, 8, 5), None), (3, ts(2026, 9, 3), ts(2026, 9, 3))]
        for fid, mtime, taken in rows:
            c.execute("INSERT INTO files(id,root_id,path,name,ext,kind,size,mtime,taken,status) VALUES(?,?,?,?,?,?,?,?,?,?)",
                      (fid, 1, f"C:/x/{fid}.jpg", f"{fid}.jpg", ".jpg", "image", 9, mtime, taken, "done"))
        c.commit()

    def test_a_date_question_finds_files_by_the_day_they_were_taken(self):
        now = ts(2026, 11, 28)
        r = main.search({"q": "files from 5th August", "now": now, "utc_offset_min": 0})
        self.assertEqual(sorted(f["id"] for f in r["files"]), [1, 2])
        self.assertTrue(any(c["key"] == "date" and c["value"] == "5 Aug 2026" for c in r["understood"]))

    def test_the_endpoint_returns_chips(self):
        r = main.understand_endpoint({"text": "red photos from last week", "now": ts(2026, 11, 28)})
        self.assertEqual(r["color"], "red")
        self.assertEqual(r["kinds"], ["image"])
        self.assertTrue(r["filters"])
