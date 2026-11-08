# 아이언클래드 빌드 카드 근거

`data/packs/v0.107.1/archetypes/ironclad.json`의 core·enabler·payoff·support 목록이 어디서 왔는지 정리한 문서입니다. 모든 카드는 상위 플레이어의 실제 승리 덱에 근거하며, Claude의 판단이나 익명 커뮤니티 글은 근거로 쓰지 않았습니다.
역할 이름 대응: core = 핵심, enabler·payoff = 필수 시너지, support = 보조 시너지.

## 1. 출처

1. **주 출처: OP.GG** (국내 랭킹). 캐릭터별 주간 최단 승리 상위 100(2026-11-02~08)과, 거기 오른 플레이어 전원의 최근 승리. 승천 10, 일반 모드, 싱글, 바닐라(게임 플레이 모드 사용 런 제외). 아이언클래드는 **94판·37명**(v0.107.1 31판·10명, v0.111.0 베타 63판·27명).
   - OP.GG 랭킹 1위 플레이어(솔로 787판, 승률 64.4%, 최고 연승 44)는 따로 봅니다. 아이언클래드 승리 12판, 모두 v0.107.1.
2. **보조 출처: Baalorlord** (A10 아이언클래드 11연승을 전부 기록으로 남긴 플레이어, Untapped.gg 파트너). 그의 A10 승리 덱 11판(<https://baalorlord.tv/runs/1790959357> 등). 표의 "Baalorlord" 열은 11판 중 그 카드가 있는 덱 수입니다.
3. **참고만: Spire Codex A10 Lift.** 이 문서의 어떤 카드도 이것만으로 넣거나 빼지 않았습니다.

## 2. 규칙 (다섯 캐릭터 공통)

- **멤버 덱**: 그 빌드의 축 카드(anchor) 2장 이상을 가진 승리 덱. 카드를 평가할 때는 그 카드를 빼고 센다(leave-one-out). 그래서 카드가 자기 덱을 멤버로 만들어 점유율을 부풀리지 못합니다.
- **빌드 성립**: 멤버 덱 6판 이상, 4명 이상, 한 사람이 절반 초과 금지. 예외(1위 빌드): 1위가 그 빌드로 4번 이상 이겼으면 성립.
- **카드 지원(OP.GG)**: 보유 플레이어 3명 이상, lift 1.5 이상(비멤버 덱 대비, 게임 버전으로 층화한 Mantel–Haenszel 비), 점유율 25% 이상 또는 (15% 이상 + 빌드 런에서 5회 이상 제시 중 픽 50% 이상).
  - 1위 빌드에서는 위 기준 **또는** 1위 경로(1위 멤버 덱의 50% 이상 + lift 1.5 이상) 중 하나.
  - 베타(v0.108~v0.111)에서 바뀐 카드는 v0.107.x 런만으로 평가합니다(표에 "v0.107만").
- **보조 출처 구제**: OP.GG 기준 미달이어도 Baalorlord A10 덱 50% 이상(6/11 이상)이고, 카드 자체 효과가 빌드 메커니즘에 작용하고, OP.GG lift가 1.0 이상이면 enabler·payoff·support로 넣을 수 있음(core는 불가).
- **core**: 지원된 고급(Uncommon)·희귀(Rare) 카드 중 효과상 빌드의 엔진이고, 멤버 덱 40% 이상·4명 이상(1위 빌드는 그의 멤버 덱 50% 이상). 최대 3장. 해당 카드가 없으면 비워 둡니다.
- **lift 경계**: lift 1.5~1.6은 데이터가 조금만 바뀌어도 빠질 수 있어 표에 "lift 경계"로 표시합니다.
- 표의 "1위 멤버 덱"은 그 빌드 멤버 덱 중 1위 플레이어 덱에서 그 카드를 가진 수입니다(베타 변경 카드는 v0.107.x 덱만).

## 3. 기존 아키타입

### 3.1 힘 성장 (`ironclad.strength`)

표본: 멤버 덱 24판·18명 (한 플레이어 최대 12%), OP.GG 랭킹 1위 플레이어 1판, v0.107.1 10판·6명 / v0.111.0 14판. 축 카드: DEMON_FORM, INFLAME, RUPTURE, BRAND, FIGHT_ME, DOMINATE.

**core 없음.** 힘 축 카드 중 다른 축 카드 2장을 가진 덱의 40% 이상에 들어가는 카드가 없습니다(최고 FIGHT_ME 33%, INFLAME 28%). INFLAME은 core → enabler. DEMON_FORM은 v0.107.1 멤버 덱 0/10이며, 1위 플레이어의 비멤버 덱 2판에만 있습니다(베타 런을 합쳐도 3/22, lift 0.89).

| 카드 | 역할 | 점유율 (LOO, k/N) | lift | 보유 플레이어 | 빌드 내 픽 (픽/제시) | 1위 멤버 덱 | Baalorlord (11판) | 통과 기준 |
|---|---|---|---|---|---|---|---|---|
| INFLAME | enabler | 28% (5/18) | 1.62 | 5 | 5/16 | 0/1 | 0 | OP.GG |
| FIGHT_ME | enabler | 33% (6/18) | 2.32 | 6 | 4/10 | 0/1 | 5 | OP.GG |
| CONFLAGRATION | payoff | 29% (7/24) | 2.93 | 7 | 6/9 | 0/1 | 0 | OP.GG |
| UNRELENTING | support | 29% (7/24) | 1.98 | 6 | 8/25 | 0/1 | 6 | OP.GG |
| VICIOUS | support | 25% (6/24) | 1.59 | 6 | 4/20 | 0/1 | 8 | OP.GG, lift 경계 |
| DARK_EMBRACE | support | 21% (5/24) | 1.59 | 4 | 5/10 | 0/1 | 0 | OP.GG, lift 경계 |

대표 런: <https://op.gg/slay-the-spire2/history/7052873> · <https://op.gg/slay-the-spire2/history/7172666> · <https://op.gg/slay-the-spire2/history/7299437>

제외한 카드:
- DEMON_FORM (기존 core): 0% (0/10), lift 0.00, 0명, 빌드 내 픽 0/1, Baalorlord 0/11, v0.107만
- BRAND (기존 enabler): 11% (2/19), lift 0.78, 2명, 빌드 내 픽 1/8, Baalorlord 3/11
- DOMINATE (기존 enabler): 14% (1/7), lift 0.26, 1명, 빌드 내 픽 2/4, Baalorlord 5/11, v0.107만
- RUPTURE (기존 enabler): 12% (2/17), lift 1.22, 2명, 빌드 내 픽 1/11, Baalorlord 0/11
- WHIRLWIND (기존 payoff): 12% (3/24), lift 0.97, 3명, 빌드 내 픽 2/12, Baalorlord 0/11
- SWORD_BOOMERANG (기존 payoff): 25% (6/24), lift 1.499, 6명, 빌드 내 픽 6/49, Baalorlord 0/11
- TWIN_STRIKE (기존 payoff): 17% (4/24), lift 1.78, 4명, 빌드 내 픽 4/47, Baalorlord 5/11
- THRASH (기존 payoff): 17% (4/24), lift 1.14, 4명, 빌드 내 픽 2/8, Baalorlord 0/11
- SETUP_STRIKE (기존 support): 30% (3/10), lift 3.15, 2명, 빌드 내 픽 4/21, Baalorlord 0/11, v0.107만
- ONE_TWO_PUNCH (기존 support): 12% (3/24), lift 0.89, 3명, 빌드 내 픽 2/7, Baalorlord 0/11

- SWORD_BOOMERANG은 lift 1.499로 기준(1.5)을 아주 근소하게 밑돌아 제외했습니다. TWIN_STRIKE·SETUP_STRIKE는 새 타격 템포 아키타입(§4.2)에서 지원됩니다.
- 보조 출처: Baalorlord 50% 이상 카드(TAUNT·CRUELTY·VICIOUS·POMMEL_STRIKE·BLOODLETTING·PERFECTED_STRIKE·FEEL_NO_PAIN·UNRELENTING·SHRUG_IT_OFF) 중 효과가 힘에 작용하는 카드가 없어 구제 대상이 없습니다(VICIOUS·UNRELENTING은 OP.GG 기준으로 이미 지원).

### 3.2 소멸 (`ironclad.exhaust`)

표본: 멤버 덱 56판·25명 (한 플레이어 최대 16%), OP.GG 랭킹 1위 플레이어 5판, v0.107.1 11판·5명 / v0.111.0 45판. 축 카드: FEEL_NO_PAIN, DARK_EMBRACE, EVIL_EYE, FORGOTTEN_RITUAL, ASHEN_STRIKE, PACTS_END, HOWL_FROM_BEYOND, DRUM_OF_BATTLE. 데이터에서 가장 크고 일관된 빌드입니다.

**core FEEL_NO_PAIN** (55%, lift 1.92, 14명, 빌드 내 픽 26/41, 1위 멤버 덱 4/5, Baalorlord 6/11). DARK_EMBRACE는 점유율 24%라 core 기준 미달 → payoff(희귀 카드라 드물지만 제시되면 12/17 픽, lift 4.58; DARK_EMBRACE 덱 15판 중 13판이 FEEL_NO_PAIN도 보유).

| 카드 | 역할 | 점유율 (LOO, k/N) | lift | 보유 플레이어 | 빌드 내 픽 (픽/제시) | 1위 멤버 덱 | Baalorlord (11판) | 통과 기준 |
|---|---|---|---|---|---|---|---|---|
| FEEL_NO_PAIN | core | 55% (26/47) | 1.92 | 14 | 26/41 | 4/5 | 6 | OP.GG |
| TRUE_GRIT | enabler | 71% (40/56) | 1.91 | 22 | 30/111 | 3/5 | 4 | OP.GG |
| TREMBLE | enabler | 66% (37/56) | 1.53 | 19 | 38/109 | 2/5 | 4 | OP.GG, lift 경계 |
| SECOND_WIND | enabler | 48% (27/56) | 2.67 | 15 | 20/38 | 2/5 | 4 | OP.GG |
| DARK_EMBRACE | payoff | 24% (13/54) | 4.58 | 10 | 12/17 | 1/5 | 0 | OP.GG |
| ASHEN_STRIKE | payoff | 29% (15/51) | 1.52 | 11 | 15/41 | 2/5 | 3 | OP.GG, lift 경계 |
| BLOODLETTING | support | 73% (8/11) | 1.62 | 4 | 6/20 | 4/5 | 7 | OP.GG, v0.107만 |
| BATTLE_TRANCE | support | 57% (32/56) | 1.59 | 18 | 31/50 | 3/5 | 4 | OP.GG, lift 경계 |
| HEADBUTT | support | 36% (20/56) | 1.51 | 14 | 21/96 | 1/5 | 0 | OP.GG, lift 경계 |
| FIGHT_ME | support | 29% (16/56) | 2.64 | 15 | 12/35 | 0/5 | 5 | OP.GG |

대표 런: <https://op.gg/slay-the-spire2/history/7090350> · <https://op.gg/slay-the-spire2/history/7121129> · <https://op.gg/slay-the-spire2/history/7176719> · <https://op.gg/slay-the-spire2/history/7263598>

제외한 카드:
- CORRUPTION (기존 enabler): 5% (3/56), lift ∞, 2명, 빌드 내 픽 0/0, Baalorlord 0/11
- BURNING_PACT (기존 enabler): 38% (21/56), lift 1.49, 13명, 빌드 내 픽 22/36, Baalorlord 4/11
- STOKE (기존 enabler): 23% (13/56), lift 1.49, 9명, 빌드 내 픽 12/19, Baalorlord 0/11
- FIEND_FIRE (기존 enabler): 16% (9/56), lift 1.02, 8명, 빌드 내 픽 9/28, Baalorlord 0/11
- BRAND (기존 enabler): 9% (5/56), lift 0.55, 5명, 빌드 내 픽 5/22, Baalorlord 3/11
- PACTS_END (기존 payoff): 0% (0/10), lift 0.00, 0명, 빌드 내 픽 0/1, Baalorlord 0/11, v0.107만
- HOWL_FROM_BEYOND (기존 payoff): 0% (0/8), lift 0.00, 0명, 빌드 내 픽 0/2, Baalorlord 3/11, v0.107만
- DRUM_OF_BATTLE (기존 payoff): 23% (12/52), lift 1.72, 9명, 빌드 내 픽 10/25, Baalorlord 3/11
- FORGOTTEN_RITUAL (기존 payoff): 0% (0/11), lift 0.00, 0명, 빌드 내 픽 0/11, Baalorlord 0/11, v0.107만
- EVIL_EYE (기존 payoff): 41% (19/46), lift 1.01, 12명, 빌드 내 픽 15/29, Baalorlord 4/11
- OFFERING (기존 support): 21% (12/56), lift 0.70, 9명, 빌드 내 픽 10/13, Baalorlord 5/11

- FORGOTTEN_RITUAL·HOWL_FROM_BEYOND·PACTS_END는 베타에서 바뀐 카드로, v0.107.1 소멸 덱에 한 장도 없습니다(1위는 FORGOTTEN_RITUAL을 13번 제시받아 한 번도 안 집음). v0.111.0에서의 인기는 베타 변경 효과라 v0.107.1 팩에 넣지 않았습니다.
- BURNING_PACT·STOKE는 lift 1.49로 기준을 근소하게 밑돕니다. EVIL_EYE는 소멸 덱에 41% 있지만 다른 덱에도 35% 있어 이 빌드에 특화되지 않았습니다(lift 1.01).
- CORRUPTION은 고대 카드로 보상에 나오지 않고, 멤버 덱 3판·2명뿐입니다. 설명(desc)에서 이 카드 언급을 뺐습니다.
- 유물: ENDLESS_APPETITE(멤버 덱 0판) 제거. CHARONS_ASHES(5명, lift 5.84)·BURNING_STICKS(3명, 2.21)·JOSS_PAPER(8명, 1.54)·FORGOTTEN_SOUL(3명, 비멤버 0판)·TOASTY_MITTENS(3명, 1.60) 유지.

### 3.3 방어 누적 (`ironclad.block`)

표본: 멤버 덱 24판·16명 (한 플레이어 최대 17%), OP.GG 랭킹 1위 플레이어 1판, v0.107.1 4판·3명 / v0.111.0 20판. 축 카드(방어도 유지·변환): BARRICADE, BODY_SLAM, JUGGERNAUT, UNMOVABLE. 축 카드는 베타 변경 카드가 아니라 두 버전을 합쳐 평가했습니다(멤버 덱 대부분이 v0.111.0).

**core 없음, 기존 정체성 상실.** 축 카드끼리 함께 쓰이지 않습니다: BARRICADE 3/14(lift 0.78), BODY_SLAM 3/8(1.21), UNMOVABLE 3/12(0.98), JUGGERNAUT 1/20(빌드 내 제시 12회 중 픽 0). BARRICADE는 v0.111.0에서 DEMON_FORM·EXPECT_A_FIGHT(둘 다 베타 변경)와 함께 쓰여 v0.107.1 팩 근거가 되지 못합니다. 남은 목록은 이 덱들에 비멤버보다 많이 들어간 방어·에너지 카드입니다. 그래서 영어 이름을 "Barricade" → "Block"으로, 설명을 바꿨습니다.

| 카드 | 역할 | 점유율 (LOO, k/N) | lift | 보유 플레이어 | 빌드 내 픽 (픽/제시) | 1위 멤버 덱 | Baalorlord (11판) | 통과 기준 |
|---|---|---|---|---|---|---|---|---|
| BLOOD_WALL | enabler | 38% (9/24) | 1.51 | 9 | 7/59 | 1/1 | 0 | OP.GG, lift 경계 |
| ARMAMENTS | enabler | 29% (7/24) | 1.66 | 6 | 9/58 | 0/1 | 3 | OP.GG |
| IMPERVIOUS | enabler | 21% (5/24) | 1.73 | 5 | 6/12 | 0/1 | 3 | OP.GG |
| SHRUG_IT_OFF | enabler | 75% (18/24) | 1.41 | 12 | 17/47 | 1/1 | 6 | Baalorlord 보조 출처 |
| TAUNT | enabler | 75% (3/4) | 1.69 | 2 | 3/4 | 1/1 | 10 | Baalorlord 보조 출처, v0.107만 |
| OFFERING | support | 33% (8/24) | 1.55 | 8 | 7/9 | 0/1 | 5 | OP.GG, lift 경계 |
| PYRE | support | 33% (8/24) | 2.22 | 7 | 5/11 | 0/1 | 0 | OP.GG |

대표 런: <https://op.gg/slay-the-spire2/history/7265502> · <https://op.gg/slay-the-spire2/history/7172208> · <https://op.gg/slay-the-spire2/history/7100524>

제외한 카드:
- BARRICADE (기존 core): 21% (3/14), lift 0.78, 3명, 빌드 내 픽 2/4, Baalorlord 3/11
- UNMOVABLE (기존 enabler): 25% (3/12), lift 0.98, 3명, 빌드 내 픽 3/4, Baalorlord 0/11
- FLAME_BARRIER (기존 enabler): 42% (10/24), lift 1.23, 9명, 빌드 내 픽 7/13, Baalorlord 0/11
- STONE_ARMOR (기존 enabler): 29% (7/24), lift 0.92, 6명, 빌드 내 픽 8/21, Baalorlord 4/11
- CRIMSON_MANTLE (기존 enabler): 0% (0/4), lift 0.00, 0명, 빌드 내 픽 0/0, Baalorlord 0/11, v0.107만
- RAGE (기존 enabler): 12% (3/24), lift 1.00, 3명, 빌드 내 픽 4/17, Baalorlord 5/11
- BODY_SLAM (기존 payoff): 38% (3/8), lift 1.21, 3명, 빌드 내 픽 4/16, Baalorlord 0/11
- JUGGERNAUT (기존 payoff): 5% (1/20), lift 0.59, 1명, 빌드 내 픽 0/12, Baalorlord 0/11
- COLOSSUS (기존 support): 25% (1/4), lift 0.75, 1명, 빌드 내 픽 0/2, Baalorlord 0/11, v0.107만
- IRON_WAVE (기존 support): 21% (5/24), lift 1.96, 4명, 빌드 내 픽 3/48, Baalorlord 0/11

- 보조 출처 구제: SHRUG_IT_OFF(OP.GG lift 1.41, Baalorlord 6/11)와 TAUNT(v0.107.1 3/4·2명, lift 1.69, Baalorlord 10/11)는 스스로 방어도를 얻는 카드라 enabler로 넣었습니다. FEEL_NO_PAIN(Baalorlord 6/11, lift 1.05)은 방어도를 소멸 축을 거쳐서만 얻어 넣지 않았습니다.
- 유물: ANCHOR(8명, lift 1.89) 유지. STURDY_CLAMP(0.49)·VAMBRACE(0.71)·PARRYING_SHIELD(0.28)·PAELS_LEGION(1명) 제거.

### 3.4 자해 (`ironclad.self_damage`)

표본: 멤버 덱 13판·11명 (한 플레이어 최대 15%), OP.GG 랭킹 1위 플레이어 2판, v0.107.1 5판·4명 / v0.111.0 8판. 축 카드: RUPTURE, INFERNO, TEAR_ASUNDER, SPITE. 빌드 기준은 넘지만 표본이 얇습니다(LOO 분모 8~13판).

**core INFERNO** (50%, lift 3.82, 4명, 1위 멤버 덱 2/2). TEAR_ASUNDER도 수치(50%, lift 5.22, 4명)는 core 기준을 넘지만, 잃은 체력 횟수만큼 더 때리는 마무리 공격이라 스키마(§5: 쌓인 자원을 터뜨리기만 하는 카드는 payoff)에 따라 payoff로 두었습니다. 두 카드를 함께 가진 멤버 덱은 6/13이라 core가 둘이어도 coreTarget 2 기준(50%)에 못 미칩니다.

| 카드 | 역할 | 점유율 (LOO, k/N) | lift | 보유 플레이어 | 빌드 내 픽 (픽/제시) | 1위 멤버 덱 | Baalorlord (11판) | 통과 기준 |
|---|---|---|---|---|---|---|---|---|
| INFERNO | core | 50% (4/8) | 3.82 | 4 | 5/9 | 2/2 | 0 | OP.GG |
| BLOODLETTING | enabler | 80% (4/5) | 1.60 | 4 | 3/11 | 1/2 | 7 | OP.GG, v0.107만 |
| BLOOD_WALL | enabler | 46% (6/13) | 2.18 | 6 | 4/22 | 1/2 | 0 | OP.GG |
| BRAND | enabler | 46% (6/13) | 5.98 | 5 | 5/8 | 1/2 | 3 | OP.GG |
| BREAKTHROUGH | enabler | 46% (6/13) | 2.63 | 6 | 6/22 | 1/2 | 0 | OP.GG |
| TEAR_ASUNDER | payoff | 50% (4/8) | 5.22 | 4 | 3/4 | 1/2 | 0 | OP.GG |
| BATTLE_TRANCE | support | 69% (9/13) | 1.53 | 9 | 10/14 | 1/2 | 4 | OP.GG, lift 경계 |
| FLAME_BARRIER | support | 46% (6/13) | 1.56 | 5 | 4/9 | 0/2 | 0 | OP.GG, lift 경계 |
| UPPERCUT | support | 38% (5/13) | 1.97 | 5 | 3/6 | 1/2 | 5 | OP.GG |
| FIEND_FIRE | support | 31% (4/13) | 2.63 | 4 | 4/11 | 0/2 | 0 | OP.GG |
| UNMOVABLE | support | 31% (4/13) | 1.55 | 4 | 4/5 | 1/2 | 0 | OP.GG, lift 경계 |
| UNRELENTING | support | 31% (4/13) | 1.82 | 4 | 4/12 | 0/2 | 6 | OP.GG |
| CONFLAGRATION | support | 23% (3/13) | 1.67 | 3 | 3/5 | 0/2 | 0 | OP.GG |

대표 런: <https://op.gg/slay-the-spire2/history/7035633> · <https://op.gg/slay-the-spire2/history/7202800> · <https://op.gg/slay-the-spire2/history/7284933>

제외한 카드:
- RUPTURE (기존 core): 22% (2/9), lift 3.16, 2명, 빌드 내 픽 2/3, Baalorlord 0/11
- OFFERING (기존 enabler): 15% (2/13), lift 0.56, 2명, 빌드 내 픽 2/3, Baalorlord 5/11
- HEMOKINESIS (기존 enabler): 23% (3/13), lift 6.22, 3명, 빌드 내 픽 3/11, Baalorlord 0/11
- CRIMSON_MANTLE (기존 enabler): 20% (1/5), lift 1.04, 1명, 빌드 내 픽 1/2, Baalorlord 0/11, v0.107만
- SPITE (기존 payoff): 22% (2/9), lift 3.13, 2명, 빌드 내 픽 2/8, Baalorlord 3/11
- FEED (기존 support): 15% (2/13), lift 3.03, 2명, 빌드 내 픽 2/5, Baalorlord 0/11
- NOT_YET (기존 support): 8% (1/13), lift 0.71, 1명, 빌드 내 픽 0/1, Baalorlord 0/11

- RUPTURE(기존 core)·SPITE는 2/9·2명으로 플레이어 수 미달. HEMOKINESIS는 매우 특화되어 있지만(lift 6.22) 3/13·빌드 내 픽 3/11로 점유율 미달.
- BLOODLETTING은 베타 변경 카드라 v0.107.1 멤버 덱 5판·4명만으로 평가했습니다(4/5).
- 유물: RED_SKULL(lift 1.32)·SELF_FORMING_CLAY(1명) 제거.

### 3.5 취약 (`ironclad.vulnerable`)

표본: 멤버 덱 57판·29명 (한 플레이어 최대 16%), OP.GG 랭킹 1위 플레이어 4판, v0.107.1 19판·8명 / v0.111.0 38판. 축 카드: VICIOUS, CRUELTY, BULLY, DISMANTLE, MOLTEN_FIST, COLOSSUS, DOMINATE.

**core 없음.** 승리 덱 절반이 취약 소비 카드를 2장 이상 갖지만, 엔진 카드 없이 여러 빌드에 섞이는 묶음입니다. 기존 core VICIOUS는 OP.GG에서 21%·lift 1.24·빌드 내 픽 8/34(24%, 평균 카드 픽률 23%와 같음)로 기준 미달입니다. Baalorlord A10 덱 8/11에 있어 보조 출처로 payoff에 남겼고(core 불가), CRUELTY도 같은 경로입니다(Baalorlord 8/11, v0.107.1 lift 1.07).

| 카드 | 역할 | 점유율 (LOO, k/N) | lift | 보유 플레이어 | 빌드 내 픽 (픽/제시) | 1위 멤버 덱 | Baalorlord (11판) | 통과 기준 |
|---|---|---|---|---|---|---|---|---|
| TREMBLE | enabler | 65% (37/57) | 1.61 | 20 | 37/111 | 2/4 | 4 | OP.GG |
| TAUNT | enabler | 58% (11/19) | 1.74 | 3 | 9/14 | 3/4 | 10 | OP.GG, v0.107만 |
| MOLTEN_FIST | payoff | 43% (20/47) | 1.62 | 13 | 18/84 | 1/4 | 0 | OP.GG |
| VICIOUS | payoff | 21% (11/53) | 1.24 | 10 | 8/34 | 1/4 | 8 | Baalorlord 보조 출처 |
| CRUELTY | payoff | 27% (4/15) | 1.07 | 2 | 4/5 | 2/4 | 8 | Baalorlord 보조 출처, v0.107만 |
| FLAME_BARRIER | support | 39% (22/57) | 1.59 | 17 | 20/46 | 0/4 | 0 | OP.GG, lift 경계 |
| HEADBUTT | support | 39% (22/57) | 1.58 | 12 | 25/97 | 1/4 | 0 | OP.GG, lift 경계 |
| UNMOVABLE | support | 28% (16/57) | 2.10 | 12 | 14/23 | 1/4 | 0 | OP.GG |
| FIGHT_ME | support | 26% (15/57) | 2.46 | 14 | 11/37 | 0/4 | 5 | OP.GG |
| SETUP_STRIKE | support | 26% (5/19) | ∞ | 3 | 6/36 | 0/4 | 0 | OP.GG, v0.107만 |

대표 런: <https://op.gg/slay-the-spire2/history/7138596> · <https://op.gg/slay-the-spire2/history/7175296> · <https://op.gg/slay-the-spire2/history/7172666> · <https://op.gg/slay-the-spire2/history/7174864>

제외한 카드:
- THUNDERCLAP (기존 enabler): 25% (14/57), lift 1.79, 7명, 빌드 내 픽 16/107, Baalorlord 0/11
- UPPERCUT (기존 enabler): 23% (13/57), lift 1.05, 10명, 빌드 내 픽 14/31, Baalorlord 5/11
- DOMINATE (기존 enabler): 50% (7/14), lift 1.21, 3명, 빌드 내 픽 11/14, Baalorlord 5/11, v0.107만
- BREAK (기존 enabler): 14% (8/57), lift 0.86, 8명, 빌드 내 픽 0/0, Baalorlord 0/11
- DISMANTLE (기존 payoff): 13% (7/54), lift 1.29, 6명, 빌드 내 픽 6/36, Baalorlord 0/11
- BULLY (기존 payoff): 18% (9/51), lift 1.08, 5명, 빌드 내 픽 6/29, Baalorlord 0/11
- COLOSSUS (기존 payoff): 29% (4/14), lift 0.81, 2명, 빌드 내 픽 4/9, Baalorlord 0/11, v0.107만

- THUNDERCLAP은 14/57 = 24.6%로 점유율 기준(25%)을 근소하게 밑돌고, 빌드 내 픽도 16/107입니다.
- TAUNT(v0.107.1 11/19)는 3명이 가졌지만 한 플레이어가 6판(55%)을 차지합니다. 카드 기준은 통과하며 Baalorlord 10/11과도 일치합니다. SETUP_STRIKE(v0.107.1 5/19, 3명)도 한 플레이어가 3판(60%)이라 마찬가지로 얇은 근거입니다.
- 유물: HAND_DRILL(3명, 비멤버 덱 0판) 유지. PAPER_PHROG(lift 1.30)·BAG_OF_MARBLES(0.60) 제거.

## 4. 새로 추가한 아키타입

모델에 없던 빌드 중 빌드 기준을 넘는 것(또는 1위 빌드)을 추가했습니다. 유물(keyRelics)은 세 빌드 모두 비웠습니다: 표본 7~18판에서 "3명 이상·lift 1.5 이상"을 넘는 유물이 빌드마다 1~20종 이상 나와 우연과 구별할 수 없기 때문입니다.

### 4.1 공격 재사용 (`ironclad.attack_replay`)

표본: 멤버 덱 18판·16명 (한 플레이어 최대 11%), OP.GG 랭킹 1위 플레이어 1판, v0.107.1 4판·4명 / v0.111.0 14판. 축 카드: STAMPEDE, AGGRESSION, THRASH, BLUDGEON, MANGLE.

**core 없음.** THRASH가 가장 흔하지만(64%, lift 6.16, 7명) 손의 공격을 흡수해 커지는 마무리 공격이라 payoff입니다. 엔진 파워 STAMPEDE는 36%로 core 기준 미달, AGGRESSION은 4/14·lift 1.32로 지원 미달, MANGLE(베타 변경)은 v0.107.1 멤버 덱 0/4.

| 카드 | 역할 | 점유율 (LOO, k/N) | lift | 보유 플레이어 | 빌드 내 픽 (픽/제시) | 1위 멤버 덱 | Baalorlord (11판) | 통과 기준 |
|---|---|---|---|---|---|---|---|---|
| STAMPEDE | enabler | 36% (5/14) | 3.86 | 5 | 2/10 | 1/1 | 0 | OP.GG |
| JUGGLING | enabler | 28% (5/18) | 4.15 | 5 | 5/15 | 0/1 | 0 | OP.GG |
| THRASH | payoff | 64% (7/11) | 6.16 | 7 | 6/8 | 1/1 | 0 | OP.GG |
| BLUDGEON | payoff | 46% (6/13) | 2.55 | 6 | 5/10 | 1/1 | 0 | OP.GG |
| VICIOUS | support | 39% (7/18) | 2.51 | 6 | 7/12 | 0/1 | 8 | OP.GG |
| FLAME_BARRIER | support | 50% (9/18) | 1.60 | 8 | 8/16 | 0/1 | 0 | OP.GG |
| PYRE | support | 33% (6/18) | 1.96 | 6 | 5/7 | 0/1 | 0 | OP.GG |
| BREAK | support | 33% (6/18) | 3.24 | 6 | 0/0 | 0/1 | 0 | OP.GG |
| INFLAME | support | 28% (5/18) | 1.71 | 5 | 5/12 | 0/1 | 0 | OP.GG |
| UNRELENTING | support | 28% (5/18) | 1.55 | 5 | 4/18 | 0/1 | 6 | OP.GG, lift 경계 |

대표 런: <https://op.gg/slay-the-spire2/history/7263598> · <https://op.gg/slay-the-spire2/history/7146430> · <https://op.gg/slay-the-spire2/history/7299437> · <https://op.gg/slay-the-spire2/history/7269656>

### 4.2 타격 템포 (`ironclad.strike_tempo`)

표본: 멤버 덱 12판·9명 (한 플레이어 최대 25%), OP.GG 랭킹 1위 플레이어 1판, v0.107.1 6판·4명 / v0.111.0 6판. 축 카드: SETUP_STRIKE, TWIN_STRIKE, PERFECTED_STRIKE, ANGER, JUGGLING, HELLRAISER. 표본이 얇아(멤버 12판) 3판·3명만으로 기준을 넘는 범용 카드가 많고, 모두 support로 두었습니다.

**core 없음.** 타격 카드는 모두 일반(Common)이라 core가 될 수 없고, 고급 이상 축 카드는 JUGGLING 4/9(3명), HELLRAISER 1/11로 기준 미달입니다. ANGER는 4/9지만 2명뿐이라 제외.
POMMEL_STRIKE는 OP.GG lift 1.41로 미달이지만 Baalorlord 8/11이고 타격 카드라 축에 작용해 보조 출처로 enabler에 넣었습니다. Baalorlord의 타격 덱은 PERFECTED_STRIKE를 3~5장 씁니다(<https://baalorlord.tv/runs/1793824214>, <https://baalorlord.tv/runs/1793119493>, <https://baalorlord.tv/runs/1793129973>).
아키타입 목록에는 아이언클래드 카드 풀의 카드만 넣으므로(고대 카드는 support 허용), 기준을 넘은 무색·이벤트 카드 2장은 제외했습니다: FLASH_OF_STEEL(무색, 25% (3/12), lift 3.89, 3명), MAD_SCIENCE(이벤트, 25% (3/12), lift 2.57, 3명).

| 카드 | 역할 | 점유율 (LOO, k/N) | lift | 보유 플레이어 | 빌드 내 픽 (픽/제시) | 1위 멤버 덱 | Baalorlord (11판) | 통과 기준 |
|---|---|---|---|---|---|---|---|---|
| SETUP_STRIKE | enabler | 83% (5/6) | ∞ | 3 | 6/10 | 0/1 | 0 | OP.GG, v0.107만 |
| TWIN_STRIKE | enabler | 36% (4/11) | 5.46 | 3 | 5/19 | 0/1 | 5 | OP.GG |
| POMMEL_STRIKE | enabler | 75% (9/12) | 1.41 | 7 | 13/23 | 1/1 | 8 | Baalorlord 보조 출처 |
| PERFECTED_STRIKE | payoff | 30% (3/10) | 3.79 | 3 | 2/23 | 0/1 | 6 | OP.GG |
| JUGGLING | support | 44% (4/9) | 5.39 | 3 | 1/2 | 1/1 | 0 | OP.GG |
| HEADBUTT | support | 67% (8/12) | 2.31 | 6 | 8/24 | 1/1 | 0 | OP.GG |
| COLOSSUS | support | 67% (4/6) | 2.78 | 3 | 4/9 | 0/1 | 0 | OP.GG, v0.107만 |
| EVIL_EYE | support | 50% (6/12) | 1.52 | 5 | 5/7 | 0/1 | 4 | OP.GG, lift 경계 |
| MOLTEN_FIST | support | 50% (6/12) | 1.68 | 4 | 7/21 | 0/1 | 0 | OP.GG |
| BODY_SLAM | support | 42% (5/12) | 1.67 | 5 | 4/27 | 0/1 | 0 | OP.GG |
| BREAKTHROUGH | support | 42% (5/12) | 2.14 | 5 | 6/24 | 1/1 | 0 | OP.GG |
| OFFERING | support | 42% (5/12) | 1.80 | 3 | 6/7 | 0/1 | 5 | OP.GG |
| BLUDGEON | support | 33% (4/12) | 2.23 | 4 | 4/12 | 0/1 | 0 | OP.GG |
| CONFLAGRATION | support | 33% (4/12) | 2.74 | 3 | 4/6 | 0/1 | 0 | OP.GG |
| INFLAME | support | 33% (4/12) | 1.88 | 4 | 3/8 | 0/1 | 0 | OP.GG |
| RAGE | support | 33% (4/12) | 2.89 | 4 | 4/11 | 1/1 | 5 | OP.GG |
| UNRELENTING | support | 33% (4/12) | 2.24 | 3 | 5/11 | 0/1 | 6 | OP.GG |
| UPPERCUT | support | 33% (4/12) | 1.59 | 3 | 4/6 | 0/1 | 5 | OP.GG, lift 경계 |
| DRUM_OF_BATTLE | support | 25% (3/12) | 2.03 | 3 | 2/8 | 0/1 | 3 | OP.GG |
| HAVOC | support | 25% (3/12) | 2.18 | 3 | 4/31 | 1/1 | 0 | OP.GG |

대표 런: <https://op.gg/slay-the-spire2/history/7121129> · <https://op.gg/slay-the-spire2/history/7172666> · <https://op.gg/slay-the-spire2/history/7261328> · <https://op.gg/slay-the-spire2/history/7107205>

### 4.3 타격 자동 사용 (`ironclad.hellraiser`) — OP.GG 랭킹 1위 플레이어의 빌드

표본: 멤버 덱 7판·4명 (한 플레이어 최대 57%), OP.GG 랭킹 1위 플레이어 4판, v0.107.1 5판·2명 / v0.111.0 2판. 멤버 덱 = HELLRAISER 보유 덱. 일반 빌드 기준(한 사람 절반 이하)은 통과하지 못하지만, 1위 플레이어가 이 빌드로 4번 이겨(그의 아이언클래드 승리 12판 중 4판) 1위 빌드 규칙으로 성립합니다. 그의 HELLRAISER 덱 4판은 22~27장으로 가볍고 기본 타격을 0~3장만 남긴 에너지·드로우 덱입니다.

**core HELLRAISER** (1위 멤버 덱 4/4. 1위는 카드 보상에서 4번 제시받아 3번 픽, 멤버 덱 7판의 빌드 내 픽 4/4). 빌드를 정의하는 카드라 점유율·lift는 정의상 100%·∞입니다. 카드 기준은 1위 경로 또는 일반 기준 중 하나(표의 "통과 기준").

| 카드 | 역할 | 점유율 (LOO, k/N) | lift | 보유 플레이어 | 빌드 내 픽 (픽/제시) | 1위 멤버 덱 | Baalorlord (11판) | 통과 기준 |
|---|---|---|---|---|---|---|---|---|
| HELLRAISER | core | 100% (7/7) | ∞ | 4 | 4/4 | 4/4 | 0 | 빌드 정의 카드, 1위 경로 4/4 |
| BURNING_PACT | enabler | 43% (3/7) | 1.67 | 2 | 4/5 | 2/4 | 4 | 1위 경로 |
| ASHEN_STRIKE | enabler | 29% (2/7) | 1.88 | 1 | 2/4 | 2/4 | 3 | 1위 경로 |
| BLOODLETTING | support | 100% (5/5) | 2.17 | 2 | 4/8 | 4/4 | 7 | 1위 경로, v0.107만 |
| FLAME_BARRIER | support | 43% (3/7) | 1.88 | 3 | 3/7 | 1/4 | 0 | OP.GG |
| PYRE | support | 43% (3/7) | 2.40 | 3 | 2/4 | 0/4 | 0 | OP.GG |

대표 런: <https://op.gg/slay-the-spire2/history/7278205> · <https://op.gg/slay-the-spire2/history/7204765> · <https://op.gg/slay-the-spire2/history/7208199> · <https://op.gg/slay-the-spire2/history/7107205>

- POMMEL_STRIKE는 1위 멤버 덱 2/4이지만 lift 0.996(비멤버 덱과 같은 비율)이라 두 경로 모두 미달이고, 보조 출처 구제도 lift 1.0 미만이라 안 됩니다. TRUE_GRIT(3/4, lift 1.43)·DOMINATE(2/4, 1.42)·FEEL_NO_PAIN(2/4, 1.13)도 lift 미달.

## 5. 삭제한 아키타입

없음. 기존 다섯 빌드 모두 멤버 덱 13판 이상·11명 이상으로 빌드 기준을 넘습니다.
검토했지만 넣지 않은 빌드:
- BARRICADE + DEMON_FORM + EXPECT_A_FIGHT "요새" 덱(20판·11명): v0.107.1에서는 1판뿐. 핵심 카드가 베타 변경 카드라 v0.107.1 팩 근거가 되지 못합니다.
- FORGOTTEN_RITUAL·DRUM_OF_BATTLE 에너지 묶음(17판·10명): v0.107.1에서 0판(FORGOTTEN_RITUAL은 v0.107.1 31판 중 1판 vs v0.111.0 63판 중 32판). 베타 전용.

## 6. 출처 간 충돌과 미해결 사항

- **VICIOUS·CRUELTY·TAUNT**: Baalorlord는 셋을 거의 매 판 씁니다(8~10/11). OP.GG에서는 VICIOUS가 평범한 픽률(24%)이고, CRUELTY·TAUNT는 v0.107.1에서 2~3명에게 몰려 있습니다. 그래서 취약에서 셋 다 목록에 있지만 core는 없습니다. Baalorlord 런의 게임 버전은 기록되어 있지 않습니다(대략 v0.10x~v0.111).
- **방어 누적**: 데이터상 BARRICADE 엔진은 상위 플레이어가 쓰지 않습니다. 이 아키타입은 "방어도 카드를 많이 쓰는 덱" 감지로만 남았고 비멤버 덱 commit 중앙값도 0.56으로 높아 특이성이 낮습니다. 삭제 기준(멤버 덱 4판 미만)에는 해당하지 않아 유지했습니다.
- **TEAR_ASUNDER·THRASH**: 수치로는 core 기준을 넘지만 마무리 공격이라 payoff로 두었습니다. 그 결과 자해는 core 1장, 공격 재사용은 core 없음입니다.
- **속도 편향**: OP.GG 주간 순위는 "가장 빠른 승리" 순이라, 느린 성장 빌드(BARRICADE·BODY_SLAM, DEMON_FORM)가 실제 승률보다 적게 잡혔을 수 있습니다.
- **버전 표본**: v0.107.1 승리는 31판·10명뿐이고 3명이 23판을 차지해, 베타 변경 카드(BLOODLETTING·TAUNT·CRUELTY·SETUP_STRIKE·COLOSSUS 등)는 모두 적은 플레이어 수에 기대고 있습니다.
- **태그 공백(참고)**: 스스로 소멸하는 카드(TREMBLE 등)는 티어 파일에 `EXHAUST` provides 태그가 없습니다. 이 문서는 티어 파일을 바꾸지 않았습니다.

## 7. 재현

분석 스크립트와 원자료는 저장소에 넣지 않았습니다. 수치는 OP.GG 전적 페이지(`https://op.gg/slay-the-spire2/history/<runId>`)의 최종 덱·카드 보상 기록과 Baalorlord 런 페이지(`https://baalorlord.tv/runs/<id>`)로 다시 셀 수 있습니다.
