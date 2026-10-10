"""Regenerate packaging/nvidia-imports.txt after upgrading PyTorch (the NVIDIA build bundles the listed modules).

    python packaging/scan_imports.py MODELS_DIR some.jpg some.mp4 some.wav

Embeds the three files and a query with the real model, then records every third-party module that got imported."""
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
models, image, video, audio = sys.argv[1:5]
os.environ["FILEMIND_MODELS"] = models
sys.path.insert(0, str(ROOT))
from app import embedder  # noqa: E402

for f, kind in ((image, "image"), (video, "video"), (audio, "audio")):
    print(f, embedder.embed_document(Path(f), kind, Path(f).name)[1])
embedder.embed_query("a house")

names = sorted(n for n, m in list(sys.modules.items())
               if "site-packages" in (getattr(m, "__file__", None) or "")
               and n.split(".")[0] not in ("torch", "torchvision", "torchgen"))
(ROOT / "packaging" / "nvidia-imports.txt").write_text(
    "# Modules PyTorch/torchvision pull in while File Mind embeds an image, a video, a sound and a query.\n"
    "# The NVIDIA build ships without PyTorch, so these are bundled explicitly (regenerate: packaging/scan_imports.py).\n"
    + "\n".join(names) + "\n")
print(len(names), "modules")
