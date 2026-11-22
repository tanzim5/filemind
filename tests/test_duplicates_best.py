import unittest

from app import duplicates as d


def f(i, name="a.jpg", w=1000, h=1000, sharp=None, size=100, mtime=10, ext=None):
    return {"id": i, "name": name, "ext": ext or "." + name.rsplit(".", 1)[-1], "width": w, "height": h, "sharp": sharp, "size": size, "mtime": mtime}


class BestCopyTests(unittest.TestCase):
    def test_resolution_beats_everything(self):
        self.assertEqual(d.best([f(1, w=800, h=800, sharp=900), f(2, w=2000, h=2000, sharp=1)]), (2, "Largest (2000×2000)"))

    def test_then_sharpness_when_clearly_different(self):
        self.assertEqual(d.best([f(1, sharp=100), f(2, sharp=200)]), (2, "Sharpest"))
        self.assertEqual(d.best([f(1, sharp=100, mtime=5), f(2, sharp=105)])[0], 1)        # within 10%: older wins

    def test_lossless_then_name_then_age(self):
        self.assertEqual(d.best([f(1, "a.jpg"), f(2, "a.png")])[1], "Lossless original")
        self.assertEqual(d.best([f(1, "photo copy.jpg"), f(2, "photo.jpg")]), (2, "Original name"))
        self.assertEqual(d.best([f(1, "a.jpg", mtime=50), f(2, "b.jpg", mtime=5)]), (2, "Oldest copy"))

    def test_exact_sets_say_so_and_the_answer_is_stable(self):
        rows = [f(1, mtime=9), f(2, mtime=3), f(3, mtime=7)]
        self.assertEqual(d.best(rows, exact=True), (2, "Identical files · Oldest copy"))
        self.assertEqual(d.best(list(reversed(rows)), exact=True), (2, "Identical files · Oldest copy"))
