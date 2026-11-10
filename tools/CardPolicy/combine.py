"""Step 5 of docs/research/LEARNED_CARD_POLICY_PLAN.md: does the simulator's card value help predict top players?

  python -P tools/CardPolicy/combine.py <screens.jsonl> <runs_index.csv> <value-model.json[,more.json]> [--folds 5]

Adds one feature per value model to the step-3 pick model: g(card | screen) from value_model.py (the smoothed
simulator value, e.g. cost and turns; Skip has 0). Its weight is learned like the engine components. Both models are
scored on the same held-out PLAYERS, with a player bootstrap of the paired difference. The value models never see
ranker picks, only simulated fights, so the features carry no label information.
"""
import argparse
import os
import sys
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pick_model as pm  # noqa: E402
from data import load_index, load_screens, player_folds  # noqa: E402
from value_model import load as load_value_model  # noqa: E402

LAM = (1.0, 3.0, 10.0)
BASE_COMPS = list(pm.CARD_COMPS)


def progress(done, total, start, what):
    """'progress 3/10 (30%) elapsed 2m10s eta 15:42:01' on stderr, so a long run can be followed."""
    elapsed = time.time() - start
    eta = time.strftime('%H:%M:%S', time.localtime(start + elapsed / done * total)) if done else '?'
    print(f'progress {done}/{total} ({100 * done / total:.0f}%) elapsed {int(elapsed // 60)}m{int(elapsed % 60):02d}s '
          f'eta {eta}  [{what}]', file=sys.stderr, flush=True)


def build(screens, features):
    """pm.Data with the given extra features (pm reads CARD_COMPS when building data and models)."""
    pm.CARD_COMPS[:] = BASE_COMPS + list(features)
    return pm.Data(screens)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('screens')
    ap.add_argument('index')
    ap.add_argument('value_models')
    ap.add_argument('--folds', type=int, default=5)
    ap.add_argument('--dim', type=int, default=8)
    ap.add_argument('--boot', type=int, default=1000)
    args = ap.parse_args()

    screens = load_screens(args.screens, load_index(args.index))
    paths = args.value_models.split(',')
    features = [f'sim_{os.path.splitext(os.path.basename(p))[0]}' for p in paths]
    for feat, path in zip(features, paths):
        predict = load_value_model(path)
        for s in screens:
            for o in s['options']:
                o['components'] = dict(o['components'])
                o['components'][feat] = predict(s, o) / pm.SCALE  # Data multiplies by SCALE again
    players = np.array([s['player'] for s in screens])
    folds = player_folds(screens, args.folds)
    v111 = np.array([s['build_id'].startswith('v0.111') for s in screens])
    print(f'{len(screens)} screens, {len(set(players))} players; features {features}')

    res = {}
    weights = []
    fits, n_fits, start = 0, 2 * args.folds, time.time()
    for name, feats in (('pick model', []), ('+ sim value', features)):
        data = build(screens, feats)
        hit, ll = np.zeros(len(screens)), np.zeros(len(screens))
        for f in range(args.folds):
            progress(fits, n_fits, start, f'{name} fold {f + 1}/{args.folds}')
            fits += 1
            tr_idx, te_idx = np.flatnonzero(folds != f), np.flatnonzero(folds == f)
            model = pm.Model(data.n_vocab, args.dim)
            th = model.fit(data.subset(tr_idx), LAM, maxiter=1000)
            te = data.subset(te_idx)
            h, l = pm.scores(model.utilities(th, te), te.y)
            hit[te_idx], ll[te_idx] = h, l
            if feats:
                w = model.unpack(th)['w']
                weights.append('/'.join(f'{w[pm.CARD_COMPS.index(k)]:+.2f}' for k in feats))
        res[name] = (hit, ll)
        progress(fits, n_fits, start, f'{name} done')
        print(f'{name:<12} top-1 {hit.mean():.4f}  LL {ll.mean():.4f}  (v0.111 screens: {hit[v111].mean():.4f} / {ll[v111].mean():.4f})')
    print(f'learned weights on {"/".join(features)} per fold (utility per unit): ' + ', '.join(weights))

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
    print(f'difference (+ sim - pick model): top-1 {dh.mean() * 100:+.2f} pp [95% {np.percentile(bh, 2.5) * 100:+.2f}, '
          f'{np.percentile(bh, 97.5) * 100:+.2f}], LL {dl.mean():+.4f} [{np.percentile(bl, 2.5):+.4f}, {np.percentile(bl, 97.5):+.4f}]')
    pm.CARD_COMPS[:] = BASE_COMPS


if __name__ == '__main__':
    main()
