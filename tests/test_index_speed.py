import unittest

from app import indexer


class IndexSpeedTests(unittest.TestCase):
    def tearDown(self):
        indexer.set_speed("balanced")

    def test_levels_set_how_many_files_are_prepared_at_once(self):
        for level, workers in (("quiet", 1), ("balanced", 3), ("fast", 4)):
            self.assertEqual(indexer.set_speed(level), level)
            self.assertEqual(indexer.PREP_WORKERS[indexer.speed()], workers)

    def test_unknown_level_is_refused_and_keeps_the_old_one(self):
        indexer.set_speed("quiet")
        with self.assertRaises(ValueError):
            indexer.set_speed("turbo")
        self.assertEqual(indexer.speed(), "quiet")


if __name__ == "__main__":
    unittest.main()
