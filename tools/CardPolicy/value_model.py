"""Step 4c of docs/research/LEARNED_CARD_POLICY_PLAN.md: a smooth model of the simulator's card value.

  python -P tools/CardPolicy/value_model.py <value.csv[,value2.csv]> <screens.jsonl> [--target cost|turns] [--out vm.json]

One simulated screen gives a noisy number per offered card (2 seeds: reliability ~0.35). A ridge regression over many
screens averages that noise out and gives a value for ANY screen, including ones never simulated. The engine's score components (tier base without
baseAdjust, need, build, ...) are shared features, so a card seen on few simulated screens borrows from cards like it:

  g(card | screen) = mu[act] + mu[character] + up * upgraded + sum_k w_k * engine_component_k
                   + v[c] + v2[c] * (act == 2) + vb[c] * (basic share of deck - 0.5) + vs[c] * (deck size - 15) / 10

g is in "cost" units: one lost elite/boss fight = 1, one max-HP worth of damage = 1 (target cost), or in turns saved.
Scored with run-grouped 5-fold CV against the noise ceiling: a perfect model can only correlate sqrt(reliability)
with a 2-seed measurement.
"""
import argparse
import collections
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pick_model import CARD_COMPS, SCALE  # noqa: E402
from sim_value import match_screens, read_value, screen_gains  # noqa: E402

BLOCKS = ('v', 'v2', 'vb', 'vs')
CHARS = ['CHARACTER.IRONCLAD', 'CHARACTER.SILENT', 'CHARACTER.DEFECT', 'CHARACTER.REGENT', 'CHARACTER.NECROBINDER']


def card_id(token):
    return token.split('#', 1)[0].split('+', 1)[0]


def deck_features(s):
    deck = [card_id(t) for t in s['deck']]
    basic = sum(c.startswith(('CARD.STRIKE_', 'CARD.DEFEND_')) for c in deck) / max(1, len(deck))
    return basic - 0.5, (len(deck) - 15) / 10.0


def engine_components(o):
    comp = dict(o['components'])
    comp['base'] = comp.get('base', 0.0) - o.get('base_adjust', 0.0)
    return np.array([comp.get(k, 0.0) * SCALE for k in CARD_COMPS])


def samples(value_paths, screens_path, target):
    data, _, _, card_of = read_value(value_paths)
    screens = match_screens(data, card_of, screens_path)
    field = 0 if target == 'cost' else 4
    out = []
    for key, s, gain, _, _ in screen_gains(data, screens, field):
        half = gain.shape[1] // 2
        for j, o in enumerate(s['options']):
            g = gain[j]
            # split halves by seed index for the reliability estimate (pairs are sorted by (encounter, seed))
            even, odd = g[0::2].mean(), g[1::2].mean()
            out.append({'run': s['run_id'], 'screen': s, 'card': o['card'], 'upgraded': o['upgrade'] > 0, 'option': o,
                        'y': g.mean(), 'y0': even, 'y1': odd, 'pairs': len(g), 'half': half})
    return out


class Ridge:
    def __init__(self, cards):
        self.cards = {c: i for i, c in enumerate(sorted(cards))}
        self.nc = len(self.cards)
        self.fixed = 3 + len(CHARS) + 1 + len(CARD_COMPS)   # act, character, upgraded, engine components
        self.size = self.fixed + len(BLOCKS) * self.nc

    def row(self, s, card, upgraded, option):
        x = np.zeros(self.size)
        a = min(max(s['act'], 1), 3) - 1
        x[a] = 1.0
        if s['character'] in CHARS:
            x[3 + CHARS.index(s['character'])] = 1.0
        x[3 + len(CHARS)] = float(upgraded)
        x[4 + len(CHARS):self.fixed] = engine_components(option)
        i = self.cards.get(card)
        if i is not None:
            basic, size = deck_features(s)
            for b, val in enumerate((1.0, float(a == 1), basic, size)):
                x[self.fixed + b * self.nc + i] = val
        return x

    def fit(self, rows, lam):
        X = np.array([self.row(r['screen'], r['card'], r['upgraded'], r['option']) for r in rows])
        y = np.array([r['y'] for r in rows])
        pen = np.concatenate([np.full(self.fixed, 1e-3)] + [np.full(self.nc, lam * m) for m in (1, 3, 3, 3)])
        self.beta = np.linalg.solve(X.T @ X + np.diag(pen), X.T @ y)
        return self

    def predict(self, s, o):
        return float(self.row(s, o['card'], o['upgrade'] > 0, o) @ self.beta)

    def export(self, lam, target):
        b = self.beta
        cards = {c: [round(float(b[self.fixed + k * self.nc + i]), 6) for k in range(len(BLOCKS))]
                 for c, i in self.cards.items()}
        return {'version': 1, 'target': target, 'lam': lam, 'act': [round(float(v), 6) for v in b[:3]],
                'character': {c: round(float(b[3 + k]), 6) for k, c in enumerate(CHARS)},
                'upgraded': round(float(b[3 + len(CHARS)]), 6),
                'components': dict(zip(CARD_COMPS, [round(float(v), 6) for v in b[4 + len(CHARS):self.fixed]])),
                'blocks': list(BLOCKS), 'cards': cards}


def load(path):
    """Exported model -> predict(screen, option) usable without numpy fitting state."""
    with open(path, encoding='utf-8') as f:
        m = json.load(f)

    def predict(s, o):
        a = min(max(s['act'], 1), 3) - 1
        g = m['act'][a] + m['character'].get(s['character'], 0.0) + m['upgraded'] * (o['upgrade'] > 0)
        g += float(np.dot(list(m['components'].values()), engine_components(o)))
        c = m['cards'].get(o['card'])
        if c:
            basic, size = deck_features(s)
            g += c[0] + c[1] * (a == 1) + c[2] * basic + c[3] * size
        return g
    return predict


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('value')
    ap.add_argument('screens')
    ap.add_argument('--target', default='cost', choices=['cost', 'turns'])
    ap.add_argument('--grid', default='0.3,1,3,10,30')
    ap.add_argument('--folds', type=int, default=5)
    ap.add_argument('--out')
    args = ap.parse_args()

    rows = samples(args.value, args.screens, args.target)
    runs = sorted({r['run'] for r in rows})
    rng = np.random.default_rng(0)
    rng.shuffle(runs)
    fold_of = {r: i % args.folds for i, r in enumerate(runs)}
    fold = np.array([fold_of[r['run']] for r in rows])
    y = np.array([r['y'] for r in rows])
    y0, y1 = np.array([r['y0'] for r in rows]), np.array([r['y1'] for r in rows])
    half_r = np.corrcoef(y0, y1)[0, 1]
    rel = 2 * half_r / (1 + half_r)
    print(f'{len(rows)} (screen, card) samples from {len({id(r["screen"]) for r in rows})} screens, {len(runs)} runs; '
          f'{len({r["card"] for r in rows})} cards; target {args.target}')
    print(f'measurement reliability {rel:.3f} -> best possible correlation with a measurement {np.sqrt(max(rel, 0)):.3f}')

    results = {}
    for lam in [float(x) for x in args.grid.split(',')]:
        pred = np.zeros(len(rows))
        for f in range(args.folds):
            tr = [r for r, k in zip(rows, fold) if k != f]
            model = Ridge({r['card'] for r in tr}).fit(tr, lam)
            for i in np.flatnonzero(fold == f):
                pred[i] = model.predict(rows[i]['screen'], rows[i]['option'])
        r = np.corrcoef(pred, y)[0, 1]
        r2 = 1 - ((y - pred) ** 2).sum() / ((y - y.mean()) ** 2).sum()
        results[lam] = (r, r2, pred)
        print(f'  lam {lam:<5} held-out corr {r:+.3f}  R2 {r2:+.3f}  share of the explainable signal {r * r / max(rel, 1e-9):.2f}')
    lam = max(results, key=lambda k: results[k][0])
    pred = results[lam][2]

    # Within-screen ranking: does the held-out prediction order the offered cards like the measurement does?
    by_screen = collections.defaultdict(list)
    for i, r in enumerate(rows):
        by_screen[id(r['screen'])].append(i)
    agree = [float(np.argmax(pred[idx]) == np.argmax(y[idx])) for idx in by_screen.values() if len(idx) > 1]
    print(f'best lam {lam}: held-out top card matches the measured top card on {np.mean(agree):.3f} of screens')

    if args.out:
        model = Ridge({r['card'] for r in rows}).fit(rows, lam)
        with open(args.out, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(model.export(lam, args.target), f, separators=(',', ':'))
        print(f'wrote {args.out}')


if __name__ == '__main__':
    main()
