# Handoff: v0.6.0 (single Windows release + Spotlight search)

Branch: `feature/spotlight-single-windows`. Each task below is self-contained: do one, build, test, commit.
Checks before every commit:

```
.venv\Scripts\python -m unittest discover -s tests -q
node --test tests/*.cjs
dotnet build native/FileMind.WinUI -c Debug -p:Platform=x64
```

## Done
- macOS removed (`build_macos.py`, `app/mlx_backend.py`, mlx requirements).
- One engine: `app/runtime.py` detects NVIDIA (driver >= 12080, compute >= 7.0) on first run and downloads CUDA
  PyTorch (~2.8 GB), otherwise CPU PyTorch (~0.12 GB). AMD/Intel run on CPU. Override with env
  `FILEMIND_ACCEL=cpu|cuda`. `/api/model` reports `accel`, `accel_reason`, `runtime_bytes`, `device`.
- `packaging/build_windows.py` builds `dist/engine/FileMind/FileMind.exe` (no torch inside).
- `.github/workflows/release.yml` publishes one file, `FileMind-Windows.zip` = native app publish + `engine/`.
- `packaging/validate_build.py` checks that zip.
- CPU indexing stall fix (140 image tokens, 4 video frames, batch 1, rate/ETA, query LRU cache) — needs a
  real CPU-only run to confirm it no longer stalls.

## Task 1 — native app uses the bundled engine
Files: `native/FileMind.Core/EngineHost.cs` (EngineLocator), `native/FileMind.WinUI/Services/AppRuntime.cs`.
- Add `EngineLocator.FindBundled(string appDir)`: returns a launch for `Path.Combine(appDir, "engine", "FileMind.exe")`
  if it exists, with env `FILEMIND_DATA`, `FILEMIND_MODELS`, `FILEMIND_RUNTIME` from `AppPaths` (same as
  `ForInstalled`) plus `FILEMIND_ACCEL` when the user picked `cpu` or `cuda` (omit for `auto`).
- `ResolveLaunch()` order: source checkout (dev, unless `FILEMIND_ENGINE=installed`) → bundled → old installed edition.
- Settings value `Settings.Engine`: `"auto" | "cpu" | "nvidia"`; map `nvidia` → `FILEMIND_ACCEL=cuda`.
- Changing the setting restarts the engine.

## Task 2 — simplify setup
Files: `native/FileMind.WinUI/Pages/SetupPage.xaml(.cs)` (edition UI ~lines 51–72), `GpuProbe.cs`,
`EngineInstaller.cs`, `engine-manifest.json`, `Models.cs:80`.
- When the bundled engine exists, skip the edition download step entirely. Show one line from `/api/model`:
  "Runs on: GPU (NVIDIA …)" or "Runs on: CPU — <accel_reason>", and the first-run download size
  (`runtime_bytes` + ~1.5 GB model) with the existing progress UI.
- Keep a small "Run on: Auto / CPU / NVIDIA" selector in Settings, not on the setup page.
- Point `engine-manifest.json` at v0.6.0 or remove the editions if nothing uses them anymore.
- Bump `<Version>` in `FileMind.WinUI.csproj` to 0.6.0.

## Task 3 — Spotlight quick search window
New files: `native/FileMind.WinUI/QuickSearch/QuickSearchWindow.xaml(.cs)`, `Services/GlobalHotkey.cs`.
- `GlobalHotkey`: Win32 `RegisterHotKey` on a message-only HWND (subclass via `SetWindowSubclass` or a hidden
  window); default **Ctrl+Alt+Space**, configurable in Settings; show a clear error if the combo is taken.
- Window: borderless, always-on-top, centred on the active monitor's upper third, ~720 px wide, Mica/acrylic,
  square-ish corners (4 px max; no big rounded "iOS" look), no dead padding.
- Behaviour: opens focused in the text box; typing debounces 150 ms → `GET /api/search`; results list with
  thumbnail, name, folder; ↑/↓ select, Enter opens, Ctrl+Enter reveals in Explorer, Esc or focus loss hides.
  Reuse the existing ApiClient search call and thumbnail URLs used by the main window.
- Mic button (and **Alt+V**): record with `Services/VoiceRecorder.cs`, POST to `/api/voice`, put the transcript
  in the box and search. Show a live recording indicator; stop on second press or 8 s.
- App must keep running in the tray when the main window closes so the hotkey works (respect the existing
  "keep running in background" preference).
- Must open in < 100 ms: create the window once at startup, hide/show it instead of recreating.

## Task 4 — indexing status in native UI
Show device (CPU/GPU), files/min and ETA from the engine's indexing status in the native library/status area.

## Task 5 — README
Windows-only badge; one download row (`FileMind-Windows.zip`); delete the Mac section (~lines 87–92) and the
MLX paragraph (~144); build section (~148) → `build_windows.py` + `dotnet publish`; note AMD runs on CPU;
mention Ctrl+Alt+Space and voice search.

## Task 6 — release
Merge PR, `git tag v0.6.0 && git push --tags`. The workflow builds and publishes the single zip.
