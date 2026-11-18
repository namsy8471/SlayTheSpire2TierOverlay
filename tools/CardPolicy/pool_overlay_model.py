"""Pool (CHARACTER.FOOL) card-reward model for the overlay, from the engine's measured card rule.

The engine picks Pool card rewards with `--run-value <runvalue.json> --run-value-kinds card --run-value-margin 0
--run-value-card-adjust <adjust.json>` (sts2-agent run_value/mod.rs). The run-value head is linear in the deck's card
counts, so adding card X moves the logit by w_X / scale_X whatever the deck, and the adjusted score of a screen is
  card X: logit * (t0 + w_X / scale_X) + c:X + up * [upgraded]      skip: logit * t0 + skip
The common logit * t0 drops out of every comparison. This writes the overlay's card-policy format (one value per card,
upgraded weight, skip tendency; no engine components) so ranking by the overlay's total is ranking by the engine rule.
usage: python3 -I pool_overlay_model.py <runvalue.json> <adjust.json> <out.json> [point_utility]"""
import json, sys
rv, adj, out = sys.argv[1:4]
unit = float(sys.argv[4]) if len(sys.argv) > 4 else 0.005
d = json.load(open(rv)); a = json.load(open(adj))['weights']
names = d['spec']['names']; scale = d['standardize']['scale']
head = d['models'][d['default']]['win']
L = a['logit']
value = {}
for f, w in zip(head['features'], head['w']):
    assert f.startswith('card:') and f != 'card:<other>', f
    value[f[5:]] = L * w / scale[names.index(f)]
for k, w in a.items():
    if k.startswith('c:'): value[k[2:]] = value.get(k[2:], 0.0) + w
cards = {c: {'n': 1000, 'value': round(v, 9), 'act': [0, 0, 0], 'a': [], 'b': []} for c, v in sorted(value.items())}
model = {
    'version': 1,
    'description': 'Pool card reward model: the simulator rule measured on whole runs (per-card run value plus per-card correction). '
                   'u(card) = value[card] + upgradedWeight*upgraded; u(skip) = skipAct. No engine components.',
    'trainedOn': 'simulator whole runs, ascension 0 card-pair screens (Pool)',
    'scale': 1.0, 'pointUtility': unit, 'copiesCap': 3,
    'cardComponents': ['base'], 'cardWeights': [0.0],
    'upgradedWeight': round(a.get('up', 0.0), 9), 'copiesWeight': 0.0,
    'skipComponents': [], 'skipWeights': [],
    'skipAct': [round(a.get('skip', 0.0), 9)] * 3,
    'dim': 0, 'skipA': [], 'cards': cards,
}
json.dump(model, open(out, 'w'), separators=(',', ':'))
vals = sorted(value.values())
print(f'{len(cards)} cards, logit {L:.3f}, up {a.get("up",0):.4f}, skip {a.get("skip",0):.4f}, value min {vals[0]:.3f} max {vals[-1]:.3f}; points = value / {unit}')
