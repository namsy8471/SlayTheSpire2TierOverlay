import sys, collections, re
rows=[]
for i,line in enumerate(open(sys.argv[1],encoding='utf-8',errors='replace')):
    if i==0: continue
    c=line.rstrip('\n').split('\t')
    if len(c)<4: continue
    name=c[0]; d=len(name)-len(name.lstrip(' '))
    try: s=float(c[2])
    except ValueError: continue
    rows.append((d,name.strip(),s,c[3]))
SUB=[
 ('L1 witness scan (ours)', ['WitnessCreatures','WitnessPile','WitnessRef','WitnessInt']),
 ('L2 rebuild list', ['AppendCreatureHookListeners','AppendPileHookListeners','BuildHookListeners']),
 ('L3 engine cost snapshot', ['GetHookListenerSnapshot','ScanListenerSources','ScanCreatureSources','ScanPileSources','HookListenerSnapshot','GetCostHookListeners','SnapshotRunHookListeners']),
 ('L4 iterate all listeners (lazy iterator)', ['IterateHookListeners','IterateCombatHookListeners','IterateDirectCombatHookListeners']),
 ('L5 IsEligibleHookListener', ['IsEligibleHookListener']),
 ('L6 query filter', ['HookQueryFilter','FilteredCombatListeners','ValidCombatListeners','CombatListenersCached']),
 ('C clone', ['CombatState::Clone','CloneOwnedState','CloneForCombat','CloneGraph','CloneExact']),
 ('M mapgen', ['BuildActMap']),
 ('E eval/search', ['Eval::','BeamPolicy::Signature','BeamPolicy::CardKey','BeamPolicy::Moves']),
 ('H hook dispatch loops (Hook.*)', ['Hooks::Hook::','Hook::<']),
]
tot=collections.Counter(); hooks=collections.Counter(); stack=[]
for d,name,s,mod in rows:
    while stack and stack[-1][0]>=d: stack.pop()
    stack.append((d,name))
    if s<=0: continue
    path=[n for _,n in stack]
    if not any('CombatRunner::RunOne' in n for n in path): tot['Z outside RunOne']+=s; continue
    cat='X other engine exec'
    for n in reversed(path):
        hit=None
        for cname,pats in SUB:
            if any(p in n for p in pats): hit=cname; break
        if hit: cat=hit; break
    tot[cat]+=s
    # which hook is the nearest Hook.* frame (inclusive of listener plumbing under it)
    for n in reversed(path):
        if 'Hooks::Hook::' in n or re.search(r'Hook::<|Hook\+<', n):
            hooks[n]+=s; break
R=sum(v for k,v in tot.items() if not k.startswith('Z'))
for k,v in sorted(tot.items()): print(f"{v:6.1f}s {100*v/R:5.1f}%  {k}")
print("\n-- time under each Hook.* entry (self of everything beneath, nearest hook frame) --")
hs=sum(hooks.values()); print(f"all hooks: {hs:.1f}s = {100*hs/R:.1f}% of RunOne")
for n,v in hooks.most_common(28): print(f"{v:6.2f}s {100*v/R:4.1f}%  {n[:95]}")
