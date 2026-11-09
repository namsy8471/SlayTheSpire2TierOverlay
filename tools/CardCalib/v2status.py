"""Parse docs/research/v2-stats/tiers.md -> {CHAR|CARD: changed|confirmed|uncertain|other}."""
import re, sys, json
CH = {'ironclad': 'CHARACTER.IRONCLAD', 'silent': 'CHARACTER.SILENT', 'defect': 'CHARACTER.DEFECT',
      'necrobinder': 'CHARACTER.NECROBINDER', 'regent': 'CHARACTER.REGENT'}
out = {}; ch = None; st = None
for ln in open(sys.argv[1], encoding='utf-8'):
    m = re.match(r'## .*\(`(\w+)\.json`\)', ln)
    if m: ch = CH.get(m.group(1)); st = None; continue
    if ln.startswith('### '):
        st = 'changed' if '티어 변경' in ln else 'confirmed' if '같은 티어' in ln else 'uncertain' if '불확실' in ln else 'other' if '그 밖에' in ln else None
        continue
    if not ch or not st: continue
    m = re.match(r'\| ([A-Z0-9_]+) \|', ln) or re.match(r'- ([A-Z0-9_]+) \(', ln)
    if m: out[f'{ch}|CARD.{m.group(1)}'] = st
json.dump(out, open(sys.argv[2], 'w'), indent=0)
import collections; print(collections.Counter(out.values()))
