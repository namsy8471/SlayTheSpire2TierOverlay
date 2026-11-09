"""Patched-card list: known v0.108-0.111 changes + drift check (|p107 - pNew| >= 15pp and |z| >= 2.5, n >= 20 each side).
usage: drift.py cards.csv runs_index.csv out.json"""
import csv, json, sys, math, collections
KNOWN = ['HAUNT', 'REAVE', 'SOUL_STORM', 'LEGION_OF_BONE', 'EIDOLON', 'SACRIFICE', 'THE_SCYTHE', 'SHROUD', 'TIMES_UP',
         'DEVASTATE', 'RESONANCE', 'GUARDS', 'PILLAR_OF_CREATION', 'COLLISION_COURSE', 'TERRAFORMING', 'CRUSH_UNDER',
         'ALIGNMENT', 'SPOILS_OF_BATTLE', 'REFINE_BLADE', 'GUIDING_STAR', 'ENTROPY', 'REND', 'SPLASH', 'SALVO']
idx = {r['runId']: r for r in csv.DictReader(open(sys.argv[2], encoding='utf-8'))}
st = collections.defaultdict(lambda: [0, 0, 0, 0])   # n107, k107, nNew, kNew
for r in csv.DictReader(open(sys.argv[1], encoding='utf-8')):
    if r['context'] not in ('CardReward', 'EliteReward', 'BossReward'): continue
    m = idx.get(r['run'])
    if not m or m.get('keep', '1') != '1': continue
    o = 0 if m['build_id'].startswith('v0.107') else 2
    x = st[(r['character'], r['card'])]; x[o] += 1; x[o + 1] += int(r['ranker_pick'])
out = {}
for k, (n1, k1, n2, k2) in st.items():
    if k[1][5:] in KNOWN:
        out[f'{k[0]}|{k[1]}'] = 'known'
    if n1 < 20 or n2 < 20: continue
    p1, p2 = k1 / n1, k2 / n2; p = (k1 + k2) / (n1 + n2)
    se = math.sqrt(max(1e-9, p * (1 - p) * (1 / n1 + 1 / n2)))
    z = (p2 - p1) / se
    if abs(p2 - p1) >= 0.15 and abs(z) >= 2.5:
        out[f'{k[0]}|{k[1]}'] = out.get(f'{k[0]}|{k[1]}', '') + f'drift {p1:.0%}->{p2:.0%} z {z:+.1f} n {n1}/{n2}'
        print(f'{k[0][10:]:12} {k[1][5:]:22} v107 {p1:.0%} (n {n1}) -> new {p2:.0%} (n {n2}) z {z:+.1f}' + (' [known]' if k[1][5:] in KNOWN else ''))
json.dump(out, open(sys.argv[3], 'w'), indent=0)
print('patched keys', len(out))
