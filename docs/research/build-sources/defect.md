# 디펙트 빌드 카드 근거 (상위 플레이어 실전 덱)

`data/packs/v0.107.1/archetypes/defect.json`의 카드 목록이 어디서 왔는지 정리한 문서입니다. 모든 카드는 아래 출처 중 하나로 뒷받침됩니다. Claude의 판단이나 익명 커뮤니티 글은 근거로 쓰지 않았습니다.

## 출처와 표본

| 출처 | 내용 | 비중 |
|---|---|---|
| **OP.GG** (1차) | 주간 최단 승리 상위 100(2026-11-02~08) + 목록에 오른 플레이어의 최근 승리. A10·싱글·바닐라(모드 수정자 런 제외). 디펙트 승리 **50판·22명**. v0.107.1 29판, v0.111.0 21판. OP.GG 랭킹 1위 플레이어(솔로 787판·승률 64.4%·최고 44연승)가 12판(전부 v0.107.1) | 주 근거 |
| **Baalorlord** (2차) | A10 디펙트 10연승(https://baalorlord.tv/runs/1793121763) 포함 승리 덱 8판(2026-09-29~11-05)과 Untapped 가이드 "Defect Build Guide: Orb Spam"(https://sts2.untapped.gg/en/guides/defect-build-orb-spam). 덱 8판 중 ≥4판: TURBO 8, HOLOGRAM 7, GLACIER 6, SHADOW_SHIELD·COOLHEADED·CHARGE_BATTERY·ITERATION·OVERCLOCK 5, CAPACITOR·BOOST_AWAY·COMPACT·WHITE_NOISE·SUBROUTINE·MODDED·BARRAGE 4 | 보조 근거 |
| Spire Codex A10 Lift | 참고만. 어떤 카드도 이것만으로 넣지 않음 | 맥락 |

런 링크 형식: `https://op.gg/slay-the-spire2/history/<runId>`.

## 판정 규칙 (다섯 캐릭터 공통)

- **빌드 성립**: 멤버 덱 ≥6판·≥4명, 한 명이 절반 초과 금지. 예외: 1위 플레이어가 ≥4번 이긴 빌드("1위 플레이어 빌드").
- **멤버십**: 기존 아키타입은 현재 core/enabler/payoff 중 다른 아키타입과 겹치지 않고 전체 덱 60% 미만인 카드로 시그니처를 만들고, 그 중 `max(2, ⌈0.3×(시그니처−1)⌉)`장 이상을 가진 덱. 새 빌드는 정의 카드 2장 이상. 평가 카드는 멤버 판정에서 뺌(leave-one-out, LOO).
- **카드 통과**: 멤버 덱 보유 ≥3명, lift(멤버/비멤버) ≥1.5, 점유율 ≥25% 또는 (≥15%이고 빌드 안에서 ≥5번 중 ≥50% 픽). 1위 플레이어 빌드는 "그의 멤버 덱 ≥50% + lift ≥1.5"(1위 경로)로도 통과.
- **보조 출처 구제**: OP.GG 기준 미달이어도 Baalorlord 덱 ≥50%에 있고, 카드 효과가 빌드 축에 직접 작용하며, OP.GG lift ≥1.0이면 enabler/payoff/support로 등재(core 불가).
- **core**: 통과 카드 중 고급/희귀, 효과상 빌드 엔진, 점유율 ≥40%, ≥4명(1위 빌드는 그의 덱 ≥50%). 최대 3장. 없으면 비움.
- **coreTarget 2**: 멤버 덱 ≥50%가 core 2장을 함께 보유할 때.
- **베타 변경 카드**(†: THUNDER, ROCKET_PUNCH, TRASH_TO_TREASURE, SHATTER, SYNCHRONIZE, NULL 등)는 v0.107.x 런만으로 수치를 냄.
- lift 1.5~1.6은 "경계선"으로 표시.

표 열: 점유율(LOO) = 보유 멤버 덱/멤버 덱, 빌드 내 픽 = 멤버 덱의 보상 화면에서 고른 수/제시된 수, 1위 = 1위 플레이어 멤버 덱 중 보유/그의 멤버 덱, Baalorlord 덱 = 그의 A10 승리 덱 8판 중 보유(≤2/8은 집계 목록 밖).

---

## defect.orb_focus — 냉기·밀집 구체 (유지, core 비움)

표본: 멤버 덱 18판·11명(1위 플레이어 4판, 한 명 최대 4판), v0.107.1 10·v0.111.0 8. 빌드 기준 통과.

| 카드 | 역할 | 점유율(LOO) | lift | 플레이어 | 빌드 내 픽 | 1위 | Baalorlord 덱 | 통과 |
|---|---|---|---|---|---|---|---|---|
| CHILL | enabler | 64% (7/11) | 1.77 | 5 | 6/9 | 1/3 | 3/8 | OP.GG(점유율) |
| COLD_SNAP | enabler | 38% (6/16) | 1.82 | 5 | 4/26 | 0/4 | 3/8 | OP.GG(점유율) |
| GLACIER | enabler | 40% (6/15) | 1.27 | 5 | 9/12 | 2/3 | 6/8 | 보조 출처 구제 |
| COOLHEADED | enabler | 83% (15/18) | 1.33 | 11 | 20/38 | 3/4 | 5/8 | 보조 출처 구제 |
| DEFRAGMENT | enabler | 33% (5/15) | 1.30 | 5 | 6/9 | 1/4 | ≤2/8 | **예외(근거 부족)** |
| HAILSTORM | payoff | 33% (5/15) | 2.33 | 4 | 5/11 | 0/3 | ≤2/8 | OP.GG(점유율) |
| SHADOW_SHIELD | support | 44% (8/18) | 2.37 | 7 | 8/11 | 0/4 | 5/8 | OP.GG(점유율) |
| BOOT_SEQUENCE | support | 44% (8/18) | 2.37 | 7 | 5/9 | 0/4 | ≤2/8 | OP.GG(점유율) |
| LIGHTNING_ROD | support | 39% (7/18) | 2.07 | 5 | 4/41 | 2/4 | ≤2/8 | OP.GG(점유율) |
| SCAVENGE | support | 33% (6/18) | 1.78 | 4 | 6/14 | 1/4 | ≤2/8 | OP.GG(점유율) |
| NULL † | support | 30% (3/10) | 1.90 | 3 | 3/6 | 1/4 | 3/8 | OP.GG(점유율) |
| BEAM_CELL | support | 28% (5/18) | 1.78 | 4 | 4/29 | 2/4 | ≤2/8 | OP.GG(점유율) |
| ADAPTIVE_STRIKE | support | 22% (4/18) | ∞ | 4 | 3/5 | 1/4 | ≤2/8 | OP.GG(픽률) |
| FLAK_CANNON | support | 22% (4/18) | 1.78 | 3 | 4/5 | 2/4 | ≤2/8 | OP.GG(픽률) |
| CAPACITOR | support | 50% (7/14) | 1.38 | 5 | 3/6 | 2/2 | 4/8 | 보조 출처 구제 |

- **core 없음.** CHILL은 수치상 core 기준(64%·5명)을 넘지만 다른 냉기 카드로 바꿔 쓰는 공급 카드라 enabler. 밀집 엔진인 DEFRAGMENT는 lift 1.3으로 카드 기준 미달. 이전 core GLACIER(lift 1.27)는 Baalorlord 덱 6/8로 구제되어 enabler(구제 카드는 core 불가).
- **DEFRAGMENT 예외(근거 부족)**: OP.GG(lift 1.30)와 Baalorlord 덱(≤2/8) 모두 미달. 유일한 출처는 Baalorlord 본인의 Untapped 가이드 "Wall of Ice" 본문(이 빌드의 성장 카드로 직접 이름을 듦)이고, 이 아키타입의 `FOCUS` 요구를 채우는 유일한 보상 카드라 enabler로 남김. 아래 "미해결 충돌" 참고.
- guideHeroCard: DEFRAGMENT → CHILL(멤버 덱에서 가장 많이 보유한 OP.GG 통과 카드).
- keyRelics: DATA_DISK(멤버 5/18·4명·lift 1.48), RUNIC_CAPACITOR(3/18·3명·2.67), GOLD_PLATED_CABLES(4/18·4명·2.37) 유지. EMOTION_CHIP(멤버 덱 0판)·SYMBIOTIC_VIRUS(2명·1.19) 제외.
- 대표 덱: https://op.gg/slay-the-spire2/history/7089417 · https://op.gg/slay-the-spire2/history/7304876 · https://op.gg/slay-the-spire2/history/7211179 · https://op.gg/slay-the-spire2/history/7174863 (1위 플레이어)

**제외한 카드**

| 카드 (이전 역할) | 수치 | 이유 |
|---|---|---|
| DARKNESS (enabler) | 18%·lift 1.16·3명 | 이 빌드에선 미달 → 새 `dark_orbs`로 이동 |
| SYNCHRONIZE † (payoff) | 11%·lift 0.56·1명 | 미달 → 새 `orb_variety`(1위 플레이어 빌드)의 core |
| SHATTER † (payoff) | 33%·lift 2.22·**2명** | 플레이어 수 미달 (status에서는 통과) |
| COOLANT (payoff) | 19%·lift 3.19·2명, 전체 픽 3/13 | 플레이어 수·점유율 미달 |
| LOOP (payoff) | 12%·lift 0.85 | lift 미달. Baalorlord 가이드엔 있으나 덱 ≤2/8 |
| MULTI_CAST (payoff) | 6%·1명, 전체 픽 0/9 | 거의 안 씀 |
| CONSUMING_SHADOW (enabler) | 멤버 덱 0판, 전체 픽 1/16 | 거의 안 씀 |
| RAINBOW (enabler) | 11%·2명, 전체 픽 2/18 | 미달 |
| BIASED_COGNITION † (enabler) | 1판 | 고대 카드, 표본 1판 |
| MODDED (support) | 22%·lift 0.79 | lift <1.0이라 Baalorlord 4/8이어도 구제 불가 |
| HOTFIX (support) | 22%·lift 2.37, 빌드 내 픽 3/40 | 점유율 <25%이고 픽률 미달 |
| CHAOS (support) | 6%·lift 0.44 | 미달 |

## defect.lightning — 번개 (유지, core THUNDER + STORM, coreTarget 2)

표본: 멤버 덱 20판·14명(1위 플레이어 2판, 한 명 최대 2판), v0.107.1 12·v0.111.0 8. THUNDER·STORM 동시 보유 12/20(60%), v0.107만 8/12(67%) → coreTarget 2.

| 카드 | 역할 | 점유율(LOO) | lift | 플레이어 | 빌드 내 픽 | 1위 | Baalorlord 덱 | 통과 |
|---|---|---|---|---|---|---|---|---|
| THUNDER † | core | 80% (8/10) | 7.60 | 7 | 9/9 | 1/1 | ≤2/8 | OP.GG(점유율) |
| STORM | core | 60% (12/20) | 3.00 | 9 | 11/16 | 2/2 | ≤2/8 | OP.GG(점유율) |
| BALL_LIGHTNING | enabler | 47% (7/15) | 1.81 | 6 | 9/32 | 0/2 | 3/8 | OP.GG(점유율) |
| LIGHTNING_ROD | enabler | 41% (7/17) | 2.26 | 7 | 6/30 | 1/1 | ≤2/8 | OP.GG(점유율) |
| TEMPEST | enabler | 26% (5/19) | 8.16 | 5 | 4/12 | 0/2 | ≤2/8 | OP.GG(점유율) |
| TESLA_COIL | payoff | 37% (7/19) | 11.42 | 6 | 7/19 | 1/2 | ≤2/8 | OP.GG(점유율) |
| VOLTAIC | payoff | 32% (6/19) | 3.26 | 6 | 6/8 | 1/2 | ≤2/8 | OP.GG(점유율) |
| CHILL | support | 60% (12/20) | 2.00 | 11 | 10/16 | 1/2 | 3/8 | OP.GG(점유율) |
| FIGHT_THROUGH | support | 45% (9/20) | 3.38 | 7 | 9/19 | 0/2 | ≤2/8 | OP.GG(점유율) |
| SUBROUTINE | support | 45% (9/20) | 2.70 | 8 | 6/16 | 1/2 | 4/8 | OP.GG(점유율) |
| BUFFER | support | 35% (7/20) | 2.62 | 5 | 6/13 | 1/2 | ≤2/8 | OP.GG(점유율) |
| COLD_SNAP | support | 35% (7/20) | 1.75 | 7 | 4/39 | 0/2 | 3/8 | OP.GG(점유율) |
| NULL † | support | 33% (4/12) | 2.83 | 3 | 3/8 | 0/2 | 3/8 | OP.GG(점유율) |
| SUPERCRITICAL | support | 20% (4/20) | 1.50 (경계선) | 3 | 4/8 | 0/2 | ≤2/8 | OP.GG(픽률) |
| MODDED | support | 30% (6/20) | 1.29 | 5 | 4/9 | 0/2 | 4/8 | 보조 출처 구제 |

- Baalorlord 가이드 "Pure Lightning"이 THUNDER·STORM·TEMPEST·VOLTAIC을 명시. 모두 OP.GG 기준도 통과해 두 출처가 일치합니다. 그의 번개 덱 1791309099(TESLA_COIL·LIGHTNING_ROD·BALL_LIGHTNING)·1790705262(STORM)와도 맞습니다.
- 정의 카드 2장 이상 기준(B1)으로 다시 재도 THUNDER 100%(8/8)·STORM 73%로 같은 core.
- STORM은 이전 enabler → core. CHILL·FIGHT_THROUGH·SUBROUTINE은 수치상 core 기준을 넘지만 번개 축 엔진이 아니라 support.
- keyRelics: INFUSED_CORE(3/20·3명·lift 1.50) 유지. METRONOME(멤버 덱 0판) 제외.
- 대표 덱: https://op.gg/slay-the-spire2/history/7196827 · https://op.gg/slay-the-spire2/history/6977215 · https://op.gg/slay-the-spire2/history/7088904 · https://op.gg/slay-the-spire2/history/7171766 (1위 플레이어)

**제외한 카드**: CAPACITOR(support; 35%·lift 0.81, Baalorlord 4/8이지만 lift <1.0), DEFRAGMENT(support; 30%·lift 1.12), BARRAGE(support; 25%·lift 0.83, Baalorlord 4/8이지만 lift <1.0), COMPILE_DRIVER(support; 5%·lift 0.17, 1위 플레이어 빌드 카드), RAINBOW(enabler; 10%·2명·전체 픽 2/18).

## defect.status — 상태이상 재활용 (유지, core ROCKET_PUNCH + COMPACT, coreTarget 2)

표본: 멤버 덱 29판·14명(1위 플레이어 7판, 한 명 최대 7판), v0.107.1 19·v0.111.0 10. 두 core 동시 보유: v0.107 10/19(53%), 전체 12/29(41%).

| 카드 | 역할 | 점유율(LOO) | lift | 플레이어 | 빌드 내 픽 | 1위 | Baalorlord 덱 | 통과 |
|---|---|---|---|---|---|---|---|---|
| ROCKET_PUNCH † | core | 71% (12/17) | 4.24 | 8 | 11/16 | 2/5 | ≤2/8 | OP.GG(점유율) |
| COMPACT | core | 52% (13/25) | 2.17 | 10 | 14/18 | 1/5 | 4/8 | OP.GG(점유율) |
| GUNK_UP | enabler | 34% (10/29) | 7.24 | 8 | 9/52 | 1/7 | ≤2/8 | OP.GG(점유율) |
| TURBO | enabler | 79% (23/29) | 1.04 | 13 | 22/57 | 6/7 | 8/8 | 보조 출처 구제 |
| OVERCLOCK | enabler | 46% (12/26) | 1.38 | 11 | 13/19 | 1/6 | 5/8 | 보조 출처 구제 |
| BOOST_AWAY | enabler | 65% (15/23) | 1.47 | 13 | 12/38 | 1/5 | 4/8 | 보조 출처 구제 |
| TRASH_TO_TREASURE † | payoff | 32% (6/19) | 3.16 | 4 | 5/5 | 2/7 | ≤2/8 | OP.GG(점유율) |
| SMOKESTACK | payoff | 25% (7/28) | 5.50 | 5 | 5/20 | 0/7 | ≤2/8 | OP.GG(점유율) |
| ITERATION | payoff | 42% (10/24) | 1.35 | 7 | 7/14 | 2/4 | 5/8 | 보조 출처 구제 |
| BULK_UP | support | 52% (15/29) | 1.81 | 11 | 14/30 | 4/7 | ≤2/8 | OP.GG(점유율) |
| WHITE_NOISE | support | 45% (13/29) | 1.88 | 8 | 13/26 | 4/7 | 4/8 | OP.GG(점유율) |
| SHATTER † | support | 32% (6/19) | ∞ | 4 | 5/10 | 2/7 | ≤2/8 | OP.GG(점유율) |
| FTL | support | 28% (8/29) | 1.93 | 8 | 7/13 | 1/7 | ≤2/8 | OP.GG(점유율) |
| NULL † | support | 26% (5/19) | 2.63 | 4 | 4/13 | 1/7 | 3/8 | OP.GG(점유율) |

- ROCKET_PUNCH는 공격 카드지만 효과가 상태이상 생성과 맞물려 반복되는 드로우 엔진이라(마무리 카드가 아님) core로 둠. COMPACT는 상태이상을 자원으로 바꾸는 엔진.
- 이전 core SMOKESTACK(25%, 전체 픽 6/33, 1위 플레이어 0/8)·TRASH_TO_TREASURE(32%)는 core 점유율 40%에 못 미쳐 payoff로.
- TURBO·OVERCLOCK·BOOST_AWAY·ITERATION은 OP.GG lift가 1.5에 못 미치지만(1.04~1.47) Baalorlord 덱 ≥4/8이고 효과가 상태이상을 만들거나 소비해 구제.
- guideHeroCard: SMOKESTACK → ROCKET_PUNCH. keyRelics 없음(변경 없음).
- 대표 덱: https://op.gg/slay-the-spire2/history/7265571 · https://op.gg/slay-the-spire2/history/7207988 · https://op.gg/slay-the-spire2/history/7161542 · https://op.gg/slay-the-spire2/history/7090349 (1위 플레이어)

**제외한 카드**: FIGHT_THROUGH(enabler; 31%·lift 1.48, Baalorlord ≤2/8 — 번개 빌드에선 support로 통과), FLAK_CANNON(payoff; 19%·lift 1.42).

## defect.zero_cost — 0코스트 (유지, OP.GG 랭킹 1위 플레이어의 빌드, core 비움)

표본: 멤버 덱 10판·5명, 그중 1위 플레이어 6판(v0.107.1 9·v0.111.0 1). 한 명이 절반을 넘어 일반 빌드 기준은 미달이지만, 1위 플레이어가 ≥4번 이긴 빌드라 **1위 플레이어 빌드**로 성립. 카드는 1위 경로(그의 멤버 덱 ≥50%·lift ≥1.5) 또는 일반 기준(≥3명 등) 중 하나로 통과.

| 카드 | 역할 | 점유율(LOO) | lift | 플레이어 | 빌드 내 픽 | 1위 | Baalorlord 덱 | 통과 |
|---|---|---|---|---|---|---|---|---|
| ALL_FOR_ONE | payoff | 30% (3/10) | ∞ | 3 | 3/3 | 0/6 | ≤2/8 | OP.GG(점유율) |
| SKIM | support | 80% (8/10) | 2.29 | 4 | 9/15 | 5/6 | ≤2/8 | 1위 경로 + OP.GG(점유율) |
| BULK_UP | support | 70% (7/10) | 2.00 | 4 | 7/12 | 4/6 | ≤2/8 | 1위 경로 + OP.GG(점유율) |
| OVERCLOCK | support | 60% (6/10) | 1.71 | 5 | 7/10 | 2/6 | 5/8 | OP.GG(점유율) |
| CAPACITOR | support | 60% (6/10) | 1.71 | 2 | 4/5 | 5/6 | 4/8 | 1위 경로 |
| FLAK_CANNON | support | 40% (4/10) | 4.00 | 2 | 3/3 | 3/6 | ≤2/8 | 1위 경로 |
| SYNCHRONIZE † | support | 33% (3/9) | 3.33 | 1 | 3/6 | 3/6 | ≤2/8 | 1위 경로 |
| BARRAGE | support | 40% (4/10) | 1.60 (경계선) | 2 | 2/16 | 3/6 | 4/8 | 1위 경로 |
| COMPILE_DRIVER | support | 30% (3/10) | 1.71 | 1 | 0/17 | 3/6 | ≤2/8 | 1위 경로 |

- core 없음: 통과 카드 중 0코스트 축 엔진이 없음. 축 payoff인 ALL_FOR_ONE은 1위 플레이어 멤버 덱 0/6이라 core 기준(그의 덱 ≥50%) 미달. 이름에서 "(클로)"를 뺌(CLAW 제외).
- 1위 경로로 통과한 CAPACITOR·FLAK_CANNON·SYNCHRONIZE·BARRAGE·COMPILE_DRIVER는 그의 다른 빌드(구체 다양성·상태이상) 카드가 같은 덱에 섞인 것으로, 0코스트 축과 무관해 모두 support입니다.
- 교차 확인: CLAW·FERAL·ALL_FOR_ONE·SCRAPE·MOMENTUM_STRIKE 중 2장 이상 기준(B7)은 5판·4명(1위 2판)으로 얇음. 이 기준에선 정의 카드 자체가 모두 2명 이하라 통과하지 못함.
- TURBO(80%·lift 1.03·Baalorlord 8/8)는 비용만 0이고 효과가 0코스트 축에 작용하지 않아 구제하지 않음.
- keyRelics: POWER_CELL(멤버 덱 0판) 제외 → 없음. fullWeight 3.5 유지(멤버 중앙값 0.72, 비멤버 0.29).
- 대표 덱: https://op.gg/slay-the-spire2/history/7087814 · https://op.gg/slay-the-spire2/history/7121486 · https://op.gg/slay-the-spire2/history/7161542 · https://op.gg/slay-the-spire2/history/7278203 (1위 플레이어)

**제외한 카드**: FERAL(core; 1명·전체 픽 4/33), CLAW(payoff; 멤버 덱 1명·1위 플레이어 멤버 덱 0/3(LOO), 전체 7판 중 5판이 1위 플레이어, 전체 픽 8/89), ADAPTIVE_STRIKE·SCRAPE †·MOMENTUM_STRIKE †(1~2명), BEAM_CELL·GO_FOR_THE_EYES·FTL(2명, 1위 멤버 덱 0/2~0/5), HELIX_DRILL(0판), HOTFIX(0판), TURBO(lift 1.03, 위 참고), BOOST_AWAY(lift 0.91).

## defect.dark_orbs — 암흑 구체 (새 아키타입, core 비움)

표본: DARKNESS·SHADOW_SHIELD·NULL·SPINNER·CONSUMING_SHADOW 중 2장 이상 보유 덱 **7판·4명**(플레이어별 3·2·1·1판, 1위 플레이어 1판), v0.107.1 4·v0.111.0 3. 빌드 기준(≥6판·≥4명·한 명 절반 이하) 통과.

| 카드 | 역할 | 점유율(LOO) | lift | 플레이어 | 빌드 내 픽 | 1위 | Baalorlord 덱 | 통과 |
|---|---|---|---|---|---|---|---|---|
| DARKNESS | enabler | 80% (4/5) | 9.00 | 3 | 4/4 | 1/1 | 3/8 | OP.GG(점유율) |
| SHADOW_SHIELD | enabler | 100% (4/4) | 4.60 | 3 | 3/3 | 1/1 | 5/8 | OP.GG(점유율) |
| BARRAGE | payoff | 43% (3/7) | 1.68 | 3 | 3/15 | 1/1 | 4/8 | OP.GG(점유율) |
| MACHINE_LEARNING | support | 71% (5/7) | 2.79 | 4 | 5/6 | 1/1 | ≤2/8 | OP.GG(점유율) |
| BALL_LIGHTNING | support | 71% (5/7) | 2.79 | 3 | 5/12 | 1/1 | 3/8 | OP.GG(점유율) |
| COMPACT | support | 57% (4/7) | 1.64 | 3 | 4/6 | 1/1 | 4/8 | OP.GG(점유율) |
| COOLHEADED | support | 100% (7/7) | 1.54 (경계선) | 4 | 8/14 | 1/1 | 5/8 | OP.GG(점유율) |
| CAPACITOR | support | 43% (3/7) | 1.08 | 2 | 1/3 | 0/1 | 4/8 | 보조 출처 구제 |
| MODDED | support | 29% (2/7) | 1.12 | 1 | 0/4 | 0/1 | 4/8 | 보조 출처 구제 |

- core 없음: DARKNESS·SHADOW_SHIELD는 축 엔진이지만 3명뿐(core는 4명 필요). guideHeroCard = SHADOW_SHIELD(멤버 7/7, COOLHEADED와 동률 중 축 카드).
- Baalorlord: 가이드 "Lord of Darkness"(DARKNESS·LOOP·MULTI_CAST·CONSUMING_SHADOW·HOLOGRAM 언급)와 그의 덱 1790881970(DARKNESS×2·SHADOW_SHIELD×3). LOOP·MULTI_CAST·CONSUMING_SHADOW는 OP.GG 멤버 덱에서 0~1명이라 넣지 않음. NULL †(1명)·SPINNER(2명)도 미달.
- needs: ORB_DARK ≥2 (멤버 6/7이 암흑 공급 카드 2장 이상). keyRelics: DATA_DISK(3/7·3명·lift 2.3). 같은 수치 기준을 넘은 VAJRA·VENERABLE_TEA_SET·POTION_BELT·ODDLY_SMOOTH_STONE·BRONZE_SCALES·PEAR는 효과가 구체와 무관해 넣지 않음.
- 대표 덱: https://op.gg/slay-the-spire2/history/7167664 · https://op.gg/slay-the-spire2/history/7153329 · https://op.gg/slay-the-spire2/history/7060909 · https://op.gg/slay-the-spire2/history/7215815 (1위 플레이어)

## defect.orb_variety — 구체 다양성 (새 아키타입, OP.GG 랭킹 1위 플레이어의 빌드)

표본: SYNCHRONIZE·COMPILE_DRIVER·GLASSWORK 중 2장 이상 보유 덱 **5판, 전부 1위 플레이어**(v0.107.1). 다른 플레이어 0판. 1위 플레이어 빌드 규칙(≥4승)으로 성립. 카드는 1위 경로(그의 멤버 덱 ≥50%·lift ≥1.5)로 판정. 정의 카드는 3장뿐이라 LOO 멤버 수가 3판까지 줄어듭니다.

| 카드 | 역할 | 점유율(LOO) | lift | 플레이어 | 빌드 내 픽 | 1위 | Baalorlord 덱 | 통과 |
|---|---|---|---|---|---|---|---|---|
| SYNCHRONIZE † | core | 100% (3/3) | 13.00 | 1 | 3/3 | 3/3 | ≤2/8 | 1위 경로 |
| GLASSWORK | enabler | 60% (3/5) | 9.00 | 1 | 3/5 | 3/5 | ≤2/8 | 1위 경로 |
| GLACIER | enabler | 40% (2/5) | 1.20 | 1 | 1/1 | 2/5 | 6/8 | 보조 출처 구제 |
| COOLHEADED | enabler | 80% (4/5) | 1.16 | 1 | 4/9 | 4/5 | 5/8 | 보조 출처 구제 |
| COMPILE_DRIVER | payoff | 100% (3/3) | 6.71 | 1 | 1/6 | 3/3 | ≤2/8 | 1위 경로 |
| COMPACT | support | 80% (4/5) | 2.40 | 1 | 5/5 | 4/5 | 4/8 | 1위 경로 |
| SKIM | support | 80% (4/5) | 2.00 | 1 | 4/5 | 4/5 | ≤2/8 | 1위 경로 |
| CAPACITOR | support | 60% (3/5) | 1.59 (경계선) | 1 | 0/1 | 3/5 | 4/8 | 1위 경로 |
| MODDED | support | 60% (3/5) | 2.70 | 1 | 3/4 | 3/5 | 4/8 | 1위 경로 |
| ITERATION | support | 60% (3/5) | 1.80 | 1 | 2/3 | 3/5 | 5/8 | 1위 경로 |
| CLAW | support | 60% (3/5) | 6.75 | 1 | 5/14 | 3/5 | ≤2/8 | 1위 경로 |

- core SYNCHRONIZE: 구체 가짓수를 성장으로 바꾸는 엔진, 그의 멤버 덱 5/5 보유. COMPILE_DRIVER도 5/5지만 일반 카드라 core 불가 → payoff.
- COMPACT·SKIM·ITERATION·CLAW는 그의 다른 덱에도 흔한 카드라(그의 12판 중 SKIM 10, ITERATION 8) 빌드 고유성보다 선수 성향일 수 있습니다. 규칙상 lift ≥1.5라 support로 둠.
- Baalorlord 가이드가 SYNCHRONIZE를 "섞인 구체" 성장 카드로 언급(덱 1790705262에도 있음). 다른 상위 플레이어 덱에는 이 빌드가 없음.
- needs: ORB_FROST ≥1(멤버 5/5가 냉기 공급 카드 보유, 시작 번개와 합쳐 두 종류 이상). keyRelics: 없음(1명뿐이라 3명 기준 불가).
- 대표 덱(모두 1위 플레이어): https://op.gg/slay-the-spire2/history/7090349 · https://op.gg/slay-the-spire2/history/7203955 · https://op.gg/slay-the-spire2/history/7208198

## 삭제: defect.powers — 파워

- 확인한 것: 이 아키타입 고유 카드 5장(CREATIVE_AI·SIGNAL_BOOST·SUBROUTINE·SYNTHESIS·WHITE_NOISE) 중 3장 이상 보유 덱 **2판·2명**(4판 미만). 다섯 장끼리 동시 출현 상관(phi) 평균 0.01.
- 2장 이상으로 느슨하게 잡으면 14판·9명이지만, 그 덱에서 기준을 넘는 카드는 STORM·THUNDER·DEFRAGMENT·HAILSTORM·SKIM 같은 번개·냉기 카드라 실체는 번개/냉기 혼합 덱입니다. 그 기준에서도 고유 카드는 모두 미달: CREATIVE_AI 25%·2명, WHITE_NOISE lift 0.92, SUBROUTINE 0.88, SIGNAL_BOOST 1명(전체 픽 5/20), SYNTHESIS 0판(전체 픽 4/31).
- 이전 core STORM은 번개 core로 이동(번개에서 lift 3.0). SUBROUTINE·BUFFER는 번개 support로 통과. ECHO_FORM(lift 1.10)·MACHINE_LEARNING(0.59, 암흑 구체에선 통과)·LOOP·CAPACITOR는 이 빌드 근거 없음.
- Baalorlord 가이드에도 파워 빌드는 없음(CREATIVE_AI·ECHO_FORM은 "Wall of Ice"의 보스용 성장 카드로만 언급, 덱 3/8·≤2/8).

## 미해결 충돌·주의

1. **DEFRAGMENT (orb_focus enabler)**: 공통 규칙상 OP.GG(lift 1.30)·Baalorlord 덱(≤2/8) 모두 미달이라 빠져야 하지만, 유일한 출처인 Baalorlord 본인의 "Wall of Ice" 가이드 본문이 이 빌드에 직접 이름을 들었고 `FOCUS` 요구를 채우는 유일한 보상 카드라 예외로 남겼습니다. 빼려면 `needs`의 `FOCUS`와 이름의 "밀집"도 함께 정리해야 합니다.
2. **status coreTarget 2**: ROCKET_PUNCH가 베타 변경 카드라 v0.107 기준(53%)으로 2를 썼습니다. 전체 버전으로는 41%라 경계선입니다.
3. **Baalorlord 가이드에만 있는 카드**: LOOP, CONSUMING_SHADOW, MULTI_CAST, ECHO_FORM, CREATIVE_AI, HOTFIX, FOCUSED_STRIKE, SUNDER, CLAW(얼음 빌드용). OP.GG 멤버 덱 근거가 없고 그의 덱 50% 미만이라 넣지 않았습니다. HOLOGRAM(그의 덱 7/8, "Lord of Darkness"에 언급)은 효과가 구체 축에 직접 작용하지 않아 구제하지 않았습니다.
4. **범용 카드의 보조 출처 구제**: TURBO(status, 전체 덱 78%)·COOLHEADED·GLACIER는 거의 모든 덱에 들어가는 카드라 commit을 넓게 올립니다. fullWeight 보정으로 비멤버 중앙값을 0.5 이하로 맞췄습니다.
5. **zero_cost와 orb_variety는 1위 플레이어 빌드**: zero_cost 멤버 10판 중 6판, orb_variety 5판 전부가 그의 덱입니다. zero_cost의 1위 경로 카드 대부분은 0코스트 축이 아니라 그의 다른 빌드 카드입니다.
6. **v0.111 전용 묶음**(UPROAR·ICE_LANCE·SUPERCRITICAL·REFRACT): 멤버 10판이 전부 v0.111이라 v0.107.1 팩에는 반영하지 않았습니다. SUPERCRITICAL만 번개 support로 통과(v0.107·v0.111 합산, lift 1.50 경계선).
7. Spire Codex A10 Lift는 맥락으로만 봤습니다(디펙트에선 ECHO_FORM만 +2 이상). 이것만으로 넣은 카드는 없습니다.

## fullWeight 보정 (§5.1 commit, 디펙트 OP.GG 승리 50판)

| 아키타입 | fullWeight | 멤버 commit 중앙값 | 비멤버 중앙값 |
|---|---|---|---|
| orb_focus | 4.5 (유지) | 1.00 | 0.46 |
| lightning | 3.5 (유지) | 1.00 | 0.34 |
| status | 3.5 → **6.0** | 1.00 → 0.87 | 0.54 → 0.32 |
| zero_cost | 3.5 (유지) | 0.72 | 0.29 |
| dark_orbs (새) | 4.0 | 1.00 | 0.42 |
| orb_variety (새) | 4.0 → **6.0** | 1.00 → 0.84 | 0.51 → 0.34 |

## 재현

분석 스크립트(작업 폴더, `python3 -I`): `dfx_eval.py`(멤버십·카드 판정), `dfx_extra.py`(보조 출처 후보·유물·coreTarget), `dfx_commit.py`(fullWeight·대표 덱), `dfx_doc.py`(표). 원자료: OP.GG 런 기록(`merged_vanilla_a10.json`), Baalorlord 런 로그와 Untapped 가이드.
