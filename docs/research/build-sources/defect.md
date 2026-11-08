# 디펙트 빌드 카드 근거 (v2, 상위 플레이어 대규모 표본)

`data/packs/v0.107.1/archetypes/defect.json`의 빌드별 카드(core = 핵심, enabler·payoff = 필수 시너지, support = 보조 시너지)를 OP.GG 상위 플레이어의 실제 승리 덱으로 다시 정한 근거입니다. v1(승리 50판)보다 표본이 약 9배 큽니다.

## 1. 표본과 방법

- **표본**: OP.GG 캐릭터별 주간 "최단 시간 승리" 상위 100에 오른 플레이어 171명의 전체 기록 중, 승천 10·일반 모드·싱글·바닐라(게임플레이 모드 없음)·v0.107.0 이후 디펙트 **승리 463판(51명)**. 이 중 **우수 플레이어**(A10 승리 5판 이상, 1막을 넘긴 판 기준 승률 25% 이상) **17명의 259판**을 판정에 우선 썼습니다. OP.GG 랭킹 1위 플레이어의 승리는 78판입니다.
- **버전**: 베타에서 바뀐 카드(ROCKET_PUNCH, TRASH_TO_TREASURE, THUNDER, SYNCHRONIZE, NULL, SHATTER, REFRACT, SCRAPE 등)는 v0.107.x 런에서만 셉니다.
- **플레이어 균형**: 모든 비율은 한 플레이어의 가중치가 전체의 10%를 넘지 않게 맞춘 값(균형값)이고, 괄호 안 raw는 단순 비율입니다. 판정은 균형값으로 합니다.
- **안정성**: 플레이어를 복원 추출하는 부트스트랩 200회에서 80% 이상 같은 결론이 나와야 채택합니다. 20~80%는 "불확실"로, v1의 역할을 그대로 둡니다(v1에 없던 카드는 넣지 않음). 20% 이하는 확정 탈락입니다.
- **우수/전체**: 판정은 우수 플레이어 기준입니다. 우수 표본이 기준 수(빌드: 멤버 덱 25판·6명, 카드: 5명·LOO 멤버 덱 25판)에 못 미치면 전체 플레이어로 판정하되, 전체 결과도 부트스트랩 80% 이상일 때만 씁니다. 표의 "판정" 열이 어느 쪽을 썼는지 보여 줍니다.
- **빌드 찾기**: 카드끼리 같은 덱에 함께 들어가는 정도(플레이어 균형 phi)로 평균 연결 군집을 만들었습니다(합치는 기준 phi 0.10). 군집의 카드가 그 빌드의 **서명 카드**이고, 덱이 서명 카드를 2장(서명 카드 7장 이상이면 3장) 이상 가지면 멤버 덱입니다. 카드를 평가할 때는 그 카드를 빼고 멤버 여부를 다시 계산합니다(leave-one-out).
- **빌드 인정**: 멤버 덱 25판 이상·6명 이상·한 사람이 절반 이하. 1위 플레이어 예외: 그의 승리 15판 이상. 미달이면 멤버 덱 10판 이상일 때 core를 비우고 "근거 부족"으로 유지, 10판 미만이면 삭제.
- **카드 근거**: 5명 이상 보유, lift(멤버 덱 보유율 ÷ 비멤버 덱 보유율, 균형값) 1.5 이상, 멤버 덱 보유율 25% 이상(또는 15% 이상이면서 그 빌드 런의 보상에서 10회 이상 나와 50% 이상 집음).
- **core**: 근거를 통과한 고급·희귀 카드 중 효과상 빌드의 엔진이고 보유율 40% 이상·5명 이상(부트스트랩 80% 이상). 최대 3장.
- **보조 출처 구제(Baalorlord, v1과 같음)**: OP.GG 기준 미달이어도 Baalorlord A10 디펙트 승리 덱 8판 중 4판 이상에 있고, 효과가 빌드 축에 작용하고, 판정 표본의 lift가 1.0 이상이면 enabler·payoff·support로 둡니다(core 불가).
- **keyRelics**: 멤버 덱 유물 lift 1.5 이상·5명 이상·부트스트랩 80% 이상이고, 효과가 빌드 축에 작용하는 유물만.
- **coreTarget / essentialTarget**: 멤버 덱이 가진 서로 다른 core 수 / enabler+payoff 수의 플레이어 균형 중앙값(부트스트랩 80% 이상 같은 값일 때만 적용). **fullWeight**: v1 규칙(멤버 commit 중앙값 < 0.7 또는 비멤버 > 0.5일 때만 멤버 중앙값 0.85에 맞춰 0.5 단위, 최소 2.0).

## 2. 빌드 요약 (v1 → v2)

| 빌드 | 판정 | 우수 멤버 덱·명 | 전체 멤버 덱·명 | 1위 판수 | core v1 → v2 | fullWeight |
|---|---|---|---|---|---|---|
| `defect.orb_focus` | 통과(우수) | 66·10 | 134·31 | 14 | 없음 → CAPACITOR | 4.5 → 3.5 |
| `defect.lightning` | 통과(우수) | 67·13 | 145·37 | 11 | THUNDER, STORM → STORM, THUNDER | 3.5 → 3.5 |
| `defect.status` | 통과(우수) | 79·12 | 202·35 | 24 | ROCKET_PUNCH, COMPACT → COMPACT, ROCKET_PUNCH | 6.0 → 6.0 |
| `defect.zero_cost` | 통과(전체·1위 예외) | 52·10 | 96·28 | 28 | 없음 → FERAL | 3.5 → 3.5 |
| `defect.dark_orbs` | 근거 부족 | 22·7 | 34·13 | 9 | 없음 → 없음 | 4.0 → 2.0 |
| `defect.orb_variety` | 통과(전체·1위 예외), 근거 카드 없음 → 근거 부족 | 38·10 | 68·23 | 15 | SYNCHRONIZE → 없음 | 6.0 → 2.0 |

추가·삭제된 빌드는 없습니다(6개 유지). 새 후보는 5절에 있습니다.

## 3. 빌드별 근거

### defect.orb_focus (냉기·구체 칸)

- 서명 카드: GLACIER, CAPACITOR, HAILSTORM (≥2장)
- 멤버 덱: 우수 66판·10명(최대 1인 26%, 부트 96%) / 전체 134판·31명(최대 1인 16%, 부트 100%). 1위 플레이어 14판. 버전: v0.107.x 71, 베타(v0.108~v0.111) 63
- 판정 표본: 우수 플레이어, 상태: pass

| 카드 | 희귀도 | 역할 | 통과 | 판정 | 우수: 보유율(보유/멤버) | lift | 명 | 부트 | 전체: 보유율(보유/멤버) | lift | 명 | 부트 | core 부트 | 빌드 내 픽(판정) | 1위 보유 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| CAPACITOR | Uncommon | core | OP.GG | 전체 | 88% (13/16) | 2.00 | 8 | 93% | 63% (30/54) | 1.84 | 17 | 94% | 94% | 68% (38회) | 13/14 |
| GLACIER | Uncommon | enabler | OP.GG | 전체 | 77% (13/16) | 2.02 | 8 | 88% | 78% (30/40) | 1.92 | 17 | 96% | 96% | 77% (35회) | 14/14 |
| COOLHEADED | Common | enabler | 구제(Baalorlord 5/8) | 우수 | 73% (42/66) | 1.17 | 10 | 0% | 69% (90/134) | 1.16 | 27 | 0% | 0% | 37% (120회) | 8/14 |
| BARRAGE | Common | payoff | 구제(Baalorlord 4/8) | 우수 | 47% (29/66) | 1.80 | 8 | 74% | 37% (51/134) | 1.88 | 17 | 92% | 62% | 21% (126회) | 6/14 |
| HAILSTORM | Uncommon | payoff | 불확실 | 우수 | 24% (13/60) | 2.13 | 8 | 40% | 32% (30/100) | 1.97 | 17 | 85% | 2% | 24% (38회) | 4/14 |

**v1에서 빠진 카드**: CHILL(v1 enabler; 우수 32%·lift 1.09·7명·부트 0%); COLD_SNAP(v1 enabler; 우수 29%·lift 1.25·7명·부트 15%); DEFRAGMENT(v1 enabler; 우수 35%·lift 1.07·7명·부트 0%); SHADOW_SHIELD(v1 support; 우수 22%·lift 1.00·6명·부트 0%); BOOT_SEQUENCE(v1 support; 우수 23%·lift 0.99·7명·부트 0%); LIGHTNING_ROD(v1 support; 우수 22%·lift 1.03·7명·부트 0%); SCAVENGE(v1 support; 우수 19%·lift 0.97·5명·부트 0%); NULL(v1 support; 전체 19%·lift 1.20·8명·부트 14%); BEAM_CELL(v1 support; 우수 23%·lift 0.68·8명·부트 0%); ADAPTIVE_STRIKE(v1 support; 우수 16%·lift 1.69·5명·부트 6%); FLAK_CANNON(v1 support; 우수 17%·lift 1.17·6명·부트 0%)

- 메모: 변경: 서명 카드를 v1의 냉기 카드 묶음(CHILL·COLD_SNAP·GLACIER·HAILSTORM…)에서 실제로 함께 쓰이는 GLACIER·CAPACITOR·HAILSTORM으로 바꿈. CHILL·COLD_SNAP은 어느 정의에서도 lift 1.0~1.3이라 빠짐(냉기 공급 카드는 모든 디펙트 덱에 고루 쓰임). DEFRAGMENT 제외(확정): 우수 플레이어 기준 lift 1.1(멤버 35%·비멤버 32%)·부트 0%. 보상 화면 픽률은 78%(우수 플레이어, 139회)로 높지만 특정 빌드에 몰리지 않는 범용 카드라 이 빌드 카드가 아님. 그래서 needs의 FOCUS와 이름의 "밀집"도 뺌.
- 유물: SYMBIOTIC_VIRUS 추가(우수 32%·lift 2.8·9명·부트 97%). v1의 DATA_DISK(우수 lift 0.9)·RUNIC_CAPACITOR(0.4)·GOLD_PLATED_CABLES(0.5)는 멤버 덱에서 오히려 덜 보여 제외. POCKETWATCH·MINIATURE_TENT 등 수치는 넘었지만 구체 축과 무관한 유물은 넣지 않음.
- coreTarget: 균형 중앙값 1(raw 1, 부트 100%)
- essentialTarget: 균형 중앙값 2(raw 2, 부트 62%) → 불확실, 넣지 않음
- fullWeight: v1 4.5 기준 멤버 commit 중앙값 0.69·비멤버 0.27 → 3.5 (멤버 0.89, raw 0.87 / 비멤버 0.34, raw 0.30; 판정 표본 멤버 66판·비멤버 193판)
- 참고(승패 비교, 우수 플레이어 균형): CAPACITOR 승리 덱 46% vs 패배 덱 27%. 패배 덱도 끝까지 간 덱이라 인과는 아닙니다.
- 대표 런: <https://op.gg/slay-the-spire2/history/7052756> <https://op.gg/slay-the-spire2/history/5870796> <https://op.gg/slay-the-spire2/history/6698663> <https://op.gg/slay-the-spire2/history/4211845>

### defect.lightning (번개)

- 서명 카드: SUBROUTINE, STORM, THUNDER, LIGHTNING_ROD, CREATIVE_AI, BALL_LIGHTNING, TEMPEST, VOLTAIC, TESLA_COIL (≥3장)
- 멤버 덱: 우수 67판·13명(최대 1인 22%, 부트 99%) / 전체 145판·37명(최대 1인 15%, 부트 100%). 1위 플레이어 11판. 버전: v0.107.x 94, 베타(v0.108~v0.111) 51
- 판정 표본: 우수 플레이어, 상태: pass

| 카드 | 희귀도 | 역할 | 통과 | 판정 | 우수: 보유율(보유/멤버) | lift | 명 | 부트 | 전체: 보유율(보유/멤버) | lift | 명 | 부트 | core 부트 | 빌드 내 픽(판정) | 1위 보유 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| THUNDER | Uncommon | core | OP.GG | 전체 | 71% (15/23) | 9.78 | 6 | 78% | 61% (46/75) | 2.84 | 16 | 100% | 100% | 74% (58회) | 9/11 |
| STORM | Uncommon | core | OP.GG | 우수 | 57% (26/44) | 2.38 | 8 | 94% | 63% (59/95) | 2.27 | 24 | 100% | 91% | 53% (33회) | 7/11 |
| BALL_LIGHTNING | Common | enabler | 불확실 | 우수 | 32% (14/57) | 2.29 | 7 | 62% | 28% (35/124) | 1.59 | 18 | 48% | 20% | 20% (108회) | 4/11 |
| LIGHTNING_ROD | Common | enabler | 불확실 | 우수 | 27% (13/54) | 1.38 | 6 | 30% | 38% (43/116) | 1.88 | 22 | 93% | 8% | 15% (95회) | 3/11 |
| TEMPEST | Uncommon | enabler | 불확실 | 우수 | 21% (13/59) | 2.56 | 6 | 31% | 24% (33/135) | 2.66 | 15 | 38% | 3% | 19% (52회) | 3/11 |
| VOLTAIC | Rare | payoff | 불확실 | 우수 | 26% (18/58) | 2.81 | 6 | 58% | 23% (31/134) | 3.26 | 13 | 60% | 6% | 39% (18회) | 6/11 |
| TESLA_COIL | Uncommon | payoff | 불확실 | 우수 | 25% (14/63) | 11.56 | 6 | 42% | 20% (26/138) | 3.84 | 13 | 13% | 5% | 18% (47회) | 3/11 |
| MODDED | Rare | support | 구제(Baalorlord 4/8) | 우수 | 33% (18/67) | 1.18 | 8 | 0% | 23% (31/145) | 0.96 | 19 | 0% | 0% | 52% (31회) | 3/11 |
| BUFFER | Rare | support | 불확실 | 우수 | 25% (15/67) | 1.55 | 9 | 50% | 26% (38/145) | 1.55 | 23 | 51% | 6% | 45% (28회) | 1/11 |
| SUPERCRITICAL | Rare | support | 불확실 | 우수 | 15% (11/67) | 2.10 | 6 | 32% | 15% (21/145) | 1.15 | 13 | 0% | 0% | 56% (17회) | 1/11 |

**v1에서 빠진 카드**: CHILL(v1 support; 우수 28%·lift 0.85·9명·부트 0%); FIGHT_THROUGH(v1 support; 우수 16%·lift 0.58·7명·부트 0%); SUBROUTINE(v1 support; 우수 35%·lift 1.00·7명·부트 0%); COLD_SNAP(v1 support; 우수 22%·lift 0.87·7명·부트 0%); NULL(v1 support; 전체 16%·lift 0.98·11명·부트 0%)

**불확실이라 넣지 않은 새 후보**(보유율 ≥25%·lift ≥1.5지만 부트 80% 미만): LEAP 30%·lift 1.6·부트 54%, LOOP 34%·lift 1.5·부트 52%

- 메모: SUBROUTINE 제외: 우수 플레이어 lift 0.9994(부트 0%, Baalorlord 구제 기준 lift ≥1.0에 미달·경계선), 전체 플레이어 lift 1.6(부트 67%). CHILL·COLD_SNAP(v1 support)은 lift ≤1.0으로 제외.
- 유물: INFUSED_CORE 유지(우수 14%·lift 9.2·6명·부트 77% → 불확실; 전체 9명·부트 95%). HORN_CLEAT·VAMBRACE 등은 수치는 넘었지만 번개 축과 무관해 넣지 않음.
- coreTarget: 균형 중앙값 1(raw 1, 부트 77%) → 불확실, v1 값 2 유지
- essentialTarget: 균형 중앙값 1(raw 1, 부트 53%) → 불확실, 넣지 않음
- fullWeight: v1 3.5 기준 멤버 commit 중앙값 1.00·비멤버 0.14 → 3.5 (멤버 1.00, raw 0.97 / 비멤버 0.14, raw 0.14; 판정 표본 멤버 67판·비멤버 192판)
- 참고(승패 비교, 우수 플레이어 균형): STORM 승리 덱 31% vs 패배 덱 13%; THUNDER 승리 덱 30% vs 패배 덱 20%. 패배 덱도 끝까지 간 덱이라 인과는 아닙니다.
- 대표 런: <https://op.gg/slay-the-spire2/history/6977215> <https://op.gg/slay-the-spire2/history/4118788> <https://op.gg/slay-the-spire2/history/6769410> <https://op.gg/slay-the-spire2/history/7171766>

### defect.status (상태이상 재활용)

- 서명 카드: ITERATION, OVERCLOCK, ROCKET_PUNCH, FIGHT_THROUGH, GUNK_UP, SMOKESTACK, FLAK_CANNON, TRASH_TO_TREASURE (≥3장)
- 멤버 덱: 우수 79판·12명(최대 1인 30%, 부트 94%) / 전체 202판·35명(최대 1인 28%, 부트 100%). 1위 플레이어 24판. 버전: v0.107.x 152, 베타(v0.108~v0.111) 50
- 판정 표본: 우수 플레이어, 상태: pass

| 카드 | 희귀도 | 역할 | 통과 | 판정 | 우수: 보유율(보유/멤버) | lift | 명 | 부트 | 전체: 보유율(보유/멤버) | lift | 명 | 부트 | core 부트 | 빌드 내 픽(판정) | 1위 보유 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| COMPACT | Uncommon | core | 불확실 | 우수 | 59% (40/79) | 1.62 | 9 | 57% | 47% (96/202) | 1.38 | 26 | 28% | 57% | 70% (59회) | 12/24 |
| ROCKET_PUNCH | Uncommon | core | OP.GG | 전체 | 34% (16/34) | 3.45 | 3 | 10% | 54% (73/130) | 2.70 | 18 | 98% | 96% | 61% (81회) | 15/24 |
| TURBO | Common | enabler | 구제(Baalorlord 8/8) | 우수 | 75% (62/79) | 1.14 | 10 | 0% | 78% (160/202) | 1.18 | 30 | 0% | 0% | 34% (151회) | 21/24 |
| BOOST_AWAY | Common | enabler | OP.GG | 우수 | 72% (52/79) | 2.01 | 10 | 90% | 75% (155/202) | 1.90 | 30 | 98% | 90% | 33% (127회) | 18/24 |
| OVERCLOCK | Uncommon | enabler | 구제(Baalorlord 5/8) | 우수 | 54% (24/46) | 1.65 | 8 | 64% | 54% (80/148) | 1.36 | 23 | 11% | 62% | 53% (29회) | 13/24 |
| GUNK_UP | Common | enabler | OP.GG | 우수 | 42% (20/59) | 2.59 | 8 | 88% | 44% (81/170) | 2.35 | 23 | 100% | 50% | 14% (89회) | 9/24 |
| SMOKESTACK | Uncommon | payoff | 불확실 | 우수 | 33% (20/69) | 2.59 | 8 | 73% | 43% (78/175) | 2.53 | 24 | 100% | 15% | 40% (42회) | 7/24 |
| TRASH_TO_TREASURE | Rare | payoff | 불확실 | 전체 | 23% (11/41) | 1.62 | 3 | 8% | 29% (46/139) | 1.83 | 12 | 76% | 0% | 78% (48회) | 5/24 |
| FTL | Uncommon | support | 불확실 | 우수 | 26% (16/79) | 1.23 | 7 | 23% | 29% (64/202) | 1.13 | 19 | 0% | 10% | 28% (48회) | 5/24 |

**v1에서 빠진 카드**: ITERATION(v1 payoff; 우수 32%·lift 1.00·7명·부트 0%); BULK_UP(v1 support; 우수 25%·lift 1.29·5명·부트 20%); WHITE_NOISE(v1 support; 우수 36%·lift 0.92·7명·부트 0%); SHATTER(v1 support; 전체 18%·lift 1.61·9명·부트 14%); NULL(v1 support; 전체 17%·lift 1.02·10명·부트 0%)

**불확실이라 넣지 않은 새 후보**(보유율 ≥25%·lift ≥1.5지만 부트 80% 미만): FIGHT_THROUGH 35%·lift 1.8·부트 56%, FLAK_CANNON 31%·lift 2.6·부트 64%, UPROAR 26%·lift 1.9·부트 44%

- 메모: COMPACT는 core 판정이 불안정(부트 57%)해 v1 core 유지(불확실). ITERATION(v1 payoff)은 우수 플레이어 lift 0.9997(1.0 미만)·부트 0%라 Baalorlord 구제(lift ≥1.0)도 안 됨 → 제외(경계선).
- 유물: 수치를 넘은 유물(BYRDPIP·FISHING_ROD·BOOMING_CONCH 등)은 모두 상태이상 축과 무관해 없음.
- coreTarget: 균형 중앙값 1(raw 1, 부트 100%) → 기본값 1
- essentialTarget: 균형 중앙값 3(raw 3, 부트 78%) → 불확실, 넣지 않음
- fullWeight: v1 6.0 기준 멤버 commit 중앙값 0.72·비멤버 0.28 → 6.0 (멤버 0.72, raw 0.67 / 비멤버 0.28, raw 0.29; 판정 표본 멤버 79판·비멤버 180판)
- 참고(승패 비교, 우수 플레이어 균형): COMPACT 승리 덱 41% vs 패배 덱 17%; ROCKET_PUNCH 승리 덱 21% vs 패배 덱 22%. 패배 덱도 끝까지 간 덱이라 인과는 아닙니다.
- 대표 런: <https://op.gg/slay-the-spire2/history/4722246> <https://op.gg/slay-the-spire2/history/7052834> <https://op.gg/slay-the-spire2/history/6651591> <https://op.gg/slay-the-spire2/history/6131585>

### defect.zero_cost (0코스트)

- 서명 카드: BEAM_CELL, BULK_UP, CLAW, FERAL, ALL_FOR_ONE, SCRAPE, MOMENTUM_STRIKE (≥3장)
- 멤버 덱: 우수 52판·10명(최대 1인 54%, 부트 56%) / 전체 96판·28명(최대 1인 29%, 부트 100%). 1위 플레이어 28판. 버전: v0.107.x 79, 베타(v0.108~v0.111) 17
- 판정 표본: 전체 플레이어, 상태: pass(all-fallback)+rank1

| 카드 | 희귀도 | 역할 | 통과 | 판정 | 우수: 보유율(보유/멤버) | lift | 명 | 부트 | 전체: 보유율(보유/멤버) | lift | 명 | 부트 | core 부트 | 빌드 내 픽(판정) | 1위 보유 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| FERAL | Uncommon | core | OP.GG | 전체 | 49% (14/31) | 3.35 | 6 | 79% | 55% (33/62) | 2.90 | 16 | 100% | 100% | 80% (36회) | 18/28 |
| CLAW | Common | enabler | OP.GG | 전체 | 58% (15/26) | 3.14 | 6 | 77% | 54% (32/60) | 2.99 | 15 | 100% | 98% | 53% (91회) | 23/28 |
| BEAM_CELL | Common | enabler | OP.GG | 전체 | 63% (13/25) | 2.22 | 6 | 73% | 47% (30/59) | 1.82 | 13 | 87% | 80% | 27% (99회) | 22/28 |
| ALL_FOR_ONE | Rare | payoff | OP.GG | 전체 | 54% (13/38) | 4.77 | 7 | 91% | 44% (29/70) | 3.36 | 13 | 100% | 74% | 63% (36회) | 9/28 |
| SCRAPE | Uncommon | payoff | OP.GG | 전체 | 35% (4/34) | 5.45 | 3 | 14% | 34% (14/68) | 3.94 | 10 | 84% | 22% | 31% (35회) | 9/28 |
| BULK_UP | Uncommon | support | 불확실 | 전체 | 30% (7/40) | 1.43 | 4 | 22% | 35% (24/73) | 1.43 | 12 | 36% | 19% | 58% (43회) | 11/28 |
| REBOOT | Rare | support | OP.GG | 전체 | 32% (13/52) | 1.95 | 6 | 56% | 33% (30/96) | 2.08 | 13 | 94% | 10% | 67% (36회) | 6/28 |

**v1에서 빠진 카드**: SKIM(v1 support; 전체 23%·lift 1.03·8명·부트 0%); OVERCLOCK(v1 support; 전체 47%·lift 1.08·17명·부트 0%); CAPACITOR(v1 support; 전체 15%·lift 0.37·8명·부트 0%); FLAK_CANNON(v1 support; 전체 25%·lift 1.18·10명·부트 0%); SYNCHRONIZE(v1 support; 전체 7%·lift 0.70·4명·부트 0%); BARRAGE(v1 support; 전체 11%·lift 0.42·5명·부트 0%); COMPILE_DRIVER(v1 support; 전체 21%·lift 0.74·10명·부트 0%)

**불확실이라 넣지 않은 새 후보**(보유율 ≥25%·lift ≥1.5지만 부트 80% 미만): MOMENTUM_STRIKE 29%·lift 4.0·부트 66%

- 메모: v1의 "1위 플레이어 빌드"가 아니라 넓은 플레이어가 쓰는 클로 빌드로 확인: CLAW·FERAL·ALL_FOR_ONE·BEAM_CELL·SCRAPE가 같은 묶음. ALL_FOR_ONE은 core 판정 부트 73%라 v1 역할(payoff) 유지. v1 support 중 1위 플레이어 경로로만 들어온 SKIM·OVERCLOCK·CAPACITOR·FLAK_CANNON·SYNCHRONIZE·BARRAGE·COMPILE_DRIVER는 모두 제외.
- 유물: ORNAMENTAL_FAN 추가(전체 22%·lift 2.6·11명·부트 95%; 공격 횟수 축). KUNAI(부트 73%)·SHURIKEN·POWER_CELL은 불안정해 넣지 않음.
- coreTarget: 균형 중앙값 1(raw 1, 부트 100%)
- essentialTarget: 균형 중앙값 2(raw 2, 부트 91%) → 적용
- fullWeight: v1 3.5 기준 멤버 commit 중앙값 0.99·비멤버 0.14 → 3.5 (멤버 0.99, raw 0.97 / 비멤버 0.14, raw 0.14; 판정 표본 멤버 96판·비멤버 367판)
- 참고(승패 비교, 우수 플레이어 균형): FERAL 승리 덱 17% vs 패배 덱 10%. 패배 덱도 끝까지 간 덱이라 인과는 아닙니다.
- 대표 런: <https://op.gg/slay-the-spire2/history/4062725> <https://op.gg/slay-the-spire2/history/5449495> <https://op.gg/slay-the-spire2/history/7106743> <https://op.gg/slay-the-spire2/history/6876502>

### defect.dark_orbs (암흑 구체)

- 서명 카드: SHADOW_SHIELD, DARKNESS, MULTI_CAST (≥2장)
- 멤버 덱: 우수 22판·7명(최대 1인 41%, 부트 34%) / 전체 34판·13명(최대 1인 26%, 부트 74%). 1위 플레이어 9판. 버전: v0.107.x 20, 베타(v0.108~v0.111) 14
- 판정 표본: 전체 플레이어, 상태: thin

| 카드 | 희귀도 | 역할 | 통과 | 판정 | 우수: 보유율(보유/멤버) | lift | 명 | 부트 | 전체: 보유율(보유/멤버) | lift | 명 | 부트 | core 부트 | 빌드 내 픽(판정) | 1위 보유 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| DARKNESS | Uncommon | enabler | 불확실 | 전체 | 70% (5/8) | 6.96 | 4 | 30% | 70% (5/8) | 6.18 | 4 | 33% | 33% | 80% (7회) | 7/9 |
| SHADOW_SHIELD | Uncommon | enabler | 구제(Baalorlord 5/8) | 전체 | 60% (5/9) | 2.94 | 4 | 30% | 43% (5/11) | 2.30 | 4 | 33% | 31% | 31% (9회) | 7/9 |
| COMPACT | Uncommon | support | 불확실 | 전체 | 52% (10/22) | 1.30 | 5 | 24% | 51% (16/34) | 1.33 | 9 | 27% | 27% | 76% (23회) | 2/9 |
| CAPACITOR | Uncommon | support | 구제(Baalorlord 4/8) | 전체 | 61% (8/22) | 1.33 | 5 | 27% | 40% (10/34) | 1.08 | 7 | 0% | 0% | 62% (15회) | 0/9 |

**v1에서 빠진 카드**: BARRAGE(v1 payoff; 전체 18%·lift 0.72·5명·부트 0%); MACHINE_LEARNING(v1 support; 전체 25%·lift 1.17·7명·부트 0%); BALL_LIGHTNING(v1 support; 전체 21%·lift 1.07·4명·부트 0%); COOLHEADED(v1 support; 전체 70%·lift 1.13·11명·부트 0%); MODDED(v1 support; 전체 19%·lift 0.83·5명·부트 0%)

**불확실이라 넣지 않은 새 후보**(보유율 ≥25%·lift ≥1.5지만 부트 80% 미만): LOOP 36%·lift 1.6·부트 56%, NULL 50%·lift 3.5·부트 54%, SHATTER 35%·lift 3.0·부트 32%

- 메모: 근거 부족: 멤버 덱이 기준(우수 25판·6명)에 못 미치고 전체 플레이어 부트스트랩도 74%. 삭제 기준(10판 미만)은 넘어 유지, core 비움. 서명 카드를 v1 정의(DARKNESS·SHADOW_SHIELD·NULL·CONSUMING_SHADOW)로 바꾸면 전체 41판·15명·부트 91%로 통과하지만 역시 core 후보 없음.
- 유물: DATA_DISK 유지(전체 42%·lift 1.5·9명·부트 42% → 불확실).
- essentialTarget: 균형 중앙값 2(raw 2, 부트 100%) → 적용
- fullWeight: v1 4.0 기준 멤버 commit 중앙값 0.47·비멤버 0.17 → 2.0 (멤버 0.95, raw 0.95 / 비멤버 0.35, raw 0.35; 판정 표본 멤버 34판·비멤버 429판)
- 대표 런: <https://op.gg/slay-the-spire2/history/7147957> <https://op.gg/slay-the-spire2/history/4682265> <https://op.gg/slay-the-spire2/history/7089417> <https://op.gg/slay-the-spire2/history/3749310>

### defect.orb_variety (구체 다양성)

- 서명 카드: COMPACT, LOOP, SYNCHRONIZE (≥2장)
- 멤버 덱: 우수 38판·10명(최대 1인 39%, 부트 72%) / 전체 68판·23명(최대 1인 22%, 부트 100%). 1위 플레이어 15판. 버전: v0.107.x 52, 베타(v0.108~v0.111) 16
- 판정 표본: 전체 플레이어, 상태: pass(all-fallback)+rank1

| 카드 | 희귀도 | 역할 | 통과 | 판정 | 우수: 보유율(보유/멤버) | lift | 명 | 부트 | 전체: 보유율(보유/멤버) | lift | 명 | 부트 | core 부트 | 빌드 내 픽(판정) | 1위 보유 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| COOLHEADED | Common | enabler | 구제(Baalorlord 5/8) | 전체 | 74% (30/38) | 1.18 | 8 | 0% | 76% (53/68) | 1.26 | 19 | 2% | 2% | 46% (138회) | 13/15 |
| SYNCHRONIZE | Uncommon | payoff | 불확실 | 전체 | 29% (4/13) | 2.68 | 2 | 0% | 21% (9/34) | 2.80 | 5 | 30% | 2% | 28% (23회) | 11/15 |
| COMPACT | Uncommon | support | 불확실 | 전체 | 38% (4/9) | 0.92 | 2 | 0% | 56% (9/15) | 1.44 | 5 | 29% | 29% | 55% (13회) | 12/15 |

**v1에서 빠진 카드**: GLASSWORK(v1 enabler; 전체 13%·lift 0.86·6명·부트 0%); GLACIER(v1 enabler; 전체 39%·lift 0.89·10명·부트 0%); COMPILE_DRIVER(v1 payoff; 전체 28%·lift 1.05·9명·부트 0%); SKIM(v1 support; 전체 24%·lift 1.04·8명·부트 0%); CAPACITOR(v1 support; 전체 41%·lift 1.13·12명·부트 0%); MODDED(v1 support; 전체 22%·lift 0.94·10명·부트 0%); ITERATION(v1 support; 전체 38%·lift 0.92·11명·부트 0%); CLAW(v1 support; 전체 19%·lift 0.82·7명·부트 0%)

**불확실이라 넣지 않은 새 후보**(보유율 ≥25%·lift ≥1.5지만 부트 80% 미만): LOOP 48%·lift 2.2·부트 51%

- 메모: 근거 부족: 같이 쓰이는 묶음(COMPACT·LOOP·SYNCHRONIZE) 기준 전체 68판·23명으로 수는 넘지만 기준을 넘는 카드가 하나도 없음. v1 정의(SYNCHRONIZE·COMPILE_DRIVER·GLASSWORK)로는 30판·10명·부트 57%로 빌드 기준 미달. SYNCHRONIZE는 v0.107.x 런에서만 셈(베타 변경) → core 근거 없음(부트 3%), 불확실로 payoff 유지.
- 유물: 기준을 넘는 유물 없음.
- essentialTarget: 균형 중앙값 1(raw 1, 부트 99%) → 적용
- fullWeight: v1 6.0 기준 멤버 commit 중앙값 0.20·비멤버 0.12 → 2.0 (멤버 0.60, raw 0.77 / 비멤버 0.35, raw 0.35; 판정 표본 멤버 68판·비멤버 395판)
- 대표 런: <https://op.gg/slay-the-spire2/history/3750116> <https://op.gg/slay-the-spire2/history/4100735> <https://op.gg/slay-the-spire2/history/4203755> <https://op.gg/slay-the-spire2/history/4722246>

## 4. 미리 정해 둔 쟁점의 결론

- **DEFRAGMENT**: v1에서는 Baalorlord 가이드 본문만으로 orb_focus에 남겼습니다. 큰 표본에서는 어느 빌드 정의에서도 우수 플레이어 lift 1.1~1.2(부트 0%)라 특정 빌드에 몰리지 않습니다. 보상 화면 픽률은 우수 플레이어 78%(139회)로 높아 **좋은 범용 카드**이지 빌드 카드가 아닙니다. 모든 아키타입에서 뺐고, orb_focus의 `FOCUS` 요구와 이름의 "밀집"도 정리했습니다. 카드의 가치는 티어(tiers)에서 반영됩니다.
- **zero_cost**: v1에서는 1위 플레이어의 구체·상태이상 덱이 대부분이라 약한 빌드였습니다. 큰 표본에서는 CLAW·FERAL·ALL_FOR_ONE·BEAM_CELL·SCRAPE·MOMENTUM_STRIKE·BULK_UP이 함께 쓰이는 뚜렷한 묶음이 있고, 전체 96판·28명(1위 플레이어 28판, 비중 29%)이 씁니다. core는 FERAL(전체 55%·lift 2.9·16명·부트 100%), 1위 플레이어 경로로만 들어왔던 support 7장은 모두 뺐습니다. 우수 플레이어만 보면 1위 플레이어 비중이 54%라 빌드 기준이 흔들려(부트 56%) 전체 플레이어로 판정했고, 1위 플레이어 예외(15판 이상)도 충족합니다.

## 5. 확인했지만 추가하지 않은 빌드 후보

| 후보(서명 카드) | 우수 멤버 덱·명·부트 | 전체 멤버 덱·명·부트 | 결론 |
|---|---|---|---|
| v1에서 삭제한 파워 빌드(CREATIVE_AI·SUBROUTINE·WHITE_NOISE·DOUBLE_ENERGY·SIGNAL_BOOST) | 99·10·96% | 196·31·100% | 멤버 덱 196판 중 96판이 번개 빌드와 겹치고, 기준을 넘는 카드는 STORM 하나(번개 core). 별도 빌드가 아니라 번개 빌드의 일부 |
| WHITE_NOISE·SPINNER·FOCUSED_STRIKE | 26·10·52% | 51·25·100% | 전체 기준으로 수는 넘지만 기준을 넘는 카드가 하나도 없음(가장 가까운 카드도 번개 카드) → 추가 안 함 |
| ECHO_FORM·REFRACT·SYNTHESIS | 16·7·12% | 41·15·68% | 빌드 기준 미달(전체 부트 69%), 카드 근거 없음 |
| UPROAR·ICE_LANCE·SUPERCRITICAL·REFRACT | 20·8·21% | 47·17·96% | 전체 기준 수는 넘지만(부트 96%) 군집으로 묶이지 않고(서로 phi < 0.10) 기준을 넘는 카드가 없음(UPROAR도 4명·부트 31%) → 추가 안 함 |
| GLASSWORK·REFRACT·SPINNER | 5·5·0% | 17·9·15% | 전체 17판, 기준 미달 |
| FUSION·METEOR_STRIKE·RAINBOW | 5·3·0% | 9·7·0% | 전체 9판, 기준 미달 |

## 6. 빌드 정의에 따른 차이(민감도)

| 정의 | 우수 멤버 덱·명·부트 | 전체 멤버 덱·명·부트 | 결과 |
|---|---|---|---|
| orb_focus, v1 냉기 정의(CHILL·COLD_SNAP·GLACIER·HAILSTORM·ICE_LANCE·DEFRAGMENT) | 115·14·100% | 226·40·100% | 서명 카드끼리 함께 쓰이지 않음(CHILL·COLD_SNAP·GLACIER LOO lift 0.9~1.3). 근거 통과 카드는 BARRAGE 하나 |
| orb_focus + 밀집(… BARRAGE·DEFRAGMENT·HOTFIX) | 122·15·100% | 221·38·100% | DEFRAGMENT 우수 lift 1.1·부트 0%로 여전히 탈락 |
| dark_orbs, v1 정의(DARKNESS·SHADOW_SHIELD·NULL·CONSUMING_SHADOW) | 26·8·48% | 41·15·90% | 전체 기준 통과하지만 core 후보 없음(DARKNESS 7명·core 부트 74%) |
| orb_variety, v1 정의(SYNCHRONIZE·COMPILE_DRIVER·GLASSWORK) | 22·4·9% | 30·10·57% | 빌드 기준 미달, 1위 플레이어 12판(예외 15판 미달) |
| zero_cost, v1 정의(CLAW·FERAL·ALL_FOR_ONE·SCRAPE·MOMENTUM_STRIKE) | 55·9·67% | 101·25·100% | 같은 결론(FERAL·CLAW·ALL_FOR_ONE·BEAM_CELL 통과) |

## 7. 불확실 항목

- `defect.orb_focus`: HAILSTORM
- `defect.lightning`: BALL_LIGHTNING, BUFFER, LIGHTNING_ROD, SUPERCRITICAL, TEMPEST, TESLA_COIL, VOLTAIC, 유물 INFUSED_CORE, coreTarget 2
- `defect.status`: COMPACT, FTL, SMOKESTACK, TRASH_TO_TREASURE
- `defect.zero_cost`: BULK_UP
- `defect.dark_orbs`: COMPACT, DARKNESS, 유물 DATA_DISK
- `defect.orb_variety`: COMPACT, SYNCHRONIZE
- 경계선: ITERATION(status, 우수 lift 0.9997)과 SUBROUTINE(lightning, 0.9994)은 구제 기준 lift 1.0에 아주 조금 못 미쳐 빠졌습니다. 전체 플레이어 기준으로는 lift 1.5·1.6이지만 부트 49%·67%로 불안정합니다.

## 8. 한계

- 승리 덱의 최종 덱을 봅니다. "이 카드가 있으면 이긴다"가 아니라 "상위 플레이어가 이길 때 이 카드를 들고 있었다"는 뜻입니다. 패배 덱(우수 플레이어, 8층 이상)은 core 카드의 참고 비교에만 썼습니다.
- 우수 플레이어가 17명이라 플레이어 단위 부트스트랩에서 흔들리는 항목이 많습니다. 그런 항목은 v1 값을 유지하고 "불확실"로 표시했습니다.
- Baalorlord 수치(덱 8판 중 보유)는 v1 조사 값을 그대로 썼습니다.

분석 스크립트(작업 폴더, `python3 -I`): `dcom.py`(로더), `cluster.py`(군집), `engine.py`(멤버십·카드·유물 수치, 부트스트랩), `decide.py`(판정 규칙), `finalize.py`(역할·구제·유물·목표치·fullWeight), `gen.py`/`doc.py`(출력), `combos_check.py`(콤보 동시 출현).
