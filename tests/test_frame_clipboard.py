import base64
import unittest

from app.frame_clipboard import decode_png_frame


class FrameClipboardTests(unittest.TestCase):
    def test_accepts_png_frame(self):
        png = b"\x89PNG\r\n\x1a\n" + b"frame bytes"
        self.assertEqual(decode_png_frame("data:image/png;base64," + base64.b64encode(png).decode()), png)

    def test_rejects_non_png_and_invalid_encoding(self):
        for payload in ("data:image/jpeg;base64,AAAA", "data:image/png;base64,%%%%", "data:image/png;base64,QUJD"):
            with self.subTest(payload=payload), self.assertRaises(ValueError):
                decode_png_frame(payload)


if __name__ == "__main__":
    unittest.main()
