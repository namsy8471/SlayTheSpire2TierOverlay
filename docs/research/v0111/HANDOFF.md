# v0.111.0 data wiring: handoff (WIP, branch claude/v0111-data)

State when the session stopped (2026-11-12). The solution builds (`dotnet build -m:4`: 0 warnings, 0 errors).

## Per work item

### 1. App pack and catalog selection: done (small gaps)
- `src/Sts2Overlay.Resources/DataVersionSelector.cs` holds the one rule. It picks the newest pack version <= the game.
  - A game older than every pack gets the oldest.
  - An unknown game version gets the newest.
  - Folders that are not versions (`sample`) are ignored.
- `AppPaths.SelectDataVersion(gameVersion)` sets `AppPaths.DataVersion` once.
  - `App.OnStartup` calls it with `GameInstall.Locate` + `release_info.json`, before anything loads.
  - The choice is logged to app.log as `data version …`.
- These follow `DataVersion`:
  - `AppPaths.DefaultPackDir`
  - `AppPaths.CommittedCatalogFile` (`data/catalog/cards.<v>.json`)
  - the vision relic catalog (`VisionRecognizer.RelicCatalogFile`)
- `CardTextProvider` / `RelicInfoProvider` take their card-vars and relic-vars from the pack dir (sibling catalog), so they follow too.
- A game update while the overlay runs: there is no re-detection while running, so it is picked up at the next start.
  - The tray "reload pack" reloads the same dir.
- An explicit `PackDir` in settings or on the command line still wins over the selection.
  - `AppSettings.PackDir` defaults to null and the app never writes it, so only a hand-edited settings.json pins a pack.
- Known mismatch: a v0.109/v0.110 game gets the v0.107.1 *pack* (the newest pack <= the game), while GameState uses the v0.111 *tables* (by save schema / RngScheme). This is the rule as briefed; decide whether the pack rule should also go by generation.
- Not done: a PrepareCommand line that prints the selected pack.

### 2. GameState embedded data by generation: done
- `GameDataVersion` maps the scheme to tables: Legacy32 → v0.107.1, Xx64 → v0.111.0. v0.109–v0.110 are assumed equal to v0.111.
- The csproj embeds both versions of `cards`, `card-pools`, `relics` and `starting-decks`.
- These take a `RngScheme`, and `GameStateTracker` passes `Scheme()`:
  - CardRarities, RelicTrading, StartingLoadouts (`EmbeddedFor`)
  - RewardCardPool, DeckReplay, AncientOptions
  - EventPrediction, DeckEffects

### 3. SCARE → SIDESTEP: done
- Scare (v0.107.1) was 0 cost, Weak to all enemies, Exhaust. Sidestep (v0.111) gives energy next turn and has no Exhaust. The game calls it "reworked and renamed" and migrates the ids.
- The learned model has its own SIDESTEP entry (n=143), so SCARE's entry (n=178) is **not** reused.
- `data/packs/v0.111.0/aliases.json` maps `CARD.SCARE` → `CARD.SIDESTEP`, so a stale id reads SIDESTEP's own tier and learned entry. The v0.107.1 pack has no alias.
- The GameState Xx64 card table aliases SCARE the same way.
- The validator accepts the alias: RealPackTestsV0111 passes.

### 4. Game-rule ports: partly done
- EventPrediction: re-read on the v0.111.0 source.
  - All 36 `IsAllowed` overrides give the same single-player answer.
  - JungleMazeAdventure and DenseVegetation are co-op-only HP conditions and fall to the default.
  - The upgrade-drops-Exhaust set is now per version: v0.111 drops SCARE and SYNCHRONIZE and adds MIRAGE and UNDERWORLD.
  - The event list and order come from the save's `event_ids`.
- EventPrediction validation, against app.log `event prediction` lines (counts only):
  - v0.111.0: 5 ok / 0 miss.
  - v0.107.1: 51 ok / 1 miss.
  - Run histories have no per-act event order, so a replay from history is not possible.
  - The live save was **not** checked.
- DeckEffects, per version:
  - Distinguished Cape: v0.111 gives 3 Apparition + 2 random curses.
  - Sere Talon: v0.111 gives 3 Wish and loses max HP (no curses).
  - New in v0.111: Dowsing Rod (+DOWSING) and Neow's Sacrifice (+GUILTY).
  - Beautiful Bracelet is left as is: v0.111 enchants 4 random cards with no selection, and the table has no "random enchant" field.
- DeckEffects gaps found that are the same in both versions (not fixed):
  - ZEN_WEAVER BREATHING_TECHNIQUES adds 2 ENLIGHTENMENT; the table says 1.
  - PAELS_HORN adds 2 RELAX; the table says 1.
  - REFLECTIONS SHATTER also clones the deck.
  - LARGE_CAPSULE adds a Strike and a Defend.
  - NEOWS_BONES adds a random curse.
  - Missing entries: Reflections TOUCH_A_MIRROR, the Wongo LEAVE downgrade, BUGSLAYER, TINKER_TIME MAD_SCIENCE, CLAWS→MAUL, NUTRITIOUS_SOUP, PAELS_CLAW, DUSTY_TOME, BYRDPIP.
- Shop prices (ShopPricing, Vision ShopPriceRules): unchanged in v0.111.0 (MerchantCardEntry / MerchantRelicEntry / MembershipCard / TheCourier). `ShopPricing.cs` got a v0.111 note; the Vision `ShopPriceRules` comment is not edited (Vision untouched).
- Weights.cs: the pool sizes (solo 20/35/25) and CardRarityOdds are unchanged. The comments now say so.
- `GameStateTracker.EventRngSeed` always adds the player slot. The game's shared events have no slot offset. This matters only in co-op; it is the same in both versions and is not fixed.

### 5. Reward replay on v0.111: not started (measurement)
- CardFactory was checked against the source: CreateForReward, RollForUpgrade (0.125/0.25 per act, Rare excluded) and the transform filters are unchanged.
- The pool now loads per version.
- **Next step:** add a `[RunCorpusFact]` in `tests/Sts2Overlay.GameState.Tests/RunCorpusTests.cs`. For each solo vanilla run (no modifiers, one player), replay every three-card combat `card_choices` offer with `RewardRollReplay` on the run's scheme. Count matched/tried for v0.111.0 (first 200 runs) and v0.107.1, and print counts only.
  - Corpus: `STS2_RUN_CORPUS`, as in RunCorpusTests.
  - Corpus versions: v0.107.1 has 1546 runs, v0.111.0 has 1614.

### 6. Tests: partly done
- New `tests/Sts2Overlay.GameState.Tests/GameDataVersionTests.cs` (11 tests) covers:
  - rarities that moved (BLOODLETTING, DOMINATE, TAUNT)
  - SIDESTEP known on Xx64 only, and SCARE aliasing to it
  - co-op-only filtering
  - the per-version reward pool
  - the per-version Exhaust set
  - DeckEffects per version
  - starting decks
- New `tests/Sts2Overlay.Resources.Tests/DataVersionSelectorTests.cs` (9 tests). Each case also runs `VersionGuard`, so the warning appears exactly when the pack differs from the game:
  - 0.107.1 → 0.107.1, no warning
  - 0.111.0 → 0.111.0, no warning
  - 0.109.1 → 0.107.1, warning
  - 0.112 → 0.111.0, warning
  - 0.100 → 0.107.1 (oldest), warning
  - unknown or garbage → 0.111.0 (newest), no warning
- `RealPackTests` is now abstract, with `RealPackTestsV0107` and `RealPackTestsV0111` subclasses and per-version expectation tables:
  - IntentionallyCoreless is the same list for both.
  - NewerRelics for v0.111 is `[]`.
  - NewerAncientOptions for v0.111 is the 3 OROBAS ids.
  - Both pass (30 tests).
- **Not done:** both-pack versions of PackTextTests, EventCatalogTests, TurnSimulatorTests and DummyHistoryTests. Use the same subclass pattern as RealPackTests.

### 7. tools/ defaults: not started
- **Next step:** grep `tools/` for `v0.107.1`, `DefaultCatalogRelativePath` and `StartingLoadouts.Embedded`. Change only defaults that would mix versions silently (MemProbe uses `DefaultCatalogRelativePath`) and list the rest.
- `DataPackLoader.DefaultCatalogRelativePath` is still v0.107.1. Tests rely on it (`RealCatalog.cs`, `DataPackLoaderTests.cs`), and the app no longer uses it.

### 8. Packaging size: measured
- `data/packs/v0.111.0`: 1,040,047 bytes.
- v0.111.0 catalogs (`data/catalog/*.v0.111.0.json`): 567,605 bytes.
- Together about **1.6 MB** more on disk, uncompressed.
- The GameState dll also embeds the v0.111.0 cards, relics, pools and decks: about 0.35 MB.
- Packaging was not changed.

## Dry check
- The installed game's `release_info.json` says `v0.111.0`, so the selector picks `v0.111.0` (reason NewestNotNewerThanGame).
- The version guard comparison is 0, so the panel shows no version-mismatch line.
- The GUI was not launched.

## Tests right now
Full runs (`dotnet test -m:1`, after the code commits). Every listed project passes:

| Project | Passed | Skipped | Failed |
|---|---|---|---|
| Core.Tests | 1412 | 0 | 0 |
| GameState.Tests | 302 | 2 | 0 |
| App.Tests | 530 | 1 | 0 |
| Resources.Tests | 88 | 0 | 0 |

Vision tests were not run: no file of the Vision project changed (the relic-catalog line is in the App project's `VisionRecognizer`).

## 2026-11-12 03:35 KST: first check on a real v0.111.0 run (log-only replay vs run history)

`tools/StateProbe --once` after the run ended (no save left, so the tracker rebuilt the deck from godot.log alone), compared with the final deck in the run's history file:

- Reward upgrade rolls: all 4 upgraded picks agree (STAMPEDE+1, AGGRESSION+1, FEED+1, ARMAMENTS+1), no wrong upgrade on the other 10 picked cards. Deck size 23 = 23.
- Difference: the history has DEFEND_IRONCLAD x5, PECK, ULTIMATE_STRIKE and no strikes; the log-only deck still has STRIKE_IRONCLAD x3 and DEFEND_IRONCLAD x4. Three strikes were turned into other cards and the log-only replay did not follow it. Cause NOT yet looked at (which event or relic did it, and whether this is a v0.111 change or the known log-only limit).
- Relics are incomplete in log-only mode (4 of 10), as before.

Next step for item 5: find the transform in the log (event id around the first missing strike), compare with `ReplayTransform` / `DeckEffects` for v0.111, then build the corpus measurement.

## 2026-11-12: follow-up session (items A–E of the second brief)

### A. The transform on the real run (seed PZ4J3CV19SSW): cause found, fixed where possible
- The run history names three deck changes the log-only replay could not follow:
  - floor 6 AROMA_OF_CHAOS LET_GO: Strike → random card (AGGRESSION). This is the pick after the one logged `deck_transform_select_screen` load.
  - floor 15 WOOD_CARVINGS BIRD: Strike → PECK (fixed target). Same scene, but no load line: Godot reused its resource cache.
  - floor 24 AMALGAMATOR COMBINE_STRIKES: 2 Strikes removed, ULTIMATE_STRIKE added.
  - Neow LARGE_CAPSULE (+Strike, +Defend) is not in the log at all.
- What a single-player godot.log has: `Player 1 chose cards [STRIKE_IRONCLAD]` only. No event id, no option key, no Neow/ancient choice, no transform result, and card plays without a combat id (`Player 1 playing card X (targeting …)`, not parsed: a created card can't be told from a deck card). `[EventSynchronizer]` / `[ActionQueueSet]` lines exist only in co-op logs.
- How the game picks the result (v0.111.0, unchanged): `AromaOfChaos.LetGo` → `CardCmd.TransformToRandom(card, EventModel.Rng)` → `CardFactory.CreateRandomCardForTransform`: `rng.NextItem` over the original's pool (unlocked, player-count filtered), Common/Uncommon/Rare, not the original. The new card goes to the end of the deck. `RewardCardPool.TransformTarget` already ports this; it runs when the event is known (co-op log).
- Bug fixed (`DeckReplay.Find`): between equal copies, the least doubtful copy is now acted on. Before, a shop removal or a second unknown pick took the already flagged Strike, so the flags disappeared and the three Strikes left looked certain. Now all remaining Strikes stay flagged `UnknownCardSelect`.
- New hint (`CardSelectScreenEvent`): `Asset not cached: …/card_selection/deck_transform_select_screen.tscn` before a pick outside shops / rest sites makes it a transform (card removed, one unidentified card); `deck_upgrade_select_screen` makes it an upgrade. Logged only the first time per game session, so it is sometimes missing, never wrong.
- DeckEffects bugs fixed (same in both versions; they matter when the choice is logged, i.e. co-op): LARGE_CAPSULE adds the character's basic Strike + Defend; PAELS_CLAW puts Goopy on every Defend-tagged card (basic Defends, ULTIMATE_DEFEND); PAELS_HORN adds 2 RELAX; ZEN_WEAVER BREATHING_TECHNIQUES adds 2 ENLIGHTENMENT; NEOWS_BONES adds 1 unknown curse.
- Log-only result on the real run after the fixes: 22 cards; STRIKE_IRONCLAD x2 both flagged, DEFEND x4, no PECK / ULTIMATE_STRIKE (the history has 23: DEFEND x5, PECK, ULTIMATE_STRIKE, no Strike). The remaining gap is the known single-player log-only limit (Neow capsule, Wood Carvings result, Amalgamator result are never logged).
- StateProbe now prints an `uncertain [...]` line.

