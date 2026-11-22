import os
import tempfile
import unittest

_tmp = tempfile.mkdtemp()
os.environ.setdefault("FILEMIND_DATA", _tmp)

from app import db, main  # noqa: E402


@unittest.skipUnless(os.path.samefile(db.DATA_DIR, os.environ["FILEMIND_DATA"]) and "tmp" in str(db.DATA_DIR).lower(), "needs its own empty data folder")
class OcrTests(unittest.TestCase):
    def setUp(self):
        db.init()
        c = db.conn()
        c.execute("DELETE FROM files")
        for i, (ext, kind) in enumerate(((".png", "image"), (".jpg", "image"), (".png", "video")), 1):
            c.execute("INSERT INTO files(id, root_id, path, name, ext, kind, size, mtime, status) VALUES(?,?,?,?,?,?,?,?,?)",
                      (i, 1, f"C:/x/Screenshots/{i}{ext}" if i == 1 else f"C:/x/{i}{ext}", f"{i}{ext}", ext, kind, 9, 9, "done"))
        c.commit()

    def test_pending_depends_on_mode_and_a_stored_result_is_not_asked_for_again(self):
        self.assertEqual([f["id"] for f in main.ocr_pending(mode="screens")["files"]], [1])
        self.assertEqual(sorted(f["id"] for f in main.ocr_pending(mode="all")["files"]), [1, 2])
        self.assertEqual(main.ocr_pending(mode="off")["files"], [])
        main.ocr_store({"id": 1, "text": "Invoice   number\n 4711"})
        self.assertEqual([f["id"] for f in main.ocr_pending(mode="screens")["files"]], [])
        c = db.conn()
        self.assertEqual(c.execute("SELECT ocr FROM files WHERE id=1").fetchone()[0], "Invoice number 4711")
        c.execute("UPDATE files SET mtime=10 WHERE id=1"); c.commit()                      # the file changed: read again
        self.assertEqual([f["id"] for f in main.ocr_pending(mode="screens")["files"]], [1])

    def test_words_in_the_picture_are_found(self):
        main.ocr_store({"id": 2, "text": "Quarterly report totals"})
        self.assertEqual(main._ocr_matches(["quarterly", "report"]), [2])
        self.assertEqual(main._ocr_matches(["quarterly", "missing"]), [])
        self.assertEqual(main._ocr_matches(["ab"]), [])                                     # too short to mean anything

    def test_media_only_leaves_out_text_and_sound(self):
        c = db.conn()
        for i, (ext, kind) in enumerate(((".txt", "text"), (".mp3", "audio")), 10):
            c.execute("INSERT INTO files(id, root_id, path, name, ext, kind, size, mtime, status) VALUES(?,?,?,?,?,?,?,?,?)",
                      (i, 1, f"C:/x/{i}{ext}", f"{i}{ext}", ext, kind, 9, 9, "done"))
        c.commit()
        everything = set(main.files(ids_only=True)["ids"])
        media = set(main.files(ids_only=True, media=True)["ids"])
        self.assertTrue({10, 11} <= everything)
        self.assertEqual(media & {10, 11}, set())
        self.assertTrue({1, 2, 3} <= media)
