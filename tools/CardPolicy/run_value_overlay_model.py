"""Overlay card-reward model (CardPolicyModel v1, same keys as model/characters/fool.json) from a ranker-free run-value
card rule, for any character.

The engine picks card rewards with `--run-value <runvalue.json> --run-value-kinds card --run-value-margin 0
[--run-value-card-adjust <adjust.json>]` (sts2-agent run_value/mod.rs). The run-value head is linear in the deck's card
counts, so adding card X moves the logit by w_X / scale_X whatever the deck, and the adjusted score of a screen is
  card X: logit * (t0 + w_X / scale_X) + c:X + up * [upgraded]      skip: logit * t0 + skip (+ skip|aN)
The common logit * t0 drops out of every comparison. A card the head has no feature for counts in the engine's
`card:<other>` bucket, so it moves the logit by w_<other> / scale_<other>. The overlay gives a card missing from the model
0, so with --catalog every catalog card the head does not name is written with that <other> value (plus its c: adjust):
ranking by the overlay's total is then ranking by the engine rule on every offered card.
`card_up:<other>` and `relic:<other>` must weigh 0 (no deck-independent overlay term for them); the script stops otherwise.
Without an adjust file the engine ranks by p, which ranks like the logit: logit 1, up 0, skip 0.
Generalises pool_overlay_model.py (Pool).
usage: python3 -I run_value_overlay_model.py <label> <runvalue.json> <adjust.json or -> <out.json>
       [--point-utility U] [--catalog cards.vX.json ...]"""
import argparse, json

ap = argparse.ArgumentParser()
ap.add_argument('label'); ap.add_argument('runvalue'); ap.add_argument('adjust'); ap.add_argument('out')
ap.add_argument('--point-utility', type=float, default=0.005)
ap.add_argument('--catalog', nargs='*', default=[])
args = ap.parse_args()

d = json.load(open(args.runvalue))
a = json.load(open(args.adjust))['weights'] if args.adjust != '-' else {'logit': 1.0}
names = d['spec']['names']; scale = d['standardize']['scale']
head = d['models'][d['default']]['win']
L = a['logit']; value = {}; other = {}
for f, w in zip(head['features'], head['w']):
    if f.endswith(':<other>'):
        other[f] = w / scale[names.index(f)]
        continue
    assert f.startswith('card:'), f
    value[f[5:]] = L * w / scale[names.index(f)]
for f in ('card_up:<other>', 'relic:<other>'):
    assert other.get(f, 0.0) == 0.0, f'{f} weighs {other[f]}: the overlay has no term for it'
other_value = L * other.get('card:<other>', 0.0)
named = len(value)
catalog = {c['id'] for p in args.catalog for c in json.load(open(p))['cards']}
for c in catalog - value.keys(): value[c] = other_value
for k, w in a.items():
    if k.startswith('c:'): value[k[2:]] = value.get(k[2:], other_value) + w
sk = a.get('skip', 0.0); skip_act = [round(sk + a.get(f'skip|a{i}', 0.0), 9) for i in (1, 2, 3)]
cards = {c: {'n': 1000, 'value': round(v, 9), 'act': [0, 0, 0], 'a': [], 'b': []} for c, v in sorted(value.items())}
model = {
    'version': 1,
    'description': f'{args.label} card reward model without ranker data: the simulator rule fitted on whole runs (per-card run value'
                   + (' plus per-card correction' if args.adjust != '-' else '') + '). u(card) = value[card] + upgradedWeight*upgraded; '
                   'u(skip) = skipAct. Cards the rule does not name carry its "other card" value. No engine components.',
    'trainedOn': f'simulator whole runs, card-pair screens ({args.label})',
    'scale': 1.0, 'pointUtility': args.point_utility, 'copiesCap': 3,
    'cardComponents': ['base'], 'cardWeights': [0.0],
    'upgradedWeight': round(a.get('up', 0.0), 9), 'copiesWeight': 0.0,
    'skipComponents': [], 'skipWeights': [], 'skipAct': skip_act,
    'dim': 0, 'skipA': [], 'cards': cards,
}
with open(args.out, 'w') as fh: json.dump(model, fh, separators=(',', ':'))
vals = sorted(value.values())
print(f'{args.out}: {len(cards)} cards ({named} named, {len(cards) - named} at other {other_value:+.4f}), logit {L:.3f}, '
      f'up {a.get("up", 0):.4f}, skipAct {skip_act}, value {vals[0]:.3f}..{vals[-1]:.3f}')
