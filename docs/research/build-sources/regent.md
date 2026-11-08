# 리젠트: 빌드 카드 근거

공통 출처와 판정 규칙은 [README.md](README.md)에 있습니다. 이 문서는 `archetypes/regent.json`의 카드마다 어떤 근거로 들어갔는지 적습니다.

## 표본

- **OP.GG**: 리젠트 승천 10 바닐라 승리 **39판, 플레이어 16명** (v0.107.1 26판, v0.111.0 13판). v0.105.1 런 1판은 팩 버전과 너무 멀어 뺐습니다.
  - OP.GG 랭킹 1위 플레이어가 **13판**(모두 v0.107.1)을 차지합니다. 그다음은 4판씩입니다.
  - 베타에서 바뀐 카드(PILLAR_OF_CREATION, ALIGNMENT, SPOILS_OF_BATTLE, REFINE_BLADE, GUARDS, COLLISION_COURSE, DEVASTATE, TERRAFORMING, CRUSH_UNDER)는 v0.107.x 26판(10명)만 봤습니다. 표에 ¹로 표시합니다.
- **Baalorlord**: A10 리젠트 승리 덱 9판 (2026-08-21 ~ 11-05, 6연승 중 2~6번째 포함). 런 페이지에 게임 버전이 없어 v0.10x~v0.111 중 어느 것인지는 확인하지 못했습니다. 예: <https://baalorlord.tv/runs/1790276915>, <https://baalorlord.tv/runs/1792083714>, <https://baalorlord.tv/runs/1793910099>.
- 표본이 작습니다. 빌드마다 10~14판이라 한 판 차이로 비율이 7~10%p 움직입니다.

## 빌드 소속 정의

카드 효과를 기준으로 고른 "엔진 카드"를, 평가하는 카드를 빼고 **2장 이상** 가진 덱을 그 빌드 덱으로 봤습니다(leave-one-out). 그래서 카드가 자기 비율을 스스로 부풀리지 못합니다.

- 별 엔진: BLACK_HOLE, CHILD_OF_THE_STARS, RADIATE, CRESCENT_SPEAR, STARDUST, GENESIS, THE_SEALED_THRONE, SEVEN_STARS, DECISIONS_DECISIONS, COMET, NEUTRON_AEGIS, ROYAL_GAMBLE (별에 반응하거나, 매 턴 별을 만들거나, 별 5개 이상을 씀)
- 벼리기 엔진: SWORD_SAGE, SEEKING_EDGE, PARRY, CONQUEROR, SUMMON_FORTH, FURNACE
- 생성 엔진: ARSENAL, PILLAR_OF_CREATION, SUPERMASSIVE, HEIRLOOM_HAMMER, SPECTRUM_SHIFT

**별 덱은 두 빌드로 나눴습니다.** 별 덱 22판 중 1위 플레이어의 10판에는 BLACK_HOLE이 한 장도 없고(그는 보상에서 2/9번만 집음), 다른 플레이어의 12판 중 9판에는 BLACK_HOLE이 있습니다. 하나로 합치면 BLACK_HOLE의 lift가 1.0으로 떨어져 어느 빌드의 엔진도 core에 남지 않습니다.
- `regent.stars` = 1위 플레이어를 뺀 별 덱. 12판·8명, 한 사람 최대 4판 → 일반 빌드 기준 통과.
- `regent.star_spend` = 1위 플레이어의 별 덱. 10판 → **1위 플레이어 빌드** 규칙(4승 이상)으로 인정. 소속이 그의 덱뿐이라 일반 기준(3명 이상)은 이 빌드에서 성립할 수 없고, 카드는 "그의 덱 절반 이상 + lift 1.5 이상"으로만 판정했습니다.

표 읽는 법: **비율** = 빌드 덱 중 그 카드를 가진 덱(LOO). **lift** = 빌드 덱 비율 ÷ 다른 덱 비율. **집음** = 빌드 덱 런의 카드 보상에서 집은 횟수/나온 횟수(상점·고대 제외). **1위** = 1위 플레이어가 그 카드를 가진 덱/그의 리젠트 승리 13판(¹ 카드도 13판 모두 v0.107.1). **B** = Baalorlord 9판 중. lift 1.5~1.6은 **경계선**으로 표시합니다.

---

## 1. `regent.stars` 별 (블랙홀)

표본: 12판·8명, 1위 플레이어 0판, v0.107.1 6판·v0.111.0 6판. 1위 플레이어가 아닌 랭커들의 별 덱입니다.

| 카드 | 역할 | 비율 | lift | 명 | 집음 | 1위 | B | 통과한 기준 |
|---|---|---|---|---|---|---|---|---|
| BLACK_HOLE | core | 5/8 = 62% | 3.2 | 4 | 3/7 | 2/13 | 4/9 | OP.GG (core: 40%·4명 이상) |
| GATHER_LIGHT | enabler | 10/12 = 83% | 1.9 | 7 | 11/27 | 7/13 | 5/9 | OP.GG |
| GLOW | enabler | 8/12 = 67% | 2.6 | 6 | 6/25 | 2/13 | 8/9 | OP.GG |
| ROYAL_GAMBLE | enabler | 6/12 = 50% | 2.7 | 5 | 4/8 | 5/13 | 0/9 | OP.GG |
| BIG_BANG | enabler | 6/12 = 50% | 2.3 | 4 | 4/4 | 2/13 | 3/9 | OP.GG |
| SOLAR_STRIKE | enabler | 4/12 = 33% | 1.8 | 3 | 5/30 | 2/13 | 0/9 | OP.GG |
| CLOAK_OF_STARS | payoff | 6/12 = 50% | 13.5 | 5 | 6/15 | 1/13 | 5/9 | OP.GG |
| STARDUST | payoff | 5/12 = 42% | 2.8 | 4 | 3/6 | 4/13 | 0/9 | OP.GG |
| COMET | payoff | 3/11 = 27% | 3.8 | 3 | 3/6 | 1/13 | 0/9 | OP.GG |
| REFLECT | payoff | 6/12 = 50% | 1.2 | 4 | 6/9 | 5/13 | 5/9 | 보조 근거 (OP.GG lift 미달) |
| PARTICLE_WALL | payoff | 2/12 = 17% | 1.1 | 2 | 1/4 | 3/13 | 6/9 | 보조 근거 |
| ASTRAL_PULSE | payoff | 2/12 = 17% | ∞ (다른 덱 0/27) | 2 | 2/23 | 0/13 | 6/9 | 보조 근거 (**충돌**, 아래 참고) |
| SPECTRUM_SHIFT | support | 8/12 = 67% | 2.0 | 6 | 6/10 | 1/13 | 3/9 | OP.GG (별 축 카드가 아니라 support) |
| ORBIT | support | 7/12 = 58% | 2.0 | 5 | 9/17 | 3/13 | 3/9 | OP.GG (범용) |

- 역할: 별을 얻는 카드는 enabler, 별 비용을 내는 카드는 효과가 방어든 공격이든 별을 소비하므로 payoff입니다. BLACK_HOLE은 별을 얻거나 쓸 때마다 작동하는 파워라 엔진입니다.
- BLACK_HOLE의 LOO 기준 덱이 8판인 이유: BLACK_HOLE 덱 9판 중 4판은 BLACK_HOLE을 빼면 별 엔진이 1장만 남아 빠집니다. 이 4판을 넣어도(9/12) 결론은 같습니다.
- 대표 덱: <https://op.gg/slay-the-spire2/history/7210354>, <https://op.gg/slay-the-spire2/history/7162265>, <https://op.gg/slay-the-spire2/history/7152833>, <https://op.gg/slay-the-spire2/history/6977736>

**뺀 카드**

| 카드 | 기존 역할 | 근거 | 이유 |
|---|---|---|---|
| GENESIS | core, 대표 카드 | 1/12, 1명, 집음 1/4 | 이 빌드에서 거의 안 씀. 1위 플레이어 덱에서도 3/9(33%)로 절반 미만 |
| HIDDEN_CACHE | enabler | 5/12, lift 1.1 | lift 미달. B 4/9로 보조 근거도 미달. `regent.star_spend`로 옮김 |
| CONVERGENCE | enabler | 2/12, lift 0.35 | `regent.star_spend`로 옮김 |
| SHINING_STRIKE | enabler | 2/12, 2명, 전체 집음 2/28 | 3명 미만, 잘 안 집음 |
| THE_SEALED_THRONE | enabler | 2/12, 2명 | 고대 카드, 3명 미만 |
| CHILD_OF_THE_STARS | payoff | 2/11, lift 0.7 | `regent.star_spend`로 옮김 (core) |
| RADIATE | payoff | 2/11, lift 0.9 | `regent.star_spend`로 옮김 |
| SEVEN_STARS | payoff | 2/12, 2명 | 3명 미만. 1위 플레이어 덱에서도 3/10 |
| GAMMA_BLAST | payoff | 3/12, lift 1.35 | lift 미달 |
| DEVASTATE ¹ | payoff | v0.107 빌드 덱 0/6 | 1위 플레이어 덱 2판뿐 |
| ALIGNMENT ¹ | payoff | v0.107 빌드 덱 0/6 | 1위 플레이어 덱 3판뿐 |
| METEOR_SHOWER | payoff | 1/12 | 고대 카드, 1명 |
| NEUTRON_AEGIS | support | 0/12, 전체 집음 0/12 | 아무도 안 씀 |

Baalorlord 덱 절반 이상이지만 별 축 효과가 없어 보조 근거로 살리지 않은 카드: KNOW_THY_PLACE(6/9, OP.GG lift 1.0), GLITTERSTREAM(7/9, 이 빌드 lift 0.6).

---

## 2. `regent.star_spend` 별 소모 (수렴) — OP.GG 랭킹 1위 플레이어 빌드 (새로 추가)

표본: 1위 플레이어의 별 덱 10판(v0.107.1, 주간 순위 4~30위 런). 그의 리젠트 승리 13판 중 10판입니다. 다른 랭커 중에도 CHILD_OF_THE_STARS를 쓰는 사람(덱 2~3판)이 있지만, 이 조합 전체(CONVERGENCE·HIDDEN_CACHE·DECISIONS_DECISIONS·RADIATE·BEGONE)는 그의 덱에서만 반복됩니다.

| 카드 | 역할 | 그의 덱 비율 | lift | 그의 집음 | B | 통과한 기준 |
|---|---|---|---|---|---|---|
| CHILD_OF_THE_STARS | core | 5/9 = 56% | 4.2 | 4/7 | 4/9 | 1위 빌드 (core: 그의 덱 50% 이상) |
| CONVERGENCE | enabler | 7/10 = 70% | 2.5 | 9/10 | 3/9 | 1위 빌드 |
| HIDDEN_CACHE | enabler | 7/10 = 70% | 2.5 | 4/18 | 4/9 | 1위 빌드 |
| GATHER_LIGHT | enabler | 6/10 = 60% | 1.1 | 5/14 | 5/9 | 보조 근거 (lift 미달) |
| DECISIONS_DECISIONS | payoff | 5/9 = 56% | 3.3 | 5/5 | 0/9 | 1위 빌드 |
| RADIATE | payoff | 5/10 = 50% | 4.8 | 4/8 | 0/9 | 1위 빌드 |
| GUIDING_STAR | payoff | 6/10 = 60% | 2.5 | 6/28 | 3/9 | 1위 빌드 |
| PARTICLE_WALL | payoff | 3/10 = 30% | 2.9 | 3/12 | 6/9 | 보조 근거 (그의 덱 절반 미만) |
| BEGONE | support | 8/10 = 80% | 1.8 | 8/21 | 5/9 | 1위 빌드 (별 축 카드가 아니라 support) |

- CONVERGENCE는 다음 턴 별을 공급하는 카드라 core가 아니라 enabler입니다. core는 별을 쓸 때마다 작동하는 파워인 CHILD_OF_THE_STARS 하나입니다.
- 대표 덱: <https://op.gg/slay-the-spire2/history/7173573>, <https://op.gg/slay-the-spire2/history/7278207>, <https://op.gg/slay-the-spire2/history/7278201>
- 그의 덱 절반에 못 미쳐 넣지 않은 카드: STARDUST·TYRANNY·MONOLOGUE 4/10, ROYAL_GAMBLE 3/8, SEVEN_STARS 3/10, GENESIS 3/9, GUARDS¹·ALIGNMENT¹ 3/10, COSMIC_INDIFFERENCE 5/10(lift 1.2).
- Baalorlord 덱 절반 이상이지만 이 빌드에서 OP.GG와 어긋나 넣지 않은 카드: ASTRAL_PULSE(그의 덱 0/10, 보상 0/19), CLOAK_OF_STARS(0/10), GLOW(lift 0.45), REFLECT(lift 0.9).
- 핵심 유물: 3명 이상 기준이 1인 빌드에서 성립하지 않아 비웠습니다.

---

## 3. `regent.forge` 벼리기(검 토큰)

표본: 14판·8명, 1위 플레이어 4판(주로 FURNACE·PARRY·BULWARK를 별 덱에 곁들인 형태, 4판 중 3판이 별 소모 덱과 겹침), v0.107.1 10판·v0.111.0 4판.

| 카드 | 역할 | 비율 | lift | 명 | 집음 | 1위 | B | 통과한 기준 |
|---|---|---|---|---|---|---|---|---|
| SWORD_SAGE | core | 6/13 = 46% | 12.0 | 5 | 6/7 | 0/13 | 1/9 | OP.GG (core) |
| FURNACE | core | 4/8 = 50% | 2.2 | 4 | 6/7 | 3/13 | 0/9 | OP.GG (core) |
| BULWARK | enabler | 6/14 = 43% | 2.7 | 3 | 6/10 | 5/13 | 3/9 | OP.GG |
| SPOILS_OF_BATTLE ¹ | enabler | 4/10 = 40% | 3.2 | 4 | 5/24 | 2/13 | 0/9 | OP.GG |
| WROUGHT_IN_WAR | enabler | 5/14 = 36% | 4.5 | 5 | 7/27 | 2/13 | 0/9 | OP.GG |
| SUMMON_FORTH | enabler | 4/12 = 33% | 4.5 | 4 | 4/8 | 1/13 | 0/9 | OP.GG |
| PARRY | payoff | 3/9 = 33% | 2.0 | 3 | 4/6 | 3/13 | 0/9 | OP.GG |
| COSMIC_INDIFFERENCE | support | 8/14 = 57% | **1.59 (경계선)** | 4 | 6/28 | 6/13 | 3/9 | OP.GG (범용) |
| ORBIT | support | 8/14 = 57% | 2.0 | 5 | 10/16 | 3/13 | 3/9 | OP.GG (범용) |

- SUMMON_FORTH는 벼리기를 공급하므로 payoff → enabler로 옮겼습니다. PARRY는 검 토큰 사용에 반응하는 파워라 payoff입니다.
- coreTarget은 기본값(1) 그대로: 두 core를 함께 가진 덱은 4/14입니다.
- 1위 플레이어는 SWORD_SAGE(1번 나옴)와 SEEKING_EDGE(5번 나옴)를 한 번도 집지 않았습니다. 그의 벼리기 덱 4판 중 3판은 별 소모 덱이기도 해서 따로 빌드로 만들지 않았습니다.
- 대표 덱: <https://op.gg/slay-the-spire2/history/7295402>, <https://op.gg/slay-the-spire2/history/7177823>, <https://op.gg/slay-the-spire2/history/7214782>, <https://op.gg/slay-the-spire2/history/7093776>

**뺀 카드**

| 카드 | 기존 역할 | 근거 | 이유 |
|---|---|---|---|
| SEEKING_EDGE | core, 대표 카드 | 3/13, 2명, 전체 집음 3/12 | 3명 미만 (근거 부족) |
| REFINE_BLADE ¹ | enabler | 3/10, lift 4.8, 2명 | 3명 미만 (근거 부족) |
| THE_SMITH | enabler | 2/14, 2명, 전체 집음 2/12 | 3명 미만 |
| BEAT_INTO_SHAPE | enabler | 1/14, 전체 집음 1/14 | 거의 안 씀 |
| BIG_BANG | enabler | 2/14, lift 0.4 | 벼리기 덱에 특별히 많지 않음 (별 빌드에 남김) |
| CONQUEROR | payoff | 2/13, 2명, 전체 집음 2/14 | 3명 미만 |

BLACK_HOLE은 이 빌드 덱에서도 5/14·4명이지만 lift 1.49로 기준 바로 아래라 넣지 않았습니다.

---

## 4. `regent.creation` 카드 생성

표본: 13판·9명, 1위 플레이어 0판, v0.107.1 7판·v0.111.0 6판.

| 카드 | 역할 | 비율 | lift | 명 | 집음 | 1위 | B | 통과한 기준 |
|---|---|---|---|---|---|---|---|---|
| CHARGE | enabler | 6/13 = 46% | 3.0 | 4 | 3/7 | 3/13 | 0/9 | OP.GG |
| MANIFEST_AUTHORITY | enabler | 6/13 = 46% | 12.0 | 5 | 6/9 | 0/13 | 3/9 | OP.GG |
| QUASAR | enabler | 5/13 = 38% | 10.0 | 5 | 4/8 | 0/13 | 4/9 | OP.GG |
| BEGONE | enabler | 8/13 = 62% | 1.2 | 7 | 8/18 | 11/13 | 5/9 | 보조 근거 (lift 미달) |
| PILLAR_OF_CREATION ¹ | payoff | 3/5 = 60% | 3.2 | 3 | 4/5 | 2/13 | 3/9 | OP.GG (core 기준 4명 미달) |
| SUPERMASSIVE | payoff | 3/11 = 27% | 3.8 | 3 | 1/6 | 0/13 | 1/9 | OP.GG |
| GLITTERSTREAM | support | 7/13 = 54% | 4.7 | 5 | 7/30 | 1/13 | 7/9 | OP.GG (범용) |
| GLOW | support | 7/13 = 54% | 1.75 | 6 | 6/23 | 2/13 | 8/9 | OP.GG (별 공급 카드라 이 빌드에선 support) |
| VOID_FORM | support | 4/13 = 31% | 2.0 | 4 | 4/7 | 2/13 | 0/9 | OP.GG (범용) |

- **core 비움.** 엔진 후보 PILLAR_OF_CREATION은 베타 변경 카드라 v0.107 덱만 셉니다. 그러면 빌드 덱 5판 중 3판(3명)으로, core 기준(4명)에 못 미칩니다. v0.111을 합치면 4명이 되지만 규칙상 쓰지 않았습니다. 그래서 이 빌드는 "코어 먼저"가 꺼집니다. 대표 카드는 근거 있는 카드 중 비율이 가장 높은 PILLAR_OF_CREATION입니다.
- BEGONE은 손패 카드를 하수인 카드로 바꾸는 카드로, 티어 파일에서 생성 축(CREATE)을 공급합니다. 이를 생성 메커니즘에 작동한다고 보고 보조 근거로 살렸습니다.
- 대표 덱: <https://op.gg/slay-the-spire2/history/7285865>, <https://op.gg/slay-the-spire2/history/6749776>, <https://op.gg/slay-the-spire2/history/7202938>, <https://op.gg/slay-the-spire2/history/6955603>

**뺀 카드**

| 카드 | 기존 역할 | 근거 | 이유 |
|---|---|---|---|
| ARSENAL | core, 대표 카드 | 2/9, 2명, lift 1.3 | 3명 미만 (근거 부족) |
| SPECTRUM_SHIFT | enabler | 4/8, lift 1.2 | 생성 덱에 특별히 많지 않음 (별 블랙홀 빌드에 support로 남김) |
| BUNDLE_OF_JOY | enabler | 3/13 = 23%, lift 6.0, 집음 1/2 | 비율 25% 미달, 보상 등장 5번 미만 |
| COLLISION_COURSE ¹ | enabler | v0.107 1/7, 전체 집음 2/44 | 1명. 사용 대부분이 v0.111 런 |
| CRASH_LANDING | enabler | 2/13, 1명 | 1명 |
| GUARDS ¹ | enabler | v0.107 1/7 | 1명. v0.107에선 1위 플레이어 별 덱에서만 씀 |
| HEIRLOOM_HAMMER | payoff | 0/10 | 아무도 안 씀 |

Baalorlord 덱 절반 이상이지만 생성 축 효과가 없어 살리지 않은 카드: REFLECT(5/9), KNOW_THY_PLACE(6/9). COSMIC_INDIFFERENCE(7/13, lift 1.4)는 lift 미달.

---

## 5. 핵심 유물

기존 핵심 유물을 빌드 덱으로 다시 확인했습니다(3명 이상·lift 1.5 이상이면 유지).
- 유지: FENCING_MANUAL (벼리기 3/14·3명·lift 1.8), TOOLBOX (생성 3/13·3명·다른 덱 0/26).
- 삭제: GALACTIC_DUST(별 2/12), LUNAR_PASTRY(1/12), MINI_REGENT(0), DIVINE_DESTINY(0), REGALITE(생성 2/13), VITRUVIAN_MINION(1/13), ORANGE_DOUGH(0).
- 새로 넣지 않음: 기준을 넘는 유물(GORGET, LANTERN, ANCHOR 등)은 여러 빌드에 고루 나오는 범용 유물이라 기존 빌드에 더하지 않았습니다.

## 6. fullWeight

빌드 덱과 다른 덱의 commit 중앙값(새 목록, 39판 기준):

| 빌드 | fullWeight | 빌드 덱 중앙값 | 다른 덱 중앙값 | 결정 |
|---|---|---|---|---|
| stars | 4.5 | 1.00 | 0.49 | 유지 (다른 덱 0.5 이하, 거의 경계) |
| star_spend | 4.0 (새로, 기본값) | 1.00 | 0.39 | 기본값으로 기준 통과 |
| forge | 4.0 | 0.99 | 0.12 | 유지 |
| creation | 4.0 | 0.72 | 0.17 | 유지 |

## 7. 빌드 추가·삭제

- **추가 `regent.star_spend`**: 1위 플레이어 빌드 규칙(그의 승리 10판)으로 추가했습니다.
- **삭제 없음**: 세 기존 빌드 모두 기준(6판·4명 이상, 한 사람 절반 이하)을 넘습니다.
- **모델링하지 않은 것**: 벼리기 보조 묶음(FURNACE+PARRY+BULWARK를 별·생성 덱에 곁들임, 1위 플레이어 4판 + 2명). 1위 플레이어의 4판 중 3판이 별 소모 덱과 겹치는 곁들이 묶음이라 별도 빌드로 만들지 않았고, 해당 카드는 `regent.forge`에 있습니다. CHARGE·PHOTON_CUT·BIG_BANG·COMET 조합은 3판뿐이라 빌드로 보지 않았습니다.

## 8. 출처 간 충돌 (해결 안 됨)

- **ASTRAL_PULSE**: Baalorlord 덱 6/9에 있지만 OP.GG에서는 보상에 68번 나와 2번만 집혔고, 1위 플레이어는 27번 중 0번입니다. 규칙상 "OP.GG와 어긋남"은 lift 1.0 미만으로 정의되어 있고, 블랙홀 빌드에서는 2/12 대 다른 덱 0/27이라 lift가 1.0 이상이므로 `regent.stars`에 payoff로 남겼습니다. 1위 빌드에는 넣지 않았습니다(그의 덱 0/10).
- **GLITTERSTREAM·KNOW_THY_PLACE·GLOW**: Baalorlord의 거의 모든 리젠트 덱(7~8/9, 6/9)에 있지만, OP.GG에선 별 빌드 특유의 카드가 아닙니다. GLITTERSTREAM·GLOW는 생성 빌드에서 OP.GG 기준으로 support에 들어갔고, GLOW는 블랙홀 별 빌드 enabler입니다.
- **BLACK_HOLE**: 다른 랭커의 별 덱 9/12에 있고 Baalorlord도 4/9를 쓰지만, 1위 플레이어는 별 덱 10판에서 한 번도 쓰지 않았습니다(집음 2/9). 그래서 별 빌드를 둘로 나눴습니다.
- **"별 덱과 벼리기 덱을 섞지 말라"는 커뮤니티 조언**: SWORD_SAGE·SEEKING_EDGE 엔진은 별 엔진과 따로 쓰는 경향이 있지만, FURNACE·PARRY·BULWARK는 별 덱에도 흔히 들어갑니다.
