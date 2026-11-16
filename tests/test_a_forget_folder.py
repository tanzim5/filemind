import os
import tempfile
import unittest

_tmp = tempfile.mkdtemp()
os.environ.setdefault("FILEMIND_DATA", _tmp)

from app import db, extract, main  # noqa: E402


@unittest.skipUnless(os.path.samefile(db.DATA_DIR, os.environ["FILEMIND_DATA"]) and "tmp" in str(db.DATA_DIR).lower(), "needs its own empty data folder")
class ForgetFolderTests(unittest.TestCase):
    def setUp(self):
        db.init()
        self.c = db.conn()
        for t in ("files", "roots", "project_files", "file_tags"):
            self.c.execute(f"DELETE FROM {t}")
        self.c.execute("INSERT INTO roots(id, path, added) VALUES(1, 'C:/a', 0)")
        for i, root in ((1, 1), (2, 1), (3, 9)):                       # file 3 belongs to a folder that no longer exists
            (extract.THUMB_DIR / f"t{i}.jpg").write_bytes(b"x")
            self.c.execute("INSERT INTO files(id, root_id, path, name, ext, kind, size, mtime, status, thumb) "
                           "VALUES(?,?,?,?,?,?,?,?,?,?)", (i, root, f"C:/a/{i}.jpg", f"{i}.jpg", ".jpg", "image", 1, 1, "done", f"t{i}.jpg"))
        self.c.execute("INSERT INTO file_tags(file_id, tag) VALUES(1, 'x')")
        self.c.commit()

    def test_forgetting_a_folder_removes_files_tags_and_thumbnails(self):
        main.del_root(1)
        self.assertEqual(self.c.execute("SELECT COUNT(*) FROM files WHERE root_id=1").fetchone()[0], 0)
        self.assertEqual(self.c.execute("SELECT COUNT(*) FROM file_tags").fetchone()[0], 0)
        self.assertFalse((extract.THUMB_DIR / "t1.jpg").exists())
        self.assertFalse((extract.THUMB_DIR / "t2.jpg").exists())
        self.assertTrue((extract.THUMB_DIR / "t3.jpg").exists())       # another folder's file is untouched

    def test_files_of_a_missing_folder_and_stray_thumbnails_are_cleaned_up(self):
        (extract.THUMB_DIR / "stray.jpg").write_bytes(b"x")
        main._drop_files(self.c, "root_id NOT IN (SELECT id FROM roots)")
        main._sweep_thumbs(self.c)
        self.assertEqual([r[0] for r in self.c.execute("SELECT id FROM files ORDER BY id")], [1, 2])
        self.assertFalse((extract.THUMB_DIR / "t3.jpg").exists())
        self.assertFalse((extract.THUMB_DIR / "stray.jpg").exists())
        self.assertTrue((extract.THUMB_DIR / "t1.jpg").exists())


if __name__ == "__main__":
    unittest.main()
