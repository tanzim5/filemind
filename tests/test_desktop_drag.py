import sqlite3
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from app import desktop_drag


class FileDragTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.original = Path(self.tmp.name) / "original ü sample.png"
        self.original.write_bytes(b"original bytes")
        self.conn = sqlite3.connect(":memory:")
        self.conn.row_factory = sqlite3.Row
        self.addCleanup(self.conn.close)
        self.conn.execute("CREATE TABLE files (id INTEGER, path TEXT)")
        self.conn.execute("INSERT INTO files VALUES (1, ?)", (str(self.original),))
        self.patch = patch.object(desktop_drag.db, "conn", return_value=self.conn)
        self.patch.start()
        self.addCleanup(self.patch.stop)

    def test_original_paths_are_deduplicated(self):
        self.assertEqual(desktop_drag.resolve_files([1, 1]), [str(self.original.resolve())])

    def test_invalid_or_missing_files_are_rejected(self):
        for ids in ([], ["1"], [True], [1] * 501):
            with self.assertRaises(ValueError):
                desktop_drag.resolve_files(ids)
        with self.assertRaises(FileNotFoundError):
            desktop_drag.resolve_files([999])
        self.original.unlink()
        with self.assertRaises(FileNotFoundError):
            desktop_drag.resolve_files([1])

    @unittest.skipUnless(desktop_drag.os.name == "nt", "Windows native format")
    def test_native_payload_copy_only_and_cancelled_release(self):
        import clr
        clr.AddReference("System.Windows.Forms")
        import System.Windows.Forms as forms
        from System.Threading import Thread, ThreadStart, ApartmentState

        calls, failures = [], []

        class Window:
            def Invoke(self, callback):
                # Exercise the real .NET delegate and Windows DataObject on an STA thread.
                def invoke():
                    try:
                        callback()
                    except BaseException as e:
                        failures.append(e)
                thread = Thread(ThreadStart(invoke))
                thread.SetApartmentState(ApartmentState.STA)
                thread.Start()
                thread.Join()
                if failures:
                    raise failures[0]

            def DoDragDrop(self, data, effect):
                self_outer.assertTrue(data.GetDataPresent(forms.DataFormats.FileDrop))
                calls.append((list(data.GetFileDropList()), effect))
                return forms.DragDropEffects.Copy

        self_outer = self
        window = SimpleNamespace(native=Window())
        fake_forms = SimpleNamespace(Control=SimpleNamespace(MouseButtons=forms.MouseButtons.Left),
                                     DataObject=forms.DataObject, DragDropEffects=forms.DragDropEffects,
                                     MouseButtons=forms.MouseButtons)
        with patch.dict(sys.modules, {"System.Windows.Forms": fake_forms}):
            result = desktop_drag.drag_files(window, [1])
        self.assertEqual(result["effect"], "Copy")
        self.assertEqual(calls, [([str(self.original.resolve())], forms.DragDropEffects.Copy)])
        self.assertEqual(self.original.read_bytes(), b"original bytes")
        fake_forms.Control.MouseButtons = getattr(forms.MouseButtons, "None")
        with patch.dict(sys.modules, {"System.Windows.Forms": fake_forms}):
            self.assertEqual(desktop_drag.drag_files(window, [1])["effect"], "cancelled")
        self.assertEqual(len(calls), 1)


if __name__ == "__main__":
    unittest.main()
