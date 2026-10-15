"""Exercise the real headless desktop lifecycle without touching login settings."""
import os
import socket
import subprocess
import sys
import tempfile
import time
import unittest
import urllib.error
import urllib.request
from pathlib import Path


class BackgroundLauncherTests(unittest.TestCase):
    def test_background_server_can_be_reused_and_stopped_privately(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as directory:
            with socket.socket() as sock:
                sock.bind(("127.0.0.1", 0))
                port = sock.getsockname()[1]
            env = dict(os.environ, FILEMIND_DATA=directory)
            process = subprocess.Popen(
                [sys.executable, str(Path(__file__).resolve().parent.parent / "filemind.py"),
                 "--background", "--port", str(port)],
                env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            )
            try:
                url = f"http://127.0.0.1:{port}"
                for _ in range(100):
                    if process.poll() is not None:
                        self.fail(f"background server exited early ({process.returncode})")
                    try:
                        with urllib.request.urlopen(url + "/api/model", timeout=1) as response:
                            if response.status == 200:
                                break
                    except (OSError, urllib.error.URLError):
                        time.sleep(0.1)
                else:
                    self.fail("background server did not start")

                endpoint = url + "/api/desktop/quit"
                with self.assertRaises(urllib.error.HTTPError) as denied:
                    urllib.request.urlopen(urllib.request.Request(endpoint, data=b"", method="POST"), timeout=2)
                self.assertEqual(denied.exception.code, 403)
                self.assertIsNone(process.poll())

                token = (Path(directory) / f"desktop-session-{port}").read_text(encoding="ascii")
                request = urllib.request.Request(endpoint, data=b"", method="POST",
                    headers={"X-FileMind-Desktop": token})
                with urllib.request.urlopen(request, timeout=2) as response:
                    self.assertEqual(response.status, 200)
                self.assertEqual(process.wait(timeout=10), 0)
                self.assertFalse((Path(directory) / f"desktop-session-{port}").exists())
            finally:
                if process.poll() is None:
                    process.terminate()
                    process.wait(timeout=10)


if __name__ == "__main__":
    unittest.main()
