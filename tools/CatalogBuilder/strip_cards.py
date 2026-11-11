"""Local CatalogBuilder card catalog (with game text) -> names-free committed card catalog.

The local catalog (data/local/catalog.<version>.json, never committed) carries card names and
description templates. The committed data/catalog/cards.<version>.json keeps only ids and numbers,
with the same fields and order as cards.v0.107.1.json.

Usage: python -I tools/CatalogBuilder/strip_cards.py data/local/catalog.<version>.json data/catalog/cards.<version>.json
"""
import json
import sys

FIELDS = ['id', 'pool', 'character', 'type', 'rarity', 'cost', 'costX', 'starCost', 'target', 'gainsBlock',
          'maxUpgrade', 'multiplayer', 'keywords', 'tags', 'showInLibrary']
TEXT_FIELDS = {'class', 'names', 'descriptionTemplate', 'unresolved', 'dynamic'}

src, out = sys.argv[1:3]
cat = json.load(open(src, encoding='utf-8'))
cards = [{k: c.get(k) for k in FIELDS} for c in cat['cards']]

res = {
    'gameVersion': cat['gameVersion'],
    'source': 'tools/CatalogBuilder (static IL of sts2.dll)',
    'note': 'Game facts only (IDs and stats). Card names/descriptions are game text and are generated locally into data/local, never committed.',
    'cardCount': len(cards),
    'cards': cards,
}
dropped = sorted({k for c in cat['cards'] for k in c} - set(FIELDS) - TEXT_FIELDS)
print('dropped non-text fields:', dropped)
print('unresolved:', {c['id']: c['unresolved'] for c in cat['cards'] if c.get('unresolved')})
with open(out, 'w', encoding='utf-8', newline='\r\n') as f:
    f.write(json.dumps(res, indent=2, ensure_ascii=False))
    f.write('\n')
print(len(cards), 'cards ->', out)
