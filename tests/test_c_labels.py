import json
import os
import tempfile
import unittest

_tmp = tempfile.mkdtemp()
os.environ.setdefault("FILEMIND_DATA", _tmp)

from app import db, main  # noqa: E402


@unittest.skipUnless(os.path.samefile(db.DATA_DIR, os.environ["FILEMIND_DATA"]) and "tmp" in str(db.DATA_DIR).lower(), "needs its own empty data folder")
class LabelTests(unittest.TestCase):
    def setUp(self):
        db.init()
        self.c = db.conn()
        for t in ("files", "roots", "file_tags", "tags", "file_labels"):
            self.c.execute(f"DELETE FROM {t}")
        self.c.execute("DELETE FROM meta WHERE key IN ('labels_migrated','min_side','min_side_mode')")
        self.c.execute("INSERT INTO roots(id, path, added) VALUES(1, 'C:/a', 0)")
        for i, (w, h, kind) in enumerate(((1920, 1080, "image"), (200, 150, "image"), (800, 2000, "image"), (None, None, "text")), 1):
            self.c.execute("INSERT INTO files(id, root_id, path, name, ext, kind, size, mtime, status, width, height) VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                           (i, 1, f"C:/a/{i}.x", f"{i}.x", ".x", kind, 1, i, "done", w, h))
        self.c.commit()
        main._label_cache = None

    def test_apply_filter_and_sort(self):
        main.labels_apply({"ids": [1, 3], "label": "red"}); main.labels_apply({"ids": [2], "label": "blue"})
        self.assertEqual(sorted(main.files(label="red", ids_only=True)["ids"]), [1, 3])
        self.assertEqual(main.files(label="blue,red", ids_only=True)["ids"].__len__(), 3)
        order = main.files(sort="label", ids_only=True)["ids"]
        self.assertEqual(order[-1], 4)                                       # unlabelled last
        self.assertEqual(main.files(limit=10)["files"][0]["label"] in ("red", "blue", None), True)
        main.labels_apply({"ids": [1], "label": None})
        self.assertEqual(main.files(label="red", ids_only=True)["ids"], [3])
        with self.assertRaises(Exception):
            main.labels_apply({"ids": [1], "label": "pink"})

    def test_old_tags_become_labels_and_are_backed_up(self):
        self.c.execute("INSERT INTO tags(name, color) VALUES('Hero', '#22C55E'), ('Favourites', '#F59E0B')")
        self.c.execute("INSERT INTO file_tags(file_id, tag) VALUES(1,'Hero'),(2,'Favourites')")
        self.c.commit()
        main._migrate_labels(self.c)
        self.assertEqual(main._file_labels().get(1), "green")
        self.assertNotIn(2, main._file_labels())                              # favourites stay favourites
        saved = json.loads((db.DATA_DIR / "legacy-tags.json").read_text(encoding="utf-8"))
        self.assertEqual(saved[0]["tag"], "Hero")
        self.assertEqual(self.c.execute("SELECT COUNT(*) FROM file_tags WHERE tag='Favourites'").fetchone()[0], 1)

    def test_minimum_size_hides_small_pictures_only_at_query_time(self):
        main.set_prefs({"min_side": 300, "min_side_mode": "short"})
        self.assertEqual(sorted(main.files(ids_only=True)["ids"]), [1, 3, 4])   # 200x150 hidden; text and unknown sizes stay
        main.set_prefs({"min_side": 1000, "min_side_mode": "long"})
        self.assertEqual(sorted(main.files(ids_only=True)["ids"]), [1, 3, 4])   # long side 1920 and 2000 pass
        main.set_prefs({"min_side": 0})
        self.assertEqual(len(main.files(ids_only=True)["ids"]), 4)
        self.assertEqual(self.c.execute("SELECT COUNT(*) FROM files").fetchone()[0], 4)   # nothing was deleted


class SourceRuleTests(unittest.TestCase):
    def test_folder_and_screen_size_say_where_a_file_came_from(self):
        from app import names
        self.assertEqual(names.source_of("anything.png", r"C:\Users\me\Pictures\Screenshots\a.png"), "screenshot")
        self.assertEqual(names.source_of("report.pdf", r"C:\Users\me\AppData\Local\Microsoft\Windows\INetCache\Content.Outlook\ABC\report.pdf"), "attachments")
        self.assertEqual(names.source_of("clip.mp4", r"D:\Downloads\clip.mp4"), "downloads")
        self.assertEqual(names.source_of("x.jpg", r"E:\DCIM\100\x.jpg"), "camera")
        self.assertEqual(names.source_of("shot.png", r"C:\work\shot.png", 2560, 1440), "screenshot")
        self.assertIsNone(names.source_of("shot.jpg", r"C:\work\shot.jpg", 2560, 1440))          # only PNG
        self.assertEqual(names.source_of("Midjourney_x--ar 16:9.png", r"D:\Downloads\x.png"), "midjourney")   # the name wins
        self.assertIsNone(names.source_of("plain.txt", r"C:\work\plain.txt"))

