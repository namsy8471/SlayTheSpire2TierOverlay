"""Builds a versioned card pool catalog from decompiled game sources (IDs only, no game text).

Usage: python gen_card_pools.py <decompiled/sts2 dir> <cards catalog json> <out json>

For each character card pool: the card order of CardPoolModel.GenerateAllCards (reward picks index into this
order, so it must be exact) and the epochs whose cards are hidden until revealed (FilterThroughEpochs).
"""
import json, re, sys, os

src_dir, catalog_path, out_path = sys.argv[1:4]
catalog = json.load(open(catalog_path, encoding='utf-8'))
by_letters = {c['id'][5:].replace('_', ''): c['id'] for c in catalog['cards']}


def card_id(cls):
    return by_letters[cls.upper()]


pools_dir = os.path.join(src_dir, 'MegaCrit.Sts2.Core.Models.CardPools')
epochs_dir = os.path.join(src_dir, 'MegaCrit.Sts2.Core.Timeline.Epochs')
out = {'gameVersion': catalog.get('gameVersion'), 'source': 'decompiled CardPools/*.cs GenerateAllCards + Timeline/Epochs/*.cs Cards',
       'pools': {}}
for character in sorted({c['character'] for c in catalog['cards'] if c.get('character')}):
    name = character.removeprefix('CHARACTER.').title().replace('_', '')
    src = open(os.path.join(pools_dir, name + 'CardPool.cs'), encoding='utf-8').read()
    body = src.split('GenerateAllCards')[1]
    cards = [card_id(c) for c in re.findall(r'ModelDb\.Card<(\w+)>\(\)', body.split('FilterThroughEpochs')[0])]
    expected = {c['id'] for c in catalog['cards'] if c.get('character') == character}
    if len(cards) != len(expected) or set(cards) != expected:
        raise ValueError(f'{character}: game pool does not match the card catalog')
    epochs = {}
    for ep in re.findall(r'IsEpochRevealed<(\w+)>', src):
        esrc = open(os.path.join(epochs_dir, ep + '.cs'), encoding='utf-8').read()
        eid = re.search(r'Id => "(\w+)"', esrc).group(1)
        lst = esrc.split('List<CardModel> Cards')[1].split('return list')[0]
        epochs[eid] = [card_id(c) for c in re.findall(r'ModelDb\.Card<(\w+)>\(\)', lst)]
        if not epochs[eid] or not set(epochs[eid]) <= expected:
            raise ValueError(f'{character}: invalid unlock cards for {eid}')
    out['pools'][character] = {'cards': cards, 'epochs': epochs}
json.dump(out, open(out_path, 'w', encoding='utf-8'), indent=1)
print({k: (len(v['cards']), {e: len(c) for e, c in v['epochs'].items()}) for k, v in out['pools'].items()})
