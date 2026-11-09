"""One-line summary of a sim_fights.csv for sweeps. Usage: python -P summary.py <csv> [label] [combats/s]"""
import csv, sys
from collections import defaultdict
import numpy as np
from scipy import stats

rows = [r for r in csv.DictReader(open(sys.argv[1], encoding="utf-8")) if r["mapped"] == "1" and r["sim_damage_mean"] != ""]
label = sys.argv[2] if len(sys.argv) > 2 else ""
cps = sys.argv[3] if len(sys.argv) > 3 else ""
out = []
for rt in ("monster", "elite", "boss"):
    s = [r for r in rows if r["room_type"] == rt]
    real = np.mean([float(r["real_damage"]) for r in s]); sim = np.mean([float(r["sim_damage_mean"]) for r in s])
    death = np.mean([1 - float(r["sim_win_rate"]) for r in s])
    out.append(f"{rt[:4]} x{sim/real:4.2f} d{death*100:4.1f}%")
real = np.mean([float(r["real_damage"]) for r in rows]); sim = np.mean([float(r["sim_damage_mean"]) for r in rows])
by = defaultdict(list)
for r in rows: by[r["encounter"]].append(r)
rx, ry = [], []
for sub in by.values():
    if len(sub) < 5: continue
    x = np.array([float(r["real_damage"]) for r in sub]); y = np.array([float(r["sim_damage_mean"]) for r in sub])
    rx.extend(x - x.mean()); ry.extend(y - y.mean())
dm = stats.spearmanr(rx, ry)[0]
allsp = stats.spearmanr([float(r["real_damage"]) for r in rows], [float(r["sim_damage_mean"]) for r in rows])[0]
print(f"all x{sim/real:4.2f} sim{sim:5.1f} | " + " | ".join(out) + f" | rho_all {allsp:+.3f} rho_dm {dm:+.3f} | {cps} c/s | {label}", flush=True)
