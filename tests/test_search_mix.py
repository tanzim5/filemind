import asyncio
import sqlite3
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np

from app import db, main, search_mix


def unit(*xs):
    v = np.array(xs, dtype=np.float32)
    return v / np.linalg.norm(v)


class CombineTests(unittest.TestCase):
    def test_default_is_an_even_mix_and_unit_length(self):
        r, t = unit(1, 0, 0), unit(0, 1, 0)
        out = search_mix.combine(r, t)
        self.assertAlmostEqual(float(np.linalg.norm(out)), 1.0, places=5)
        np.testing.assert_allclose(out, unit(1, 1, 0), atol=1e-6)

    def test_weights_pick_the_ends(self):
        r, t = unit(1, 0), unit(0, 1)
        np.testing.assert_allclose(search_mix.combine(r, t, 0), r, atol=1e-6)
        np.testing.assert_allclose(search_mix.combine(r, t, 1), t, atol=1e-6)
        toward_text = search_mix.combine(r, t, 0.8)
        self.assertGreater(float(toward_text @ t), float(toward_text @ r))

    def test_inputs_are_normalised_first(self):
        out = search_mix.combine(np.array([10, 0, 0], dtype=np.float32), np.array([0, 0.1, 0], dtype=np.float32))
        np.testing.assert_allclose(out, unit(1, 1, 0), atol=1e-6)

    def test_weight_is_clamped_and_bad_values_use_the_default(self):
        r, t = unit(1, 0), unit(0, 1)
        np.testing.assert_allclose(search_mix.combine(r, t, 7), t, atol=1e-6)
        np.testing.assert_allclose(search_mix.combine(r, t, -3), r, atol=1e-6)
        for bad in (None, "abc", float("nan")):
            np.testing.assert_allclose(search_mix.combine(r, t, bad), unit(1, 1), atol=1e-6)

    def test_opposite_vectors_fall_back_to_the_reference(self):
        r = unit(1, 0)
        np.testing.assert_allclose(search_mix.combine(r, -r, 0.5), r, atol=1e-6)


class EndpointTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.conn = sqlite3.connect(":memory:")
        self.conn.row_factory = sqlite3.Row
        self.addCleanup(self.conn.close)
        self.conn.executescript(db.SCHEMA)
        self.ref_vec = unit(1, 0, 0, 0)
        self.path = Path(self.tmp.name) / "a.png"
        self.path.write_bytes(b"x")
        self.conn.execute("INSERT INTO files(id,root_id,path,name,ext,kind,size,mtime,status,embed_mode,vec) VALUES(1,1,?,?,?,?,?,?,?,?,?)",
                          (str(self.path), "a.png", ".png", "image", 1, 1, "done", "content", self.ref_vec.tobytes()))
        self.conn.commit()
        self.captured = []
        for p in (patch.object(main.db, "conn", lambda: self.conn),
                  patch.object(main, "_rank", lambda q, *a, **k: self.captured.append(q) or []),
                  patch.object(main.embedder, "embed_query", lambda text: unit(0, 1, 0, 0)),
                  patch.object(main.embedder, "embed_reference", lambda path, kind, text="": self.ref_vec)):
            p.start()
            self.addCleanup(p.stop)

    def test_similar_to_without_words_uses_the_file_vector_alone(self):
        main.search({"similar_to": 1})
        np.testing.assert_allclose(self.captured[0], self.ref_vec)

    def test_similar_to_with_words_mixes_and_text_weight_moves_it(self):
        main.search({"similar_to": 1, "text": "but at night"})
        np.testing.assert_allclose(self.captured[0], unit(1, 1, 0, 0), atol=1e-6)
        main.search({"similar_to": 1, "text": "but at night", "text_weight": 1})
        np.testing.assert_allclose(self.captured[1], unit(0, 1, 0, 0), atol=1e-6)
        main.search({"similar_to": 1, "text": "but at night", "text_weight": 0.2})
        self.assertGreater(float(self.captured[2] @ self.ref_vec), float(self.captured[2] @ unit(0, 1, 0, 0)))

    def test_reference_id_with_words_mixes(self):
        main.search({"reference_id": 1, "text": "at night", "text_weight": 0.5})
        np.testing.assert_allclose(self.captured[0], unit(1, 1, 0, 0), atol=1e-6)

    def test_blank_words_do_not_mix(self):
        main.search({"similar_to": 1, "text": "   "})
        np.testing.assert_allclose(self.captured[0], self.ref_vec)

    def test_uploaded_image_endpoint_takes_text_and_weight(self):
        class Upload:
            filename = "q.png"
            async def read(self):
                return b"x"
        asyncio.run(main.search_by_image(Upload(), None, 100, "at night", 0.5))
        np.testing.assert_allclose(self.captured[0], unit(1, 1, 0, 0), atol=1e-6)
        asyncio.run(main.search_by_image(Upload(), None, 100, "", 0.5))
        np.testing.assert_allclose(self.captured[1], self.ref_vec, atol=1e-6)


if __name__ == "__main__":
    unittest.main()
