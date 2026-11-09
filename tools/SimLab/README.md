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
dotnet build -c Release                                      # needs the .NET 9 runtime; SDK 10 builds it
```

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
