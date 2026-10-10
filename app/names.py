"""Works out where a file came from (Midjourney, ChatGPT, Higgsfield...) purely from its file name,
and builds generic 'name pattern' signatures for everything else."""
import re

UUID = r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
_UUID_RE = re.compile(UUID)

# (key, label, colour, test(lowercased name) -> bool)  — first match wins, so order matters.
def _mj(n: str) -> bool:
    if re.search(r"(--ar\b|--v\b|--chaos|--stylize|--raw\b|--profile|mj\.run|midjourney)", n):
        return True
    m = _UUID_RE.search(n)
    if m and not n.startswith(("hf_", "temp_", "comfyui")):
        head = n[:m.start()]
        return head.count("_") >= 3 and re.match(r"^[a-z0-9]+_", head) is not None   # user_prompt_words_<uuid>_<n>
    return False


RULES = [
    ("midjourney", "Midjourney", "#6d5bd0", _mj),
    ("chatgpt", "ChatGPT", "#10a37f", lambda n: n.startswith("chatgpt image") or n.startswith("chatgpt")),
    ("nanobanana", "Nano Banana / Gemini", "#f2b705", lambda n: bool(re.search(r"nano[ _-]?banana|gemini_generated|^gemini", n))),
    ("flow", "Google Flow / Veo", "#4285f4", lambda n: bool(re.match(r"^(flow|whisk|veo)[ _\-]", n)) or "google flow" in n),
    ("higgsfield", "Higgsfield", "#e5484d", lambda n: n.startswith("hf_") and re.match(r"^hf_\d{8}", n) is not None),
    ("dreamina", "Dreamina", "#00b4d8", lambda n: n.startswith("dreamina")),
    ("seedance", "Seedance", "#ff7a45", lambda n: n.startswith("seedance") or "seedance" in n[:20]),
    ("comfyui", "ComfyUI", "#46a876", lambda n: n.startswith("comfyui")),
    ("krea", "Krea", "#222222", lambda n: bool(re.search(r"(^|[_\-])krea", n))),
    ("openart", "OpenArt", "#c026d3", lambda n: n.startswith("openart")),
    ("elevenlabs", "ElevenLabs", "#111827", lambda n: n.startswith("elevenlabs")),
    ("kling", "Kling", "#0ea5e9", lambda n: n.startswith("kling")),
    ("runway", "Runway", "#8b8b95", lambda n: n.startswith("runway")),
    ("sora", "Sora", "#334155", lambda n: n.startswith("sora")),
    ("hailuo", "Hailuo / MiniMax", "#f43f5e", lambda n: bool(re.match(r"^(hailuo|minimax)", n))),
    ("luma", "Luma", "#7c3aed", lambda n: n.startswith("luma")),
    ("firefly", "Firefly", "#ef4444", lambda n: n.startswith("firefly")),
    ("leonardo", "Leonardo", "#a16207", lambda n: n.startswith("leonardo")),
    ("ideogram", "Ideogram", "#0891b2", lambda n: n.startswith("ideogram")),
    ("whatsapp", "WhatsApp", "#25d366", lambda n: n.startswith("whatsapp") or bool(re.match(r"^(img|vid|aud|ptt)-\d{8}-wa", n))),
    ("screenshot", "Screenshots", "#64748b", lambda n: bool(re.match(r"^(screenshot|screen shot|snip|capture)", n))),
    ("camera", "Camera & phone", "#78716c", lambda n: bool(re.match(r"^(img_|dsc|dscf|dji_|pxl_|mvimg|vid_|mov_|gopr|p\d{7}|_dsc)", n))),
    ("creation", "creation_… exports", "#db2777", lambda n: bool(re.match(r"^creation_\d+", n))),
]
LABEL = {k: (l, c) for k, l, c, _ in RULES}


def source_of(name: str):
    n = name.lower()
    for key, _, _, test in RULES:
        try:
            if test(n):
                return key
        except Exception:
            continue
    return None


_MONTHS = {m: i for i, m in enumerate("jan feb mar apr may jun jul aug sep oct nov dec".split(), 1)}


def _words(s: str) -> str:
    s = re.sub(r"https?s?\.?mj\.run\S*?(?=_|$)", " ", s)          # midjourney image-prompt URLs
    s = re.sub(r"--\w+(_[\w.:]+)?", " ", s)                         # --ar 16:9, --chaos 20 ...
    s = s.replace("_", " ").replace("…", " ").replace("....", " ")
    s = re.sub(r"\s+", " ", s).strip(" .-")
    return s


def parse(name: str) -> dict:
    """What the file name tells us: source tool, prompt text, creation date, author handle."""
    stem = re.sub(r"\.[^.]+$", "", name)
    n = stem.lower()
    src = source_of(name)
    out = {"source": src, "prompt": None, "date": None, "author": None}
    try:
        if src == "midjourney":
            m = _UUID_RE.search(n)
            head = stem[:m.start()] if m else stem
            parts = head.strip("_").split("_", 1)
            if len(parts) == 2 and re.match(r"^[A-Za-z0-9]+$", parts[0]):
                out["author"], head = parts[0], parts[1]
            out["prompt"] = _words(head)
        elif src == "dreamina":
            m = re.match(r"dreamina-(\d{4}-\d{2}-\d{2})-\d+-(.*)", stem, re.I)
            if m:
                out["date"], out["prompt"] = m.group(1), _words(re.sub(r"\s{2,}.*$", "", m.group(2)))
        elif src == "seedance":
            m = re.search(r"\s-\s(.*)", stem)
            out["prompt"] = _words(m.group(1)) if m else None
        elif src == "chatgpt":
            m = re.search(r"(\w{3})\w* (\d{1,2}), (\d{4}), (\d{2})_(\d{2})", stem)
            if m and m.group(1).lower() in _MONTHS:
                out["date"] = f"{m.group(3)}-{_MONTHS[m.group(1).lower()]:02d}-{int(m.group(2)):02d} {m.group(4)}:{m.group(5)}"
        elif src == "higgsfield":
            m = re.match(r"hf_(\d{4})(\d{2})(\d{2})_(\d{2})(\d{2})", n)
            if m:
                out["date"] = f"{m.group(1)}-{m.group(2)}-{m.group(3)} {m.group(4)}:{m.group(5)}"
        elif src == "whatsapp":
            m = re.search(r"(\d{4}-\d{2}-\d{2})", stem)
            out["date"] = m.group(1) if m else None
        elif src is None:                                           # long descriptive names read like prompts
            w = _words(_UUID_RE.sub("", stem))
            if len(w.split()) >= 6:
                out["prompt"] = w
        if out["prompt"] and len(out["prompt"]) < 8:
            out["prompt"] = None
    except Exception:
        pass
    return out


def signature(name: str) -> str:
    """A loose shape of the file name: digits, ids and copy-numbers are blanked out, first words kept."""
    s = re.sub(r"\.[^.]+$", "", name.lower())
    s = _UUID_RE.sub("{id}", s)
    s = re.sub(r"[0-9a-f]{12,}", "{id}", s)
    s = re.sub(r"\(\d+\)", " ", s)
    s = re.sub(r"\d+", "#", s)
    toks = [t for t in re.split(r"[\s_\-\.,]+", s) if t]
    return " ".join(toks[:3]) if toks else "(unnamed)"
