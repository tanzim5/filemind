# File Mind: instructions for coding agents

Local multimodal file-search app for Windows. Repo: tanzim5/filemind.

## Layout
- app/ : Python FastAPI engine (entry filemind.py; faster-whisper for voice)
- native/FileMind.WinUI : WinUI 3 app (.NET 9); native/FileMind.Core : shared library
- native/FileMind.Core.Tests : C# tests; tests/ : Python tests
- packaging/ + .github/workflows/release.yml : release build

## Commands
- Python tests: .venv\Scripts\python.exe -m unittest discover -s tests (pytest is not installed)
- Build: close FileMind.exe first, then cd native && dotnet build FileMind.WinUI -c Release -p:Platform=x64
- C# tests: dotnet test native/FileMind.Core.Tests
- Visual check: set env FILEMIND_DEV_PAGE (library/discover/spaces/duplicates) and capture the window; never click or take over the user's screen.

## Git workflow
- git pull first. Branch from main (feature/... or fix/...). Never commit to main.
- Build and run tests before committing; report anything not tested.
- Push, open a PR with gh pr create, and wait for the user to say "merge".
- Release: bump <Version> in native/FileMind.WinUI/FileMind.WinUI.csproj, merge, tag vX.Y.Z, push the tag; the workflow publishes FileMind-Windows.zip.

## Style
- Short replies, modest corner radii, no unrequested features.