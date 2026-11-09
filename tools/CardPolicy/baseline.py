"""Step 2 of docs/research/LEARNED_CARD_POLICY_PLAN.md: baselines a learned pick model has to beat, on held-out PLAYERS.

  python -I tools/CardPolicy/baseline.py <screens.jsonl> <runs_index.csv> [--folds 5] [--out summary.json]

Models (all scored on players they were not fit on):
  engine        the shipped engine's recommendation (argmax with its skip rule). Leaks: the pack's baseAdjust was fit on
                these same runs (CARD_CALIBRATION), so it is an optimistic reference, not a fair baseline.
  engine_noadj  argmax of (total - baseAdjust) against skip + margin: the engine without the data-fit card values.
  logit         conditional logit u = b*(total - baseAdjust) + delta[card], skip u = b*skip + s[act]; delta has L2 shrinkage.
                The fair re-creation of the CardCalib model: card values are learned on the training players only.
Metrics: top-1 (predicted option == player's pick, Skip counts as an option) and mean log-likelihood per screen.
"""
import argparse
import json
import os
import sys

import numpy as np
from scipy.optimize import minimize

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from data import Arrays, load_index, load_screens, player_folds  # noqa: E402

SKIP_MARGIN = 0.5  # weights skip.margin


def utilities(theta, a, use_delta):
    b, s = theta[0], theta[1:4]
    u = b * (a.total - a.base_adjust)
    u[:, 0] = b * a.total[:, 0] + s[a.act]
    if use_delta:
        delta = theta[4:]
        has = a.cards >= 0
        u[has] += delta[a.cards[has]]
    return np.where(a.mask, u, -np.inf)


def log_probs(u):
    m = u.max(axis=1, keepdims=True)
    z = np.exp(u - m)
    return u - m - np.log(z.sum(axis=1, keepdims=True))


def nll_and_grad(theta, a, lam, use_delta):
    lp = log_probs(utilities(theta, a, use_delta))
    n = len(a.y)
    rows = np.arange(n)
    nll = -lp[rows, a.y].sum()
    p = np.where(a.mask, np.exp(lp), 0.0)
    r = p.copy()
    r[rows, a.y] -= 1.0                       # d nll / d u
    x = np.where(a.mask, a.total - a.base_adjust, 0.0)
    x[:, 0] = a.total[:, 0]
    g = np.zeros_like(theta)
    g[0] = (r * x).sum()
    g[1:4] = np.bincount(a.act, weights=r[:, 0], minlength=3)
    if use_delta:
        has = a.cards >= 0
        g[4:] = np.bincount(a.cards[has], weights=r[has], minlength=len(theta) - 4)
        nll += 0.5 * lam * (theta[4:] ** 2).sum()
        g[4:] += lam * theta[4:]
    return nll, g


def fit(a, n_cards, lam=1.0, use_delta=True):
    theta = np.zeros(4 + (n_cards if use_delta else 0))
    theta[0] = 0.05
    res = minimize(nll_and_grad, theta, args=(a, lam, use_delta), jac=True, method='L-BFGS-B')
    return res.x


def metrics(u, a, rows=None):
    lp = log_probs(u)
    rows = np.arange(len(a.y)) if rows is None else rows
    pred = np.argmax(u, axis=1)
    return {'n': int(len(rows)), 'top1': float((pred[rows] == a.y[rows]).mean()), 'll': float(lp[rows, a.y[rows]].mean())}


def engine_noadj_pred(a):
    x = np.where(a.mask, a.total - a.base_adjust, -np.inf)
    x[:, 0] = -np.inf
    best = np.argmax(x, axis=1)
    beats = x[np.arange(len(best)), best] >= a.total[:, 0] + SKIP_MARGIN
    return np.where(beats, best, 0)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('screens')
    ap.add_argument('index')
    ap.add_argument('--folds', type=int, default=5)
    ap.add_argument('--lam', type=float, default=1.0)
    ap.add_argument('--out')
    args = ap.parse_args()

    screens = load_screens(args.screens, load_index(args.index))
    folds = player_folds(screens, args.folds)
    a = Arrays(screens)
    n_cards = len(a.vocab)
    print(f'{len(screens)} screens, {len({s["player"] for s in screens})} players, {n_cards} cards, {args.folds} player folds')

    results = {'engine': [], 'engine_noadj': [], 'logit_beta': [], 'logit': []}
    recent = {k: [] for k in results}
    for f in range(args.folds):
        tr, te = a.subset(folds != f), a.subset(folds == f)
        rec = np.flatnonzero(te.recent)
        for name, pred in (('engine', te.engine), ('engine_noadj', engine_noadj_pred(te))):
            hit = pred == te.y
            results[name].append({'n': int(len(hit)), 'top1': float(hit.mean()), 'll': None})
            recent[name].append({'n': int(len(rec)), 'top1': float(hit[rec].mean()), 'll': None})
        for name, use_delta in (('logit_beta', False), ('logit', True)):
            theta = fit(tr, n_cards, args.lam, use_delta)
            u = utilities(theta, te, use_delta)
            results[name].append(metrics(u, te))
            recent[name].append(metrics(u, te, rec))
        print(f'fold {f}: ' + '  '.join(f'{k} {v[-1]["top1"]:.3f}' for k, v in results.items()))

    def pooled(rows):
        n = sum(r['n'] for r in rows)
        out = {'n': n, 'top1': sum(r['top1'] * r['n'] for r in rows) / n}
        if rows[0]['ll'] is not None:
            out['ll'] = sum(r['ll'] * r['n'] for r in rows) / n
        return out

    summary = {k: {'all': pooled(v), 'recent': pooled(recent[k]), 'folds': v} for k, v in results.items()}
    print()
    print('model          top-1   ll      | recent top-1  ll')
    for k, v in summary.items():
        al, rc = v['all'], v['recent']
        ll = f"{al['ll']:.4f}" if 'll' in al else '   -   '
        rll = f"{rc['ll']:.4f}" if 'll' in rc else '   -   '
        print(f'{k:<14} {al["top1"]:.4f} {ll} | {rc["top1"]:.4f}       {rll}')
    if args.out:
        with open(args.out, 'w', encoding='utf-8') as f:
            json.dump(summary, f, indent=1)


if __name__ == '__main__':
    main()
