# v0.111.0 follow-up: handoff (branch claude/v0111-followup)

Only what is still open. Finished work is described in `CHANGES.md`, `LAYOUT_CHECK.md` and the commit messages.

## This session's items (brief of 2026-11-12, items 1-5)

1. Vision layout check: **done**. `LAYOUT_CHECK.md`: 46 constants, 42 same, 0 changed, 4 could not be checked without real v0.111 frames (event button texture inset/colours, ancient text colours, map visible band / top strip, price glyph shapes and rug colours). No Vision or map code changed.
2. Both-pack tests: **done**. PackTextTests, EventCatalogTests (new: shared flags per version), the real-pack part of TurnSimulatorTests (now `CombatPackTests`) and DummyHistoryTests run per version; all pass on v0.111.0, no pack data fix was needed. Per pack: Core pack tests v0.107.1 37, v0.111.0 37, sample 3 (+24 version-free TurnSimulatorTests on v0.107.1 data).
3. Co-op shared events: **done for Xx64**. `GameDataVersion.IsSharedEvent` lists the 8 v0.111.0 `IsShared` events; `GameStateTracker.EventRngSeed` adds no slot for them (only Morphic Grove GROUP, a one-card transform, currently reaches a replay). Legacy32 is unchanged (slot always added): v0.107.1 had 6 shared events, but its `BeginEvent` seed rule is unknown without a v0.107.1 decompile. Open: confirm on a v0.107.1 co-op log with a shared-event transform, if one turns up.
4. RewardCorpusTests: **done**. `REWARD_REPLAY.md` has the per-rule table. Matched/tried v0.111.0 3891/3901 → 5369/5375 (upgrade flags right on every match), v0.107.1 2882/2894 → 3978/3987. Rules added in the test: Brain Leech / This or That / Arcane Scroll counted, Darv's Dusty Tome offer draws 1 value, in-combat potions are not reward potions, egg / Lava Lamp / same-screen relic upgrades. Open: 6 + 9 unexplained card mismatches, 2 v0.107.1 potion rolls, 9 v0.107.1 act-1 offers with every card upgraded (needs a v0.107.1 decompile); production `RewardUpgrade` ignores Lava Lamp (needs combat damage).
5. tools/ defaults that mix versions, `DataPackLoader.DefaultCatalogRelativePath`: **not started**.

## Older open points (carried over)

- Pack selection: a v0.109/v0.110 game gets the v0.107.1 pack while GameState uses the v0.111 tables. Decide whether the pack rule should go by generation. No PrepareCommand line prints the selected pack.
- No re-detection of the game version while the overlay runs (picked up at the next start).
- EventPrediction was not checked against a live v0.111 save.
- DeckEffects gaps (both versions): REFLECTIONS SHATTER also clones the deck; missing entries Reflections TOUCH_A_MIRROR, the Wongo LEAVE downgrade, BUGSLAYER, TINKER_TIME MAD_SCIENCE, CLAWS→MAUL, NUTRITIOUS_SOUP, DUSTY_TOME, BYRDPIP; Beautiful Bracelet's random enchant (no table field).
- Single-player log-only deck: Neow capsule, Aroma's transform result, Wood Carvings, Amalgamator, Pael's Claw, Symbiote are never logged. Possible next step: with a save, EventPrediction names the event and the transform-screen hint makes Aroma's LET_GO replayable (`TransformRngCall` 0).
- Packaging: the v0.111.0 pack + catalogs add about 1.6 MB on disk; packaging unchanged.

## Build / test state
- Last full runs (previous session, at `2559692`): GameState 312 passed / 3 skipped; Core 1412, App 530, Resources 88 (earlier commit).
- This session: commit "Both-pack tests" built; the converted classes + RealPackTests + TurnSimulatorTests pass (101/101); commit "Shared events" built, GameDataVersionTests + CoopGuestDeckTests + RngSeedsTests pass (64/64).
