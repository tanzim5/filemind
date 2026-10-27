# File Mind 2.0 — Phase 1: architecture audit

Audited from the repository at `main` (159e259). Nothing was changed to produce this report.

## 1. What exists today

| Question | Finding |
|---|---|
| Frontend | Plain HTML/CSS/JS in `app/static/` (no framework, no bundler). Fonts are bundled locally. A second, native **WinUI 3** front end lives in `native/` (untracked, in progress). |
| Desktop shell | `pywebview` window around a local web server (`filemind.py`); Windows tray/background mode. Not Electron, not Tauri. |
| Backend | Python **FastAPI + uvicorn** (`app/main.py`, 51 routes, 1,177 lines). About 3,800 lines of Python in total. |
| Front end ↔ back end | HTTP on `127.0.0.1:<port>`. The WinUI shell spawns the same engine as a sidecar and talks to the same API. |
| Embedding model | One model for everything: **EmbeddingGemma 2** (`google/embeddinggemma-2`, local weights in `models/`, 1.5 GB), through sentence-transformers/PyTorch (CUDA if it works, else CPU) or MLX on Apple Silicon. Text, image, audio and video go into **one shared vector space**. |
| Database | SQLite (`data/index.db`, 214 MB). Tables: `roots`, `files` (vector stored as a float32 BLOB on the row), `projects`, `project_files`, `proposed_names`, `commits`, `commit_ops`. No schema-version table, no FTS. |
| Vector search | **Brute force**: the whole matrix is loaded into numpy and scanned per query (`indexer.matrix()`). Fine to roughly 100k files; no ANN index. |
| Thumbnails | Pillow / ffmpeg into `DATA_DIR`. Video: sampled frames via ffmpeg (`extract.py`). |
| Audio / video | ffmpeg (CLI) + PyAV; audio clip capped at 120 s; `faster-whisper` (base, CPU int8) for transcripts; `moments.py` finds moments inside videos. |
| Documents | Only **PDF and DOCX** (`pymupdf`, `python-docx`). |
| Duplicates | `duplicates.py`: hashing + perceptual comparison with Pillow. |
| File watching | **None.** Indexing runs on demand (`indexer.start`); there is no change monitoring. |
| Packaging | `packaging/build_windows.py` (frozen build, NVIDIA runtime split out via `runtime-nvidia.json`), GitHub release workflow. |
| Tests | 22 Python tests (autostart, discover, file actions, library tools, transcribe, …) and 3 JS tests. None cover indexing, migrations, search ranking or worker failure. |

## 2. Findings that change the plan

1. **The app is already local.** Inference runs on the user's machine and no query leaves it. The brief reads as if this were a cloud-embedding app; it is not. The remaining offline gaps are small and specific:
   - `app/fetch.py` downloads the model from `huggingface.co` the first time (also `app/transcribe.py` for Whisper). This violates "no downloading models at runtime".
   - `app/static/index.html:84` has an outbound link to x.com (user-click only; it should not be an in-app link in an offline build).
   - `HF_HUB_OFFLINE=1` is already forced, which is good.
2. **EmbeddingGemma 2 is a single multimodal model.** The brief proposes replacing it with SigLIP 2 + BGE-M3 + CLAP, with separate indexes and rank fusion. That is a real trade-off, not a free upgrade: it needs three models, three vector indexes, a fusion layer and re-embedding of the whole library. It should be *benchmarked against the current model on our own library first*, and added as optional providers behind an interface, not swapped in.
3. **No file watcher, no schema versioning, no ANN index, no FTS** — these are the highest-value gaps and none of them requires Rust.
4. **Existing index must survive.** Vectors live inside `files.vec`; any new design must migrate from that table in place and keep the current vectors usable until re-indexing is validated.

## 3. Keep / optimise / migrate / replace

| Component | Decision | Why |
|---|---|---|
| Web UI (`app/static`) and its look | **Keep**, extend (Documents category, search-by-image/audio, indexing status) | The brief asks for this explicitly. |
| WinUI 3 shell (`native/`) | **Keep**, continue as a second front end | Same engine API. |
| FastAPI routes | **Keep** as the stable contract during any migration | Lets Rust replace internals without touching either UI. |
| SQLite | **Keep**; add `schema_version`, migrations, FTS5, WAL, backup | Already the right store. |
| Vector search | **Optimise** (usearch or hnswlib behind an interface) | Brute force is fine now, not at 100k+. Benchmark first. |
| Embedding provider | **Wrap** in a `Provider` interface; current model becomes provider #1 | Enables optional SigLIP 2 / BGE-M3 / CLAP later. |
| Model download (`fetch.py`) | **Replace** with an offline "import model pack" flow; keep download only in packaging scripts | Meets the offline rule. |
| Doc extraction | **Extend** (TXT/MD/HTML/RTF/XLSX/CSV/PPTX, page/slide references, structure-aware chunks) | Currently only PDF/DOCX, no chunk references. |
| Scanning, watching, job queue, thumbnail cache | **Migrate to Rust** (stage B), behind the existing API | These are the parts where Rust gives measurable wins. |
| Inference | **Stay in Python** until ONNX Runtime parity is proven (stage D) | The model is PyTorch/MLX today. |

## 4. Rust feasibility (short)

- **Good fit now:** directory traversal, hashing/fingerprints, `ReadDirectoryChangesW` watching, job queue with persistence and cancellation, SQLite access (`rusqlite`), thumbnail cache, Windows shell integration. The WinUI shell already owns process management.
- **Not yet:** inference. EmbeddingGemma 2 has no validated ONNX export in this repo; moving it is a research task with a quality gate (recall on our own library must match).
- **Recommended shape:** a Rust `filemind-core` service that owns the scan/watch/job/DB layer and a local Python worker for inference over a named pipe (not an HTTP port).

## 5. Proposed increments (each one verifiable on its own)

1. **Benchmark baseline** (traversal, thumbnails, query latency, startup, memory) on the existing library. Needed before any rewrite is justified.
2. **Offline hardening**: remove runtime downloads, add an offline model-pack import, add a test that runs the engine with sockets blocked and fails on any outbound connection.
3. **Schema versioning + migrations + FTS5 + backup** (non-destructive on the 214 MB index).
4. **File watcher** (Windows) with debounce and periodic reconcile; handle unplugged drives without marking files deleted.
5. **Universal documents**: more formats, structure-aware chunks with page/slide references, snippet display, open-at-page.
6. **Hybrid search**: filename + FTS + vector with Reciprocal Rank Fusion, search-by-image/audio.
7. **Video/audio**: scene segmentation and timestamp results; waveform UI.
8. **Provider interface**, then benchmark SigLIP 2 / BGE-M3 / CLAP against EmbeddingGemma 2 on English and Bengali queries.
9. **Rust core** (stage B), then Windows integration (global shortcut, Explorer menu), then packaging.

## 6. Decisions needed before increment 2 onwards

- Is EmbeddingGemma 2 staying the default embedding model (recommended until benchmarks say otherwise)?
- May the WinUI shell become the primary Windows front end, or must the web UI stay primary?
- Is there a sample library for the benchmarks, and a small set of Bengali test queries?
