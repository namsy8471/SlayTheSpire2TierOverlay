"""Card score calibration pipeline (docs/research/CARD_CALIBRATION.md). Ranker runs stay on this machine; nothing here is committed.
  0. need urgency: fixed 5-cell recipe (act1 draw, act2/3 frontload+aoe) - or 'fit' / 'none'
  1. patched cards (known list + drift check) -> judged on v0.107.x runs only
  2. tier letters: directional bootstrap; v2 changed/confirmed cards need 95%, uncertain/other 80%
  3. conditional logit -> per-card offset = delta / divisor (0.08 default), tanh soft cap,
     residual cap +-10 for v2-backed or moved cards, +-15 otherwise; offset tier moves only for v2-uncertain cards.

usage: python pipeline.py fit --runs <dir> --index idx.csv --base <pack> --out <pack> [--guides-csv g.csv] [--tier-allow allow.json]
       pipeline.py cv  --runs <dir> --index idx.csv --base <pack> --work <dir> [options]   (2-fold by player, both directions)
       pipeline.py eval --runs <dir> --index idx.csv --base <pack>
"""
import argparse, csv, json, os, re, shutil, subprocess, sys, collections, math, random

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..'))          # tools/CardCalib -> repo root
PY = sys.executable                                               # needs numpy + scipy
CATALOG = os.path.join(REPO, 'data', 'catalog', 'cards.v0.107.1.json')
FILES = {'CHARACTER.IRONCLAD': 'ironclad', 'CHARACTER.SILENT': 'silent', 'CHARACTER.DEFECT': 'defect',
         'CHARACTER.NECROBINDER': 'necrobinder', 'CHARACTER.REGENT': 'regent'}
ORD = 'DCBAS'
ROLES = ['frontload', 'aoe', 'block', 'scaling', 'draw']
URG = {1: {'frontload': 1.4, 'aoe': 1.0, 'block': 0.8, 'scaling': 0.5, 'draw': 0.6},
       2: {'frontload': 1.0, 'aoe': 1.1, 'block': 1.3, 'scaling': 1.1, 'draw': 0.9},
       3: {'frontload': 0.8, 'aoe': 1.0, 'block': 1.2, 'scaling': 1.2, 'draw': 1.2}}
# Change only the five cells both CV halves agreed on (CARD_CALIBRATION.md §3); keep the other ten.
FIXED = {1: {'draw': 0.18}, 2: {'frontload': 0.7, 'aoe': 0.75}, 3: {'frontload': 0.6, 'aoe': 0.7}}
V2STATUS = None   # set in main(): v2status.py output for docs/research/v2-stats/tiers.md
GUIDES_CSV = None # set in main(): character,card,baal,jorbs,baal_n,jorbs_n,verdict (tier letters from the guides; not committed)


def run(cmd):
    r = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace')
    if r.returncode != 0:
        sys.stderr.write(r.stdout[-2000:] + r.stderr[-2000:]); raise SystemExit(r.returncode)
    return r.stdout


def subset_dir(runs, ids, dest):
    if os.path.exists(dest): shutil.rmtree(dest)
    os.makedirs(dest)
    for i in ids:
        src = os.path.join(runs, i + '.run')
        if os.path.exists(src):
            try: os.link(src, os.path.join(dest, i + '.run'))
            except OSError: shutil.copy(src, dest)
    return dest


def dump(pack, runsdir, out):
    run(['dotnet', 'run', '--project', os.path.join(REPO, 'tools', 'DeckSim'), '-c', 'Release', '--no-build', '--', '--pack', pack,
         '--catalog', CATALOG, '--rankers', runsdir, '--cards', out, '--losses', '--policies', 'none'])
    return out


def read_tiers(pack):
    cur = {}
    for ch, f in FILES.items():
        for ln in open(os.path.join(pack, 'tiers', f + '.json'), encoding='utf-8'):
            m = re.match(r'\s*"(CARD\.\w+)": \{"tier": "(\w)"', ln)
            if m: cur[(ch, m.group(1))] = m.group(2)
    return cur


def read_dupfactors(pack):
    cur = {}
    for ch, f in FILES.items():
        for ln in open(os.path.join(pack, 'tiers', f + '.json'), encoding='utf-8'):
            m = re.match(r'\s*"(CARD\.\w+)": \{.*"dupFactor": ([\d.]+)', ln)
            if m: cur[(ch, m.group(1))] = float(m.group(2))
    return cur


def _parse_dup(s):
    if not s: return 0.0
    for part in s.split(';'):
        if part.startswith('duplicate='):
            return float(part.split('=', 1)[1])
    return 0.0


def fit_dupfactors(csvpath, index_path, cur_tiers, cur_df, edits2, notes, floor=0.2):
    """Per-card dupFactor from screens where the card came up again while the deck already had a copy (duplicate < 0).
    Counts how often the engine (after this fit's baseAdjust / tier edits) and the ranker take that second copy, and moves
    dupFactor on a grid only when both halves of the training players keep or gain top-1 hits and the gap shrinks.
    Raising it (fewer copies) is allowed only for cards rankers rarely double (<= 12 %): the engine's argmax overshoots
    on cards rankers take about half the time, so E > R alone is not evidence. CARD_CALIBRATION.md §9.1."""
    idx = {r['runId']: r for r in csv.DictReader(open(index_path, encoding='utf-8'))}
    player = lambda run: idx.get(run, {}).get('player_key') or ('run:' + run)
    sc = collections.OrderedDict()
    for r in csv.DictReader(open(csvpath, encoding='utf-8')):
        if r['context'] == 'Shop' or idx.get(r['run'], {}).get('keep', '1') != '1': continue
        ck = (r['character'], r['card'])
        e = edits2.get(ck, {})
        t_shift = 15 * (ORD.index(e['tier']) - ORD.index(cur_tiers[ck])) if 'tier' in e else 0
        r['_tot'] = float(r['total']) + e.get('baseAdjust', 0) + t_shift
        r['_dup'] = _parse_dup(r['components'])
        sc.setdefault((r['run'], r['screen']), []).append(r)
    card_sc = collections.defaultdict(list)
    for key, cs in sc.items():
        for r in cs:
            if r['_dup'] < 0:
                card_sc[(r['character'], r['card'])].append((key, cs))
    tr_players = sorted({player(k[0]) for k in sc.keys()})
    sub_half = set(tr_players[::2])
    grid = tuple(g for g in (0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 1.0, 1.3, 1.5, 1.8, 2.0, 2.5) if g >= floor)
    for ck, sc_list in sorted(card_sc.items()):
        uniq_sc = list({k: cs for k, cs in sc_list}.items())
        if len(uniq_sc) < 15: continue
        pls = {player(k[0]) for k, _ in uniq_sc}
        if len(pls) < 4: continue
        df0 = cur_df.get(ck, 1.0)
        if df0 <= 0: continue
        sc_A = [cs for k, cs in uniq_sc if player(k[0]) in sub_half]
        sc_B = [cs for k, cs in uniq_sc if player(k[0]) not in sub_half]
        if not sc_A or not sc_B: continue
        def stats_for(sub, df_val, c_key=ck, d0=df0):
            h = ep = rp = 0
            for cs in sub:
                sk = float(cs[0]['skip_total'])
                best_j = None; best_s = sk
                for j, r in enumerate(cs):
                    s = r['_tot']
                    if r['_dup'] < 0 and (r['character'], r['card']) == c_key:
                        s += r['_dup'] * (df_val / d0 - 1.0)
                    if s > best_s: best_s = s; best_j = j
                r_j = next((j for j, r in enumerate(cs) if r['ranker_pick'] == '1'), None)
                if best_j == r_j: h += 1
                if best_j is not None and (cs[best_j]['character'], cs[best_j]['card']) == c_key and cs[best_j]['_dup'] < 0: ep += 1
                if r_j is not None and (cs[r_j]['character'], cs[r_j]['card']) == c_key and cs[r_j]['_dup'] < 0: rp += 1
            return h, ep, rp
        hA0, epA0, rpA0 = stats_for(sc_A, df0)
        hB0, epB0, rpB0 = stats_for(sc_B, df0)
        h0 = hA0 + hB0; ep0 = epA0 + epB0; rp0 = rpA0 + rpB0
        gap0 = abs(ep0 - rp0)
        if gap0 < 4: continue
        if ep0 > rp0 and rp0 / len(uniq_sc) > 0.12: continue
        best_df = df0
        best_score = (h0, -gap0)
        for g in grid:
            if (ep0 < rp0 and g >= df0) or (ep0 > rp0 and g <= df0): continue
            hA, epA, rpA = stats_for(sc_A, g)
            hB, epB, rpB = stats_for(sc_B, g)
            if hA >= hA0 and hB >= hB0 and (hA + hB > h0 + 1 or (hA + hB >= h0 and abs(epA + epB - rp0) <= gap0 - 4)):
                if abs(epA - rpA) <= abs(epA0 - rpA0) and abs(epB - rpB) <= abs(epB0 - rpB0):
                    cand = (hA + hB, -abs(epA + epB - rp0), -abs(g - df0))
                    if cand > (best_score[0], best_score[1], -abs(best_df - df0)):
                        best_score = (cand[0], cand[1])
                        best_df = g
        if best_df != df0:
            edits2.setdefault(ck, {})['dupFactor'] = best_df
            notes.append(f"dupFactor {ck[1]} {df0:g}->{best_df:g} (n={len(uniq_sc)}, R={rp0}, E={ep0})")


def write_cards(pack, edits):
    """edits[(ch, card)] = {'tier': 'B', 'baseAdjust': int or 0}"""
    for ch, f in FILES.items():
        p = os.path.join(pack, 'tiers', f + '.json')
        text = open(p, encoding='utf-8', newline='').read()
        lines = text.split('\n')
        for i, ln in enumerate(lines):
            m = re.match(r'(\s*)"(CARD\.\w+)": \{', ln)
            if not m or (ch, m.group(2)) not in edits: continue
            e = edits[(ch, m.group(2))]
            if 'tier' in e:
                ln = re.sub(r'"tier": "\w"', '"tier": "%s"' % e['tier'], ln, count=1)
            ln = re.sub(r', "baseAdjust": -?\d+', '', ln)
            if e.get('baseAdjust'):
                if '"upgradedDelta"' in ln:
                    ln = re.sub(r'("upgradedDelta": -?[\d.]+)', r'\1, "baseAdjust": %d' % e['baseAdjust'], ln, count=1)
                else:
                    ln = re.sub(r'("tier": "\w")', r'\1, "baseAdjust": %d' % e['baseAdjust'], ln, count=1)
            if 'dupFactor' in e:
                df_str = ('%.2f' % e['dupFactor']).rstrip('0').rstrip('.')
                ln = re.sub(r'"dupFactor": [\d.]+', '"dupFactor": %s' % df_str, ln, count=1)
            if e.get('calibrated'):
                ln = re.sub(r'"confidence": 0\.45\b', '"confidence": 0.65', ln, count=1)
            lines[i] = ln
        open(p, 'w', encoding='utf-8', newline='').write('\n'.join(lines))


def write_urgency(pack, urg):
    p = os.path.join(pack, 'weights.json')
    text = open(p, encoding='utf-8', newline='').read()
    nl = chr(13) + chr(10) if chr(13) + chr(10) in text else chr(10)
    lines = text.split(nl)
    if '  "urgency": {' in lines:
        i = lines.index('  "urgency": {')
        j = i
        while lines[j] != '  },': j += 1
        while lines[i - 1].startswith('  // 2026-11 카드 점수 보정') or lines[i - 1].startswith('  //   '): i -= 1
        del lines[i:j + 1]
    body = ['    "%d": { %s }%s' % (a, ', '.join('"%s": %s' % (r, ('%.2f' % urg[a][r]).rstrip('0').rstrip('.')) for r in ROLES),
                                   ',' if a < 3 else '') for a in (1, 2, 3)]
    block = ['  // 2026-11 카드 점수 보정(docs/research/CARD_CALIBRATION.md): 랭커 선택으로 다시 맞춘 막별 역할 긴급도.',
             '  //   바꾼 칸은 1막 draw, 2·3막 frontload·aoe 다섯 칸뿐이고 나머지는 코드 기본값과 같습니다.',
             '  "urgency": {'] + body + ['  },']
    k = lines.index('{')
    lines[k + 1:k + 1] = block
    open(p, 'w', encoding='utf-8', newline='').write(nl.join(lines))


def archetype_cards(pack):
    s = set()
    for f in os.listdir(os.path.join(pack, 'archetypes')):
        s.update(re.findall(r'CARD\.[A-Z0-9_]+', open(os.path.join(pack, 'archetypes', f), encoding='utf-8').read()))
    return s


def guides():
    g = {}
    if not GUIDES_CSV:
        return g
    for r in csv.DictReader(open(GUIDES_CSV, encoding='utf-8')):
        g[(r['character'], r['card'])] = r
    return g


def fit(a, train_ids, out_pack, log):
    shutil.rmtree(out_pack, ignore_errors=True); shutil.copytree(a.base, out_pack)
    tr_dir = subset_dir(a.runs, train_ids, out_pack + '.runs')
    status = json.load(open(V2STATUS))
    # 0. need urgency first, so every later dump sees it
    urg = {act: dict(URG[act]) for act in (1, 2, 3)}
    if a.need == 'fixed':
        for act, cells in FIXED.items():
            urg[act].update(cells)
    if a.need != 'fit':
        write_urgency(out_pack, urg)
    # 1. patched cards + tier letters
    c0 = dump(out_pack, tr_dir, out_pack + '.cards0.csv')
    pj = out_pack + '.patched.json'
    log.append(run([sys.executable, '-I', os.path.join(HERE, 'drift.py'), c0, a.index, pj]))
    if not a.patched:
        json.dump({k: v for k, v in json.load(open(pj)).items() if v == 'known'}, open(pj, 'w'))
    rr = out_pack + '.rerank.json'
    log.append(run([sys.executable, '-I', os.path.join(HERE, 'tier_rerank.py'), c0, a.index, os.path.join(out_pack, 'tiers'), rr,
                    '--boot', str(a.boot), '--status', V2STATUS, '--patched', pj]))
    rer = json.load(open(rr))
    allow = set(json.load(open(a.tier_allow))) if a.tier_allow else None
    edits = {}
    if a.tier_moves:
        for v in rer.values():
            if v['new'] != v['current'] and (allow is None or f"{v['character']}|{v['card']}" in allow):
                edits[(v['character'], v['card'])] = {'tier': v['new']}
        write_cards(out_pack, edits)
    # 2. conditional logit on the corrected tiers
    c1 = dump(out_pack, tr_dir, out_pack + '.cards1.csv')
    oj = out_pack + '.clogit.json'
    cmd = [PY, '-P', os.path.join(HERE, 'clogit2.py'), c1, '--index', a.index, '--players', a.players, '--lam', str(a.lam),
           '--folds', '2', '--json', oj, '--patched', pj]
    if a.need == 'fit':
        cmd += ['--need', '--lam-m', str(a.lam_m)]
    log.append(run(cmd))
    cl = json.load(open(oj))
    beta = cl['beta']
    if a.need == 'fit':
        for act in (1, 2, 3):
            for i, r in enumerate(ROLES):
                urg[act][r] = round(URG[act][r] * max(0.0, min(1.5, cl['need'][act - 1][i])) * 20) / 20
        write_urgency(out_pack, urg)
    # 3. per-card offsets -> baseAdjust (+ a tier move for large ones, only on cards v2 left uncertain)
    div = beta if a.divisor == 'beta' else float(a.divisor)
    cur = read_tiers(out_pack)
    g = guides() if a.guides else {}
    arch = archetype_cards(out_pack)
    edits2 = {}
    notes = []
    for c in cl['cards']:
        k = (c['character'], c['card'])
        if k not in cur or c['n'] < a.min_n: continue
        st = status.get(f'{k[0]}|{k[1]}', 'other')
        backed = st in ('changed', 'confirmed')
        off = c['delta'] / div
        gg = g.get(k)
        if gg and a.guides:
            gb = [float(x) for x in (gg['baal_n'], gg['jorbs_n']) if x]
            t = ORD.index(cur[k]) + 1                      # D1 .. S5
            sides = [(x - t) for x in gb]
            if len(sides) == 2 and all(sd * off < 0 and abs(sd) >= 1 for sd in sides) and c['card'] not in arch \
                    and 'act 1' not in gg['verdict'].lower() and 'act-1' not in gg['verdict'].lower():
                notes.append(f"halved {k[1]} off {off:+.1f} guides {gg['baal']}/{gg['jorbs']}")
                off *= 0.5
        tier = cur[k]
        moved = k in edits
        if a.tier_moves and not backed and not moved and abs(off) >= 12 and gg and (allow is None or f'{k[0]}|{k[1]}' in allow):
            gb = [float(x) for x in (gg['baal_n'], gg['jorbs_n']) if x]
            avg = sum(gb) / len(gb) if gb else None
            t = ORD.index(tier) + 1
            agree = avg is not None and ((off > 0 and avg >= t + 0.5) or (off < 0 and avg <= t - 0.5))
            ni = ORD.index(tier) + (1 if off > 0 else -1)
            if agree and 0 <= ni < len(ORD) and not (off > 0 and ORD[ni] == 'S'):
                notes.append(f"tier {k[1]} {tier}->{ORD[ni]} off {off:+.1f}")
                off -= 15 if off > 0 else -15
                tier = ORD[ni]; moved = True
        adj = 15 * math.tanh(off / 15) if a.soft else max(-15, min(15, off))
        cap = 10 if (backed or moved) else 15
        adj = int(round(max(-cap, min(cap, adj))))
        e = {'calibrated': True}
        if tier != cur[k]: e['tier'] = tier
        if adj: e['baseAdjust'] = adj
        edits2[k] = e
    if a.dupfactor:
        fit_dupfactors(c1, a.index, cur, read_dupfactors(out_pack), edits2, notes, a.dup_floor)
    write_cards(out_pack, edits2)
    json.dump({'tierMoves1': {f'{k[0]}|{k[1]}': v for k, v in edits.items()},
               'cards': {f'{k[0]}|{k[1]}': v for k, v in edits2.items()}, 'urgency': urg, 'beta': beta, 'notes': notes,
               'cv': cl['cv'], 'need': cl['need'], 'patched': json.load(open(pj))},
              open(out_pack + '.fit.json', 'w'), indent=1)
    return out_pack


def screens_of(csvpath, idx):
    sc = collections.OrderedDict()
    for r in csv.DictReader(open(csvpath, encoding='utf-8')):
        if r['context'] == 'Shop' or idx.get(r['run'], {}).get('keep', '1') != '1': continue
        sc.setdefault((r['run'], r['screen']), []).append(r)
    return sc


def _arr(sc):
    import numpy as np
    O = max(len(v) for v in sc.values()) + 1
    X = np.full((len(sc), O), np.nan); A = np.zeros(len(sc), int); P = np.zeros(len(sc), int)
    L = np.zeros(len(sc), int)
    for n, cs in enumerate(sc.values()):
        for j, r in enumerate(cs): X[n, j] = float(r['total'])
        X[n, len(cs)] = float(cs[0]['skip_total']); A[n] = min(3, max(1, int(cs[0]['act']))) - 1
        P[n] = next((j for j, r in enumerate(cs) if r['ranker_pick'] == '1'), len(cs)); L[n] = len(cs)
    return X, A, P, L


def _nll(th, X, A, P, L):
    import numpy as np
    rows = np.arange(len(X))
    u = th[0] * np.nan_to_num(X, nan=0.0)
    u[rows, L] += th[1:4][A]
    u = np.where(np.isnan(X), -1e9, u)
    mx = u.max(axis=1, keepdims=True)
    lse = mx[:, 0] + np.log(np.exp(u - mx).sum(axis=1))
    return -(u[rows, P] - lse).mean()


def ll_fit_eval(train_sc, test_sc):
    """u = beta * total (+ s[act] on skip), fitted on train screens; returns mean test log-likelihood and beta."""
    import numpy as np
    from scipy.optimize import minimize
    Xt = _arr(train_sc); Xe = _arr(test_sc)
    r = minimize(_nll, np.array([0.05, 0.0, 0.0, 0.0]), args=Xt, method='Nelder-Mead',
                 options={'maxiter': 4000, 'xatol': 1e-6, 'fatol': 1e-8})
    return -_nll(r.x, *Xe), r.x[0]


def top1(sc, only=None):
    hit = n = 0
    for key, cs in sc.items():
        if only is not None and key not in only: continue
        rp = next((r['card'] for r in cs if r['ranker_pick'] == '1'), None)
        ep = next((r['card'] for r in cs if r['engine_best'] == '1'), None)
        n += 1; hit += int(rp == ep)
    return hit / max(1, n), n


def confident(sc, gap=10.0):
    out = set()
    for key, cs in sc.items():
        v = sorted([float(r['total']) for r in cs] + [float(cs[0]['skip_total'])], reverse=True)
        if len(v) >= 2 and v[0] - v[1] >= gap: out.add(key)
    return out


def metrics(pack, runs, tr, te, idx, work, conf=None):
    ctr = dump(pack, subset_dir(runs, tr, work + '.tr.runs'), work + '.tr.csv')
    cte = dump(pack, subset_dir(runs, te, work + '.te.runs'), work + '.te.csv')
    s_tr, s_te = screens_of(ctr, idx), screens_of(cte, idx)
    ll, b = ll_fit_eval(s_tr, s_te)
    t, n = top1(s_te)
    skipE = collections.Counter(); per = collections.Counter()
    for cs in s_te.values():
        per[cs[0]['act']] += 1; skipE[cs[0]['act']] += int(not any(r['engine_best'] == '1' for r in cs))
    m = {'screens': n, 'top1': t, 'll': ll, 'beta': b, 'skip_engine': {k: round(skipE[k] / per[k], 3) for k in sorted(per)}}
    if conf is not None:
        m['conf_top1'], m['conf_n'] = top1(s_te, conf)
    return m, s_te


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('mode'); ap.add_argument('--runs'); ap.add_argument('--index'); ap.add_argument('--base'); ap.add_argument('--out')
    ap.add_argument('--work'); ap.add_argument('--divisor', default='0.08'); ap.add_argument('--soft', type=int, default=1)
    ap.add_argument('--guides', type=int, default=1); ap.add_argument('--lam', type=float, default=1.0); ap.add_argument('--lam-m', type=float, default=5.0)
    ap.add_argument('--boot', type=int, default=200); ap.add_argument('--players', default='all'); ap.add_argument('--tier-moves', type=int, default=1)
    ap.add_argument('--need', default='fixed'); ap.add_argument('--min-n', type=int, default=30); ap.add_argument('--seed', type=int, default=5)
    ap.add_argument('--patched', type=int, default=1, help='1: known list + drift check, 0: known list only')
    ap.add_argument('--tier-allow', default='', help='json list of CHAR|CARD keys allowed to change tier (e.g. moves both CV halves agree on)')
    ap.add_argument('--guides-csv', default='', help='guide tier letters (Baalorlord / Jorbs); without it no guide rule is applied')
    ap.add_argument('--dupfactor', type=int, default=1, help='1: also fit per-card dupFactor (fit_dupfactors), 0: leave dupFactor as in --base')
    ap.add_argument('--dup-floor', type=float, default=0.2, help='lowest per-card dupFactor fit_dupfactors may pick')
    ap.add_argument('--v2status', default='', help='json from v2status.py; built from docs/research/v2-stats/tiers.md when left out')
    a = ap.parse_args()
    global V2STATUS, GUIDES_CSV
    GUIDES_CSV = a.guides_csv or None
    V2STATUS = a.v2status
    if not V2STATUS:
        V2STATUS = os.path.join(a.work or os.path.dirname(os.path.abspath(a.out or a.base)), 'v2status.json')
        run([sys.executable, '-I', os.path.join(HERE, 'v2status.py'), os.path.join(REPO, 'docs', 'research', 'v2-stats', 'tiers.md'), V2STATUS])
    idx = {r['runId']: r for r in csv.DictReader(open(a.index, encoding='utf-8'))}
    ids = [i for i, r in idx.items() if r.get('keep', '1') == '1' and os.path.exists(os.path.join(a.runs, i + '.run'))]
    if a.mode == 'fit':
        log = []
        fit(a, ids, a.out, log)
        print('\n'.join(log))
    elif a.mode == 'cv':
        os.makedirs(a.work, exist_ok=True)
        players = sorted({idx[i]['player_key'] or ('run:' + i) for i in ids})
        random.seed(a.seed); random.shuffle(players)
        half = set(players[::2])
        A = [i for i in ids if (idx[i]['player_key'] or ('run:' + i)) in half]
        Aset = set(A)
        B = [i for i in ids if i not in Aset]
        res = []
        for name, tr, te in (('A', A, B), ('B', B, A)):
            log = []
            bp = os.path.join(a.work, 'base' + name)
            shutil.rmtree(bp, ignore_errors=True); shutil.copytree(a.base, bp)
            m0, s0 = metrics(bp, a.runs, tr, te, idx, bp)
            conf = confident(s0)
            m0['conf_top1'], m0['conf_n'] = top1(s0, conf)
            pk = os.path.join(a.work, 'pack' + name)
            fit(a, tr, pk, log)
            m1, _ = metrics(pk, a.runs, tr, te, idx, pk, conf)
            res.append((m0, m1))
            print(f"fold {name}: train {len(tr)} test {len(te)} | top1 {m0['top1']:.3f} -> {m1['top1']:.3f} | ll {m0['ll']:.4f} -> {m1['ll']:.4f}"
                  f" | confident({m0['conf_n']}) {m0['conf_top1']:.3f} -> {m1['conf_top1']:.3f} | skip e {m1['skip_engine']} (base {m0['skip_engine']})")
        W = lambda j, key: sum(r[j][key] * r[j]['screens'] for r in res) / sum(r[j]['screens'] for r in res)
        Wc = lambda j: sum(r[j]['conf_top1'] * r[j]['conf_n'] for r in res) / sum(r[j]['conf_n'] for r in res)
        print(f"CV: top1 {W(0, 'top1'):.3f} -> {W(1, 'top1'):.3f} | ll {W(0, 'll'):.4f} -> {W(1, 'll'):.4f} | confident {Wc(0):.3f} -> {Wc(1):.3f}")
        json.dump(res, open(os.path.join(a.work, 'cv.json'), 'w'), indent=1)
    elif a.mode == 'eval':
        c = dump(a.base, subset_dir(a.runs, ids, a.base + '.all.runs'), a.base + '.all.csv')
        sc = screens_of(c, idx)
        print(json.dumps({'top1': top1(sc)}, indent=1))


if __name__ == '__main__':
    main()
