# 고수 티어 리스트로 core 다시 보기 (v0.111 팩)

랭커 덱 통계로 정한 core 23장을, 두 고수가 직접 매긴 카드 티어와 대조했습니다.

## 1. 출처

Untapped.gg 컴패니언의 티어 리스트(<https://sts2.untapped.gg/en/tier-lists>) 중 캐릭터 다섯을 모두 채운 두 사람의 목록입니다. 카드마다 S~D(F) 등급과 한 줄 평이 있습니다.

| 작성자 | 목록 | 기준 버전 |
|---|---|---|
| Baalorlord (A10 연승 기록 보유, Untapped.gg 파트너) | 아이언클래드·사일런트·디펙트·네크로바인더·리젠트 | v0.107.1 (2026-08 갱신) |
| Jorbs (전작부터의 고승천 연승 플레이어) | 같은 다섯 캐릭터 | v0.107.1 (2026-08 갱신) |

- 두 목록 모두 v0.107.1 기준입니다. 아래 core 23장은 v0.111에서 효과가 바뀌지 않은 카드입니다.
- 티어 리스트는 **카드 한 장의 세기**를 매긴 것이고, "이 빌드의 core / 시너지"를 구분해 주지는 않습니다. 그래서 여기서는 core 판정의 한 가지 조건으로만 썼습니다.
- 이 저장소에는 등급만 옮겼습니다. 한 줄 평 원문은 위 링크에서 볼 수 있습니다.

## 2. 규칙

오버레이에서 core는 "덱이 그 빌드로 기울면 먼저 밀어 주는 카드"입니다(빠진 core에 최대 15점, 1~2막에는 기울지 않아도 6점).

1. 등급을 점수로 바꿉니다: S 4, A 3, B 2, C 1, D 0. 두 사람 평균이 **2.5 이상(A/B 이상)** 이면 고수도 먼저 집을 만한 카드로 봅니다.
2. 평균 2.5 미만이면 core에서 payoff로 내릴 후보입니다. 두 사람의 평은 이런 카드에 대해 한결같이 "그 빌드 카드가 이미 있을 때만 집어라"는 쪽입니다(예: 정확도는 단도 카드 한 장만 있을 때 집지 말 것, 지옥불 일으키기는 폼멜 타격이 여러 장일 때만).
3. 후보를 실제로 내릴지는 v0.111 랭커 선택과 대조해 정합니다(`RunHistoryAnalyzer validate`, 1,162판·카드 선택 15,232회). 내렸을 때 랭커 선택과의 일치가 떨어지는 캐릭터는 내리지 않습니다(프로젝트 규칙: 이론보다 랭커 데이터).

## 3. 결과

| 빌드 | core | Baalorlord | Jorbs | 평균 | 판정 |
|---|---|---|---|---|---|
| `ironclad.exhaust` | FEEL_NO_PAIN | A | A | 3.0 | core 유지 |
| `ironclad.block` | BARRICADE | B | C | 1.5 | **payoff로 내림** |
| `ironclad.self_damage` | RUPTURE | B | B | 2.0 | **payoff로 내림** |
| `ironclad.hellraiser` | HELLRAISER | C | D | 0.5 | **payoff로 내림** |
| `silent.poison` | NOXIOUS_FUMES | A | S | 3.5 | core 유지 |
| `silent.poison` | ACCELERANT | A | A | 3.0 | core 유지 |
| `silent.shiv` | ACCURACY | B | C | 1.5 | core 유지(랭커 선택이 반대) |
| `silent.shiv` | INFINITE_BLADES | C | B | 1.5 | core 유지(랭커 선택이 반대) |
| `silent.shiv` | PHANTOM_BLADES | B | B | 2.0 | core 유지(랭커 선택이 반대) |
| `defect.orb_focus` | CAPACITOR | B | B | 2.0 | core 유지(랭커 선택이 반대) |
| `defect.orb_focus` | GLACIER | S | S | 4.0 | core 유지 |
| `defect.lightning` | THUNDER | B | C | 1.5 | core 유지(랭커 선택이 반대) |
| `defect.lightning` | STORM | B | C | 1.5 | core 유지(랭커 선택이 반대) |
| `defect.status` | COMPACT | B | A | 2.5 | core 유지 |
| `defect.status` | ROCKET_PUNCH | A | S | 3.5 | core 유지 |
| `defect.zero_cost` | FERAL | A | S | 3.5 | core 유지 |
| `necrobinder.osty` | CALCIFY | B | D | 1.0 | **payoff로 내림** |
| `necrobinder.doom` | SLEIGHT_OF_FLESH | S | S | 4.0 | core 유지 |
| `necrobinder.doom` | SHROUD | B | D | 1.0 | **payoff로 내림** |
| `regent.stars` | BLACK_HOLE | B | A | 2.5 | core 유지 |
| `regent.star_spend` | CHILD_OF_THE_STARS | A | A | 3.0 | core 유지 |
| `regent.forge` | FURNACE | B | D | 1.0 | **payoff로 내림** |
| `regent.creation` | PILLAR_OF_CREATION | A | A | 3.0 | core 유지 |

- 고수 기준을 넘은 core 11장: 그대로 둠.
- 기준 미달 12장 중 **6장을 payoff로 내림**: `BARRICADE`, `RUPTURE`, `HELLRAISER`(아이언클래드), `CALCIFY`, `SHROUD`(네크로바인더), `FURNACE`(리젠트).
- 기준 미달이지만 **core로 둔 6장**: 사일런트 단도의 `ACCURACY`·`INFINITE_BLADES`·`PHANTOM_BLADES`, 디펙트의 `CAPACITOR`·`THUNDER`·`STORM`. 이 카드들을 내리면 랭커 선택과 덜 맞습니다(아래).

## 4. 랭커 선택과의 대조

| 바꾼 것 (main 대비) | 1위 일치 차이 | 완전 일치 차이 | 나아진 게임 | 나빠진 게임 |
|---|---|---|---|---|
| 12장 모두 내림 | −19회 | −25회 | 50 | 74 |
| 아이언클래드·네크로바인더·리젠트 6장만 내림 | +6회 | +2회 | 25 | 22 |

캐릭터별 1위 일치율(12장 모두 내렸을 때 → main): 아이언클래드 65.1% → 65.1%, 사일런트 70.0% → 70.2%, 디펙트 63.0% → 63.7%, 네크로바인더 69.4% → 69.1%, 리젠트 61.8% → 61.7%.
사일런트와 디펙트는 내리면 나빠지고, 나머지 셋은 같거나 조금 나아집니다. 6장만 내린 쪽은 사실상 중립이라, 고수 평가를 따라 내렸습니다.

## 5. 바뀌는 동작

- 내린 6장은 "코어 확보" 가산을 받지 않습니다. 빌드 안에서의 역할 점수(payoff)는 남습니다.
- core가 없는 빌드는 13개가 됩니다. 이런 빌드는 필수 시너지(enabler·payoff) 목표를 채우면 "빌드 필수 카드" 규칙이 적용됩니다.
- `data/packs/v0.107.1`은 그대로입니다.

## 6. 한계

- 티어 리스트는 v0.107.1 기준이고 두 사람의 판단입니다. v0.111용 목록이 나오면 다시 대조해야 합니다.
- 일치율 차이는 작습니다(15,232회 중 수십 회). 방향을 보는 용도입니다.
- 카드 티어 전체(빌드와 무관한 등급)를 고수 목록과 맞추는 일은 하지 않았습니다. 오버레이 티어는 랭커 선택률로 보정한 값입니다.
