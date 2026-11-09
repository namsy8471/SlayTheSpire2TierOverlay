"""Leak pairs from a DeckSim report vs what rankers did when both cards were on the same reward screen.
usage: h2h.py report.md cards.csv [--top 25]
For each 'taken > passed' leak pair (character-level), counts ranker screens with both offered: took taken / took passed / other / skip.
"""
import csv, re, sys, collections, argparse

ap = argparse.ArgumentParser(); ap.add_argument('report'); ap.add_argument('cards'); ap.add_argument('--top', type=int, default=25)
a = ap.parse_args()
pairs = collections.Counter()
for ln in open(a.report, encoding='utf-8'):
    m = re.match(r'- (\w+) \d막 .*추천 (\w+) [\d.]+ .*vs 빌드 (\w+) [\d.]+', ln)
    if m: pairs[(m.group(1), m.group(2), m.group(3))] += 1
screens = collections.defaultdict(list)
for r in csv.DictReader(open(a.cards, encoding='utf-8')):
    if r['context'] == 'Shop': continue
    screens[(r['run'], r['screen'])].append(r)
h = collections.defaultdict(lambda: [0, 0, 0, 0])
for cs in screens.values():
    ch = cs[0]['character'][10:]
    names = {r['card'][5:] for r in cs}
    pick = next((r['card'][5:] for r in cs if r['ranker_pick'] == '1'), None)
    for (c, t, p) in pairs:
        if c == ch and t in names and p in names:
            x = h[(c, t, p)]
            x[0 if pick == t else 1 if pick == p else 3 if pick is None else 2] += 1
print(f"{'pair (engine took > passed build card)':52} sim  | ranker: took/passed/other/skip")
for k, n in pairs.most_common(a.top):
    x = h[k]
    print(f"{k[0]:11} {k[1]:20} > {k[2]:18} {n:4} | {x[0]:3} {x[1]:3} {x[2]:3} {x[3]:3}")
