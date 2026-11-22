import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

_tmp = tempfile.mkdtemp()
os.environ.setdefault("FILEMIND_DATA", _tmp)

from PIL import Image  # noqa: E402

from app import db, indexer, main, sequences  # noqa: E402


def names(prefix, a, b, pad=4, ext=".png", sep="_", skip=()):
    return [f"{prefix}{sep}{i:0{pad}d}{ext}" for i in range(a, b + 1) if i not in skip]


class DetectTests(unittest.TestCase):
    def test_padded_sequence_is_found_with_its_gaps(self):
        found = sequences.detect(names("render", 1, 100, skip=(40, 41)) + ["notes.txt"])
        self.assertEqual(len(found), 1)
        g = found[0]
        self.assertEqual((g["prefix"], g["sep"], g["pad"], g["first"], g["last"], g["count"]), ("render", "_", 4, 1, 100, 98))
        self.assertEqual(g["missing"], [40, 41])
        self.assertEqual(sequences.pattern(g), "render_####.png")

    def test_unpadded_and_two_passes_in_one_folder(self):
        files = [f"beauty.{i}.exr" for i in range(100, 130)] + names("depth", 1, 50, pad=3)
        found = {g["prefix"]: g for g in sequences.detect(files)}
        self.assertEqual(set(found), {"beauty", "depth"})
        self.assertEqual(sequences.frame_name(found["beauty"], 101), "beauty.101.exr")

    def test_photos_screenshots_and_short_runs_are_not_sequences(self):
        self.assertEqual(sequences.detect(names("IMG", 1, 300, sep="_")), [])             # camera photos
        self.assertEqual(sequences.detect(names("Screenshot", 1, 60, sep=" ")), [])
        self.assertEqual(sequences.detect(names("frame", 1, 10)), [])                      # too few
        self.assertEqual(sequences.detect([f"x_{i:04d}.png" for i in range(1, 200, 5)]), [])   # too sparse


@unittest.skipUnless(os.path.samefile(db.DATA_DIR, os.environ["FILEMIND_DATA"]) and "tmp" in str(db.DATA_DIR).lower(), "needs its own empty data folder")
class ScanTests(unittest.TestCase):
    def setUp(self):
        db.init()
        self.c = db.conn()
        for t in ("files", "roots"):
            self.c.execute(f"DELETE FROM {t}")
        self.c.execute("INSERT INTO roots(id, path, added) VALUES(1, 'x', 0)")
        self.c.commit()
        self.dir = Path(tempfile.mkdtemp())
        for n in names("shot", 1, 30):
            Image.new("RGB", (64, 48), "red").save(self.dir / n)
        Image.new("RGB", (64, 48), "blue").save(self.dir / "lonely.png")
        (self.dir / "lonely.png").write_bytes((self.dir / "lonely.png").read_bytes() + b"0" * 5000)

    def test_scan_collapses_frames_into_one_row_and_a_new_frame_triggers_a_reread(self):
        indexer.scan(1, str(self.dir))
        rows = self.c.execute("SELECT * FROM files ORDER BY name").fetchall()
        self.assertEqual([r["name"] for r in rows], ["lonely.png", "shot_####.png"])
        seq = rows[1]
        self.assertEqual((seq["kind"], seq["codec"], seq["width"], seq["height"]), ("video", "sequence", 64, 48))
        self.assertTrue(seq["path"].endswith("shot_0001.png"))
        self.c.execute("UPDATE files SET status='done' WHERE id=?", (seq["id"],)); self.c.commit()
        indexer.scan(1, str(self.dir))
        self.assertEqual(self.c.execute("SELECT status FROM files WHERE id=?", (seq["id"],)).fetchone()[0], "done")     # unchanged
        Image.new("RGB", (64, 48), "red").save(self.dir / "shot_0031.png")
        indexer.scan(1, str(self.dir))
        self.assertEqual(self.c.execute("SELECT status FROM files WHERE id=?", (seq["id"],)).fetchone()[0], "pending")
        self.assertEqual(json.loads(self.c.execute("SELECT seq_json FROM files WHERE id=?", (seq["id"],)).fetchone()[0])["count"], 31)

    def test_deleting_a_sequence_trashes_every_frame(self):
        indexer.scan(1, str(self.dir))
        fid = self.c.execute("SELECT id FROM files WHERE seq_json IS NOT NULL").fetchone()[0]
        self.c.execute("UPDATE files SET status='done'"); self.c.commit()
        with patch.object(main, "send2trash", lambda p: os.remove(p)):
            main.file_actions({"action": "delete", "file_ids": [fid]})
        self.assertEqual(sorted(os.listdir(self.dir)), ["lonely.png"])
        self.assertEqual(self.c.execute("SELECT COUNT(*) FROM files WHERE seq_json IS NOT NULL").fetchone()[0], 0)

    def test_frame_with_a_different_size_means_not_a_sequence(self):
        Image.new("RGB", (10, 10), "red").save(self.dir / "shot_0015.png")
        indexer.scan(1, str(self.dir))
        self.assertEqual(self.c.execute("SELECT COUNT(*) FROM files WHERE seq_json IS NOT NULL").fetchone()[0], 1)    # samples match: still one
        for n in (1, 16, 30):
            Image.new("RGB", (10 + n, 10), "red").save(self.dir / f"shot_{n:04d}.png")
        self.c.execute("DELETE FROM files"); self.c.commit()
        indexer.scan(1, str(self.dir))
        self.assertEqual(self.c.execute("SELECT COUNT(*) FROM files WHERE seq_json IS NOT NULL").fetchone()[0], 0)    # plain pictures instead
