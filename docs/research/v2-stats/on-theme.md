# 빌드 밖 감점 예외 카드(onTheme) 근거

아키타입 파일의 `onTheme` 목록을 어떻게 정했는지와 수치입니다. 이 카드들은 빌드 점수(시너지)는 받지 않고, 빌드가 확정된 뒤 "빌드 밖 카드" 감점만 면제됩니다.

## 방법

- 표본: OP.GG 주간 상위 100에 오른 플레이어들의 A10 바닐라 솔로 승리(v0.107 이상). 판정은 좋은 플레이어(A10 승리 5판 이상, 실제 승률 25% 이상) 기준이고, 좋은 플레이어 표본이 얇을 때(멤버 덱 25판 미만이거나 그 카드를 든 플레이어 5명 미만)만 전체 플레이어로 다시 판정합니다.
- 멤버 덱: 그 아키타입의 commit(DATA_SCHEMA §5.1 공식, 이 파일의 역할 목록·핵심 유물·fullWeight 그대로)이 commitThreshold(0.7) 이상인 최종 덱. 앱이 빌드 밖 감점을 거는 바로 그 덱입니다.
- 카드 기준: 멤버 덱에서 그 카드를 든 비율 ÷ 같은 캐릭터의 다른 승리 덱에서 든 비율(lift) ≥ 1.5, 멤버 덱에서 그 카드를 든 플레이어 ≥ 5명. 비율은 플레이어 균형(한 플레이어 비중 10% 상한)이고, 두 조건이 플레이어 200회 재표본 중 80% 이상에서 함께 성립해야 합니다.
- 대상: 그 캐릭터 카드와 무색·이벤트 카드 중 기본·저주·상태이상·토큰·퀘스트가 아닌 카드. 그 아키타입의 역할 목록(core·enabler·payoff·support)에 이미 있는 카드는 뺍니다(다른 아키타입의 역할 카드는 들어갈 수 있습니다). 베타에서 바뀐 카드는 v0.107.x 판만 셉니다.

## 결과

| 아키타입 | 멤버 덱(좋은/전체, 플레이어) | 카드 | 판정 | 멤버 덱 점유율 | lift | 든 플레이어 | 부트스트랩 |
|---|---|---|---|---|---|---|---|
| ironclad.strength | 21판·10명 / 49판·24명 | RUPTURE | 전체 플레이어 | 41% | 1.87 | 13 | 82% |
| ironclad.strength |  | TWIN_STRIKE | 전체 플레이어 | 31% | 2.00 | 11 | 84% |
| ironclad.exhaust | 103판·12명 / 199판·41명 | DRUM_OF_BATTLE | 좋은 플레이어 | 28% | 2.42 | 11 | 99% |
| ironclad.block | 102판·14명 / 177판·43명 | (없음) | | | | | |
| ironclad.self_damage | 87판·12명 / 147판·37명 | HEMOKINESIS | 전체 플레이어 | 14% | 3.24 | 13 | 98% |
| ironclad.self_damage |  | NOT_YET | 좋은 플레이어 | 30% | 2.63 | 9 | 82% |
| ironclad.vulnerable | 155판·17명 / 246판·43명 | BULLY | 좋은 플레이어 | 27% | 2.98 | 13 | 100% |
| ironclad.attack_replay | 18판·11명 / 42판·28명 | AGGRESSION | 전체 플레이어 | 31% | 2.71 | 12 | 97% |
| ironclad.attack_replay |  | BLUDGEON | 전체 플레이어 | 31% | 2.13 | 12 | 87% |
| ironclad.attack_replay |  | UNRELENTING | 전체 플레이어 | 29% | 1.85 | 12 | 86% |
| ironclad.strike_tempo | 68판·14명 / 116판·41명 | HELLRAISER | 좋은 플레이어 | 32% | 3.25 | 10 | 93% |
| ironclad.hellraiser | 77판·14명 / 115판·41명 | PILLAGE | 좋은 플레이어 | 26% | 2.04 | 10 | 82% |
| silent.poison | 131판·17명 / 214판·45명 | BULLET_TIME | 좋은 플레이어 | 20% | 2.98 | 10 | 94% |
| silent.poison |  | BURST | 좋은 플레이어 | 21% | 2.09 | 11 | 90% |
| silent.poison |  | CORROSIVE_WAVE | 좋은 플레이어 | 18% | 2.32 | 9 | 81% |
| silent.poison |  | MASTER_PLANNER | 좋은 플레이어 | 18% | 3.18 | 10 | 92% |
| silent.poison |  | MIRAGE | 전체 플레이어 | 30% | 21.57 | 11 | 94% |
| silent.poison |  | OUTBREAK | 전체 플레이어 | 30% | 7.24 | 11 | 94% |
| silent.poison |  | POISONED_STAB | 좋은 플레이어 | 18% | 2.24 | 11 | 93% |
| silent.shiv | 341판·16명 / 667판·65명 | ASSASSINATE | 좋은 플레이어 | 18% | 3.80 | 13 | 99% |
| silent.shiv |  | FINISHER | 좋은 플레이어 | 19% | 4.94 | 11 | 100% |
| silent.shiv |  | PANACHE | 좋은 플레이어 | 9% | 2.48 | 11 | 85% |
| silent.shiv |  | SHADOW_STEP | 좋은 플레이어 | 11% | 2.03 | 9 | 89% |
| silent.shiv |  | STORM_OF_STEEL | 좋은 플레이어 | 15% | 7.15 | 14 | 100% |
| silent.shiv |  | UP_MY_SLEEVE | 좋은 플레이어 | 20% | 3.56 | 14 | 100% |
| silent.sly_discard | 198판·17명 / 318판·49명 | CORROSIVE_WAVE | 좋은 플레이어 | 16% | 2.83 | 11 | 98% |
| silent.sly_discard |  | FLICK_FLACK | 전체 플레이어 | 23% | 4.95 | 9 | 80% |
| silent.sly_discard |  | MASTER_PLANNER | 좋은 플레이어 | 14% | 2.49 | 11 | 95% |
| silent.sly_discard |  | SPEEDSTER | 좋은 플레이어 | 16% | 4.91 | 11 | 98% |
| defect.orb_focus | 62판·12명 / 118판·27명 | COOLANT | 좋은 플레이어 | 21% | 2.79 | 9 | 80% |
| defect.lightning | 61판·12명 / 128판·34명 | MASTER_OF_STRATEGY | 전체 플레이어 | 11% | 2.76 | 9 | 82% |
| defect.status | 39판·10명 / 128판·27명 | BEAM_CELL | 좋은 플레이어 | 54% | 2.03 | 9 | 82% |
| defect.status |  | SUPERCRITICAL | 전체 플레이어 | 20% | 1.80 | 13 | 84% |
| defect.zero_cost | 45판·10명 / 81판·23명 | (없음) | | | | | |
| defect.dark_orbs | 45판·12명 / 79판·26명 | (없음) | | | | | |
| defect.orb_variety | 59판·11명 / 101판·22명 | (없음) | | | | | |
| necrobinder.osty | 72판·12명 / 88판·24명 | (없음) | | | | | |
| necrobinder.doom | 138판·11명 / 155판·21명 | (없음) | | | | | |
| necrobinder.ethereal | 110판·10명 / 127판·19명 | SEANCE | 좋은 플레이어 | 30% | 3.91 | 9 | 87% |
| regent.stars | 102판·11명 / 139판·30명 | (없음) | | | | | |
| regent.star_spend | 60판·7명 / 77판·21명 | (없음) | | | | | |
| regent.forge | 49판·8명 / 82판·26명 | (없음) | | | | | |
| regent.creation | 60판·11명 / 97판·30명 | (없음) | | | | | |

`(없음)`은 기준을 넘는 카드가 없는 아키타입입니다. 리젠트와 네크로바인더는 역할 목록이 이미 멤버 덱의 특징 카드를 대부분 담고 있어 남는 카드가 거의 없습니다.

참고: 사일런트 중독의 OUTBREAK·POISONED_STAB·CORROSIVE_WAVE는 역할 카드 기준(멤버 덱 점유율 25% 이상, 또는 15% 이상이면서 제시될 때 50% 이상 선택)은 못 넘지만 중독 덱에 2~7배 자주 들어가므로 여기에 들어갑니다. CORROSIVE_WAVE는 교활·버리기 덱에서도 높아(중독 전용 카드가 아님) 두 아키타입 모두에 있습니다.
