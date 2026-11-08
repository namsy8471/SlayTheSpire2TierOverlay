# 사일런트 빌드 카드 근거

`data/packs/v0.107.1/archetypes/silent.json`의 카드 목록이 어디서 왔는지 정리한 문서입니다. 모든 카드는 상위 플레이어가 실제로 이긴 덱에서 뽑았고, 직접 판단이나 익명 커뮤니티 글은 출처로 쓰지 않았습니다.

## 출처

1. **주 출처: OP.GG** 사일런트 주간 최단 승리 상위 100(2026-11-02~08)과, 목록에 오른 모든 플레이어의 최근 승리. 바닐라, A10, 싱글만. v0.107.1 46판 + v0.111.0 48판 = **94판·41명**. 그 이전 빌드(v0.103.2·v0.105.1) 7판은 뺐습니다. 런은 `https://op.gg/slay-the-spire2/history/<runId>` 형식으로 인용합니다.
   - **OP.GG 랭킹 1위 플레이어**(솔로 787판·승률 64.4%·최고 연승 44)는 사일런트 13승, 전부 v0.107.1입니다. 표의 "1위 플레이어 덱" 열은 그의 멤버 덱 중 그 카드를 든 덱 수입니다.
2. **보조 출처: Baalorlord** A10 사일런트 승리 덱 5개(최고 연승 4, 2026-10-14~11-05): https://baalorlord.tv/runs/1792001030 , https://baalorlord.tv/runs/1793132909 , https://baalorlord.tv/runs/1793816244 , https://baalorlord.tv/runs/1793821221 , https://baalorlord.tv/runs/1793902215 . 그의 글 "In Defense of 2-Cost Cards"(https://sts2.untapped.gg/articles/in-defense-of-2-cost-cards)도 참고했습니다. 표의 "Baalorlord" 열은 5덱 중 그 카드를 든 덱 수입니다.
3. **참고만: Spire Codex A10 Lift.** 이 문서의 어떤 판정도 이것만으로 정하지 않았습니다.

베타에서 바뀐 카드(†, `changed_in_beta`: ACCELERANT, BLADE_OF_INK, ECHOING_SLASH, EXPERTISE, FLICK_FLACK, HAZE, MIRAGE, OUTBREAK, SCARE, TRACKING, WELL_LAID_PLANS 등)는 v0.107.1 판 46개만으로 계산했습니다. 나머지 카드는 두 버전을 합쳤습니다.

## 방법

- **덱**: 최종 덱의 서로 다른 카드 집합. 강화·인챈트는 같은 카드로, 시작·저주·상태이상·토큰·퀘스트 카드는 제외.
- **빌드 멤버십(누락 보정)**: 축(POISON·SHIV·DISCARD)별로 티어 태그가 그 축을 공급·소비·요구하는 사일런트 카드를 후보로 두고, 남은 후보 기준 lift가 2.0 미만인 카드를 하나씩 빼서 "시그니처 카드"를 정했습니다. 카드 X를 평가할 때는 **X를 뺀 시그니처 카드 2장 이상**을 든 덱을 멤버로 봅니다(자기 점유율을 스스로 올리지 못하게).
  - 중독: ACCELERANT, BOUNCING_FLASK, BUBBLE_BUBBLE, CORROSIVE_WAVE, DEADLY_POISON, MIRAGE, NOXIOUS_FUMES, OUTBREAK, POISONED_STAB, SNAKEBITE
  - 단도: ACCURACY, BLADE_DANCE, BLADE_OF_INK, CLOAK_AND_DAGGER, FAN_OF_KNIVES, FINISHER, HIDDEN_DAGGERS, INFINITE_BLADES, KNIFE_TRAP, LEADING_STRIKE, PHANTOM_BLADES, UP_MY_SLEEVE
  - 교활·버리기: DAGGER_THROW, FLICK_FLACK, HAND_TRICK, HAZE, MASTER_PLANNER, MEMENTO_MORI, PREPARED, REFLEX, RICOCHET, SHADOW_STEP, STORM_OF_STEEL, TACTICIAN, UNTOUCHABLE
- **점유율** = 멤버 덱 중 그 카드를 든 비율, **lift** = 점유율 ÷ 비멤버 덱 점유율(비멤버 0%면 ∞), **빌드 내 픽/제시** = 멤버 덱 런의 카드 보상 화면(일반·엘리트·보스·미지)에서 집은 횟수/나온 횟수. 상점은 픽 표시가 없어 뺐습니다. 전체 평균 픽률은 21%입니다.
- **빌드 인정**: 멤버 덱 ≥6판, ≥4명, 한 사람이 절반 이하.
- **카드 인정**: ≥3명, lift ≥1.5, 그리고 점유율 ≥25% 또는 (점유율 ≥15%이고 빌드 내 ≥5번 제시 중 ≥50% 픽). lift 1.5~1.6은 **경계**로 표시했습니다.
- **보조 출처 구제**: OP.GG 기준에 못 미친 카드라도 Baalorlord 사일런트 덱의 ≥50%(3/5 이상)에 있고, 카드 자체가 그 빌드의 축에 작용하며, OP.GG lift ≥1.0이면 core가 아닌 역할로 넣을 수 있습니다.
- **core**: 인정된 고급·희귀 카드 중 그 빌드의 엔진(축을 공급하거나 키우는 파워)이고 점유율 ≥40%·≥4명. 최대 3장. 일반·고대·이벤트 카드는 core 불가. 없으면 비웁니다.
- **나머지 역할**: 축 공급 = enabler, 축 소비·마무리 = payoff, 그 밖에 인정된 카드 = support. 범용 카드라도 lift 기준을 통과하면 support로 넣었습니다.

## silent.poison (중독)

- 표본: 멤버 덱 **31판·22명**(v0.107.1 18 / v0.111.0 13), 한 사람 최대 19%, 랭킹 1위 플레이어 6판. 빌드 인정.
- core: NOXIOUS_FUMES(이전 core 유지), **ACCELERANT†**(payoff → core). BOUNCING_FLASK는 core → enabler(36%, 기준 40% 미달).
- 두 core를 함께 든 덱 5/31이라 `coreTarget`은 기본값 1. `fullWeight` 4.0 유지(멤버 commit 중앙값 1.00→1.00, 비멤버 0.38→0.18).
- `keyRelics`: SNECKO_SKULL(멤버 13%, lift 1.4, 3명), TWISTED_FUNNEL(26%, lift 2.3, 8명) 유지.

| 카드 | 역할 | 점유율(멤버) | 비멤버 | lift | 플레이어 | 빌드 내 픽/제시 | 1위 플레이어 덱 | Baalorlord | 통과 기준 |
|---|---|---|---|---|---|---|---|---|---|
| NOXIOUS_FUMES | core | 52% (14/27) | 19% | 2.7 | 13 | 12/29 | 2/6 | 2/5 | 점유율≥25% |
| ACCELERANT† | core | 41% (7/17) | 3% | 11.9 | 4 | 5/10 | 4/6 | 1/5 | 점유율≥25% / 15%+픽≥50% |
| SNAKEBITE | enabler | 40% (10/25) | 13% | 3.1 | 9 | 12/44 | 2/5 | 3/5 | 점유율≥25% |
| BOUNCING_FLASK | enabler | 36% (10/28) | 5% | 7.9 | 10 | 10/25 | 1/6 | 1/5 | 점유율≥25% |
| DEADLY_POISON | enabler | 29% (9/31) | 6% | 4.6 | 8 | 12/67 | 2/6 | 0/5 | 점유율≥25% |
| MIRAGE† | payoff | 33% (6/18) | 0% | ∞ | 5 | 6/15 | 2/6 | 0/5 | 점유율≥25% |
| BUBBLE_BUBBLE | payoff | 26% (8/31) | 0% | ∞ | 8 | 5/21 | 1/6 | 1/5 | 점유율≥25% |
| BLUR | support | 45% (14/31) | 22% | 2.0 | 10 | 17/33 | 4/6 | 3/5 | 점유율≥25% / 15%+픽≥50% |
| LEG_SWEEP | support | 42% (13/31) | 21% | 2.0 | 12 | 14/29 | 1/6 | 2/5 | 점유율≥25% |
| REFLEX | support | 26% (8/31) | 16% | 1.63 | 6 | 6/19 | 3/6 | 0/5 | 점유율≥25% |
| UNTOUCHABLE | support | 26% (8/31) | 13% | 2.0 | 8 | 7/62 | 1/6 | 0/5 | 점유율≥25% |
| TRACKING† | support | 22% (4/18) | 14% | 1.56 | 3 | 4/7 | 2/6 | 0/5 | 15%+픽≥50% (경계: lift 1.5~1.6) |

대표 승리 덱: https://op.gg/slay-the-spire2/history/7306479 (v0.107.1, 두 core 모두), https://op.gg/slay-the-spire2/history/7176716 (v0.111.0, NOXIOUS_FUMES·BOUNCING_FLASK·SNAKEBITE), https://op.gg/slay-the-spire2/history/7279795 (v0.107.1), https://op.gg/slay-the-spire2/history/7085517 (랭킹 1위 플레이어, 두 core 모두). Baalorlord 중독 덱: https://baalorlord.tv/runs/1792001030 , https://baalorlord.tv/runs/1793816244 (둘 다 NOXIOUS_FUMES·SNAKEBITE).

제거한 카드:

| 카드 | 이전 역할 | 점유율(멤버) | 비멤버 | lift | 플레이어 | 빌드 내 픽/제시 | 1위 플레이어 덱 | Baalorlord | 통과 기준 |
|---|---|---|---|---|---|---|---|---|---|
| POISONED_STAB | enabler | 17% (5/29) | 6% | 2.8 | 5 | 5/67 | 1/6 | 1/5 | 미달: 점유율<25%·픽<50% |
| HAZE† | enabler | 17% (3/18) | 11% | 1.56 | 2 | 2/15 | 2/6 | 1/5 | 미달: 플레이어<3, 점유율<25%·픽<50% |
| CORROSIVE_WAVE | enabler | 13% (4/30) | 5% | 2.8 | 3 | 3/12 | 2/6 | 0/5 | 미달: 점유율<15% |
| ENVENOM | enabler | 23% (7/31) | 13% | 1.8 | 6 | 5/12 | 1/6 | 1/5 | 미달: 점유율<25%·픽<50% |
| OUTBREAK† | payoff | 20% (3/15) | 10% | 2.1 | 3 | 1/10 | 1/5 | 1/5 | 미달: 점유율<25%·픽<50% |
| MALAISE | support | 13% (4/31) | 8% | 1.63 | 3 | 6/10 | 2/6 | 1/5 | 미달: 점유율<15% |
| PIERCING_WAIL | support | 48% (15/31) | 46% | 1.1 | 13 | 17/64 | 2/6 | 4/5 | 미달: lift<1.5 |
| FOOTWORK | support | 58% (18/31) | 71% | 0.8 | 15 | 16/21 | 4/6 | 3/5 | 미달: lift<1.5 |

- PIERCING_WAIL(Baalorlord 4/5)·FOOTWORK(3/5)은 보조 출처 50% 조건은 넘지만 카드 자체가 중독 축에 작용하지 않고, OP.GG lift도 1.1·0.8이라 구제하지 않았습니다. 둘 다 빌드와 상관없이 거의 모든 덱에 들어가는 범용 카드입니다.
- Baalorlord 중독 덱 2개에 모두 있는 BURST는 그의 전체 사일런트 덱 기준 2/5(40%)라 구제 조건(≥50%) 미달이고, OP.GG 중독 멤버 덱에서도 10%(3/31)입니다.

## silent.shiv (단도)

- 표본: 멤버 덱 **73판·34명**(v0.107.1 33 / v0.111.0 40), 한 사람 최대 12%, 랭킹 1위 플레이어 9판. 사일런트 최다 빌드.
- core: ACCURACY, INFINITE_BLADES(이전 core 둘 다 유지). 둘 다 비멤버 덱에는 한 번도 없습니다.
- 두 core를 함께 든 덱 22/73(30%)이라 `coreTarget` 기본값 1. `fullWeight` 4.0 유지(멤버 중앙값 1.00→1.00, 비멤버 0.13→0.19).
- `keyRelics`: HELICAL_DART 삭제(멤버 1판·1명, lift 0.3). NINJA_SCROLL(8%, 비멤버 0%, 6명), KUNAI(11%, lift 2.3, 8명), SHURIKEN(10%, lift 2.0, 7명), ORNAMENTAL_FAN(33%, lift 2.3, 19명) 유지.

| 카드 | 역할 | 점유율(멤버) | 비멤버 | lift | 플레이어 | 빌드 내 픽/제시 | 1위 플레이어 덱 | Baalorlord | 통과 기준 |
|---|---|---|---|---|---|---|---|---|---|
| ACCURACY | core | 64% (47/73) | 0% | ∞ | 25 | 34/50 | 5/9 | 2/5 | 점유율≥25% / 15%+픽≥50% |
| INFINITE_BLADES | core | 48% (35/73) | 0% | ∞ | 21 | 23/52 | 4/9 | 1/5 | 점유율≥25% |
| LEADING_STRIKE | enabler | 75% (52/69) | 24% | 3.1 | 27 | 62/157 | 5/8 | 2/5 | 점유율≥25% |
| BLADE_DANCE | enabler | 68% (48/71) | 9% | 7.8 | 23 | 63/165 | 6/8 | 1/5 | 점유율≥25% |
| CLOAK_AND_DAGGER | enabler | 66% (48/73) | 10% | 6.9 | 28 | 65/156 | 2/9 | 2/5 | 점유율≥25% |
| HIDDEN_DAGGERS | enabler | 56% (40/71) | 13% | 4.3 | 25 | 34/46 | 5/9 | 0/5 | 점유율≥25% / 15%+픽≥50% |
| BLADE_OF_INK† | enabler | 36% (12/33) | 8% | 4.7 | 7 | 10/15 | 2/9 | 0/5 | 점유율≥25% / 15%+픽≥50% |
| PHANTOM_BLADES | payoff | 40% (29/73) | 0% | ∞ | 23 | 25/51 | 1/9 | 1/5 | 점유율≥25% |
| KNIFE_TRAP | payoff | 29% (21/73) | 0% | ∞ | 15 | 18/22 | 2/9 | 0/5 | 점유율≥25% / 15%+픽≥50% |
| FAN_OF_KNIVES | payoff | 27% (20/73) | 0% | ∞ | 18 | 20/25 | 2/9 | 0/5 | 점유율≥25% / 15%+픽≥50% |
| ENVENOM | payoff | 18% (13/73) | 10% | 1.9 | 10 | 12/22 | 2/9 | 1/5 | 15%+픽≥50% |
| EXPOSE | support | 55% (40/73) | 19% | 2.9 | 25 | 38/58 | 4/9 | 0/5 | 점유율≥25% / 15%+픽≥50% |
| DEFLECT | support | 44% (32/73) | 29% | 1.53 | 16 | 39/149 | 5/9 | 0/5 | 점유율≥25% (경계: lift 1.5~1.6) |
| AFTERIMAGE | support | 38% (28/73) | 5% | 8.1 | 18 | 26/33 | 4/9 | 1/5 | 점유율≥25% / 15%+픽≥50% |
| ESCAPE_PLAN | support | 26% (19/73) | 14% | 1.8 | 14 | 17/49 | 1/9 | 0/5 | 점유율≥25% |
| TRACKING† | support | 21% (7/33) | 8% | 2.8 | 6 | 8/14 | 2/9 | 0/5 | 15%+픽≥50% |

대표 승리 덱: https://op.gg/slay-the-spire2/history/7299147 (v0.111.0, 두 core), https://op.gg/slay-the-spire2/history/7191806 (v0.107.1, 두 core), https://op.gg/slay-the-spire2/history/7135144 (v0.111.0, ACCURACY), https://op.gg/slay-the-spire2/history/7204766 (랭킹 1위 플레이어, ACCURACY). Baalorlord 단도 덱: https://baalorlord.tv/runs/1793821221 , https://baalorlord.tv/runs/1793132909 (둘 다 ACCURACY·CLOAK_AND_DAGGER).

제거한 카드:

| 카드 | 이전 역할 | 점유율(멤버) | 비멤버 | lift | 플레이어 | 빌드 내 픽/제시 | 1위 플레이어 덱 | Baalorlord | 통과 기준 |
|---|---|---|---|---|---|---|---|---|---|
| UP_MY_SLEEVE | enabler | 24.7% (18/73) | 0% | ∞ | 14 | 13/45 | 2/9 | 0/5 | 미달: 점유율<25%·픽<50% |
| STORM_OF_STEEL | enabler | 10% (7/73) | 14% | 0.7 | 7 | 5/25 | 1/9 | 0/5 | 미달: lift<1.5, 점유율<15% |
| FINISHER | payoff | 20% (14/71) | 9% | 2.3 | 10 | 12/47 | 0/9 | 0/5 | 미달: 점유율<25%·픽<50% |

- UP_MY_SLEEVE는 비멤버 덱에 없고 14명이 썼지만 점유율 24.7%(18/73)로 25%에 조금 못 미치고 빌드 내 픽률도 29%(13/45)라 기준 미달입니다.
- STORM_OF_STEEL은 단도 덱에서 오히려 드뭅니다(lift 0.7).
- 역할 변경: ENVENOM support → payoff(단도 타격을 소비하는 카드), HIDDEN_DAGGERS는 enabler 유지.
- 추가: EXPOSE, DEFLECT(경계), ESCAPE_PLAN, TRACKING†(support).

## silent.sly_discard (교활·버리기)

- 표본: 멤버 덱 **51판·27명**(v0.107.1 31 / v0.111.0 20), 한 사람 최대 22%, 랭킹 1위 플레이어 11판. 빌드 인정. 단 멤버 덱 중 단도와 겹치는 덱이 36판, 중독과 겹치는 덱이 17판으로, 대부분 다른 빌드 위에 얹힌 형태입니다.
- **core 없음.** 이전 core TOOLS_OF_THE_TRADE(12%, lift 1.0)와 ACROBATICS(39%, lift 1.3)는 교활 덱이 아닌 덱에서도 같은 비율로 쓰여 카드 기준을 통과하지 못했습니다. 기준을 통과한 카드 중 점유율 40%를 넘는 고급·희귀 카드는 TACTICIAN(44%, 12명)과 WELL_LAID_PLANS†(45%, 6명)뿐인데, TACTICIAN은 버려질 때 쓰이는 1회성 교활 카드(축 소비)라 payoff, WELL_LAID_PLANS는 버리기 축 카드가 아니라 support입니다. 엔진 역할을 하는 파워가 없으므로 core를 비웠고, 이 빌드에는 "코어 먼저" 규칙이 적용되지 않습니다.
- `guideHeroCard`: TOOLS_OF_THE_TRADE(제거됨) → PREPARED(인정된 카드 중 최다 보유, 62%).
- `fullWeight` 4.5 → **3.0**: 새 목록으로 멤버 commit 중앙값이 0.60이라 기준(0.7) 미달. 3.0에서 멤버 0.90, 비멤버 0.17(이전 목록: 0.89 / 0.38).
- `keyRelics`: TINGSHA(멤버 10%, lift 0.8), TOUGH_BANDAGES(4%, lift 0.6, 2명) 삭제. GAMBLING_CHIP(8%, lift 1.1, 3명) 유지.
- `desc`: 엔진 없이 교활 카드만 모으면 안 된다는 문장은 데이터와 맞지 않아(상위 플레이어는 싼 버리기 카드로 굴림) 고쳤습니다.

| 카드 | 역할 | 점유율(멤버) | 비멤버 | lift | 플레이어 | 빌드 내 픽/제시 | 1위 플레이어 덱 | Baalorlord | 통과 기준 |
|---|---|---|---|---|---|---|---|---|---|
| PREPARED | enabler | 62% (28/45) | 31% | 2.0 | 16 | 32/81 | 4/10 | 5/5 | 점유율≥25% |
| DAGGER_THROW | enabler | 27% (12/45) | 12% | 2.2 | 8 | 7/87 | 4/9 | 0/5 | 점유율≥25% |
| TACTICIAN | payoff | 44% (21/48) | 13% | 3.4 | 12 | 16/28 | 6/11 | 0/5 | 점유율≥25% / 15%+픽≥50% |
| RICOCHET | payoff | 31% (15/48) | 15% | 2.1 | 11 | 14/93 | 4/11 | 1/5 | 점유율≥25% |
| REFLEX | payoff | 31% (15/49) | 7% | 4.6 | 8 | 15/35 | 7/11 | 0/5 | 점유율≥25% |
| UNTOUCHABLE | payoff | 29% (14/49) | 4% | 6.4 | 10 | 14/89 | 2/11 | 0/5 | 점유율≥25% |
| ABRASIVE | payoff | 20% (10/51) | 12% | 1.69 | 7 | 11/17 | 3/11 | 0/5 | 15%+픽≥50% |
| WELL_LAID_PLANS† | support | 45% (14/31) | 27% | 1.69 | 6 | 8/28 | 8/11 | 0/5 | 점유율≥25% |
| TRACKING† | support | 23% (7/31) | 7% | 3.4 | 7 | 7/11 | 1/11 | 0/5 | 15%+픽≥50% |

대표 승리 덱: https://op.gg/slay-the-spire2/history/7169193 (v0.107.1, TACTICIAN·REFLEX·RICOCHET·ABRASIVE), https://op.gg/slay-the-spire2/history/7144897 (v0.107.1, REFLEX·RICOCHET·UNTOUCHABLE), https://op.gg/slay-the-spire2/history/7202942 (v0.111.0, TACTICIAN·RICOCHET), https://op.gg/slay-the-spire2/history/7085517 (랭킹 1위 플레이어). Baalorlord 버리기 덱: https://baalorlord.tv/runs/1793902215 .

제거한 카드:

| 카드 | 이전 역할 | 점유율(멤버) | 비멤버 | lift | 플레이어 | 빌드 내 픽/제시 | 1위 플레이어 덱 | Baalorlord | 통과 기준 |
|---|---|---|---|---|---|---|---|---|---|
| TOOLS_OF_THE_TRADE | core | 12% (6/51) | 12% | 1.0 | 5 | 6/14 | 2/11 | 0/5 | 미달: lift<1.5, 점유율<15% |
| ACROBATICS | core | 39% (20/51) | 30% | 1.3 | 13 | 21/41 | 5/11 | 0/5 | 미달: lift<1.5 |
| CALCULATED_GAMBLE | enabler | 39% (20/51) | 40% | 1.0 | 14 | 17/26 | 4/11 | 1/5 | 미달: lift<1.5 |
| HIDDEN_DAGGERS | enabler | 43% (22/51) | 49% | 0.9 | 14 | 23/33 | 5/11 | 0/5 | 미달: lift<1.5 |
| STORM_OF_STEEL | enabler | 16% (8/50) | 5% | 3.5 | 7 | 4/20 | 2/11 | 0/5 | 미달: 점유율<25%·픽<50% |
| SHADOW_STEP | enabler | 14% (7/50) | 5% | 3.1 | 5 | 4/23 | 3/11 | 0/5 | 미달: 점유율<15% |
| HAZE† | payoff | 19% (6/31) | 0% | ∞ | 4 | 4/20 | 3/11 | 1/5 | 미달: 점유율<25%·픽<50% |
| MEMENTO_MORI | payoff | 8% (4/51) | 0% | ∞ | 4 | 3/55 | 1/11 | 0/5 | 미달: 점유율<15% |
| FLICK_FLACK† | payoff | 19% (6/31) | 7% | 2.9 | 4 | 8/58 | 2/11 | 0/5 | 미달: 점유율<25%·픽<50% |
| MASTER_PLANNER | support | 6% (3/50) | 2% | 2.6 | 3 | 3/17 | 0/10 | 0/5 | 미달: 점유율<15% |
| HAND_TRICK | support | 20% (10/50) | 2% | 8.8 | 9 | 7/36 | 0/11 | 0/5 | 미달: 점유율<25%·픽<50% |

- WELL_LAID_PLANS†는 멤버 덱 14판 중 8판이 랭킹 1위 플레이어 덱입니다(그를 빼면 6/20). 기준은 통과해 support로 넣었지만 근거가 한 사람에게 쏠려 있습니다.
- CALCULATED_GAMBLE은 Baalorlord 버리기 덱에 3장 있지만 그의 사일런트 덱 기준 1/5라 구제 조건 미달이고, OP.GG lift 1.0입니다.
- PREPARED는 Baalorlord 5/5와 OP.GG 기준 둘 다 통과했습니다. BACKFLIP(Baalorlord 4/5)은 버리기 축에 작용하지 않고 OP.GG lift 0.9라 넣지 않았습니다.

## 삭제한 아키타입: silent.draw_burst (드로우 폭발)

상위 플레이어가 사실상 쓰지 않는 빌드라 삭제했습니다.

- 빌드를 정의하는 카드: SPEEDSTER는 94판 중 5판(전부 1장), 제시 65번 중 2번 픽(3%, 평균 21%). MURDER는 2판, 45번 중 1번 픽. 두 카드를 함께 든 덱은 0판. 랭킹 1위 플레이어는 SPEEDSTER를 10번 제시받아 한 번도 집지 않았습니다.
- 드로우 보상 카드(SPEEDSTER·MURDER)와 다른 드로우 시그니처 카드 2장 이상을 함께 든 덱은 4판(4명)뿐이고, 그 4판과 SPEEDSTER·MURDER를 든 나머지 3판까지 7판 모두 단도·교활·중독 빌드의 멤버 덱입니다. 예: https://op.gg/slay-the-spire2/history/7189737 (단도 시그니처 8장에 SPEEDSTER 1장).
- DRAW 축 멤버십(시그니처 2장 이상, 30판·18명)은 범용 드로우 카드 PREPARED·ACROBATICS가 만든 것이고, 거기서 기준을 통과한 카드도 드로우·교활·범용 카드뿐이며 드로우 보상 카드는 하나도 통과하지 못했습니다.
- Baalorlord의 버리기 덱(https://baalorlord.tv/runs/1793902215)에 SPEEDSTER가 1장 있지만 1/5입니다.

이전 카드별 수치(DRAW 축 멤버 기준):

| 카드 | 이전 역할 | 점유율(멤버) | 비멤버 | lift | 플레이어 | 빌드 내 픽/제시 | 1위 플레이어 덱 | Baalorlord | 통과 기준 |
|---|---|---|---|---|---|---|---|---|---|
| SPEEDSTER | core | 10% (3/29) | 3% | 3.4 | 3 | 1/21 | 0/8 | 1/5 | 미달: 점유율<15% |
| ACROBATICS | enabler | 62% (13/21) | 27% | 2.3 | 8 | 15/24 | 4/6 | 0/5 | 점유율≥25% / 15%+픽≥50% |
| BACKFLIP | enabler | 73% (22/30) | 67% | 1.1 | 15 | 26/61 | 5/8 | 4/5 | 미달: lift<1.5 |
| ADRENALINE | enabler | 37% (11/30) | 22% | 1.68 | 10 | 11/15 | 1/8 | 1/5 | 점유율≥25% / 15%+픽≥50% |
| EXPERTISE† | enabler | 29% (5/17) | 14% | 2.1 | 4 | 6/14 | 1/6 | 2/5 | 점유율≥25% |
| PREDATOR | enabler | 7% (2/30) | 8% | 0.9 | 2 | 1/62 | 0/8 | 0/5 | 미달: 플레이어<3, lift<1.5, 점유율<15% |
| ESCAPE_PLAN | enabler | 30% (9/30) | 20% | 1.48 | 7 | 11/23 | 2/8 | 0/5 | 미달: lift<1.5 |
| PREPARED | enabler | 81% (13/16) | 38% | 2.1 | 8 | 14/35 | 4/6 | 5/5 | 점유율≥25% |
| MURDER | payoff | 3% (1/29) | 2% | 2.2 | 1 | 1/13 | 1/8 | 0/5 | 미달: 플레이어<3, 점유율<15% |
| CORROSIVE_WAVE | payoff | 23% (7/30) | 0% | ∞ | 6 | 6/17 | 2/8 | 0/5 | 미달: 점유율<25%·픽<50% |
| TOOLS_OF_THE_TRADE | support | 13% (4/30) | 11% | 1.2 | 4 | 4/13 | 1/8 | 0/5 | 미달: lift<1.5, 점유율<15% |
| FOOTWORK | support | 63% (19/30) | 69% | 0.9 | 13 | 14/23 | 6/8 | 3/5 | 미달: lift<1.5 |

## 새 빌드 검토(추가 없음)

- **PINPOINT + POUNCE**: 둘 다 든 덱 4판·4명(랭킹 1위 플레이어 1판). 빌드 기준(≥6판) 미달.
- **SERPENT_FORM 덱 묶음**(덱 군집 7판·7명): SERPENT_FORM은 20판·17명에 있지만 모든 빌드에서 lift ≤1.4인 범용 카드이고, 함께 나오는 카드도 교활 카드(TACTICIAN 55%, ABRASIVE)와 범용 카드라 별도 빌드로 보지 않았습니다.
- **약화 빌드**: 약화 축 카드(LEG_SWEEP·MALAISE·SCARE·SUCKER_PUNCH·SUPPRESS·TRACKING) 2장 이상을 든 덱은 12판·10명이지만, 축을 소비하는 TRACKING†은 다른 약화 카드 2장 이상을 든 v0.107 덱 6판 중 2판(2명)에만 있어 카드 기준 미달이고, 나머지는 방어용 범용 카드 조합입니다. 엔진 카드가 없어 빌드로 보지 않았고, TRACKING†은 여러 빌드의 support로만 넣었습니다.
- **랭킹 1위 플레이어 빌드**: 13승 모두 단도·중독·교활 중 하나 이상의 멤버 덱이라(대부분 교활을 단도나 중독 위에 얹음) 모델에 없는 그만의 빌드는 없습니다.

## 출처 사이 충돌과 미해결

- **Baalorlord 최다 카드가 OP.GG에서는 범용**: PREPARED(5/5)는 교활 빌드 enabler로 맞아떨어지지만, PIERCING_WAIL(4/5)·BACKFLIP(4/5)·FOOTWORK(3/5)·ECHOING_SLASH†(3/5)는 OP.GG에서 어느 빌드에서도 lift 1.5를 넘지 않고(또는 축과 무관) 빌드 목록에 넣지 않았습니다. 이 카드들의 가치는 티어 쪽에서 다룰 문제입니다.
- **BOUNCING_FLASK vs DEADLY_POISON**: Baalorlord는 BOUNCING_FLASK가 낫다고 썼고, OP.GG도 BOUNCING_FLASK lift 7.9 > DEADLY_POISON 4.6입니다. 둘 다 enabler로 두었습니다(점유율은 BOUNCING_FLASK 36%, DEADLY_POISON 29%).
- **ACCELERANT† core는 근거가 얇음**: 숫자 기준(41%, 4명)은 통과하지만 멤버 덱 7판 중 4판이 랭킹 1위 플레이어 덱이고(그를 빼면 3/11), 베타에서 바뀐 카드라 v0.107 판만으로 판단했습니다. 다음 데이터 갱신 때 다시 볼 대상입니다.
- **INFINITE_BLADES core**: 48%로 core 기준(40%)은 넘지만, ACCURACY(빌드 내 픽 68%)가 더 우선입니다. `coreTarget` 1에서는 INFINITE_BLADES만 들어도 "코어 확보"가 됩니다.
- **교활 빌드 core 없음**: TACTICIAN은 숫자만 보면 core 기준을 넘지만 엔진 카드가 아니라 비웠습니다. "코어 먼저" 안내가 필요하면 별도 결정이 필요합니다.
- **Spire Codex A10 Lift(참고)**: TOOLS_OF_THE_TRADE는 A10 Lift +6.1로 전체 표본에서는 좋은 카드지만, OP.GG 상위 덱에서는 교활 덱이 아닌 덱에서도 같은 비율(12%)로 쓰여 교활 빌드 전용 카드가 아니었습니다. 판정에는 쓰지 않았습니다.
