# File Mind

Search your own photos, videos and sounds by what is in them — fully offline.

File Mind indexes a folder (for example Downloads) on your own PC with Google's
[EmbeddingGemma 2](https://huggingface.co/google/embeddinggemma-2), a multimodal embedding model.
Then you can:

- **Search in plain language** — "videos of a man running without text", "photos from Midjourney".
  Combine several searches as tags, plus colours, moods and sources.
- **Search by voice** — the recording itself is understood by the model; no speech-to-text, no cloud.
- **See tags and colours** for every file, and browse "more like this".
- **Organise into spaces** and draft new file names.

Nothing on disk changes until you review and commit, and every commit can be undone.
Nothing is uploaded: the model, the index and every search stay on this machine.

## Download (Windows)

1. Download **`FileMind-Windows-portable.zip`** from the [latest release](https://github.com/tanzim5/filemind/releases/latest).
2. Unzip it anywhere (Desktop, a USB stick…).
3. Double-click **`FileMind.exe`**.

Nothing to install: Python, ffmpeg and everything else are inside the folder. The first launch downloads the
AI model once (about 1.5 GB) into that same folder; after that File Mind works fully offline.
To remove File Mind, delete the folder.

It runs on any modern PC — no graphics card needed. Searching is instant. Reading a new library is a one-time
job of a few seconds per photo (more for videos) and uses about 4 GB of memory; you can keep using File Mind
while it works, and it picks up where it left off if you close it.

Windows may show "Windows protected your PC" the first time, because the app isn't code-signed:
click **More info → Run anyway**.

## Run from source

Requires Python 3.12 and [ffmpeg](https://ffmpeg.org/) on your PATH. An NVIDIA GPU makes indexing much faster; it also runs on CPU.

```bash
python -m venv .venv
.venv\Scripts\activate          # Windows  (macOS/Linux: source .venv/bin/activate)
pip install -r requirements.txt
python -m uvicorn app.main:app --port 8765
```

Open http://127.0.0.1:8765. On first run the model (~1.4 GB) is downloaded once from Hugging Face
(or place it in `models/embeddinggemma-2/` yourself); after that File Mind works with no internet connection.

On Windows you can also double-click `run.bat`. To get the desktop window instead of a browser tab,
`pip install pywebview` and run `python filemind.py`.

To build the portable zip yourself, see `packaging/build_windows.py`; GitHub Actions builds it automatically
for every `v*` tag.

## Privacy

Your index lives in `data/` next to the app and is excluded from git. Delete that folder to forget everything.

## Licence

The EmbeddingGemma 2 weights are Apache-2.0. Interface fonts: Inter and Instrument Serif (SIL OFL).
