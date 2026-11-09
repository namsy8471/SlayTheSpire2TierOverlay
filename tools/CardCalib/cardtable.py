"""Per-card pick rates on reward screens: ranker vs engine before vs after, plus the applied change.
usage: cardtable.py base.all.csv main.all.csv main.fit.json guides.csv out.md
"""
import csv, json, sys, collections

base_csv, main_csv, fitj, guides_csv, out = sys.argv[1:6]
NAMES = {}
try:
    loc = json.load(open(sys.argv[6], encoding='utf-8'))
    for k, v in loc.items():
        if k.endswith('.title'): NAMES['CARD.' + k[:-6]] = v
except (IndexError, OSError):
    pass


def rates(path):
    st = collections.defaultdict(lambda: [0, 0, 0])
    for r in csv.DictReader(open(path, encoding='utf-8')):
        if r['context'] == 'Shop': continue
        x = st[(r['character'], r['card'])]; x[0] += 1; x[1] += int(r['ranker_pick']); x[2] += int(r['engine_best'])
    return st


b, m = rates(base_csv), rates(main_csv)
fit = json.load(open(fitj))
g = {(r['character'], r['card']): r for r in csv.DictReader(open(guides_csv, encoding='utf-8'))}
moves = dict(fit['tierMoves1'])
rows = []
for k, v in fit['cards'].items():
    ch, card = k.split('|')
    n, rk, eb = b[(ch, card)]
    _, _, em = m[(ch, card)]
    t = v.get('tier') or moves.get(k, {}).get('tier')
    rows.append((ch, card, n, rk / n if n else 0, eb / n if n else 0, em / n if n else 0, t, v.get('baseAdjust', 0), g.get((ch, card), {})))
for k, v in moves.items():
    if k not in fit['cards']:
        ch, card = k.split('|'); n, rk, eb = b[(ch, card)]; _, _, em = m[(ch, card)]
        rows.append((ch, card, n, rk / n, eb / n, em / n, v['tier'], 0, g.get((ch, card), {})))
with open(out, 'w', encoding='utf-8') as f:
    for ch in sorted({r[0] for r in rows}):
        f.write(f'\n### {ch[10:]}\n\n| 카드 | 제시 | 랭커 선택 | 엔진 전 | 엔진 후 | 티어 | baseAdjust | Baalorlord / Jorbs |\n|---|---|---|---|---|---|---|---|\n')
        rs = sorted([r for r in rows if r[0] == ch and (r[6] or abs(r[7]) >= 8)], key=lambda r: -abs(r[7]) - (20 if r[6] else 0))
        for r in rs:
            nm = NAMES.get(r[1], '')
            f.write(f"| {r[1][5:]}{' (' + nm + ')' if nm else ''} | {r[2]} | {r[3]:.0%} | {r[4]:.0%} | {r[5]:.0%} | {r[6] or ''} | {r[7]:+d} | {r[8].get('baal', '')} / {r[8].get('jorbs', '')} |\n")
print(len(rows))
