import os
import tempfile
import unittest

import numpy as np

_tmp = tempfile.mkdtemp()
os.environ.setdefault("FILEMIND_DATA", _tmp)

from app import voice_stream  # noqa: E402

SR = voice_stream.SR


def pcm(seconds, loud):
    n = int(seconds * SR)
    wave = (np.sin(np.arange(n) / 20.0) * (12000 if loud else 0)).astype("<i2")
    return wave.tobytes()


def energy_vad(audio):
    """A stand-in for the speech detector: loud stretches are speech."""
    loud = np.abs(audio) > 0.05
    if not loud.any():
        return []
    idx = np.flatnonzero(loud)
    return [{"start": int(idx[0]), "end": int(idx[-1]) + 1}]


class UtteranceTests(unittest.TestCase):
    def test_common_prefix_ignores_case_and_punctuation(self):
        self.assertEqual(voice_stream.common_prefix(["Find", "me", "a"], ["find", "me,", "a", "file"]), 3)
        self.assertEqual(voice_stream.common_prefix([], ["x"]), 0)

    def test_words_become_stable_when_two_readings_agree(self):
        readings = iter(["find me", "find me a file", "find me a file from august"])
        u = voice_stream.Utterance(lambda a: next(readings), energy_vad)
        u.feed(pcm(1.0, True)); u.probe()
        self.assertTrue(u.heard)
        self.assertEqual(u.read_partial(), ("", "find me"))                 # first reading: nothing confirmed yet
        u.feed(pcm(0.5, True)); u.probe()
        self.assertEqual(u.read_partial(), ("find me", "a file"))           # confirmed by the second reading
        u.feed(pcm(0.5, True)); u.probe()
        self.assertEqual(u.read_partial(), ("find me a file", "from august"))

    def test_speech_then_silence_ends_the_utterance(self):
        u = voice_stream.Utterance(lambda a: "hello", energy_vad)
        u.feed(pcm(1.0, True))
        speaking, ended = u.probe()
        self.assertTrue(speaking)
        self.assertFalse(ended)
        u.feed(pcm(0.8, False))
        speaking, ended = u.probe()
        self.assertFalse(speaking)
        self.assertTrue(ended)

    def test_silence_alone_gives_up_with_nothing(self):
        u = voice_stream.Utterance(lambda a: "should not be read", energy_vad)
        u.feed(pcm(2.0, False)); u.probe()
        self.assertFalse(u.gave_up())
        u.feed(pcm(5.0, False)); u.probe()
        self.assertTrue(u.gave_up())
        self.assertEqual(u.final_text(), "")

    def test_the_final_reading_wins_and_a_failure_falls_back_to_the_live_words(self):
        u = voice_stream.Utterance(lambda a: "fine me a file", energy_vad, lambda a: "find me a file")
        u.feed(pcm(1.0, True)); u.probe(); u.read_partial()
        self.assertEqual(u.final_text(), "find me a file")

        def boom(a):
            raise RuntimeError("model crashed")
        v = voice_stream.Utterance(lambda a: "fine me a file", energy_vad, boom)
        v.feed(pcm(1.0, True)); v.probe(); v.read_partial()
        self.assertEqual(v.final_text(), "fine me a file")


class SocketTests(unittest.TestCase):
    def setUp(self):
        from fastapi.testclient import TestClient
        from app import main
        self.main = main
        main._TOKEN = "secret"
        self.client = TestClient(main.app)
        self.addCleanup(setattr, main, "_TOKEN", "")

    def test_no_token_or_an_origin_is_refused(self):
        from starlette.websockets import WebSocketDisconnect
        with self.assertRaises(WebSocketDisconnect):
            with self.client.websocket_connect("/api/voice/stream"):
                pass
        with self.assertRaises(WebSocketDisconnect):
            with self.client.websocket_connect("/api/voice/stream?t=secret", headers={"origin": "http://evil.example"}):
                pass

    def test_a_missing_model_is_reported_not_crashed(self):
        orig = voice_stream.ready_partial
        voice_stream.ready_partial = lambda: False
        self.addCleanup(setattr, voice_stream, "ready_partial", orig)
        with self.client.websocket_connect("/api/voice/stream?t=secret") as ws:
            msg = ws.receive_json()
        self.assertEqual((msg["type"], msg["code"]), ("error", "model_missing"))


if __name__ == "__main__":
    unittest.main()


class FakeSocket:
    def __init__(self, incoming):
        self.incoming, self.sent = list(incoming), []

    async def receive(self):
        import asyncio
        while not self.incoming:
            await asyncio.sleep(0.01)
        item = self.incoming.pop(0)
        if isinstance(item, (int, float)):
            await asyncio.sleep(item)
            return await self.receive()
        return item

    async def send_json(self, data):
        self.sent.append(data)


class SessionFlowTests(unittest.IsolatedAsyncioTestCase):
    async def test_a_spoken_question_goes_in_and_a_final_text_with_filters_comes_out(self):
        import json
        from app.understand import understand
        orig = voice_stream.ready_partial
        voice_stream.ready_partial = lambda: True
        self.addCleanup(setattr, voice_stream, "ready_partial", orig)

        class Fake:
            language = None
            def partial(self, audio): return "find me red photos"
            def final(self, audio): return "find me red photos from yesterday"

        ws = FakeSocket([{"type": "websocket.receive", "text": json.dumps({"type": "start", "now": 1795000000, "utc_offset_min": 0, "day_first": True})},
                         {"type": "websocket.receive", "bytes": pcm(1.2, True)}, 0.5,
                         {"type": "websocket.receive", "bytes": pcm(1.0, False)}])
        await voice_stream.run(ws, lambda text, ctx: understand(text, **ctx), make_recogniser=Fake, vad_fn=energy_vad)
        kinds = [m["type"] for m in ws.sent]
        self.assertIn("speech", kinds)
        self.assertIn("partial", kinds)
        final = ws.sent[-1]
        self.assertEqual(final["type"], "final")
        self.assertEqual(final["text"], "find me red photos from yesterday")
        self.assertEqual(final["understood"]["color"], "red")
        self.assertEqual(final["understood"]["kinds"], ["image"])
        self.assertIn("after", final["understood"]["filters"])
