"""Colour helpers. Every image gets a palette coverage map ({swatch: share of the picture}),
so 'green' means pictures that are actually largely green, not ones with a green speck."""
import colorsys
import json

# (key, label, swatch) — matched in CIE-Lab space, so they behave like a person would name them
SWATCHES = [
    ("red", "Red", "#e0383e"), ("coral", "Coral", "#ff7b67"), ("orange", "Orange", "#f47c20"), ("amber", "Amber", "#eda51b"),
    ("yellow", "Yellow", "#f4d63a"), ("lime", "Lime", "#a6d13c"), ("green", "Green", "#3c9d55"), ("forest", "Forest", "#245c3a"),
    ("teal", "Teal", "#1b9f97"), ("cyan", "Cyan", "#40c3e0"), ("sky", "Sky", "#82b8ee"), ("blue", "Blue", "#2f63d8"),
    ("navy", "Navy", "#1c2a55"), ("indigo", "Indigo", "#5947c9"), ("purple", "Purple", "#8f4fcf"), ("magenta", "Magenta", "#cf3aae"),
    ("pink", "Pink", "#f087b5"), ("blush", "Blush", "#f3c8cc"), ("brown", "Brown", "#77482a"), ("tan", "Tan", "#c49d74"),
    ("cream", "Cream", "#eee4cf"), ("white", "White", "#f8f8f9"), ("grey", "Grey", "#9a9fa9"), ("charcoal", "Charcoal", "#3b3e46"),
    ("black", "Black", "#0e0f12"),
]
NEUTRAL = {"white", "grey", "charcoal", "black", "cream"}
MIN_SHARE = 0.16          # a colour must cover this much of a picture to count as "a green picture"


def _rgb(hexcolor: str):
    h = hexcolor.lstrip("#")
    return int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16)


def _lab(r, g, b):
    def lin(c):
        c /= 255
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
    r, g, b = lin(r), lin(g), lin(b)
    x = (r * 0.4124 + g * 0.3576 + b * 0.1805) / 0.95047
    y = r * 0.2126 + g * 0.7152 + b * 0.0722
    z = (r * 0.0193 + g * 0.1192 + b * 0.9505) / 1.08883
    f = lambda t: t ** (1 / 3) if t > 0.008856 else 7.787 * t + 16 / 116
    fx, fy, fz = f(x), f(y), f(z)
    return 116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)


PALETTE_VERSION = "2"      # bump to recompute every stored palette


def _lch(r, g, b):
    """OKLCh, scaled to Lab-like ranges (L 0-100, C ~0-130). OKLab keeps blue blue (CIE-Lab bends it to purple)."""
    import math
    def lin(c):
        c /= 255
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
    r, g, b = lin(r), lin(g), lin(b)
    l = (0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b) ** (1 / 3)
    m = (0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b) ** (1 / 3)
    s = (0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b) ** (1 / 3)
    L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s
    A = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s
    B = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s
    return L * 100, math.hypot(A, B) * 400, math.degrees(math.atan2(B, A)) % 360


_SW_LCH = [(k, _lch(*_rgb(h))) for k, _, h in SWATCHES if k not in NEUTRAL]


def nearest(r, g, b) -> str:
    """Hue decides first (so chroma-key #00ff00 is 'green', not 'lime'), then lightness (green vs forest)."""
    L, C, H = _lch(r, g, b)
    if C < 10:                                           # no real colour: by lightness
        return "white" if L > 92 else "cream" if L > 84 and r > b + 6 else "grey" if L > 48 else "charcoal" if L > 18 else "black"
    if 40 <= H <= 110 and C < 20 and L > 80:
        return "cream"
    if 25 <= H <= 85 and C < 42:                         # skin, wood, sand, leather
        return "tan" if L >= 55 else "brown"
    best, bd = None, 1e9
    for k, (l2, c2, h2) in _SW_LCH:
        dh = abs(H - h2); dh = min(dh, 360 - dh)
        d = dh ** 2 + (0.4 * (L - l2)) ** 2 + (0.1 * (C - c2)) ** 2
        if C < 22:                                       # muted colours lean towards the earthy swatches
            d *= 0.7 if k in ("brown", "tan", "blush") else 1.3
        if d < bd:
            best, bd = k, d
    return best


def palette(thumb_path) -> str:
    """JSON {swatch: share} for a thumbnail (shares sum to 1)."""
    from PIL import Image
    with Image.open(thumb_path) as im:
        im = im.convert("RGB").resize((64, 64))
        q = im.quantize(colors=20, method=Image.Quantize.MEDIANCUT)
        pal = q.getpalette()
        cover = {}
        for count, idx in q.getcolors():
            k = nearest(*pal[idx * 3:idx * 3 + 3])
            cover[k] = cover.get(k, 0) + count
    tot = sum(cover.values()) or 1
    return json.dumps({k: round(v / tot, 3) for k, v in sorted(cover.items(), key=lambda kv: -kv[1]) if v / tot >= 0.02})


def shares(pal_json: str | None) -> dict:
    try:
        return json.loads(pal_json) if pal_json else {}
    except Exception:
        return {}


def hls(hexcolor: str):
    r, g, b = _rgb(hexcolor)
    h, l, s = colorsys.rgb_to_hls(r / 255, g / 255, b / 255)
    return h * 360, l, s


def rainbow_key(hexcolor: str | None):
    """Chromatic colours first, around the colour wheel; then greys/blacks/whites by lightness."""
    if not hexcolor or len(hexcolor) != 7:
        return (2, 0, 0)
    h, l, s = hls(hexcolor)
    if s < 0.13 or l < 0.1 or l > 0.93:
        return (1, l, 0)
    return (0, round(h / 12), -s)


def distance(a: str | None, b: str) -> float:
    if not a or len(a) != 7:
        return 1e9
    ra, ga, ba = _rgb(a)
    rb, gb, bb = _rgb(b)
    rm = (ra + rb) / 2
    return ((2 + rm / 256) * (ra - rb) ** 2 + 4 * (ga - gb) ** 2 + (2 + (255 - rm) / 256) * (ba - bb) ** 2) ** 0.5
