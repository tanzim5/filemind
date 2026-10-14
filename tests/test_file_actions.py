import sqlite3
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from fastapi import HTTPException

from app import main


class FileActionTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        root = Path(self.tmp.name)
        self.source = root / "source"
        self.dest = root / "destination"
        self.source.mkdir()
        self.dest.mkdir()
        self.file = self.source / "picture.png"
        self.file.write_bytes(b"original")
        self.db = sqlite3.connect(":memory:")
        self.db.row_factory = sqlite3.Row
        self.addCleanup(self.db.close)
        self.db.executescript("""
            CREATE TABLE files (id INTEGER PRIMARY KEY, path TEXT);
            CREATE TABLE project_files (project_id INTEGER, file_id INTEGER);
            CREATE TABLE proposed_names (file_id INTEGER, new_name TEXT);
            INSERT INTO project_files VALUES (4, 1);
            INSERT INTO proposed_names VALUES (1, 'new name');
        """)
        self.db.execute("INSERT INTO files VALUES (1, ?)", (str(self.file),))
        self.db.commit()
        p = patch.object(main.db, "conn", lambda: self.db)
        p.start()
        self.addCleanup(p.stop)

    def test_copy_preflights_destination_and_never_overwrites(self):
        target = self.dest / self.file.name
        target.write_bytes(b"existing")
        with self.assertRaises(HTTPException) as caught:
            main.file_actions({"action": "copy", "file_ids": [1], "destination": str(self.dest)})
        self.assertEqual(caught.exception.status_code, 409)
        self.assertEqual(target.read_bytes(), b"existing")

    def test_copy_passes_original_path_to_journal(self):
        with patch.object(main.commit_mod, "run", return_value={"done": 1, "failed": 0}) as run:
            result = main.file_actions({"action": "copy", "file_ids": [1], "destination": str(self.dest)})
        self.assertEqual(result["done"], 1)
        ops, mode = run.call_args.args
        self.assertEqual(mode, "copy")
        self.assertEqual(ops[0], {"old": str(self.file), "new": str(self.dest / self.file.name), "file_id": 1})

    def test_delete_uses_system_trash_then_removes_index_entry(self):
        with patch.object(main, "send2trash") as trash:
            result = main.file_actions({"action": "delete", "file_ids": [1]})
        trash.assert_called_once_with(str(self.file))
        self.assertEqual(result["done"], 1)
        for table in ("files", "project_files", "proposed_names"):
            self.assertEqual(self.db.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0], 0)

    def test_failed_trash_preserves_index(self):
        with patch.object(main, "send2trash", side_effect=OSError("recycle bin unavailable")):
            with self.assertRaises(HTTPException) as caught:
                main.file_actions({"action": "delete", "file_ids": [1]})
        self.assertEqual(caught.exception.status_code, 500)
        self.assertEqual(self.db.execute("SELECT COUNT(*) FROM files").fetchone()[0], 1)


if __name__ == "__main__":
    unittest.main()
