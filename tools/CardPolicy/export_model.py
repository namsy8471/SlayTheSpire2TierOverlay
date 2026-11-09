"""Fit the step-3 pick model on ALL ranker screens and write it into the data pack for the overlay (plan step 6).

  python -P tools/CardPolicy/export_model.py <screens.jsonl> <runs_index.csv> <pack-dir> [--dim 8] [--fixture <file.json>]

Writes <pack-dir>/model/card-policy.json (ids and numbers only). --fixture also writes N screens with the utilities this
Python model gives them, for the C# parity test (tests/Sts2Overlay.Core.Tests/Learning/CardPolicyModelTests.cs).
"""
import argparse
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from data import load_index, load_screens  # noqa: E402
from pick_model import CARD_COMPS, SCALE, SKIP_COMPS, Data, Model  # noqa: E402

LAM = (1.0, 3.0, 10.0)  # the setting most outer folds chose in step 3


def r(x):
    return [round(float(v), 6) for v in np.ravel(x)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('screens')
    ap.add_argument('index')
    ap.add_argument('pack')
    ap.add_argument('--dim', type=int, default=8)
    ap.add_argument('--fixture')
    ap.add_argument('--fixture-count', type=int, default=200)
    args = ap.parse_args()

    screens = load_screens(args.screens, load_index(args.index))
    data = Data(screens)
    model = Model(data.n_vocab, args.dim)
    theta = model.fit(data, LAM, maxiter=1000)
    p = model.unpack(theta)
    inv = {i: c for c, i in data.vocab.items()}
    counts = np.bincount(data.cards[data.cards >= 0], minlength=data.n_vocab)

    out = {
        'version': 1,
        'description': 'Learned card pick model (docs/research/LEARNED_CARD_POLICY_PLAN.md 5.4). '
                       'u(card) = scale*(w . comps) + value + actValue[act] + a . mean(b over deck); u(skip) likewise.',
        'trainedOn': f'{len(screens)} OP.GG A10 ranker reward screens, {len({s["player"] for s in screens})} players',
        'scale': SCALE,
        'copiesCap': 3,
        'cardComponents': CARD_COMPS,
        'cardWeights': r(p['w'][:len(CARD_COMPS)]),
        'upgradedWeight': round(float(p['w'][-2]), 6),
        'copiesWeight': round(float(p['w'][-1]), 6),
        'skipComponents': SKIP_COMPS,
        'skipWeights': r(p['vs']),
        'skipAct': r(p['s']),
        'dim': args.dim,
        'skipA': r(p['A'][model.v]),
        'cards': {inv[i]: {'n': int(counts[i]), 'value': round(float(p['delta'][i]), 6), 'act': r(p['dact'][i]),
                           'a': r(p['A'][i]), 'b': r(p['B'][i])} for i in range(data.n_vocab)},
    }
    os.makedirs(os.path.join(args.pack, 'model'), exist_ok=True)
    path = os.path.join(args.pack, 'model', 'card-policy.json')
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, separators=(',', ':'))
    print(f'wrote {path} ({os.path.getsize(path) // 1024} KB), vocab {data.n_vocab}')

    if args.fixture:
        u = model.utilities(theta, data)
        rng = np.random.default_rng(0)
        pick = rng.choice(len(screens), args.fixture_count, replace=False)
        rows = []
        for i in pick:
            s = screens[i]
            rows.append({
                'act': s['act'],
                'deck': s['deck'],
                'options': [{'card': o['card'], 'upgrade': o['upgrade'], 'copies': o['copies'], 'components': o['components'],
                             'baseAdjust': o.get('base_adjust', 0.0)} for o in s['options']],
                'skipComponents': s['skip_components'],
                'utilities': r(u[i, :len(s['options']) + 1]),
            })
        with open(args.fixture, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(rows, f, separators=(',', ':'))
        print(f'wrote {args.fixture} ({len(rows)} screens)')


if __name__ == '__main__':
    main()
