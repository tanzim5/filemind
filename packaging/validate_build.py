"""Fail a release job if its download is missing the files needed to launch."""

import argparse
from pathlib import Path
from zipfile import ZipFile


EXPECTED = {
    "windows": ("FileMind-Windows-portable.zip", "FileMind/", "FileMind.exe"),
    "nvidia": ("FileMind-Windows-NVIDIA.zip", "FileMind-NVIDIA/", "FileMind.exe"),
    "macos": ("FileMind-macOS-arm64.zip", "FileMind.app/", "Contents/MacOS/FileMind"),
}


def validate(variant: str, dist: Path) -> None:
    archive, root, launcher = EXPECTED[variant]
    path = dist / archive
    with ZipFile(path) as package:
        names = set(package.namelist())
        required = {root + launcher}
        for asset in ("index.html", "app.js", "style.css"):
            if not any(name.startswith(root) and name.endswith("/app/static/" + asset) for name in names):
                raise ValueError(f"{archive}: missing {asset}")
        if variant == "nvidia":
            required.add(root + "_internal/runtime.json")
        missing = required - names
        if missing:
            raise ValueError(f"{archive}: missing {', '.join(sorted(missing))}")
        if any(package.getinfo(name).file_size == 0 for name in required):
            raise ValueError(f"{archive}: empty launcher or runtime manifest")
    print(f"Validated {archive}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("variant", choices=EXPECTED)
    parser.add_argument("--dist", type=Path, default=Path("dist"))
    args = parser.parse_args()
    validate(args.variant, args.dist)
