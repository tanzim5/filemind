# File Mind roadmap: fast, native, Windows-only

Goal: install, double-click, it runs on any PC. A Raycast/Spotlight-style launcher (Ctrl+Alt+Space) where you
type or *talk* ("the PDF with the blue chart about Q3 sales") and the right files appear instantly.

## Architecture decision

| Layer | Now | Target | Why |
|---|---|---|---|
| UI | WinUI 3 (C#) | WinUI 3 (C#) | Native, fast, already built |
| Launcher | none | WinUI borderless window + Win32 `RegisterHotKey` | Opens in <100 ms (pre-created, hidden) |
| Speech-to-text | audio clip embedded as the query (poor) | **Windows speech recognizer, live words as you talk** → text query; local Whisper as fallback | Smooth, streaming, no download |
| Model runtime | Python + PyTorch (separate process) | **ONNX Runtime in C#**, DirectML (NVIDIA/AMD/Intel GPU), CPU fallback; CUDA/TensorRT provider for max NVIDIA speed | No Python, no 2.8 GB download, AMD/Intel GPUs work, ARM64 works |
| Vector search | Python/numpy | C# in-memory (SIMD dot product) | <1 ms for 100k files |
| File scanning | Rust scanner (`core/`) | keep | Already fast |
| Media decode | ffmpeg.exe | keep | Not Python |

Language choice: **C# for everything at runtime** (one process, one language, ships as one folder; .NET AOT-ready
with SIMD). Python survives only as an offline developer tool to convert the model to ONNX.
Rust stays only for the scanner.

## Hardware

| PC | v0.6 (Python engine) | v0.7 (ONNX) |
|---|---|---|
| NVIDIA | CUDA PyTorch | CUDA EP (TensorRT optional), fp16 |
| AMD / Intel GPU | CPU | DirectML, fp16 |
| CPU only (x64) | CPU PyTorch | ONNX CPU, int8 |
| Windows on ARM | not supported | ONNX CPU (ARM64 build) / QNN NPU later |

## Phases

### v0.6 — ship what exists, add the launcher (now)
1. Native app runs the bundled `engine/FileMind.exe`; Auto/CPU/NVIDIA in Settings. ✅ in progress
2. **Quick Search launcher**: Ctrl+Alt+Space, type-ahead results, ↑/↓/Enter/Ctrl+Enter/Esc, tray-resident.
3. **Talk to search**: hold the mic (or Alt+V), live transcript while speaking, searches when you stop.
4. Indexing status with device, speed and ETA.
5. README + release one `FileMind-Windows.zip`.

### v0.7 — remove Python
1. Spike: export EmbeddingGemma 2 text + image encoders to ONNX; require cosine ≥ 0.99 vs PyTorch so
   existing libraries need no re-index; benchmark CPU / DirectML / CUDA.
2. Query embedding in C# (search never touches Python).
3. Indexing in C# (images → video frames via ffmpeg → audio).
4. Delete the Python engine; release is just the native app + model download (~1.5 GB, int8 ~0.4 GB on CPU).

### v0.8 — Raycast-grade
- "Ask" mode: describe a file conversationally, refine ("no, the newer one") using previous results as context.
- Actions on results: copy path, copy file, open with…, drag out, preview (Space).
- Recents and pinned searches; filters by voice ("only videos from last week").

## Quality bar
- Every commit: Python tests, JS tests, `dotnet build`. Release: install on a clean CPU-only PC and an NVIDIA PC.
- Launcher open < 100 ms, results < 200 ms after typing stops, first word on screen < 300 ms after speaking.
