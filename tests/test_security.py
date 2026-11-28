import os
import tempfile
import unittest

os.environ["FILEMIND_TESTING"] = "1"
os.environ.setdefault("FILEMIND_DATA", tempfile.mkdtemp())

from fastapi.testclient import TestClient  # noqa: E402

from app import main  # noqa: E402


class GuardTests(unittest.TestCase):
    def setUp(self):
        main._TOKEN = "secret"
        self.addCleanup(setattr, main, "_TOKEN", "")
        self.client = TestClient(main.app)

    def test_reading_needs_the_token_too(self):
        self.assertEqual(self.client.get("/api/stats").status_code, 403)
        self.assertEqual(self.client.get("/api/thumb/1").status_code, 403)
        self.assertEqual(self.client.get("/api/stats", headers={"x-filemind-token": "wrong"}).status_code, 403)

    def test_the_token_works_in_a_header_or_in_the_address(self):
        self.assertNotEqual(self.client.get("/api/stats", headers={"x-filemind-token": "secret"}).status_code, 403)
        self.assertNotEqual(self.client.get("/api/thumb/1?fm_token=secret").status_code, 403)

    def test_only_health_is_open(self):
        self.assertEqual(self.client.get("/api/health").status_code, 200)

    def test_a_web_page_is_refused_even_with_the_token(self):
        r = self.client.get("/api/stats", headers={"x-filemind-token": "secret", "origin": "http://evil.example"})
        self.assertEqual(r.status_code, 403)
        r = self.client.get("/api/stats", headers={"x-filemind-token": "secret", "sec-fetch-site": "cross-site"})
        self.assertEqual(r.status_code, 403)

    def test_a_foreign_host_name_is_refused(self):
        evil = TestClient(main.app, base_url="http://evil.example")
        self.assertEqual(evil.get("/api/health").status_code, 400)

    def test_posting_needs_the_token(self):
        self.assertEqual(self.client.post("/api/understand", json={"text": "red"}).status_code, 403)
        ok = self.client.post("/api/understand", json={"text": "red"}, headers={"x-filemind-token": "secret"})
        self.assertEqual(ok.status_code, 200)
        self.assertEqual(ok.json()["color"], "red")


if __name__ == "__main__":
    unittest.main()
