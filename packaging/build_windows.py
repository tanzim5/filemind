"""Build the portable Windows app: dist/FileMind/FileMind.exe, zipped as dist/FileMind-Windows-portable.zip.

Run inside a venv that has the CPU build of PyTorch (keeps the zip under GitHub's 2 GB limit):
    pip install torch torchvision --index-url https://download.pytorch.org/whl/cpu
    pip install -r requirements.txt pywebview pyinstaller
    python packaging/build_windows.py --ffmpeg C:/path/to/folder/with/ffmpeg.exe+ffprobe.exe

The model is not bundled; FileMind.exe downloads it on first launch into a models/ folder beside itself."""
import argparse
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "dist"

COLLECT_ALL = ["transformers", "sentence_transformers", "tokenizers", "safetensors", "huggingface_hub",
               "accelerate", "librosa", "soundfile", "uvicorn", "webview"]
METADATA = ["torch", "numpy", "tqdm", "regex", "requests", "packaging", "filelock", "pyyaml", "pillow", "scipy",
            "scikit-learn"]
EXCLUDE = ["tkinter", "matplotlib", "IPython", "jupyter", "notebook", "pytest",
           "tensorboard", "triton"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ffmpeg", required=True, help="folder containing ffmpeg.exe and ffprobe.exe")
    a = ap.parse_args()
    ff = Path(a.ffmpeg)
    for exe in ("ffmpeg.exe", "ffprobe.exe"):
        if not (ff / exe).exists():
            sys.exit(f"missing {ff / exe}")

    cmd = [sys.executable, "-m", "PyInstaller", "--noconfirm", "--onedir", "--windowed",
           "--name", "FileMind", "--distpath", str(DIST), "--workpath", str(ROOT / "build"),
           "--specpath", str(ROOT / "build"), "--icon", str(ROOT / "packaging" / "filemind.ico"),
           "--add-data", f"{ROOT / 'app' / 'static'};app/static",
           "--add-binary", f"{ff / 'ffmpeg.exe'};ffmpeg", "--add-binary", f"{ff / 'ffprobe.exe'};ffmpeg",
           "--hidden-import", "app.main", "--collect-submodules", "app"]
    cmd += [x for m in COLLECT_ALL for x in ("--collect-all", m)]
    cmd += [x for m in METADATA for x in ("--copy-metadata", m)]
    cmd += [x for m in EXCLUDE for x in ("--exclude-module", m)]
    cmd.append(str(ROOT / "filemind.py"))
    subprocess.run(cmd, check=True, cwd=ROOT)

    app = DIST / "FileMind"
    for name in ("README.md",):
        shutil.copy(ROOT / name, app / name)
    (app / "LICENSES.txt").write_text(
        "File Mind bundles:\n"
        "- FFmpeg (ffmpeg.exe, ffprobe.exe), https://ffmpeg.org — licensed under the GNU LGPL/GPL (run `ffmpeg -L` for this build); source at https://ffmpeg.org/download.html\n"
        "- Python and the open-source packages listed in the File Mind repository (PyTorch BSD, Transformers Apache-2.0, ...)\n"
        "The EmbeddingGemma 2 model, downloaded on first run, is Apache-2.0.\n", encoding="utf-8")
    zipped = shutil.make_archive(str(DIST / "FileMind-Windows-portable"), "zip", DIST, "FileMind")
    print(f"\n{zipped}  {Path(zipped).stat().st_size / 2**20:.0f} MB")


if __name__ == "__main__":
    main()
