# v0.111.0 새 카드·바뀐 카드 티어 근거 (임시)

v0.111.0 팩(`data/packs/v0.111.0/tiers/`)에 새로 넣은 카드 17장의 티어와, v0.111에서 바뀐 카드 57장(티어 팩에 있는 것)을 v0.111 랭커 선택률로 다시 본 결과입니다.
**새 카드 티어는 모두 임시(provisional)입니다.** 표본이 쌓이면 다시 매깁니다.

## 1. 방법

- 런: OP.GG 랭커 코퍼스 중 `build_id` v0.111.0이고 색인의 `keep` 표시가 있는 런 **1,162판**(승천 10, 솔로, 바닐라; [`CARD_CALIBRATION.md`](../CARD_CALIBRATION.md)의 v0.111.0 1,162판과 같은 집합). 런 파일은 이 PC에만 있고 저장소에 넣지 않습니다.
- 통계: 상점이 아닌 카드 보상 화면에서 **제시됐을 때 고른 비율**(선택 / 제시). 플레이어 균형·부트스트랩 없이 단순 비율입니다(v2 티어보다 거친 통계).
- 기준 카드: 같은 캐릭터 티어 팩에 이미 있고 v0.111에서 **바뀌지 않은** 카드 중, 같은 런에서 30번 이상 제시된 카드(기본·고대 희귀도 제외). 무색은 모든 캐릭터의 제시를 합쳐 셉니다.
- 배치: 기준 카드를 같은 통계로 줄 세우고, 그 캐릭터 파일의 **지금 티어 비율대로** 잘라(아래 §4) 새 카드가 들어가는 구간의 티어를 줍니다. v2 티어가 "기존 티어 비율대로 자른 선택률 순위"였으므로 같은 눈금입니다.
- 계산 스크립트는 작업용으로만 썼고 커밋하지 않았습니다(입력이 런 파일이라서). 다시 하려면 런 색인에서 v0.111.0·`keep`을 고르고 위 통계를 세면 됩니다.

## 2. 새 카드 17장

| 캐릭터 | 카드 | 희귀도 | 모드 | 제시 | 선택 | 선택률 | 백분위 | 백분위 티어 | 넣은 티어 | 신뢰도 | 근거 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| DEFECT | HIBERNATE | Uncommon | 협동 전용 | 0 | 0 | - | - | - | C | 0.45 | 없음(협동 전용, 솔로 표본 0) → 판단 |
| DEFECT | IMITATION_LEARNING | Rare | 협동 전용 | 0 | 0 | - | - | - | C | 0.4 | 없음 → 판단 |
| DEFECT | ONE_FOR_ALL | Rare | 협동 전용 | 0 | 0 | - | - | - | C | 0.45 | 없음 → 판단 |
| IRONCLAD | BLAZE | Uncommon | 협동 전용 | 0 | 0 | - | - | - | C | 0.45 | 없음 → 판단 |
| IRONCLAD | MIDNIGHT | Rare | 협동 전용 | 0 | 0 | - | - | - | C | 0.4 | 없음 → 판단 |
| IRONCLAD | OUTRAGE | Uncommon | 협동 전용 | 0 | 0 | - | - | - | C | 0.45 | 없음 → 판단 |
| NECROBINDER | CACOPHONY | Rare | 협동 전용 | 0 | 0 | - | - | - | C | 0.4 | 없음 → 판단 |
| NECROBINDER | SOULBOUND | Uncommon | 협동 전용 | 0 | 0 | - | - | - | D | 0.4 | 없음 → 판단 |
| NECROBINDER | UNDERWORLD | Uncommon | 협동 전용 | 0 | 0 | - | - | - | C | 0.4 | 없음 → 판단 |
| REGENT | CONSTELLATION | Uncommon | 협동 전용 | 0 | 0 | - | - | - | C | 0.45 | 없음 → 판단 |
| REGENT | PLOT | Uncommon | 협동 전용 | 0 | 0 | - | - | - | C | 0.45 | 없음 → 판단 |
| REGENT | TUTOR | Rare | 협동 전용 | 0 | 0 | - | - | - | C | 0.4 | 없음 → 판단 |
| SILENT | BLADE_SYMPHONY | Uncommon | 협동 전용 | 0 | 0 | - | - | - | C | 0.45 | 없음 → 판단 |
| SILENT | CONCOCT | Uncommon | 협동 전용 | 0 | 0 | - | - | - | C | 0.45 | 없음 → 판단 |
| SILENT | FADE | Uncommon | 협동 전용 | 0 | 0 | - | - | - | C | 0.45 | 없음 → 판단 |
| SILENT | SIDESTEP | Uncommon | 공용 | 143 | 45 | 31.5% | 64.3 | B | **B** | 0.55 | 선택률 (플레이어 42명) |
| COLORLESS | THE_BALL | Uncommon | 협동 전용 | 0 | 0 | - | - | - | C | 0.45 | 없음 → 판단 |

- **새 카드 17장 중 16장이 협동 전용**(`MultiplayerOnly`)이라 솔로 랭커 코퍼스에는 한 번도 나오지 않습니다. 선택률 근거가 있는 카드는 `SIDESTEP` 1장뿐입니다(제시 143회, 30회 이상).
- 협동 전용 16장은 이 팩의 기존 협동 전용 카드 관례를 따랐습니다: 대부분 C, 다른 플레이어에게만 효과가 가는 약한 카드는 D, 신뢰도 0.4~0.45(0.5 미만 = "추정" 배지, 스킵보다 위로 추천하지 않음), `source`는 `claude-draft-v1; v0.111 new, co-op only, no solo data (provisional)`, 팁은 "협동 전용. …". 역할·축 태그는 v0.111 카드 클래스에서 정했습니다(예: `MIDNIGHT`는 `requires: ["EXHAUST"]`, `ONE_FOR_ALL`은 `requires: ["ZERO_COST"]`, `HIBERNATE`는 `provides: ["ORB", "ORB_FROST"]`, `BLADE_SYMPHONY`는 `provides: ["SHIV"]`). 다른 플레이어에게만 효과가 가는 카드는 역할을 비웠습니다(`roles: {}`).
- `upgradeValue`(휴식처 강화 보정값)는 새 카드에 넣지 않았습니다. 강화 표본이 없으므로 엔진은 `upgradedDelta`를 씁니다.
- `ABUNDANCE`(이벤트 풀, 고대 희귀도)와 `DOWSING`(퀘스트 카드)은 티어를 넣지 않았습니다. 둘 다 카드 보상에 나오지 않습니다: `DOWSING`은 Neow의 `DOWSING_ROD`가 덱에 넣는 퀘스트 카드이고, 미지의 장소 5곳을 지나면 `ABUNDANCE`로 바뀝니다. 이 팩은 원래 저주·상태이상·토큰·이벤트·퀘스트 카드를 티어 팩에 넣지 않으며, 검증 테스트도 티어 항목은 캐릭터·무색 풀 카드만 허용합니다. 두 카드 모두 전투 데이터(`combat/cards.json`)에는 있습니다.

## 3. 바뀐 카드 57장: v0.111 선택률로 다시 보기

v0.111에서 카탈로그 필드(희귀도·비용·타입·키워드·별 비용)나 설명 수치가 바뀐 카드는 64장이고, 그중 57장이 티어 팩에 있습니다(나머지 7장 `BRIGHTEST_FLAME`·`FUEL`·`GIANT_ROCK`·`MAUL`·`MINION_SACRIFICE`·`RELAX`·`WHISTLE`은 고대·이벤트가 주는 카드라 티어 항목이 없습니다).

**결과: 지금 티어에서 2단계 이상 떨어진 카드는 0장입니다.** 그래서 티어 글자를 바꾼 카드는 없습니다(명확한 경우만 적용한다는 규칙). 1단계 차이 18장과 v0.111 표본이 30회 미만인 7장, 모두 25장에 `changedIn: "v0.111.0"`을 달아 "추정"으로 표시했습니다. 표본 30회 이상이고 같은 티어로 나온 32장은 v0.111 데이터가 지금 티어를 확인한 것으로 보고 표시하지 않았습니다.

| 캐릭터 | 카드 | 바뀐 것 | 제시 | 선택률 | 백분위 | 현재 티어 | 백분위 티어 | 차이 | changedIn |
|---|---|---|---|---|---|---|---|---|---|
| DEFECT | BIASED_COGNITION | 수치 | 0 | - | - | A | - |  | v0.111.0 |
| DEFECT | HYPERBEAM | 수치 | 60 | 20.0% | 35.2 | C | B | -1 | v0.111.0 |
| DEFECT | IGNITION | 카탈로그+수치 | 0 | - | - | C | - |  | v0.111.0 |
| DEFECT | MOMENTUM_STRIKE | 수치 | 280 | 3.2% | 0.0 | C | D | +1 | v0.111.0 |
| DEFECT | NULL | 수치 | 107 | 31.8% | 60.6 | B | B | +0 |  |
| DEFECT | REFRACT | 수치 | 110 | 26.4% | 49.3 | B | B | +0 |  |
| DEFECT | SHATTER | 카탈로그+수치 | 52 | 23.1% | 40.8 | B | B | +0 |  |
| DEFECT | SUNDER | 수치 | 131 | 10.7% | 7.0 | C | C | +0 |  |
| DEFECT | SYNCHRONIZE | 카탈로그+수치 | 114 | 12.3% | 14.1 | B | C | +1 | v0.111.0 |
| DEFECT | THUNDER | 수치 | 116 | 50.0% | 86.6 | B | B | +0 |  |
| DEFECT | TRASH_TO_TREASURE | 수치 | 50 | 32.0% | 62.0 | A | B | +1 | v0.111.0 |
| IRONCLAD | BLOODLETTING | 카탈로그+수치 | 188 | 51.6% | 91.0 | B | A | -1 | v0.111.0 |
| IRONCLAD | COLOSSUS | 수치 | 180 | 53.9% | 92.5 | B | A | -1 | v0.111.0 |
| IRONCLAD | CRIMSON_MANTLE | 수치 | 80 | 53.7% | 92.5 | B | A | -1 | v0.111.0 |
| IRONCLAD | CRUELTY | 카탈로그+수치 | 185 | 39.5% | 77.6 | B | B | +0 |  |
| IRONCLAD | DEMON_FORM | 수치 | 101 | 36.6% | 76.1 | B | B | +0 |  |
| IRONCLAD | DOMINATE | 카탈로그+수치 | 91 | 44.0% | 85.1 | A | B | +1 | v0.111.0 |
| IRONCLAD | EXPECT_A_FIGHT | 카탈로그+수치 | 204 | 27.5% | 55.2 | C | B | -1 | v0.111.0 |
| IRONCLAD | HOWL_FROM_BEYOND | 수치 | 190 | 26.8% | 55.2 | C | B | -1 | v0.111.0 |
| IRONCLAD | MANGLE | 수치 | 84 | 26.2% | 53.7 | C | B | -1 | v0.111.0 |
| IRONCLAD | PACTS_END | 수치 | 82 | 36.6% | 76.1 | C | B | -1 | v0.111.0 |
| IRONCLAD | RAMPAGE | 수치 | 182 | 9.9% | 10.4 | C | C | +0 |  |
| IRONCLAD | SETUP_STRIKE | 수치 | 522 | 6.9% | 4.5 | C | C | +0 |  |
| IRONCLAD | TANK | 수치 | 0 | - | - | D | - |  | v0.111.0 |
| IRONCLAD | TAUNT | 카탈로그+수치 | 538 | 28.6% | 59.7 | B | B | +0 |  |
| NECROBINDER | EIDOLON | 카탈로그+수치 | 42 | 2.4% | 1.4 | C | C | +0 |  |
| NECROBINDER | HAUNT | 수치 | 94 | 11.7% | 16.4 | C | C | +0 |  |
| NECROBINDER | LEGION_OF_BONE | 카탈로그+수치 | 0 | - | - | C | - |  | v0.111.0 |
| NECROBINDER | REAVE | 수치 | 265 | 3.4% | 1.4 | C | C | +0 |  |
| NECROBINDER | SHROUD | 수치 | 91 | 27.5% | 54.8 | B | B | +0 |  |
| NECROBINDER | SOUL_STORM | 수치 | 46 | 8.7% | 6.8 | C | C | +0 |  |
| NECROBINDER | THE_SCYTHE | 수치 | 39 | 20.5% | 38.4 | C | B | -1 | v0.111.0 |
| NECROBINDER | TIMES_UP | 카탈로그+수치 | 36 | 19.4% | 34.2 | C | C | +0 |  |
| REGENT | ALIGNMENT | 카탈로그+수치 | 72 | 23.6% | 53.7 | B | B | +0 |  |
| REGENT | COLLISION_COURSE | 수치 | 250 | 16.4% | 23.9 | B | C | +1 | v0.111.0 |
| REGENT | CRUSH_UNDER | 수치 | 251 | 10.0% | 9.0 | C | C | +0 |  |
| REGENT | DEVASTATE | 수치 | 83 | 13.3% | 20.9 | C | C | +0 |  |
| REGENT | GUIDING_STAR | 카탈로그+수치 | 225 | 12.9% | 19.4 | C | C | +0 |  |
| REGENT | PILLAR_OF_CREATION | 수치 | 105 | 33.3% | 70.9 | B | B | +0 |  |
| REGENT | REFINE_BLADE | 수치 | 230 | 12.6% | 19.4 | B | C | +1 | v0.111.0 |
| REGENT | RESONANCE | 카탈로그+수치 | 74 | 17.6% | 23.9 | C | C | +0 |  |
| REGENT | SPOILS_OF_BATTLE | 수치 | 223 | 13.0% | 20.9 | C | C | +0 |  |
| REGENT | TERRAFORMING | 수치 | 91 | 18.7% | 28.4 | C | B | -1 | v0.111.0 |
| SILENT | ACCELERANT | 카탈로그+수치 | 167 | 25.1% | 52.9 | B | B | +0 |  |
| SILENT | ANTICIPATE | 수치 | 415 | 1.7% | 0.0 | C | D | +1 | v0.111.0 |
| SILENT | ECHOING_SLASH | 카탈로그+수치 | 168 | 19.0% | 44.3 | B | B | +0 |  |
| SILENT | EXPERTISE | 수치 | 170 | 14.7% | 31.4 | B | B | +0 |  |
| SILENT | FLANKING | 카탈로그+수치 | 0 | - | - | D | - |  | v0.111.0 |
| SILENT | FLICK_FLACK | 수치 | 463 | 4.5% | 4.3 | C | C | +0 |  |
| SILENT | HAZE | 카탈로그+수치 | 152 | 17.8% | 42.9 | B | B | +0 |  |
| SILENT | MIRAGE | 수치 | 147 | 11.6% | 24.3 | C | C | +0 |  |
| SILENT | OUTBREAK | 카탈로그+수치 | 71 | 31.0% | 64.3 | B | B | +0 |  |
| SILENT | WELL_LAID_PLANS | 카탈로그+수치 | 89 | 52.8% | 90.0 | B | B | +0 |  |
| COLORLESS | BEACON_OF_HOPE | 카탈로그+수치 | 0 | - | - | D | - |  | v0.111.0 |
| COLORLESS | REND | 카탈로그+수치 | 24 | 0.0% | 13.6 | C | C | +0 | v0.111.0 |
| COLORLESS | SALVO | 카탈로그+수치 | 34 | 5.9% | 36.4 | C | C | +0 |  |
| COLORLESS | SPLASH | 카탈로그+수치 | 21 | 4.8% | 27.3 | C | C | +0 | v0.111.0 |

- 차이: (백분위 티어 − 현재 티어), 음수 = 데이터가 더 높게 봄. 제시 0인 카드는 협동 전용이거나 고대 희귀도(`BIASED_COGNITION`)입니다.
- **1단계 차이 후보(적용 안 함, 다음 보정 후보)**: 올림 쪽 `BLOODLETTING`·`COLOSSUS`·`CRIMSON_MANTLE`(B→A), `EXPECT_A_FIGHT`·`HOWL_FROM_BEYOND`·`MANGLE`·`PACTS_END`(C→B), `HYPERBEAM`·`THE_SCYTHE`·`TERRAFORMING`(C→B); 내림 쪽 `DOMINATE`·`TRASH_TO_TREASURE`(A→B), `SYNCHRONIZE`·`COLLISION_COURSE`·`REFINE_BLADE`(B→C), `MOMENTUM_STRIKE`·`ANTICIPATE`(C→D, 기준 카드 전부보다 낮음). 단순 비율이고 재표본을 하지 않았으므로 바로 옮기지 않았습니다. 17장 중 11장은 이미 같은 방향의 `baseAdjust`(카드 점수 보정, 배지 티어는 그대로)가 있습니다: 올림 쪽 10장 중 6장이 +1~+10, 내림 쪽 7장 중 5장이 −8~−10.
- 바뀐 효과에 맞춰 **역할·축 태그만** 고친 카드: `HAZE`(교활 제거 → `requires: DISCARD` 삭제, 약화 추가 → `provides: WEAK`), `OUTBREAK`(파워 → 스킬, 독 9를 주고 바로 발동 → `provides: POISON`, `requires` 삭제, `aoe 0.7`), `EXPECT_A_FIGHT`(에너지 → 힘 비례 방어 → `roles: block 0.6`, `consumes: STRENGTH`), `EXPERTISE`(최대 6장 → 2장 드로우 → `draw 0.4`, `cycle 0.4`), `EIDOLON`(소멸 더미의 에테리얼 카드를 다시 씀 → `consumes/requires: ETHEREAL`). 대체값 `type`·`rarity`·`cost`는 v0.111 카탈로그로 갱신(19칸).

## 4. 기준 카드와 자르는 점

| 캐릭터 | 기준 카드 수 | 티어 분포 | 자르는 점(위에서부터 누적 비율) |
|---|---|---|---|
| DEFECT | 71 | A 5, B 43, C 23 | S 0.000, A 0.070, B 0.676, C 1.000, D 1.000 |
| IRONCLAD | 67 | A 7, B 32, C 27, S 1 | S 0.015, A 0.119, B 0.597, C 1.000, D 1.000 |
| NECROBINDER | 73 | A 4, B 43, C 26 | S 0.000, A 0.055, B 0.644, C 1.000, D 1.000 |
| REGENT | 67 | A 2, B 46, C 17, D 1, S 1 | S 0.015, A 0.045, B 0.731, C 0.985, D 1.000 |
| SILENT | 70 | A 7, B 43, C 20 | S 0.000, A 0.100, B 0.714, C 1.000, D 1.000 |
| COLORLESS | 11 | B 7, C 4 | S 0.000, A 0.000, B 0.636, C 1.000, D 1.000 |

기준 카드에는 D가 거의 없습니다(D는 대부분 기본 카드와 협동 전용 카드라 기준에서 빠짐). 그래서 기준 카드 전부보다 선택률이 낮으면 D로 표시했습니다. 무색은 기준 카드가 11장뿐이라 무색 배치는 약한 근거입니다.

## 5. 학습 모델(`model/card-policy.json`)

학습한 카드 선택 모델은 카드 id별 값을 가지며(535장), v0.107~v0.111 랭커 화면으로 학습했습니다. v0.111 새 카드 중 아는 카드는 `SIDESTEP`(n=143), `ABUNDANCE`, `DOWSING`뿐입니다. 협동 전용 새 카드 16장은 모델에 없고, 오버레이는 이런 카드에 학습값을 만들지 않고 "솔로 데이터 없음"(`CoopOnlyNoSoloData`, 추정)으로 표시합니다(`LearnedRescore`). 모델 파일은 바꾸지 않았습니다(없는 값을 지어내지 않기 위해). 사라진 `SCARE` 항목은 남아 있지만 제시되지 않으므로 쓰이지 않습니다. `changedIn`이 달린 카드는 모델이 알아도 "추정"으로 남습니다.
