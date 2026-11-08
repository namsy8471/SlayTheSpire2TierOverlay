# Combat data (v0.107.1): spec

The four JSON files here describe each card, relic and potion's combat effect as a list of **ops**. They feed the
3-turn damage simulator (`src/Sts2Overlay.Core/Combat/`). The simulator fights a target that never acts, has no Block
and never dies early. This spec describes what the code does, not what the game does. Where the two differ, the
entry's `model` and `note` say so.

## 1. Purpose and coverage

- **Used by** the event advisor for `EVENT.BATTLEWORN_DUMMY`. The player has 3 turns to kill the dummy.
  `TurnSimulator.Forecast` plays the deck many times with different shuffles. `DamageForecast.ProbAtLeast(hp)` gives
  the share of fights that dealt at least the dummy's HP (`DummyFight.ScaleHp` scales the HP for co-op). The advisor
  runs 300 fights per player with `Rollouts = 2` and seed `20261109 + player index`, then adds the players' samples
  together (`DamageForecast.Combine`).
- **Result per fight** (`SimFight.Effective`): `max(damage dealt, damage dealt + Doom at the best Doom check)`.
- **`model`** on every card, relic and potion. Any value other than these three reads as `none`.

| model | meaning | coverage effect |
|---|---|---|
| `full` | every damage-relevant effect is expressed exactly, or the entry has none | counted in `FullCards` |
| `approx` | expressed with a simplification (random or conditional parts); `note` says which | card → `ApproxCards` (UI: "근사 계산 N종"); relic → `ApproxRelics` |
| `none` | the ops can't express the effect; the entry is simulated as doing nothing useful | card → `UnknownCards` (UI: "효과 미반영: …"); relic → `ApproxRelics` |

A deck card missing from `cards.json` also goes to `UnknownCards`. A relic missing from `relics.json` is ignored.
`ApproxRelics` is recorded but not shown in the UI yet.

## 2. Files and fields

All `*.json` files in this folder are loaded in name order. Each file may hold any of the top-level objects `cards`,
`relics`, `potions` and `characters`. Keys that start with `_` are comments (`_note`, `_irrelevant`), and so is the
free-text `note`. The loader ignores keys it does not know, such as `ext` (effects with no op yet), `starterRelics`, and
an `attack`'s `target`.

**cards.json**: `cards.<CARD.ID>`. Every `…Up` field falls back to its base field when it is missing.

| field | meaning |
|---|---|
| `model`, `note` | see §1 |
| `cost` / `costUp` | energy cost: a number, or `"X"`; anything else (or missing) = unplayable (−1) |
| `costExpr` / `costExprUp` | formula that replaces `cost` whenever the current cost is computed (Stomp: `max(0,3-attacksPlayedThisTurn)`) |
| `type` | `Attack`, `Skill`, `Power`, `Status`, `Curse`, `Quest` (catalog) |
| `keywords` / `keywordsUp` | catalog keywords. The sim reads `Exhaust`, `Ethereal`, `Innate`, `Retain`, `Sly`, `Unplayable`. `keywordsUp` replaces the whole list |
| `tags` | catalog tags (`Strike`, `Shiv`, `Defend`, `OstyAttack`, …), used by filters and variables |
| `pool`, `rarity` | catalog pool (`CARD_POOL.X_CARD_POOL`) and rarity, used by random-card effects |
| `play` / `playUp` | ops run when the card is played |
| `onDraw` / `onDrawUp` | ops run when this card is drawn |
| `onExhaust` / `onExhaustUp` | ops run when this card is exhausted |
| `endOfTurnInHand` / `endOfTurnInHandUp` | ops run at end of turn while the card is in hand |
| `playableIf` | formula; the card is playable only while it is > 0 |
| `starCost` / `starCostUp` | Regent stars needed, paid on play (not on a free play) |
| `starCostX` | spends all stars; X = stars spent |
| `inHandMaxCardsPerTurn` | while in hand, at most this many cards can be played per turn |
| `mustPlayFirst` | while in hand, no other card can be played |

**relics.json**: `relics.<RELIC.ID>`. `_irrelevant` lists relics that were checked and have no effect on this fight.

| field | meaning |
|---|---|
| `model`, `note` | see §1 |
| `combatStart` | ops at combat start ("turn 0", before the turn-1 draw). A `perTurn` start registered here also runs on turn 1, and a `nextTurn` runs on turn 1 |
| `turnStart` + `turns` | ops after the hand draw, on the listed turns (1-based; empty = every turn). A `draw` with `handDraw: true` (the game's `ModifyHandDraw`: Ring of the Snake, Bag of Preparation, Ring of the Drake, Pocketwatch, Big Mushroom) or a constant negative `draw` changes that turn's hand draw instead of drawing after it |
| `turnEnd` + `turnEndTurns` | ops at end of turn, on the listed turns (empty = every turn) |
| `drawPerTurn` | added to the hand draw every turn (may be negative) |
| `energyPerTurn` | added to the turn's energy |

**potions.json**: `potions.<POTION.ID>`: `model`, `note`, and `use` (ops). Potions run only when
`SimOptions.UsePotions` is set: each belt potion is used on turn 1 after the start-of-turn effects, then `onPotionUsed`
fires. The dummy advisor doesn't set it.

**characters.json**: `characters.<CHARACTER.ID>`: `energy` (default 3), `draw` (default 5), `orbSlots` (default 0),
and `pool` (the class pool for `random: "class|…"`).

## 3. Amounts

The amount fields are `dmg`, `hits` (default 1), `n` (default **0**, so always write it), `times` (default 1), plus the
card fields `costExpr` and `playableIf`. Each one is:

- a **number**, or a string that parses as a number;
- `"all"`: every card or orb, for counting ops (`evoke`, `fetch`, `exhaustCards`, `upgradeCards`, `upgradeInHand`,
  `returnFromDiscard`, `playTop`, …);
- a **formula** (`EventFormula`): numbers, `+ - * /`, parentheses, `floor ceil round abs min max pow`, and the
  variables below (case-insensitive). At runtime an unknown variable or a parse error gives 0. The data test catches
  both.

Counts (hits, cards, orbs) are floored and never go below 0. `strMult`, `set` and `delta` must be plain numbers.
`choose`, `setCost`, `count`, `every` and `limitPerTurn` must be integers (test `RealPack_CountFieldsAreNumbers`).

**X** is the energy when an X-cost card is played (a free play uses the current energy and spends nothing). For
`starCostX` it is the stars spent. Otherwise it is 0. Example: Multi-Cast+ `{"op":"evoke","n":1,"times":"X+1"}`.

**Variables** (`SimFight.Snapshot`; `RealPack_EveryFormulaOnlyUsesKnownVariables` rejects any other name). A card's
variables are read once per play, normally before its first op. The card being played is not counted in hand or pile
counts.

- turn and play counts: `X`, `turn`, `cardsPlayedThisTurn`, `attacksPlayedThisTurn`, `skillsPlayedThisTurn`,
  `cardsPlayedThisCombat`, `cardsPlayedLastTurn`, `attacksPlayedLastTurn`, `timesPlayedThisCombat` (this card id),
  `timesDrawnThisCombat` (this copy), `energyLeft` (after paying), `energySpentThisTurn`
- hand and piles: `handSize`, `skillsInHand`, `attacksInHand`, `drawPileSize`, `discardPileSize`, `exhaustPileSize`,
  `strikesInDeck` (fixed at setup), `statusCardsOwned`, `statusCreatedThisTurn`, `cardsGeneratedThisCombat`,
  `cardsDiscardedThisTurn`, `cardsDrawnThisTurn`, `cardsDrawnThisCombat`, `nonHandDrawsThisTurn`, `exhaustedThisTurn`,
  `etherealPlayedThisCombat`, `shivsInExhaust`, `soulsInExhaust`
- target and player: `poisonOnTarget`, `doomOnTarget`, `vulnerableOnTarget`, `strength` (includes temporary),
  `attackHitsThisTurn`, `hpLostThisTurn`, `hpLossCountThisCombat`, `block` (reset at each turn start)
- orbs: `orbCount`, `orbSlots`, `distinctOrbTypes`, `lightningOrbs`, `frostOrbs`, `darkOrbs`,
  `lightningChanneledThisCombat`, `focus` (includes temporary)
- Osty and stars: `ostyAlive`, `ostyHp`, `ostyAttacksThisTurn`, `ostyAttackCardsInDeck`, `stars`,
  `starsGainedThisTurn`, `starCostCardsInDeck` (counts the card itself)
- set only inside triggers (0 elsewhere): `lastHitDamage` (in `onHit`), `lostHp` (in `onOstyLoseHp`)

## 4. Ops

Each op is `{ "op": "<name>", …fields }`. The names are the constants in `CombatOps`, and
`RealPack_EveryOpIsOneTheSimulatorKnows` rejects any other name.

**Damage and HP**

| op | fields | effect |
|---|---|---|
| `attack` | `dmg`, `hits`, `strMult` | player attack hits, using the damage maths in §7; fires `onHit` per hit |
| `ostyAttack` | `dmg`, `hits` | Osty's hits (nothing if Osty is dead); fires `onHit` with the `osty` filter |
| `damage` | `n` | non-attack damage, dealt as it is with no modifiers |
| `loseHp` | `n` | the player loses HP (counters only); fires `onLoseHp` |
| `gainBlock` | `n` | adds to `block`; fires `onGainBlock` |

**Target debuffs.** Each fires `onApply` when n > 0.

| op | fields | effect |
|---|---|---|
| `vulnerable` | `n` | adds Vulnerable turns (×1.5 on player and Osty hits) |
| `weak` | `n` | only fires `onApply` (the dummy never attacks; its Weak isn't stored) |
| `poison` | `n` | adds poison, which ticks on the enemy turn |
| `doom` | `n` | adds Doom (see the Doom check in §6) |
| `accelerant` | `n` | poison ticks `1 + accelerant` times per enemy turn |
| `vulnerableBonus` | `n` | Vulnerable multiplier becomes `1.5 + n` |
| `doomCheck` | none | records a Doom check right now |
| `gainPower` | `power`, `n` | only a power id containing `ACCELERANT` does anything (adds to accelerant); any other power is ignored |

**Player stats, energy and draw**

| op | fields | effect |
|---|---|---|
| `strength` / `loseStrength` | `n` | permanent Strength ± n |
| `tempStrength` | `n` | Strength until end of turn |
| `vigor` | `n` | added to the next Attack card's hits, then used up |
| `selfWeak` | `n` | player Weak: hits ×0.75; −1 at each end of turn |
| `energy` | `n` | gain (or lose) energy; never below 0 |
| `draw` | `n` | draw n cards |
| `noDraw` / `noEnergyGain` | none | no more draws / energy gains this turn |

**Orbs**

| op | fields | effect |
|---|---|---|
| `channel` | `orb` (`lightning` default, `frost`, `dark`, `plasma`, `glass`, `random`), `n` | channel n orbs (§6) |
| `evoke` | `n`, `times`, `from` (`last`, else front) | evoke n orbs, each `times` times (0 = nothing) |
| `focus` / `tempFocus` | `n` | Focus; the temporary part ends at end of turn |
| `orbSlots` | `n` | ± slots, clamped to 0–10; extra orbs are dropped from the back without being evoked |
| `triggerOrbPassives` | `n`, `which` (`front` default, `all`, or an orb kind) | runs the end-of-turn passive n times |

**Osty, stars and forge**

| op | fields | effect |
|---|---|---|
| `summon` | `n` | Osty HP += n |
| `killOsty` | none | Osty HP → 0; fires `onOstyLoseHp` with `lostHp` |
| `stars` | `n` | gain stars; when n > 0 also fires `onApply` with power `stars` |
| `forge` | `n` | adds a Sovereign Blade to hand if none is in hand, draw or discard; that card's hits get + total forge |

**Cards and piles**

| op | fields | effect |
|---|---|---|
| `addCard` | `card` or `random` (+`choose`), `n`, `upgraded`, `to` (`hand` default, `draw` = random spot, `drawTop`, `discard`), `free` (0 cost this turn), `setCost` (cost for the rest of combat) | create cards. A `random` spec is `<pool>\|<kind>`: pool `class`, `colorless`, or a pool id; kind `any`, `common`/`uncommon`/`rare`, `zeroCost`, or a type. It picks from the playable Common/Uncommon/Rare base cards; with `choose: k` it takes the best of k random draws. A Status card fires `onStatusCreated` |
| `copyInHand` | `filter`, `n` | n copies of the best matching other hand card |
| `transformInHand` | `card`, `upgraded`, `filter`, `n` | matching hand cards become `card` (all of them, or the n least useful). The enchantment is lost |
| `upgradeInHand` | `n` | upgrade the n hand cards that gain the most |
| `upgradeCards` | `n`, `from` (`hand`, `draw`, `discard`, `deck`/`all`), `pick` (`random` default, else best) | upgrade for this combat |
| `exhaustCards` | `n`, `from`, `filter`, `pick` (`random`, else worst first: unplayable, then lowest value) | exhaust |
| `discardCards` | `n` | discard from hand: Sly first (a Sly card discarded by an effect is played for free), then unplayable, then lowest value; fires `onDiscard` |
| `fetch` | `from` (`draw` default, `discard`), `filter`, `n`, `choose` | move the best matching card to hand (with `choose`, the best of that many random matches) |
| `returnFromDiscard` | `n`, `filter` | best matching discard cards to hand |
| `playTop` / `autoPlayTop` | `n`, `exhaust` | play the top card(s) of the draw pile for free; an unplayable card is discarded instead |
| `playFromHand` | `filter`, `pick` (`random`, else best), `times` | play a hand card for free, `times` times |
| `playFromDiscard` | `n`, `filter`, `pick` (default `random`), `exhaust` | play discard cards for free |
| `playDrawn` | `exhaust` | inside `onDraw`: play the drawn card for free |
| `replayNext` | `filter`, `n` | this turn, the next n matching cards are played once more (one replay per card). Only cards that start after it was registered: the game fixes a card's play count before its effects run, so a card never replays itself |
| `nextToDrawTop` | `filter`, `n` | this turn, the next n matching played cards go on top of the draw pile (not the card that registered it: the game picks the result pile before the effects run) |
| `returnNextTurn` | none | this card returns to hand at the next turn start instead of the discard pile |
| `retainHand` | none | the whole hand is kept at end of this turn |
| `redrawHand` | `n` | shuffle the hand into the draw pile, then draw n |

**Powers and triggers.** These persist after the op runs. Their `effects` run with the card that registered them as
the source, so `self` and `costMod self` mean that card.

| op | fields | effect |
|---|---|---|
| `perTurn` | `when` (`start`, `end`), `effects` | `start`: every later turn start. `end`: this turn's end and every later one |
| `nextTurn` | `effects` | once, at the next turn start, before the hand draw (its draws count as hand draws) |
| `onPlay`, `onExhaust`, `onDiscard`, `onStatusCreated` | `filter` on the card, `effects` | after a matching card is played / exhausted / discarded / created |
| `onDraw` | `filter`, `excludeTurnDraw`, `effects` | a matching card was drawn (`excludeTurnDraw`: not by the hand draw); `playDrawn` refers to that card |
| `onHit` | hit `filter`, `effects` | after each matching hit; `lastHitDamage` is set |
| `onApply` | `power` (`debuff` default = vulnerable/weak/poison/doom; or `vulnerable`, `weak`, `poison`, `doom`, `stars`) | after that power is applied |
| `onEvoke` / `onChannel` | `orb` (optional) | per evoke / channel of that kind |
| `onLoseHp`, `onGainBlock`, `onOstyLoseHp`, `onShuffle`, `onHandEmpty`, `onStarsSpent`, `onPotionUsed` | `effects` | when that happens (`onHandEmpty`: after a card play leaves the hand empty) |

All triggers also take `duration` (`turn` = this turn, `nextTurn` = the next turn only, anything else = the rest of
the fight), `every` (fire on every Nth match), `resetEachTurn` (the `every` counter restarts each turn) and
`limitPerTurn`. Triggers fire after the card has resolved. A trigger only sees plays that started after it was
registered (`Effect.Born`), so it never fires for the card that made it: the game's powers record a card in
`BeforeCardPlayed` and act on it in `AfterCardPlayed`.

**Modifiers**

| op | fields | effect |
|---|---|---|
| `attackBonus` | `n`, hit `filter`, `duration`, `limitPerTurn` | + n per matching hit. n is read when the modifier is registered. The limit counts cards, not hits |
| `damageMultiplier` | `n`, hit `filter`, `duration`, `limitPerTurn` | × n per matching hit. When the same card's `turn`/`nextTurn` multiplier (not `self`) is registered again, it lasts one turn longer instead of stacking |
| `costMod` | `filter`, `set` or `delta`, `duration`, `count`, `lowerOnly`, `exhaust` | `filter: hand` changes the cards in hand right now (`combat` = rest of combat, else this turn). `self` changes the source card (`turn`/`next` = this turn, else combat). Any other filter registers a modifier: `turn` = this turn; `next` = the next `count` (default 1) matching paid cards; `eachTurn` = the first `count` matching paid cards each turn; otherwise the rest of combat. `exhaust: true` exhausts the matching cards played |

## 5. Filters

Alternatives are joined by `|` (OR). An empty filter or `any` matches everything.

- **card tokens**: a type (`attack`, `skill`, `power`, `status`, `curse`, … or `type:X`), `tag:X`, `keyword:X` (at the
  card's current level, with enchantment changes), `rarity:X`, an exact `CARD.X`, `zeroCost` (printed cost 0, not X),
  `upgraded`, `colorless`, `self` (the card that registered the effect) and `enchanted` (a card with an enchantment;
  Mystic Lighter).
- **hit filters** (`attackBonus`, `damageMultiplier`, `onHit`): an empty filter means hits from Attack cards only.
  `osty` matches Osty's hits. Other tokens test the card that caused the hit.
- `hand` and `self` have special meanings on `costMod` (§4). An unknown token matches nothing.

## 6. Turn order

**Setup.** The deck is shuffled. Imbued cards go to the bottom and Innate cards to the top. Relic `combatStart` ops
run. Then 3 player turns, each followed by an enemy turn (`SimOptions.Turns`, default 3).

**Start of turn.**
1. Turn counters reset, Block goes to 0, `noDraw`/`noEnergyGain`/`retainHand` turn off, and "this turn" costs in hand
   clear.
2. Energy is set to character energy + `energyPerTurn` (unspent energy is lost). `returnNextTurn` cards come back, and
   `nextTurn` effects due this turn run.
3. Hand draw: character draw + `drawPerTurn` + relic `handDraw` draws and negative relic draws. On turn 1 it is at
   least the Innate count (max 10).
4. Relic `turnStart`, then `perTurn` start effects. Each Plasma orb gives +1 energy. Turn 1 only: Imbued cards are
   played for free, then potions are used (if enabled).

**Play phase.** Cards are played one at a time (§9) until nothing worth playing is left. Paying a card: X-cost cards
spend all energy, cost modifiers are used up, and the star cost is paid. Then the card's ops run, then any enchantment
`OnPlay` (§8). Extra plays (Spiral, Glam) and a pending `replayNext` run the ops again. Afterwards a Power card leaves
play; otherwise the card goes to the next turn's hand (`returnNextTurn`), to the exhaust pile (Exhaust, or a `costMod`
with `exhaust`), to the top of the draw pile (`nextToDrawTop`), or to the discard pile. Then `onPlay` and `onHandEmpty`
fire.

**Drawing.** An empty draw pile reshuffles the discard pile (Perfect Fit goes on top) and fires `onShuffle`. With 10
cards in hand, a drawn card goes to the discard pile. When a card is drawn: Slither sets its cost, the card's own
`onDraw` ops run, then `onDraw` triggers.

**End of turn**, in this order:
1. `selfWeak` −1, then the `endOfTurnInHand` ops of cards in hand.
2. Orb passives (front to back), then `perTurn` end effects (so a Consuming Shadow evoke comes after the passives),
   then relic `turnEnd`.
3. Slumbering Essence −1 cost. The hand is discarded: Retain cards and a `retainHand` hand stay, Ethereal cards are
   exhausted.
4. Temporary Strength and Focus, pending replays and `nextToDrawTop` are cleared.

**Enemy turn.** Poison ticks `min(poison, 1 + accelerant)` times; each tick deals the current poison, then poison −1.
Then the Doom check: if Doom > 0, the fight counts as a kill at `damage so far + Doom`. Then Vulnerable −1. Damage on
the enemy turn counts toward that turn in `MeanPerTurn`.

**Orbs.** Channelling into full slots evokes the front orb first. A character with no slots gets one on its first
channel. Slots max out at 10.

| orb | passive (end of turn) | evoke |
|---|---|---|
| Lightning | `3 + Focus` damage | `8 + Focus` damage |
| Dark (starts at 6) | value += `6 + Focus` | value damage |
| Glass (starts at 4) | `value + Focus` damage, then value −1 | `2 × (value + Focus)` damage |
| Plasma | +1 energy at turn start | +2 energy |
| Frost | nothing | nothing |

Orb amounts are floored at 0. Orb damage, poison, Doom and `damage` ops ignore Vulnerable and every other modifier.

## 7. Damage maths

For each `attack` op, the matching modifiers are read once. Then each hit deals:

1. `base` = `dmg`, then the card's own enchantment: `(base + additive) × multiplier` (§8).
2. `+ (strength + tempStrength) × strMult`.
3. `+ flat`: the matching `attackBonus` values, plus Vigor on Attack cards.
4. `+ forge`, Sovereign Blade only.
5. `×` the matching `damageMultiplier` values, `× (1.5 + vulnerableBonus)` if the target is Vulnerable, and `× 0.75`
   if the player has `selfWeak`.
6. `floor`, and never below 0.

For `ostyAttack`: `floor((dmg after enchantment + matching flat bonus) × multipliers × Vulnerable)`. Strength, Vigor
and Weak don't apply. Vigor is used up after an Attack card that landed at least one player hit.

## 8. Enchantments (`Enchantments.cs`)

The damage changes apply only to the enchanted card's own `attack`/`ostyAttack` ops, not to trigger damage.

| enchantment | in the sim |
|---|---|
| `CLONE`, `NIMBLE`, `DEPRECATED_ENCHANTMENT` | nothing |
| `GOOPY` / `SOULS_POWER` | adds / removes Exhaust |
| `STEADY` / `ROYALLY_APPROVED` | adds Retain / adds Innate and Retain |
| `SHARP` / `INSTINCT` | attack + Amount / attack ×2 |
| `CORRUPTED` | attack ×1.5; after each play: `loseHp` 2 |
| `INKY` | attack +1; after each play: `weak` 1 |
| `MOMENTUM` | attack + the momentum built up; each play adds Amount |
| `VIGOROUS` | attack + Amount on the first play only |
| `TEZCATARAS_EMBER` | cost 0 (not for X-cost or unplayable cards), adds Eternal, attack +3 |
| `ADROIT` | after each play: `gainBlock` Amount |
| `SPIRAL` / `GLAM` | every play / the first play of the combat is played twice |
| `SWIFT` / `SOWN` | first play: draw Amount / gain Amount energy |
| `IMBUED` | starts at the bottom of the draw pile; played for free on turn 1 after the start-of-turn effects |
| `PERFECT_FIT` | goes on top when the discard pile is reshuffled into the draw pile |
| `SLITHER` | when drawn: costs a random 0–3 for the rest of combat |
| `SLUMBERING_ESSENCE` | each end of turn in hand: −1 cost until played |

## 9. Card choice

**Greedy policy (`PlayTurn`).**
- A card is playable if it is not Unplayable, no other `mustPlayFirst` card is in hand, no `inHandMaxCardsPerTurn`
  limit is reached, it costs at most the energy left (X-cost: always), its stars are at most the stars left, and its
  `playableIf` is > 0.
- Each playable card gets a heuristic `Value` in damage units. `attack` ops count their real hit damage now. Other ops
  are priced from the hand and from deck averages computed at setup (hits, attack damage and value per card, damage
  per energy), times the turns left. A pending `replayNext` doubles the value; matching `onPlay` triggers add theirs.
- It plays the best `value / max(cost, 0.5)` (X-cost counts all energy left; a tie goes to the higher value). The turn
  ends when the best value is ≤ 0.01, so cards valued at about 0 are never options.
- Limits: 80 plays per turn, an op budget of 20 000 per fight, nested plays and triggers stop at depth 12.

**Look-ahead (`Rollouts` > 0, only when there are at least 2 options).** Before each play:
1. Draw `Rollouts` seeds from the fight's random numbers. Candidates are the greedy pick plus each distinct option
   (same card, level, enchantment state and current cost = one try).
2. For each candidate and seed: copy the fight (piles, powers, orbs, counters) and reshuffle the copy's draw pile,
   because the player knows what is in it but not its order. Play the candidate, then finish the fight greedily.
3. Every candidate faces the same seeds, so the comparison is between cards, not between lucky and unlucky draws. The
   highest total `Effective` wins. It must beat the greedy pick strictly, so ties keep the greedy pick
   (`DummyHistoryTests.Lookahead_DealsAtLeastAsMuchAsGreedy`).

**Known limitation.** The look-ahead goes one card deep, and the rest of the fight is played greedily. Nothing plans a
combo across turns (for example, setting up on turn 1 for a big turn 3). Forecasts therefore lean low for combo and
scaling decks. A won fight with a low forecast is the expected kind of miss (`RecordedOutcomes_AreWithinTheForecast`).

**Debugging.** `SimFight.Trace` (internal) prints one line per turn start (energy, hand), per card played (value,
energy, damage so far) and per enemy turn (damage, poison, Doom, Strength, Focus, orbs, Osty HP). Rollout copies don't
trace.

## 10. Adding or fixing a card

1. Edit the card's entry in `cards.json`: `play` (and `playUp` if the upgrade changes it), `cost`/`costUp`,
   `keywords`/`keywordsUp`, and `type`/`tags`/`pool`/`rarity` copied from the catalog.
2. Set `model` honestly, and write in `note` whatever you simplified. Prefer an existing op. If no op can express the
   effect, use `approx` or `none` and leave the effect out of the ops.
3. A new op needs code in `CombatOps`, `SimFight.Exec` and `SimFight.Value`; a new variable goes in
   `SimFight.Snapshot`. Add an exact small-deck test in `TurnSimulatorTests`, with the per-turn damage worked out by
   hand (like `TenStrikes_ThreeEachTurn`). Rules can be tested on synthetic data built with `CombatData.Parse`.
4. Run `dotnet test tests/Sts2Overlay.Core.Tests --filter "FullyQualifiedName~Sts2Overlay.Core.Tests.Combat"`. The
   data guards in `tests/Sts2Overlay.Core.Tests/Combat/TurnSimulatorTests.cs`:
   - `RealPack_HasEveryCardAndTheFiveCharacters`: every catalog card has an entry; 5 characters.
   - `RealPack_EveryOpIsOneTheSimulatorKnows`: op names exist in `CombatOps`.
   - `RealPack_EveryFormulaOnlyUsesKnownVariables`: formulas parse and use only §3's variables.
   - `RealPack_CountFieldsAreNumbers`: `choose`, `setCost`, `count`, `every` and `limitPerTurn` are numbers.
   - `StarterDecks_DealPlausibleDamage`: starter decks average 20–75 in total and 3–40 per turn, with no unknown cards.
   - `DummyHistoryTests`: the forecasts still agree with the recorded dummy fights.
