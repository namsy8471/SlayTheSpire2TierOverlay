"""Re-rank tiers by good players' combat-reward pick rate (v2 method) with a DIRECTIONAL bootstrap rule.
usage: tier_rerank.py cards.csv runs_index.csv tiersdir out.json [--boot 200] [--versions v0.107]
Rule (v2): eligible = character non-basic/ancient cards, offered >= 30, Kish n_eff >= 20; balanced pick rate (one player <= 10%
of a card's weight); rank within character; cut by the current tier counts among eligible cards (S counted with A);
S if rate >= 80%.
New (directional): P(grade > current) >= 0.8 -> 20% quantile grade; P(grade < current) >= 0.8 -> 80% quantile grade; else keep.
"""
import csv, json, re, random, argparse, collections, os

ap = argparse.ArgumentParser()
ap.add_argument('cards'); ap.add_argument('index'); ap.add_argument('tiers'); ap.add_argument('out')
ap.add_argument('--boot', type=int, default=200); ap.add_argument('--versions', default='')
ap.add_argument('--all-players', action='store_true')
ap.add_argument('--status', default='')   # v2status.json: changed/confirmed -> 95% rule, else 80%
ap.add_argument('--patched', default='')  # patched.json: judge these on v0.107.x runs only
a = ap.parse_args()
ORD = 'DCBAS'
idx = {r['runId']: r for r in csv.DictReader(open(a.index, encoding='utf-8'))}
good = {r['player_key'] for r in idx.values() if r.get('good_player') == '1' and r['player_key']}
files = {'CHARACTER.IRONCLAD': 'ironclad', 'CHARACTER.SILENT': 'silent', 'CHARACTER.DEFECT': 'defect',
         'CHARACTER.NECROBINDER': 'necrobinder', 'CHARACTER.REGENT': 'regent'}
cur = {}
for ch, f in files.items():
    for ln in open(os.path.join(a.tiers, f + '.json'), encoding='utf-8'):
        m = re.match(r'\s*"(CARD\.\w+)": \{"tier": "(\w)"', ln)
        if m:
            rar = re.search(r'"rarity": "(\w+)"', ln)
            cur[(ch, m.group(1))] = (m.group(2), rar.group(1) if rar else '')

status = json.load(open(a.status)) if a.status else {}
patched = set(json.load(open(a.patched))) if a.patched else set()
offers = collections.defaultdict(dict)   # (ch, card) -> player -> [offered, picked]
runs_used = set()
for r in csv.DictReader(open(a.cards, encoding='utf-8')):
    if r['context'] not in ('CardReward', 'EliteReward', 'BossReward'):
        continue
    meta = idx.get(r['run'])
    if not meta or not meta['player_key'] or meta.get('keep', '1') != '1':
        continue
    if a.versions and not meta['build_id'].startswith(a.versions):
        continue
    if f"{r['character']}|{r['card']}" in patched and not meta['build_id'].startswith('v0.107'):
        continue
    p = meta['player_key']
    if not a.all_players and p not in good:
        continue
    x = offers[(r['character'], r['card'])].setdefault(p, [0, 0])
    x[0] += 1; x[1] += int(r['ranker_pick'])
    runs_used.add(r['run'])


def rate(pp):
    """pp: key -> [offered, picked]. Balanced pick rate (no key > 10% of weight when > 10 keys) and Kish n_eff."""
    if not pp:
        return None, 0, 0
    w = {p: 1.0 for p in pp}
    for _ in range(10):
        W = sum(w[p] * pp[p][0] for p in pp)
        changed = False
        for p in pp:
            share = w[p] * pp[p][0] / W
            if share > 0.1 + 1e-9 and len(pp) > 10:
                w[p] *= 0.1 / share; changed = True
        if not changed:
            break
    W = sum(w[p] * pp[p][0] for p in pp)
    r_ = sum(w[p] * pp[p][1] for p in pp) / W
    neff = W ** 2 / sum(w[p] ** 2 * pp[p][0] for p in pp)
    return r_, sum(v[0] for v in pp.values()), neff


def grade_all(ch, view):
    """view(pp) -> resampled pp. Returns card -> (grade, rate, rank, eligible, n, neff)."""
    rows = []
    for (c2, card), pp in offers.items():
        if c2 != ch or (ch, card) not in cur:
            continue
        t, rar = cur[(ch, card)]
        if rar in ('basic', 'ancient'):
            continue
        r_, n, ne = rate(view(pp))
        if r_ is None or n < 30 or ne < 20:
            continue
        rows.append((r_, card, t, n, ne))
    rows.sort(reverse=True)
    cnt = collections.Counter('A' if t == 'S' else t for _, _, t, _, _ in rows)
    cut = []
    for g in 'ABCD':
        cut += [g] * cnt.get(g, 0)
    res = {}
    for i, (r_, card, t, n, ne) in enumerate(rows):
        g = cut[i] if i < len(cut) else 'D'
        if r_ >= 0.8:
            g = 'S'
        res[card] = (g, r_, i + 1, len(rows), n, ne)
    return res


players_by_char = collections.defaultdict(set)
for (ch, card), pp in offers.items():
    players_by_char[ch].update(pp)
random.seed(2026)
out = {}
for ch in files:
    base = grade_all(ch, lambda pp: pp)
    pl = sorted(players_by_char[ch])
    boots = collections.defaultdict(list)
    for b in range(a.boot):
        draw = [random.choice(pl) for _ in pl]
        mult = collections.Counter(draw)

        def view(pp, mult=mult):
            v = {}
            for p, k in mult.items():
                if p in pp:
                    for j in range(k):
                        v[(p, j)] = pp[p]
            return v
        for card, (g, *_rest) in grade_all(ch, view).items():
            boots[card].append(g)
    for card, (g, r_, rank, N, n, ne) in base.items():
        t = cur[(ch, card)][0]
        bs = boots.get(card, [])
        nb = max(1, len(bs))
        dist = collections.Counter(bs)
        up = sum(v for k, v in dist.items() if ORD.index(k) > ORD.index(t)) / nb
        down = sum(v for k, v in dist.items() if ORD.index(k) < ORD.index(t)) / nb
        srt = sorted(bs, key=ORD.index)
        new = t
        st = status.get(f'{ch}|{card}', 'other')
        th = 0.95 if st in ('changed', 'confirmed') else 0.8
        q = 1 - th
        if srt and up >= th:
            new = srt[int(q * len(srt))]
        elif srt and down >= th:
            new = srt[max(0, int(th * len(srt)) - 1)]
        mode, modec = dist.most_common(1)[0] if dist else (g, 0)
        out[f'{ch}|{card}'] = dict(character=ch, card=card, current=t, measured=g, rate=r_, rank=rank, eligible=N, n=n, neff=ne,
                                   boot={k: v / nb for k, v in dist.items()}, inBoot=len(bs) / a.boot, up=up, down=down, new=new,
                                   v2rule=(mode if modec / nb >= 0.8 else t), v2status=st, patched=f'{ch}|{card}' in patched)
json.dump(out, open(a.out, 'w'), indent=1)
print('runs used', len(runs_used), 'good players', len(good), 'eligible cards',
      dict(collections.Counter(v['character'][10:] for v in out.values())))
chg = [v for v in out.values() if v['new'] != v['current']]
print('directional changes', len(chg))
for v in sorted(chg, key=lambda v: (v['character'], v['rank'])):
    print(f"{v['character'][10:]:12} {v['card'][5:]:22} {v['current']}->{v['new']} rank {v['rank']}/{v['eligible']} "
          f"rate {v['rate']:.0%} n {v['n']} neff {v['neff']:.0f} boot {dict(sorted(v['boot'].items()))} v2 {v['v2status']}{' patched' if v['patched'] else ''}")
