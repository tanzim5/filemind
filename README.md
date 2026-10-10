<p align="center"><img src="docs/banner.png" alt="File Mind — search your own photos, videos and sounds by what's in them, fully offline" width="100%"></p>

<p align="center">
  <a href="https://github.com/tanzim5/filemind/releases/latest"><b>⬇ Download for Windows</b></a> ·
  <a href="#what-it-does">What it does</a> ·
  <a href="#privacy">Privacy</a> ·
  <a href="#run-from-source">Run from source</a>
</p>

---

File Mind reads a folder on your PC — Downloads, Pictures, a project drive — and lets you find anything in it
by describing it. It understands **what is in** your photos, videos and sounds using Google's
[EmbeddingGemma 2](https://huggingface.co/google/embeddinggemma-2), a multimodal AI model that runs entirely on
your own computer. No account, no cloud, no uploads.

![File Mind](docs/empty.png)

## What it does

| | |
|---|---|
| 🔎 **Search in plain language** | "videos of a man running without text", "photos from Midjourney", "red logo on black". Stack several searches as tags. |
| 🎙️ **Search by voice** | Click the mic and describe it. The recording itself is understood by the model — no speech-to-text, nothing sent anywhere. |
| 🖼️ **Photos, videos and sounds** | Looks at images and video frames, listens to audio. Also filter by colour, by type, or by the AI tool a file came from. |
| 🏷️ **Tags and colour palettes** | Every file gets automatic tags and its main colours. Click any of them to find more like it. |
| ✨ **More like this** | Open a file and keep following similar ones, endlessly. |
| 🗂️ **Spaces and renames** | Group files into spaces and draft clearer file names. Nothing on disk changes until you review and commit — and every commit can be undone. |
| 🌗 **Make it yours** | Ten themes including dark Night and Ink, accent colours, masonry or rows, tile size. |

<p align="center"><img src="docs/dark.png" alt="File Mind in the Night theme" width="49%"> <img src="docs/themes.png" alt="Customize panel with themes and accent colours" width="49%"></p>

## Download (Windows)

1. Download **`FileMind-Windows-portable.zip`** from the [latest release](https://github.com/tanzim5/filemind/releases/latest).
2. Unzip it anywhere (Desktop, a USB stick…).
3. Double-click **`FileMind.exe`**.

Nothing to install: Python, ffmpeg and everything else are inside the folder. The first launch downloads the
AI model once (about 1.5 GB) into that same folder; after that File Mind works fully offline.
To remove File Mind, delete the folder.

<p align="center"><img src="docs/setup.png" alt="First launch: the model downloads once" width="70%"></p>

**Requirements:** Windows 10 or 11 (64-bit) and about 8 GB of RAM. No graphics card needed.
Searching is instant. Reading a new library is a one-time job of a few seconds per photo (more for videos);
you can keep using File Mind while it works, and it picks up where it left off if you close it.

Windows may show "Windows protected your PC" the first time, because the app isn't code-signed:
click **More info → Run anyway**.

macOS and Linux builds are planned; until then you can [run from source](#run-from-source) on any platform.

## Privacy

- Everything runs on your computer. Your files, the index and every search stay there.
- The only network use is the one-time model download from Hugging Face.
- File Mind only **reads** your folders. Renames and moves happen only when you press commit, and can be undone.
- The index lives in a `data` folder next to the app. Delete it to forget everything.

## Run from source

Requires Python 3.12 and [ffmpeg](https://ffmpeg.org/) on your PATH. An NVIDIA GPU makes indexing much faster; it also runs on CPU.

```bash
python -m venv .venv
.venv\Scripts\activate          # Windows  (macOS/Linux: source .venv/bin/activate)
pip install -r requirements.txt
python -m uvicorn app.main:app --port 8765
```

Open http://127.0.0.1:8765. On first run the model is downloaded once from Hugging Face
(or place it in `models/embeddinggemma-2/` yourself); after that File Mind works with no internet connection.

On Windows you can also double-click `run.bat`. To get the desktop window instead of a browser tab,
`pip install pywebview` and run `python filemind.py`.

To build the portable zip yourself, see `packaging/build_windows.py`; GitHub Actions builds it automatically
for every `v*` tag.

## Licence

The EmbeddingGemma 2 weights are Apache-2.0. The Windows build bundles [FFmpeg](https://ffmpeg.org) (LGPL/GPL).
Interface fonts: Inter and Instrument Serif (SIL OFL).
