"""Step 5 of docs/research/LEARNED_CARD_POLICY_PLAN.md: does the simulator's card value help predict top players?

  python -P tools/CardPolicy/combine.py <screens.jsonl> <runs_index.csv> <value-model.json> [--folds 5] [--dim 8]

Adds one feature to the step-3 pick model: g(card | screen) from value_model.py (the smoothed simulator value; Skip
has 0). Its weight is learned like the engine components. Both models are scored on the same held-out PLAYERS, with a
player bootstrap of the paired difference. The value model never sees ranker picks, only simulated fights, so the
feature carries no label information.
"""
import argparse
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pick_model as pm  # noqa: E402
from data import load_index, load_screens, player_folds  # noqa: E402
from value_model import load as load_value_model  # noqa: E402

LAM = (1.0, 3.0, 10.0)
FEATURE = 'sim_value'


def build(screens, with_sim):
    """pm.Data with or without the sim feature (pm reads CARD_COMPS when building data and models)."""
    base = [k for k in pm.CARD_COMPS if k != FEATURE]
    pm.CARD_COMPS[:] = base + ([FEATURE] if with_sim else [])
    return pm.Data(screens)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('screens')
    ap.add_argument('index')
    ap.add_argument('value_model')
    ap.add_argument('--folds', type=int, default=5)
    ap.add_argument('--dim', type=int, default=8)
    ap.add_argument('--boot', type=int, default=1000)
    args = ap.parse_args()

    screens = load_screens(args.screens, load_index(args.index))
    predict = load_value_model(args.value_model)
    for s in screens:
        for o in s['options']:
            o['components'] = dict(o['components'])
            o['components'][FEATURE] = predict(s, o) / pm.SCALE  # Data multiplies by SCALE again
    players = np.array([s['player'] for s in screens])
    folds = player_folds(screens, args.folds)
    v111 = np.array([s['build_id'].startswith('v0.111') for s in screens])
    print(f'{len(screens)} screens, {len(set(players))} players; sim feature mean {np.mean([o["components"][FEATURE] * pm.SCALE for s in screens for o in s["options"]]):+.3f}')

    res = {}
    weights = []
    for name, with_sim in (('pick model', False), ('+ sim value', True)):
        data = build(screens, with_sim)
        hit, ll = np.zeros(len(screens)), np.zeros(len(screens))
        for f in range(args.folds):
            tr_idx, te_idx = np.flatnonzero(folds != f), np.flatnonzero(folds == f)
            model = pm.Model(data.n_vocab, args.dim)
            th = model.fit(data.subset(tr_idx), LAM, maxiter=1000)
            te = data.subset(te_idx)
            h, l = pm.scores(model.utilities(th, te), te.y)
            hit[te_idx], ll[te_idx] = h, l
            if with_sim:
                weights.append(float(model.unpack(th)['w'][pm.CARD_COMPS.index(FEATURE)]))
        res[name] = (hit, ll)
        print(f'{name:<12} top-1 {hit.mean():.4f}  LL {ll.mean():.4f}  (v0.111 screens: {hit[v111].mean():.4f} / {ll[v111].mean():.4f})')
    print('learned weight on the sim value per fold (utility per 1 cost unit): ' + ', '.join(f'{w:+.2f}' for w in weights))

    dh = res['+ sim value'][0] - res['pick model'][0]
    dl = res['+ sim value'][1] - res['pick model'][1]
    uniq = sorted(set(players))
    by_player = {p: np.flatnonzero(players == p) for p in uniq}
    rng = np.random.default_rng(0)
    bh, bl = [], []
    for _ in range(args.boot):
        idx = np.concatenate([by_player[p] for p in rng.choice(uniq, len(uniq))])
        bh.append(dh[idx].mean())
        bl.append(dl[idx].mean())
    print(f'difference (+ sim − pick model): top-1 {dh.mean() * 100:+.2f} pp [95% {np.percentile(bh, 2.5) * 100:+.2f}, '
          f'{np.percentile(bh, 97.5) * 100:+.2f}], LL {dl.mean():+.4f} [{np.percentile(bl, 2.5):+.4f}, {np.percentile(bl, 97.5):+.4f}]')
    pm.CARD_COMPS[:] = [k for k in pm.CARD_COMPS if k != FEATURE]


if __name__ == '__main__':
    main()
