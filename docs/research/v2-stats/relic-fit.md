# 빌드별 유물 적합도 근거 (relic-fit.json)

OP.GG 주간 상위 100에 오른 플레이어들의 A10 바닐라 솔로 기록(v0.107 이상)에서, 각 빌드로 기울어 있을 때 상위 플레이어가 어떤 유물을 더(또는 덜) 고르는지 잰 값입니다. 플레이어 이름이나 ID는 넣지 않았고, 런은 `https://op.gg/slay-the-spire2/history/<id>`로만 인용합니다.

## 방법

- **결정 단위**: 상점의 유물 칸 하나(산 것 = 선택), 고대 선택지의 유물 하나(고른 것 = 선택). 엘리트·보물·보스 유물은 하나만 주어져 고르는 결정이 아니라 뺐습니다.
- **그 시점의 덱**: 최종 덱에서 획득·제거·변환을 거꾸로 되돌려 그 층에 들어가기 직전의 덱과 유물을 복원했습니다.
- **빌드 안(in)**: 그 시점 덱의 commit(DATA_SCHEMA §5.1 공식, 현재 아키타입 파일의 역할·핵심 유물·fullWeight)이 0.4(`corePriority.leanFull`) 이상이고 캐릭터의 1위 빌드와 0.15(`hybridMargin`) 이내. 처음 요청대로 commitThreshold(0.7)로 나누면 결정 시점에 0.7을 넘는 덱이 적어 사일런트 단도 외에는 유물당 표본이 10~20회에 그쳤습니다.
- **비교군(out)**: 같은 캐릭터에서 **다른 빌드**로 기운 결정. 빌드가 아직 없는 초반 결정과 비교하면 "런 후반이라 골드가 많다" 같은 시점 차이가 섞여서 제외했습니다. 비교군 비율은 빌드 안 결정의 막 분포에 맞춰 막별로 가중했습니다.
- **fitRatio**: (그 유물의 in 선택률 ÷ out 선택률) ÷ (같은 신호(상점/고대)의 모든 유물의 in ÷ out). 빌드에 따라 유물을 전반적으로 더 사는 경향을 빼고 그 유물만의 차이를 봅니다. 1보다 크면 그 빌드에서 더 고르는 유물입니다.
- **use(적용)**: 빌드 안 결정 30회 이상, 유효 표본(eff_n) 20 이상, fitRatio 1.5 이상 또는 0.67 이하, 플레이어 200회 재표본 중 80% 이상에서 방향(1보다 큼/작음) 유지. 비율은 플레이어 균형(한 사람 비중 10% 상한). 좋은 플레이어(승리+패배)로 먼저 보고, 표본이 얇을 때만 전체 플레이어로 판정합니다(`basis`).
- **presenceLift**: 승리한 런의 최종 유물에서, 그 빌드 멤버 덱(최종 commit ≥ commitThreshold, 그 유물의 핵심 유물 가중치는 빼고 계산)이 그 유물을 가진 비율 ÷ 다른 승리 덱의 비율. 참고값입니다.
- **pickRateCoreHeld / pickRateNoCore**: 그 빌드로 기운 결정을 core 보유(coreTarget 충족) 여부로 나눈 선택률. core가 없는 빌드는 비어 있습니다.

## 주의

- 빌드 24개 × 유물 약 150개를 한꺼번에 시험했고 방향 안정 기준이 80%라, `use: true` 중 일부는 우연일 수 있습니다. fitRatio는 큰 가산점보다 완만한 배수로 쓰는 편이 안전합니다.
- 표본이 큰 사일런트 단도(결정이 많음)에 `use: true`가 몰려 있습니다. 디펙트·네크로바인더·리젠트는 빌드별 결정이 적어 거의 모두 `use: false`입니다.
- 상점 구매는 골드에 묶여 있어 선택률의 절대값보다 비율(fitRatio)로 읽어야 합니다.

## 적용(use: true) 목록

| 빌드 | 유물 | 신호 | 판정 | in 선택률 | out 선택률 | fitRatio | in 결정 | 플레이어 | 부트스트랩 | presenceLift | 예시 런 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| ironclad.self_damage | BRONZE_SCALES | 상점 | 전체 | 19% | 32% | 0.66 | 35 | 17 | 83% | 0.871 |  |
| ironclad.vulnerable | RED_MASK | 상점 | 전체 | 54% | 37% | 1.77 | 32 | 20 | 100% | 0.926 | [3977656](https://op.gg/slay-the-spire2/history/3977656) [4061960](https://op.gg/slay-the-spire2/history/4061960) |
| ironclad.vulnerable | CHEMICAL_X | 상점 | 전체 | 22% | 41% | 0.66 | 32 | 19 | 94% | 1.93 |  |
| ironclad.vulnerable | THE_ABACUS | 상점 | 전체 | 14% | 31% | 0.54 | 31 | 17 | 87% | 0.79 |  |
| ironclad.vulnerable | BLOOD_VIAL | 상점 | 전체 | 12% | 33% | 0.43 | 30 | 16 | 94% | 0.893 |  |
| silent.poison | SNECKO_SKULL | 상점 | 전체 | 72% | 31% | 2.17 | 38 | 17 | 100% | 1.565 | [4373572](https://op.gg/slay-the-spire2/history/4373572) [4619712](https://op.gg/slay-the-spire2/history/4619712) |
| silent.poison | BRONZE_SCALES | 상점 | 전체 | 28% | 41% | 0.64 | 31 | 19 | 98% | 0.963 |  |
| silent.poison | NINJA_SCROLL | 상점 | 전체 | 12% | 21% | 0.51 | 31 | 16 | 96% | 0.79 |  |
| silent.poison | TOASTY_MITTENS | 고대 | 전체 | 14% | 33% | 0.43 | 30 | 16 | 98% | 0.373 |  |
| silent.poison | GNARLED_HAMMER | 상점 | 전체 | 12% | 26% | 0.43 | 35 | 20 | 98% | 0.481 |  |
| silent.poison | TOUCH_OF_OROBAS | 고대 | 좋은 | 15% | 36% | 0.42 | 30 | 9 | 100% | 0.322 |  |
| silent.poison | VAJRA | 상점 | 전체 | 21% | 55% | 0.35 | 38 | 19 | 100% | 0.829 |  |
| silent.shiv | PEAR | 상점 | 전체 | 45% | 15% | 3.26 | 49 | 20 | 95% | 1.252 | [3838192](https://op.gg/slay-the-spire2/history/3838192) [4406658](https://op.gg/slay-the-spire2/history/4406658) |
| silent.shiv | DELICATE_FROND | 고대 | 좋은 | 38% | 12% | 3.08 | 37 | 10 | 95% | 1.039 | [4707679](https://op.gg/slay-the-spire2/history/4707679) [5414823](https://op.gg/slay-the-spire2/history/5414823) |
| silent.shiv | REPTILE_TRINKET | 상점 | 전체 | 29% | 12% | 2.61 | 55 | 24 | 95% | 1.484 | [5847033](https://op.gg/slay-the-spire2/history/5847033) [6289066](https://op.gg/slay-the-spire2/history/6289066) |
| silent.shiv | CALLING_BELL | 고대 | 전체 | 47% | 19% | 2.49 | 42 | 21 | 94% | 2.864 | [3614760](https://op.gg/slay-the-spire2/history/3614760) [3675724](https://op.gg/slay-the-spire2/history/3675724) |
| silent.shiv | MUSIC_BOX | 고대 | 전체 | 39% | 16% | 2.35 | 57 | 21 | 96% | 3.308 | [4277526](https://op.gg/slay-the-spire2/history/4277526) [4571722](https://op.gg/slay-the-spire2/history/4571722) |
| silent.shiv | PETRIFIED_TOAD | 상점 | 좋은 | 34% | 13% | 2.28 | 31 | 12 | 80% | 1.075 | [5424522](https://op.gg/slay-the-spire2/history/5424522) [5870720](https://op.gg/slay-the-spire2/history/5870720) |
| silent.shiv | ORNAMENTAL_FAN | 상점 | 전체 | 45% | 22% | 2.21 | 50 | 23 | 99% | 1.254 | [3951734](https://op.gg/slay-the-spire2/history/3951734) [4187456](https://op.gg/slay-the-spire2/history/4187456) |
| silent.shiv | BLESSED_ANTLER | 고대 | 좋은 | 32% | 15% | 2.11 | 36 | 10 | 96% | 0.998 | [4211850](https://op.gg/slay-the-spire2/history/4211850) [4598724](https://op.gg/slay-the-spire2/history/4598724) |
| silent.shiv | JOSS_PAPER | 상점 | 전체 | 44% | 23% | 2.05 | 48 | 23 | 95% | 1.394 | [4279151](https://op.gg/slay-the-spire2/history/4279151) [4797892](https://op.gg/slay-the-spire2/history/4797892) |
| silent.shiv | BLACK_STAR | 고대 | 전체 | 44% | 22% | 2.04 | 30 | 19 | 90% | 2.112 | [3597924](https://op.gg/slay-the-spire2/history/3597924) [4565365](https://op.gg/slay-the-spire2/history/4565365) |
| silent.shiv | SAND_CASTLE | 고대 | 전체 | 43% | 24% | 1.84 | 37 | 17 | 97% | 0.659 | [4406658](https://op.gg/slay-the-spire2/history/4406658) [4890120](https://op.gg/slay-the-spire2/history/4890120) |
| silent.shiv | KUSARIGAMA | 상점 | 전체 | 45% | 27% | 1.81 | 47 | 22 | 95% | 1.645 | [4187456](https://op.gg/slay-the-spire2/history/4187456) [4339601](https://op.gg/slay-the-spire2/history/4339601) |
| silent.shiv | PARRYING_SHIELD | 상점 | 좋은 | 49% | 25% | 1.72 | 44 | 13 | 90% | 1.271 | [3985583](https://op.gg/slay-the-spire2/history/3985583) [4157096](https://op.gg/slay-the-spire2/history/4157096) |
| silent.shiv | RAINBOW_RING | 상점 | 전체 | 62% | 40% | 1.68 | 30 | 18 | 88% | 1.62 | [3724911](https://op.gg/slay-the-spire2/history/3724911) [4110994](https://op.gg/slay-the-spire2/history/4110994) |
| silent.shiv | GHOST_SEED | 상점 | 좋은 | 50% | 26% | 1.63 | 61 | 15 | 94% | 1.082 | [3724911](https://op.gg/slay-the-spire2/history/3724911) [3724929](https://op.gg/slay-the-spire2/history/3724929) |
| silent.shiv | SHURIKEN | 상점 | 전체 | 62% | 41% | 1.62 | 31 | 23 | 80% | 2.011 | [3728934](https://op.gg/slay-the-spire2/history/3728934) [3951734](https://op.gg/slay-the-spire2/history/3951734) |
| silent.shiv | ASTROLABE | 고대 | 전체 | 57% | 36% | 1.59 | 40 | 27 | 96% | 2.584 | [4111000](https://op.gg/slay-the-spire2/history/4111000) [4406658](https://op.gg/slay-the-spire2/history/4406658) |
| silent.shiv | CANDELABRA | 상점 | 좋은 | 54% | 30% | 1.58 | 32 | 11 | 80% | 1.832 | [3614760](https://op.gg/slay-the-spire2/history/3614760) [4300481](https://op.gg/slay-the-spire2/history/4300481) |
| silent.shiv | TOASTY_MITTENS | 고대 | 좋은 | 31% | 20% | 1.57 | 38 | 13 | 96% | 6.242 | [4856481](https://op.gg/slay-the-spire2/history/4856481) [5283305](https://op.gg/slay-the-spire2/history/5283305) |
| silent.shiv | BURNING_STICKS | 상점 | 좋은 | 42% | 23% | 1.56 | 48 | 12 | 88% | 1.839 | [3951734](https://op.gg/slay-the-spire2/history/3951734) [4533777](https://op.gg/slay-the-spire2/history/4533777) |
| silent.shiv | MERCURY_HOURGLASS | 상점 | 좋은 | 28% | 16% | 1.52 | 36 | 13 | 87% | 1.671 | [4995367](https://op.gg/slay-the-spire2/history/4995367) [5870720](https://op.gg/slay-the-spire2/history/5870720) |
| silent.shiv | ARCHAIC_TOOTH | 고대 | 좋은 | 47% | 31% | 1.52 | 49 | 13 | 99% | 0.992 | [4533777](https://op.gg/slay-the-spire2/history/4533777) [4565365](https://op.gg/slay-the-spire2/history/4565365) |
| silent.shiv | VAJRA | 상점 | 좋은 | 54% | 30% | 1.52 | 47 | 15 | 98% | 1.15 | [3724929](https://op.gg/slay-the-spire2/history/3724929) [4157096](https://op.gg/slay-the-spire2/history/4157096) |
| silent.shiv | NUNCHAKU | 상점 | 전체 | 46% | 33% | 1.50 | 49 | 22 | 84% | 1.779 | [4110999](https://op.gg/slay-the-spire2/history/4110999) [4790142](https://op.gg/slay-the-spire2/history/4790142) |
| silent.shiv | PAELS_LEGION | 고대 | 전체 | 21% | 32% | 0.67 | 62 | 24 | 85% | 0.415 |  |
| silent.shiv | GREMLIN_HORN | 상점 | 전체 | 27% | 45% | 0.66 | 52 | 28 | 88% | 1.279 |  |
| silent.shiv | LOOMING_FRUIT | 고대 | 좋은 | 19% | 29% | 0.66 | 33 | 9 | 86% | 1.264 |  |
| silent.shiv | MINIATURE_CANNON | 상점 | 전체 | 27% | 45% | 0.65 | 42 | 16 | 91% | 0.694 |  |
| silent.shiv | CHEMICAL_X | 상점 | 좋은 | 21% | 28% | 0.64 | 41 | 14 | 95% | 0.934 |  |
| silent.shiv | JEWELRY_BOX | 고대 | 좋은 | 34% | 53% | 0.64 | 36 | 10 | 86% | 1.09 |  |
| silent.shiv | VERY_HOT_COCOA | 고대 | 좋은 | 54% | 85% | 0.64 | 38 | 11 | 100% | 0.493 |  |
| silent.shiv | BAG_OF_PREPARATION | 상점 | 좋은 | 45% | 63% | 0.61 | 58 | 12 | 99% | 0.946 |  |
| silent.shiv | SNECKO_SKULL | 상점 | 좋은 | 49% | 70% | 0.61 | 55 | 12 | 98% | 0.903 |  |
| silent.shiv | MEAL_TICKET | 상점 | 좋은 | 29% | 42% | 0.60 | 38 | 14 | 93% | 1.115 |  |
| silent.shiv | RUNIC_PYRAMID | 고대 | 전체 | 43% | 71% | 0.60 | 40 | 22 | 96% | 0.662 |  |
| silent.shiv | PLANISPHERE | 상점 | 전체 | 15% | 27% | 0.59 | 37 | 19 | 80% | 0.991 |  |
| silent.shiv | TWISTED_FUNNEL | 상점 | 전체 | 23% | 43% | 0.58 | 49 | 22 | 86% | 0.921 |  |
| silent.shiv | CHOICES_PARADOX | 고대 | 전체 | 22% | 38% | 0.56 | 42 | 22 | 88% | 0.438 |  |
| silent.shiv | ELECTRIC_SHRYMP | 고대 | 좋은 | 39% | 71% | 0.55 | 34 | 12 | 99% | 0.242 |  |
| silent.shiv | EMPTY_CAGE | 고대 | 전체 | 41% | 76% | 0.54 | 36 | 17 | 98% | 0.502 |  |
| silent.shiv | DINGY_RUG | 상점 | 좋은 | 17% | 27% | 0.52 | 47 | 12 | 92% | 0.461 |  |
| silent.shiv | CLAWS | 고대 | 좋은 | 18% | 37% | 0.49 | 36 | 11 | 88% | 0.709 |  |
| silent.shiv | ORRERY | 상점 | 좋은 | 35% | 63% | 0.48 | 49 | 13 | 100% | 0.655 |  |
| silent.shiv | BRILLIANT_SCARF | 고대 | 좋은 | 23% | 51% | 0.44 | 34 | 11 | 96% | 1.313 |  |
| silent.shiv | GORGET | 상점 | 좋은 | 28% | 56% | 0.43 | 49 | 15 | 100% | 1.17 |  |
| silent.shiv | BEAUTIFUL_BRACELET | 고대 | 좋은 | 13% | 30% | 0.42 | 44 | 11 | 92% | 0.661 |  |
| silent.shiv | GLASS_EYE | 고대 | 좋은 | 23% | 59% | 0.40 | 36 | 12 | 96% | 1.568 |  |
| silent.shiv | SPIKED_GAUNTLETS | 고대 | 좋은 | 4% | 13% | 0.35 | 36 | 14 | 80% | 0.501 |  |
| silent.shiv | TOY_BOX | 고대 | 전체 | 6% | 24% | 0.27 | 40 | 19 | 94% | 0.431 |  |
| silent.sly_discard | RINGING_TRIANGLE | 상점 | 전체 | 47% | 21% | 1.89 | 31 | 16 | 99% | 2.509 | [4025960](https://op.gg/slay-the-spire2/history/4025960) [4312944](https://op.gg/slay-the-spire2/history/4312944) |
| silent.sly_discard | BREAD | 상점 | 전체 | 32% | 15% | 1.85 | 35 | 19 | 92% | 3.948 | [4073381](https://op.gg/slay-the-spire2/history/4073381) [4900052](https://op.gg/slay-the-spire2/history/4900052) |
| silent.sly_discard | ROYAL_STAMP | 상점 | 전체 | 59% | 27% | 1.85 | 36 | 18 | 98% | 2.305 | [3724911](https://op.gg/slay-the-spire2/history/3724911) [4110994](https://op.gg/slay-the-spire2/history/4110994) |
| silent.sly_discard | SCREAMING_FLAGON | 상점 | 전체 | 24% | 12% | 1.63 | 32 | 19 | 83% | 0.845 | [4957562](https://op.gg/slay-the-spire2/history/4957562) [6253646](https://op.gg/slay-the-spire2/history/6253646) |
| silent.sly_discard | TOUCH_OF_OROBAS | 고대 | 전체 | 35% | 22% | 1.60 | 32 | 13 | 84% | 1.056 | [4950070](https://op.gg/slay-the-spire2/history/4950070) [6234531](https://op.gg/slay-the-spire2/history/6234531) |
| silent.sly_discard | WAR_PAINT | 상점 | 전체 | 30% | 42% | 0.61 | 41 | 17 | 99% | 0.94 |  |
| silent.sly_discard | STRAWBERRY | 상점 | 전체 | 25% | 35% | 0.60 | 36 | 15 | 94% | 0.912 |  |
| necrobinder.doom | RED_MASK | 상점 | 전체 | 24% | 51% | 0.49 | 31 | 14 | 99% | 1.007 |  |
| necrobinder.doom | BONE_FLUTE | 상점 | 좋은 | 18% | 48% | 0.46 | 30 | 7 | 96% | 0.558 |  |
| regent.stars | RED_MASK | 상점 | 전체 | 28% | 53% | 0.43 | 35 | 13 | 87% | 1.117 |  |

## 빌드별 개수

| 빌드 | 시험한 유물 | use: true |
|---|---|---|
| ironclad.strength | 7 | 0 |
| ironclad.exhaust | 116 | 0 |
| ironclad.block | 133 | 0 |
| ironclad.self_damage | 134 | 1 |
| ironclad.vulnerable | 148 | 4 |
| ironclad.attack_replay | 8 | 0 |
| ironclad.strike_tempo | 125 | 0 |
| ironclad.hellraiser | 140 | 0 |
| silent.poison | 154 | 7 |
| silent.shiv | 189 | 49 |
| silent.sly_discard | 161 | 7 |
| defect.orb_focus | 113 | 0 |
| defect.lightning | 115 | 0 |
| defect.status | 100 | 0 |
| defect.zero_cost | 75 | 0 |
| defect.dark_orbs | 26 | 0 |
| defect.orb_variety | 41 | 0 |
| necrobinder.osty | 110 | 0 |
| necrobinder.doom | 148 | 2 |
| necrobinder.ethereal | 107 | 0 |
| regent.stars | 137 | 1 |
| regent.star_spend | 37 | 0 |
| regent.forge | 71 | 0 |
| regent.creation | 95 | 0 |
