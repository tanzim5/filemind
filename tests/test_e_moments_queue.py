import os
import tempfile
import time
import unittest
from unittest.mock import patch

_tmp = tempfile.mkdtemp()
os.environ.setdefault("FILEMIND_DATA", _tmp)

from app import db, moments  # noqa: E402


@unittest.skipUnless(os.path.samefile(db.DATA_DIR, os.environ["FILEMIND_DATA"]) and "tmp" in str(db.DATA_DIR).lower(), "needs its own empty data folder")
class QueueTests(unittest.TestCase):
    def setUp(self):
        db.init()
        c = db.conn()
        for t in ("files", "moment_runs", "moments"):
            c.execute(f"DELETE FROM {t}")
        for i, kind in enumerate(("video", "video", "audio", "image"), 1):
            c.execute("INSERT INTO files(id, root_id, path, name, ext, kind, size, mtime, status) VALUES(?,?,?,?,?,?,?,?,?)",
                      (i, 1, f"C:/x/{i}", f"{i}", ".mp4", kind, 5, 5, "done"))
        c.execute("INSERT INTO moment_runs VALUES(2, 5, 5, 'ready', NULL)")       # file 2 is already analysed
        c.commit()
        moments._queue.clear(); moments._qstats.update(total=0, done=0, current=None)

    def test_only_unanalysed_videos_and_sounds_are_queued_once_and_worked_in_order(self):
        seen = []
        with patch.object(moments, "_analyze", lambda fid, row: seen.append(fid)), patch.object(moments, "_work", lambda: None):
            first = moments.queue(all_media=True)
            again = moments.queue([1, 3])
        self.assertEqual(first["pending"], 2)                  # 1 and 3: not 2 (done), not 4 (a picture)
        self.assertEqual(again["pending"], 2)                  # nothing added twice
        self.assertEqual([fid for fid, _ in moments._queue], [1, 3])

    def test_worker_drains_the_queue_and_reports_progress(self):
        done = []
        with patch.object(moments, "_analyze", lambda fid, row: done.append(fid)):
            moments.queue([1, 3])
            for _ in range(100):
                if len(done) == 2:
                    break
                time.sleep(0.05)
        self.assertEqual(done, [1, 3])
