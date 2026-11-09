"""Step 3 of docs/research/LEARNED_CARD_POLICY_PLAN.md: a learned pick model, scored on held-out PLAYERS.

  python -P tools/CardPolicy/pick_model.py <screens.jsonl> <runs_index.csv> [--folds 5] [--dim 8] [--out result.json]

One softmax per screen over {Skip, offered cards}. Utility of an offered card c on a screen with deck D in act a:

  u(c) = sum_k w_k * comp_k(c)          engine components, each with its own weight (base without the fitted baseAdjust)
       + delta[c] + delta_act[c, a]     per-card value, and its per-act change (L2 shrunk)
       + A[c] . mean_{x in D} B[x]      card x deck interaction: low-rank (dim d) "this card is good with these cards"
  u(Skip) = sum_k v_k * skip_comp_k + s[a] + A[skip] . mean B[x]

dim 0 drops the interaction (model "comp"). Hyper-parameters are chosen per outer fold on an inner player split of the
training players only. Compared against the step-2 logit baseline on the same folds, with a player bootstrap of the
paired difference.
"""
import argparse
import json
import os
import sys

import numpy as np
from scipy import sparse
from scipy.optimize import minimize

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from data import MAX_CARDS, load_index, load_screens, player_folds  # noqa: E402

CARD_COMPS = ['base', 'need', 'build', 'dilution', 'off_build', 'duplicate', 'core_missing', 'secured_essential',
              'engine_missing', 'combo', 'saturation', 'situation', 'power_saturation', 'core_priority']
SKIP_COMPS = ['skip_base', 'skip_basics', 'skip_commit', 'skip_deck_size']
SCALE = 0.05  # components are in engine points (tens); scale so weights start near 1


def card_id(token):
    return token.split('#', 1)[0].split('+', 1)[0]


class Data:
    def __init__(self, screens):
        n, width = len(screens), MAX_CARDS + 1
        cards = sorted({o['card'] for s in screens for o in s['options']} | {card_id(t) for s in screens for t in s['deck']})
        self.vocab = {c: i for i, c in enumerate(cards)}
        v = len(cards)
        self.skip_token = v  # row of A used by Skip
        self.mask = np.zeros((n, width), bool)
        self.X = np.zeros((n, width, len(CARD_COMPS) + 2))  # + upgraded flag, + copies
        self.S = np.zeros((n, len(SKIP_COMPS)))
        self.cards = np.full((n, width), -1, int)
        self.act = np.zeros(n, int)
        self.y = np.zeros(n, int)
        self.base_total = np.zeros((n, width))  # step-2 logit input: total - baseAdjust (skip column: skip total)
        rows, cols, vals = [], [], []
        for i, s in enumerate(screens):
            self.mask[i, 0] = True
            self.S[i] = [s['skip_components'].get(k, 0.0) * SCALE for k in SKIP_COMPS]
            self.base_total[i, 0] = s['skip_total']
            for j, o in enumerate(s['options'], start=1):
                self.mask[i, j] = True
                comp = dict(o['components'])
                comp['base'] = comp.get('base', 0.0) - o.get('base_adjust', 0.0)
                self.X[i, j, :len(CARD_COMPS)] = [comp.get(k, 0.0) * SCALE for k in CARD_COMPS]
                self.X[i, j, -2] = 1.0 if o['upgrade'] > 0 else 0.0
                self.X[i, j, -1] = min(o['copies'], 3)
                self.cards[i, j] = self.vocab[o['card']]
                self.base_total[i, j] = o['total'] - o.get('base_adjust', 0.0)
            self.act[i] = min(max(s['act'], 1), 3) - 1
            self.y[i] = s['pick'] + 1
            deck = [card_id(t) for t in s['deck']]
            w = 1.0 / max(1, len(deck))
            for c in deck:
                rows.append(i)
                cols.append(self.vocab[c])
                vals.append(w)
        self.M = sparse.csr_matrix((vals, (rows, cols)), shape=(n, v))  # deck mean operator
        self.n_vocab = v

    def subset(self, idx):
        out = object.__new__(Data)
        out.vocab, out.skip_token, out.n_vocab = self.vocab, self.skip_token, self.n_vocab
        for name in ('mask', 'X', 'S', 'cards', 'act', 'y', 'base_total'):
            setattr(out, name, getattr(self, name)[idx])
        out.M = self.M[idx]
        return out


class Model:
    def __init__(self, n_vocab, dim):
        self.v, self.d = n_vocab, dim
        self.kc, self.ks = len(CARD_COMPS) + 2, len(SKIP_COMPS)
        sizes = [('w', self.kc), ('vs', self.ks), ('s', 3), ('delta', n_vocab), ('dact', n_vocab * 3),
                 ('A', (n_vocab + 1) * dim), ('B', n_vocab * dim)]
        self.slices, at = {}, 0
        for name, size in sizes:
            self.slices[name] = slice(at, at + size)
            at += size
        self.size = at

    def unpack(self, th):
        g = {k: th[sl] for k, sl in self.slices.items()}
        g['dact'] = g['dact'].reshape(self.v, 3)
        g['A'] = g['A'].reshape(self.v + 1, self.d)
        g['B'] = g['B'].reshape(self.v, self.d)
        return g

    def init(self, seed=0):
        th = np.zeros(self.size)
        th[self.slices['w']][0] = 1.0
        if self.d:
            rng = np.random.default_rng(seed)
            th[self.slices['A']] = rng.normal(0, 0.05, (self.v + 1) * self.d)
            th[self.slices['B']] = rng.normal(0, 0.05, self.v * self.d)
        return th

    def utilities(self, th, a, keep=False):
        p = self.unpack(th)
        has = a.cards >= 0
        cidx = np.where(has, a.cards, 0)
        u = a.X @ p['w']
        u += np.where(has, p['delta'][cidx] + p['dact'][cidx, a.act[:, None]], 0.0)
        D = a.M @ p['B'] if self.d else None
        if self.d:
            u += np.where(has, np.einsum('nwd,nd->nw', p['A'][cidx], D), 0.0)
        u[:, 0] = a.S @ p['vs'] + p['s'][a.act] + (p['A'][self.v] @ D.T if self.d else 0.0)
        u = np.where(a.mask, u, -np.inf)
        return (u, p, D, cidx, has) if keep else u

    def loss(self, th, a, lam):
        u, p, D, cidx, has = self.utilities(th, a, keep=True)
        m = u.max(axis=1, keepdims=True)
        lp = u - m - np.log(np.exp(u - m).sum(axis=1, keepdims=True))
        n = len(a.y)
        rows = np.arange(n)
        nll = -lp[rows, a.y].sum()
        r = np.where(a.mask, np.exp(lp), 0.0)
        r[rows, a.y] -= 1.0
        rc = np.where(has, r, 0.0)
        g = {k: np.zeros_like(v) for k, v in p.items()}
        g['w'] = np.einsum('nw,nwk->k', rc, a.X)
        g['vs'] = r[:, 0] @ a.S
        g['s'] = np.bincount(a.act, weights=r[:, 0], minlength=3)
        g['delta'] = np.bincount(cidx[has], weights=rc[has], minlength=self.v)
        for k in range(3):
            sel = has & (a.act[:, None] == k)
            g['dact'][:, k] = np.bincount(cidx[sel], weights=rc[sel], minlength=self.v)
        if self.d:
            # u[n,w] += A[c_nw] . D_n ; u[n,0] += A[skip] . D_n ; D = M B
            gA = np.zeros_like(p['A'])
            np.add.at(gA, cidx[has], rc[has][:, None] * np.repeat(D, has.sum(axis=1), axis=0))
            gA[self.v] = r[:, 0] @ D
            gD = np.einsum('nw,nwd->nd', rc, p['A'][cidx]) + r[:, :1] * p['A'][self.v]
            g['A'], g['B'] = gA, a.M.T @ gD
        # L2: per-card values and interaction factors (engine weights unpenalised)
        l_delta, l_act, l_emb = lam
        nll += 0.5 * (l_delta * (p['delta'] ** 2).sum() + l_act * (p['dact'] ** 2).sum()
                      + l_emb * ((p['A'] ** 2).sum() + (p['B'] ** 2).sum()))
        g['delta'] += l_delta * p['delta']
        g['dact'] += l_act * p['dact']
        if self.d:
            g['A'] += l_emb * p['A']
            g['B'] += l_emb * p['B']
        flat = np.concatenate([g[k].ravel() for k in self.slices])
        return nll, flat

    def fit(self, a, lam, maxiter=400):
        res = minimize(self.loss, self.init(), args=(a, lam), jac=True, method='L-BFGS-B', options={'maxiter': maxiter})
        return res.x


def scores(u, y):
    m = u.max(axis=1, keepdims=True)
    lp = u - m - np.log(np.exp(u - m).sum(axis=1, keepdims=True))
    rows = np.arange(len(y))
    return (np.argmax(u, axis=1) == y).astype(float), lp[rows, y]


def baseline_logit(tr, te, lam=1.0):
    """Step-2 model on the same arrays: u = b*(total - baseAdjust) + delta[card], skip b*skip + s[act]."""
    v = tr.n_vocab

    def util(th, a):
        u = th[0] * a.base_total
        has = a.cards >= 0
        u += np.where(has, th[4:][np.where(has, a.cards, 0)], 0.0)
        u[:, 0] = th[0] * a.base_total[:, 0] + th[1:4][a.act]
        return np.where(a.mask, u, -np.inf)

    def loss(th):
        u = util(th, tr)
        m = u.max(axis=1, keepdims=True)
        lp = u - m - np.log(np.exp(u - m).sum(axis=1, keepdims=True))
        rows = np.arange(len(tr.y))
        r = np.where(tr.mask, np.exp(lp), 0.0)
        r[rows, tr.y] -= 1.0
        has = tr.cards >= 0
        x = np.where(tr.mask, tr.base_total, 0.0)
        g = np.zeros_like(th)
        g[0] = (r * x).sum()
        g[1:4] = np.bincount(tr.act, weights=r[:, 0], minlength=3)
        g[4:] = np.bincount(tr.cards[has], weights=r[has], minlength=v) + lam * th[4:]
        return -lp[rows, tr.y].sum() + 0.5 * lam * (th[4:] ** 2).sum(), g

    th = np.zeros(4 + v)
    th[0] = 0.05
    th = minimize(loss, th, jac=True, method='L-BFGS-B').x
    return util(th, te)


def player_split(players, frac, seed):
    uniq = sorted(set(players))
    rng = np.random.default_rng(seed)
    rng.shuffle(uniq)
    held = set(uniq[:max(1, int(len(uniq) * frac))])
    return np.array([p in held for p in players])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('screens')
    ap.add_argument('index')
    ap.add_argument('--folds', type=int, default=5)
    ap.add_argument('--dim', type=int, default=8)
    ap.add_argument('--grid', default='1,3,10', help='candidate L2 strengths for card values (others scale with it)')
    ap.add_argument('--out')
    args = ap.parse_args()

    screens = load_screens(args.screens, load_index(args.index))
    players = np.array([s['player'] for s in screens])
    folds = player_folds(screens, args.folds)
    data = Data(screens)
    print(f'{len(screens)} screens, {len(set(players))} players, vocab {data.n_vocab}, dim {args.dim}')

    grid = [float(x) for x in args.grid.split(',')]
    lams = [(l, 3 * l, 10 * l) for l in grid]
    per_screen = {k: (np.zeros(len(screens)), np.zeros(len(screens))) for k in ('logit', 'comp', 'interact')}
    chosen = []
    for f in range(args.folds):
        tr_idx, te_idx = np.flatnonzero(folds != f), np.flatnonzero(folds == f)
        tr, te = data.subset(tr_idx), data.subset(te_idx)
        hit, ll = scores(baseline_logit(tr, te), te.y)
        per_screen['logit'][0][te_idx], per_screen['logit'][1][te_idx] = hit, ll
        inner = player_split(players[tr_idx], 0.2, seed=f)
        itr, iva = data.subset(tr_idx[~inner]), data.subset(tr_idx[inner])
        for name, dim in (('comp', 0), ('interact', args.dim)):
            model = Model(data.n_vocab, dim)
            best = max(lams, key=lambda lam: scores(model.utilities(model.fit(itr, lam), iva), iva.y)[1].mean())
            th = model.fit(tr, best)
            hit, ll = scores(model.utilities(th, te), te.y)
            per_screen[name][0][te_idx], per_screen[name][1][te_idx] = hit, ll
            chosen.append({'fold': f, 'model': name, 'lam': best})
        print(f'fold {f}: ' + '  '.join(f'{k} {v[0][te_idx].mean():.3f}/{v[1][te_idx].mean():.3f}' for k, v in per_screen.items()))

    # Player bootstrap of the paired difference against the step-2 logit.
    uniq = sorted(set(players))
    by_player = {p: np.flatnonzero(players == p) for p in uniq}
    rng = np.random.default_rng(0)
    summary = {}
    for name, (hit, ll) in per_screen.items():
        entry = {'top1': float(hit.mean()), 'll': float(ll.mean())}
        if name != 'logit':
            dh, dl = hit - per_screen['logit'][0], ll - per_screen['logit'][1]
            bs_h, bs_l = [], []
            for _ in range(1000):
                idx = np.concatenate([by_player[p] for p in rng.choice(uniq, len(uniq))])
                bs_h.append(dh[idx].mean())
                bs_l.append(dl[idx].mean())
            entry['d_top1'] = float(dh.mean())
            entry['d_top1_ci95'] = [float(np.percentile(bs_h, 2.5)), float(np.percentile(bs_h, 97.5))]
            entry['d_ll'] = float(dl.mean())
            entry['d_ll_ci95'] = [float(np.percentile(bs_l, 2.5)), float(np.percentile(bs_l, 97.5))]
        summary[name] = entry
    summary['chosen'] = chosen
    print(json.dumps({k: v for k, v in summary.items() if k != 'chosen'}, indent=1))
    if args.out:
        with open(args.out, 'w', encoding='utf-8') as f:
            json.dump(summary, f, indent=1)


if __name__ == '__main__':
    main()
