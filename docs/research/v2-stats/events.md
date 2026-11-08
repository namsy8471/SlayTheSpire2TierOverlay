# 이벤트·고대·유물·포션 선택 통계 (v2 events 작업)

상위 플레이어 코퍼스(OP.GG 주간 상위 100 → A10 솔로 바닐라 v0.107+)에서 이벤트 선택지, 고대(NEOW 포함) 선택, 상점 유물·포션 구매, 포션 보상과 사용을 쟀습니다. 좋은 플레이어(good) 22명의 승리 1692 + 패배 1650판, 전체(all) 100명의 승리 2738 + 좋은 플레이어 패배. 패배 기록은 좋은 플레이어 것만 있습니다.

## 방법

- 비율은 모두 `stats_lib.balanced_rate`(플레이어별 가중치 상한 10%)로 낸 **균형 비율**이고, 원시 비율은 JSON에 같이 있습니다. 결정은 good 플레이어 값으로, good이 n<30 또는 eff_n<20이면 all 값이 안정할 때만 all을 씁니다.
- **이벤트·고대 선택지의 팩 포함 기준 = 방향 안정성**: 점수에 (선택률 − 1/k)를 연속값으로 섞으므로, 플레이어 200회 재표본(`stats_lib.bootstrap`)에서 선택률이 중립 1/k(고대는 1/3)보다 높음(또는 낮음)이 80% 이상 유지 + n≥30 + eff_n≥20이면 넣습니다. 등급 구간 유지율(부트)은 참고 열로 남겼습니다.
- **상점 유물·포션의 기준 = 등급 안정성**: 등급 구간(S≥80% A≥60% B≥40% C≥20% D)이 재표본 80% 이상 유지 + n≥30 + eff_n≥20. 고대 유물은 고대 선택지와 같은 방향 기준. 기준을 못 넘으면 **불확실**로 두고 팩 값을 쓰지 않습니다(공식·희귀도 값 유지).
- 이벤트: `event_choices`는 고른 선택지만 기록하고 제시된 선택지 목록은 없습니다. 그래서 선택률 = (그 페이지에서 그 선택지를 고른 방문) / (그 페이지에 도달해 무엇이든 고른 방문)입니다. 조건부로 잠기는 선택지는 분모에서 구분하지 못합니다.
  - 선택지가 하나만 기록된 페이지(이어지는 단계, 싸움 시작 등), 한 방문에서 여러 번 고르는 페이지(미끄러운 다리의 버티기 반복, 끝없는 컨베이어), 베타 전용 이벤트는 팩에서 뺐습니다.
  - 막별 값은 그 막의 방문이 20회 이상, 그 막 값의 방향이 안정(n≥20, eff_n≥14, 80%)하고 등급 구간이 전체와 다를 때만 `byAct`로 넣었습니다.
- 고대: `ancient_choice`(제시된 3개, `*` = 고른 것). 선택률 = 제시됐을 때 고른 비율, 중립값은 1/3.
- 유물: 상점 층(`t=shop`, 그리고 `r`이 빈 `?` 상점)의 `relic_choices`는 진열된 유물 전부이고 `*`가 산 것입니다(`bought_relics`와 12,279층 중 12,264층 일치). 카드 상점(안 산 카드만 기록)과 다릅니다. `?` 중 유물 1개를 받고 골드만 얻은 1,563층은 상점이 아니라 보물방으로 셌습니다. 보상 층(엘리트·보물·보스·일반)의 유물은 1개만 주어지고 34,739개 중 34,705개(99.9%)를 가져가므로 선택 신호가 아닙니다.
- 포션: 전투 보상(`potion_choices`, 보통 1개)에서 가져간 비율, 상점 구매율, 사용 수(`potion_used`), 버린 수(`potion_discarded`). 보상 포션을 안 가져가는 주된 이유는 포션 칸이 찼기 때문일 수 있어(층별 보유 수가 기록되지 않음) 가치와 섞여 있습니다.
- 결과 차이(승률): good 플레이어만, 그 선택지를 고른 방문 vs 같은 페이지에서 다른 걸 고른 방문의 균형 승률 차이. **선택 편향이 큽니다**(강한 덱일 때 위험한 선택지를 고름). 참고용이며 결정에 쓰지 않았습니다.
- OP.GG 전체 유저 통계(v0.107.1)는 비교용으로만 붙였습니다.

## 1. 이벤트 선택지

고려한 선택지 140개 중 방향이 안정한 115개를 `pack/events.stats.json`에 넣었습니다(제외한 페이지 29개). 아래는 good 방문 n≥150인 이벤트와 차이가 큰 항목입니다.

| 이벤트.페이지 | 선택지 | good 균형 | n / 명 | 방향 부트 | 등급(부트) | all 균형 | 팩 | 막별 | 승률 차(참고) | OP.GG 전체 |
|---|---|---|---|---|---|---|---|---|---|---|
| THIS_OR_THAT.INITIAL | ORNATE | 68% | 950 / 20 | 0.99 | A (0.73) | 71% | 넣음 |  | -10%p | 70% |
| THIS_OR_THAT.INITIAL | PLAIN | 32% | 950 / 20 | 0.99 | C (0.73) | 29% | 넣음 |  | +10%p | 30% |
| SELF_HELP_BOOK.INITIAL | READ_ENTIRE_BOOK | 45% | 927 / 20 | 1.0 | B (0.955) | 48% | 넣음 | 1막 29%, 3막 75% | +14%p | 28% |
| SELF_HELP_BOOK.INITIAL | READ_THE_BACK | 35% | 927 / 20 | 0.69 | C (0.89) | 33% | 불확실 |  | -5%p | 46% |
| SELF_HELP_BOOK.INITIAL | READ_PASSAGE | 19% | 927 / 20 | 1.0 | D (0.645) | 19% | 넣음 | 1막 25% | -10%p | 26% |
| ROOM_FULL_OF_CHEESE.INITIAL | GORGE | 57% | 752 / 22 | 0.81 | B (0.63) | 58% | 넣음 |  | -19%p | 60% |
| ROOM_FULL_OF_CHEESE.INITIAL | SEARCH | 43% | 752 / 22 | 0.81 | B (0.63) | 42% | 넣음 |  | +19%p | 40% |
| BRAIN_LEECH.INITIAL | RIP | 58% | 678 / 20 | 0.87 | B (0.64) | 56% | 넣음 | 2막 61% | +3%p | 28% |
| BRAIN_LEECH.INITIAL | SHARE_KNOWLEDGE | 42% | 678 / 20 | 0.87 | B (0.64) | 44% | 넣음 | 2막 39% | -3%p | 72% |
| POTION_COURIER.INITIAL | RANSACK | 53% | 535 / 20 | 0.72 | B (0.95) | 51% | 불확실 |  | +5%p | 63% |
| POTION_COURIER.INITIAL | GRAB_POTIONS | 47% | 535 / 20 | 0.72 | B (0.95) | 49% | 불확실 |  | -5%p | 37% |
| SYMBIOTE.INITIAL | KILL_WITH_FIRE | 68% | 520 / 20 | 1.0 | A (0.985) | 72% | 넣음 |  | +2%p | 68% |
| SYMBIOTE.INITIAL | APPROACH | 32% | 520 / 20 | 1.0 | C (0.985) | 28% | 넣음 |  | -2%p | 32% |
| SUNKEN_STATUE.INITIAL | GRAB_SWORD | 64% | 441 / 19 | 0.985 | A (0.72) | 64% | 넣음 |  | +19%p | 66% |
| SUNKEN_STATUE.INITIAL | DIVE_INTO_WATER | 36% | 441 / 19 | 0.985 | C (0.72) | 36% | 넣음 |  | -19%p | 34% |
| THE_LEGENDS_WERE_TRUE.INITIAL | NAB_THE_MAP | 89% | 431 / 19 | 1.0 | S (0.985) | 90% | 넣음 |  | -13%p | 94% |
| THE_LEGENDS_WERE_TRUE.INITIAL | SLOWLY_FIND_AN_EXIT | 11% | 431 / 19 | 1.0 | D (0.985) | 10% | 넣음 |  | +13%p | 6% |
| CRYSTAL_SPHERE.INITIAL | PAYMENT_PLAN | 57% | 374 / 19 | 0.71 | B (0.68) | 59% | 불확실 |  | -9%p | 36% |
| CRYSTAL_SPHERE.INITIAL | UNCOVER_FUTURE | 43% | 374 / 19 | 0.71 | B (0.68) | 41% | 불확실 |  | +9%p | 64% |
| RANWID_THE_ELDER.INITIAL | RELIC | 42% | 348 / 17 | 0.995 | B (0.74) | 39% | 넣음 |  | +0%p | 37% |
| RANWID_THE_ELDER.INITIAL | POTION | 36% | 348 / 17 | 0.61 | C (0.84) | 38% | 불확실 |  | -6%p | 42% |
| RANWID_THE_ELDER.INITIAL | GOLD | 22% | 348 / 17 | 1.0 | C (0.685) | 23% | 넣음 | 2막 17% | +3%p | 21% |
| RELIC_TRADER.INITIAL | BOTTOM | 35% | 321 / 19 | 0.71 | C (0.99) | 34% | 불확실 |  | -0%p | 35% |
| RELIC_TRADER.INITIAL | MIDDLE | 33% | 321 / 19 | 0.61 | C (0.99) | 30% | 불확실 |  | +10%p | 33% |
| RELIC_TRADER.INITIAL | TOP | 32% | 321 / 19 | 0.655 | C (1.0) | 35% | 불확실 |  | -9%p | 32% |
| ROUND_TEA_PARTY.INITIAL | PICK_FIGHT | 86% | 317 / 20 | 1.0 | S (0.89) | 85% | 넣음 |  | +35%p | 88% |
| ROUND_TEA_PARTY.INITIAL | ENJOY_TEA | 14% | 317 / 20 | 1.0 | D (0.89) | 15% | 넣음 |  | -35%p | 12% |
| HUNGRY_FOR_MUSHROOMS.INITIAL | FRAGRANT_MUSHROOM | 73% | 315 / 18 | 1.0 | A (0.955) | 73% | 넣음 |  | +7%p | 82% |
| HUNGRY_FOR_MUSHROOMS.INITIAL | BIG_MUSHROOM | 27% | 315 / 18 | 1.0 | C (0.955) | 27% | 넣음 |  | -7%p | 18% |
| TINKER_TIME.CHOOSE_CARD_TYPE | SKILL | 45% | 314 / 17 | 1.0 | B (0.94) | 44% | 넣음 |  | +3%p | 35% |
| TINKER_TIME.CHOOSE_CARD_TYPE | POWER | 40% | 314 / 17 | 0.97 | C (0.575) | 40% | 넣음 |  | +3%p | 48% |
| TINKER_TIME.CHOOSE_CARD_TYPE | ATTACK | 15% | 314 / 17 | 1.0 | D (0.91) | 15% | 넣음 |  | -4%p | 17% |
| TINKER_TIME.CHOOSE_RIDER | EXPERTISE | 19% | 314 / 17 | 0.995 | D (0.62) | 18% | 넣음 |  | +5%p | 21% |
| TINKER_TIME.CHOOSE_RIDER | WISDOM | 18% | 314 / 17 | 0.975 | D (0.665) | 18% | 넣음 |  | +4%p | 8% |
| TINKER_TIME.CHOOSE_RIDER | ENERGIZED | 18% | 314 / 17 | 0.99 | D (0.81) | 20% | 넣음 |  | +7%p | 19% |
| TINKER_TIME.CHOOSE_RIDER | CURIOUS | 15% | 314 / 17 | 0.855 | D (0.875) | 18% | 넣음 |  | +0%p | 18% |
| TINKER_TIME.CHOOSE_RIDER | CHAOS | 9% | 314 / 17 | 0.76 | D (1.0) | 7% | 불확실 |  | -1%p | 7% |
| TINKER_TIME.CHOOSE_RIDER | VIOLENCE | 7% | 314 / 17 | 0.945 | D (1.0) | 6% | 넣음 |  | -8%p | 7% |
| TINKER_TIME.CHOOSE_RIDER | SAPPING | 6% | 314 / 17 | 0.965 | D (1.0) | 8% | 넣음 |  | -18%p | 7% |
| TINKER_TIME.CHOOSE_RIDER | IMPROVEMENT | 5% | 314 / 17 | 1.0 | D (1.0) | 4% | 넣음 |  | -5%p | 9% |
| TINKER_TIME.CHOOSE_RIDER | CHOKING | 2% | 314 / 17 | 1.0 | D (1.0) | 2% | 넣음 |  | -2%p | 4% |
| COLORFUL_PHILOSOPHERS.INITIAL | IRONCLAD | 31% | 293 / 18 | 1.0 | C (0.995) | 32% | 넣음 |  | -16%p | 28% |
| COLORFUL_PHILOSOPHERS.INITIAL | SILENT | 26% | 293 / 18 | 0.99 | C (0.99) | 21% | 넣음 |  | -3%p | 29% |
| COLORFUL_PHILOSOPHERS.INITIAL | DEFECT | 17% | 293 / 18 | 0.82 | D (0.82) | 19% | 넣음 |  | +26%p | 16% |
| COLORFUL_PHILOSOPHERS.INITIAL | NECROBINDER | 13% | 293 / 18 | 0.985 | D (0.985) | 13% | 넣음 |  | +2%p | 14% |
| COLORFUL_PHILOSOPHERS.INITIAL | REGENT | 13% | 293 / 18 | 0.975 | D (0.975) | 15% | 넣음 |  | +1%p | 13% |
| THE_LANTERN_KEY.INITIAL | RETURN_THE_KEY | 58% | 282 / 19 | 0.84 | B (0.565) | 61% | 넣음 |  | -5%p | 57% |
| THE_LANTERN_KEY.INITIAL | KEEP_THE_KEY | 42% | 282 / 19 | 0.84 | B (0.565) | 39% | 넣음 |  | +5%p | 43% |
| BATTLEWORN_DUMMY.INITIAL | SETTING_2 | 61% | 277 / 18 | 0.995 | A (0.555) | 68% | 넣음 |  | -1%p | 60% |
| BATTLEWORN_DUMMY.INITIAL | SETTING_3 | 31% | 277 / 18 | 0.57 | C (0.705) | 23% | 불확실 |  | +5%p | 27% |
| BATTLEWORN_DUMMY.INITIAL | SETTING_1 | 8% | 277 / 18 | 1.0 | D (1.0) | 8% | 넣음 |  | -9%p | 13% |
| FIELD_OF_MAN_SIZED_HOLES.INITIAL | ENTER_YOUR_HOLE | 62% | 273 / 19 | 0.94 | A (0.585) | 63% | 넣음 |  | -4%p | 76% |
| FIELD_OF_MAN_SIZED_HOLES.INITIAL | RESIST | 38% | 273 / 19 | 0.94 | C (0.585) | 37% | 넣음 |  | +4%p | 24% |
| BUGSLAYER.INITIAL | EXTERMINATION | 56% | 271 / 18 | 0.81 | B (0.745) | 55% | 넣음 |  | +3%p | 50% |
| BUGSLAYER.INITIAL | SQUASH | 44% | 271 / 18 | 0.81 | B (0.745) | 45% | 넣음 |  | -3%p | 50% |
| GRAVE_OF_THE_FORGOTTEN.INITIAL | CONFRONT | 57% | 270 / 19 | 0.81 | B (0.71) | 52% | 넣음 |  | +4%p | 37% |
| GRAVE_OF_THE_FORGOTTEN.INITIAL | ACCEPT | 43% | 270 / 19 | 0.81 | B (0.71) | 48% | 넣음 |  | -4%p | 63% |
| REFLECTIONS.INITIAL | TOUCH_A_MIRROR | 73% | 269 / 18 | 1.0 | A (0.815) | 77% | 넣음 |  | +5%p | 85% |
| REFLECTIONS.INITIAL | SHATTER | 27% | 269 / 18 | 1.0 | C (0.815) | 23% | 넣음 |  | -5%p | 15% |
| TRIAL.INITIAL | ACCEPT | 97% | 269 / 18 | 1.0 | S (1.0) | 97% | 넣음 |  | -2%p | 95% |
| TRIAL.INITIAL | REJECT | 3% | 269 / 18 | 1.0 | D (1.0) | 3% | 넣음 |  | +2%p | 5% |
| TRIAL.MERCHANT | GUILTY | 62% | 92 / 13 | 0.925 | A (0.585) | 67% | 넣음 |  | +5%p | 71% |
| TRIAL.MERCHANT | INNOCENT | 38% | 92 / 13 | 0.94 | C (0.565) | 33% | 넣음 |  | -5%p | 29% |
| TRIAL.NOBLE | GUILTY | 75% | 88 / 14 | 1.0 | A (0.735) | 68% | 넣음 |  | +4%p | 72% |
| TRIAL.NOBLE | INNOCENT | 25% | 88 / 14 | 1.0 | C (0.735) | 32% | 넣음 |  | -4%p | 28% |
| TRIAL.NONDESCRIPT | INNOCENT | 72% | 89 / 16 | 0.99 | A (0.75) | 79% | 넣음 |  | -14%p | 52% |
| TRIAL.NONDESCRIPT | GUILTY | 28% | 89 / 16 | 0.99 | C (0.75) | 21% | 넣음 |  | +14%p | 48% |
| LOST_WISP.INITIAL | SEARCH | 68% | 267 / 18 | 0.985 | A (0.77) | 68% | 넣음 |  | +4%p | 77% |
| LOST_WISP.INITIAL | CLAIM | 32% | 267 / 18 | 0.985 | C (0.77) | 32% | 넣음 |  | -4%p | 23% |
| DOLL_ROOM.INITIAL | TAKE_SOME_TIME | 52% | 260 / 19 | 0.965 | B (0.7) | 47% | 넣음 |  | +15%p | 37% |
| DOLL_ROOM.INITIAL | RANDOM | 34% | 260 / 19 | 0.52 | C (0.715) | 42% | 불확실 |  | -27%p | 49% |
| DOLL_ROOM.INITIAL | EXAMINE | 14% | 260 / 19 | 1.0 | D (0.86) | 11% | 넣음 |  | +9%p | 15% |
| INFESTED_AUTOMATON.INITIAL | STUDY | 61% | 259 / 19 | 0.965 | A (0.57) | 65% | 넣음 |  | +14%p | 62% |
| INFESTED_AUTOMATON.INITIAL | TOUCH_CORE | 39% | 259 / 19 | 0.965 | C (0.57) | 35% | 넣음 |  | -14%p | 38% |
| SPIRALING_WHIRLPOOL.INITIAL | OBSERVE | 64% | 259 / 16 | 0.885 | A (0.595) | 60% | 넣음 |  | +11%p | 61% |
| SPIRALING_WHIRLPOOL.INITIAL | DRINK | 36% | 259 / 16 | 0.885 | C (0.595) | 40% | 넣음 |  | -11%p | 39% |
| DOORS_OF_LIGHT_AND_DARK.INITIAL | LIGHT | 53% | 255 / 18 | 0.645 | B (0.7) | 54% | 불확실 |  | -13%p | 56% |
| DOORS_OF_LIGHT_AND_DARK.INITIAL | DARK | 47% | 255 / 18 | 0.645 | B (0.7) | 46% | 불확실 |  | +13%p | 44% |
| ABYSSAL_BATHS.INITIAL | ABSTAIN | 65% | 254 / 17 | 0.95 | A (0.645) | 68% | 넣음 |  | -15%p | 48% |
| ABYSSAL_BATHS.INITIAL | IMMERSE | 35% | 254 / 17 | 0.95 | C (0.645) | 32% | 넣음 |  | +15%p | 52% |
| DROWNING_BEACON.INITIAL | CLIMB | 61% | 254 / 17 | 0.915 | A (0.49) | 57% | 넣음 |  | +10%p | 52% |
| DROWNING_BEACON.INITIAL | BOTTLE | 39% | 254 / 17 | 0.915 | C (0.49) | 43% | 넣음 |  | -10%p | 48% |
| TEA_MASTER.INITIAL | EMBER_TEA | 37% | 254 / 18 | 0.66 | C (0.635) | 32% | 불확실 |  | +14%p | 18% |
| TEA_MASTER.INITIAL | BONE_TEA | 33% | 254 / 18 | 0.485 | C (0.81) | 38% | 불확실 |  | -14%p | 52% |
| TEA_MASTER.INITIAL | TEA_OF_DISCOURTESY | 31% | 254 / 18 | 0.695 | C (0.805) | 30% | 불확실 |  | -8%p | 30% |
| SUNKEN_TREASURY.INITIAL | FIRST_CHEST | 62% | 252 / 18 | 0.92 | A (0.585) | 56% | 넣음 |  | +5%p | 54% |
| SUNKEN_TREASURY.INITIAL | SECOND_CHEST | 38% | 252 / 18 | 0.92 | C (0.585) | 44% | 넣음 |  | -5%p | 46% |
| COLOSSAL_FLOWER.INITIAL | REACH_DEEPER_1 | 65% | 249 / 19 | 0.955 | A (0.7) | 61% | 넣음 |  | +2%p | 48% |
| COLOSSAL_FLOWER.INITIAL | EXTRACT_CURRENT_PRIZE_1 | 35% | 249 / 19 | 0.955 | C (0.7) | 39% | 넣음 |  | -2%p | 52% |
| COLOSSAL_FLOWER.REACH_DEEPER_1 | REACH_DEEPER_2 | 97% | 156 / 16 | 1.0 | S (1.0) | 94% | 넣음 |  | +20%p | 86% |
| COLOSSAL_FLOWER.REACH_DEEPER_1 | EXTRACT_CURRENT_PRIZE_2 | 3% | 156 / 16 | 1.0 | D (1.0) | 6% | 넣음 |  | -20%p | 14% |
| COLOSSAL_FLOWER.REACH_DEEPER_2 | POLLINOUS_CORE | 66% | 153 / 16 | 0.94 | A (0.655) | 65% | 넣음 |  | +22%p | 62% |
| COLOSSAL_FLOWER.REACH_DEEPER_2 | EXTRACT_INSTEAD | 34% | 153 / 16 | 0.94 | C (0.655) | 35% | 넣음 |  | -22%p | 38% |
| SPIRIT_GRAFTER.INITIAL | REJECTION | 66% | 247 / 16 | 0.96 | A (0.715) | 66% | 넣음 |  | +16%p | 50% |
| SPIRIT_GRAFTER.INITIAL | LET_IT_IN | 34% | 247 / 16 | 0.96 | C (0.715) | 34% | 넣음 |  | -16%p | 50% |
| TRASH_HEAP.INITIAL | DIVE_IN | 61% | 239 / 17 | 0.96 | A (0.61) | 59% | 넣음 |  | +1%p | 68% |
| TRASH_HEAP.INITIAL | GRAB | 39% | 239 / 17 | 0.965 | C (0.61) | 41% | 넣음 |  | -1%p | 32% |
| SAPPHIRE_SEED.INITIAL | PLANT | 56% | 232 / 21 | 0.8 | B (0.555) | 56% | 넣음 |  | +10%p | 48% |
| SAPPHIRE_SEED.INITIAL | EAT | 44% | 232 / 21 | 0.8 | B (0.555) | 44% | 넣음 |  | -10%p | 52% |
| TABLET_OF_TRUTH.INITIAL | SMASH | 68% | 231 / 15 | 0.975 | A (0.685) | 72% | 넣음 |  | -8%p | 55% |
| TABLET_OF_TRUTH.INITIAL | DECIPHER_1 | 32% | 231 / 15 | 0.975 | C (0.685) | 28% | 넣음 |  | +8%p | 45% |
| JUNGLE_MAZE_ADVENTURE.INITIAL | JOIN_FORCES | 68% | 226 / 19 | 0.995 | A (0.81) | 67% | 넣음 |  | +7%p | 50% |
| JUNGLE_MAZE_ADVENTURE.INITIAL | SOLO_QUEST | 32% | 226 / 19 | 0.995 | C (0.81) | 33% | 넣음 |  | -7%p | 50% |
| BYRDONIS_NEST.INITIAL | EAT | 72% | 216 / 18 | 1.0 | A (0.935) | 72% | 넣음 |  | -6%p | 51% |
| BYRDONIS_NEST.INITIAL | TAKE | 28% | 216 / 18 | 1.0 | C (0.935) | 28% | 넣음 |  | +6%p | 49% |
| WELLSPRING.INITIAL | BATHE | 95% | 216 / 17 | 1.0 | S (0.995) | 91% | 넣음 |  | +13%p | 62% |
| WELLSPRING.INITIAL | BOTTLE | 5% | 216 / 17 | 1.0 | D (0.995) | 9% | 넣음 |  | -13%p | 38% |
| STONE_OF_ALL_TIME.INITIAL | LIFT | 59% | 215 / 16 | 0.99 | B (0.53) | 60% | 넣음 |  | -7%p | 69% |
| STONE_OF_ALL_TIME.INITIAL | PUSH | 41% | 215 / 16 | 0.99 | B (0.53) | 40% | 넣음 |  | +7%p | 31% |
| AROMA_OF_CHAOS.INITIAL | LET_GO | 66% | 214 / 20 | 0.945 | A (0.715) | 71% | 넣음 |  | -16%p | 50% |
| AROMA_OF_CHAOS.INITIAL | MAINTAIN_CONTROL | 34% | 214 / 20 | 0.945 | C (0.715) | 29% | 넣음 |  | +16%p | 50% |
| DENSE_VEGETATION.INITIAL | TRUDGE_ON | 57% | 214 / 19 | 0.855 | B (0.75) | 54% | 넣음 |  | -10%p | 69% |
| DENSE_VEGETATION.INITIAL | REST | 43% | 214 / 19 | 0.855 | B (0.75) | 46% | 넣음 |  | +10%p | 31% |
| AMALGAMATOR.INITIAL | COMBINE_STRIKES | 70% | 206 / 16 | 1.0 | A (0.89) | 71% | 넣음 |  | -22%p | 61% |
| AMALGAMATOR.INITIAL | COMBINE_DEFENDS | 30% | 206 / 16 | 1.0 | C (0.89) | 29% | 넣음 |  | +22%p | 39% |
| WOOD_CARVINGS.INITIAL | BIRD | 44% | 198 / 19 | 0.905 | B (0.66) | 42% | 넣음 |  | -12%p | 37% |
| WOOD_CARVINGS.INITIAL | SNAKE | 32% | 198 / 19 | 0.595 | C (0.89) | 34% | 불확실 |  | +4%p | 28% |
| WOOD_CARVINGS.INITIAL | TORUS | 25% | 198 / 19 | 0.85 | C (0.68) | 24% | 넣음 |  | +19%p | 36% |
| WELCOME_TO_WONGOS.INITIAL | BARGAIN_BIN | 53% | 194 / 18 | 1.0 | B (0.835) | 59% | 넣음 |  | +12%p | 65% |
| WELCOME_TO_WONGOS.INITIAL | FEATURED_ITEM | 25% | 194 / 18 | 0.545 | C (0.84) | 25% | 불확실 |  | -9%p | 15% |
| WELCOME_TO_WONGOS.INITIAL | MYSTERY_BOX | 23% | 194 / 18 | 0.655 | C (0.68) | 16% | 불확실 |  | -19%p | 18% |
| WELCOME_TO_WONGOS.INITIAL | LEAVE | 0% | 194 / 18 | 1.0 | D (1.0) | 1% | 넣음 |  | — | 2% |
| ZEN_WEAVER.INITIAL | ARACHNID_ACUPUNCTURE | 48% | 187 / 19 | 0.995 | B (0.8) | 40% | 넣음 |  | -8%p | 18% |
| ZEN_WEAVER.INITIAL | EMOTIONAL_AWARENESS | 44% | 187 / 19 | 0.935 | B (0.76) | 50% | 넣음 |  | +9%p | 44% |
| ZEN_WEAVER.INITIAL | BREATHING_TECHNIQUES | 7% | 187 / 19 | 1.0 | D (1.0) | 10% | 넣음 |  | -15%p | 38% |
| WHISPERING_HOLLOW.INITIAL | HUG | 87% | 181 / 16 | 1.0 | S (0.95) | 84% | 넣음 |  | +28%p | 77% |
| WHISPERING_HOLLOW.INITIAL | GOLD | 13% | 181 / 16 | 1.0 | D (0.95) | 16% | 넣음 |  | -28%p | 23% |
| WATERLOGGED_SCRIPTORIUM.INITIAL | PRICKLY_SPONGE | 55% | 176 / 16 | 0.99 | B (0.69) | 41% | 넣음 |  | +3%p | 12% |
| WATERLOGGED_SCRIPTORIUM.INITIAL | BLOODY_INK | 29% | 176 / 16 | 0.71 | C (0.815) | 44% | 불확실 |  | -12%p | 74% |
| WATERLOGGED_SCRIPTORIUM.INITIAL | TENTACLE_QUILL | 16% | 176 / 16 | 1.0 | D (0.885) | 15% | 넣음 |  | +2%p | 14% |
| MORPHIC_GROVE.INITIAL | GROUP | 57% | 157 / 17 | 0.76 | B (0.6) | 62% | 불확실 |  | +9%p | 33% |
| MORPHIC_GROVE.INITIAL | LONER | 43% | 157 / 17 | 0.76 | B (0.6) | 38% | 불확실 |  | -9%p | 67% |

전체 목록(작은 이벤트, 막별, 버전별 n 포함)은 `event_options.json`.

### 상위 플레이어와 전체 유저가 크게 다른 선택 (안정, |차이| ≥ 15%p)

| 이벤트.페이지 | 선택지 | 상위(good) | OP.GG 전체 v0.107.1 |
|---|---|---|---|
| WATERLOGGED_SCRIPTORIUM.INITIAL | PRICKLY_SPONGE | 55% | 12% |
| WELLSPRING.INITIAL | BOTTLE | 5% | 38% |
| WELLSPRING.INITIAL | BATHE | 95% | 62% |
| ZEN_WEAVER.INITIAL | BREATHING_TECHNIQUES | 7% | 38% |
| ZEN_WEAVER.INITIAL | ARACHNID_ACUPUNCTURE | 48% | 18% |
| BRAIN_LEECH.INITIAL | RIP | 58% | 28% |
| BRAIN_LEECH.INITIAL | SHARE_KNOWLEDGE | 42% | 72% |
| BYRDONIS_NEST.INITIAL | EAT | 72% | 51% |
| BYRDONIS_NEST.INITIAL | TAKE | 28% | 49% |
| GRAVE_OF_THE_FORGOTTEN.INITIAL | CONFRONT | 57% | 37% |
| GRAVE_OF_THE_FORGOTTEN.INITIAL | ACCEPT | 43% | 63% |
| JUNGLE_MAZE_ADVENTURE.INITIAL | JOIN_FORCES | 68% | 50% |
| JUNGLE_MAZE_ADVENTURE.INITIAL | SOLO_QUEST | 32% | 50% |
| SELF_HELP_BOOK.INITIAL | READ_ENTIRE_BOOK | 45% | 28% |
| COLOSSAL_FLOWER.INITIAL | EXTRACT_CURRENT_PRIZE_1 | 35% | 52% |

## 2. 고대 선택 (NEOW 포함)

선택지 105개 중 방향이 안정해 팩에 넣은 것 79개. 중립 = 33%.

| 고대 | 선택지 | good 균형 | 제시 n / 명 | 방향 부트 | 등급(부트) | all 균형 | 팩 | 승률 차(참고) | OP.GG 전체 픽률 |
|---|---|---|---|---|---|---|---|---|---|
| NEOW | SILKEN_TRESS | 77% | 395 / 18 | 1.0 | A (0.74) | 81% | 넣음 | -19%p | 53% |
| NEOW | LEAFY_POULTICE | 55% | 348 / 20 | 0.985 | B (0.67) | 61% | 넣음 | -12%p | 35% |
| NEOW | CURSED_PEARL | 53% | 382 / 19 | 0.975 | B (0.69) | 56% | 넣음 | -6%p | 38% |
| NEOW | SILVER_CRUCIBLE | 50% | 362 / 18 | 0.97 | B (0.77) | 51% | 넣음 | +2%p | 38% |
| NEOW | LEAD_PAPERWEIGHT | 49% | 437 / 19 | 1.0 | B (0.935) | 45% | 넣음 | +2%p | 34% |
| NEOW | ARCANE_SCROLL | 48% | 372 / 19 | 0.995 | B (0.875) | 48% | 넣음 | +2%p | 59% |
| NEOW | SMALL_CAPSULE | 48% | 191 / 17 | 0.975 | B (0.83) | 47% | 넣음 | +0%p | 50% |
| NEOW | GOLDEN_PEARL | 42% | 400 / 20 | 0.885 | B (0.56) | 43% | 넣음 | -6%p | 36% |
| NEOW | NEW_LEAF | 40% | 369 / 20 | 0.945 | B (0.555) | 43% | 넣음 | +4%p | 32% |
| NEOW | DOWSING_ROD | 40% | 187 / 11 | 0.735 | B (0.475) | 40% | 불확실 | +17%p | — |
| NEOW | HEFTY_TABLET | 39% | 372 / 19 | 0.74 | C (0.515) | 43% | 불확실 | -5%p | 53% |
| NEOW | KALEIDOSCOPE | 38% | 416 / 19 | 0.81 | C (0.685) | 36% | 넣음 | -5%p | 34% |
| NEOW | PRECARIOUS_SHEARS | 38% | 391 / 19 | 0.78 | C (0.555) | 37% | 불확실 | +7%p | 25% |
| NEOW | NEOWS_BONES | 38% | 355 / 20 | 0.655 | C (0.595) | 45% | 불확실 | -18%p | 45% |
| NEOW | LARGE_CAPSULE | 30% | 367 / 19 | 0.775 | C (0.845) | 34% | 불확실 | -4%p | 46% |
| NEOW | BOOMING_CONCH | 26% | 439 / 18 | 0.855 | C (0.805) | 22% | 넣음 | +3%p | 24% |
| NEOW | FISHING_ROD | 24% | 455 / 20 | 0.865 | C (0.765) | 21% | 넣음 | +16%p | 49% |
| NEOW | POMANDER | 24% | 230 / 17 | 0.92 | C (0.71) | 22% | 넣음 | +4%p | 18% |
| NEOW | WINGED_BOOTS | 23% | 393 / 20 | 0.96 | C (0.63) | 19% | 넣음 | +2%p | 11% |
| NEOW | SCROLL_BOXES | 22% | 457 / 20 | 1.0 | C (0.77) | 23% | 넣음 | -13%p | 34% |
| NEOW | PRECISE_SCISSORS | 22% | 388 / 19 | 0.97 | C (0.615) | 20% | 넣음 | +8%p | 15% |
| NEOW | NEOWS_SACRIFICE | 20% | 181 / 10 | 0.99 | C (0.455) | 16% | 넣음 | -12%p | — |
| NEOW | NEOWS_TALISMAN | 17% | 217 / 18 | 0.99 | D (0.685) | 17% | 넣음 | -12%p | 15% |
| NEOW | LOST_COFFER | 15% | 415 / 20 | 1.0 | D (0.98) | 13% | 넣음 | -8%p | 21% |
| NEOW | LAVA_ROCK | 14% | 217 / 17 | 1.0 | D (0.96) | 14% | 넣음 | +3%p | 34% |
| NEOW | NEOWS_TORMENT | 14% | 425 / 20 | 1.0 | D (0.9) | 13% | 넣음 | +10%p | 14% |
| NEOW | PHIAL_HOLSTER | 13% | 429 / 19 | 1.0 | D (0.995) | 12% | 넣음 | -2%p | 18% |
| NEOW | NUTRITIOUS_OYSTER | 10% | 235 / 18 | 1.0 | D (0.99) | 9% | 넣음 | -2%p | 20% |
| NEOW | STONE_HUMIDIFIER | 9% | 195 / 16 | 1.0 | D (1.0) | 12% | 넣음 | -6%p | 25% |
| DARV | RUNIC_PYRAMID | 57% | 140 / 15 | 0.995 | B (0.61) | 56% | 넣음 | -0%p | 32% |
| DARV | PANDORAS_BOX | 51% | 137 / 19 | 1.0 | B (0.885) | 54% | 넣음 | +5%p | 44% |
| DARV | ASTROLABE | 48% | 143 / 17 | 1.0 | B (0.89) | 51% | 넣음 | +9%p | 47% |
| DARV | EMPTY_CAGE | 35% | 142 / 17 | 0.625 | C (0.775) | 32% | 불확실 | -18%p | 38% |
| DARV | CALLING_BELL | 34% | 139 / 18 | 0.535 | C (0.84) | 33% | 불확실 | +6%p | 42% |
| DARV | BLACK_STAR | 33% | 131 / 17 | 0.49 | C (0.78) | 31% | 불확실 | -10%p | 45% |
| DARV | DUSTY_TOME | 32% | 249 / 19 | 0.67 | C (0.93) | 30% | 불확실 | -11%p | 28% |
| DARV | PHILOSOPHERS_STONE | 26% | 62 / 14 | 0.835 | C (0.715) | 31% | 넣음 | -5%p | 42% |
| DARV | SOZU | 21% | 67 / 16 | 0.955 | C (0.555) | 17% | 넣음 | +20%p | 21% |
| DARV | SNECKO_EYE | 14% | 130 / 16 | 1.0 | D (0.985) | 17% | 넣음 | -20%p | 7% |
| DARV | VELVET_CHOKER | 13% | 76 / 12 | 0.995 | D (0.915) | 8% | 넣음 | +13%p | 14% |
| DARV | ECTOPLASM | 9% | 78 / 15 | 1.0 | D (0.98) | 7% | 넣음 | -13%p | 13% |
| NONUPEIPE | SIGNET_RING | 55% | 207 / 19 | 0.995 | B (0.7) | 52% | 넣음 | +1%p | 51% |
| NONUPEIPE | GLITTER | 48% | 207 / 18 | 0.965 | B (0.76) | 41% | 넣음 | -5%p | 46% |
| NONUPEIPE | DIAMOND_DIADEM | 43% | 202 / 15 | 0.885 | B (0.615) | 43% | 넣음 | -4%p | 15% |
| NONUPEIPE | BRILLIANT_SCARF | 40% | 199 / 18 | 0.845 | C (0.5) | 37% | 넣음 | -0%p | 31% |
| NONUPEIPE | JEWELRY_BOX | 33% | 227 / 17 | 0.5 | C (0.97) | 35% | 불확실 | -9%p | 33% |
| NONUPEIPE | LOOMING_FRUIT | 28% | 187 / 17 | 0.91 | C (0.96) | 26% | 넣음 | +7%p | 35% |
| NONUPEIPE | BEAUTIFUL_BRACELET | 24% | 216 / 16 | 1.0 | C (0.865) | 30% | 넣음 | -0%p | 29% |
| NONUPEIPE | BLESSED_ANTLER | 23% | 220 / 16 | 0.93 | C (0.705) | 25% | 넣음 | -2%p | 32% |
| NONUPEIPE | DELICATE_FROND | 20% | 213 / 16 | 1.0 | D (0.535) | 25% | 넣음 | +6%p | 27% |
| NONUPEIPE | FUR_COAT | 17% | 213 / 17 | 0.99 | D (0.71) | 17% | 넣음 | +3%p | 32% |
| OROBAS | PRISMATIC_GEM | 63% | 64 / 14 | 1.0 | A (0.58) | 72% | 넣음 | -19%p | 37% |
| OROBAS | IRONCLAD | 53% | 33 / 11 | 0.96 | B (0.63) | 49% | 넣음 | -16%p | — |
| OROBAS | ARCHAIC_TOOTH | 50% | 375 / 18 | 1.0 | B (0.975) | 47% | 넣음 | +8%p | 60% |
| OROBAS | ELECTRIC_SHRYMP | 44% | 240 / 17 | 0.985 | B (0.8) | 43% | 넣음 | -3%p | 28% |
| OROBAS | SAND_CASTLE | 36% | 186 / 18 | 0.765 | C (0.66) | 36% | 불확실 | +10%p | 34% |
| OROBAS | NECROBINDER | 32% | 32 / 10 | 0.625 | C (0.555) | 25% | 불확실 | -49%p | — |
| OROBAS | TOUCH_OF_OROBAS | 31% | 399 / 19 | 0.64 | C (0.94) | 27% | 불확실 | +8%p | 50% |
| OROBAS | DEFECT | 31% | 30 / 11 | 0.61 | C (0.745) | 38% | 불확실 | -22%p | — |
| OROBAS | ALCHEMICAL_COFFER | 29% | 227 / 19 | 0.77 | C (0.87) | 28% | 불확실 | +7%p | 14% |
| OROBAS | GLASS_EYE | 25% | 226 / 19 | 0.995 | C (0.9) | 28% | 넣음 | -21%p | 28% |
| OROBAS | RADIANT_PEARL | 24% | 228 / 18 | 0.99 | C (0.8) | 28% | 넣음 | -11%p | 20% |
| OROBAS | SILENT | 22% | 25 / 8 | 0.82 | C (0.475) | 19% | 넣음 | -4%p | — |
| OROBAS | DRIFTWOOD | 10% | 223 / 17 | 1.0 | D (0.995) | 12% | 넣음 | +2%p | 15% |
| OROBAS | REGENT | 10% | 34 / 12 | 1.0 | D (0.975) | 12% | 넣음 | -1%p | — |
| PAEL | PAELS_TEARS | 46% | 259 / 20 | 0.96 | B (0.71) | 43% | 넣음 | +1%p | 37% |
| PAEL | PAELS_BLOOD | 45% | 269 / 18 | 0.95 | B (0.72) | 46% | 넣음 | +9%p | 38% |
| PAEL | PAELS_WING | 44% | 259 / 18 | 0.925 | B (0.655) | 43% | 넣음 | -0%p | 61% |
| PAEL | PAELS_FLESH | 43% | 258 / 19 | 0.91 | B (0.63) | 46% | 넣음 | -5%p | 54% |
| PAEL | PAELS_LEGION | 32% | 247 / 17 | 0.625 | C (0.855) | 30% | 불확실 | +10%p | 21% |
| PAEL | PAELS_GROWTH | 32% | 120 / 16 | 0.57 | C (0.85) | 34% | 불확실 | +13%p | 33% |
| PAEL | PAELS_HORN | 27% | 271 / 18 | 0.985 | C (0.96) | 27% | 넣음 | -7%p | 24% |
| PAEL | PAELS_CLAW | 25% | 188 / 16 | 0.97 | C (0.895) | 26% | 넣음 | +4%p | 27% |
| PAEL | PAELS_TOOTH | 23% | 221 / 18 | 0.975 | C (0.705) | 23% | 넣음 | -9%p | 29% |
| PAEL | PAELS_EYE | 16% | 272 / 19 | 0.995 | D (0.845) | 14% | 넣음 | -11%p | 9% |
| TANX | SAI | 51% | 203 / 16 | 0.99 | B (0.89) | 52% | 넣음 | +7%p | 46% |
| TANX | IRON_CLUB | 45% | 195 / 16 | 0.985 | B (0.85) | 47% | 넣음 | -1%p | 28% |
| TANX | THROWING_AXE | 40% | 207 / 16 | 0.905 | B (0.61) | 42% | 넣음 | -9%p | 48% |
| TANX | TRI_BOOMERANG | 39% | 186 / 17 | 0.975 | C (0.61) | 36% | 넣음 | +7%p | 48% |
| TANX | WAR_HAMMER | 38% | 215 / 17 | 0.785 | C (0.71) | 35% | 불확실 | -5%p | 36% |
| TANX | MEAT_CLEAVER | 36% | 191 / 16 | 0.66 | C (0.77) | 41% | 불확실 | +7%p | 37% |
| TANX | CROSSBOW | 32% | 204 / 17 | 0.515 | C (0.865) | 34% | 불확실 | +5%p | 33% |
| TANX | CLAWS | 19% | 203 / 18 | 1.0 | D (0.73) | 15% | 넣음 | -22%p | 17% |
| TANX | SPIKED_GAUNTLETS | 17% | 217 / 18 | 1.0 | D (0.75) | 16% | 넣음 | -18%p | 21% |
| TANX | TANXS_WHISTLE | 16% | 192 / 18 | 1.0 | D (0.9) | 14% | 넣음 | +4%p | 17% |
| TEZCATARA | YUMMY_COOKIE | 62% | 280 / 20 | 1.0 | A (0.7) | 61% | 넣음 | -7%p | 72% |
| TEZCATARA | VERY_HOT_COCOA | 56% | 262 / 17 | 1.0 | B (0.78) | 56% | 넣음 | -9%p | 68% |
| TEZCATARA | NUTRITIOUS_SOUP | 42% | 230 / 18 | 0.93 | B (0.63) | 42% | 넣음 | -2%p | 53% |
| TEZCATARA | PUMPKIN_CANDLE | 33% | 211 / 18 | 0.53 | C (0.74) | 32% | 불확실 | +19%p | 27% |
| TEZCATARA | GOLDEN_COMPASS | 29% | 193 / 17 | 0.775 | C (0.96) | 29% | 불확실 | -1%p | 38% |
| TEZCATARA | TOASTY_MITTENS | 25% | 251 / 17 | 0.945 | C (0.685) | 27% | 넣음 | +7%p | 5% |
| TEZCATARA | SEAL_OF_GOLD | 23% | 188 / 18 | 0.87 | C (0.59) | 20% | 넣음 | +23%p | 15% |
| TEZCATARA | STORYBOOK | 23% | 267 / 19 | 0.975 | C (0.68) | 21% | 넣음 | -16%p | 18% |
| TEZCATARA | BIIIG_HUG | 17% | 254 / 19 | 1.0 | D (0.76) | 18% | 넣음 | -14%p | 10% |
| TEZCATARA | TOY_BOX | 13% | 180 / 17 | 1.0 | D (0.995) | 16% | 넣음 | +2%p | 16% |
| VAKUU | JEWELED_MASK | 65% | 170 / 17 | 1.0 | A (0.76) | 69% | 넣음 | +12%p | 77% |
| VAKUU | CHOICES_PARADOX | 42% | 168 / 16 | 0.845 | B (0.57) | 36% | 넣음 | +12%p | 47% |
| VAKUU | MUSIC_BOX | 38% | 158 / 18 | 0.805 | C (0.55) | 42% | 넣음 | +20%p | 44% |
| VAKUU | PRESERVED_FOG | 38% | 225 / 18 | 0.775 | C (0.64) | 36% | 불확실 | -3%p | 31% |
| VAKUU | DISTINGUISHED_CAPE | 31% | 213 / 18 | 0.66 | C (0.96) | 33% | 불확실 | -12%p | 32% |
| VAKUU | SERE_TALON | 30% | 215 / 17 | 0.765 | C (0.94) | 28% | 불확실 | +5%p | 15% |
| VAKUU | WHISPERING_EARRING | 29% | 218 / 19 | 0.695 | C (0.84) | 24% | 불확실 | -24%p | 26% |
| VAKUU | FIDDLE | 23% | 234 / 17 | 0.995 | C (0.74) | 23% | 넣음 | +11%p | 16% |
| VAKUU | BLOOD_SOAKED_ROSE | 22% | 201 / 17 | 0.98 | C (0.67) | 23% | 넣음 | -12%p | 22% |
| VAKUU | LORDS_PARASOL | 22% | 157 / 17 | 0.945 | C (0.545) | 27% | 넣음 | -8%p | 38% |

캐릭터별(NEOW 등, 캐릭터당 제시 20회 이상)은 `ancients.json`의 `byCharacter`.

## 3. 유물

- 보상 유물: 선택이 아님(99.9% 수령). 상점: 진열 3개 중 구매. 상점 구매율 중앙값 33%.
- `relics.json` 항목 231개, 그중 값을 쓰는(`use: true`) 것 82개 = 상점 6개(등급 기준) + 고대 유물 76개(방향 기준). 상점 구매율은 대부분 20~50%에 몰려 있고 골드에 크게 좌우돼 등급 경계에서 흔들립니다(불확실 → null). 구매 직전 골드 300 이상인 상점만 본 값(`pickRateGold300`)을 참고로 같이 넣었습니다.

| 유물 | 희귀도 | 신호 | 균형 비율 | n / 명 | 부트 | 등급 | 골드≥300 | 보유 승률 배수(참고) |
|---|---|---|---|---|---|---|---|---|
| DATA_DISK | Common | 상점 | 71% | 71 / 15 | 0.815 | A | 98% | 0.987 |
| RUNIC_CAPACITOR | Shop | 상점 | 68% | 74 / 14 | 0.82 | A | 83% | — |
| MINIATURE_TENT | Shop | 상점 | 64% | 355 / 18 | 0.88 | A | 87% | 0.991 |
| MEMBERSHIP_CARD | Shop | 상점 | 61% | 304 / 19 | 0.565 | 불확실 | 91% | 1.055 |
| BAG_OF_PREPARATION | Common | 상점 | 56% | 381 / 19 | 0.665 | 불확실 | 81% | 1.005 |
| LANTERN | Common | 상점 | 56% | 378 / 19 | 0.67 | 불확실 | 75% | 1.023 |
| PANTOGRAPH | Uncommon | 상점 | 54% | 211 / 19 | 0.805 | B | 86% | 1.088 |
| PAPER_PHROG | Uncommon | 상점 | 54% | 62 / 13 | 0.635 | 불확실 | 77% | 0.843 |
| PAPER_KRANE | Rare | 상점 | 53% | 33 / 12 | 0.65 | 불확실 | — | — |
| ANCHOR | Common | 상점 | 52% | 432 / 19 | 0.785 | 불확실 | 76% | 1.035 |
| SNECKO_SKULL | Common | 상점 | 51% | 119 / 14 | 0.7 | 불확실 | 64% | 1.04 |
| ICE_CREAM | Rare | 상점 | 49% | 80 / 15 | 0.7 | 불확실 | 78% | 1.091 |
| MUMMIFIED_HAND | Rare | 상점 | 49% | 90 / 16 | 0.75 | 불확실 | 73% | 1.165 |
| HAPPY_FLOWER | Common | 상점 | 48% | 393 / 18 | 0.785 | 불확실 | 78% | 1.025 |
| SELF_FORMING_CLAY | Uncommon | 상점 | 47% | 59 / 14 | 0.625 | 불확실 | 63% | 1.098 |
| RAINBOW_RING | Rare | 상점 | 46% | 85 / 14 | 0.57 | 불확실 | 71% | 1.076 |
| GHOST_SEED | Shop | 상점 | 45% | 369 / 18 | 0.655 | 불확실 | 64% | 0.982 |
| CENTENNIAL_PUZZLE | Common | 상점 | 45% | 403 / 16 | 0.725 | 불확실 | 69% | 1.034 |
| KUNAI | Rare | 상점 | 45% | 101 / 15 | 0.555 | 불확실 | 74% | 1.025 |
| POTION_BELT | Common | 상점 | 44% | 378 / 19 | 0.725 | 불확실 | 69% | 0.989 |
| RED_SKULL | Common | 상점 | 44% | 86 / 17 | 0.65 | 불확실 | 71% | 1.104 |
| KIFUDA | Shop | 상점 | 43% | 395 / 20 | 0.635 | 불확실 | 67% | 1.016 |
| RAZOR_TOOTH | Rare | 상점 | 43% | 93 / 16 | 0.665 | 불확실 | 63% | 1.1 |
| GAMBLING_CHIP | Rare | 상점 | 42% | 95 / 15 | 0.57 | 불확실 | 73% | 1.098 |
| TOXIC_EGG | Rare | 상점 | 42% | 77 / 15 | 0.525 | 불확실 | 83% | 1.133 |
| STURDY_CLAMP | Rare | 상점 | 42% | 95 / 18 | 0.525 | 불확실 | 60% | 1.094 |
| HORN_CLEAT | Uncommon | 상점 | 41% | 218 / 19 | 0.545 | 불확실 | 68% | 1.009 |
| ODDLY_SMOOTH_STONE | Common | 상점 | 41% | 377 / 18 | 0.53 | 불확실 | 67% | 0.989 |
| DOLLYS_MIRROR | Shop | 상점 | 40% | 364 / 16 | 0.565 | 불확실 | 66% | 1.052 |
| RUINED_HELMET | Rare | 상점 | 40% | 30 / 14 | 0.455 | 불확실 | — | — |
| WHITE_STAR | Rare | 상점 | 39% | 61 / 16 | 0.51 | 불확실 | 60% | 1.088 |
| ORRERY | Shop | 상점 | 39% | 363 / 19 | 0.525 | 불확실 | 55% | 1.02 |
| PERMAFROST | Uncommon | 상점 | 39% | 199 / 17 | 0.475 | 불확실 | 60% | 1.039 |
| CAPTAINS_WHEEL | Rare | 상점 | 38% | 100 / 16 | 0.485 | 불확실 | 67% | 1.05 |
| FUNERARY_MASK | Uncommon | 상점 | 38% | 47 / 10 | 0.48 | 불확실 | — | 1.086 |
| GAME_PIECE | Rare | 상점 | 38% | 93 / 18 | 0.555 | 불확실 | 63% | 1.038 |
| SHURIKEN | Rare | 상점 | 38% | 90 / 17 | 0.535 | 불확실 | 55% | 0.955 |
| REGALITE | Uncommon | 상점 | 38% | 32 / 8 | 0.51 | 불확실 | — | — |
| WAR_PAINT | Common | 상점 | 38% | 384 / 20 | 0.63 | 불확실 | 62% | 1.019 |
| ETERNAL_FEATHER | Uncommon | 상점 | 38% | 241 / 19 | 0.575 | 불확실 | 65% | 1.034 |
| VAMBRACE | Uncommon | 상점 | 37% | 232 / 17 | 0.52 | 불확실 | 62% | 0.986 |
| REGAL_PILLOW | Common | 상점 | 36% | 400 / 19 | 0.635 | 불확실 | 57% | 0.974 |
| STONE_CRACKER | Uncommon | 상점 | 36% | 206 / 17 | 0.6 | 불확실 | 59% | 1.053 |
| INTIMIDATING_HELMET | Rare | 상점 | 36% | 110 / 15 | 0.595 | 불확실 | 69% | 1.084 |
| SYMBIOTIC_VIRUS | Uncommon | 상점 | 36% | 43 / 10 | 0.515 | 불확실 | — | — |
| GORGET | Common | 상점 | 35% | 424 / 18 | 0.705 | 불확실 | 62% | 1.022 |
| ROYAL_STAMP | Shop | 상점 | 35% | 352 / 18 | 0.705 | 불확실 | 56% | 1.182 |
| PEN_NIB | Uncommon | 상점 | 35% | 210 / 18 | 0.69 | 불확실 | 52% | 1.04 |
| BEATING_REMNANT | Rare | 상점 | 35% | 116 / 17 | 0.575 | 불확실 | 55% | 1.069 |
| BRONZE_SCALES | Common | 상점 | 35% | 381 / 18 | 0.72 | 불확실 | 55% | 1.042 |
| CLOAK_CLASP | Rare | 상점 | 34% | 109 / 16 | 0.595 | 불확실 | 57% | 1.142 |
| PUNCH_DAGGER | Shop | 상점 | 34% | 374 / 19 | 0.74 | 불확실 | 50% | 0.869 |
| JOSS_PAPER | Uncommon | 상점 | 34% | 207 / 18 | 0.705 | 불확실 | 49% | 1.062 |
| FROZEN_EGG | Rare | 상점 | 34% | 69 / 14 | 0.585 | 불확실 | — | 0.988 |
| MEAT_ON_THE_BONE | Rare | 상점 | 34% | 96 / 15 | 0.68 | 불확실 | 50% | 1.019 |
| TUNING_FORK | Uncommon | 상점 | 34% | 218 / 19 | 0.725 | 불확실 | 52% | 1.028 |
| BAG_OF_MARBLES | Common | 상점 | 34% | 386 / 19 | 0.68 | 불확실 | 55% | 1.012 |
| CANDELABRA | Uncommon | 상점 | 34% | 217 / 20 | 0.785 | 불확실 | 62% | 1.084 |
| VENERABLE_TEA_SET | Common | 상점 | 34% | 356 / 20 | 0.64 | 불확실 | 58% | 1.032 |
| VEXING_PUZZLEBOX | Rare | 상점 | 33% | 80 / 14 | 0.615 | 불확실 | 54% | 1.02 |
| PENDULUM | Common | 상점 | 33% | 389 / 19 | 0.745 | 불확실 | 58% | 1.052 |
| TINGSHA | Uncommon | 상점 | 33% | 74 / 13 | 0.615 | 불확실 | 59% | 1.12 |
| BELLOWS | Rare | 상점 | 33% | 85 / 19 | 0.655 | 불확실 | 46% | 1.122 |
| CHANDELIER | Rare | 상점 | 33% | 100 / 16 | 0.675 | 불확실 | 48% | 1.047 |
| MINIATURE_CANNON | Uncommon | 상점 | 33% | 206 / 20 | 0.765 | 불확실 | 51% | 0.939 |
| DRAGON_FRUIT | Shop | 상점 | 33% | 335 / 19 | 0.785 | 불확실 | 52% | 1.028 |
| MOLTEN_EGG | Rare | 상점 | 33% | 95 / 16 | 0.695 | 불확실 | 52% | 1.076 |
| NUNCHAKU | Uncommon | 상점 | 32% | 212 / 18 | 0.74 | 불확실 | 49% | 1.005 |
| LEES_WAFFLE | Shop | 상점 | 32% | 370 / 20 | 0.705 | 불확실 | 54% | 0.928 |
| GOLD_PLATED_CABLES | Uncommon | 상점 | 32% | 39 / 12 | 0.655 | 불확실 | — | — |
| LIZARD_TAIL | Rare | 상점 | 32% | 105 / 19 | 0.75 | 불확실 | 62% | 1.003 |
| PARRYING_SHIELD | Uncommon | 상점 | 32% | 233 / 19 | 0.715 | 불확실 | 40% | 1.031 |
| PRAYER_WHEEL | Rare | 상점 | 32% | 103 / 16 | 0.625 | 불확실 | 49% | 1.133 |
| RINGING_TRIANGLE | Shop | 상점 | 32% | 367 / 18 | 0.78 | 불확실 | 54% | 0.977 |
| UNCEASING_TOP | Rare | 상점 | 32% | 107 / 17 | 0.61 | 불확실 | 47% | 0.908 |
| FENCING_MANUAL | Common | 상점 | 31% | 56 / 12 | 0.665 | 불확실 | — | 1.101 |
| RED_MASK | Common | 상점 | 31% | 398 / 21 | 0.72 | 불확실 | 52% | 1.07 |
| SHOVEL | Rare | 상점 | 31% | 82 / 16 | 0.63 | 불확실 | 45% | 1.074 |
| ORNAMENTAL_FAN | Uncommon | 상점 | 31% | 211 / 19 | 0.705 | 불확실 | 50% | 1.076 |
| GNARLED_HAMMER | Shop | 상점 | 31% | 369 / 20 | 0.725 | 불확실 | 45% | 0.843 |
| VAJRA | Common | 상점 | 30% | 391 / 20 | 0.75 | 불확실 | 47% | 1.005 |
| TOOLBOX | Shop | 상점 | 30% | 368 / 20 | 0.78 | 불확실 | 47% | 1.019 |
| VITRUVIAN_MINION | Shop | 상점 | 30% | 44 / 11 | 0.6 | 불확실 | — | — |
| GREMLIN_HORN | Uncommon | 상점 | 30% | 239 / 18 | 0.695 | 불확실 | 42% | 1.008 |
| WHITE_BEAST_STATUE | Rare | 상점 | 30% | 90 / 15 | 0.72 | 불확실 | 56% | 1.058 |
| POCKETWATCH | Rare | 상점 | 30% | 100 / 15 | 0.68 | 불확실 | 46% | 1.027 |
| PEAR | Uncommon | 상점 | 29% | 232 / 17 | 0.72 | 불확실 | 49% | 0.995 |
| KUSARIGAMA | Uncommon | 상점 | 29% | 213 / 17 | 0.625 | 불확실 | 42% | 1.052 |
| LASTING_CANDY | Uncommon | 상점 | 28% | 199 / 17 | 0.595 | 불확실 | 42% | 1.059 |
| MANGO | Rare | 상점 | 28% | 92 / 17 | 0.655 | 불확실 | 51% | 1.046 |
| GIRYA | Rare | 상점 | 28% | 91 / 13 | 0.54 | 불확실 | 38% | 0.981 |
| LETTER_OPENER | Uncommon | 상점 | 28% | 207 / 18 | 0.695 | 불확실 | 40% | 1.035 |
| UNDYING_SIGIL | Shop | 상점 | 28% | 61 / 11 | 0.64 | 불확실 | 56% | — |
| BURNING_STICKS | Shop | 상점 | 28% | 351 / 18 | 0.755 | 불확실 | 42% | 1.16 |
| MEAL_TICKET | Common | 상점 | 28% | 335 / 19 | 0.68 | 불확실 | 45% | 0.995 |
| FESTIVE_POPPER | Common | 상점 | 27% | 392 / 19 | 0.745 | 불확실 | 38% | 1.048 |
| TUNGSTEN_ROD | Rare | 상점 | 27% | 103 / 15 | 0.62 | 불확실 | 67% | 1.061 |
| BRIMSTONE | Shop | 상점 | 27% | 85 / 17 | 0.66 | 불확실 | 38% | — |
| WHETSTONE | Common | 상점 | 27% | 377 / 18 | 0.695 | 불확실 | 45% | 0.979 |
| BLOOD_VIAL | Common | 상점 | 27% | 377 / 19 | 0.675 | 불확실 | 43% | 0.97 |
| BONE_FLUTE | Common | 상점 | 27% | 83 / 11 | 0.625 | 불확실 | 30% | 0.895 |
| WING_CHARM | Shop | 상점 | 26% | 363 / 18 | 0.74 | 불확실 | 35% | 1.08 |
| NINJA_SCROLL | Shop | 상점 | 26% | 105 / 15 | 0.665 | 불확실 | 36% | — |
| THE_ABACUS | Shop | 상점 | 26% | 383 / 19 | 0.665 | 불확실 | 40% | 1.217 |
| PLANISPHERE | Uncommon | 상점 | 26% | 177 / 16 | 0.595 | 불확실 | 38% | 1.036 |
| SPARKLING_ROUGE | Uncommon | 상점 | 26% | 205 / 18 | 0.69 | 불확실 | 35% | 1.015 |
| BELT_BUCKLE | Shop | 상점 | 25% | 369 / 19 | 0.69 | 불확실 | 40% | 1.021 |
| RIPPLE_BASIN | Uncommon | 상점 | 25% | 198 / 19 | 0.585 | 불확실 | 40% | 0.977 |
| STRAWBERRY | Common | 상점 | 25% | 388 / 19 | 0.64 | 불확실 | 40% | 1.003 |
| UNSETTLING_LAMP | Rare | 상점 | 25% | 104 / 15 | 0.635 | 불확실 | 33% | 1.02 |
| BOOK_OF_FIVE_RINGS | Common | 상점 | 25% | 329 / 19 | 0.635 | 불확실 | 38% | 0.985 |
| SLING_OF_COURAGE | Shop | 상점 | 24% | 356 / 21 | 0.665 | 불확실 | 37% | 1.029 |
| CHEMICAL_X | Shop | 상점 | 24% | 357 / 19 | 0.55 | 불확실 | 36% | 1.174 |
| LAVA_LAMP | Shop | 상점 | 24% | 384 / 20 | 0.625 | 불확실 | 34% | 1.06 |
| BREAD | Shop | 상점 | 24% | 332 / 20 | 0.835 | C | 37% | 0.941 |
| MYSTIC_LIGHTER | Shop | 상점 | 24% | 329 / 18 | 0.555 | 불확실 | 34% | 1.212 |
| AKABEKO | Uncommon | 상점 | 24% | 220 / 19 | 0.595 | 불확실 | 32% | 0.943 |
| REPTILE_TRINKET | Uncommon | 상점 | 23% | 223 / 20 | 0.605 | 불확실 | 33% | 1.022 |
| STONE_CALENDAR | Rare | 상점 | 23% | 92 / 16 | 0.605 | 불확실 | 39% | 0.999 |
| STRIKE_DUMMY | Common | 상점 | 23% | 402 / 18 | 0.575 | 불확실 | 32% | 0.913 |
| MERCURY_HOURGLASS | Uncommon | 상점 | 23% | 200 / 18 | 0.595 | 불확실 | 34% | 0.931 |
| TWISTED_FUNNEL | Uncommon | 상점 | 22% | 60 / 14 | 0.545 | 불확실 | — | 1.059 |
| ORICHALCUM | Uncommon | 상점 | 21% | 205 / 18 | 0.46 | 불확실 | 33% | 1.023 |
| JUZU_BRACELET | Common | 상점 | 21% | 307 / 19 | 0.52 | 불확실 | 33% | 1.047 |
| PETRIFIED_TOAD | Uncommon | 상점 | 21% | 231 / 17 | 0.485 | 불확실 | 31% | 1.007 |
| ART_OF_WAR | Rare | 상점 | 21% | 94 / 17 | 0.49 | 불확실 | 30% | 1.043 |
| BOOK_REPAIR_KNIFE | Uncommon | 상점 | 19% | 38 / 10 | 0.585 | 불확실 | — | — |
| TINY_MAILBOX | Uncommon | 상점 | 18% | 221 / 21 | 0.595 | 불확실 | 29% | 0.943 |
| CAULDRON | Shop | 상점 | 18% | 336 / 18 | 0.595 | 불확실 | 23% | 1.245 |
| SCREAMING_FLAGON | Shop | 상점 | 16% | 338 / 19 | 0.65 | 불확실 | 25% | 1.263 |
| DINGY_RUG | Shop | 상점 | 12% | 380 / 20 | 0.935 | D | 17% | — |

고대 유물은 2절 표와 같은 값입니다(`signal: ancient:<고대>`).

## 4. 포션

전투 보상 포션 62종 중 안정 35종. 보상 포션은 대부분 가져갑니다(70~100%). 상점 포션 구매율은 2~32%.

| 포션 | 보상 수령 good 균형 | n / 명 | 부트 | 결정 | 상점 구매 | 사용/획득 | 버림/획득 | 보스·엘리트 사용 비중 |
|---|---|---|---|---|---|---|---|---|
| POTION_SHAPED_ROCK | 100% | 1325 / 18 | 1.0 | S | — | 77% | 13% | 53% |
| FRUIT_JUICE | 99% | 173 / 17 | 1.0 | S | 24% | 98% | 1% | 28% |
| OROBIC_ACID | 99% | 203 / 16 | 1.0 | S | 17% | 89% | 8% | 85% |
| FAIRY_IN_A_BOTTLE | 95% | 186 / 16 | 1.0 | S | 29% | 57% | 9% | 94% |
| ENTROPIC_BREW | 95% | 162 / 17 | 1.0 | S | 20% | 93% | 5% | 79% |
| DUPLICATOR | 94% | 472 / 19 | 1.0 | S | 19% | 83% | 7% | 83% |
| SOLDIERS_STEW | 93% | 37 / 13 | 0.97 | S | 23% | 81% | 6% | 93% |
| POWER_POTION | 93% | 1258 / 21 | 1.0 | S | 31% | 92% | 6% | 90% |
| DISTILLED_CHAOS | 93% | 188 / 17 | 1.0 | S | 13% | 89% | 7% | 68% |
| FOCUS_POTION | 92% | 182 / 16 | 1.0 | S | 32% | 90% | 4% | 89% |
| TOUCH_OF_INSANITY | 91% | 471 / 20 | 0.99 | S | 11% | 77% | 14% | 81% |
| CURE_ALL | 91% | 473 / 21 | 0.99 | S | 16% | 92% | 6% | 70% |
| BLOOD_POTION | 91% | 267 / 18 | 0.955 | S | 29% | 91% | 3% | 53% |
| ESSENCE_OF_DARKNESS | 91% | 24 / 9 | 0.965 | S | 2% | 44% | 24% | 86% |
| GIGANTIFICATION_POTION | 90% | 193 / 17 | 0.975 | S | 12% | 70% | 9% | 81% |
| BEETLE_JUICE | 90% | 198 / 19 | 0.95 | S | 12% | 84% | 11% | 84% |
| LUCKY_TONIC | 90% | 173 / 18 | 0.935 | S | 17% | 74% | 7% | 74% |
| BONE_BREW | 90% | 90 / 12 | 0.955 | S | 16% | 87% | 9% | 64% |
| LIQUID_MEMORIES | 88% | 166 / 17 | 0.945 | S | 10% | 78% | 13% | 69% |
| CUNNING_POTION | 88% | 135 / 15 | 0.96 | S | 16% | 84% | 11% | 69% |
| KINGS_COURAGE | 88% | 67 / 10 | 0.94 | S | 4% | 60% | 30% | 70% |
| SHIP_IN_A_BOTTLE | 88% | 175 / 17 | 0.965 | S | 8% | 89% | 5% | 66% |
| MAZALETHS_GIFT | 86% | 206 / 17 | 0.855 | S | 10% | 76% | 12% | 96% |
| RADIANT_TINCTURE | 86% | 474 / 21 | 0.92 | S | 12% | 81% | 11% | 76% |
| HEART_OF_IRON | 85% | 468 / 19 | 0.86 | S | 9% | 92% | 7% | 73% |
| POT_OF_GHOULS | 85% | 40 / 12 | 0.705 | S 불확실 | 2% | 88% | 15% | 62% |
| COSMIC_CONCOCTION | 85% | 30 / 9 | 0.7 | S | 6% | 93% | 6% | 70% |
| REGEN_POTION | 85% | 425 / 20 | 0.88 | S | 12% | 91% | 6% | 57% |
| FORTIFIER | 84% | 461 / 20 | 0.88 | S | 9% | 79% | 14% | 71% |
| ENERGY_POTION | 84% | 1274 / 21 | 0.845 | S | 14% | 79% | 12% | 71% |
| POTION_OF_DOOM | 84% | 230 / 11 | 0.7 | S 불확실 | 10% | 81% | 15% | 67% |
| BLOCK_POTION | 84% | 1198 / 21 | 0.845 | S | 14% | 89% | 8% | 60% |
| POTION_OF_CAPACITY | 83% | 77 / 10 | 0.7 | S 불확실 | 2% | 46% | 27% | 93% |
| FOUL_POTION | 83% | 6 / 1 | 1.0 | S 불확실 | — | 84% | 3% | 2% |
| STAR_POTION | 83% | 176 / 13 | 0.695 | S 불확실 | 17% | 60% | 15% | 70% |
| DROPLET_OF_PRECOGNITION | 83% | 183 / 17 | 0.69 | S 불확실 | 5% | 83% | 12% | 74% |
| COLORLESS_POTION | 82% | 1242 / 21 | 0.77 | S 불확실 | 13% | 88% | 12% | 69% |
| GAMBLERS_BREW | 82% | 480 / 19 | 0.715 | S 불확실 | 9% | 71% | 17% | 74% |
| SKILL_POTION | 82% | 1240 / 21 | 0.705 | S 불확실 | 14% | 88% | 10% | 65% |
| SHACKLING_POTION | 82% | 187 / 16 | 0.635 | S 불확실 | 6% | 81% | 12% | 64% |
| POWDERED_DEMISE | 82% | 472 / 20 | 0.715 | S 불확실 | 6% | 81% | 15% | 83% |
| DEXTERITY_POTION | 82% | 1208 / 21 | 0.65 | S 불확실 | 10% | 74% | 20% | 84% |
| ATTACK_POTION | 81% | 1273 / 21 | 0.54 | S 불확실 | 10% | 85% | 12% | 66% |
| SWIFT_POTION | 81% | 1227 / 21 | 0.59 | S 불확실 | 11% | 81% | 14% | 67% |
| POISON_POTION | 81% | 337 / 17 | 0.605 | S 불확실 | 9% | 77% | 20% | 82% |
| BLESSING_OF_THE_FORGE | 81% | 456 / 20 | 0.605 | S 불확실 | 6% | 73% | 20% | 76% |
| FYSH_OIL | 81% | 503 / 18 | 0.585 | S 불확실 | 6% | 78% | 18% | 86% |
| FLEX_POTION | 80% | 1184 / 20 | 0.565 | S 불확실 | 5% | 63% | 28% | 64% |
| WEAK_POTION | 80% | 1164 / 22 | 0.49 | S 불확실 | 9% | 78% | 18% | 75% |
| BOTTLED_POTENTIAL | 80% | 209 / 17 | 0.5 | A 불확실 | 5% | 69% | 26% | 68% |
| STRENGTH_POTION | 80% | 1260 / 21 | 0.49 | A 불확실 | 9% | 70% | 24% | 82% |
| POTION_OF_BINDING | 80% | 473 / 20 | 0.555 | A 불확실 | 5% | 79% | 19% | 67% |
| FIRE_POTION | 79% | 1197 / 20 | 0.57 | A 불확실 | 7% | 73% | 20% | 53% |
| GHOST_IN_A_JAR | 79% | 46 / 13 | 0.42 | A 불확실 | 20% | 86% | 6% | 85% |
| LIQUID_BRONZE | 79% | 475 / 21 | 0.585 | A 불확실 | 3% | 74% | 21% | 77% |
| VULNERABLE_POTION | 78% | 1281 / 21 | 0.71 | A 불확실 | 6% | 68% | 24% | 76% |
| CLARITY | 78% | 431 / 19 | 0.715 | A 불확실 | 6% | 76% | 22% | 72% |
| SPEED_POTION | 76% | 1186 / 21 | 0.895 | A | 4% | 58% | 32% | 61% |
| EXPLOSIVE_AMPOULE | 75% | 1205 / 22 | 0.91 | A | 5% | 68% | 25% | 50% |
| SNECKO_OIL | 74% | 178 / 17 | 0.93 | A | 3% | 81% | 19% | 59% |
| STABLE_SERUM | 71% | 473 / 19 | 0.98 | A | 2% | 51% | 36% | 79% |
| ASHWATER | 69% | 93 / 17 | 0.97 | A | 5% | 37% | 53% | 93% |

런당 포션 사용(good): 1막 승리 2.531 / 패배 2.728, 2막 승리 2.915 / 패배 3.264, 3막 승리 3.946 / 패배 3.834 (패배는 그 막에서 죽은 런 포함이라 막 길이가 짧을 수 있음).

## 5. 불확실로 남긴 것

- 이벤트 선택지 25개, 고대 선택지 26개는 방향 유지가 80% 미만(대부분 중립 근처, 예: 두 선택지 이벤트의 45~55%)이거나 표본이 부족해, 유물 149개와 포션 27종은 등급(고대 유물은 방향) 기준을 못 넘어 팩 값을 쓰지 않습니다(현재 공식·희귀도 값 유지).
- 이벤트 효과 종류별 가중치(EventWeights: 골드 0.15, 최대 체력 등)는 선택지별 효과 목록(events.json)이 이 작업 공간에 없어 직접 다시 맞추지 않았습니다. 대신 선택지 키별 선택률을 공식과 섞는 방식을 제안합니다(LOADER_PROPOSAL.md).
