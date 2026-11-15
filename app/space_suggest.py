"""Files that probably belong in a space: the ones nearest the average of what the space already holds."""
import numpy as np

# Cosine similarity to the space's centroid below which a file is not offered. Chosen from the real library:
# two random files there reach 0.60 in the middle and 0.78 at the 99th percentile, while a member held out of a
# three-file space scored 0.84 to 0.94 against the other two. 0.80 sits between the two.
MIN_SIMILARITY = 0.80
MIN_MEMBERS = 2


def centroid(ids, mat, member_ids) -> np.ndarray | None:
    """Mean of the members' vectors, renormalised; None when none of them has a vector."""
    pos = {int(i): n for n, i in enumerate(ids)}
    rows = [pos[m] for m in member_ids if m in pos]
    if not rows:
        return None
    v = mat[rows].mean(axis=0)
    n = float(np.linalg.norm(v))
    return v / n if n > 0 else None


def suggest(ids, mat, member_ids, dismissed=(), limit=40, min_similarity=MIN_SIMILARITY, min_members=MIN_MEMBERS):
    """[(file_id, similarity)] best first: not already members, not dismissed, at least min_similarity."""
    members = {int(m) for m in member_ids}
    if len(members) < min_members or len(ids) == 0:
        return []
    c = centroid(ids, mat, members)
    if c is None:
        return []
    sims = mat @ c
    skip = members | {int(d) for d in dismissed}
    out = []
    for n in np.argsort(-sims):
        s = float(sims[n])
        if s < min_similarity:
            break
        fid = int(ids[n])
        if fid not in skip:
            out.append((fid, s))
            if len(out) >= limit:
                break
    return out
