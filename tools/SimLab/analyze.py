"""Fidelity summary for sim_fights.csv.  Usage: python -P analyze.py <sim_fights.csv>"""
import csv
import sys
from collections import defaultdict

import numpy as np
from scipy import stats

path = sys.argv[1]
rows = list(csv.DictReader(open(path, encoding="utf-8")))
print(f"rows={len(rows)}")
errs = [r for r in rows if r["error"]]
print(f"rows with error text={len(errs)}  (unmapped={sum(r['error']=='unmapped' for r in rows)})")

ok = [r for r in rows if r["mapped"] == "1" and r["sim_damage_mean"] != ""]
print(f"mapped & simulated={len(ok)}")


def corr(sub, label):
    if len(sub) < 5:
        return
    x = np.array([float(r["real_damage"]) for r in sub])
    y = np.array([float(r["sim_damage_mean"]) for r in sub])
    pr = stats.pearsonr(x, y)[0]
    sr = stats.spearmanr(x, y)[0]
    rd = np.mean([int(r["real_died"]) for r in sub])
    sw = np.mean([float(r["sim_win_rate"]) for r in sub])
    print(f"{label:<22} n={len(sub):5d}  pearson={pr:+.3f} spearman={sr:+.3f}  "
          f"real_dmg={x.mean():6.2f} sim_dmg={y.mean():6.2f}  real_died={rd:.3f} sim_death={1-sw:.3f}")


corr(ok, "all")
for rt in ("monster", "elite", "boss"):
    corr([r for r in ok if r["room_type"] == rt], rt)
for a in ("1", "2", "3"):
    corr([r for r in ok if r["act"] == a], f"act{a}")
for ch in sorted({r["character"] for r in ok}):
    corr([r for r in ok if r["character"] == ch], ch.replace("CHARACTER.", ""))

# Within-encounter: does the sim order decks the same way inside one encounter?
by_enc = defaultdict(list)
for r in ok:
    by_enc[r["encounter"]].append(r)
within = []
for enc, sub in by_enc.items():
    if len(sub) >= 20:
        x = [float(r["real_damage"]) for r in sub]
        y = [float(r["sim_damage_mean"]) for r in sub]
        if np.std(x) > 0 and np.std(y) > 0:
            within.append((stats.spearmanr(x, y)[0], len(sub)))
if within:
    w = np.array([c for c, _ in within]); n = np.array([k for _, k in within])
    print(f"within-encounter spearman (encounters n>=20): count={len(within)} "
          f"mean={w.mean():+.3f} n-weighted={np.average(w, weights=n):+.3f} median={np.median(w):+.3f}")

# Per-encounter means (encounters with >=20 fights)
encs = [(enc, sub) for enc, sub in by_enc.items() if len(sub) >= 20]
if len(encs) >= 3:
    rm = np.array([np.mean([float(r["real_damage"]) for r in s]) for _, s in encs])
    sm = np.array([np.mean([float(r["sim_damage_mean"]) for r in s]) for _, s in encs])
    print(f"per-encounter mean damage (n_enc={len(encs)}): pearson={stats.pearsonr(rm, sm)[0]:+.3f} "
          f"spearman={stats.spearmanr(rm, sm)[0]:+.3f}")
    order = np.argsort(-rm)
    print("  top encounters by real mean damage: real / sim / n")
    for i in order[:12]:
        print(f"   {encs[i][0]:<48} {rm[i]:6.1f} {sm[i]:6.1f} {len(encs[i][1]):4d}")

# Residual check: real - expected-from-encounter vs sim - expected-from-encounter
res_x, res_y = [], []
for enc, sub in by_enc.items():
    if len(sub) < 5:
        continue
    x = np.array([float(r["real_damage"]) for r in sub]); y = np.array([float(r["sim_damage_mean"]) for r in sub])
    res_x.extend(x - x.mean()); res_y.extend(y - y.mean())
if res_x:
    print(f"encounter-demeaned damage: pearson={stats.pearsonr(res_x, res_y)[0]:+.3f} "
          f"spearman={stats.spearmanr(res_x, res_y)[0]:+.3f} n={len(res_x)}")

died = [r for r in ok if r["real_died"] == "1"]
if died:
    print(f"real deaths={len(died)}: sim death rate on those={np.mean([1-float(r['sim_win_rate']) for r in died]):.3f} "
          f"vs on survived fights={np.mean([1-float(r['sim_win_rate']) for r in ok if r['real_died']=='0']):.3f}")
x = np.array([float(r["real_turns"]) for r in ok]); y = np.array([float(r["sim_turns_mean"]) for r in ok])
print(f"turns: real mean={x.mean():.2f} sim mean={y.mean():.2f} spearman={stats.spearmanr(x, y)[0]:+.3f}")
