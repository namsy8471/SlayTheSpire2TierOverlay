//! Upgrades of the cards on a card reward screen, replayed from the run seed (C#
//! `GameStateTrackerTests.Reward_upgrades_on_screen_come_from_the_replayed_rolls`).

use std::collections::BTreeMap;

use ov_gamestate::tracking::tracker::{Tracker, TrackerOptions};

fn levels(pairs: &[(&str, i32)]) -> Option<BTreeMap<String, i32>> {
    Some(pairs.iter().map(|(id, l)| (id.to_string(), *l)).collect())
}

fn ids(list: &[&str]) -> Vec<String> {
    list.iter().map(|s| s.to_string()).collect()
}

#[test]
fn reward_upgrades_on_screen_come_from_the_replayed_rolls() {
    // A real act-3 Necrobinder run (v0.113.1). Floor 40 showed Blight Strike plain for its first frames (it was
    // upgraded); floor 43 showed Putrefy "+" (it was not). The run history has the true levels.
    let mut t = Tracker::new(TrackerOptions::default());
    t.process_lines(&[
        "Release Version: v0.113.1",
        "[INFO] Embarking on a singleplayer NECROBINDER run. Ascension: 0 Seed: 3PWM5ZKNZFG0",
        "[INFO] Preloading 'Act=OVERGROWTH' assets... count=8 vfx=0",
        "[INFO] Preloading 'Act=HIVE' assets... count=6 vfx=0",
        "[INFO] Preloading 'Act=GLORY' assets... count=6 vfx=0",
        "[INFO] Preloading 'Combat Room' assets... count=8 vfx=0",
        "[INFO] Card rarity: Rolled 0.7185067, need < 0.01 for rare (offset = -0.02)",
        "[INFO] Card rarity: Rolled 0.18402304, need < 0.02 for rare (offset = -0.01)",
        "[INFO] Card rarity: Rolled 0.8314024, need < 0.03 for rare (offset = 0)",
    ]);
    assert_eq!(
        t.replayed_reward_upgrades(&ids(&[
            "CARD.FLATTEN",
            "CARD.NO_ESCAPE",
            "CARD.BLIGHT_STRIKE"
        ])),
        levels(&[
            ("CARD.FLATTEN", 1),
            ("CARD.NO_ESCAPE", 1),
            ("CARD.BLIGHT_STRIKE", 1)
        ])
    );
    assert_eq!(
        t.replayed_reward_upgrades(&ids(&["CARD.NO_ESCAPE"])),
        levels(&[("CARD.NO_ESCAPE", 1)]),
        "a card not read yet"
    );
    assert_eq!(
        t.replayed_reward_upgrades(&ids(&["CARD.FLATTEN", "CARD.SOW"])),
        None,
        "a card this reward did not roll"
    );

    // Live, the tailer can hand one screen's rarity lines over in separate reads.
    for line in [
        "[INFO] Player 1 obtained CARD.NO_ESCAPE from card reward",
        "[INFO] Preloading 'Combat Room' assets... count=8 vfx=0",
        "[INFO] Card rarity: Rolled 0.12508376, need < 0.04 for rare (offset = 0.01)",
        "[INFO] Card rarity: Rolled 0.57421595, need < 0.049999997 for rare (offset = 0.02)",
        "[INFO] Card rarity: Rolled 0.3711682, need < 0.06 for rare (offset = 0.03)",
    ] {
        t.process_lines(&[line]);
    }
    assert_eq!(
        t.replayed_reward_upgrades(&ids(&["CARD.RIGHT_HAND_HAND", "CARD.SOW", "CARD.PUTREFY"])),
        levels(&[
            ("CARD.RIGHT_HAND_HAND", 1),
            ("CARD.SOW", 0),
            ("CARD.PUTREFY", 0)
        ])
    );
}
