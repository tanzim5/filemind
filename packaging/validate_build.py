"""Fail a release job if its download is missing the files needed to launch.

There is one Windows download: the native app with the engine inside it as engine/.
    python packaging/validate_build.py            # dist/FileMind-Windows.zip
    python packaging/validate_build.py engine     # dist/FileMind-Engine.zip (engine alone)"""

import argparse
from pathlib import Path
from zipfile import ZipFile

# archive, folder inside the zip that holds the engine, files that must be there (relative to the zip root)
EXPECTED = {
    "windows": ("FileMind-Windows.zip", "FileMind/engine/",
                ["FileMind/FileMind.exe"]),
    "engine": ("FileMind-Engine.zip", "FileMind/", []),
}
ENGINE_FILES = ["FileMind.exe", "_internal/runtime.json"]


def validate(variant: str, dist: Path) -> None:
    archive, engine, extra = EXPECTED[variant]
    path = dist / archive
    with ZipFile(path) as package:
        names = set(package.namelist())
        required = set(extra) | {engine + f for f in ENGINE_FILES}
        for asset in ("index.html", "app.js", "style.css"):
            if not any(name.startswith(engine) and name.endswith("/app/static/" + asset) for name in names):
                raise ValueError(f"{archive}: missing {asset}")
        missing = required - names
        if missing:
            raise ValueError(f"{archive}: missing {', '.join(sorted(missing))}")
        if any(package.getinfo(name).file_size == 0 for name in required):
            raise ValueError(f"{archive}: an empty launcher or manifest")
    print(f"Validated {archive}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("variant", nargs="?", default="windows", choices=EXPECTED)
    parser.add_argument("--dist", type=Path, default=Path("dist"))
    args = parser.parse_args()
    validate(args.variant, args.dist)
