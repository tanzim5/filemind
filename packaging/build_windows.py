"""Build the File Mind engine for Windows: dist/engine/FileMind/FileMind.exe (+ dist/FileMind-Engine.zip).

One engine for every PC. It ships without PyTorch; on first run it checks the graphics card and downloads the
right PyTorch from pytorch.org, as listed in packaging/runtime-windows.json (see app/runtime.py):
    usable NVIDIA GPU  ->  PyTorch with CUDA (~2.8 GB, too big for a GitHub release file anyway)
    anything else      ->  the CPU build (~120 MB); AMD and Intel graphics run on the CPU too
The release workflow puts this folder inside the native app as engine/, so the release is a single zip.

Run inside a venv with the CPU build of PyTorch (it is left out of the build, but PyInstaller needs to see it):
    pip install torch==2.11.0 torchvision==0.26.0 --index-url https://download.pytorch.org/whl/cpu
    pip install -r packaging/requirements-windows.txt
    python packaging/build_windows.py --ffmpeg C:/path/to/folder/with/ffmpeg.exe+ffprobe.exe

The model is not bundled either; it is downloaded on first launch into a models/ folder."""
import argparse
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "dist"
OUT = DIST / "engine"

COLLECT_ALL = ["transformers", "sentence_transformers", "tokenizers", "safetensors", "huggingface_hub",
               "accelerate", "librosa", "soundfile", "uvicorn", "webview", "faster_whisper", "ctranslate2", "av", "onnxruntime"]
METADATA = ["numpy", "tqdm", "regex", "requests", "packaging", "filelock", "pyyaml", "pillow", "scipy",
            "scikit-learn"]
EXCLUDE = ["tkinter", "matplotlib", "IPython", "jupyter", "notebook", "pytest",
           "tensorboard", "triton", "torch", "torchvision", "torchaudio"]
# The downloaded PyTorch imports these pure-Python packages from the app, so the build must carry them.
TORCH_DEPS = ["sympy", "mpmath", "networkx", "jinja2", "markupsafe", "fsspec", "filelock", "setuptools"]


def _stdlib_stub(folder: Path) -> Path:
    """The downloaded PyTorch may import any standard-library module, but PyInstaller only bundles what it sees
    imported. Write a module that (never executed) imports the whole stdlib, plus the third-party modules
    PyTorch was seen using, so all of it gets bundled."""
    import importlib.util
    import keyword
    import pkgutil
    skip = ("test", "tests", "idlelib", "tkinter", "turtle", "turtledemo", "lib2to3", "ensurepip", "venv",
            "pydoc_data", "__phello__", "antigravity", "this")
    names = set()
    for top in sorted(sys.stdlib_module_names):
        if top in skip or top.startswith("_test") or importlib.util.find_spec(top) is None:
            continue
        names.add(top)
        spec = importlib.util.find_spec(top)
        if spec.submodule_search_locations:
            for m in pkgutil.walk_packages(spec.submodule_search_locations, top + ".", onerror=lambda _: None):
                if not set(m.name.split(".")) & set(skip):
                    names.add(m.name)
    # ...and every third-party module PyTorch was seen importing (packaging/scan_imports.py)
    lines = (ROOT / "packaging" / "nvidia-imports.txt").read_text().splitlines()
    names |= {s.strip() for s in lines if s.strip() and not s.startswith("#")}
    names = {n for n in names if n.isascii()
             and all(p.isidentifier() and not keyword.iskeyword(p) for p in n.split("."))}
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "_stdlib_all.py").write_text("if False:\n" + "".join(f"    import {n}\n" for n in sorted(names)))
    return folder


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ffmpeg", required=True, help="folder containing ffmpeg.exe and ffprobe.exe")
    ap.add_argument("--no-zip", action="store_true", help="skip dist/FileMind-Engine.zip")
    a = ap.parse_args()
    ff = Path(a.ffmpeg).resolve()
    for exe in ("ffmpeg.exe", "ffprobe.exe"):
        if not (ff / exe).exists():
            sys.exit(f"missing {ff / exe}")

    work = ROOT / "build" / "engine"
    cmd = [sys.executable, "-m", "PyInstaller", "--noconfirm", "--onedir", "--windowed",
           "--name", "FileMind", "--distpath", str(OUT), "--workpath", str(work),
           "--specpath", str(work), "--icon", str(ROOT / "packaging" / "filemind.ico"),
           "--add-data", f"{ROOT / 'app' / 'static'};app/static",
           "--add-binary", f"{ff / 'ffmpeg.exe'};ffmpeg", "--add-binary", f"{ff / 'ffprobe.exe'};ffmpeg",
           "--add-data", f"{ROOT / 'packaging' / 'runtime-windows.json'};.",
           "--hidden-import", "app.main", "--collect-submodules", "app",
           "--hidden-import", "typing_extensions", "--hidden-import", "_stdlib_all",
           "--paths", str(_stdlib_stub(work))]
    cmd += [x for m in COLLECT_ALL + TORCH_DEPS for x in ("--collect-all", m)]
    cmd += [x for m in METADATA for x in ("--copy-metadata", m)]
    cmd += [x for m in EXCLUDE for x in ("--exclude-module", m)]
    cmd.append(str(ROOT / "filemind.py"))
    shutil.rmtree(OUT, ignore_errors=True)
    subprocess.run(cmd, check=True, cwd=ROOT)

    app = OUT / "FileMind"
    (app / "_internal" / "runtime-windows.json").rename(app / "_internal" / "runtime.json")
    shutil.copy(ROOT / "README.md", app / "README.md")
    (app / "LICENSES.txt").write_text(
        "File Mind bundles:\n"
        "- FFmpeg (ffmpeg.exe, ffprobe.exe), https://ffmpeg.org — licensed under the GNU LGPL/GPL (run `ffmpeg -L` for this build); source at https://ffmpeg.org/download.html\n"
        "- Python and the open-source packages listed in the File Mind repository (Transformers Apache-2.0, ...)\n"
        "The EmbeddingGemma 2 model, downloaded on first run, is Apache-2.0.\n"
        "PyTorch, downloaded on first run from pytorch.org, is BSD-licensed; its CUDA build includes NVIDIA CUDA "
        "libraries under the NVIDIA CUDA EULA.\n", encoding="utf-8")
    if not a.no_zip:
        zipped = shutil.make_archive(str(DIST / "FileMind-Engine"), "zip", OUT, "FileMind")
        print(f"\n{zipped}  {Path(zipped).stat().st_size / 2**20:.0f} MB")


if __name__ == "__main__":
    main()
