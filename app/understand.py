"""Reads a typed or spoken question ("find me a file from 5th August, red colour") into exact filters plus the words left
over for the meaning search. Pure: no database, no model, so it is instant and easy to test.

understand(text, now, utc_offset_min, day_first) -> {text, without, filters, kinds, color, source, favourite, chips}
Filters use the engine's own names (after, before, min_size, max_size, min_pixels, min_duration, max_duration, min_fps, aspect, codec).
"""
import calendar
import re
from datetime import datetime, timezone

from . import colors, names

MONTHS = {"january": 1, "jan": 1, "february": 2, "feb": 2, "march": 3, "mar": 3, "april": 4, "apr": 4, "may": 5, "june": 6, "jun": 6,
          "july": 7, "jul": 7, "august": 8, "aug": 8, "september": 9, "sept": 9, "sep": 9, "october": 10, "oct": 10,
          "november": 11, "nov": 11, "december": 12, "dec": 12}
MON_ABBR = ["", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]
_MON = "|".join(sorted(MONTHS, key=len, reverse=True))
_PREP = r"(?:from|on|in|during|of|since|before|after|until|till|through|between|taken|made|dated|at)"

_ONES = {"zero": 0, "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7, "eight": 8, "nine": 9, "ten": 10,
         "eleven": 11, "twelve": 12, "thirteen": 13, "fourteen": 14, "fifteen": 15, "sixteen": 16, "seventeen": 17, "eighteen": 18,
         "nineteen": 19, "twenty": 20, "thirty": 30, "forty": 40, "fifty": 50, "sixty": 60, "seventy": 70, "eighty": 80, "ninety": 90}
_ORD = {"first": 1, "second": 2, "third": 3, "fourth": 4, "fifth": 5, "sixth": 6, "seventh": 7, "eighth": 8, "ninth": 9, "tenth": 10,
        "eleventh": 11, "twelfth": 12, "thirteenth": 13, "fourteenth": 14, "fifteenth": 15, "sixteenth": 16, "seventeenth": 17,
        "eighteenth": 18, "nineteenth": 19, "twentieth": 20, "thirtieth": 30}
_UNIT_S = {"second": 1, "sec": 1, "minute": 60, "min": 60, "hour": 3600, "hr": 3600}
_DAY = 86400

FILLERS = [r"um+", r"uh+", r"er+m?", r"please", r"can you", r"could you", r"would you", r"will you", r"i'?m looking for", r"i am looking for",
           r"i need", r"i want", r"show me", r"find me", r"get me", r"search for", r"look for", r"look up", r"give me", r"pull up", r"find", r"show",
           r"get", r"all of the", r"all the", r"all", r"some", r"any", r"the", r"my", r"a file", r"files?", r"stuff"]
GLUE_END = {"of", "with", "from", "a", "an", "the", "that", "file", "files", "and", "in", "on", "at", "for", "by", "to", "which", "taken",
            "made", "is", "are", "was", "were", "have", "has", "me", "my", "about", "showing", "where", "or", "than", "over", "under"}
GLUE_START = {"of", "with", "from", "that", "file", "files", "and", "in", "on", "at", "for", "by", "to", "which", "taken", "made", "is", "are",
              "was", "were", "have", "has", "showing", "where", "or", "than"}

KIND_WORDS = [("video", r"videos?|clips?|footage|reels?|movies?"), ("image", r"photos?|photographs?|images?|pictures?|pics?|stills?|renders?"),
              ("audio", r"songs?|music|audio|sounds?|tracks?|voice ?overs?|recordings?"), ("doc", r"pdfs?|documents?|docs?")]
_KIND_LABEL = {"video": "Videos", "image": "Photos", "audio": "Sounds", "doc": "Documents"}

# Colour words that are only ever colours, and ones (sky, forest...) that also name things, so need "colour" after them.
_COLOR_BARE = {"red": "red", "orange": "orange", "yellow": "yellow", "green": "green", "teal": "teal", "cyan": "cyan", "blue": "blue",
               "purple": "purple", "magenta": "magenta", "pink": "pink", "brown": "brown", "white": "white", "grey": "grey", "gray": "grey",
               "black": "black", "violet": "purple", "turquoise": "teal", "beige": "tan", "reddish": "red", "bluish": "blue",
               "greenish": "green", "yellowish": "yellow", "pinkish": "pink", "purplish": "purple", "orangish": "orange", "brownish": "brown"}
_COLOR_NEEDS_WORD = {k: k for k, _, _ in colors.SWATCHES if k not in _COLOR_BARE}
_COLOR_NEEDS_WORD.update({"golden": "amber", "gold": "amber", "dark blue": "navy", "light blue": "sky", "sky blue": "sky", "dark green": "forest",
                          "light green": "lime", "lime green": "lime", "hot pink": "pink", "off white": "cream"})
_COLOR_LABEL = {k: lab for k, lab, _ in colors.SWATCHES}

_AMBIGUOUS_SOURCES = {"camera", "screenshot", "creation", "flow", "downloads", "attachments"}


def _norm_numbers(t: str) -> str:
    t = re.sub(r"\bhalf an? (minute|hour)\b", lambda m: "30 seconds" if m.group(1) == "minute" else "30 minutes", t)
    t = re.sub(r"\b(an?|one) (second|minute|hour|day|week|month|year)s?\b", r"1 \2", t)
    tens = "|".join(k for k, v in _ONES.items() if v >= 20)
    ones = "|".join(k for k, v in _ONES.items() if 0 < v < 10)
    t = re.sub(rf"\b({tens})[ -]({ones})\b", lambda m: str(_ONES[m.group(1)] + _ONES[m.group(2)]), t)
    t = re.sub(rf"\b({'|'.join(sorted(_ONES, key=len, reverse=True))})\b(?=\s*(?:seconds?|secs?|minutes?|mins?|hours?|days?|weeks?|months?|years?|"
               rf"mb|gb|kb|tb|fps|megabytes?|gigabytes?|k\b|p\b))", lambda m: str(_ONES[m.group(1)]), t)
    t = re.sub(rf"\b(twenty|thirty)[ -]({'|'.join(k for k in _ORD if _ORD[k] < 10)})\b",
               lambda m: f"{_ONES[m.group(1)] + _ORD[m.group(2)]}th", t)
    t = re.sub(rf"\b({'|'.join(sorted(_ORD, key=len, reverse=True))})\b(?=\s+(?:of\s+)?(?:{_MON})\b)", lambda m: f"{_ORD[m.group(1)]}th", t)
    t = re.sub(rf"\b({_MON})\s+(?:the\s+)?({'|'.join(sorted(_ORD, key=len, reverse=True))})\b", lambda m: f"{m.group(1)} {_ORD[m.group(2)]}th", t)
    t = re.sub(r"\b(\d{1,2})(?:st|nd|rd|th)\b", r"\1", t)         # "5th" -> "5" once the ordinal words are numbers
    return t


class _Text:
    """The sentence being read; each rule takes what it understands out of it."""

    def __init__(self, s: str):
        self.s = s

    def take(self, pattern: str, flags=re.I):
        m = re.search(pattern, self.s, flags)
        if m:
            self.s = (self.s[:m.start()] + " " + self.s[m.end():])
        return m

    def take_all(self, pattern: str, flags=re.I):
        found = list(re.finditer(pattern, self.s, flags))
        self.s = re.sub(pattern, " ", self.s, flags=flags)
        return found


def _utc(ts):
    return datetime.fromtimestamp(ts, timezone.utc).replace(tzinfo=None)


def _fmt_day(ts: float, off: int, year=True) -> str:
    d = _utc(ts + off * 60)
    return f"{d.day} {MON_ABBR[d.month]}" + (f" {d.year}" if year else "")


def _range_label(a: float, b: float, off: int) -> str:
    last = b - 1
    if last - a < _DAY:
        return _fmt_day(a, off)
    da, db_ = _utc(a + off * 60), _utc(last + off * 60)
    if da.year == db_.year:
        return f"{_fmt_day(a, off, False)} – {_fmt_day(last, off)}"
    return f"{_fmt_day(a, off)} – {_fmt_day(last, off)}"


def understand(text: str, now: float, utc_offset_min: int = 0, day_first: bool = True) -> dict:
    off = int(utc_offset_min)
    today_dt = _utc(now + off * 60)

    def day_start(y, m, d) -> float:
        return calendar.timegm(datetime(y, m, d).timetuple()) - off * 60

    def add_months(y, m, n):
        k = y * 12 + (m - 1) + n
        return k // 12, k % 12 + 1

    today0 = day_start(today_dt.year, today_dt.month, today_dt.day)
    original = (text or "").strip()
    low = original.lower().replace("’", "'")
    low = re.sub(r"[?!;]+", " ", low)
    low = re.sub(r"\bcolou?rs?\b", "colour", low)
    low = _norm_numbers(low)
    out = {"text": "", "without": None, "filters": {}, "kinds": [], "color": None, "source": None, "favourite": False, "chips": []}
    if not low.strip():
        return out
    T = _Text(" " + low + " ")
    chips = []
    f = out["filters"]

    # ---- fillers at the start ("um can you show me ...")
    for _ in range(6):
        m = re.match(r"^\s*(?:" + "|".join(FILLERS) + r")\b[\s,]*", T.s, re.I)
        if not m or m.end() == 0:
            break
        T.s = " " + T.s[m.end():]

    # ---- favourites
    if T.take(r"\b(?:my\s+)?(?:favou?rites?|favou?rited|starred)\b"):
        out["favourite"] = True
        chips.append(("favourite", "Favourites", "yes"))

    # ---- dates
    date_chip = None

    def set_range(a, b, label=None):
        nonlocal date_chip
        f["after"], f["before"] = float(a), float(b)
        date_chip = label or _range_label(a, b, off)

    m = T.take(rf"\b(?:{_PREP}\s+)?(?:the\s+)?(today)\b")
    if m:
        set_range(today0, today0 + _DAY, "Today")
    elif T.take(rf"\b(?:{_PREP}\s+)?yesterday\b"):
        set_range(today0 - _DAY, today0, "Yesterday")
    elif (m := T.take(rf"\b(?:{_PREP}\s+)?(this|last|past|previous)\s+(week|month|year)\b")):
        which, unit = m.group(1), m.group(2)
        if which == "this":
            if unit == "week":
                a = today0 - today_dt.weekday() * _DAY
            elif unit == "month":
                a = day_start(today_dt.year, today_dt.month, 1)
            else:
                a = day_start(today_dt.year, 1, 1)
            set_range(a, today0 + _DAY, "This " + unit)
        else:                                               # "last week/month/year": the previous calendar span
            if unit == "week":
                mon = today0 - today_dt.weekday() * _DAY
                set_range(mon - 7 * _DAY, mon, "Last week")
            elif unit == "month":
                y, mo = add_months(today_dt.year, today_dt.month, -1)
                set_range(day_start(y, mo, 1), day_start(today_dt.year, today_dt.month, 1), "Last month")
            else:
                set_range(day_start(today_dt.year - 1, 1, 1), day_start(today_dt.year, 1, 1), "Last year")
    elif (m := T.take(rf"\b(?:{_PREP}\s+)?(?:the\s+)?(?:last|past|previous)\s+(\d+)\s+(day|week|month|year)s?\b")):
        n, unit = int(m.group(1)), m.group(2)
        if unit == "day":
            a = today0 - (n - 1) * _DAY
        elif unit == "week":
            a = today0 - n * 7 * _DAY
        elif unit == "month":
            y, mo = add_months(today_dt.year, today_dt.month, -n)
            a = day_start(y, mo, min(today_dt.day, 28))
        else:
            a = day_start(today_dt.year - n, today_dt.month, min(today_dt.day, 28))
        set_range(a, today0 + _DAY, f"Last {n} {unit}{'s' if n != 1 else ''}")
    elif (m := T.take(r"\b(\d+)\s+(day|week|month|year)s?\s+ago\b")):
        n, unit = int(m.group(1)), m.group(2)
        if unit == "month":
            y, mo = add_months(today_dt.year, today_dt.month, -n)
            point, span = day_start(y, mo, min(today_dt.day, 28)), n * 30 * _DAY
        elif unit == "year":
            point, span = day_start(today_dt.year - n, today_dt.month, min(today_dt.day, 28)), n * 365 * _DAY
        else:
            span = n * (7 if unit == "week" else 1) * _DAY
            point = today0 - span
        half = max(span * 0.25, _DAY)
        a = point - half
        a -= (a - today0) % _DAY                              # snap to a day start
        b = point + half
        b += (-(b - today0)) % _DAY
        set_range(a, b + (_DAY if b <= a else 0), f"About {n} {unit}{'s' if n != 1 else ''} ago")
    elif (m := T.take(r"\b(?:in|from|during|of)?\s*(?:the\s+)?last\s+(spring|summer|autumn|fall|winter)\b")):
        season = m.group(1)
        start_m, end_m = {"spring": (3, 6), "summer": (6, 9), "autumn": (9, 12), "fall": (9, 12), "winter": (12, 3)}[season]
        y = today_dt.year
        for _ in range(2):                                   # most recent finished season
            ys, ye = (y, y) if season != "winter" else (y - 1, y)
            if season == "winter":
                a, b = day_start(ys, 12, 1), day_start(ye, 3, 1)
            else:
                a, b = day_start(ys, start_m, 1), day_start(ye, end_m, 1)
            if b <= today0 + _DAY:
                break
            y -= 1
        set_range(a, b, "Last " + season)
    else:
        date_chip = _absolute_dates(T, f, today_dt, today0, off, day_first, day_start, add_months, _range_label)

    if date_chip:
        chips.append(("date", "Date", date_chip))

    # ---- length of a video or sound
    dur_unit = r"(seconds?|secs?|minutes?|mins?|hours?|hrs?)\b"
    num = r"(\d+(?:\.\d+)?)"

    def secs(v, u):
        return float(v) * next(s for k, s in _UNIT_S.items() if u.startswith(k))
    m = T.take(rf"\b(?:between\s+)?{num}\s*(?:and|to|-|–)\s*{num}\s*{dur_unit}")
    if m:
        lo, hi = secs(m.group(1), m.group(3)), secs(m.group(2), m.group(3))
        f["min_duration"], f["max_duration"] = min(lo, hi), max(lo, hi)
        chips.append(("length", "Length", f"{m.group(1)}–{m.group(2)} {m.group(3)}"))
    else:
        if (m := T.take(rf"\b(?:over|longer than|more than|at least|above|exceeding|longer)\s+{num}\s*{dur_unit}")):
            f["min_duration"] = secs(m.group(1), m.group(2))
            chips.append(("length", "Length", f"Over {m.group(1)} {m.group(2)}"))
        if (m := T.take(rf"\b(?:under|shorter than|less than|at most|below|within|up to|shorter)\s+{num}\s*{dur_unit}")):
            f["max_duration"] = secs(m.group(1), m.group(2))
            chips.append(("length", "Length", f"Under {m.group(1)} {m.group(2)}"))
        if not any(k in f for k in ("min_duration", "max_duration")) and (m := T.take(rf"\b{num}[ -]{dur_unit}(?:\s+long)?")):
            v = secs(m.group(1), m.group(2))
            f["min_duration"], f["max_duration"] = round(v * 0.75, 2), round(v * 1.25, 2)
            chips.append(("length", "Length", f"About {m.group(1)} {m.group(2)}"))

    # ---- file size
    size_unit = r"(kb|mb|gb|tb|kilobytes?|megabytes?|gigabytes?)\b"
    mult = {"k": 1000, "m": 1_000_000, "g": 1_000_000_000, "t": 1_000_000_000_000}
    if (m := T.take(rf"\b(?:over|bigger than|larger than|more than|at least|above|exceeding)\s+{num}\s*{size_unit}")):
        f["min_size"] = int(float(m.group(1)) * mult[m.group(2)[0]])
        chips.append(("size", "Size", f"Over {m.group(1)} {m.group(2).upper()}"))
    if (m := T.take(rf"\b(?:under|smaller than|less than|at most|below|up to)\s+{num}\s*{size_unit}")):
        f["max_size"] = int(float(m.group(1)) * mult[m.group(2)[0]])
        chips.append(("size", "Size", f"Under {m.group(1)} {m.group(2).upper()}"))
    if "min_size" not in f and T.take(r"\bhuge\b(?:\s+files?)?"):
        f["min_size"] = 500_000_000
        chips.append(("size", "Size", "Over 500 MB"))
    if "max_size" not in f and T.take(r"\b(?:tiny|small)\s+files\b"):
        f["max_size"] = 1_000_000
        chips.append(("size", "Size", "Under 1 MB"))

    # ---- resolution, shape, motion, codec
    if T.take(r"\b8k\b"):
        f["min_pixels"] = 7680 * 4320; chips.append(("res", "Resolution", "8K"))
    elif T.take(r"\b(?:4k|uhd|ultra hd|2160p)\b"):
        f["min_pixels"] = 3840 * 2160; chips.append(("res", "Resolution", "4K"))
    elif T.take(r"\b(?:1080p|full hd|fhd|hd)\b"):
        f["min_pixels"] = 1920 * 1080; chips.append(("res", "Resolution", "HD"))
    elif T.take(r"\b720p\b"):
        f["min_pixels"] = 1280 * 720; chips.append(("res", "Resolution", "720p"))
    kind_after = r"(?=\s+(?:videos?|clips?|footage|photos?|images?|pictures?|pics?|reels?|orientation|format|mode)\b)"
    if T.take(r"\b(?:vertical|tall)\b") or T.take(rf"\bportrait\b{kind_after}") or T.take(r"\bportrait\s+(?:orientation|format|mode)\b"):
        f["aspect"] = "portrait"; chips.append(("shape", "Shape", "Vertical"))
    elif T.take(r"\bhorizontal\b") or T.take(rf"\blandscape\b{kind_after}"):
        f["aspect"] = "landscape"; chips.append(("shape", "Shape", "Horizontal"))
    elif T.take(r"\bsquare\b"):
        f["aspect"] = "square"; chips.append(("shape", "Shape", "Square"))
    elif T.take(r"\b(?:widescreen|cinematic|16\s*:\s*9|ultra ?wide)\b"):
        f["aspect"] = "wide"; chips.append(("shape", "Shape", "Widescreen"))
    if (m := T.take(r"\b(\d{2,3})\s*fps\b")):
        n = int(m.group(1)); f["min_fps"] = 50 if n >= 50 else max(int(n * 0.85), 1)
        chips.append(("fps", "Frame rate", f"{n} fps"))
    elif T.take(r"\b(?:high frame ?rate|slow[ -]?mo(?:tion)?|slo[ -]?mo)\b"):
        f["min_fps"] = 50; chips.append(("fps", "Frame rate", "High frame rate"))
    for pat, codec in ((r"\bh\.?264\b|\bavc\b", "h264"), (r"\bhevc\b|\bh\.?265\b", "hevc"), (r"\bav1\b", "av1"),
                       (r"\bprores\b", "prores"), (r"\bvp9\b", "vp9")):
        if T.take(pat):
            f["codec"] = codec; chips.append(("codec", "Codec", codec.upper())); break

    # ---- kinds
    kinds = []
    if T.take(r"\bmusic videos?\b"):
        kinds.append("video")
    for k, pat in KIND_WORDS:
        for _ in range(3):
            m = T.take(rf"\b(?:{pat})\b(?:\s+(?:of|with|showing|where|that|about|from)\b)?")
            if not m:
                break
            if k not in kinds:
                kinds.append(k)
    out["kinds"] = kinds
    for k in kinds:
        chips.append(("kind", "Type", _KIND_LABEL[k]))

    # ---- colour
    plain = re.sub(r"\s+", " ", T.s)
    if "black and white" not in plain and "monochrome" not in plain and "b&w" not in plain:
        needs = "|".join(sorted(map(re.escape, _COLOR_NEEDS_WORD), key=len, reverse=True))
        bare = "|".join(sorted(_COLOR_BARE, key=len, reverse=True))
        m = T.take(rf"\b(?:in\s+(?:a\s+)?)?(?:{needs})\s+(?:colou?r(?:ed|s)?|tones?|tinted|hues?|shades?)\b")
        key = None
        if m:
            word = re.sub(r"^(?:in\s+(?:a\s+)?)", "", m.group(0).strip())
            word = re.sub(r"\s+(?:colou?r(?:ed|s)?|tones?|tinted|hues?|shades?)$", "", word)
            key = _COLOR_NEEDS_WORD.get(word)
        else:
            m = T.take(rf"\b(?:in\s+(?:a\s+)?)?({bare})(?:\s+(?:colou?r(?:ed|s)?|tones?|tinted|hues?|shades?))?\b")
            key = _COLOR_BARE[m.group(1)] if m else None
        if key:
            out["color"] = key
            chips.append(("color", "Colour", _COLOR_LABEL.get(key, key.title())))
    # ---- where it came from
    if T.take(r"\bscreenshots?\b"):
        out["source"] = "screenshot"
    elif T.take(r"\b(?:from|on)\s+(?:my\s+)?(?:phone|camera|iphone|android)\b"):
        out["source"] = "camera"
    elif T.take(r"\bfrom\s+(?:my\s+)?downloads\b"):
        out["source"] = "downloads"
    else:
        for key, (label, _) in names.LABEL.items():
            if key in _AMBIGUOUS_SOURCES:
                continue
            words = {key, label.lower().split(" ")[0]}
            alt = "|".join(map(re.escape, words))
            if T.take(rf"\b(?:from|by|made (?:with|in|by)|generated (?:with|by|in)|in|on|using|with)?\s*\b({alt})\b(?:\s+(?:images?|videos?|pics?|clips?|files?|generations?))?"):
                out["source"] = key
                break
    if out["source"]:
        chips.append(("source", "Source", names.LABEL[out["source"]][0] if out["source"] in names.LABEL else out["source"]))

    # ---- "without ..."
    rest = re.sub(r"\s+", " ", T.s).strip()
    m = re.search(r"(?:^|\s)(?:without|but not|except|excluding|no)\s+(?:any\s+)?(.+)$", rest)
    if m and (m.start() > 0 or kinds or f or out["color"] or out["source"]):
        out["without"] = rest[m.start(1):].strip(" .,")
        rest = rest[:m.start()].strip()

    # ---- what is left is the search
    words = [w for w in re.split(r"\s+", rest.replace(",", " ")) if w]
    while words and words[0] in GLUE_START:
        words.pop(0)
    while words and words[-1] in GLUE_END:
        words.pop()
    leftover = " ".join(words).strip(" .,")
    out["text"] = leftover
    if leftover:
        chips.append(("text", "Text", leftover))
    if out["without"]:
        chips.append(("without", "Without", out["without"]))
    out["chips"] = [{"key": k, "label": l, "value": v} for k, l, v in chips]
    return out


def _absolute_dates(T, f, today_dt, today0, off, day_first, day_start, add_months, range_label):
    """'5th August', 'August 5 2024', '5/8', 'in March', 'since 2023', 'between 1 and 5 May', 'from X to Y'."""
    year_now = today_dt.year
    s = T.s
    spans = []                          # (start, end, y, m, d or None, text span start, end)

    def past_year(mo, d=None):
        y = year_now
        if d is not None:
            try:
                if day_start(y, mo, d) > today0 + _DAY:
                    y -= 1
            except ValueError:
                pass
        elif day_start(y, mo, 1) > today0 + _DAY:
            y -= 1
        return y

    def day_range(y, mo, d):
        try:
            a = day_start(y, mo, d)
        except ValueError:
            return None
        return a, a + _DAY

    pats = [
        rf"\b(\d{{1,2}})\s+(?:of\s+)?({_MON})\b(?:,?\s+(\d{{4}}))?",           # 5 august [2024]
        rf"\b({_MON})\s+(\d{{1,2}})\b(?!\s*(?::|\d))(?:,?\s+(\d{{4}}))?",         # august 5 [2024]
    ]
    found = []
    for i, p in enumerate(pats):
        for m in re.finditer(p, s, re.I):
            d, mo, y = (int(m.group(1)), MONTHS[m.group(2).lower()], m.group(3)) if i == 0 else (int(m.group(2)), MONTHS[m.group(1).lower()], m.group(3))
            if not 1 <= d <= 31:
                continue
            yy = int(y) if y else past_year(mo, d)
            r = day_range(yy, mo, d)
            if r:
                found.append((m.start(), m.end(), r))
    for m in re.finditer(r"\b(\d{1,2})/(\d{1,2})(?:/(\d{2,4}))?\b|\b(\d{1,2})[./-](\d{1,2})[./-](\d{2,4})\b", s):
        g = m.groups()
        a, b, y = (g[0], g[1], g[2]) if g[0] else (g[3], g[4], g[5])
        d, mo = (int(a), int(b)) if day_first else (int(b), int(a))
        if not (1 <= mo <= 12 and 1 <= d <= 31):
            if day_first and 1 <= int(b) <= 31 and 1 <= int(a) <= 12:
                d, mo = int(b), int(a)
            else:
                continue
        yy = (int(y) + 2000 if y and len(y) == 2 else int(y)) if y else past_year(mo, d)
        r = day_range(yy, mo, d)
        if r:
            found.append((m.start(), m.end(), r))
    # a month on its own ("in March", "March 2024"); "may"/"march" need a helper word
    covered = [(a, b) for a, b, _ in found]
    for m in re.finditer(rf"\b(?:{_PREP}\s+)?({_MON})\b(?:,?\s+(\d{{4}}))?", s, re.I):
        word = m.group(1).lower()
        if any(a <= m.start(1) < b for a, b in covered):
            continue
        if word in ("may", "march", "mar") and not (m.group(2) or re.match(rf"\s*{_PREP}\s", m.group(0), re.I)):
            continue
        mo = MONTHS[word]
        y = int(m.group(2)) if m.group(2) else past_year(mo)
        y2, mo2 = add_months(y, mo, 1)
        found.append((m.start(), m.end(), (day_start(y, mo, 1), day_start(y2, mo2, 1))))
        covered.append((m.start(), m.end()))
    for m in re.finditer(r"\b(?:in|from|during|of|since|before|after|until|year)\s+((?:19|20)\d\d)\b", s, re.I):
        if any(a <= m.start(1) < b for a, b in covered):
            continue
        y = int(m.group(1))
        found.append((m.start(), m.end(), (day_start(y, 1, 1), day_start(y + 1, 1, 1))))
        covered.append((m.start(), m.end()))
    if not found:
        return None
    found.sort()
    # drop overlaps, keep the first of each
    clean = []
    for it in found:
        if not clean or it[0] >= clean[-1][1]:
            clean.append(it)
    first = clean[0]
    before_txt = s[max(0, first[0] - 14):first[0] + 14].lower()
    # a range: "between X and Y" / "from X to Y|until Y"
    if len(clean) >= 2:
        gap = s[clean[0][1]:clean[1][0]].lower()
        lead = s[max(0, clean[0][0] - 9):clean[0][0]].lower()
        if re.fullmatch(r"\s*(?:and|to|until|till|through|-|–)\s*", gap) and re.search(r"(between|from)\s*$", lead + s[clean[0][0]:clean[0][0]]) or \
                re.fullmatch(r"\s*(?:and|to|until|till|through|-|–)\s*", gap):
            a, b = clean[0][2][0], clean[1][2][1]
            T.s = s[:clean[0][0]] + " " + s[clean[1][1]:]
            T.s = re.sub(r"\bbetween\b", " ", T.s, count=1) if "between" in lead else T.s
            f["after"], f["before"] = float(a), float(b)
            return range_label(a, b, off)
    a, b = first[2]
    text = s[first[0]:first[1]].lower()
    kw = re.match(r"\s*(since|before|after|until|till)\b", text)
    lead = None if kw else re.search(r"\b(since|before|after|until|till)\s+(?:the\s+)?$", s[:first[0]].lower() + " ")
    if lead:                                    # the keyword sits just before the date: take it out too
        kw = lead
        T.s = s[:lead.start()] + " " + s[first[1]:]
    else:
        T.s = s[:first[0]] + " " + s[first[1]:]
    if kw:
        w = kw.group(1)
        if w == "since":
            f["after"] = float(a); label = "Since " + _fmt_day(a, off)
        elif w == "after":
            f["after"] = float(b); label = "After " + _fmt_day(b - 1, off)
        else:
            f["before"] = float(a if w == "before" else b); label = ("Before " if w == "before" else "Until ") + _fmt_day(a, off)
        return label
    f["after"], f["before"] = float(a), float(b)
    return range_label(a, b, off)
