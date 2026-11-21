<p align="center"><img src="docs/banner.png" alt="File Mind — search your own photos, videos and sounds by what's in them, fully offline" width="100%"></p>

<h3 align="center">Find any photo, video or sound by describing it.<br>Runs entirely on your computer. No account, no cloud, no uploads.</h3>

<p align="center">
  <a href="https://github.com/tanzim5/filemind/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/tanzim5/filemind?style=for-the-badge&color=ff5a1f&labelColor=111114"></a>
  <img alt="Platforms" src="https://img.shields.io/badge/Windows-111114?style=for-the-badge">
  <img alt="Offline" src="https://img.shields.io/badge/100%25-offline-111114?style=for-the-badge">
  <a href="https://github.com/tanzim5/filemind/stargazers"><img alt="Stars" src="https://img.shields.io/github/stars/tanzim5/filemind?style=for-the-badge&color=ff5a1f&labelColor=111114"></a>
</p>

<p align="center">
  <a href="https://github.com/tanzim5/filemind/releases/latest"><b>⬇ Download</b></a> &nbsp;·&nbsp;
  <a href="#features">Features</a> &nbsp;·&nbsp;
  <a href="#get-started">Get started</a> &nbsp;·&nbsp;
  <a href="#privacy">Privacy</a> &nbsp;·&nbsp;
  <a href="#build-and-run-from-source">Build from source</a> &nbsp;·&nbsp;
  <a href="https://x.com/tanzim31">@tanzim31</a>
</p>


File Mind reads a folder — Downloads, Pictures, a project drive — and understands **what is in** your photos, videos
and sounds using Google's [EmbeddingGemma 2](https://huggingface.co/google/embeddinggemma-2), a multimodal AI model
that runs on your own machine. Type what you remember ("red logo on black", "a man running at night") and it finds it.

<p align="center"><img src="docs/library.png" alt="The Library: a foldable sidebar with spaces, sources and refine filters beside a wall of photos and videos" width="100%"></p>
<p align="center"><img src="docs/discover.png" alt="Discover: themes found in your own library" width="100%"></p>

## Features

### 🔎 Find anything
- **Search in plain language.** Stack several searches as tags to narrow down.
- **Search by voice.** Press the microphone and speak. A Whisper model on this PC turns your voice into text (it downloads once) and stops listening when you pause. Settings can switch to Windows speech recognition instead.
- **Search with a reference.** Open any image, video or audio file and choose *Search with this file*, optionally adding words.
- **Find by details.** "4K videos of motorcycles", "vertical clips under 30 seconds", "images larger than 20 MB": media details become exact filters, the rest searches by meaning.
- **Find moments.** Search inside videos and sounds and open the result at the matching time.
- **More like this.** Keep following similar files, endlessly.
- **Automatic tags and colours** on every file. Filter by colour, type, source tool, date or size.
- **Quick search from anywhere.** Press **Ctrl+Shift+Space** for a launcher with results, a preview and file details.
- **Favourites and your own tags.** Star files, make coloured tags, and filter by them.
- **A sidebar you can fold.** Click the heading of Tags, Spaces, Sources, Refine or any filter group to fold it away; File Mind remembers. Spaces shows five at first, with the rest one click away. Settings > Sidebar order lets you put the sections in any order you like.

### 🧭 Explore your library
- **Discover** visual themes (architecture, products, illustrations…) or start from a suggested question.
- **Spaces.** Group files into collections. *Live* spaces keep matching files added later; *snapshots* stay fixed. **Auto-group** suggests named collections for you to review.
- **Review duplicates**: byte-identical copies and visually similar matches. Nothing is deleted for you.

### 🖱️ Work with your files
- **Right-click a tile** in the Library for a menu: open, show in folder, copy the file or its path, favourite, find similar, add to a space, delete. Over several ticked files it acts on all of them. Right-click in the viewer for copy, show in Explorer, export and previous or next file.
- **Look closely.** Double-click a picture to zoom in on that spot (again to zoom out), drag to move around while zoomed in, and use the wheel with **Ctrl** to zoom. In full screen the wheel goes to the previous or next picture.
- **Pick several similar files.** In the viewer's *More like this*, Ctrl-click or right-click to select several, then copy, favourite, add to a space or delete them together.
- **Drag originals into other apps**: Photoshop, After Effects, Explorer and more. Drag a tile, or select several and drag them together.
- **Copy a video frame** as a PNG straight from the viewer.
- **Export.** Save part of a video or sound as its own clip (mark the start with **I** and the end with **O**), turn files into MP4, WebM, GIF, MP3, WAV, JPG, PNG or WebP, shrink them to fit under a size, pull the sound out of a video, or make a contact sheet. Exports are always new files; the original is never changed. Select several files in the Library to export them together.
- **Audio playback and transcripts**: play and scrub a waveform right on its tile, then transcribe speech on your device in the detected language.
- **Previews for documents too**: PDFs show their first page, text, Markdown and Word files show a text card, and each opens in a viewer.
- **File actions**: copy or move the original, or send it to the Recycle Bin. Copy and move never overwrite.
- **Preview renames** before anything on disk changes; commits can be undone.

### 🎨 Make it yours
- Four themes (Midnight, Graphite, Black and Light), six accent colours, a transparency slider, four gallery layouts (masonry, rows, contact sheet, storyboard) and tile size.
- File Mind stays in the notification area after you close the window so the quick-search shortcut keeps working. You can turn that off in Settings.


## Get started

Nothing to install: Python, ffmpeg and everything else are inside the download. The first launch fetches the AI model once; after that File Mind works fully offline.

| Your computer | Download | First launch also downloads |
|---|---|---|
| **Windows 10/11** (64-bit) | `FileMind-Windows.zip`: one app for every PC; on first run it picks CPU or NVIDIA PyTorch automatically | AI model (~1.5 GB), plus GPU support (~2.8 GB) on NVIDIA cards |

**Quick search anywhere:** press **Ctrl+Shift+Space** for the Raycast-style launcher. Change the shortcut in Settings.

**Keyboard:** in the viewer, **Space** plays or pauses, **Left/Right** skip 5 seconds (or step between pictures), **Ctrl+Left/Right** go to the previous or next file, **Up/Down** change the volume, **M** mutes, **[** and **]** change the speed, **,** and **.** step one frame, **I** and **O** mark a clip, **E** exports, **F** fills the screen (then the wheel changes pictures), double-click zooms, drag pans and **Esc** goes back. In the Library, right-click a tile for its menu, **Ctrl+C** copies the selected files, **Ctrl+Shift+C** copies their paths and **Enter** opens the selected file.

➡ Get them from the **[latest release](https://github.com/tanzim5/filemind/releases/latest)**.

<details>
<summary><b>Windows steps</b></summary>

1. Unzip anywhere (Desktop, a USB stick…) and double-click **`FileMind.exe`**.
2. Windows may show "Windows protected your PC" because the app isn't code-signed: click **More info → Run anyway**.
3. While it reads your files, the banner shows what it runs on (for example *on NVIDIA GeForce RTX 3060 (CUDA)* or *on CPU*). To watch the card work, open Task Manager → Performance → your NVIDIA GPU and switch a graph from *3D* to *Cuda*.

**Requirements:** Windows 10/11 (64-bit), about 8 GB RAM. No graphics card needed. Searching is instant; reading a new library is a one-time job of a few seconds per photo (more for videos), you can keep using the app meanwhile, and it resumes where it left off.

**Uninstall:** quit File Mind from its notification-area icon and delete the folder. If your NVIDIA card can't be used, it quietly runs on the CPU.

<p align="center"><img src="docs/setup.png" alt="First launch: the model downloads once" width="70%"></p>
</details>

## Privacy

- Everything runs on your computer. Your files, the index and every search stay there.
- File Mind's own network use is downloading models from Hugging Face (and PyTorch on first run). Voice search is local unless you choose Windows speech recognition in Settings: see *Search by voice* above. The speech model (~148 MB) downloads only if you choose **Transcribe audio**.
- Indexing only reads your folders. Staged renames and moves happen when you press commit and can be undone. Copy, Move and Delete act immediately; deleted files go to the Recycle Bin.
- The index lives in a `data` folder next to the app. Forgetting a folder removes its files, tags and cached thumbnails from the index (never the files themselves). Delete `data` to forget everything.

## Build and run from source

File Mind is a native Windows app (WinUI 3, .NET 9) that starts a local Python engine (FastAPI) for the AI work.

**You need:** Windows 10/11, the .NET 9 SDK, Python 3.12, and **ffmpeg** (`winget install Gyan.FFmpeg`) for videos, sounds and voice search.

```bash
git clone https://github.com/tanzim5/filemind.git
cd filemind
python -m venv .venv
.venv\Scripts\python.exe -m pip install -r requirements.txt
# NVIDIA card: replace the CPU PyTorch with the CUDA build
.venv\Scripts\python.exe -m pip install --force-reinstall --no-deps torch==2.11.0+cu128 torchvision==0.26.0+cu128 --index-url https://download.pytorch.org/whl/cu128
cd native
dotnet build FileMind.WinUI -c Release -p:Platform=x64
```

Run `native\FileMind.WinUIind\Release
et9.0-windows10.0.22621.0\win-x64\FileMind.exe`. Next to a source checkout it starts `filemind.py` from `.venv` by itself. The first run downloads the model once.

Tests: `.venv\Scripts\python.exe -m unittest discover -s tests` and `dotnet test native/FileMind.Core.Tests`.

<details>
<summary><b>Audio transcription and other details</b></summary>

**Audio transcription.** EmbeddingGemma 2 understands audio for search without turning speech into text. The optional **Transcribe audio** action uses a separate multilingual speech model for a copyable, timestamped transcript. Its first use downloads the model; later runs are offline. Music and sound effects may have no usable transcript.

Set `FILEMIND_MODELS` to put the model elsewhere and `FILEMIND_DATA` for the index. You can also place model files in `models/embeddinggemma-2/` yourself.

**Speed.** An NVIDIA GPU is used automatically when it works (Settings → Engine shows what runs where). Preparing files (decoding, thumbnails, video frames) always uses the CPU; Settings → Library → Indexing speed trades speed for a quieter PC.

**Building the app.** See `packaging/build_windows.py`. GitHub Actions builds the Windows download for every `v*` tag, checks the archive's contents and smoke-tests the engine. You can also run the **Release builds** workflow manually; to publish from a manual run, give it an existing `v*` tag.
</details>

## Licence

The EmbeddingGemma 2 weights are Google's, under the licence on their Hugging Face model card. The Windows app bundles [FFmpeg](https://ffmpeg.org) (LGPL/GPL). Interface fonts: Geist, Inter and Instrument Serif (SIL OFL). Sidebar icons: [Phosphor Icons](https://phosphoricons.com) (MIT).

---

<p align="center">Made with care by <b>Tanzim</b> — questions, ideas, or something you found with it?<br>
<a href="https://x.com/tanzim31"><b>Say hi on X → @tanzim31</b></a></p>
