import unittest
from datetime import datetime, timezone

from app.understand import understand

NOW = datetime(2026, 11, 28, 12, 0, tzinfo=timezone.utc).timestamp()


def day(y, m, d):
    return datetime(y, m, d, tzinfo=timezone.utc).timestamp()


def U(text, day_first=True):
    return understand(text, NOW, 0, day_first)


class UnderstandTests(unittest.TestCase):
    def test_a_file_from_a_date_with_a_colour(self):
        r = U("find me a file from 5th August, red colour")
        self.assertEqual(r["filters"]["after"], day(2026, 8, 5))
        self.assertEqual(r["filters"]["before"], day(2026, 8, 6))
        self.assertEqual(r["color"], "red")
        self.assertEqual(r["text"], "")

    def test_relative_months_ago(self):
        r = U("files from 1 month ago")
        self.assertTrue(day(2026, 10, 18) <= r["filters"]["after"] <= day(2026, 10, 23))
        self.assertTrue(day(2026, 11, 3) <= r["filters"]["before"] <= day(2026, 11, 8))
        self.assertEqual(r["text"], "")

    def test_the_long_question(self):
        r = U("vertical 4K videos of a beach over 10 seconds")
        self.assertEqual(r["filters"]["aspect"], "portrait")
        self.assertEqual(r["filters"]["min_pixels"], 8294400)
        self.assertEqual(r["filters"]["min_duration"], 10)
        self.assertEqual(r["kinds"], ["video"])
        self.assertEqual(r["text"], "a beach")

    def test_last_week_and_yesterday(self):
        r = U("photos from last week")            # 2026-11-28 is a Saturday: last week = 16 Nov .. 23 Nov
        self.assertEqual((r["filters"]["after"], r["filters"]["before"]), (day(2026, 11, 16), day(2026, 11, 23)))
        self.assertEqual(r["kinds"], ["image"])
        r = U("screenshots from yesterday")
        self.assertEqual(r["source"], "screenshot")
        self.assertEqual((r["filters"]["after"], r["filters"]["before"]), (day(2026, 11, 27), day(2026, 11, 28)))

    def test_source_and_without(self):
        r = U("midjourney images of cats without text")
        self.assertEqual(r["source"], "midjourney")
        self.assertEqual(r["kinds"], ["image"])
        self.assertEqual(r["without"], "text")
        self.assertEqual(r["text"], "cats")

    def test_year_and_kind(self):
        r = U("pdfs about invoices from 2024")
        self.assertEqual(r["kinds"], ["doc"])
        self.assertEqual((r["filters"]["after"], r["filters"]["before"]), (day(2024, 1, 1), day(2025, 1, 1)))
        self.assertEqual(r["text"], "invoices")

    def test_durations_and_sizes(self):
        r = U("songs under 3 minutes")
        self.assertEqual((r["kinds"], r["filters"]["max_duration"]), (["audio"], 180))
        r = U("clips between 5 and 10 seconds")
        self.assertEqual((r["filters"]["min_duration"], r["filters"]["max_duration"]), (5, 10))
        r = U("files bigger than 2 gb")
        self.assertEqual(r["filters"]["min_size"], 2_000_000_000)

    def test_only_the_first_colour_is_a_filter(self):
        r = U("blue and orange sunset")
        self.assertEqual(r["color"], "blue")
        self.assertEqual(r["text"], "orange sunset")

    def test_scene_words_are_not_colours(self):
        r = U("a forest at dawn")
        self.assertIsNone(r["color"])
        self.assertEqual(r["text"], "a forest at dawn")
        self.assertEqual(U("sky blue colour")["color"], "sky")

    def test_numeric_date_depends_on_region(self):
        r = U("5/8", day_first=True)
        self.assertEqual(r["filters"]["after"], day(2026, 8, 5))
        r = U("5/8", day_first=False)
        self.assertEqual(r["filters"]["after"], day(2026, 5, 8))

    def test_a_month_means_its_most_recent_past(self):
        r = U("photos in august")
        self.assertEqual(r["filters"]["after"], day(2026, 8, 1))
        later = understand("photos in august", datetime(2027, 2, 10, 12, tzinfo=timezone.utc).timestamp(), 0, True)
        self.assertEqual(later["filters"]["after"], day(2026, 8, 1))
        earlier = understand("photos in december", NOW, 0, True)
        self.assertEqual(earlier["filters"]["after"], day(2025, 12, 1))

    def test_spoken_slow_motion_clips(self):
        r = U("um can you show me ten second slow motion clips")
        self.assertEqual(r["kinds"], ["video"])
        self.assertEqual(r["filters"]["min_fps"], 50)
        self.assertEqual((r["filters"]["min_duration"], r["filters"]["max_duration"]), (7.5, 12.5))
        self.assertEqual(r["text"], "")

    def test_ranges_and_since(self):
        r = U("between 5 august and 10 august")
        self.assertEqual((r["filters"]["after"], r["filters"]["before"]), (day(2026, 8, 5), day(2026, 8, 11)))
        r = U("since 1 november")
        self.assertEqual(r["filters"]["after"], day(2026, 11, 1))
        self.assertNotIn("before", r["filters"])

    def test_nothing_to_understand(self):
        self.assertEqual(U("")["text"], "")
        r = U("a dog running on the beach")
        self.assertEqual((r["text"], r["filters"], r["kinds"]), ("a dog running on the beach", {}, []))

    def test_the_timezone_moves_the_day(self):
        r = understand("yesterday", NOW, 600, True)       # UTC+10: it is already 28 Nov 22:00 there
        self.assertEqual(r["filters"]["before"], day(2026, 11, 28) - 600 * 60)

    def test_chips_describe_what_was_understood(self):
        chips = {c["key"]: c["value"] for c in U("find me red photos from 5th August of a beach")["chips"]}
        self.assertEqual(chips["date"], "5 Aug 2026")
        self.assertEqual(chips["color"], "Red")
        self.assertEqual(chips["kind"], "Photos")
        self.assertEqual(chips["text"], "a beach")


if __name__ == "__main__":
    unittest.main()
