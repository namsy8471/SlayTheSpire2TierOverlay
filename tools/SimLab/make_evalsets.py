"""Fixed dev / held-out test fight sets for battle-AI work.

Usage: python -P make_evalsets.py <fights.jsonl> <out_dir>
Only v0.111 fights (the simulator's game version). Runs are split by a hash of run_id, so a run's
fights are all in dev or all in test (the test runs are never looked at while tuning).
Stratified by room type so bosses and elites have enough fights to measure death rates.
"""
import hashlib, json, random, sys
from collections import defaultdict

src, out = sys.argv[1], sys.argv[2]
rows = [json.loads(l) for l in open(src, encoding="utf-8")]
rows = [r for r in rows if r["build_id"].startswith("v0.111")]

def split(run_id):
    h = int(hashlib.sha256(("simlab:" + run_id).encode()).hexdigest(), 16) % 100
    return "dev" if h < 30 else "test"

quota = {"dev": {"monster": 300, "elite": 150, "boss": 150},
         "test": {"monster": 1200, "elite": 400, "boss": 400}}
pools = defaultdict(list)
for r in rows:
    if r["room_type"] in ("monster", "elite", "boss"):
        pools[(split(r["run_id"]), r["room_type"])].append(r)
rng = random.Random(20261109)
for s, q in quota.items():
    picked = []
    for rt, n in q.items():
        pool = pools[(s, rt)]
        rng.shuffle(pool)
        picked += pool[:n]
        print(s, rt, "pool", len(pool), "picked", min(n, len(pool)))
    with open(f"{out}/{s}.jsonl", "w", encoding="utf-8") as f:
        for r in picked:
            f.write(json.dumps(r) + "\n")
