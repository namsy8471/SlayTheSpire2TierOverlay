"""Before/after table for sim_fights CSVs of the same fight set (markdown).

Usage: python -P compare.py <label>=<csv> [<label>=<csv> ...]
Only fights simulated in every CSV are compared. Damage ratio = mean sim damage / mean real damage.
Deck-order metrics: within-encounter Spearman (encounters with >= 20 fights, fight-weighted) and the
Spearman of encounter-demeaned damage (all encounters with >= 5 fights).
"""
import csv, sys
from collections import defaultdict
import numpy as np
from scipy import stats

sets = []
for arg in sys.argv[1:]:
    label, path = arg.split("=", 1)
    rows = {(r["run_id"], r["node_index"]): r for r in csv.DictReader(open(path, encoding="utf-8"))
            if r["sim_damage_mean"] != "" and r["mapped"] == "1"}
    sets.append((label, rows))
common = set.intersection(*(set(r) for _, r in sets))


def metrics(rows):
    sub = [rows[k] for k in sorted(common)]
    out = {}
    real = np.array([float(r["real_damage"]) for r in sub]); sim = np.array([float(r["sim_damage_mean"]) for r in sub])
    out["damage ratio (all)"] = f"{sim.mean() / real.mean():.2f}x ({sim.mean():.1f} vs {real.mean():.1f})"
    for rt in ("monster", "elite", "boss"):
        s = [r for r in sub if r["room_type"] == rt]
        re_ = np.mean([float(r["real_damage"]) for r in s]); si = np.mean([float(r["sim_damage_mean"]) for r in s])
        d = np.mean([1 - float(r["sim_win_rate"]) for r in s]); rd = np.mean([int(r["real_died"]) for r in s])
        out[f"{rt}: damage ratio"] = f"{si / re_:.2f}x"
        out[f"{rt}: death rate (real {rd * 100:.1f}%)"] = f"{d * 100:.1f}%"
    by = defaultdict(list)
    for r in sub: by[r["encounter"]].append(r)
    w, n = [], []
    for s in by.values():
        if len(s) >= 20:
            x = [float(r["real_damage"]) for r in s]; y = [float(r["sim_damage_mean"]) for r in s]
            if np.std(x) > 0 and np.std(y) > 0:
                w.append(stats.spearmanr(x, y)[0]); n.append(len(s))
    out[f"within-encounter Spearman ({len(w)} enc. n>=20)"] = f"{np.average(w, weights=n):+.3f}"
    rx, ry = [], []
    for s in by.values():
        if len(s) < 5: continue
        x = np.array([float(r["real_damage"]) for r in s]); y = np.array([float(r["sim_damage_mean"]) for r in s])
        rx.extend(x - x.mean()); ry.extend(y - y.mean())
    out["encounter-demeaned Spearman"] = f"{stats.spearmanr(rx, ry)[0]:+.3f}"
    encs = [s for s in by.values() if len(s) >= 20]
    rm = [np.mean([float(r["real_damage"]) for r in s]) for s in encs]; sm = [np.mean([float(r["sim_damage_mean"]) for r in s]) for s in encs]
    out[f"encounter-order Spearman ({len(encs)} enc.)"] = f"{stats.spearmanr(rm, sm)[0]:+.3f}"
    out["all-fight Spearman"] = f"{stats.spearmanr(real, sim)[0]:+.3f}"
    return out


cols = [(label, metrics(rows)) for label, rows in sets]
print(f"fights compared: {len(common)}\n")
print("| metric | " + " | ".join(l for l, _ in cols) + " |")
print("|---|" + "---|" * len(cols))
for k in cols[0][1]:
    print(f"| {k} | " + " | ".join(m[k] for _, m in cols) + " |")
