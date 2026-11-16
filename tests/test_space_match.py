import unittest
from unittest.mock import patch

import numpy as np

from app import main, space_match


def unit(*xs):
    v = np.array(xs, dtype=np.float32)
    return v / np.linalg.norm(v)


rng = np.random.default_rng(1)
# 200 files scattered around one direction (the "background"), then 5 cat files along x and 4 dog files along y.
noise = rng.normal(size=(200, 8)).astype(np.float32) * 0.15
noise[:, 7] += 1.0
BG = noise / np.linalg.norm(noise, axis=1, keepdims=True)
CATS = np.vstack([unit(1, 0.05 * i, 0, 0, 0, 0, 0, 0.4) for i in range(5)])
DOGS = np.vstack([unit(0, 1, 0.05 * i, 0, 0, 0, 0, 0.4) for i in range(4)])
MAT = np.vstack([BG, CATS, DOGS]).astype(np.float32)
IDS = np.arange(1000, 1000 + len(MAT))
QCAT, QDOG = unit(1, 0, 0, 0, 0, 0, 0, 0.3), unit(0, 1, 0, 0, 0, 0, 0, 0.3)


class MatchTests(unittest.TestCase):
    def test_only_the_files_that_stand_out_are_kept(self):
        rows, sims, z = space_match.match_one(MAT, QCAT, floor=0.5)
        self.assertEqual(set(rows.tolist()), set(range(200, 205)))
        self.assertTrue(all(z[i] >= z[i + 1] for i in range(len(z) - 1)))

    def test_raw_floor_drops_weak_matches_even_when_they_stand_out(self):
        rows, _, _ = space_match.match_one(MAT, QCAT, floor=0.999)
        self.assertEqual(len(rows), 0)

    def test_empty_library(self):
        rows, _, _ = space_match.match_one(np.zeros((0, 8), np.float32), QCAT)
        self.assertEqual(len(rows), 0)

    def test_merge_unions_and_counts_each_description(self):
        found, per = space_match.match(IDS, MAT, [QCAT, QDOG], floor=0.5)
        self.assertEqual(per, [5, 4])
        self.assertEqual({f for f, _ in found}, set(range(1200, 1209)))

    def test_a_file_fitting_two_descriptions_is_listed_once(self):
        found, per = space_match.match(IDS, MAT, [QCAT, QCAT], floor=0.5)
        self.assertEqual(per, [5, 5])
        self.assertEqual(len(found), 5)
        self.assertEqual(len({f for f, _ in found}), 5)

    def test_cap(self):
        found, _ = space_match.match(IDS, MAT, [QCAT, QDOG], floor=0.5, limit=6)
        self.assertEqual(len(found), 6)

    def test_nothing_matches_a_vector_that_fits_nothing(self):
        found, per = space_match.match(IDS, MAT, [unit(0, 0, 1, 0, 0, 0, 0, 0)], floor=0.5)
        self.assertEqual((found, per), ([], [0]))


class EndpointTests(unittest.TestCase):
    def test_validation(self):
        with self.assertRaises(Exception):
            main.spaces_match({"descriptions": []})
        with self.assertRaises(Exception):
            main.spaces_match({"descriptions": ["x"] * 9})


if __name__ == "__main__":
    unittest.main()
