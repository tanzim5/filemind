import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from app import autostart


class AutostartTests(unittest.TestCase):
    def test_mac_login_agent_and_background_choice_are_opt_in(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as directory:
            home = Path(directory)
            with patch.object(autostart.sys, "platform", "darwin"), \
                 patch.object(autostart.Path, "home", return_value=home), \
                 patch.object(autostart, "SETTINGS", home / "desktop.json"):
                self.assertEqual(autostart.preferences(), {"background": False, "startup": False})
                self.assertEqual(autostart.update(background=True, startup=True),
                                 {"background": True, "startup": True})
                self.assertTrue((home / "Library" / "LaunchAgents" / "app.filemind.background.plist").exists())
                self.assertEqual(autostart.update(background=False, startup=False),
                                 {"background": False, "startup": False})
                self.assertFalse((home / "Library" / "LaunchAgents" / "app.filemind.background.plist").exists())


if __name__ == "__main__":
    unittest.main()
