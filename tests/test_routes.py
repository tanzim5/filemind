import os
import tempfile
import unittest

os.environ.setdefault("FILEMIND_DATA", tempfile.mkdtemp())

from app import main  # noqa: E402

# The app calls these; deleting one by accident breaks first launch or a whole page.
NEEDED = ["/api/health", "/api/model", "/api/model/download", "/api/files", "/api/search", "/api/understand", "/api/stats",
          "/api/roots", "/api/status", "/api/facets", "/api/voice/model",
          "/api/voice/stream", "/api/duplicates", "/api/discover", "/api/projects", "/api/labels", "/api/info/{fid}"]


class RouteTests(unittest.TestCase):
    def test_the_routes_the_app_needs_exist(self):
        have = {getattr(r, "path", "") for r in main.app.routes}
        missing = [p for p in NEEDED if p not in have]
        self.assertEqual(missing, [])
