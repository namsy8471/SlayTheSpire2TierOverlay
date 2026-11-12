"""Commit / target calibration for the role lists in an archetypes file (aggregates only)."""
import sys, json, collections, random
sys.path.insert(0, __import__("os").path.dirname(__import__("os").path.abspath(__file__)))
import build_stats as B
RW = {'core': 1.0, 'enabler': 0.7, 'payoff': 0.9, 'support': 0.5}
index, runs_dir, arch_path, anchors_path, character, prefix = sys.argv[1:7]
arch = B.load_jsonc(arch_path); anc = json.load(open(anchors_path))
runs = [r for r in B.load_runs(index, runs_dir, character, prefix) if r.good]
def wmedian(vals):  # vals: list of (player, value); player-capped weights
    n = collections.Counter(p for p, _ in vals); w = B.capped_weights(n)
    items = sorted((v, w[p] / n[p]) for p, v in vals); tot = sum(x for _, x in items); acc = 0
    for v, x in items:
        acc += x
        if acc >= tot / 2: return v
    return 0
def commit(a, r, fw):
    s = 0
    for role in RW:
        for c in a['cards'].get(role, []):
            n = r.cards.get(c.replace('CARD.', ''), 0)
            if n: s += RW[role] * (1.5 if n >= 2 else 1)
    for kr in a.get('keyRelics', []):
        if kr['id'].replace('RELIC.', '') in r.relics: s += kr['w']
    return min(1, s / fw)
def ess(a, r):
    return sum(1 for role in ('enabler', 'payoff') for c in a['cards'].get(role, []) if r.cards.get(c.replace('CARD.', ''), 0))
def corec(a, r):
    return sum(1 for c in a['cards'].get('core', []) if r.cards.get(c.replace('CARD.', ''), 0))
for a in arch['archetypes']:
    sp = anc[a['id']]; fw = a['fullWeight']
    mem = [r for r in runs if B.member(r, sp['anchors'], sp.get('min', 2))]
    non = [r for r in runs if not B.member(r, sp['anchors'], sp.get('min', 2))]
    out = '%-24s fw %.1f members %d | commit median mem %.2f non %.2f' % (a['id'], fw, len(mem), wmedian([(r.player, commit(a, r, fw)) for r in mem]), wmedian([(r.player, commit(a, r, fw)) for r in non]))
    for f2 in (2.0, 2.5, 3.0, 3.5, 4.0, 4.5):
        out += ' | fw%.1f: %.2f/%.2f' % (f2, wmedian([(r.player, commit(a, r, f2)) for r in mem]), wmedian([(r.player, commit(a, r, f2)) for r in non]))
    print(out)
    e = wmedian([(r.player, ess(a, r)) for r in mem]); c = wmedian([(r.player, corec(a, r)) for r in mem])
    rng = random.Random(3); by = collections.defaultdict(list)
    for r in mem: by[r.player].append(r)
    pl = sorted(by); same = 0
    for _ in range(200):
        smp = []
        for j in range(len(pl)):
            p = rng.choice(pl); smp += [('%s#%d' % (p, j), ess(a, r)) for r in by[p]]
        same += wmedian(smp) == e
    print('   essential median %s (bootstrap same %.0f%%), core held median %s, current essentialTarget %s' % (e, same / 2, c, a.get('essentialTarget')))
