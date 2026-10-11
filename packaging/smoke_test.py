"""End-to-end check of a built File Mind app: start it headless, download the model, index a photo, a video and a
sound, and make sure searching finds the right files.

    python packaging/smoke_test.py path/to/FileMind[.exe|.app/Contents/MacOS/FileMind] [--ffmpeg DIR] [--models DIR]
    python packaging/smoke_test.py python filemind.py        (a source checkout)

--models points at an existing models/ folder to reuse instead of downloading. Uses a throwaway data folder and
port 8791, so it never touches your real library."""
import argparse
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
import wave
from pathlib import Path

PORT = 8791
URL = f"http://127.0.0.1:{PORT}"


def call(path, body=None):
    req = urllib.request.Request(URL + path, data=None if body is None else json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"}, method="GET" if body is None else "POST")
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.loads(r.read() or b"null")


def wait(what, fn, seconds):
    end = time.time() + seconds
    while time.time() < end:
        try:
            v = fn()
            if v:
                return v
        except Exception:
            pass
        time.sleep(3)
    raise SystemExit(f"timed out waiting for {what}")


def make_media(d: Path, ffmpeg: str):
    from PIL import Image, ImageDraw
    img = Image.new("RGB", (640, 480), (135, 190, 235))                       # a house: sky, wall, roof, door
    g = ImageDraw.Draw(img)
    g.rectangle([0, 340, 640, 480], fill=(70, 150, 70))
    g.rectangle([200, 200, 440, 380], fill=(200, 160, 110))
    g.polygon([(180, 200), (320, 100), (460, 200)], fill=(150, 50, 40))
    g.rectangle([290, 280, 350, 380], fill=(90, 60, 40))
    img.save(d / "house.jpg", quality=92)
    with wave.open(str(d / "beep.wav"), "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(16000)
        w.writeframes(b"".join(int(20000 * math.sin(2 * math.pi * 880 * i / 16000)).to_bytes(2, "little", signed=True)
                               for i in range(32000)))
    subprocess.run([ffmpeg, "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=duration=3:size=320x240:rate=15",
                    "-pix_fmt", "yuv420p", str(d / "clip.mp4")], check=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("app", nargs="+", help="the app command (a source checkout: python filemind.py)")
    ap.add_argument("--ffmpeg", default="", help="folder with ffmpeg (default: from PATH)")
    ap.add_argument("--video-frames", default="", help="frames to read per video (default 8; fewer is faster on a CPU)")
    ap.add_argument("--models", default="", help="reuse this models folder instead of downloading")
    a = ap.parse_args()
    ffmpeg = str(Path(a.ffmpeg) / "ffmpeg") if a.ffmpeg else (shutil.which("ffmpeg") or "ffmpeg")

    root = Path(tempfile.mkdtemp(prefix="filemind-smoke-"))
    media = root / "media"; media.mkdir()
    make_media(media, ffmpeg)
    env = dict(os.environ, FILEMIND_DATA=str(root / "data"),
               FILEMIND_MODELS=a.models or str(root / "models"))
    if a.video_frames:
        env["FILEMIND_VIDEO_FRAMES"] = a.video_frames
    proc = subprocess.Popen([*a.app, "--no-window", "--port", str(PORT)], env=env)
    try:
        wait("the app to start", lambda: call("/api/model"), 180)
        if not call("/api/model")["ready"]:
            call("/api/model/download", {})
            def ready():
                s = call("/api/model")
                if s.get("error"):
                    raise SystemExit(f"download failed: {s['error']}")
                print(f"  setup: {s.get('stage')} {s.get('done', 0) >> 20}/{s.get('total', 0) >> 20} MB", flush=True)
                return s["ready"]
            wait("the model download", ready, 3600)
        call("/api/roots", {"path": str(media)})
        def indexed():
            s = call("/api/status")
            if s["phase"] == "error":
                raise SystemExit(f"indexing failed: {s['message']}")
            return not s["running"] and s["done"] == s["total"] == 3
        wait("indexing", indexed, 1800)
        errors = call("/api/status")["errors"]
        ok = True
        for q, want in (("a house with a red roof", "house.jpg"), ("a beeping tone", "beep.wav")):
            names = [f["name"] for f in call("/api/search", {"q": q, "limit": 3})["files"]]
            hit = bool(names) and names[0] == want
            ok &= hit
            print(f"  {'OK  ' if hit else 'FAIL'} {q!r} -> {names}")
        if errors or not ok:
            raise SystemExit(f"smoke test failed (indexing errors: {errors})")
        print("smoke test passed")
    except BaseException:
        log = root / "data" / "filemind.log"
        if log.exists():
            print("---- filemind.log (tail) ----\n" + "\n".join(log.read_text("utf-8", "replace").splitlines()[-40:]))
        raise
    finally:
        proc.kill()
        shutil.rmtree(root, ignore_errors=True)


if __name__ == "__main__":
    main()
