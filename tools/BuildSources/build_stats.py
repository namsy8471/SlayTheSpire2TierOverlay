"""Build membership / card support statistics from ranker winning decks (v2 rules, one game version).

Usage: python build_stats.py <runs_index.csv> <runs dir> <archetypes json> <cards catalog json> <anchors json> <CHARACTER> <build prefix> [out json]

Reads per-run files (private, never committed) and prints aggregates only.
Rules (docs/research/build-sources/<character>.md, section 2):
  member deck   = winning deck holding >= minAnchors anchor cards (leave-one-out for the card under test)
  player balance = shares are weighted means of per-player shares, one player's weight capped at 10 %
  support       = >= 5 players hold it in member decks, lift >= 1.5, balanced share >= 25 %
                  (or >= 15 % with >= 10 offers in member runs' combat rewards and balanced pick rate >= 50 %)
  core          = support + engine candidate + share >= 40 %
  bootstrap     = players resampled with replacement 200 times
"""
import csv, json, random, re, sys, collections

def load_jsonc(path):
    txt = open(path, encoding='utf-8').read()
    txt = re.sub(r'^\s*//.*$', '', txt, flags=re.M)
    txt = re.sub(r'(?<=[\]\}\",0-9el])\s*//.*$', '', txt, flags=re.M)
    return json.loads(txt)

def capped_weights(counts, cap=0.10):
    """counts: {player: n}. Weight ~ n, but no player above cap of the total (when feasible)."""
    w = {p: float(n) for p, n in counts.items() if n > 0}
    if not w:
        return w
    if len(w) * cap < 1.0:
        return {p: 1.0 for p in w}
    for _ in range(50):
        tot = sum(w.values())
        over = [p for p, v in w.items() if v > cap * tot + 1e-12]
        if not over:
            break
        free = sum(v for p, v in w.items() if p not in over)
        # capped players each get cap share: x = cap * (free + k x) -> x = cap*free/(1-k*cap)
        k = len(over)
        x = cap * free / (1 - k * cap)
        for p in over:
            w[p] = x
    return w

def balanced_share(decks, pred):
    """decks: list of (player, deck). Returns (balanced share, k, n, players holding)."""
    n_p = collections.Counter(); k_p = collections.Counter()
    for pl, d in decks:
        n_p[pl] += 1
        if pred(d):
            k_p[pl] += 1
    w = capped_weights(n_p)
    tot = sum(w.values())
    if tot == 0:
        return 0.0, 0, 0, 0
    s = sum(w[p] * k_p[p] / n_p[p] for p in w) / tot
    return s, sum(k_p.values()), sum(n_p.values()), sum(1 for p in k_p if k_p[p] > 0)

class Run:
    __slots__ = ('id', 'player', 'good', 'cards', 'relics', 'offers', 'sample', 'win')

def load_runs(index, runs_dir, character, build_prefix, wins_only=True):
    out = []
    for x in csv.DictReader(open(index, encoding='utf-8')):
        if x['character'] != character or x['keep'] != '1' or not x['build_id'].startswith(build_prefix):
            continue
        if wins_only and x['win'] != '1':
            continue
        try:
            d = json.load(open('%s/%s.run' % (runs_dir, x['runId']), encoding='utf-8'))
        except FileNotFoundError:
            continue
        r = Run(); r.id = x['runId']; r.player = x['player_key']; r.good = x['good_player'] == '1'
        r.sample = x['sample']; r.win = x['win'] == '1'
        p = d['players'][0]
        r.cards = collections.Counter(c['id'].replace('CARD.', '') for c in p['deck'])
        r.relics = set(c['id'].replace('RELIC.', '') for c in p['relics'])
        r.offers = []  # (card, picked) from combat reward screens
        for act in d.get('map_point_history', []):
            for pt in act:
                if pt.get('map_point_type') not in ('monster', 'elite', 'boss'):
                    continue
                for ps in pt.get('player_stats', []):
                    for ch in ps.get('card_choices', []) or []:
                        r.offers.append((ch['card']['id'].replace('CARD.', ''), bool(ch.get('was_picked'))))
        out.append(r)
    return out

def member(r, anchors, min_anchors, leave=None):
    if len(anchors) <= min_anchors:
        leave = None  # a build defined by one card: membership cannot leave it out
    return sum(1 for a in anchors if a != leave and r.cards.get(a, 0) > 0) >= min_anchors

def card_stats(runs, anchors, min_anchors, card):
    mem = [(r.player, r) for r in runs if member(r, anchors, min_anchors, leave=card)]
    non = [(r.player, r) for r in runs if not member(r, anchors, min_anchors, leave=card)]
    has = lambda r: r.cards.get(card, 0) > 0
    s_m, k_m, n_m, pl_m = balanced_share(mem, has)
    s_n, k_n, n_n, _ = balanced_share(non, has)
    lift = (s_m / s_n) if s_n > 0 else (float('inf') if s_m > 0 else 0.0)
    raw_lift = ((k_m / n_m) / (k_n / n_n)) if n_m and n_n and k_n else (float('inf') if k_m else 0.0)
    # pick rate inside member runs (combat rewards)
    off_p = collections.Counter(); pick_p = collections.Counter()
    for pl, r in mem:
        for c, picked in r.offers:
            if c == card:
                off_p[pl] += 1
                if picked:
                    pick_p[pl] += 1
    w = capped_weights(off_p); tot = sum(w.values())
    pick = (sum(w[p] * pick_p[p] / off_p[p] for p in w) / tot) if tot else 0.0
    offers = sum(off_p.values())
    return dict(share=s_m, k=k_m, n=n_m, players=pl_m, non_share=s_n, lift=lift, raw_lift=raw_lift,
                pick=pick, offers=offers, picks=sum(pick_p.values()))

def supported(st):
    if st['players'] < 5 or st['lift'] < 1.5:
        return False
    return st['share'] >= 0.25 or (st['share'] >= 0.15 and st['offers'] >= 10 and st['pick'] >= 0.5)

def is_core(st):
    return supported(st) and st['share'] >= 0.40

def bootstrap(runs, anchors, min_anchors, cards, n=200, seed=7):
    rng = random.Random(seed)
    by_p = collections.defaultdict(list)
    for r in runs:
        by_p[r.player].append(r)
    players = sorted(by_p)
    sup = collections.Counter(); core = collections.Counter(); lift1 = collections.Counter(); below = collections.Counter()
    for _ in range(n):
        sample = []
        for j in range(len(players)):
            p = rng.choice(players)
            for r in by_p[p]:
                c = Run(); c.id = r.id; c.player = '%s#%d' % (p, j); c.good = r.good
                c.cards = r.cards; c.relics = r.relics; c.offers = r.offers; c.sample = r.sample; c.win = r.win
                sample.append(c)
        for card in cards:
            st = card_stats(sample, anchors, min_anchors, card)
            if supported(st): sup[card] += 1
            if is_core(st): core[card] += 1
            if st['lift'] >= 1.0: lift1[card] += 1
            if st['lift'] < 1.0: below[card] += 1
    return {c: dict(sup=sup[c] / n, core=core[c] / n, lift1=lift1[c] / n) for c in cards}

def main():
    index, runs_dir, arch_path, cat_path, anchors_path, character, prefix = sys.argv[1:8]
    out_path = sys.argv[8] if len(sys.argv) > 8 else None
    arch = load_jsonc(arch_path)
    cat = json.load(open(cat_path, encoding='utf-8'))['cards']
    anchors_all = json.load(open(anchors_path, encoding='utf-8'))
    runs = load_runs(index, runs_dir, character, prefix)
    rar = {}
    for c in (cat.values() if isinstance(cat, dict) else cat):
        if isinstance(c, dict) and 'id' in c:
            rar[c['id'].replace('CARD.', '')] = str(c.get('rarity', '?'))
    good = [r for r in runs if r.good]
    print('# %s %s wins: all %d runs / %d players, good %d runs / %d players' % (
        character, prefix, len(runs), len(set(r.player for r in runs)), len(good), len(set(r.player for r in good))))
    pool = collections.Counter()
    for r in runs:
        for c in r.cards: pool[c] += 1
    result = {}
    for a in arch['archetypes']:
        aid = a['id']
        spec = anchors_all[aid]
        anchors, min_a = spec['anchors'], spec.get('min', 2)
        roles = {}
        for role in ('core', 'enabler', 'payoff', 'support'):
            for c in a['cards'].get(role, []):
                roles[c.replace('CARD.', '')] = role
        for c in a.get('onTheme', []):
            roles.setdefault(c.replace('CARD.', ''), 'onTheme')
        res = {}
        for label, rs in (('good', good), ('all', runs)):
            mem = [r for r in rs if member(r, anchors, min_a)]
            pc = collections.Counter(r.player for r in mem)
            # candidates: role cards, anchors, and any card held in >= 15 % of member decks (raw)
            cands = set(roles) | set(anchors)
            cnt = collections.Counter()
            for r in mem:
                for c in r.cards: cnt[c] += 1
            for c, k in cnt.items():
                if mem and k / len(mem) >= 0.15 and k >= 4:
                    cands.add(c)
            cands = sorted(c for c in cands if not c.startswith('STRIKE_') and not c.startswith('DEFEND_'))
            stats = {c: card_stats(rs, anchors, min_a, c) for c in cands}
            boot = bootstrap(rs, anchors, min_a, cands)
            res[label] = dict(members=len(mem), players=len(pc), top_player_share=(max(pc.values()) / len(mem) if mem else 0),
                              cards={c: dict(stats[c], **boot[c]) for c in cands})
        result[aid] = dict(anchors=anchors, min=min_a, roles=roles, **res)
        g, al = res['good'], res['all']
        print('\n## %s  anchors=%s (min %d)' % (aid, ','.join(anchors), min_a))
        print('members good %d runs / %d players (top %.0f%%), all %d runs / %d players' % (
            g['members'], g['players'], 100 * g['top_player_share'], al['members'], al['players']))
        print('%-22s %-8s | good: share  k/n   lift  pl  pick(off)  sup%% core%% | all: share  k/n   lift  pl  sup%% core%%' % ('card', 'role'))
        keys = sorted(set(g['cards']) | set(al['cards']), key=lambda c: -(al['cards'].get(c, {}).get('lift', 0) if al['cards'].get(c, {}).get('lift', 0) != float('inf') else 99))
        for c in keys:
            sg = g['cards'].get(c); sa = al['cards'].get(c)
            def fmt(s, pick=True):
                if not s: return ' ' * 40
                l = '  inf' if s['lift'] == float('inf') else '%5.2f' % s['lift']
                t = '%4.0f%% %3d/%-3d %s %3d' % (100 * s['share'], s['k'], s['n'], l, s['players'])
                if pick: t += ' %4.0f%%(%3d)' % (100 * s['pick'], s['offers'])
                t += ' %4.0f %4.0f' % (100 * s['sup'], 100 * s['core'])
                return t
            rarity = rar.get(c, '?')
            print('%-22s %-8s | %s | %s  %s' % (c, roles.get(c, '-') + ('*' if c in anchors else ''), fmt(sg), fmt(sa, False), rarity))
    if out_path:
        def clean(o):
            if isinstance(o, dict): return {k: clean(v) for k, v in o.items()}
            if isinstance(o, float) and o == float('inf'): return 'inf'
            return o
        json.dump(clean(result), open(out_path, 'w', encoding='utf-8'), indent=1)

if __name__ == '__main__':
    main()
