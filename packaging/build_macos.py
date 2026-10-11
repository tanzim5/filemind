"""Build the macOS app (Apple Silicon): dist/FileMind.app  ->  dist/FileMind-macOS-arm64.zip

Run on an Apple Silicon Mac (the GitHub workflow does, on macos-14) inside a venv with File Mind's requirements:
    pip install torch==2.11.0 torchvision==0.26.0 transformers==5.19.0 sentence-transformers==6.1.0
    pip install -r requirements.txt pyinstaller
    python packaging/build_macos.py --ffmpeg /path/to/folder/with/ffmpeg+ffprobe

The app runs the model on the GPU with MLX. The model (1.1 GB) is not bundled: FileMind.app downloads it on first
launch into ~/Library/Application Support/File Mind/models. The app is not code-signed (no Apple developer
account), so macOS asks for confirmation the first time: see README."""
import argparse
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "dist"

COLLECT_ALL = ["transformers", "sentence_transformers", "tokenizers", "safetensors", "huggingface_hub",
               "accelerate", "librosa", "soundfile", "uvicorn", "webview", "mlx", "mlx_vlm", "mlx_audio"]
METADATA = ["numpy", "tqdm", "regex", "requests", "packaging", "filelock", "pyyaml", "pillow", "scipy",
            "scikit-learn", "torch", "mlx", "mlx-vlm"]
HIDDEN = ["objc", "Foundation", "AppKit", "WebKit", "PyObjCTools.AppHelper", "typing_extensions"]
EXCLUDE = ["tkinter", "matplotlib", "IPython", "jupyter", "notebook", "pytest", "tensorboard", "triton", "gradio"]


def make_icns(out: Path):
    from PIL import Image
    im = Image.open(ROOT / "packaging" / "filemind.png").convert("RGBA").resize((512, 512), Image.LANCZOS)
    im.save(out, format="ICNS")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ffmpeg", required=True, help="folder containing ffmpeg and ffprobe (macOS arm64 builds)")
    a = ap.parse_args()
    ff = Path(a.ffmpeg).resolve()
    for exe in ("ffmpeg", "ffprobe"):
        if not (ff / exe).exists():
            sys.exit(f"missing {ff / exe}")

    work = ROOT / "build" / "macos"
    work.mkdir(parents=True, exist_ok=True)
    icns = work / "filemind.icns"
    make_icns(icns)

    cmd = [sys.executable, "-m", "PyInstaller", "--noconfirm", "--onedir", "--windowed",
           "--name", "FileMind", "--distpath", str(DIST / "macos"), "--workpath", str(work),
           "--specpath", str(work), "--icon", str(icns), "--osx-bundle-identifier", "app.filemind.FileMind",
           "--add-data", f"{ROOT / 'app' / 'static'}:app/static",
           "--add-binary", f"{ff / 'ffmpeg'}:ffmpeg", "--add-binary", f"{ff / 'ffprobe'}:ffmpeg",
           "--hidden-import", "app.main", "--collect-submodules", "app"]
    cmd += [x for m in COLLECT_ALL for x in ("--collect-all", m)]
    cmd += [x for m in METADATA for x in ("--copy-metadata", m)]
    cmd += [x for m in HIDDEN for x in ("--hidden-import", m)]
    cmd += [x for m in EXCLUDE for x in ("--exclude-module", m)]
    cmd.append(str(ROOT / "filemind.py"))
    shutil.rmtree(DIST / "macos", ignore_errors=True)
    subprocess.run(cmd, check=True, cwd=ROOT)

    app = DIST / "macos" / "FileMind.app"
    # MLX looks for its Metal kernels (mlx.metallib) beside the running executable; PyInstaller buries the copy it
    # collects, so put one where MLX will find it, then re-sign the bundle (an edited bundle fails its own seal).
    import importlib.util
    metal = list(Path(importlib.util.find_spec("mlx.core").origin).parent.rglob("mlx.metallib"))
    if not metal:
        sys.exit("mlx.metallib not found: this must be an Apple Silicon build of mlx")
    for dest in (app / "Contents" / "MacOS", app / "Contents" / "Resources"):
        shutil.copy(metal[0], dest / "mlx.metallib")
    subprocess.run(["codesign", "--force", "--deep", "--sign", "-", str(app)], check=True)
    subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)], check=True)
    zipped = DIST / "FileMind-macOS-arm64.zip"
    zipped.unlink(missing_ok=True)
    # ditto keeps the symlinks and permissions inside the bundle (shutil/zip would break the app)
    subprocess.run(["ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", str(app), str(zipped)], check=True)
    size = zipped.stat().st_size
    print(f"\n{zipped}  {size / 2**20:.0f} MB")
    if size >= 2 * 2**30:
        sys.exit("zip is over GitHub's 2 GB per-file limit")


if __name__ == "__main__":
    main()
