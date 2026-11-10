import sys, collections
# input: vtune top-down csv (tab), first column indented with spaces by depth, second = CPU Time:Total
rows = []
for i, line in enumerate(open(sys.argv[1], encoding="utf-8", errors="replace")):
    if i == 0: continue
    c = line.rstrip("\n").split("\t")
    if len(c) < 2: continue
    name = c[0]; depth = len(name) - len(name.lstrip(" "))
    try: t = float(c[1])
    except ValueError: continue
    rows.append((depth, name.strip(), t))
incl = collections.Counter()
stack = []  # names on path
for depth, name, t in rows:
    while stack and stack[-1][0] >= depth: stack.pop()
    if name not in {n for _, n in stack}:  # outermost occurrence only
        incl[name] += t
    stack.append((depth, name))
root = incl.get("SimTrial::CombatRunner::RunOne", 1.0)
pat = sys.argv[2] if len(sys.argv) > 2 else ""
for n, t in incl.most_common(400):
    if pat and not any(p in n for p in pat.split("|")): continue
    print(f"{100*t/root:5.1f}%  {t:7.2f}s  {n[:110]}")
