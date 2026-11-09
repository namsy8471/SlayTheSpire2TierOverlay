"""Step 4a of docs/research/LEARNED_CARD_POLICY_PLAN.md: outcome-based card value from REAL run results (no simulator).

  python -P tools/CardPolicy/pick_lift.py <screens.jsonl> <runs_index.csv> [--pack-model <card-policy.json>] [--out lift.csv]

pick lift (MetaBot style) = win rate of runs that took the card when offered - win rate of runs that were offered it and
did not take it. Only the natural sample (runs_index sample == "recent": 377 wins / 1,088 losses) is used, because the
other wins were added on purpose and would make every pick look like a winner. A run counts once per card (first offer).
Lift is shrunk toward 0 with a beta prior (k pseudo-runs on each side), and a player bootstrap gives a 90 % interval.

This is correlation, not cause: a card picked mostly in strong runs looks good. It is a baseline the simulator value
model has to agree with or explain, and a check of the pick model's per-card values (ranker preference vs outcome).
"""
import argparse
import collections
import csv
import json
import os
import sys

import numpy as np
from scipy.stats import spearmanr

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from data import load_index, load_screens  # noqa: E402


def lift_table(screens, k):
    """(card, act) -> [picked runs, picked wins, passed runs, passed wins] over each run's first offer of the card in that act."""
    first = {}
    for s in screens:
        for j, o in enumerate(s['options']):
            key = (s['run_id'], o['card'], s['act'])
            if key not in first:
                first[key] = (o['card'], s['act'], s['pick'] == j, s['win'], s['player'])
    return first, count(first.values())


def count(rows):
    stats = collections.defaultdict(lambda: [0, 0, 0, 0])
    for card, act, took, win, _ in rows:
        st = stats[(card, act)]
        if took:
            st[0] += 1
            st[1] += win
        else:
            st[2] += 1
            st[3] += win
    return stats


def shrunk_lift(stats, card, base_by_act, k):
    """Lift within each act (runs offered the card in that act all reached it), averaged with the offer counts as weights:
    a card mostly passed in act 3 by runs that were already winning is not compared with act-1 picks."""
    total, weight = 0.0, 0
    for act in (1, 2, 3):
        pn, pw, nn, nw = stats.get((card, act), (0, 0, 0, 0))
        if pn == 0 or nn == 0:
            continue
        b = base_by_act[act]
        total += (pn + nn) * ((pw + k * b) / (pn + k) - (nw + k * b) / (nn + k))
        weight += pn + nn
    return total / weight if weight else 0.0


def totals(stats, card):
    out = [0, 0, 0, 0]
    for act in (1, 2, 3):
        for i, v in enumerate(stats.get((card, act), (0, 0, 0, 0))):
            out[i] += v
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('screens')
    ap.add_argument('index')
    ap.add_argument('--pack-model')
    ap.add_argument('--k', type=float, default=10.0)
    ap.add_argument('--min-runs', type=int, default=40)
    ap.add_argument('--boot', type=int, default=300)
    ap.add_argument('--out')
    args = ap.parse_args()

    screens = [s for s in load_screens(args.screens, load_index(args.index)) if s['sample'] == 'recent']
    runs = {s['run_id']: s['win'] for s in screens}
    base = sum(runs.values()) / len(runs)
    print(f'natural sample: {len(screens)} screens, {len(runs)} runs, win rate {base:.3f}')

    first, stats = lift_table(screens, args.k)
    # Win rate of runs that reached each act (the baseline a card offered in that act is compared to).
    reached = {a: [w for r, w in runs.items() if any(s['run_id'] == r and s['act'] >= a for s in screens)] for a in (1, 2, 3)}
    base_by_act = {a: sum(v) / max(1, len(v)) for a, v in reached.items()}
    print('win rate of runs reaching act 1/2/3: ' + ' / '.join(f'{base_by_act[a]:.3f}' for a in (1, 2, 3)))
    names = sorted({c for c, _ in stats})
    cards = [c for c in names if (t := totals(stats, c))[0] + t[2] >= args.min_runs and t[0] >= 5 and t[2] >= 5]
    lift = {c: shrunk_lift(stats, c, base_by_act, args.k) for c in cards}

    # Player bootstrap: resample players, recount.
    by_player = collections.defaultdict(list)
    for v in first.values():
        by_player[v[4]].append(v)
    players = sorted(by_player)
    rng = np.random.default_rng(0)
    boots = {c: [] for c in cards}
    for _ in range(args.boot):
        st = count(v for p in rng.choice(players, len(players)) for v in by_player[p])
        for c in cards:
            boots[c].append(shrunk_lift(st, c, base_by_act, args.k))
    ci = {c: (np.percentile(boots[c], 5), np.percentile(boots[c], 95)) for c in cards}
    sure = [c for c in cards if ci[c][0] > 0 or ci[c][1] < 0]
    print(f'{len(cards)} cards with >= {args.min_runs} offered runs; {len(sure)} with a 90% interval excluding 0')

    model_value = {}
    if args.pack_model:
        with open(args.pack_model, encoding='utf-8') as f:
            m = json.load(f)
        model_value = {c: e['value'] + float(np.mean(e['act'])) for c, e in m['cards'].items()}
        both = [c for c in cards if c in model_value]
        rho = spearmanr([lift[c] for c in both], [model_value[c] for c in both])[0]
        rho_sure = spearmanr([lift[c] for c in sure if c in model_value], [model_value[c] for c in sure if c in model_value])[0]
        print(f'Spearman(pick lift, pick-model card value): all {rho:+.2f} (n={len(both)}), confident lifts {rho_sure:+.2f} (n={len(sure)})')

    order = sorted(cards, key=lambda c: lift[c])
    for title, part in (('lowest lift', order[:12]), ('highest lift', order[-12:][::-1])):
        print(title)
        for c in part:
            pn, pw, nn, nw = totals(stats, c)
            mv = f'  model {model_value[c]:+.2f}' if c in model_value else ''
            print(f'  {c[5:]:<24} lift {lift[c]:+.3f} [{ci[c][0]:+.3f},{ci[c][1]:+.3f}]  took {pw}/{pn}  passed {nw}/{nn}{mv}')
    if args.out:
        with open(args.out, 'w', encoding='utf-8', newline='') as f:
            w = csv.writer(f)
            w.writerow(['card', 'lift', 'ci5', 'ci95', 'took_runs', 'took_wins', 'passed_runs', 'passed_wins', 'model_value'])
            for c in order:
                w.writerow([c, f'{lift[c]:.4f}', f'{ci[c][0]:.4f}', f'{ci[c][1]:.4f}', *totals(stats, c), f'{model_value.get(c, float("nan")):.4f}'])


if __name__ == '__main__':
    main()
