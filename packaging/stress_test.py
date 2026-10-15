"""Read-only load check against a running local File Mind instance.

Usage: python packaging/stress_test.py --url http://127.0.0.1:8767
Never changes indexed files or settings. Exercises browsing, precise filters, semantic search,
duplicate lookup, thumbnails, and concurrent requests against the current library.
"""
import argparse
import json
import statistics
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed


def request(base, path, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(base + path, data=data,
                                 headers={"Content-Type": "application/json"} if data else {},
                                 method="POST" if data else "GET")
    start = time.perf_counter()
    with urllib.request.urlopen(req, timeout=90) as response:
        payload = response.read()
    elapsed = time.perf_counter() - start
    return (json.loads(payload) if not path.startswith("/api/thumb/") else None), elapsed


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:8765")
    parser.add_argument("--rounds", type=int, default=8)
    args = parser.parse_args()
    base = args.url.rstrip("/")
    library, _ = request(base, "/api/files?limit=20")
    ids = [item["id"] for item in library["files"]]
    visual_ids = [item["id"] for item in library["files"] if item["kind"] != "audio"]
    jobs = []
    for _ in range(args.rounds):
        jobs.extend([(f"/api/files?limit=100&offset={offset}", None) for offset in (0, 100, 200)])
        jobs.extend([(path, None) for path in (
            "/api/files?kind=video&min_pixels=8294400&limit=100",
            "/api/files?aspect=portrait&max_duration=30&limit=100", "/api/discover")])
        jobs.extend([("/api/search", {"q": q, "limit": 40, "filters": {"min_pixels": 2073600}})
                     for q in ("a motorcycle on a road", "a product on a dark background")])
    jobs.extend([(f"/api/thumb/{fid}", None) for fid in visual_ids[:10]])
    jobs.extend([(f"/api/duplicates?focus={fid}", None) for fid in ids[:3]])
    times = []
    with ThreadPoolExecutor(max_workers=6) as pool:
        futures = [pool.submit(request, base, path, body) for path, body in jobs]
        for future in as_completed(futures):
            _, seconds = future.result()
            times.append(seconds)
    times.sort()
    p95 = times[int((len(times) - 1) * .95)]
    print(f"{len(times)} requests succeeded; median {statistics.median(times):.3f}s, p95 {p95:.3f}s, max {max(times):.3f}s")


if __name__ == "__main__":
    main()
