"""Step 4b data: turn reward screens into simulator requests, and simulator results into value rows.

  python -P tools/CardPolicy/sim_requests.py make <screens.jsonl> <fights.jsonl> <requests.jsonl>
         [--screens 400] [--acts 1,2] [--build v0.111] [--seeds 2] [--only value.csv] [--seed 0]
  dotnet tools/SimLab/bin/Release/net9.0/SimFuzz.dll api <requests.jsonl> <results.jsonl> --seeds 2   (offline)
  python -P tools/CardPolicy/sim_requests.py collect <requests.jsonl> <results.jsonl> <value.csv>

For each screen: the deck before the screen ("SKIP", variant -1) and deck + each offered card fight every elite and boss
of that act at full HP, no potions. All variants of a (screen, encounter) share seed_base, so they meet the same monsters
(common random numbers). The value CSV is what sim_value.py reads.

Encounter pools come from the corpus itself: act-1 encounters are grouped into the two act-1 variants by which ones
appear in the same run (a run plays one variant), acts 2 and 3 have one pool each.
"""
import argparse
import collections
import csv
import json
import random


def pools(fights_path, build):
    """(run_id -> act-1 group id), {(act, group): sorted elite/boss encounter ids}."""
    parent = {}

    def find(x):
        while parent.setdefault(x, x) != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    run_act1 = collections.defaultdict(set)
    kind = {}
    later = collections.defaultdict(collections.Counter)
    with open(fights_path, encoding='utf-8') as f:
        for line in f:
            r = json.loads(line)
            if not r['build_id'].startswith(build):
                continue
            kind[r['encounter']] = r['room_type'].capitalize()
            if r['act'] == 1:
                run_act1[r['run_id']].add(r['encounter'])
            else:
                later[r['act']][r['encounter']] += 1
    for encs in run_act1.values():
        encs = sorted(encs)
        for e in encs[1:]:
            parent[find(e)] = find(encs[0])
    group_of_run = {run: find(min(encs)) for run, encs in run_act1.items()}
    out = collections.defaultdict(set)
    for encs in run_act1.values():
        for e in encs:
            if kind[e] in ('Elite', 'Boss'):
                out[(1, find(e))].add(e)
    for act, counts in later.items():
        for e, n in counts.items():
            if kind[e] in ('Elite', 'Boss') and n >= 5:
                out[(act, 0)].add(e)
    return group_of_run, {k: sorted(v) for k, v in out.items()}, kind


def make(args):
    group_of_run, pool, kind = pools(args.fights, args.build)
    for (act, g), encs in sorted(pool.items()):
        print(f'act {act} pool {g if act == 1 else "-"}: {len(encs)} encounters: ' + ', '.join(e[10:] for e in encs))
    acts = {int(a) for a in args.acts.split(',')}
    only = None
    if args.only:
        with open(args.only, encoding='utf-8') as f:
            only = collections.defaultdict(set)
            for r in csv.DictReader(f):
                if r['card'] != 'SKIP':
                    only[(r['run_id'], int(r['node_index']))].add(r['card'])
    done = set()
    for path in args.exclude:
        with open(path, encoding='utf-8') as f:
            done |= {(r['run_id'], int(r['node_index'])) for r in csv.DictReader(f)}
    screens = []
    with open(args.screens_file, encoding='utf-8') as f:
        for line in f:
            s = json.loads(line)
            if (s['run_id'], s['node_index']) in done:
                continue
            if s['build_id'].startswith(args.build) and s['act'] in acts and s['run_id'] in group_of_run:
                key = (1, group_of_run[s['run_id']]) if s['act'] == 1 else (s['act'], 0)
                if any(e.startswith('ENCOUNTER.ACTSFROMTHEPAST') for e in pool.get(key, ['ENCOUNTER.ACTSFROMTHEPAST'])):
                    continue  # the legacy act-1 variant is not in the simulator
                if only is None or only.get((s['run_id'], s['node_index'])) == {option_token(o) for o in s['options']}:
                    screens.append(s)
    random.Random(args.seed).shuffle(screens)
    if only is None:
        screens = screens[:args.screens]
    n = 0
    files = [open(args.out if args.chunks == 1 else f'{args.out}.{i}', 'w', encoding='utf-8', newline='\n')
             for i in range(args.chunks)]
    try:
        for si, s in enumerate(screens):
            f = files[si * args.chunks // len(screens)]
            key = (1, group_of_run[s['run_id']]) if s['act'] == 1 else (s['act'], 0)
            variants = [None] + list(range(len(s['options'])))
            for enc in pool[key]:
                base = f"{s['run_id']}_{s['node_index']}_{s['group']}_{enc[10:]}"
                for v in variants:
                    deck = list(s['deck'])
                    card = 'SKIP'
                    if v is not None:
                        o = s['options'][v]
                        card = option_token(o)
                        deck.append(card)
                    req = {'id': f"{s['run_id']}|{s['node_index']}|{s['group']}|{-1 if v is None else v}|{card}|{enc}|{kind[enc]}|"
                                 f"{s['act']}|{s['floor']}|{s['max_hp']}|{len(s['deck'])}|{s['pick']}|{s['character']}",
                           'character': s['character'], 'deck': deck, 'relics': s['relics'], 'hp': s['max_hp'],
                           'max_hp': s['max_hp'], 'encounter': enc, 'act': s['act'], 'seeds': args.seeds, 'seed_base': base}
                    f.write(json.dumps(req, separators=(',', ':')) + '\n')
                    n += 1
    finally:
        for f in files:
            f.close()
    print(f'{len(screens)} screens -> {n} requests ({n * args.seeds} combats)')


COLUMNS = ['run_id', 'node_index', 'character', 'act', 'floor', 'max_hp', 'deck_size', 'pick', 'variant', 'card',
           'encounter', 'room_type', 'seed', 'won', 'died', 'timed_out', 'damage', 'turns', 'enemy_hp_left', 'error', 'group']


def option_token(o):
    return o['card'] + (f"+{o['upgrade']}" if o['upgrade'] > 0 else '')


def meta_group(rows):
    return rows[0][0]['id'].split('|')[2]


def collect(args):
    with open(args.results, encoding='utf-8') as f:
        results = [json.loads(line) for line in f if line.strip()]
    # A (screen, encounter) is kept only if every variant finished every seed (timeouts are dropped by the API and
    # would break the seed pairing).
    by_pair = collections.defaultdict(list)
    for r in results:
        run, node, group, v, card, enc, room, act, floor, max_hp, deck_size, pick, ch = r['id'].split('|')
        by_pair[(run, node, group, enc)].append((r, (run, node, ch, act, floor, max_hp, deck_size, pick, v, card, enc, room)))
    kept = dropped = 0
    with open(args.value, 'w', encoding='utf-8', newline='') as f:
        w = csv.writer(f)
        w.writerow(COLUMNS)
        for rows in by_pair.values():
            seeds = max(len(r['outcomes']) for r, _ in rows)
            if any(r.get('error') or len(r['outcomes']) < seeds for r, _ in rows) or seeds == 0:
                dropped += 1
                continue
            kept += 1
            for r, meta in rows:
                for k, o in enumerate(r['outcomes']):
                    w.writerow([*meta, k, int(o['won']), int(o['died']), 0, o['damage'], o['turns'], '', '', meta_group(rows)])
    print(f'(screen, encounter) groups kept {kept}, dropped {dropped} (error or timeout in some variant)')


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest='cmd', required=True)
    m = sub.add_parser('make')
    m.add_argument('screens_file')
    m.add_argument('fights')
    m.add_argument('out')
    m.add_argument('--screens', type=int, default=400)
    m.add_argument('--acts', default='1,2')
    m.add_argument('--build', default='v0.111')
    m.add_argument('--seeds', type=int, default=2)
    m.add_argument('--seed', type=int, default=0)
    m.add_argument('--only', help='reuse the screens of an earlier value CSV')
    m.add_argument('--exclude', action='append', default=[], help='skip the screens of an earlier value CSV')
    m.add_argument('--chunks', type=int, default=1, help='split the requests into N files by screen (<out>.0 ...)')
    c = sub.add_parser('collect')
    c.add_argument('requests')
    c.add_argument('results')
    c.add_argument('value')
    args = ap.parse_args()
    make(args) if args.cmd == 'make' else collect(args)


if __name__ == '__main__':
    main()
