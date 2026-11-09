"""Upgrade value calibration (docs/research/UPGRADE_CALIBRATION.md). Ranker runs stay on this machine; nothing they
contain is committed, only per-card numbers and weights.

Input: the candidate table from
    dotnet run --project tools/RunHistoryAnalyzer -- upgrade-export <runs-dir> --pack data/packs/v0.107.1 --all-players --out up.csv
(one row per upgrade candidate at every recorded smith visit, chosen=1 for the card the player upgraded) and the run index
(runId -> player_key) used to split players for cross-validation.

Model: conditional logit over the candidates of one smith visit,
    u(card) = card constant + sum_role beta_role * [card has that role in a build] * commit + beta_need * need
    card constant = prior(card) + fe(card)
    prior(card) = b_delta*upgradedDelta + b_costdown*[upgrade lowers cost] + b_kw*[upgrade adds/removes a keyword]
                  + b_power*[power] + b_basic*[basic rarity] + b_base*base/10
    fe(card)    = per-card effect, L2-shrunk toward 0 (so rare cards stay near their prior)
Cards changed in v0.108-0.111 (tools/CardCalib/drift.py KNOWN) get a separate effect on newer runs, which is dropped.

Points: score = A + S*u, with A, S chosen so the mean candidate score and the mean best-candidate score over all
visits equal the old advisor's (upgradedDelta + need): the ranking follows the fitted model, the heal-vs-smith balance
of the rest-site advisor stays where it was.

usage: python -P fit.py <up.csv> <runs_index.csv> [--write] [--json out.json]
"""
import argparse, json, math, os, re, sys
import numpy as np
from scipy.optimize import minimize
from scipy import sparse

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..'))
PACK = os.path.join(REPO, 'data', 'packs', 'v0.107.1')
CATALOG = os.path.join(REPO, 'data', 'catalog', 'cards.v0.107.1.json')
VARS = os.path.join(REPO, 'data', 'catalog', 'card-vars.v0.107.1.json')
TIER_FILES = ['ironclad', 'silent', 'defect', 'necrobinder', 'regent', 'colorless']
TIER_VALUE = {'S': 80, 'A': 65, 'B': 50, 'C': 35, 'D': 20}
ROLES = ['core', 'payoff', 'enabler', 'support']
PRIOR = ['delta', 'costdown', 'kw', 'power', 'basic', 'base10']
L2, LFE = 0.01, 0.3  # chosen by player-split CV (UPGRADE_CALIBRATION.md §2)


def known_patched():
    src = open(os.path.join(REPO, 'tools', 'CardCalib', 'drift.py'), encoding='utf8').read()
    return set(eval(re.search(r'KNOWN = (\[.*?\])', src, re.S).group(1)))


def strip_comments(text):
    return '\n'.join(ln for ln in text.splitlines() if not ln.lstrip().startswith('//'))


def load_pack_cards():
    cards = {}
    for f in TIER_FILES:
        j = json.loads(strip_comments(open(os.path.join(PACK, 'tiers', f + '.json'), encoding='utf8').read()))
        for cid, e in j['cards'].items():
            cards.setdefault(cid, e)
    return cards


def read_csv(path):
    import csv
    with open(path, encoding='utf8') as fh:
        return list(csv.DictReader(fh))


class Data:
    def __init__(self, up_csv, index_csv):
        rows = read_csv(up_csv)
        player = {r['runId']: r['player_key'] for r in read_csv(index_csv)}
        cat = {c['id']: c for c in json.load(open(CATALOG, encoding='utf8'))['cards']}
        upg = {c['id']: c['upgrade'] for c in json.load(open(VARS, encoding='utf8'))['cards']}
        self.cat, self.upg = cat, upg
        patched = known_patched()
        rows.sort(key=lambda r: int(r['decision']))
        n = len(rows)
        self.card = np.array([r['card'] for r in rows])
        self.ckey = np.array([r['card'] + ('@new' if r['card'][5:] in patched and not r['build'].startswith('v0.107') else '')
                              for r in rows])
        self.chosen = np.array([float(r['chosen']) for r in rows])
        f = lambda k: np.array([float(r[k]) for r in rows])
        self.cols = {'delta': f('delta'), 'need': f('need'), 'base10': f('base') / 10}
        for k in ['costdown', 'kw', 'power', 'basic']:
            self.cols[k] = np.array([self.card_feature(c, k) for c in self.card])
        commit = f('commit')
        role = np.array([r['role'] for r in rows])
        for r in ROLES:
            self.cols['r_' + r] = (role == r) * commit
        dec = np.array([int(r['decision']) for r in rows])
        self.starts = np.r_[0, np.flatnonzero(np.diff(dec)) + 1]
        self.ends = np.r_[self.starts[1:], n]
        self.player = np.array([player.get(rows[a]['source'], rows[a]['source']) for a in self.starts])
        self.build = np.array([rows[a]['build'] for a in self.starts])
        self.keys = sorted(set(self.ckey))
        ki = {k: i for i, k in enumerate(self.keys)}
        self.F = sparse.csr_matrix((np.ones(n), (np.arange(n), [ki[k] for k in self.ckey])), shape=(n, len(self.keys)))

    def card_feature(self, cid, k):
        u, c = self.upg.get(cid, {}), self.cat.get(cid, {})
        if k == 'costdown': return 1.0 if u.get('costDelta', 0) < 0 else 0.0
        if k == 'kw': return 1.0 if (u.get('addKeywords') or u.get('removeKeywords')) else 0.0
        if k == 'power': return 1.0 if c.get('type') == 'Power' else 0.0
        if k == 'basic': return 1.0 if c.get('rarity') == 'Basic' else 0.0
        raise KeyError(k)

    def X(self, cols):
        return np.column_stack([self.cols[c] for c in cols])

    def rep(self, v):
        return np.repeat(v, self.ends - self.starts)

    def probs(self, s):
        m = np.maximum.reduceat(s, self.starts)
        e = np.exp(s - self.rep(m))
        return e / self.rep(np.add.reduceat(e, self.starts))

    def fit(self, cols, mask, fe=True):
        X, y, k = self.X(cols), self.chosen, len(cols)
        F = self.F if fe else sparse.csr_matrix((len(y), 0))
        w = self.rep(mask).astype(float)
        pen = np.r_[np.full(k, L2), np.full(F.shape[1], LFE)]

        def f(b):
            p = self.probs(X @ b[:k] + F @ b[k:])
            llg = np.add.reduceat(np.log(p) * y, self.starts)
            r = (y - p) * w
            return -(llg * mask).sum() + (pen * b * b).sum(), -np.r_[r @ X, F.T @ r] + 2 * pen * b

        b = minimize(f, np.zeros(k + F.shape[1]), jac=True, method='L-BFGS-B', options={'maxiter': 5000}).x
        return b[:k], b[k:]

    def score_eval(self, s, mask):
        """Expected top-1 / top-3 (ties split at random) and mean log-likelihood of the chosen card."""
        t1 = t3 = ll = 0.0
        n = 0
        for a, b, m in zip(self.starts, self.ends, mask):
            if not m: continue
            v, y = s[a:b], self.chosen[a:b]
            c = v[y == 1][0]
            best, tied = 1 + (v > c + 1e-9).sum(), (abs(v - c) <= 1e-9).sum()
            t1 += min(1, max(0, (1 - best + 1) / tied))
            t3 += min(1, max(0, (3 - best + 1) / tied))
            mm = v.max()
            ll += c - mm - math.log(np.exp(v - mm).sum())
            n += 1
        return t1 / n, t3 / n, ll / n


def card_constants(D, cols, beta, fe):
    """Per-card constant = prior (features at the card's mean row; pack/catalog for unseen cards) + its v0.107 effect."""
    pack = load_pack_cards()
    fe_of = dict(zip(D.keys, fe))
    bi = dict(zip(cols, beta))
    out = {}
    for cid in sorted(set(pack) | set(D.card)):
        e = pack.get(cid, {})
        rows = D.card == cid
        x = {}
        if rows.any():
            x['delta'] = D.cols['delta'][rows].mean()
            x['base10'] = D.cols['base10'][rows].mean()
        else:
            x['delta'] = e.get('upgradedDelta', 0) or 0
            x['base10'] = (TIER_VALUE.get(e.get('tier', 'C'), 35) + e.get('baseAdjust', 0)) / 10
        for k in ['costdown', 'kw', 'power', 'basic']:
            x[k] = D.card_feature(cid, k)
        prior = sum(bi[k] * x[k] for k in PRIOR)
        out[cid] = (prior, fe_of.get(cid, 0.0), int(rows.sum()), int(D.chosen[rows].sum()))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('up_csv'); ap.add_argument('index_csv')
    ap.add_argument('--reps', type=int, default=5)
    ap.add_argument('--write', action='store_true', help='write upgradeValue into the tier packs and the weights block')
    ap.add_argument('--json', help='summary json (numbers for the doc)')
    a = ap.parse_args()

    D = Data(a.up_csv, a.index_csv)
    cols = PRIOR + ['r_' + r for r in ROLES] + ['need']
    ndec = len(D.starts)
    print(f'visits {ndec}, candidates {len(D.card)}, players {len(set(D.player))}')

    # ---- 2-fold CV by player, both directions, a.reps times ----
    rng = np.random.default_rng(7)
    players = np.unique(D.player)
    old = D.cols['delta'] + D.cols['need']
    cv = {'old (upgradedDelta + need)': [], 'prior only': [], 'prior + roles': [], 'prior + roles + card effect': [],
          'prior + card effect (no roles)': []}
    for _ in range(a.reps):
        perm = rng.permutation(players)
        half = set(perm[:len(perm) // 2])
        A = np.array([p in half for p in D.player])
        for tr, te in ((A, ~A), (~A, A)):
            cv['old (upgradedDelta + need)'].append(D.score_eval(old, te))
            for name, cs, fe in (('prior only', PRIOR + ['need'], False),
                                 ('prior + roles', cols, False),
                                 ('prior + card effect (no roles)', PRIOR + ['need'], True),
                                 ('prior + roles + card effect', cols, True)):
                b, f = D.fit(cs, tr, fe)
                s = D.X(cs) @ b + (D.F @ f if fe else 0)
                cv[name].append(D.score_eval(s, te))
    cvs = {k: dict(zip(['top1', 'top3', 'll'], np.mean(v, 0).round(4).tolist())) for k, v in cv.items()}
    for k, v in cvs.items():
        print(f'  CV {k:34s} top1 {v["top1"]:.3f} top3 {v["top3"]:.3f} ll {v["ll"]:.4f}')

    # ---- full fit ----
    allm = np.ones(ndec, bool)
    beta, fe = D.fit(cols, allm, True)
    bi = dict(zip(cols, beta))
    print('  beta', {k: round(v, 3) for k, v in bi.items()})
    need_w = max(0.0, bi['need'])          # a negative need weight is dropped (UPGRADE_CALIBRATION.md §3)
    consts = card_constants(D, cols, beta, fe)

    # In-sample score of the stored model (card constant + roles + need), and the scale that keeps the old level.
    const_row = np.array([consts[c][0] + consts[c][1] for c in D.card])
    role_u = sum(bi['r_' + r] * D.cols['r_' + r] for r in ROLES)
    u = const_row + role_u + need_w * D.cols['need']
    mean_u, best_u = u.mean(), np.maximum.reduceat(u, D.starts).mean()
    mean_o, best_o = old.mean(), np.maximum.reduceat(old, D.starts).mean()
    S = (best_o - mean_o) / (best_u - mean_u)
    A0 = mean_o - S * mean_u
    print(f'  scale: {S:.3f} points per logit, offset {A0:.2f} (old mean {mean_o:.2f}, best {best_o:.2f})')

    value = {c: round(A0 + S * (p + f), 1) for c, (p, f, n, k) in consts.items()}
    role_pts = {r: round(S * bi['r_' + r], 2) for r in ROLES}
    need_pts = round(S * need_w, 2)
    stored = np.array([value[c] for c in D.card]) + sum(role_pts[r] * D.cols['r_' + r] for r in ROLES) + need_pts * D.cols['need']
    ins = dict(zip(['top1', 'top3', 'll'], np.round(D.score_eval(stored, allm), 4).tolist()))
    print(f'  stored model in-sample top1 {ins["top1"]:.3f} top3 {ins["top3"]:.3f} (old {D.score_eval(old, allm)[:2]})')

    summary = {'visits': ndec, 'candidates': len(D.card), 'players': len(set(D.player)), 'cv': cvs,
               'beta': {k: round(v, 4) for k, v in bi.items()}, 'scale': round(S, 4), 'offset': round(A0, 3),
               'rolePoints': role_pts, 'needPoints': need_pts, 'inSample': ins,
               'cards': {c: {'value': value[c], 'prior': round(p, 3), 'effect': round(f, 3), 'n': n, 'chosen': k}
                         for c, (p, f, n, k) in consts.items()}}
    if a.json:
        json.dump(summary, open(a.json, 'w', encoding='utf8'), indent=1, ensure_ascii=False)
    if a.write:
        write_pack(value, role_pts, need_pts, S)


def write_pack(value, role_pts, need_pts, scale):
    for f in TIER_FILES:
        path = os.path.join(PACK, 'tiers', f + '.json')
        lines = open(path, encoding='utf8').read().split('\n')
        out = []
        for ln in lines:
            m = re.match(r'\s*"(CARD\.[A-Z0-9_]+)": \{', ln)
            if m and m.group(1) in value:
                ln = re.sub(r', "upgradeValue": -?[\d.]+', '', ln)
                v = value[m.group(1)]
                vs = ('%d' % v) if float(v).is_integer() else ('%.1f' % v)
                anchor = r'("baseAdjust": -?\d+)' if '"baseAdjust"' in ln else r'("upgradedDelta": -?[\d.]+)' if '"upgradedDelta"' in ln else r'("tier": "\w")'
                ln = re.sub(anchor, r'\1, "upgradeValue": ' + vs, ln, count=1)
            out.append(ln)
        open(path, 'w', encoding='utf8', newline='').write('\n'.join(out))

    path = os.path.join(PACK, 'weights.json')
    text = open(path, encoding='utf8').read()
    text = re.sub(r',\n  // 2026-11 강화 보정.*?"upgrade": \{.*?\n  \}', '', text, flags=re.S)
    block = (',\n  // 2026-11 강화 보정(docs/research/UPGRADE_CALIBRATION.md): 랭커 강화 선택 조건부 로짓. 카드별 값은 tiers/*.json upgradeValue.\n'
             f'  //   빌드 역할 점수 = buildRole[역할] × 그 빌드 commit (로짓 계수 × 척도 {scale:.3f}점). 역할 부족 배수 needWeight도 같은 척도(로짓 계수가 거의 0).\n'
             '  "upgrade": {\n'
             '    "buildRole": { ' + ', '.join(f'"{r}": {role_pts[r]}' for r in ROLES) + ' },\n'
             f'    "needWeight": {need_pts}\n'
             '  }')
    idx = text.rstrip().rfind('}')
    text = text[:idx].rstrip() + block + '\n}\n'
    open(path, 'w', encoding='utf8', newline='').write(text)


if __name__ == '__main__':
    main()
