# 아이언클래드 빌드 카드 근거 (v2)

`data/packs/v0.107.1/archetypes/ironclad.json`의 core·enabler·payoff·support 목록과 유물·fullWeight가 어디서 왔는지 정리한 문서입니다. v1(OP.GG 승리 94판)을 같은 방법으로, 훨씬 큰 표본과 더 엄격한 기준으로 다시 계산했습니다.
역할 이름 대응: core = 핵심, enabler·payoff = 필수 시너지, support = 보조 시너지.

## 1. 표본

- **OP.GG 상위 플레이어 코퍼스**: 캐릭터별 주간 "최단 시간 승리" 상위 100(2026-11-02~08)에 오른 171명 전원의 전체 전적에서, 승천 10·일반 모드·싱글·바닐라(게임플레이 모드 없음)·v0.107.0 이상 승리를 모았습니다. 아이언클래드 승리 **683판·66명**(v0.107.x 270판, 베타 v0.108~v0.111 413판).
- **우수 플레이어**: A10 v0.107+ 5승 이상이고, 1막을 통과한 판 기준 승률 25% 이상인 플레이어(전체 27명). 아이언클래드 승리 **397판·18명**. 모든 결정은 우수 플레이어 기준이고, 전체 플레이어 값은 함께 보고합니다.
- **OP.GG 랭킹 1위 플레이어**는 아이언클래드 승리 79판(전체의 12%)이라 플레이어 균형으로 비중을 제한했습니다(아래).
- **Baalorlord**(A10 아이언클래드 11연승 기록): v1과 같은 보조 출처. 표의 Baalorlord 열은 그의 A10 승리 덱 11판 중 보유 수.
- 진 판(우수 플레이어의 1막 이후 패배)은 이 문서의 빌드 판정에 쓰지 않았습니다(빌드 판정은 승리 덱 기준).

## 2. 규칙 (다섯 캐릭터 공통, v2)

- **버전**: 베타에서 바뀐 카드(BLOODLETTING·COLOSSUS·CRIMSON_MANTLE·CRUELTY·DEMON_FORM·DOMINATE·EXPECT_A_FIGHT·FORGOTTEN_RITUAL·HOWL_FROM_BEYOND·MANGLE·PACTS_END·PRIMAL_FORCE·RAMPAGE·SETUP_STRIKE·TAUNT 등)는 v0.107.x 런에서만 셉니다(멤버십 계산과 카드 평가 모두). 나머지 카드는 v0.107~v0.111 런을 합칩니다.
- **멤버 덱**: 빌드 축 카드(anchor, v1과 같은 집합) 2장 이상을 가진 승리 덱(타격 자동 사용은 HELLRAISER 1장). 카드를 평가할 때 그 카드를 빼고 멤버십을 다시 셉니다(leave-one-out).
- **플레이어 균형**: 점유율·lift·픽률은 플레이어별 비율의 가중 평균이며, 한 플레이어 가중치는 전체의 10%를 넘지 못합니다. lift = (멤버 덱 균형 점유율) / (비멤버 덱 균형 점유율). 괄호 안은 원시값.
- **빌드 인정**: 멤버 덱 25판 이상·6명 이상·한 사람이 절반 이하. 1위 플레이어 빌드 예외는 그의 승리 15판 이상.
- **카드 지원**: 보유 플레이어 5명 이상, lift 1.5 이상, 균형 점유율 25% 이상(또는 15% 이상이면서 빌드 런의 카드 보상(전투 후 보상 화면만, 상점·이벤트 제외)에서 10번 이상 제시되어 균형 픽률 50% 이상).
- **core**: 지원 카드 중 고급·희귀이고 효과상 빌드 엔진(미리 정한 후보: 힘 DEMON_FORM·INFLAME·RUPTURE·FIGHT_ME·DOMINATE·BRAND, 소멸 FEEL_NO_PAIN·DARK_EMBRACE, 방어 BARRICADE·JUGGERNAUT·UNMOVABLE, 자해 RUPTURE·INFERNO·CRIMSON_MANTLE, 취약 VICIOUS·CRUELTY, 공격 재사용 STAMPEDE·AGGRESSION, 타격 템포 HELLRAISER·JUGGLING, 타격 자동 사용 HELLRAISER)이며 점유율 40% 이상·5명 이상. 최대 3장.
- **확실성(부트스트랩)**: 플레이어를 복원 추출해 200번 다시 계산합니다. 지원(또는 core) 판정이 80% 이상에서 유지되면 통과, 20% 이하이면 탈락. 그 사이면 우수 플레이어 표본으로는 결정할 수 없으므로 전체 플레이어 결과를 보고, 그것도 80%/20%를 넘지 못하면 **불확실**: v1 역할을 그대로 두고(새 카드는 추가하지 않음) 표시합니다.
- **보조 출처 구제**: OP.GG 기준을 통과하지 못한 카드라도 Baalorlord A10 덱 6/11 이상, 카드 효과가 빌드 축에 작용, 우수 플레이어 lift 1.0 이상이 부트스트랩 80% 이상 유지되면 enabler·payoff·support로 둡니다(core 불가).
- **역할**: 축을 공급하면 enabler, 쌓인 축을 소비·마무리하면 payoff, 그 밖은 support. v1에 있던 카드는 v1 역할을 유지했고, core에서 내려온 카드와 새 카드는 효과로 정했습니다.
- **유물(keyRelics)**: 멤버 덱 lift 1.5 이상·5명 이상이 부트스트랩 80% 이상 유지. 빌드마다 유물 100여 종을 시험하므로 우연히 통과하는 유물이 많아, 새로 넣는 유물은 효과가 빌드 축에 작용하는 것만 넣었습니다(가중치 0.4). v1 유물은 불확실이면 유지.
- **fullWeight**: v1과 같은 보정(우수 플레이어 승리 덱의 commit 중앙값, 플레이어 균형). 멤버 중앙값 0.7 미만이거나 비멤버 0.5 초과일 때만 멤버 중앙값 약 0.85가 되도록 0.5 단위, 최솟값 2.0. 부트스트랩의 80% 이상이 ±0.5 안이어야 바꿈.
- **coreTarget / essentialTarget**: 멤버 승리 덱이 가진 서로 다른 core 카드 수 / enabler·payoff 카드 수의 균형 중앙값. 부트스트랩 80% 이상 같은 값일 때만 씀. core 보유 중앙값은 모든 빌드에서 1이라 coreTarget은 기본값(1)입니다.

## 3. 빌드 요약

| 빌드 | 멤버 덱 (우수) | 플레이어 (우수) | 멤버 덱 (전체) | 플레이어 (전체) | 1위 덱 | v0.107.x (우수) | 빌드 기준 부트스트랩 | core | 카드 수 |
|---|---|---|---|---|---|---|---|---|---|
| 힘 성장 (`ironclad.strength`) | 94 | 14 (최대 26%) | 157 | 43 | 16 | 64 | 100% | 없음 | 2 |
| 소멸 (`ironclad.exhaust`) | 190 | 16 (최대 17%) | 327 | 47 | 32 | 81 | 100% | FEEL_NO_PAIN | 8 |
| 방어 누적 (`ironclad.block`) | 59 | 11 (최대 20%) | 115 | 34 | 12 | 22 | 98% | BARRICADE | 5 |
| 자해 (`ironclad.self_damage`) | 75 | 13 (최대 29%) | 126 | 35 | 22 | 42 | 100% | RUPTURE | 6 |
| 취약 (`ironclad.vulnerable`) | 136 | 17 (최대 24%) | 216 | 45 | 32 | 95 | 100% | 없음 | 6 |
| 공격 재사용 (`ironclad.attack_replay`) | 55 | 13 (최대 24%) | 108 | 39 | 7 | 25 | 100% | 없음 | 3 |
| 타격 템포 (`ironclad.strike_tempo`) | 79 | 15 (최대 25%) | 125 | 36 | 7 | 51 | 100% | 없음 | 6 |
| 타격 자동 사용 (`ironclad.hellraiser`) | 50 | 11 (최대 24%) | 72 | 26 | 11 | 31 | 98% | HELLRAISER | 4 |

여덟 빌드 모두 기준을 넘어 유지했습니다(삭제 없음). v1에서 1위 플레이어 예외로 들어왔던 타격 자동 사용도 이제 일반 기준(우수 50판·11명, 한 사람 최대 24%)을 넘습니다.

## 4. 빌드별 근거

### 4.1 힘 성장 (`ironclad.strength`)

축 카드: BRAND, DEMON_FORM, DOMINATE, FIGHT_ME, INFLAME, RUPTURE. 멤버 덱 우수 94판·14명(한 사람 최대 26%, 1위 16판, v0.107.x 64판) / 전체 157판·43명(v0.107.x 101판). 빌드 기준 부트스트랩 우수 100%·전체 100%.

**core: 없음** (v1: 없음).

| 카드 | 역할 | 판정 | 우수: 점유율 (k/N) | lift (원시) | 플레이어 | 빌드 내 픽 | 지원 부트 | core 부트 | 전체: 점유율 (k/N) | lift | 플레이어 | 지원 부트 | Baalorlord |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| CONFLAGRATION | payoff | 불확실(v1 유지) | 22% (23/94) | 1.95 (2.32) | 9 | 47% (17/34) | 43% | – | 23% (37/157) | 1.97 | 17 | 41% | 0 |
| BLOOD_WALL | support | OP.GG 우수 | 53% (50/94) | 1.82 (1.70) | 11 | 19% (37/201) | 93% | – | 52% (81/157) | 1.52 | 26 | 50% | 0 |

대표 런(우수 플레이어 멤버 덱, 서로 다른 플레이어): <https://op.gg/slay-the-spire2/history/3952049> · <https://op.gg/slay-the-spire2/history/5870693> · <https://op.gg/slay-the-spire2/history/6441784> · <https://op.gg/slay-the-spire2/history/6616541>

v1에서 뺀 카드(부트스트랩으로 탈락 확정):
- DARK_EMBRACE (v1 support): 우수 19%·lift 0.81·8명·지원 부트 2% / 전체 14%·lift 0.68·13명·지원 부트 0% → 우수 기준 탈락. Baalorlord 0/11
- FIGHT_ME (v1 enabler): 우수 13%·lift 0.70·5명·지원 부트 0% / 전체 13%·lift 0.79·10명·지원 부트 0% → 우수 기준 탈락. Baalorlord 5/11
- INFLAME (v1 enabler): 우수 20%·lift 0.89·6명·지원 부트 2% / 전체 16%·lift 0.86·12명·지원 부트 0% → 우수 기준 탈락. Baalorlord 0/11
- UNRELENTING (v1 support): 우수 16%·lift 1.41·8명·지원 부트 2% / 전체 19%·lift 1.21·21명·지원 부트 5% → 우수 기준 탈락. Baalorlord 6/11
- VICIOUS (v1 support): 우수 31%·lift 1.19·10명·지원 부트 11% / 전체 27%·lift 1.07·19명·지원 부트 0% → 우수 기준 탈락. Baalorlord 8/11

추가하지 않은 불확실 후보(점 추정은 통과하거나 근접했지만 부트스트랩 20~80%):
- BLOODLETTING: 우수 73%·lift 1.25·10명·지원 부트 21% / 전체 76%·lift 1.40·지원 부트 30%
- BREAKTHROUGH: 우수 28%·lift 1.77·8명·지원 부트 58% / 전체 29%·lift 1.59·지원 부트 50%
- INFERNO: 우수 25%·lift 1.41·9명·지원 부트 32% / 전체 27%·lift 1.53·지원 부트 35%
- SWORD_BOOMERANG: 우수 34%·lift 1.74·8명·지원 부트 68% / 전체 31%·lift 1.48·지원 부트 43%

유물: v1 없음 → v2 없음.
- 통계 기준은 넘었지만 효과가 빌드 축과 무관하거나 확인되지 않아 넣지 않은 유물: BURNING_STICKS, DRAGON_FRUIT, MINIATURE_CANNON

fullWeight 2.0 → 2.0: 새 목록 기준 멤버 덱 commit 중앙값 우수 0.25(원시 0.25)·비멤버 0.00 / 전체 0.25·0.00. 보정값 부트스트랩 ±0.5 안 100%. 적용 후 멤버 0.25·비멤버 0.00.
목표 수: core 보유 중앙값 – → coreTarget 기본값. enabler·payoff 보유 중앙값 우수 0 (부트 100%, 전체 0) → essentialTarget 설정 안 함.

### 4.2 소멸 (`ironclad.exhaust`)

축 카드: ASHEN_STRIKE, DARK_EMBRACE, DRUM_OF_BATTLE, EVIL_EYE, FEEL_NO_PAIN, FORGOTTEN_RITUAL, HOWL_FROM_BEYOND, PACTS_END. 멤버 덱 우수 190판·16명(한 사람 최대 17%, 1위 32판, v0.107.x 81판) / 전체 327판·47명(v0.107.x 115판). 빌드 기준 부트스트랩 우수 100%·전체 100%.

**core: FEEL_NO_PAIN** (v1: FEEL_NO_PAIN).

| 카드 | 역할 | 판정 | 우수: 점유율 (k/N) | lift (원시) | 플레이어 | 빌드 내 픽 | 지원 부트 | core 부트 | 전체: 점유율 (k/N) | lift | 플레이어 | 지원 부트 | Baalorlord |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| FEEL_NO_PAIN | core | OP.GG 전체(우수 미결), core 전체(우수 미결) | 58% (73/125) | 1.51 (1.44) | 13 | 62% (61/103) | 55% | 55% | 61% (133/219) | 1.61 | 35 | 80% | 6 |
| TRUE_GRIT | enabler | OP.GG 전체(우수 미결) | 57% (109/190) | 1.71 (1.65) | 12 | 25% (99/381) | 78% | – | 58% (191/327) | 1.77 | 39 | 99% | 4 |
| SECOND_WIND | enabler | OP.GG 전체(우수 미결) | 41% (79/190) | 1.60 (1.46) | 11 | 49% (66/139) | 60% | – | 46% (151/327) | 1.74 | 36 | 88% | 4 |
| STOKE | enabler | OP.GG 전체(우수 미결) | 29% (54/190) | 1.60 (1.37) | 12 | 67% (43/60) | 62% | – | 31% (102/327) | 1.84 | 30 | 88% | 0 |
| FORGOTTEN_RITUAL | payoff | OP.GG 우수 | 42% (23/75) | 3.44 (4.99) | 10 | 39% (17/65) | 92% | – | 37% (35/107) | 4.01 | 18 | 98% | 0 |
| DARK_EMBRACE | payoff | OP.GG 우수 | 31% (46/162) | 1.90 (1.63) | 13 | 68% (38/56) | 84% | 2% | 27% (77/290) | 2.07 | 28 | 92% | 0 |
| ASHEN_STRIKE | payoff | 불확실(v1 유지) | 30% (42/161) | 1.86 (1.71) | 12 | 32% (32/104) | 72% | – | 31% (84/273) | 1.64 | 31 | 73% | 3 |
| STONE_ARMOR | support | OP.GG 우수 | 30% (50/190) | 2.07 (1.88) | 12 | 31% (45/163) | 80% | – | 25% (83/327) | 1.27 | 27 | 10% | 4 |

대표 런(우수 플레이어 멤버 덱, 서로 다른 플레이어): <https://op.gg/slay-the-spire2/history/3724927> · <https://op.gg/slay-the-spire2/history/5870711> · <https://op.gg/slay-the-spire2/history/6868530> · <https://op.gg/slay-the-spire2/history/6696752>

v1에서 뺀 카드(부트스트랩으로 탈락 확정):
- BATTLE_TRANCE (v1 support): 우수 49%·lift 1.09·14명·지원 부트 2% / 전체 49%·lift 0.97·37명·지원 부트 0% → 우수 기준 탈락. Baalorlord 4/11
- BLOODLETTING (v1 support): 우수 68%·lift 1.10·11명·지원 부트 7% / 전체 70%·lift 1.19·25명·지원 부트 11% → 우수 기준 탈락. Baalorlord 7/11
- FIGHT_ME (v1 support): 우수 21%·lift 1.32·11명·지원 부트 20% / 전체 17%·lift 1.18·25명·지원 부트 0% → 우수 기준 탈락. Baalorlord 5/11
- HEADBUTT (v1 support): 우수 36%·lift 1.08·12명·지원 부트 4% / 전체 29%·lift 1.12·25명·지원 부트 2% → 우수 기준 탈락. Baalorlord 0/11
- TREMBLE (v1 enabler): 우수 63%·lift 1.28·15명·지원 부트 12% / 전체 59%·lift 1.20·42명·지원 부트 0% → 우수 기준 탈락. Baalorlord 4/11

추가하지 않은 불확실 후보(점 추정은 통과하거나 근접했지만 부트스트랩 20~80%):
- BURNING_PACT: 우수 45%·lift 1.36·15명·지원 부트 32% / 전체 48%·lift 1.52·지원 부트 56%
- DOMINATE: 우수 56%·lift 1.61·10명·지원 부트 62% / 전체 52%·lift 1.52·지원 부트 53%
- HAVOC: 우수 24%·lift 1.44·9명·지원 부트 30% / 전체 24%·lift 1.69·지원 부트 37%

유물: v1 CHARONS_ASHES, BURNING_STICKS, JOSS_PAPER, FORGOTTEN_SOUL, TOASTY_MITTENS → v2 CHARONS_ASHES (w 0.7), TOASTY_MITTENS (w 0.4).
- BURNING_STICKS: 우수 기준 근처 아님 / 전체 기준 근처 아님 → 제거
- CHARONS_ASHES: 우수 lift 1.63·8명·부트 58% / 전체 lift 1.52·15명·부트 56% → 유지 (불확실)
- FORGOTTEN_SOUL: 우수 기준 근처 아님 / 전체 기준 근처 아님 → 제거
- JOSS_PAPER: 우수 기준 근처 아님 / 전체 기준 근처 아님 → 제거
- TOASTY_MITTENS: 우수 lift 3.25·7명·부트 76% / 전체 lift 3.67·15명·부트 99% → 유지
- 통계 기준은 넘었지만 효과가 빌드 축과 무관하거나 확인되지 않아 넣지 않은 유물: ART_OF_WAR, BIIIG_HUG, DUSTY_TOME, PANDORAS_BOX, SHURIKEN, TOOLBOX

fullWeight 4.5 → 4.5 (불확실, v1 유지): 새 목록 기준 멤버 덱 commit 중앙값 우수 0.71(원시 0.69)·비멤버 0.23 / 전체 0.73·0.27. 보정값 부트스트랩 ±0.5 안 53%. 적용 후 멤버 0.71·비멤버 0.23.
목표 수: core 보유 중앙값 1 (부트 100%) → coreTarget 기본값. enabler·payoff 보유 중앙값 우수 2 (부트 97%, 전체 2) → essentialTarget 2.

### 4.3 방어 누적 (`ironclad.block`)

축 카드: BARRICADE, BODY_SLAM, JUGGERNAUT, UNMOVABLE. 멤버 덱 우수 59판·11명(한 사람 최대 20%, 1위 12판, v0.107.x 22판) / 전체 115판·34명(v0.107.x 38판). 빌드 기준 부트스트랩 우수 98%·전체 100%.

**core: BARRICADE** (v1: 없음).

| 카드 | 역할 | 판정 | 우수: 점유율 (k/N) | lift (원시) | 플레이어 | 빌드 내 픽 | 지원 부트 | core 부트 | 전체: 점유율 (k/N) | lift | 플레이어 | 지원 부트 | Baalorlord |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BARRICADE | core | OP.GG 우수, core 우수 | 62% (15/30) | 4.34 (3.40) | 8 | 90% (13/15) | 93% | 91% | 47% (31/66) | 3.16 | 18 | 100% | 3 |
| SHRUG_IT_OFF | enabler | Baalorlord 보조 출처 | 73% (42/59) | 1.45 (1.48) | 10 | 33% (35/115) | 45% | – | 68% (78/115) | 1.33 | 28 | 6% | 6 |
| ARMAMENTS | enabler | 불확실(v1 유지) | 41% (23/59) | 1.83 (1.57) | 9 | 14% (17/129) | 78% | – | 37% (43/115) | 1.40 | 22 | 23% | 3 |
| IMPERVIOUS | enabler | 불확실(v1 유지) | 23% (12/59) | 1.43 (1.30) | 7 | 34% (8/21) | 30% | – | 22% (25/115) | 1.49 | 16 | 33% | 3 |
| BODY_SLAM | payoff | OP.GG 우수 | 61% (15/25) | 3.62 (3.23) | 8 | 33% (17/54) | 93% | – | 63% (32/51) | 3.20 | 19 | 100% | 0 |

대표 런(우수 플레이어 멤버 덱, 서로 다른 플레이어): <https://op.gg/slay-the-spire2/history/6176418> · <https://op.gg/slay-the-spire2/history/6695107> · <https://op.gg/slay-the-spire2/history/5870896> · <https://op.gg/slay-the-spire2/history/6222079>

v1에서 뺀 카드(부트스트랩으로 탈락 확정):
- BLOOD_WALL (v1 enabler): 우수 34%·lift 0.96·7명·지원 부트 7% / 전체 47%·lift 1.27·23명·지원 부트 8% → 우수 기준 탈락. Baalorlord 0/11
- OFFERING (v1 support): 우수 40%·lift 1.33·10명·지원 부트 36% / 전체 31%·lift 0.93·20명·지원 부트 0% → 전체 기준 탈락. Baalorlord 5/11
- PYRE (v1 support): 우수 21%·lift 1.08·6명·지원 부트 16% / 전체 19%·lift 1.14·13명·지원 부트 12% → 우수 기준 탈락. Baalorlord 0/11
- TAUNT (v1 enabler): 우수 44%·lift 1.45·4명·지원 부트 22% / 전체 33%·lift 1.04·6명·지원 부트 3% → 전체 기준 탈락. Baalorlord 10/11

추가하지 않은 불확실 후보(점 추정은 통과하거나 근접했지만 부트스트랩 20~80%):
- UNMOVABLE: 우수 27%·lift 1.54·6명·지원 부트 38% / 전체 31%·lift 1.59·지원 부트 64%

유물: v1 ANCHOR → v2 없음.
- ANCHOR: 우수 기준 근처 아님 / 전체 lift 1.31·19명·부트 14% → 제거
- 통계 기준은 넘었지만 효과가 빌드 축과 무관하거나 확인되지 않아 넣지 않은 유물: ASTROLABE, FRESNEL_LENS, SERE_TALON

fullWeight 2.5 → 2.5: 새 목록 기준 멤버 덱 commit 중앙값 우수 1.00(원시 1.00)·비멤버 0.28 / 전체 1.00·0.28. 보정값 부트스트랩 ±0.5 안 100%. 적용 후 멤버 1.00·비멤버 0.28.
목표 수: core 보유 중앙값 1 (부트 100%) → coreTarget 기본값. enabler·payoff 보유 중앙값 우수 2 (부트 96%, 전체 2) → essentialTarget 2.

### 4.4 자해 (`ironclad.self_damage`)

축 카드: INFERNO, RUPTURE, SPITE, TEAR_ASUNDER. 멤버 덱 우수 75판·13명(한 사람 최대 29%, 1위 22판, v0.107.x 42판) / 전체 126판·35명(v0.107.x 59판). 빌드 기준 부트스트랩 우수 100%·전체 100%.

**core: RUPTURE** (v1: INFERNO).

| 카드 | 역할 | 판정 | 우수: 점유율 (k/N) | lift (원시) | 플레이어 | 빌드 내 픽 | 지원 부트 | core 부트 | 전체: 점유율 (k/N) | lift | 플레이어 | 지원 부트 | Baalorlord |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| RUPTURE | core | OP.GG 우수, core 전체(우수 미결) | 54% (17/35) | 2.77 (2.20) | 7 | 59% (15/25) | 86% | 78% | 48% (26/55) | 2.27 | 14 | 97% | 0 |
| BLOODLETTING | enabler | Baalorlord 보조 출처 | 84% (35/42) | 1.42 (1.30) | 7 | 42% (34/90) | 36% | – | 88% (51/59) | 1.55 | 16 | 63% | 7 |
| BLOOD_WALL | enabler | OP.GG 우수 | 63% (45/75) | 2.10 (1.93) | 11 | 24% (39/153) | 98% | – | 61% (76/126) | 1.80 | 24 | 92% | 0 |
| BREAKTHROUGH | enabler | OP.GG 우수 | 39% (30/75) | 2.70 (2.86) | 9 | 14% (24/124) | 92% | – | 46% (58/126) | 3.01 | 25 | 100% | 0 |
| INFERNO | payoff | OP.GG 전체(우수 미결) | 29% (15/36) | 1.69 (2.28) | 6 | 48% (11/27) | 60% | 16% | 40% (25/62) | 2.28 | 14 | 96% | 0 |
| TEAR_ASUNDER | payoff | 불확실(v1 유지) | 25% (13/54) | 2.46 (2.23) | 6 | 53% (11/23) | 51% | – | 22% (20/87) | 2.04 | 13 | 72% | 0 |

대표 런(우수 플레이어 멤버 덱, 서로 다른 플레이어): <https://op.gg/slay-the-spire2/history/7045157> · <https://op.gg/slay-the-spire2/history/4062736> · <https://op.gg/slay-the-spire2/history/7060442> · <https://op.gg/slay-the-spire2/history/4211860>

v1에서 뺀 카드(부트스트랩으로 탈락 확정):
- BATTLE_TRANCE (v1 support): 우수 53%·lift 1.14·12명·지원 부트 10% / 전체 52%·lift 1.06·27명·지원 부트 0% → 우수 기준 탈락. Baalorlord 4/11
- BRAND (v1 enabler): 우수 16%·lift 1.17·7명·지원 부트 13% / 전체 25%·lift 2.04·19명·지원 부트 64% → 우수 기준 탈락. Baalorlord 3/11
- CONFLAGRATION (v1 support): 우수 22%·lift 1.64·8명·지원 부트 46% / 전체 17%·lift 1.26·13명·지원 부트 3% → 전체 기준 탈락. Baalorlord 0/11
- FIEND_FIRE (v1 support): 우수 14%·lift 1.21·6명·지원 부트 4% / 전체 13%·lift 1.23·11명·지원 부트 1% → 우수 기준 탈락. Baalorlord 0/11
- FLAME_BARRIER (v1 support): 우수 27%·lift 1.11·8명·지원 부트 16% / 전체 26%·lift 1.15·16명·지원 부트 8% → 우수 기준 탈락. Baalorlord 0/11
- UNMOVABLE (v1 support): 우수 18%·lift 1.05·6명·지원 부트 13% / 전체 27%·lift 1.38·18명·지원 부트 32% → 우수 기준 탈락. Baalorlord 0/11
- UNRELENTING (v1 support): 우수 15%·lift 1.29·5명·지원 부트 12% / 전체 16%·lift 0.94·14명·지원 부트 0% → 우수 기준 탈락. Baalorlord 6/11
- UPPERCUT (v1 support): 우수 32%·lift 1.26·7명·지원 부트 23% / 전체 25%·lift 1.02·16명·지원 부트 0% → 전체 기준 탈락. Baalorlord 5/11

추가하지 않은 불확실 후보(점 추정은 통과하거나 근접했지만 부트스트랩 20~80%):
- CRIMSON_MANTLE: 우수 17%·lift 1.53·4명·지원 부트 30% / 전체 22%·lift 1.90·지원 부트 60%
- NOT_YET: 우수 26%·lift 2.24·8명·지원 부트 61% / 전체 20%·lift 1.58·지원 부트 36%
- RAGE: 우수 28%·lift 1.41·9명·지원 부트 34% / 전체 23%·lift 1.26·지원 부트 22%
- SWORD_BOOMERANG: 우수 33%·lift 1.53·7명·지원 부트 43% / 전체 30%·lift 1.32·지원 부트 23%

유물: v1 없음 → v2 SELF_FORMING_CLAY (w 0.4).
- SELF_FORMING_CLAY: 우수 lift 1.68·10명·부트 60% / 전체 lift 1.93·21명·부트 88% → 추가
- 통계 기준은 넘었지만 효과가 빌드 축과 무관하거나 확인되지 않아 넣지 않은 유물: RAZOR_TOOTH

fullWeight 3.5 → 3.5: 새 목록 기준 멤버 덱 commit 중앙값 우수 0.94(원시 0.94)·비멤버 0.20 / 전체 0.94·0.20. 보정값 부트스트랩 ±0.5 안 100%. 적용 후 멤버 0.94·비멤버 0.20.
목표 수: core 보유 중앙값 1 (부트 98%) → coreTarget 기본값. enabler·payoff 보유 중앙값 우수 3 (부트 79%, 전체 3) → essentialTarget 설정 안 함.

### 4.5 취약 (`ironclad.vulnerable`)

축 카드: BULLY, COLOSSUS, CRUELTY, DISMANTLE, DOMINATE, MOLTEN_FIST, VICIOUS. 멤버 덱 우수 136판·17명(한 사람 최대 24%, 1위 32판, v0.107.x 95판) / 전체 216판·45명(v0.107.x 144판). 빌드 기준 부트스트랩 우수 100%·전체 100%.

**core: 없음** (v1: 없음).

| 카드 | 역할 | 판정 | 우수: 점유율 (k/N) | lift (원시) | 플레이어 | 빌드 내 픽 | 지원 부트 | core 부트 | 전체: 점유율 (k/N) | lift | 플레이어 | 지원 부트 | Baalorlord |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| TREMBLE | enabler | 불확실(v1 유지) | 69% (85/136) | 1.43 (1.36) | 16 | 35% (90/263) | 43% | – | 67% (140/216) | 1.39 | 39 | 20% | 4 |
| TAUNT | enabler | OP.GG 우수 | 43% (42/95) | 2.07 (1.81) | 12 | 50% (36/65) | 83% | – | 40% (60/144) | 1.97 | 28 | 86% | 10 |
| MOLTEN_FIST | payoff | OP.GG 우수 | 43% (45/106) | 2.60 (2.57) | 13 | 31% (56/200) | 100% | – | 46% (73/162) | 2.16 | 27 | 99% | 0 |
| COLOSSUS | payoff | OP.GG 우수 | 42% (33/83) | 1.99 (2.01) | 10 | 40% (23/47) | 84% | – | 46% (54/123) | 1.78 | 24 | 80% | 0 |
| VICIOUS | payoff | Baalorlord 보조 출처 | 39% (41/100) | 1.56 (1.60) | 12 | 33% (31/81) | 64% | 46% | 36% (58/159) | 1.56 | 23 | 61% | 8 |
| CRUELTY | payoff | OP.GG 우수 | 37% (23/86) | 2.81 (3.06) | 12 | 73% (21/33) | 94% | 30% | 24% (30/132) | 1.97 | 18 | 73% | 8 |

대표 런(우수 플레이어 멤버 덱, 서로 다른 플레이어): <https://op.gg/slay-the-spire2/history/3977656> · <https://op.gg/slay-the-spire2/history/4038599> · <https://op.gg/slay-the-spire2/history/7052699> · <https://op.gg/slay-the-spire2/history/7075134>

v1에서 뺀 카드(부트스트랩으로 탈락 확정):
- FIGHT_ME (v1 support): 우수 18%·lift 1.01·9명·지원 부트 0% / 전체 17%·lift 1.11·20명·지원 부트 0% → 우수 기준 탈락. Baalorlord 5/11
- FLAME_BARRIER (v1 support): 우수 24%·lift 0.92·10명·지원 부트 0% / 전체 20%·lift 0.81·23명·지원 부트 0% → 우수 기준 탈락. Baalorlord 0/11
- HEADBUTT (v1 support): 우수 33%·lift 0.99·12명·지원 부트 2% / 전체 27%·lift 0.98·19명·지원 부트 2% → 우수 기준 탈락. Baalorlord 0/11
- SETUP_STRIKE (v1 support): 우수 13%·lift 0.46·5명·지원 부트 0% / 전체 17%·lift 0.83·11명·지원 부트 0% → 우수 기준 탈락. Baalorlord 0/11
- UNMOVABLE (v1 support): 우수 20%·lift 1.24·12명·지원 부트 4% / 전체 23%·lift 1.11·29명·지원 부트 2% → 우수 기준 탈락. Baalorlord 0/11

추가하지 않은 불확실 후보(점 추정은 통과하거나 근접했지만 부트스트랩 20~80%):
- BULLY: 우수 27%·lift 2.88·12명·지원 부트 70% / 전체 25%·lift 3.12·지원 부트 46%
- DISMANTLE: 우수 21%·lift 1.75·10명·지원 부트 20% / 전체 22%·lift 2.08·지원 부트 28%
- DOMINATE: 우수 55%·lift 1.48·9명·지원 부트 52% / 전체 52%·lift 1.47·지원 부트 44%
- SWORD_BOOMERANG: 우수 28%·lift 1.36·9명·지원 부트 32% / 전체 29%·lift 1.35·지원 부트 22%
- THUNDERCLAP: 우수 23%·lift 2.84·10명·지원 부트 39% / 전체 27%·lift 2.88·지원 부트 59%

유물: v1 HAND_DRILL → v2 없음.
- HAND_DRILL: 우수 기준 근처 아님 / 전체 기준 근처 아님 → 제거
- 통계 기준은 넘었지만 효과가 빌드 축과 무관하거나 확인되지 않아 넣지 않은 유물: CHOICES_PARADOX, WINGED_BOOTS

fullWeight 3.5 → 3.5: 새 목록 기준 멤버 덱 commit 중앙값 우수 0.71(원시 0.71)·비멤버 0.40 / 전체 0.71·0.40. 보정값 부트스트랩 ±0.5 안 100%. 적용 후 멤버 0.71·비멤버 0.40.
목표 수: core 보유 중앙값 – → coreTarget 기본값. enabler·payoff 보유 중앙값 우수 3 (부트 61%, 전체 3) → essentialTarget 설정 안 함.

### 4.6 공격 재사용 (`ironclad.attack_replay`)

축 카드: AGGRESSION, BLUDGEON, MANGLE, STAMPEDE, THRASH. 멤버 덱 우수 55판·13명(한 사람 최대 24%, 1위 7판, v0.107.x 25판) / 전체 108판·39명(v0.107.x 41판). 빌드 기준 부트스트랩 우수 100%·전체 100%.

**core: 없음** (v1: 없음).

| 카드 | 역할 | 판정 | 우수: 점유율 (k/N) | lift (원시) | 플레이어 | 빌드 내 픽 | 지원 부트 | core 부트 | 전체: 점유율 (k/N) | lift | 플레이어 | 지원 부트 | Baalorlord |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| STAMPEDE | enabler | OP.GG 전체(우수 미결) | 22% (9/31) | 1.42 (1.86) | 4 | 20% (4/20) | 26% | 4% | 36% (22/60) | 1.89 | 13 | 86% | 0 |
| THRASH | payoff | OP.GG 전체(우수 미결) | 23% (7/34) | 1.74 (1.56) | 5 | 53% (4/9) | 35% | – | 29% (20/70) | 2.26 | 15 | 82% | 0 |
| PYRE | support | 불확실(v1 유지) | 31% (16/55) | 1.79 (1.66) | 8 | 45% (12/22) | 68% | – | 26% (28/108) | 1.70 | 17 | 59% | 0 |

대표 런(우수 플레이어 멤버 덱, 서로 다른 플레이어): <https://op.gg/slay-the-spire2/history/5551713> · <https://op.gg/slay-the-spire2/history/4463429> · <https://op.gg/slay-the-spire2/history/6159797> · <https://op.gg/slay-the-spire2/history/6903808>

v1에서 뺀 카드(부트스트랩으로 탈락 확정):
- BLUDGEON (v1 payoff): 우수 21%·lift 1.88·5명·지원 부트 18% / 전체 30%·lift 2.14·14명·지원 부트 78% → 우수 기준 탈락. Baalorlord 0/11
- BREAK (v1 support): 우수 13%·lift 0.97·5명·지원 부트 7% / 전체 15%·lift 1.34·11명·지원 부트 0% → 우수 기준 탈락. Baalorlord 0/11
- FLAME_BARRIER (v1 support): 우수 34%·lift 1.47·10명·지원 부트 39% / 전체 30%·lift 1.32·21명·지원 부트 17% → 전체 기준 탈락. Baalorlord 0/11
- INFLAME (v1 support): 우수 21%·lift 0.95·7명·지원 부트 6% / 전체 22%·lift 1.20·18명·지원 부트 12% → 우수 기준 탈락. Baalorlord 0/11
- JUGGLING (v1 enabler): 우수 20%·lift 1.34·7명·지원 부트 12% / 전체 15%·lift 1.26·13명·지원 부트 0% → 우수 기준 탈락. Baalorlord 0/11
- UNRELENTING (v1 support): 우수 12%·lift 1.06·5명·지원 부트 2% / 전체 19%·lift 1.19·12명·지원 부트 4% → 우수 기준 탈락. Baalorlord 6/11
- VICIOUS (v1 support): 우수 31%·lift 1.14·9명·지원 부트 10% / 전체 28%·lift 1.10·20명·지원 부트 3% → 우수 기준 탈락. Baalorlord 8/11

유물: v1 없음 → v2 없음.
- 통계 기준은 넘었지만 효과가 빌드 축과 무관하거나 확인되지 않아 넣지 않은 유물: CHANDELIER, GAME_PIECE, JEWELRY_BOX, TRI_BOOMERANG, WHITE_STAR

fullWeight 4.0 → 2.0: 새 목록 기준 멤버 덱 commit 중앙값 우수 0.26(원시 0.23)·비멤버 0.00 / 전체 0.26·0.00. 보정값 부트스트랩 ±0.5 안 100%. 적용 후 멤버 0.52·비멤버 0.00.
목표 수: core 보유 중앙값 – → coreTarget 기본값. enabler·payoff 보유 중앙값 우수 1 (부트 100%, 전체 1) → essentialTarget 1.

### 4.7 타격 템포 (`ironclad.strike_tempo`)

축 카드: ANGER, HELLRAISER, JUGGLING, PERFECTED_STRIKE, SETUP_STRIKE, TWIN_STRIKE. 멤버 덱 우수 79판·15명(한 사람 최대 25%, 1위 7판, v0.107.x 51판) / 전체 125판·36명(v0.107.x 73판). 빌드 기준 부트스트랩 우수 100%·전체 100%.

**core: 없음** (v1: 없음).

| 카드 | 역할 | 판정 | 우수: 점유율 (k/N) | lift (원시) | 플레이어 | 빌드 내 픽 | 지원 부트 | core 부트 | 전체: 점유율 (k/N) | lift | 플레이어 | 지원 부트 | Baalorlord |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| POMMEL_STRIKE | enabler | Baalorlord 보조 출처 | 74% (62/79) | 1.35 (1.36) | 14 | 53% (89/163) | 18% | – | 74% (94/125) | 1.43 | 35 | 26% | 8 |
| SETUP_STRIKE | enabler | OP.GG 우수 | 40% (19/44) | 4.29 (6.96) | 7 | 21% (17/70) | 84% | – | 38% (26/64) | 3.86 | 13 | 95% | 0 |
| TWIN_STRIKE | enabler | OP.GG 전체(우수 미결) | 29% (19/55) | 1.87 (2.51) | 7 | 21% (26/111) | 60% | – | 38% (33/86) | 2.87 | 17 | 96% | 5 |
| PERFECTED_STRIKE | payoff | OP.GG 우수 | 37% (20/49) | 2.36 (2.90) | 8 | 27% (33/98) | 92% | – | 39% (32/80) | 2.77 | 16 | 98% | 6 |
| ARMAMENTS | support | OP.GG 우수 | 40% (26/79) | 1.90 (1.29) | 12 | 14% (21/171) | 82% | – | 38% (45/125) | 1.41 | 25 | 46% | 3 |
| RAGE | support | 불확실(v1 유지) | 27% (25/79) | 1.47 (1.77) | 11 | 40% (22/50) | 38% | – | 28% (37/125) | 1.74 | 18 | 66% | 5 |

대표 런(우수 플레이어 멤버 덱, 서로 다른 플레이어): <https://op.gg/slay-the-spire2/history/6176418> · <https://op.gg/slay-the-spire2/history/6558157> · <https://op.gg/slay-the-spire2/history/7060474> · <https://op.gg/slay-the-spire2/history/7121129>

v1에서 뺀 카드(부트스트랩으로 탈락 확정):
- BLUDGEON (v1 support): 우수 5%·lift 0.34·2명·지원 부트 0% / 전체 10%·lift 0.61·9명·지원 부트 0% → 우수 기준 탈락. Baalorlord 0/11
- BODY_SLAM (v1 support): 우수 15%·lift 0.76·7명·지원 부트 0% / 전체 14%·lift 0.58·13명·지원 부트 0% → 우수 기준 탈락. Baalorlord 0/11
- BREAKTHROUGH (v1 support): 우수 14%·lift 0.71·5명·지원 부트 0% / 전체 22%·lift 1.06·15명·지원 부트 4% → 우수 기준 탈락. Baalorlord 0/11
- COLOSSUS (v1 support): 우수 29%·lift 0.96·6명·지원 부트 8% / 전체 32%·lift 0.90·12명·지원 부트 0% → 우수 기준 탈락. Baalorlord 0/11
- CONFLAGRATION (v1 support): 우수 9%·lift 0.60·5명·지원 부트 0% / 전체 14%·lift 0.93·12명·지원 부트 0% → 우수 기준 탈락. Baalorlord 0/11
- DRUM_OF_BATTLE (v1 support): 우수 12%·lift 0.65·5명·지원 부트 2% / 전체 15%·lift 0.72·11명·지원 부트 0% → 우수 기준 탈락. Baalorlord 3/11
- EVIL_EYE (v1 support): 우수 26%·lift 0.76·6명·지원 부트 0% / 전체 32%·lift 0.95·16명·지원 부트 0% → 우수 기준 탈락. Baalorlord 4/11
- HAVOC (v1 support): 우수 29%·lift 1.51·8명·지원 부트 33% / 전체 22%·lift 1.21·16명·지원 부트 8% → 전체 기준 탈락. Baalorlord 0/11
- HEADBUTT (v1 support): 우수 35%·lift 1.03·8명·지원 부트 2% / 전체 26%·lift 0.95·11명·지원 부트 0% → 우수 기준 탈락. Baalorlord 0/11
- INFLAME (v1 support): 우수 23%·lift 1.07·7명·지원 부트 6% / 전체 22%·lift 1.22·15명·지원 부트 8% → 우수 기준 탈락. Baalorlord 0/11
- JUGGLING (v1 support): 우수 18%·lift 1.22·5명·지원 부트 12% / 전체 21%·lift 1.92·11명·지원 부트 20% → 우수 기준 탈락. Baalorlord 0/11
- MOLTEN_FIST (v1 support): 우수 20%·lift 0.82·7명·지원 부트 0% / 전체 24%·lift 0.88·13명·지원 부트 0% → 우수 기준 탈락. Baalorlord 0/11
- OFFERING (v1 support): 우수 35%·lift 1.15·10명·지원 부트 9% / 전체 37%·lift 1.15·23명·지원 부트 4% → 우수 기준 탈락. Baalorlord 5/11
- UNRELENTING (v1 support): 우수 14%·lift 1.21·7명·지원 부트 1% / 전체 19%·lift 1.18·16명·지원 부트 4% → 우수 기준 탈락. Baalorlord 6/11
- UPPERCUT (v1 support): 우수 23%·lift 0.85·8명·지원 부트 0% / 전체 23%·lift 0.91·17명·지원 부트 0% → 우수 기준 탈락. Baalorlord 5/11

추가하지 않은 불확실 후보(점 추정은 통과하거나 근접했지만 부트스트랩 20~80%):
- CRUELTY: 우수 32%·lift 1.74·6명·지원 부트 46% / 전체 20%·lift 1.27·지원 부트 28%

유물: v1 없음 → v2 FAKE_STRIKE_DUMMY (w 0.4), STRIKE_DUMMY (w 0.4).
- FAKE_STRIKE_DUMMY: 우수 lift 2.89·5명·부트 62% / 전체 lift 2.70·7명·부트 81% → 추가
- STRIKE_DUMMY: 우수 lift 1.63·12명·부트 70% / 전체 lift 1.83·24명·부트 92% → 추가
- 통계 기준은 넘었지만 효과가 빌드 축과 무관하거나 확인되지 않아 넣지 않은 유물: ART_OF_WAR, BRIMSTONE, GORGET, LARGE_CAPSULE, LIZARD_TAIL, MOLTEN_EGG, UNSETTLING_LAMP, VEXING_PUZZLEBOX

fullWeight 6.5 → 3.0: 새 목록 기준 멤버 덱 commit 중앙값 우수 0.41(원시 0.42)·비멤버 0.15 / 전체 0.42·0.12. 보정값 부트스트랩 ±0.5 안 98%. 적용 후 멤버 0.88·비멤버 0.33.
목표 수: core 보유 중앙값 – → coreTarget 기본값. enabler·payoff 보유 중앙값 우수 2 (부트 96%, 전체 2) → essentialTarget 2.

### 4.8 타격 자동 사용 (`ironclad.hellraiser`)

축 카드: HELLRAISER. 멤버 덱 우수 50판·11명(한 사람 최대 24%, 1위 11판, v0.107.x 31판) / 전체 72판·26명(v0.107.x 45판). 빌드 기준 부트스트랩 우수 98%·전체 100%.

**core: HELLRAISER** (v1: HELLRAISER).

| 카드 | 역할 | 판정 | 우수: 점유율 (k/N) | lift (원시) | 플레이어 | 빌드 내 픽 | 지원 부트 | core 부트 | 전체: 점유율 (k/N) | lift | 플레이어 | 지원 부트 | Baalorlord |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| HELLRAISER | core | OP.GG 우수, core 우수 | 100% (50/50) | ∞ (∞) | 11 | 82% (38/46) | 100% | 100% | 100% (72/72) | ∞ | 26 | 100% | 0 |
| POMMEL_STRIKE | enabler | Baalorlord 보조 출처 | 81% (41/50) | 1.44 (1.39) | 10 | 69% (75/98) | 46% | – | 79% (57/72) | 1.47 | 22 | 40% | 8 |
| TWIN_STRIKE | enabler | OP.GG 전체(우수 미결) | 28% (9/50) | 1.55 (1.10) | 7 | 15% (9/85) | 40% | – | 33% (21/72) | 2.21 | 17 | 86% | 5 |
| PERFECTED_STRIKE | payoff | OP.GG 우수 | 59% (19/50) | 3.84 (2.64) | 10 | 41% (33/99) | 98% | – | 55% (35/72) | 4.16 | 22 | 100% | 6 |

대표 런(우수 플레이어 멤버 덱, 서로 다른 플레이어): <https://op.gg/slay-the-spire2/history/3724927> · <https://op.gg/slay-the-spire2/history/6176418> · <https://op.gg/slay-the-spire2/history/7052680> · <https://op.gg/slay-the-spire2/history/6661658>

v1에서 뺀 카드(부트스트랩으로 탈락 확정):
- ASHEN_STRIKE (v1 enabler): 우수 28%·lift 1.35·6명·지원 부트 33% / 전체 24%·lift 1.02·12명·지원 부트 5% → 전체 기준 탈락. Baalorlord 3/11
- BLOODLETTING (v1 support): 우수 58%·lift 0.89·4명·지원 부트 0% / 전체 63%·lift 0.96·10명·지원 부트 1% → 우수 기준 탈락. Baalorlord 7/11
- BURNING_PACT (v1 enabler): 우수 42%·lift 1.06·7명·지원 부트 8% / 전체 40%·lift 1.01·14명·지원 부트 2% → 우수 기준 탈락. Baalorlord 4/11
- FLAME_BARRIER (v1 support): 우수 22%·lift 0.86·6명·지원 부트 6% / 전체 22%·lift 0.92·12명·지원 부트 4% → 우수 기준 탈락. Baalorlord 0/11
- PYRE (v1 support): 우수 22%·lift 1.08·5명·지원 부트 15% / 전체 16%·lift 0.89·6명·지원 부트 1% → 우수 기준 탈락. Baalorlord 0/11

추가하지 않은 불확실 후보(점 추정은 통과하거나 근접했지만 부트스트랩 20~80%):
- ARMAMENTS: 우수 43%·lift 1.84·8명·지원 부트 66% / 전체 38%·lift 1.37·지원 부트 36%
- PILLAGE: 우수 33%·lift 2.75·7명·지원 부트 75% / 전체 25%·lift 2.27·지원 부트 48%
- SETUP_STRIKE: 우수 30%·lift 1.68·4명·지원 부트 28% / 전체 35%·lift 2.12·지원 부트 73%
- STAMPEDE: 우수 25%·lift 1.56·7명·지원 부트 33% / 전체 32%·lift 1.60·지원 부트 56%

유물: v1 없음 → v2 없음.
- 통계 기준은 넘었지만 효과가 빌드 축과 무관하거나 확인되지 않아 넣지 않은 유물: MOLTEN_EGG, PARRYING_SHIELD, YUMMY_COOKIE

fullWeight 2.5 → 2.5: 새 목록 기준 멤버 덱 commit 중앙값 우수 1.00(원시 0.82)·비멤버 0.28 / 전체 1.00·0.28. 보정값 부트스트랩 ±0.5 안 100%. 적용 후 멤버 1.00·비멤버 0.28.
목표 수: core 보유 중앙값 1 (부트 100%) → coreTarget 기본값. enabler·payoff 보유 중앙값 우수 2 (부트 72%, 전체 2) → essentialTarget 설정 안 함.

## 5. 추가·삭제한 빌드

- **삭제 없음.** 여덟 빌드 모두 멤버 덱 50판 이상·11명 이상(우수 플레이어)입니다.
- **추가 없음.** 카드 동시 사용 lift로 찾은 후보 묶음은 모두 멤버 덱의 90% 이상이 기존 빌드 멤버라 별도 빌드가 아닙니다(우수 플레이어 승리 덱):
  - 요새(BARRICADE·DEMON_FORM·EXPECT_A_FIGHT·BODY_SLAM): 44판·10명, 기존 빌드 멤버 43판(98%), 가장 많이 겹치는 빌드 `ironclad.block` 36판
  - 의식 에너지(FORGOTTEN_RITUAL·DRUM_OF_BATTLE·EXPECT_A_FIGHT·PRIMAL_FORCE): 5판·5명, 기존 빌드 멤버 5판(100%), 가장 많이 겹치는 빌드 `ironclad.exhaust` 5판
  - 도발 힘(FIGHT_ME·EXPECT_A_FIGHT·CONFLAGRATION·DEMON_FORM): 28판·10명, 기존 빌드 멤버 26판(93%), 가장 많이 겹치는 빌드 `ironclad.strength` 17판
  - 0코스트 공격(INFERNAL_BLADE·RAGE·SETUP_STRIKE·JUGGLING): 50판·12명, 기존 빌드 멤버 48판(96%), 가장 많이 겹치는 빌드 `ironclad.strike_tempo` 28판
  - 약탈 타격(PILLAGE·HELLRAISER·PERFECTED_STRIKE): 36판·12명, 기존 빌드 멤버 36판(100%), 가장 많이 겹치는 빌드 `ironclad.hellraiser` 30판
  - 소멸 공격(ASHEN_STRIKE·HOWL_FROM_BEYOND·FIEND_FIRE·STOKE·CINDER): 62판·14명, 기존 빌드 멤버 61판(98%), 가장 많이 겹치는 빌드 `ironclad.exhaust` 53판
  - 진홍 망토 자해(CRIMSON_MANTLE·RUPTURE·INFERNO·BRAND): 69판·12명, 기존 빌드 멤버 69판(100%), 가장 많이 겹치는 빌드 `ironclad.self_damage` 49판

## 6. 해석과 남은 문제

- **힘 성장**: 힘 축 카드(DEMON_FORM·INFLAME·FIGHT_ME·RUPTURE·BRAND·DOMINATE)는 서로 함께 쓰이지 않습니다. 축 카드 자신은 leave-one-out lift가 0.7~1.3이고, 남은 카드는 BLOOD_WALL(지원 확정)과 CONFLAGRATION(불확실)뿐입니다. 상위 플레이어에게 "힘 빌드"는 독립된 덱이라기보다 다른 빌드에 힘 카드 한두 장을 얹는 형태입니다. 멤버 덱이 94판이라 삭제 기준(10판 미만)에는 해당하지 않아 유지했고, 카드 2장이라 검증기가 경고합니다.
- **방어 누적**: v1에서는 BARRICADE 엔진이 근거가 없어 빠졌지만, v2 표본에서는 BARRICADE(core)·BODY_SLAM이 강하게 함께 쓰입니다(lift 4.3·3.6). v1 결론은 표본 크기 탓이었습니다. 설명을 엔진 중심으로 되돌렸습니다.
- **자해**: core가 INFERNO → RUPTURE로 바뀌었습니다. RUPTURE는 우수 플레이어 core 부트스트랩 78%(경계)라 전체 플레이어(80%)로 판정했습니다. INFERNO는 지원은 되지만 우수 플레이어 core 부트스트랩 17%라 payoff로 내렸습니다.
- **소멸**: FEEL_NO_PAIN core는 우수 플레이어 부트스트랩 55%(미결)라 전체 플레이어(80.5%)로 판정했습니다. 경계값입니다. v1에서 베타 효과로 보고 뺐던 FORGOTTEN_RITUAL은 v0.107.x 런만으로도 지원(lift 3.4, 부트 92%)되어 payoff로 넣었습니다.
- **우수 플레이어 표본의 한계**: 우수 플레이어는 18명이라 "5명 이상" 기준과 부트스트랩이 엄격하게 작동합니다. 그래서 v1 목록의 많은 범용 카드가 빠졌고(특히 타격 템포 15장), 공격 재사용은 3장만 남았습니다.
- **Baalorlord와의 충돌**: POMMEL_STRIKE는 타격 빌드 덱의 74~81%에 있지만 다른 덱에도 흔해 lift가 1.4 안팎입니다. Baalorlord 8/11로 보조 출처 구제했습니다. TAUNT는 취약에서 OP.GG로 지원되지만 방어 누적에서는 v0.107.x 4명뿐이라 뺐습니다.
- **유물**: 통계로 통과한 유물 대부분(예: 힘 빌드의 DRAGON_FRUIT·MINIATURE_CANNON)은 빌드 축과 관련이 없어 다중 비교의 우연으로 보고 넣지 않았습니다. ANCHOR·HAND_DRILL·JOSS_PAPER·BURNING_STICKS·FORGOTTEN_SOUL은 v2 표본에서 기준 미달로 뺐습니다.
- **속도 편향**: 표본은 주간 최단 승리 순위에 오른 플레이어의 전적이라 빠른 공격 덱이 조금 더 많을 수 있습니다(v1과 같음). 다만 v2는 그 플레이어들의 전체 전적을 썼기 때문에 v1보다 편향이 작습니다.

## 7. 재현

분석 스크립트와 원자료는 저장소에 넣지 않았습니다. 수치는 OP.GG 전적 페이지(`https://op.gg/slay-the-spire2/history/<runId>`)의 최종 덱·카드 보상 기록으로 다시 셀 수 있습니다.
