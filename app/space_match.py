"""Spaces from descriptions: which files fit "cats" or "dogs at the beach", and how the answers are merged."""
import numpy as np

# A file fits a description when its similarity to the description is unusually high for the whole library:
# at least Z_MIN standard deviations above that description's median, and not below FLOOR in raw cosine.
# Chosen from the real library (about 2,900 files, text-to-file cosine): the median sits near 0.57 with a spread
# of 0.02 to 0.04, and for "dogs" or "cats" the ranked list stops looking relevant a few dozen files in. At
# 3 deviations the queries tried (cats, dogs, a beach, city at night, food) keep 12 to 51 files, nearly all
# relevant; 2.5 let in unrelated documents, 2.0 hundreds of them. The raw floor protects tiny libraries and
# very flat distributions.
Z_MIN = 3.0
FLOOR = 0.63
MAX_FILES = 300


def match_one(mat, qvec, z_min=Z_MIN, floor=FLOOR):
    """(row indices, raw similarities, z-scores) of the files that fit one description, best first."""
    if len(mat) == 0:
        return np.zeros(0, int), np.zeros(0, np.float32), np.zeros(0, np.float32)
    sims = mat @ qvec
    z = (sims - np.median(sims)) / (sims.std() + 1e-6)
    rows = np.flatnonzero((z >= z_min) & (sims >= floor))
    rows = rows[np.argsort(-z[rows])]
    return rows, sims[rows], z[rows]


def match(ids, mat, qvecs, z_min=Z_MIN, floor=FLOOR, limit=MAX_FILES):
    """Merge several descriptions: a file counts once, with its best fit. Returns
    ([(file_id, similarity)] best first, capped at limit, [files fitting each description])."""
    best = {}                                    # file id -> (z, similarity)
    per = []
    for q in qvecs:
        rows, sims, z = match_one(mat, np.asarray(q, dtype=np.float32), z_min, floor)
        per.append(len(rows))
        for r, s, zz in zip(rows, sims, z):
            fid = int(ids[r])
            if fid not in best or zz > best[fid][0]:
                best[fid] = (float(zz), float(s))
    ranked = sorted(best.items(), key=lambda kv: -kv[1][0])[:limit]
    return [(fid, s) for fid, (_, s) in ranked], per
