# SimLab — replay real A10 fights in sts2-sim with a search AI

Offline research tool. **Nothing here ships in the overlay**, and the simulator itself is never
committed: `sts2-sim` (MIT, a C# port of the game's code, game v0.111.0) is cloned locally into the
git-ignored `ext/` folder. Only this harness and its scripts are in the repo.

For each recorded ranker fight it rebuilds the combat in the simulator — same character, deck
(upgrades + enchantments), relics (+ run-long relic state), current/max HP, encounter and monster
composition, Ascension 10, and optionally the potions the player used in that fight — lets an AI
play it N times, and writes `sim_fights.csv` next to the real outcome. The goal is a simulator whose
damage numbers order decks the way real fights do, so a card-pick value model can be trained on it.

## Setup

```sh
git clone https://github.com/iRyougi/sts2-sim ext/sts2-sim   # MIT; stays local (ext/ is git-ignored)
git -C ext/sts2-sim checkout 31f4dc0                         # the commit the speed patch was made on
git -C ext/sts2-sim apply ../../patches/sts2-sim-perf.patch  # speed patch + one clone fix (below)
dotnet build -c Release                                      # needs the .NET 9 runtime; SDK 10 builds it
```

`patches/sts2-sim-perf.patch` holds one fidelity fix and otherwise changes only how the simulator
does its work. The fix: a cloned monster built its move machine before the clone was bound to its
combat, so intent values that read the ascension through `Creature.CombatState` got the low value
(Stabbot's attack showed 11 instead of 12 at high ascension; 88 of 115 monster files read the
ascension this way). Real hits were right, but every search clone under-predicted incoming damage.
`CloneCreature` now binds the combat first. Because of this, results differ from the unpatched
clone, and the beam's "keep the greedy probe's clone" shortcut now matches replaying the move on the
original state exactly (checked on the 600-fight dev set). The rest caches the combat hook-listener
list between dispatches (validated by a scan of exactly the sources it was built from, kept in
reusable per-state buffers), gives 13 pure query hooks (damage, block, HP loss, should-play,
should-allow-hitting, play count/location) only the listeners whose type overrides them, tests the
most common listener type first, builds the act-1 map only when something reads it (combat runs
never do), shares immutable history entries between combat clones and drops LINQ/boxing
allocations from card cloning and the engine's own cost-listener witness.

**The engine's own tests pass with the patch, and its default behaviour is the unpatched one.**
The speed shortcuts that rest on an assumption are behind one switch, `CombatState.SimLabFastPaths`
(default off; `Program.cs` turns it on for the fight runner and `api`; `SIMLAB_STRICT_LISTENERS=1`
keeps it off for cross-checks, same results, about 40% more CPU). The switch enables: validating
the listener caches by `List<T>` version numbers and a card-attachment epoch instead of re-reading
every item (the engine's tests require the caches to notice a list changed through a span, which
no engine code does at this pin; `Program.cs` lists the greps to redo when the pin moves), the hook
snapshot's fast validation, the per-hook override tables (94 hook slots; the 13 query loops plus 78 more), and building an
act map on first read. With the switch off, `dotnet test ext/sts2-sim/Sts2Sim.sln -c Release` gives
the same result as the unpatched 31f4dc0 (4662 passed, 3 skipped); earlier versions of this patch
broke 19 of those tests (stale clone field registries after the lazy map, a map that built itself
on first read, and cache fields that the clone-graph tests saw as world state; the cache state now
lives in one holder object that those graph walks do not descend into). `SIMLAB_LISTENER_CHECK=1`
makes every cache hit and every filtered dispatch compare itself with the original computation.
Check every patch change against the engine suite on an unpatched clone, not only against
identical simulation output.

`Directory.Build.props` here is intentionally empty: it stops the repo-wide props (net10,
warnings-as-errors) from applying to this tool and to the clone. The assembly is named `SimFuzz`
because `Sts2Sim.Core` declares `InternalsVisibleTo("SimFuzz")`, which gives the harness the internal
combat-setup API without modifying the simulator.

## Data

```sh
# fights.jsonl comes from: RunHistoryAnalyzer pick-export <runs> --pack <dir> --out <dir>
python -P make_evalsets.py fights.jsonl out                 # out/dev.jsonl (600), out/test.jsonl (2000)
python -P enrich_fights.py <ranker .run dir> out/dev.jsonl out/dev_p.jsonl     # + potions, relic_state
python -P enrich_fights.py <ranker .run dir> out/test.jsonl out/test_p.jsonl
```

* `make_evalsets.py`: v0.111 fights only, split by a hash of run_id (30% of runs dev, 70% test, so no
  run is in both), stratified 50% monster / 25% elite / 25% boss so boss deaths can be measured.
  Tune on dev only; test is the held-out check.
* `enrich_fights.py` reads each fight's floor in the `.run` file: `potions` = potions the player used
  in that fight; `relic_state` = Girya lifts, Pumpkin Candle combats left lit, Venerable Tea Set armed
  by a rest site since the last fight (state the relic list alone does not carry).

## Run

```sh
./run.sh out/dev_p.jsonl out/dev_x --seeds 3 --potions [--set Name=value ...]   # 4 procs x 6 threads
PROCS=2 THREADS=4 ./run.sh ...                                                   # gentler while the game runs
./sweep.sh out/dev_p.jsonl tag 3 "<args A>" "<args B>" ...   # one summary line per setting
python -P summary.py out/dev_x.csv                           # one line: damage ratio, deaths, Spearman
python -P compare.py before=out/a.csv after=out/b.csv        # markdown before/after table
python -P analyze.py out/dev_x.csv                           # longer fidelity report
SIMTRIAL_TRACE=1 dotnet bin/Release/net9.0/SimFuzz.dll one.jsonl o.csv --seeds 1 --threads 1   # every decision
SIMLAB_PROFILE=1 ...                                         # time split + card-selection prompt counts
```

`--policy old` reproduces the first trial's 1-step policy (`--horizon 2 --enemy-w 0.6`).

## API for other offline tools

```sh
dotnet bin/Release/net9.0/SimFuzz.dll api requests.jsonl results.jsonl [--seeds 5] [--threads N] [--set ...]
```

One request per line:
`{"id", "character", "deck": ["CARD.X+1#ENCHANTMENT.Y:3", ...], "relics", "hp", "max_hp",
"encounter", "act", "monsters"?, "potions"?, "relic_state"?, "seeds"?, "seed_base"?}` →
one result per line: `{"id", "runs", "win_rate", "damage_mean", "damage_sd", "turns_mean",
"outcomes": [{"won", "died", "damage", "turns"}], "notes", "error"?}`. From C#:
`SimApi.Simulate(FightRequest, seeds)` after `ModelDb.Init(ContentRegistry.AllTypes)`.
Damage = HP lost in the fight (all current HP if the player dies). Seeds are deterministic
(`"{seed_base}_0_{k}"`), so the same request gives the same answer.

## The AI (`BeamPolicy.cs`)

Turn-level beam search on cloned combat states (`CombatState.Clone()` copies the RNG exactly, so the
search sees the real future draws and enemy rolls — mild clairvoyance, the same for every deck):

1. **Search one turn.** From the current state, try every playable card × legal target (identical
   cards merged) and every usable potion; each partial turn is a clone. Partial turns are ordered by a
   cheap score (`Eval.Static`: HP after the enemies' announced attacks hit the current block, plus
   the non-HP terms below); the best `Beam` (6) are expanded per depth, identical states merged.
2. **Finish the best `LeafK` (16) end points exactly.** End the turn on a clone, let the enemies act,
   then play `LeafHorizon − 1` (2) more turns with a one-step greedy rollout (`Rollout` 1: each step
   plays the move with the best `Eval.Static`), and score with `Eval.Leaf`.
3. Play the winning plan card by card; replan if the live state stops matching.

`Eval` (units: player HP points): HP − w·(enemy HP + ½ block) + 8/kill + 1.5/buff stack
− 1.5/debuff stack + 1/enemy debuff − 0.5/enemy buff + 2/orb + 0.3·pet HP + 6/held potion.
w comes from a **race model** (`RaceK` 0.7): w = RaceK · leak · D / O with this fight's running
averages of enemy attack damage per turn (D), enemy HP removed per turn (O) and the share of D not
blocked (leak), so killing matters more against hard hitters. Intent damage is passed through the
game's damage hooks (`Hook.ModifyDamage`): the intent objects only hold the raw number, without
strength / weak / vulnerable. All numbers are `Tun` fields, settable with `--set Name=value`.

Card-selection prompts (`SimpleSelector`): sacrifices take the minimum, worst first; discards put Sly
cards first (they play for free when discarded); upgrade/copy prompts (Armaments, Dual Wield, …) take
the best card; gains from piles/generated choices take the best by rarity/upgrade.

## Outputs

`sim_fights.csv`: `run_id,node_index,build_id,act,room_type,encounter,character,real_damage,
real_turns,real_died,mapped,unmapped_note,sim_runs,sim_win_rate,sim_damage_mean,sim_damage_sd,
sim_turns_mean,error`. Runs hitting 30 player turns or the wall-time cap are counted as timeouts and
left out.

## Results (held-out test set, 1,965 fights × 3 seeds, potions used in the fight, 2026-11-10)

`before` = the first trial's 1-step policy (no potions); `v1` = first beam AI; `v2` = boss-mechanic
fixes + Beam 8 / LeafHorizon 4 (default); `high` = `--quality high`. Real ranker damage per fight is 12.7.

| metric | before | v1 | v2 (default) | v2 high |
|---|---|---|---|---|
| damage ratio (all) | 1.70x | 1.30x | 1.19x | 1.15x |
| monster: damage ratio / death (real 1.4%) | 1.88x / 2.9% | 1.36x / 1.5% | 1.23x / 1.3% | 1.18x / 1.2% |
| elite: damage ratio / death (real 3.0%) | 1.71x / 22.3% | 1.26x / 10.9% | 1.20x / 9.4% | 1.16x / 7.2% |
| boss: damage ratio / death (real 7.8%) | 1.57x / 49.8% | 1.29x / 33.6% | 1.17x / 24.6% | 1.12x / 21.9% |
| within-encounter Spearman (43 encounters, n≥20) | +0.459 | +0.488 | +0.504 | +0.506 |
| encounter-demeaned Spearman | +0.439 | +0.458 | +0.487 | +0.488 |
| encounter-order Spearman | +0.936 | +0.945 | +0.948 | +0.941 |

Speed on this run (4 processes × 6 threads, other programs running): default 11.5 combats/s, high
6.1. With the speed patch, the clone fix and the GC settings (same set, idle PC, 4 × 6): default
52.3 combats/s, high 14.7. The clone fix (search clones now see the ascension intent damage) left
quality within noise on this set: default boss death 24.2% → 25.2%, elite 9.4% → 9.1%, damage
ratio 1.20x both, encounter-demeaned Spearman +0.489 → +0.495; high unchanged (21.9% boss).

Speed work, in order of gain (dev set, 600 fights × 3 seeds, 24 threads: 17.1 → 39.2 combats/s):
the combat hook-listener list is cached instead of rebuilt per dispatch (it was ~30% of CPU), the
greedy rollout keeps its best probe's clone instead of replaying the move, query hooks see only the
listeners that override them, one server-GC heap per worker thread (4 processes each made 24) and a
1 GB gen0 budget (search states die young instead of being promoted). Every run prints `progress
d/t (pct) elapsed .. eta HH:mm:ss (.. left)` every 10 s and writes it to `<output>.progress`.

What moved boss deaths (dev boss set, 150 fights × 3, real 10%): v1 27.3% → reading boss mechanics
the eval got wrong 23.3% (The Insatiable's Sandpit countdown counted as an enemy buff; the Waterfall
Giant's 1e9-HP pre-explosion state counted as enemy HP; Test Subject's next forms not counted, so
finishing a phase looked like a loss; status cards' end-of-turn damage ignored; Knowledge Demon curse
prompt always took Disintegration) → Beam 8 / horizon 4 21.3% → high 16.2%. Backtracking on a loss
(`Backtracks`, rewind 1–3 turns) saved no fights: deaths come from misread mechanics several turns
earlier, not from the last turn. Queen has no single eval bug found; it is now the worst boss.

Earlier (v1) steps on dev: exact intent damage + race weight + second turn, real potions, one-step greedy
rollout, 16 exactly scored end points, relic state and prompt fixes, RaceK 0.7. The harness setup
explains only ~5–8% of the remaining gap; the rest is play quality.
