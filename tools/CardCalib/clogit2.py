"""Vectorised conditional logit over ranker reward screens.
u(card) = beta * (engine_total - need + sum_r m[act, r] * need_r) + delta[card] / beta_div-free
u(skip) = beta * skip_total + s[act]
delta: L2 (lam). need multipliers m: optional (--need), L2 toward 1 with lam_m.
CV grouped by player (index player_key) or run.

usage: clogit2.py cards.csv --index runs_index.csv [--players good|all] [--group player|run] [--lam 1] [--need] [--no-delta]
                  [--json out.json] [--folds 5] [--versions v0.107] [--min-n 8]
"""
import csv, json, argparse, collections, random
import numpy as np
from scipy.optimize import minimize

ap = argparse.ArgumentParser()
ap.add_argument('csv'); ap.add_argument('--index'); ap.add_argument('--players', default='all')
ap.add_argument('--group', default='player'); ap.add_argument('--lam', type=float, default=1.0)
ap.add_argument('--lam-m', type=float, default=0.0); ap.add_argument('--need', action='store_true')
ap.add_argument('--no-delta', action='store_true'); ap.add_argument('--json'); ap.add_argument('--folds', type=int, default=5)
ap.add_argument('--versions', default=''); ap.add_argument('--min-n', type=int, default=8); ap.add_argument('--quiet', action='store_true')
ap.add_argument('--fixed-beta', type=float, default=0.0)
ap.add_argument('--patched', default='')   # patched.json: these cards get a separate delta on non-v0.107 runs (not exported)
a = ap.parse_args()
ROLES = ['frontload', 'aoe', 'block', 'scaling', 'draw']; R = len(ROLES)

meta = {}
if a.index:
    for r in csv.DictReader(open(a.index, encoding='utf-8')):
        meta[r['runId']] = r
screens = collections.OrderedDict()
for r in csv.DictReader(open(a.csv, encoding='utf-8')):
    if r['context'] == 'Shop':
        continue
    m = meta.get(r['run'])
    if a.index:
        if m is None or m.get('keep', '1') != '1':
            continue
        if a.players == 'good' and m.get('good_player') != '1':
            continue
        if a.versions and not m['build_id'].startswith(a.versions):
            continue
    screens.setdefault((r['run'], r['screen']), []).append(r)

patched = set(json.load(open(a.patched))) if a.patched else set()


def ckey(run, r):
    k = (r['character'], r['card'])
    if f'{k[0]}|{k[1]}' in patched and not meta.get(run, {}).get('build_id', 'v0.107').startswith('v0.107'):
        return (k[0], k[1] + '@new')
    return k


keys = sorted({ckey(run, r) for (run, _), cs in screens.items() for r in cs})
kidx = {k: i for i, k in enumerate(keys)}; C = len(keys)
N = len(screens); O = max(len(cs) for cs in screens.values()) + 1
TOT = np.zeros((N, O)); ND = np.zeros((N, O, R)); CI = np.full((N, O), -2); ACT = np.zeros(N, int); PICK = np.zeros(N, int)
GRP = []
for n, ((run, _), cs) in enumerate(screens.items()):
    for j, r in enumerate(cs):
        nd = [float(r['need_' + k]) for k in ROLES]
        TOT[n, j] = float(r['total']) - sum(nd); ND[n, j] = nd; CI[n, j] = kidx[ckey(run, r)]
    k = len(cs)
    TOT[n, k] = float(cs[0]['skip_total']); CI[n, k] = -1
    ACT[n] = min(3, max(1, int(cs[0]['act']))) - 1
    p = next((j for j, r in enumerate(cs) if r['ranker_pick'] == '1'), k)
    PICK[n] = p
    pk = meta.get(run, {}).get('player_key') or ('run:' + run)
    GRP.append(pk if a.group == 'player' else run)
GRP = np.array(GRP)
MASK = CI > -2
ISSKIP = CI == -1
K = 4 + 3 * R


def unpack(th):
    return th[0], th[1:4], th[4:K].reshape(3, R), th[K:]


def nll(th, idx, lam):
    beta, s, m, d = unpack(th)
    if a.fixed_beta:
        beta = a.fixed_beta
    tot, nd, ci, act, pick, mask, sk = TOT[idx], ND[idx], CI[idx], ACT[idx], PICK[idx], MASK[idx], ISSKIP[idx]
    mm = m[act]                                     # n x R
    x = tot + np.einsum('nor,nr->no', nd, mm)
    off = np.where(ci >= 0, d[np.maximum(ci, 0)], 0.0) + np.where(sk, s[act][:, None], 0.0)
    u = np.where(mask, beta * x + off, -1e9)
    u -= u.max(axis=1, keepdims=True)
    e = np.exp(u) * mask; z = e.sum(axis=1, keepdims=True); p = e / z
    rows = np.arange(len(idx))
    f = -np.log(p[rows, pick] + 1e-300).sum()
    rr = -p; rr[rows, pick] += 1; rr *= mask
    g = np.zeros_like(th)
    if not a.fixed_beta:
        g[0] = -(rr * x).sum()
    sk_r = (rr * sk).sum(axis=1)
    g[1:4] = -np.bincount(act, weights=sk_r, minlength=3)
    gm = -beta * np.einsum('no,nor->nr', rr, nd)    # n x R
    for t in range(3):
        g[4 + t * R: 4 + (t + 1) * R] = gm[act == t].sum(axis=0)
    cc = ci[mask & (ci >= 0)]; ww = rr[mask & (ci >= 0)]
    g[K:] = -np.bincount(cc, weights=ww, minlength=C)
    f += lam * (d ** 2).sum(); g[K:] += 2 * lam * d
    if a.lam_m:
        f += a.lam_m * ((m - 1) ** 2).sum(); g[4:K] += 2 * a.lam_m * (m - 1).ravel()
    return f, g


def fit(idx, lam, with_delta=True):
    th0 = np.zeros(K + C); th0[0] = a.fixed_beta or 0.05; th0[4:K] = 1.0
    free = [0, 1, 2, 3] + (list(range(4, K)) if a.need else []) + (list(range(K, K + C)) if with_delta else [])
    def f2(t):
        full = th0.copy(); full[free] = t
        v, g = nll(full, idx, lam)
        return v, g[free]
    r = minimize(f2, th0[free], jac=True, method='L-BFGS-B', options={'maxiter': 2000})
    full = th0.copy(); full[free] = r.x
    return full


def score(th, idx):
    beta, s, m, d = unpack(th)
    if a.fixed_beta:
        beta = a.fixed_beta
    tot, nd, ci, act, pick, mask, sk = TOT[idx], ND[idx], CI[idx], ACT[idx], PICK[idx], MASK[idx], ISSKIP[idx]
    x = tot + np.einsum('nor,nr->no', nd, m[act])
    off = np.where(ci >= 0, d[np.maximum(ci, 0)], 0.0) + np.where(sk, s[act][:, None], 0.0)
    u = np.where(mask, beta * x + off, -1e9)
    mx = u.max(axis=1, keepdims=True)
    ll = (u[np.arange(len(idx)), pick] - mx[:, 0] - np.log(np.exp(u - mx).sum(axis=1))).mean()
    top = (u.argmax(axis=1) == pick).mean()
    return ll, top


groups = sorted(set(GRP)); random.seed(11); random.shuffle(groups)
allidx = np.arange(N)
cv = []
for f in range(a.folds):
    test = set(groups[f::a.folds])
    te = np.array([i for i in allidx if GRP[i] in test]); tr = np.array([i for i in allidx if GRP[i] not in test])
    base = fit(tr, a.lam, False)
    full = fit(tr, a.lam, not a.no_delta)
    cv.append((score(base, te), score(full, te)))
b = np.mean([c[0] for c in cv], axis=0); dd = np.mean([c[1] for c in cv], axis=0)
th = fit(allidx, a.lam, not a.no_delta)
beta, s, m, d = unpack(th)
if a.fixed_beta:
    beta = a.fixed_beta
print(f"screens {N} groups {len(groups)} by {a.group} players={a.players} lam {a.lam} need={a.need}")
print(f"CV base{'+need' if a.need else ''}: ll {b[0]:.4f} top1 {b[1]:.3f} | +delta: ll {dd[0]:.4f} top1 {dd[1]:.3f}")
print(f"beta {beta:.4f}/pt skip offsets pts {np.round(s / beta, 1).tolist()}")
if a.need:
    for t in range(3):
        print(f"  need mult act{t + 1}: " + ' '.join(f"{ROLES[i]} {m[t, i]:+.2f}" for i in range(R)))
st = collections.defaultdict(lambda: [0, 0, 0])
for (run, _), cs in screens.items():
    for r in cs:
        x = st[ckey(run, r)]; x[0] += 1; x[1] += int(r['ranker_pick']); x[2] += int(r['engine_best'])
if a.json:
    json.dump({'beta': beta, 'skip': (s / beta).tolist(), 'need': m.tolist(), 'cv': {'base': b.tolist(), 'delta': dd.tolist()},
               'cards': [{'character': k[0], 'card': k[1], 'n': st[k][0], 'ranker': st[k][1], 'engine': st[k][2],
                          'delta': d[kidx[k]], 'offset': d[kidx[k]] / beta} for k in keys if not k[1].endswith('@new')]}, open(a.json, 'w'), indent=1)
