# v0.111.0 follow-up: handoff (branch claude/v0111-followup)

Only what is still open. Finished work is described in `CHANGES.md`, `LAYOUT_CHECK.md` and the commit messages.

## This session's items (brief of 2026-11-12, items 1-5)

1. Vision layout check: **done**. `LAYOUT_CHECK.md`: 46 constants, 42 same, 0 changed, 4 could not be checked without real v0.111 frames (event button texture inset/colours, ancient text colours, map visible band / top strip, price glyph shapes and rug colours). No Vision or map code changed.
2. Both-pack tests (PackTextTests, EventCatalogTests, TurnSimulatorTests / DummyHistoryTests pack parts): **not started**.
3. Co-op shared events in `GameStateTracker.EventRngSeed`: **not started**.
4. RewardCorpusTests mismatches (10 + 12 card offers, 10 potion rolls) and the stop rooms BRAIN_LEECH / THIS_OR_THAT / ARCANE_SCROLL: **not started**.
5. tools/ defaults that mix versions, `DataPackLoader.DefaultCatalogRelativePath`: **not started**.

## Older open points (carried over)

- Pack selection: a v0.109/v0.110 game gets the v0.107.1 pack while GameState uses the v0.111 tables. Decide whether the pack rule should go by generation. No PrepareCommand line prints the selected pack.
- No re-detection of the game version while the overlay runs (picked up at the next start).
- EventPrediction was not checked against a live v0.111 save.
- DeckEffects gaps (both versions): REFLECTIONS SHATTER also clones the deck; missing entries Reflections TOUCH_A_MIRROR, the Wongo LEAVE downgrade, BUGSLAYER, TINKER_TIME MAD_SCIENCE, CLAWS→MAUL, NUTRITIOUS_SOUP, DUSTY_TOME, BYRDPIP; Beautiful Bracelet's random enchant (no table field).
- Single-player log-only deck: Neow capsule, Aroma's transform result, Wood Carvings, Amalgamator, Pael's Claw, Symbiote are never logged. Possible next step: with a save, EventPrediction names the event and the transform-screen hint makes Aroma's LET_GO replayable (`TransformRngCall` 0).
- Reward replay upgrade flags: wrong on about 1-5 % of matched offers per index; likely late egg / Lava Lamp upgrades without a draw (unverified, part of item 4).
- Packaging: the v0.111.0 pack + catalogs add about 1.6 MB on disk; packaging unchanged.

## Build / test state
- Last full runs (previous session, at `2559692`): GameState 312 passed / 3 skipped; Core 1412, App 530, Resources 88 (earlier commit).
- This session: docs only so far.
