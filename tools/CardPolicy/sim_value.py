"""Step 4b of docs/research/LEARNED_CARD_POLICY_PLAN.md: read the simulator value data and check it against the rankers.

  python -P tools/CardPolicy/sim_value.py <value.csv> <screens.jsonl> [--pack-model card-policy.json] [--lift pick_lift.csv]

value.csv comes from the offline sts2-sim harness (scratchpad SimValue, never shipped): for each sampled reward screen
the deck before the screen ("SKIP", variant -1) and deck + each offered card fight every elite and boss of that act with
shared seeds. Per combat cost = damage / max HP, plus 1 if the fight was not won (death or 30-turn timeout), so a
loss always costs more than any win. A card's sim value on a screen = cost(skip) - cost(with the card), averaged over
the (encounter, seed) pairs: positive means the card made the act's hardest fights cheaper.
"""
import argparse
import collections
import csv
import json
import os
import sys

import numpy as np
from scipy.optimize import minimize_scalar
from scipy.stats import spearmanr

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pick_model import CARD_COMPS, SCALE, SKIP_COMPS  # noqa: E402


def read_value(path):
    """(run_id, node_index) -> {variant: {(encounter, seed): (cost, won, damage)}}, plus screen metadata."""
    data = collections.defaultdict(lambda: collections.defaultdict(dict))
    meta = {}
    card_of = collections.defaultdict(dict)
    errors = 0
    with open(path, encoding='utf-8') as f:
        for r in csv.DictReader(f):
            if r['error'] or r['won'] == '':
                errors += 1
                continue
            key = (r['run_id'], int(r['node_index']), int(r['group']) if r.get('group') else None)
            won = r['won'] == '1'
            dmg = float(r['damage'])
            cost = dmg / float(r['max_hp']) + (0.0 if won else 1.0)
            data[key][int(r['variant'])][(r['encounter'], r['seed'])] = (cost, won, dmg, r['room_type'])
            meta[key] = (int(r['act']), int(r['pick']), r['character'])
            if int(r['variant']) >= 0:
                card_of[key][int(r['variant'])] = r['card']
    return data, meta, errors, card_of


def model_utilities(m, s):
    """The shipped pick model (CardPolicyModel.cs) in Python: utilities for [skip, options...]."""
    a = min(max(s['act'], 1), 3) - 1
    deck = [t.split('#')[0].split('+')[0] for t in s['deck']]
    d = np.zeros(m['dim'])
    for c in deck:
        if c in m['cards']:
            d += np.array(m['cards'][c]['b'])
    if deck:
        d /= len(deck)
    sk = s['skip_components']
    u = [m['scale'] * sum(w * sk.get(k, 0.0) for k, w in zip(m['skipComponents'], m['skipWeights']))
         + m['skipAct'][a] + float(np.dot(m['skipA'], d))]
    for o in s['options']:
        comps = dict(o['components'])
        comps['base'] = comps.get('base', 0.0) - o.get('base_adjust', 0.0)
        v = m['scale'] * sum(w * comps.get(k, 0.0) for k, w in zip(m['cardComponents'], m['cardWeights']))
        v += m['upgradedWeight'] * (o['upgrade'] > 0) + m['copiesWeight'] * min(o['copies'], m['copiesCap'])
        c = m['cards'].get(o['card'])
        if c:
            v += c['value'] + c['act'][a] + float(np.dot(c['a'], d))
        u.append(v)
    return np.array(u)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('value')
    ap.add_argument('screens')
    ap.add_argument('--pack-model')
    ap.add_argument('--lift')
    ap.add_argument('--boot', type=int, default=1000)
    args = ap.parse_args()

    data, meta, errors, card_of = read_value(args.value)
    # A node can hold several reward screens (e.g. two elite rewards): match on the group when the value file has it,
    # else on the offered cards.
    wanted = collections.defaultdict(list)
    for key in data:
        wanted[key[:2]].append(key)
    screens = {}
    with open(args.screens, encoding='utf-8') as f:
        for line in f:
            s = json.loads(line)
            for key in wanted.get((s['run_id'], s['node_index']), []):
                cards = sorted(o['card'] + (f"+{o['upgrade']}" if o['upgrade'] > 0 else '') for o in s['options'])
                sim_cards = sorted(card_of[key][v] for v in range(len(card_of[key])) if v in card_of[key])
                if (key[2] is None or key[2] == s['group']) and cards == sim_cards:
                    screens[key] = s
    model = None
    if args.pack_model:
        with open(args.pack_model, encoding='utf-8') as f:
            model = json.load(f)

    rows = []          # one per screen
    card_gain = collections.defaultdict(list)
    room_gain = collections.defaultdict(list)
    for key, variants in data.items():
        s = screens.get(key)
        if s is None or -1 not in variants:
            continue
        n = len(s['options'])
        pairs = set(variants[-1])
        for v in range(n):
            pairs &= set(variants.get(v, {}))
        if not pairs or any(v not in variants for v in range(n)):
            continue
        pairs = sorted(pairs)
        skip_cost = np.array([variants[-1][p][0] for p in pairs])
        gain = np.array([[skip_cost[i] - variants[v][p][0] for i, p in enumerate(pairs)] for v in range(n)])  # n x pairs
        mean_gain = gain.mean(axis=1)
        for v in range(n):
            card_gain[s['options'][v]['card']].append(mean_gain[v])
            for i, p in enumerate(pairs):
                room_gain[variants[v][p][3]].append(gain[v, i])
        row = {'key': key, 'act': s['act'], 'pick': s['pick'], 'n': n, 'gain': mean_gain,
               'skip_cost': skip_cost.mean(), 'engine_best': s.get('engine_best', -1)}
        if model:
            u = model_utilities(model, s)
            row['model_top'] = int(np.argmax(u)) - 1  # -1 = skip
            row['model_card_top'] = int(np.argmax(u[1:]))
            row['u'] = u
        rows.append(row)

    print(f'value rows with errors/timeouts dropped: {errors}')
    print(f'screens analysed: {len(rows)} (act 1: {sum(r["act"] == 1 for r in rows)}, act 2: {sum(r["act"] == 2 for r in rows)})')
    all_gain = np.concatenate([r['gain'] for r in rows])
    print(f'mean sim gain of adding an offered card: {all_gain.mean():+.3f} cost '
          f'(share of offered cards that help: {(all_gain > 0).mean():.2f})')
    for room, g in sorted(room_gain.items()):
        print(f'  {room:<6} mean gain per combat {np.mean(g):+.3f} (n={len(g)})')
    print(f'mean skip-deck cost per elite/boss fight: {np.mean([r["skip_cost"] for r in rows]):.3f}')

    picked = [r for r in rows if r['pick'] >= 0]
    skipped = [r for r in rows if r['pick'] < 0]

    def agree(rs, choice):
        hits = [float(choice(r) == int(np.argmax(r['gain']))) for r in rs]
        return np.mean(hits)

    chance = np.mean([1.0 / r['n'] for r in picked])
    rng = np.random.default_rng(0)

    def boot(vals):
        vals = np.asarray(vals)
        b = [vals[rng.integers(0, len(vals), len(vals))].mean() for _ in range(args.boot)]
        return np.percentile(b, 5), np.percentile(b, 95)

    print(f'\nscreens where the ranker took a card: {len(picked)}; skipped: {len(skipped)}')
    print('"sim-best card" = the offered card with the largest sim gain. How often does each chooser take it?')
    hits = [float(r['pick'] == int(np.argmax(r['gain']))) for r in picked]
    lo, hi = boot(hits)
    print(f'  ranker pick      {np.mean(hits):.3f}  [90% {lo:.3f}, {hi:.3f}]   (chance {chance:.3f})')
    eng = [r for r in picked if r['engine_best'] >= 0]
    print(f'  engine best      {agree(eng, lambda r: r["engine_best"]):.3f}  (n={len(eng)})')
    if model:
        print(f'  learned model    {agree(picked, lambda r: r["model_card_top"]):.3f}')
    # Gain of the picked card vs the other offered cards on the same screen (paired).
    diff = [r['gain'][r['pick']] - np.mean(np.delete(r['gain'], r['pick'])) for r in picked if r['n'] > 1]
    lo, hi = boot(diff)
    print(f'picked card gain minus mean of the passed cards: {np.mean(diff):+.4f} [90% {lo:+.4f}, {hi:+.4f}]')
    if skipped:
        print(f'when rankers skip, best offered gain {np.mean([r["gain"].max() for r in skipped]):+.3f} '
              f'vs {np.mean([r["gain"].max() for r in picked]):+.3f} when they pick')

    if model:
        # Does sim gain explain ranker picks beyond the pick model? u + beta * gain (skip gain = 0), one softmax per
        # screen. The pick model was trained on these screens too, so this is the hard case for the sim term.
        def nll(b, with_model=True):
            t = 0.0
            for r in rows:
                z = b * np.concatenate([[0.0], r['gain']]) + (r['u'] if with_model else 0.0)
                z = z - z.max()
                t -= z[r['pick'] + 1] - np.log(np.exp(z).sum())
            return t
        fit = minimize_scalar(nll, bounds=(-20, 40), method='bounded')
        alone = minimize_scalar(lambda b: nll(b, False), bounds=(-20, 40), method='bounded')
        n = len(rows)
        print(f'pick model LL/screen {-nll(0.0) / n:.4f}; + beta*sim gain: beta {fit.x:.2f}, LL/screen {-fit.fun / n:.4f}, '
              f'LR {2 * (nll(0.0) - fit.fun):.1f} (chi2_1 5% = 3.84)')
        print(f'sim gain alone: beta {alone.x:.2f}, LL/screen {-alone.fun / n:.4f} vs uniform {-nll(0.0, False) / n:.4f}')

    # Per-card sim value vs ranker preference and real outcome lift.
    cards = {c: np.mean(g) for c, g in card_gain.items() if len(g) >= 5}
    print(f'\ncards with >= 5 sim screens: {len(cards)}')
    if model:
        mv = {c: e['value'] + float(np.mean(e['act'])) for c, e in model['cards'].items()}
        both = [c for c in cards if c in mv]
        print(f'Spearman(sim card value, pick-model card value) = {spearmanr([cards[c] for c in both], [mv[c] for c in both])[0]:+.2f} (n={len(both)})')
    if args.lift:
        with open(args.lift, encoding='utf-8') as f:
            lift = {r['card']: float(r['lift']) for r in csv.DictReader(f)}
        both = [c for c in cards if c in lift]
        if len(both) > 5:
            print(f'Spearman(sim card value, real pick lift)  = {spearmanr([cards[c] for c in both], [lift[c] for c in both])[0]:+.2f} (n={len(both)})')
    order = sorted(cards, key=cards.get)
    print('lowest sim value: ' + ', '.join(f'{c[5:]} {cards[c]:+.2f}' for c in order[:8]))
    print('highest sim value: ' + ', '.join(f'{c[5:]} {cards[c]:+.2f}' for c in order[-8:][::-1]))


if __name__ == '__main__':
    main()
