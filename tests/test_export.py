import shutil
import sqlite3
import subprocess
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch

from PIL import Image

from app import export


def spec(**over):
    return export.clean_spec(over)


class SpecTests(unittest.TestCase):
    def test_defaults_keep_the_source_format(self):
        s = spec()
        self.assertEqual(s["format"], "same")
        self.assertEqual(s["speed"], 1.0)
        self.assertEqual(export.target_format(s, "video", ".MOV"), "mov")
        self.assertEqual(export.target_format(s, "video", ".avi"), "mp4")
        self.assertEqual(export.target_format(s, "video", ".webm"), "webm")
        self.assertEqual(export.target_format(s, "audio", ".wav"), "wav")
        self.assertEqual(export.target_format(s, "image", ".bmp"), "jpg")

    def test_bad_values_are_refused_with_a_message(self):
        for bad in ({"format": "exe"}, {"start": 5, "end": 5}, {"rotate": 45}, {"speed": 9}, {"height": "tall"}):
            with self.assertRaises(ValueError):
                export.clean_spec(bad)

    def test_only_sensible_conversions_are_allowed(self):
        self.assertTrue(export.allowed("mp3", "video"))
        self.assertTrue(export.allowed("sheet", "video"))
        self.assertFalse(export.allowed("mp4", "audio"))
        self.assertFalse(export.allowed("mp4", "image"))
        self.assertFalse(export.allowed("jpg", "video"))


class CommandTests(unittest.TestCase):
    facts = {"width": 1920, "height": 1080, "duration": 60.0}

    def cmd(self, s, fmt="mp4", kind="video", src="in.mp4"):
        return export.build_command(s, Path(src), Path("out.part"), fmt, kind, self.facts)

    def test_straight_cut_copies_streams(self):
        c = self.cmd(spec(start=42, end=51, copy=True))
        self.assertEqual(c[c.index("-ss") + 1], "42.000")
        self.assertEqual(c[c.index("-t") + 1], "9.000")
        self.assertIn("copy", c)
        self.assertNotIn("libx264", c)
        self.assertLess(c.index("-ss"), c.index("-i"))

    def test_copy_is_dropped_when_the_picture_changes(self):
        c = self.cmd(spec(start=1, end=4, copy=True, height=720))
        self.assertIn("libx264", c)
        self.assertIn("scale=-2:720", c[c.index("-vf") + 1])

    def test_copy_is_dropped_when_the_container_changes(self):
        self.assertIn("libx264", self.cmd(spec(start=1, end=4, copy=True), src="in.avi"))

    def test_a_mov_can_be_cut_without_re_encoding(self):
        c = self.cmd(spec(start=1, end=4, copy=True), fmt="mov", src="in.mov")
        self.assertIn("copy", c)
        self.assertEqual(c[c.index("-f") + 1], "mov")

    def test_second_attempt_at_a_size_limit_uses_a_lower_bitrate(self):
        def rate(c):
            return int(c[c.index("-b:v") + 1].rstrip("k"))
        first = self.cmd(spec(target_mb=25))
        again = export.build_command(spec(target_mb=25), Path("in.mp4"), Path("out.part"), "mp4", "video", self.facts, 0.8)
        self.assertAlmostEqual(rate(again) / rate(first), 0.8, delta=0.01)

    def test_never_scales_up(self):
        self.assertNotIn("-vf", self.cmd(spec(height=2160)))

    def test_size_limit_sets_a_bitrate_that_fits(self):
        c = self.cmd(spec(target_mb=25))
        video_k = int(c[c.index("-b:v") + 1].rstrip("k"))
        self.assertLessEqual((video_k + 128) * 60 / 8192, 25)
        self.assertGreater(video_k, 3000)
        self.assertNotIn("-crf", c)

    def test_size_limit_uses_the_trimmed_length(self):
        def rate(c):
            return int(c[c.index("-b:v") + 1].rstrip("k"))
        long = self.cmd(spec(target_mb=10))
        short = self.cmd(spec(target_mb=10, start=0, end=10))
        self.assertGreater(rate(short), rate(long) * 5)

    def test_speed_changes_picture_and_sound_together(self):
        c = self.cmd(spec(speed=4))
        self.assertIn("setpts=PTS/4.0000", c[c.index("-vf") + 1])
        self.assertEqual(c[c.index("-af") + 1], "atempo=2.0,atempo=2.0000")

    def test_rotation_swaps_the_side_used_for_scaling(self):
        # 1920x1080 turned on its side is 1080x1920, so 1080 tall is a real reduction.
        c = self.cmd(spec(rotate=90, height=1080))
        self.assertIn("scale=-2:1080", c[c.index("-vf") + 1])

    def test_soundtrack_drops_the_picture(self):
        c = self.cmd(spec(format="mp3", normalize=True), fmt="mp3")
        self.assertIn("-vn", c)
        self.assertIn("libmp3lame", c)
        self.assertIn("loudnorm", c[c.index("-af") + 1])

    def test_gif_has_no_sound_and_its_own_scale(self):
        c = self.cmd(spec(format="gif", height=1080), fmt="gif")
        self.assertIn("-an", c)
        graph = c[c.index("-filter_complex") + 1]
        self.assertIn("palettegen", graph)
        self.assertEqual(graph.count("scale=-2:"), 1)
        self.assertIn("scale=-2:720", graph)

    def test_mute(self):
        c = self.cmd(spec(mute=True))
        self.assertIn("-an", c)
        self.assertNotIn("aac", c)

    def test_names_say_what_was_done(self):
        self.assertEqual(export.output_name(spec(start=42, end=51), "trip", "mp4"), "trip (0m42s-0m51s).mp4")
        self.assertEqual(export.output_name(spec(height=720, target_mb=25), "trip", "mp4"), "trip (720p, 25 MB).mp4")
        self.assertEqual(export.output_name(spec(), "trip", "mp3"), "trip.mp3")
        self.assertEqual(export.output_name(spec(), "trip", "sheet"), "trip (contact sheet).jpg")


class PathTests(unittest.TestCase):
    def test_free_path_never_reuses_a_name(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            (folder / "a.mp4").write_bytes(b"x")
            taken = set()
            first = export.free_path(folder, "a.mp4", taken)
            second = export.free_path(folder, "a.mp4", taken)
            self.assertEqual(first.name, "a 2.mp4")
            self.assertEqual(second.name, "a 3.mp4")


class RunTests(unittest.TestCase):
    """Real exports of tiny generated files, so the commands are known to work with the FFmpeg on this machine."""

    def setUp(self):
        if not shutil.which("ffmpeg"):
            self.skipTest("ffmpeg is not installed")
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.video = self.root / "clip.mp4"
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=duration=4:size=320x240:rate=24",
                        "-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-c:v", "libx264", "-g", "12", "-pix_fmt", "yuv420p",
                        "-c:a", "aac", "-shortest", str(self.video)], check=True, capture_output=True)
        self.image = self.root / "pic.png"
        Image.new("RGBA", (400, 300), (255, 0, 0, 128)).save(self.image)
        self.db = sqlite3.connect(":memory:", check_same_thread=False)
        self.db.row_factory = sqlite3.Row
        self.addCleanup(self.db.close)
        self.db.execute("CREATE TABLE files (id INTEGER PRIMARY KEY, path TEXT, name TEXT, kind TEXT, width INT, height INT, duration REAL)")
        self.db.execute("INSERT INTO files VALUES (1, ?, 'clip.mp4', 'video', 320, 240, 4.0)", (str(self.video),))
        self.db.execute("INSERT INTO files VALUES (2, ?, 'pic.png', 'image', 400, 300, NULL)", (str(self.image),))
        self.db.commit()
        p = patch.object(export.db, "conn", lambda: self.db)
        p.start()
        self.addCleanup(p.stop)
        export.clear_finished()

    def finish(self, made):
        ids = {j["id"] for j in made}
        deadline = time.time() + 90
        while time.time() < deadline:
            mine = [j for j in export.jobs() if j["id"] in ids]
            if all(j["status"] in ("done", "error", "cancelled") for j in mine):
                return mine
            time.sleep(0.1)
        self.fail("export did not finish")

    def duration(self, path):
        out = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", str(path)],
                             capture_output=True, text=True, check=True)
        return float(out.stdout.strip())

    def test_clip_soundtrack_gif_and_sheet(self):
        made = []
        made += export.submit([1], spec(start=1, end=3), None)
        made += export.submit([1], spec(start=1, end=3, copy=True), None)
        made += export.submit([1], spec(format="mp3"), None)
        made += export.submit([1], spec(format="gif", start=0, end=1), None)
        made += export.submit([1], spec(format="sheet"), None)
        made += export.submit([1], spec(format="webm", height=120, speed=2), None)
        done = self.finish(made)
        self.assertEqual([j["error"] for j in done], [None] * 6)
        self.assertEqual({j["status"] for j in done}, {"done"})
        exact, copied, mp3, gif, sheet, webm = [Path(j["output"]) for j in done]
        self.assertEqual(exact.name, "clip (0m01s-0m03s).mp4")
        self.assertEqual(copied.name, "clip (0m01s-0m03s) 2.mp4")
        self.assertAlmostEqual(self.duration(exact), 2.0, delta=0.2)
        self.assertAlmostEqual(self.duration(mp3), 4.0, delta=0.3)
        self.assertAlmostEqual(self.duration(webm), 2.0, delta=0.3)
        self.assertEqual(gif.read_bytes()[:3], b"GIF")
        with Image.open(sheet) as im:
            self.assertGreater(im.width, 1000)
        self.assertEqual(list(self.root.glob("*.part")), [])
        self.assertTrue(self.video.is_file())

    def test_picture_to_jpg_flattens_and_resizes(self):
        done = self.finish(export.submit([2], spec(format="jpg", height=150), None))
        self.assertEqual(done[0]["status"], "done", done[0]["error"])
        with Image.open(done[0]["output"]) as im:
            self.assertEqual((im.size, im.mode, im.format), ((200, 150), "RGB", "JPEG"))

    def test_wrong_type_is_refused_before_anything_is_queued(self):
        with self.assertRaises(ValueError):
            export.submit([1, 2], spec(format="mp4"), None)
        self.assertEqual(export.jobs(), [])

    def test_failure_leaves_no_file_behind(self):
        self.video.write_bytes(b"not a video")
        done = self.finish(export.submit([1], spec(format="mp3"), None))
        self.assertEqual(done[0]["status"], "error")
        self.assertTrue(done[0]["error"])
        self.assertEqual(sorted(p.name for p in self.root.iterdir()), ["clip.mp4", "pic.png"])


if __name__ == "__main__":
    unittest.main()
