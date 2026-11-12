# 사일런트 카드 티어: 69연승 기록 보유자 티어 리스트와 대조 (v0.111 팩)

## 1. 출처

- 요청받은 글: 레딧 r/slaythespire "Chinese world record holder Silent tier list (69 ...)". 작업 환경에서 레딧을 열 수 없어 **원문을 직접 읽지 못했습니다.**
- 대신 쓴 것: 그 글을 옮겼다고 밝힌 Untapped.gg 목록 "Giga 69 Streakers"(설명: "Based on the recent WR holder's 69 Silent Streak Tier List", 베타 브랜치, 2026-09 갱신, 카드 80장, S~F 여섯 단계, 카드별 평 없음). 제3자가 옮긴 것이라 원문과 다른 칸이 있을 수 있습니다.
- 목록에 v0.111에서 새로 생긴 `SIDESTEP`이 있어 베타(v0.108~v0.111) 기준으로 봅니다.
- 참고로 같이 본 것: Baalorlord·Jorbs·Xecnar의 사일런트 목록(모두 v0.107.1 기준, 바뀐 카드에는 쓰지 않음).

## 2. 대조 방법

- 오버레이 점수 = 티어 한 칸 15점(D 0 … S 60) + `baseAdjust`. 목록의 등급도 같은 척도로 바꿉니다(F는 D보다 반 칸 아래).
- 한 칸(15점) 이상 차이 나는 카드: 80장 중 **29장**.
- 그 차이를 반영했을 때 v0.111.0 랭커의 실제 선택과 얼마나 맞는지 쟀습니다(`RunHistoryAnalyzer validate`, 사일런트 277판, 카드 선택 3,774회).

## 3. 결과

| 반영 정도 (29장) | 1위 일치율 | main 대비 1위 일치 |
|---|---|---|
| 반영 안 함 (main) | 70.2% | – |
| 차이의 30% | 69.5% | −26회 |
| 차이의 50% | 68.7% | −54회 |
| 전부 | 64.5% | −214회 |

많이 따를수록 랭커의 실제 선택과 덜 맞습니다. 카드를 한 장씩 따로 반영해 보면 1위 일치가 늘어난 카드는 3장뿐이었습니다(`WELL_LAID_PLANS` +2회, `PINPOINT` +3회, `MURDER` +1회). `PINPOINT`는 다른 고수 목록(Baalorlord A, Jorbs B, Xecnar B)이 반대라 바꾸지 않았습니다. 차이가 큰 쪽은 두 갈래입니다.

- **목록이 더 높게 본 카드**(단도·방어 일반 카드): `CLOAK_AND_DAGGER` −53회, `BLADE_DANCE` −39회, `PIERCING_WAIL` −37회, `LEADING_STRIKE` −25회. 목록은 A를 줬지만 랭커는 보상 화면에서 그만큼 자주 집지 않습니다.
- **목록이 더 낮게 본 카드**(버리기·교활 쪽): `ACROBATICS` −27회, `BUBBLE_BUBBLE` −13회, `UP_MY_SLEEVE` −13회, `TACTICIAN` −9회. 목록은 D~F를 줬지만 랭커는 집습니다.

티어 리스트는 "이 사람의 플레이 방식에서 카드가 얼마나 좋은가"이고, 오버레이 티어는 "랭커 여럿이 같은 화면에서 실제로 무엇을 골랐는가"로 보정한 값이라 둘이 다를 수 있습니다. 프로젝트 규칙(이론보다 랭커 데이터)에 따라 통째로 반영하지 않았습니다.

## 4. 반영한 것 (2장)

목록과 랭커 선택이 같은 방향이고 다른 고수 목록도 뒷받침하는 카드만 바꿨습니다. 두 장을 함께 반영하면 1위 일치 +3회, 완전 일치 +4회(나아진 게임 24, 나빠진 게임 19)입니다.

| 카드 | 이전 | 이후 | 근거 |
|---|---|---|---|
| `WELL_LAID_PLANS` | B (+1) | **S** | v0.111에서 "턴 끝에 손패를 버리지 않음"으로 바뀜(이전: 카드 몇 장 보존). 69연승 목록 S. 옛 효과 기준으로도 Xecnar S, Baalorlord·Jorbs A. 이전 값은 옛 효과 때의 선택률로 매긴 것 |
| `MURDER` | B (−7) | **D (−8)** | 69연승 목록 F, Xecnar F, Jorbs C. v0.111에서 안 바뀐 카드. 이전 값은 랭커 표본 없이 매긴 초안(`claude-draft-v1`) |

## 5. 빌드 쪽에서 확인된 것

- 단도 core(`ACCURACY`, `INFINITE_BLADES`, `PHANTOM_BLADES`)와 중독 core(`NOXIOUS_FUMES`, `ACCELERANT`)는 이 목록에서 모두 A입니다. Baalorlord·Jorbs가 단도 core를 낮게 봤어도 core로 둔 판단([`../build-sources/v0111/EXPERT_TIERS.md`](../build-sources/v0111/EXPERT_TIERS.md))과 맞습니다.
- 교활·버리기 빌드의 카드(`TACTICIAN` D, `REFLEX` D, `ACROBATICS` D, `UNTOUCHABLE`·`RICOCHET`·`DAGGER_THROW` F)는 이 목록에서 낮습니다. 이 빌드는 원래 core가 없고, 빌드 목록은 바꾸지 않았습니다.

## 6. 한계

- 원문이 아니라 제3자가 옮긴 목록입니다. 원문 이미지나 본문을 받으면 다시 대조할 수 있습니다.
- 랭커 선택과의 일치는 대리 지표입니다. 69연승 보유자의 판단이 랭커 평균보다 나을 수 있지만, 그 차이를 이 자료로는 잴 수 없습니다.
