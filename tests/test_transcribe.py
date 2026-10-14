import json
import sqlite3
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from app import transcribe


class TranscriptionTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        root = Path(self.tmp.name)
        self.audio = root / "spoken audio.mp3"
        self.audio.write_bytes(b"test audio")
        self.db = sqlite3.connect(":memory:")
        self.db.row_factory = sqlite3.Row
        self.addCleanup(self.db.close)
        self.db.execute("CREATE TABLE files (id INTEGER, path TEXT, kind TEXT, size INTEGER, mtime REAL)")
        self.db.execute("INSERT INTO files VALUES (1, ?, 'audio', 10, 42)", (str(self.audio),))
        for obj, name, value in ((transcribe.db, "conn", lambda: self.db),
                                 (transcribe.db, "DATA_DIR", root),
                                 (transcribe, "MODEL_DIR", root / "model")):
            p = patch.object(obj, name, value)
            p.start()
            self.addCleanup(p.stop)
        transcribe._jobs.clear()
        transcribe._model = None

    def test_cached_transcript_and_invalid_audio(self):
        self.assertEqual(transcribe.status(1)["status"], "idle")
        path = transcribe._cache_path(1, transcribe._row(1))
        path.write_text(json.dumps({"language": "bn", "segments": [], "text": "বাংলা"}), encoding="utf-8")
        self.assertEqual(transcribe.status(1)["text"], "বাংলা")
        with self.assertRaises(FileNotFoundError):
            transcribe.status(99)

    def test_worker_saves_copyable_segments(self):
        class Model:
            def __init__(self, *args, **kwargs):
                self.kwargs = kwargs
            def transcribe(self, path, **kwargs):
                self_path = path
                return [SimpleNamespace(start=1.2, end=2.3, text=" Hello world ")], SimpleNamespace(language="en")
        module = SimpleNamespace(WhisperModel=Model)
        with patch.object(transcribe, "_ensure_model"), patch.dict(sys.modules, {"faster_whisper": module}):
            transcribe._run(1, str(self.audio), transcribe._cache_path(1, transcribe._row(1)))
        result = transcribe.status(1)
        self.assertEqual(result["status"], "done")
        self.assertEqual(result["text"], "Hello world")
        self.assertEqual(result["segments"], [{"start": 1.2, "end": 2.3, "text": "Hello world"}])


if __name__ == "__main__":
    unittest.main()
