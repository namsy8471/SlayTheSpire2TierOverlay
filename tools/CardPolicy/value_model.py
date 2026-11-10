"""Step 4c of docs/research/LEARNED_CARD_POLICY_PLAN.md: a smooth model of the simulator's card value.

  python -P tools/CardPolicy/value_model.py <value.csv[,value2.csv]> <screens.jsonl> [--target cost|turns] [--out vm.json]

One simulated screen gives a noisy number per offered card (2 seeds: reliability ~0.35). A ridge regression over many
screens averages that noise out and gives a value for ANY screen, including ones never simulated:

  g(card | screen) = w . f(card, deck) + v[c] + v2[c] * (act == 2)

f: act, character, upgraded, the engine components (tier base without baseAdjust, need, build, ...), the deck (size,
basic share, attack share, powers, block-card share) and the card's catalog type / cost / rarity / block crossed with
the deck. v are per-card terms (L2 shrunk), so a card seen on few simulated screens leans on cards like it.

g is in "cost" units (one lost elite/boss fight = 1, one max-HP worth of damage = 1) or in turns saved. What matters for
a pick is the difference between the offered cards on one screen, so the model is also scored within screens (both
sides centred per screen), each against its own noise ceiling: a perfect model can only correlate sqrt(reliability)
with a 2-seed measurement. Folds are by run.
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

BLOCKS = ('v', 'v2')
TYPES = ('Attack', 'Skill', 'Power')
CHARS = ['CHARACTER.IRONCLAD', 'CHARACTER.SILENT', 'CHARACTER.DEFECT', 'CHARACTER.REGENT', 'CHARACTER.NECROBINDER']
CATALOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'data', 'catalog', 'cards.v0.107.1.json')
with open(CATALOG, encoding='utf-8') as _f:
    CARDS = {c['id']: c for c in json.load(_f)['cards']}


def card_id(token):
    return token.split('#', 1)[0].split('+', 1)[0]


def deck_features(s):
    """(basic share - 0.5, (size - 15) / 10, attack share - 0.4, powers / 5, block-card share - 0.3)."""
    deck = [card_id(t) for t in s['deck']]
    n = max(1, len(deck))
    info = [CARDS.get(c, {}) for c in deck]
    basic = sum(c.startswith(('CARD.STRIKE_', 'CARD.DEFEND_')) for c in deck) / n
    attack = sum(i.get('type') == 'Attack' for i in info) / n
    powers = sum(i.get('type') == 'Power' for i in info)
    block = sum(bool(i.get('gainsBlock')) for i in info) / n
    return basic - 0.5, (len(deck) - 15) / 10.0, attack - 0.4, powers / 5.0, block - 0.3


def engine_components(o):
    comp = dict(o['components'])
    comp['base'] = comp.get('base', 0.0) - o.get('base_adjust', 0.0)
    return np.array([comp.get(k, 0.0) * SCALE for k in CARD_COMPS])


def shared_features(s, o):
    """Dense features: act(3), character(5), upgraded, engine components, deck(5), card catalog x deck."""
    a = min(max(s['act'], 1), 3) - 1
    act = [float(a == k) for k in range(3)]
    ch = [float(s['character'] == c) for c in CHARS]
    deck = deck_features(s)
    basic, size, attack, powers, block = deck
    info = CARDS.get(o['card'], {})
    typ = [float(info.get('type') == t) for t in TYPES]
    cost = info.get('cost', 1)
    cost = 1.5 if info.get('costX') else (max(0, cost) if cost is not None else 1)
    rar = [float(info.get('rarity') == r) for r in ('Uncommon', 'Rare')]
    cross = []
    for tv in typ:
        cross += [tv, tv * size, tv * basic, tv * attack, tv * powers, tv * float(a == 1)]
    gb = float(bool(info.get('gainsBlock')))
    extra = [cost, cost * size, gb, gb * block, gb * size] + rar
    return np.concatenate([act, ch, [float(o['upgrade'] > 0)], engine_components(o), deck, cross, extra])


N_SHARED = 3 + len(CHARS) + 1 + len(CARD_COMPS) + 5 + 6 * len(TYPES) + 7


def samples(value_paths, screens_path, target):
    data, _, _, card_of = read_value(value_paths)
    screens = match_screens(data, card_of, screens_path)
    field = 0 if target == 'cost' else 4
    out = []
    for key, s, gain, _, _ in screen_gains(data, screens, field):
        for j, o in enumerate(s['options']):
            g = gain[j]
            # split halves by seed index for the reliability estimate (pairs are sorted by (encounter, seed))
            out.append({'run': s['run_id'], 'screen': s, 'card': o['card'], 'option': o,
                        'y': g.mean(), 'y0': g[0::2].mean(), 'y1': g[1::2].mean()})
    return out


class Ridge:
    def __init__(self, cards):
        self.cards = {c: i for i, c in enumerate(sorted(cards))}
        self.nc = len(self.cards)
        self.fixed = N_SHARED
        self.size = self.fixed + len(BLOCKS) * self.nc

    def row(self, s, o):
        x = np.zeros(self.size)
        x[:self.fixed] = shared_features(s, o)
        i = self.cards.get(o['card'])
        if i is not None:
            a = min(max(s['act'], 1), 3) - 1
            x[self.fixed + i] = 1.0
            x[self.fixed + self.nc + i] = float(a == 1)
        return x

    def fit(self, rows, lam, shared_lam=1.0):
        X = np.array([self.row(r['screen'], r['option']) for r in rows])
        y = np.array([r['y'] for r in rows])
        pen = np.concatenate([np.full(self.fixed, shared_lam), np.full(self.nc, lam), np.full(self.nc, 3 * lam)])
        self.beta = np.linalg.solve(X.T @ X + np.diag(pen), X.T @ y)
        return self

    def predict(self, s, o):
        return float(self.row(s, o) @ self.beta)

    def export(self, lam, target):
        b = self.beta
        cards = {c: [round(float(b[self.fixed + k * self.nc + i]), 6) for k in range(len(BLOCKS))]
                 for c, i in self.cards.items()}
        return {'version': 2, 'target': target, 'lam': lam, 'shared': [round(float(v), 6) for v in b[:self.fixed]],
                'blocks': list(BLOCKS), 'cards': cards}


def load(path):
    """Exported model -> predict(screen, option)."""
    with open(path, encoding='utf-8') as f:
        m = json.load(f)
    w = np.array(m['shared'])

    def predict(s, o):
        a = min(max(s['act'], 1), 3) - 1
        g = float(w @ shared_features(s, o))
        c = m['cards'].get(o['card'])
        if c:
            g += c[0] + c[1] * (a == 1)
        return g
    return predict


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('value')
    ap.add_argument('screens')
    ap.add_argument('--target', default='cost', choices=['cost', 'turns'])
    ap.add_argument('--grid', default='3,10,30,100')
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
    by_screen = collections.defaultdict(list)
    for i, r in enumerate(rows):
        by_screen[id(r['screen'])].append(i)
    groups = list(by_screen.values())

    def centre(v):
        out = np.array(v, float).copy()
        for idx in groups:
            out[idx] -= out[idx].mean()
        return out

    def reliability(a, b):
        h = np.corrcoef(a, b)[0, 1]
        return 2 * h / (1 + h)

    rel, rel_w = reliability(y0, y1), reliability(centre(y0), centre(y1))
    yc = centre(y)
    print(f'{len(rows)} (screen, card) samples from {len(groups)} screens, {len(runs)} runs; '
          f'{len({r["card"] for r in rows})} cards; target {args.target}')
    print(f'measurement reliability {rel:.3f} (within screens {rel_w:.3f}) -> best possible correlation '
          f'{np.sqrt(max(rel, 0)):.3f} (within {np.sqrt(max(rel_w, 0)):.3f})')

    results = {}
    for lam in [float(x) for x in args.grid.split(',')]:
        pred = np.zeros(len(rows))
        for f in range(args.folds):
            tr = [r for r, k in zip(rows, fold) if k != f]
            model = Ridge({r['card'] for r in tr}).fit(tr, lam)
            for i in np.flatnonzero(fold == f):
                pred[i] = model.predict(rows[i]['screen'], rows[i]['option'])
        r = np.corrcoef(pred, y)[0, 1]
        rw = np.corrcoef(centre(pred), yc)[0, 1]
        results[lam] = (rw, r, pred)
        print(f'  lam {lam:<5} held-out corr {r:+.3f} (share of explainable {r * r / max(rel, 1e-9):.2f}); '
              f'within screens {rw:+.3f} (share {rw * rw / max(rel_w, 1e-9):.2f})')
    lam = max(results, key=lambda k: results[k][0])
    pred = results[lam][2]
    multi = [idx for idx in groups if len(idx) > 1]
    agree = [float(np.argmax(pred[idx]) == np.argmax(y[idx])) for idx in multi]
    print(f'best lam {lam}: held-out top card matches the measured top card on {np.mean(agree):.3f} of screens '
          f'(chance {np.mean([1 / len(idx) for idx in multi]):.3f})')

    if args.out:
        model = Ridge({r['card'] for r in rows}).fit(rows, lam)
        with open(args.out, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(model.export(lam, args.target), f, separators=(',', ':'))
        print(f'wrote {args.out}')


if __name__ == '__main__':
    main()
