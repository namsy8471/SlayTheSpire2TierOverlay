# 디펙트 콤보 검증 (combos/defect.json, 13개)

저장소 main의 `data/packs/v0.107.1/combos/defect.json`(13개)을 그대로 읽어 쟀습니다. 콤보 파일은 고치지 않았습니다.

- 지표: 승리 덱에서 두 카드의 동시 출현 lift = P(둘 다) ÷ (P(A)·P(B)), 각 P는 플레이어 균형값(1인 10% 상한). 베타 변경 카드(THUNDER, TRASH_TO_TREASURE 등)가 든 콤보는 v0.107.x 런만 셉니다.
- 기준(BRIEF_V2): lift ≥ 1.2 그리고 둘 다 가진 플레이어 ≥ 5명. 우수 플레이어 기준이며, 우수에서 5명 미만이면 전체 플레이어로 판정합니다. 부트스트랩 = 플레이어 복원 추출 200회 중 기준을 만족한 비율(80% 이상 yes, 20% 이하 no, 그 사이 unsure).
- 다시 돌리기: `python3 -I combos_check.py <저장소>/data/packs/v0.107.1/combos/defect.json --character defect`

## 결과

| 카드 | 우수 덱(둘 다/전체) | 우수 lift | 우수 플레이어 | 우수 부트스트랩 | 전체 덱 | 전체 lift | 전체 플레이어 | 전체 부트스트랩 | 판정 기준 | 지지 |
|---|---|---|---|---|---|---|---|---|---|---|
| DEFRAGMENT + GLACIER | 37/259 | 0.99 | 9 | 0.11 | 68/463 | 1.26 | 21 | 0.69 | good | no |
| HAILSTORM + GLACIER | 16/259 | 1.37 | 8 | 0.61 | 54/463 | 1.40 | 20 | 0.93 | good | unsure |
| LOOP + CONSUMING_SHADOW | 2/259 | 0.34 | 2 | 0.00 | 2/463 | 0.39 | 2 | 0.00 | all | no |
| THUNDER + TEMPEST | 4/124 | 2.16 | 4 | 0.34 | 20/275 | 1.84 | 10 | 0.95 | all | yes |
| THUNDER + VOLTAIC | 10/124 | 1.76 | 5 | 0.53 | 21/275 | 1.71 | 12 | 0.99 | good | unsure |
| TESLA_COIL + LIGHTNING_ROD | 13/259 | 3.03 | 6 | 0.76 | 24/463 | 2.42 | 12 | 1.00 | good | unsure |
| STORM + CREATIVE_AI | 18/259 | 1.46 | 8 | 0.69 | 44/463 | 1.44 | 20 | 0.92 | good | unsure |
| SUBROUTINE + CREATIVE_AI | 25/259 | 1.50 | 7 | 0.74 | 54/463 | 1.52 | 20 | 0.97 | good | unsure |
| CLAW + ALL_FOR_ONE | 26/259 | 2.70 | 6 | 0.78 | 44/463 | 2.36 | 15 | 1.00 | good | unsure |
| CLAW + FERAL | 34/259 | 2.64 | 6 | 0.78 | 55/463 | 2.13 | 17 | 1.00 | good | unsure |
| SMOKESTACK + OVERCLOCK | 24/259 | 1.61 | 9 | 0.98 | 68/463 | 1.28 | 20 | 0.76 | good | yes |
| TRASH_TO_TREASURE + BOOST_AWAY | 14/124 | 1.72 | 4 | 0.35 | 54/275 | 1.37 | 14 | 0.96 | all | yes |
| CAPACITOR + MULTI_CAST | 8/259 | 1.01 | 5 | 0.20 | 10/463 | 1.13 | 7 | 0.34 | good | unsure |

지지되지 않음(lift < 1.2 또는 5명 미만, 또는 부트스트랩 80% 미만): DEFRAGMENT + GLACIER, HAILSTORM + GLACIER, LOOP + CONSUMING_SHADOW, THUNDER + VOLTAIC, TESLA_COIL + LIGHTNING_ROD, STORM + CREATIVE_AI, SUBROUTINE + CREATIVE_AI, CLAW + ALL_FOR_ONE, CLAW + FERAL, CAPACITOR + MULTI_CAST

## 요약

- **지지 안 됨(점 추정부터 기준 미달)** — 3개:
  - `DEFRAGMENT + GLACIER`: 우수 lift 0.99(9명, 부트 11%). DEFRAGMENT는 어느 빌드에도 몰리지 않는 범용 카드라 v2 아키타입에서도 빠졌습니다.
  - `LOOP + CONSUMING_SHADOW`: 둘 다 가진 승리 덱 2판·2명, lift 0.34.
  - `CAPACITOR + MULTI_CAST`: 우수 lift 1.01(5명, 부트 20%).
- **지지(안정)** — 3개: `THUNDER + TEMPEST`(우수 4명이라 전체로 판정: lift 1.84·10명·부트 95%), `SMOKESTACK + OVERCLOCK`(우수 lift 1.61·9명·부트 98%), `TRASH_TO_TREASURE + BOOST_AWAY`(전체로 판정: lift 1.37·14명·부트 96%).
- **점 추정은 기준을 넘지만 우수 플레이어 부트스트랩이 80% 미만(불확실)** — 7개: `HAILSTORM + GLACIER`(61%), `THUNDER + VOLTAIC`(53%), `TESLA_COIL + LIGHTNING_ROD`(76%), `STORM + CREATIVE_AI`(69%), `SUBROUTINE + CREATIVE_AI`(74%), `CLAW + ALL_FOR_ONE`(78%), `CLAW + FERAL`(78%). 7개 모두 전체 플레이어 기준으로는 부트 92~100%로 안정적입니다. 우수 플레이어가 6~8명뿐이라 흔들리는 것이지, 반대 증거는 아닙니다.

## 참고: 디펙트 v2 아키타입 카드끼리의 동시 출현(빌드 안 카드 쌍 전체)

같은 방식으로 v2 아키타입의 같은 빌드 안 카드 쌍을 모두 쟀습니다(우수 기준, 5명 미만이면 전체). 새 콤보 후보를 고를 때 "지지 = yes"인 쌍을 참고하면 됩니다.

### defect.orb_focus

| 카드 | 우수 덱(둘 다/전체) | 우수 lift | 우수 플레이어 | 우수 부트스트랩 | 전체 덱 | 전체 lift | 전체 플레이어 | 전체 부트스트랩 | 판정 기준 | 지지 |
|---|---|---|---|---|---|---|---|---|---|---|
| CAPACITOR + COOLHEADED | 70/259 | 1.06 | 13 | 0.03 | 109/463 | 1.05 | 28 | 0.04 | good | no |
| CAPACITOR + GLACIER | 60/259 | 1.30 | 10 | 0.79 | 100/463 | 1.39 | 28 | 0.99 | good | unsure |
| CAPACITOR + BARRAGE | 41/259 | 1.33 | 11 | 0.88 | 59/463 | 1.39 | 20 | 0.97 | good | yes |
| CAPACITOR + HAILSTORM | 16/259 | 1.26 | 10 | 0.62 | 40/463 | 1.28 | 19 | 0.70 | good | unsure |
| COOLHEADED + GLACIER | 63/259 | 1.07 | 11 | 0.12 | 135/463 | 1.10 | 32 | 0.03 | good | no |
| COOLHEADED + BARRAGE | 49/259 | 1.20 | 11 | 0.46 | 82/463 | 1.14 | 25 | 0.28 | good | unsure |
| COOLHEADED + HAILSTORM | 21/259 | 1.31 | 9 | 0.79 | 60/463 | 1.13 | 21 | 0.17 | good | unsure |
| GLACIER + BARRAGE | 36/259 | 1.13 | 8 | 0.28 | 65/463 | 1.27 | 19 | 0.74 | good | unsure |
| GLACIER + HAILSTORM | 16/259 | 1.37 | 8 | 0.61 | 54/463 | 1.40 | 20 | 0.93 | good | unsure |
| BARRAGE + HAILSTORM | 11/259 | 1.67 | 7 | 0.82 | 25/463 | 1.16 | 13 | 0.40 | good | yes |

### defect.lightning

| 카드 | 우수 덱(둘 다/전체) | 우수 lift | 우수 플레이어 | 우수 부트스트랩 | 전체 덱 | 전체 lift | 전체 플레이어 | 전체 부트스트랩 | 판정 기준 | 지지 |
|---|---|---|---|---|---|---|---|---|---|---|
| STORM + THUNDER | 18/124 | 2.10 | 5 | 0.60 | 52/275 | 1.53 | 16 | 0.99 | good | unsure |
| STORM + BALL_LIGHTNING | 15/259 | 1.67 | 8 | 0.85 | 36/463 | 1.20 | 21 | 0.45 | good | yes |
| STORM + LIGHTNING_ROD | 16/259 | 1.40 | 8 | 0.69 | 51/463 | 1.39 | 26 | 0.88 | good | unsure |
| STORM + TEMPEST | 13/259 | 1.30 | 5 | 0.51 | 26/463 | 1.20 | 11 | 0.47 | good | unsure |
| STORM + TESLA_COIL | 11/259 | 2.08 | 6 | 0.76 | 19/463 | 1.33 | 12 | 0.71 | good | unsure |
| STORM + VOLTAIC | 22/259 | 2.10 | 7 | 0.91 | 33/463 | 1.82 | 13 | 0.99 | good | yes |
| STORM + BUFFER | 16/259 | 1.39 | 9 | 0.91 | 39/463 | 1.31 | 23 | 0.86 | good | yes |
| STORM + MODDED | 23/259 | 1.40 | 11 | 0.81 | 37/463 | 1.09 | 22 | 0.25 | good | yes |
| STORM + SUPERCRITICAL | 8/259 | 1.39 | 5 | 0.38 | 17/463 | 0.86 | 10 | 0.06 | good | unsure |
| THUNDER + BALL_LIGHTNING | 9/124 | 2.55 | 4 | 0.35 | 28/275 | 1.45 | 10 | 0.89 | all | yes |
| THUNDER + LIGHTNING_ROD | 6/124 | 2.79 | 3 | 0.18 | 29/275 | 1.49 | 12 | 0.96 | all | yes |
| THUNDER + TEMPEST | 4/124 | 2.16 | 4 | 0.34 | 20/275 | 1.84 | 10 | 0.95 | all | yes |
| THUNDER + TESLA_COIL | 8/124 | 3.03 | 4 | 0.33 | 17/275 | 1.99 | 8 | 0.93 | all | yes |
| THUNDER + VOLTAIC | 10/124 | 1.76 | 5 | 0.53 | 21/275 | 1.71 | 12 | 0.99 | good | unsure |
| THUNDER + BUFFER | 4/124 | 1.18 | 3 | 0.14 | 21/275 | 1.16 | 9 | 0.43 | all | unsure |
| THUNDER + MODDED | 5/124 | 1.00 | 3 | 0.07 | 16/275 | 0.95 | 11 | 0.17 | all | no |
| THUNDER + SUPERCRITICAL | 4/124 | 3.02 | 3 | 0.14 | 11/275 | 1.27 | 8 | 0.60 | all | unsure |
| BALL_LIGHTNING + LIGHTNING_ROD | 9/259 | 1.10 | 5 | 0.28 | 26/463 | 1.20 | 17 | 0.50 | good | unsure |
| BALL_LIGHTNING + TEMPEST | 10/259 | 1.41 | 5 | 0.42 | 22/463 | 1.75 | 11 | 0.91 | good | unsure |
| BALL_LIGHTNING + TESLA_COIL | 7/259 | 2.70 | 3 | 0.15 | 18/463 | 2.24 | 12 | 0.99 | all | yes |
| BALL_LIGHTNING + VOLTAIC | 9/259 | 1.73 | 8 | 0.87 | 18/463 | 1.69 | 13 | 0.95 | good | yes |
| BALL_LIGHTNING + BUFFER | 7/259 | 0.78 | 5 | 0.04 | 23/463 | 1.24 | 13 | 0.57 | good | no |
| BALL_LIGHTNING + MODDED | 12/259 | 1.19 | 6 | 0.38 | 21/463 | 1.04 | 13 | 0.17 | good | unsure |
| BALL_LIGHTNING + SUPERCRITICAL | 3/259 | 0.46 | 3 | 0.00 | 7/463 | 0.57 | 7 | 0.00 | all | no |
| LIGHTNING_ROD + TEMPEST | 11/259 | 1.52 | 3 | 0.14 | 23/463 | 1.57 | 12 | 0.92 | all | yes |
| LIGHTNING_ROD + TESLA_COIL | 13/259 | 3.03 | 6 | 0.76 | 24/463 | 2.42 | 12 | 1.00 | good | unsure |
| LIGHTNING_ROD + VOLTAIC | 13/259 | 1.99 | 6 | 0.74 | 24/463 | 1.89 | 13 | 0.96 | good | unsure |
| LIGHTNING_ROD + BUFFER | 14/259 | 1.89 | 7 | 0.86 | 31/463 | 1.42 | 19 | 0.91 | good | yes |
| LIGHTNING_ROD + MODDED | 11/259 | 0.77 | 5 | 0.05 | 26/463 | 1.07 | 18 | 0.21 | good | no |
| LIGHTNING_ROD + SUPERCRITICAL | 6/259 | 1.52 | 4 | 0.32 | 15/463 | 1.07 | 11 | 0.32 | all | unsure |
| TEMPEST + TESLA_COIL | 10/259 | 3.44 | 5 | 0.56 | 15/463 | 2.56 | 10 | 0.98 | good | unsure |
| TEMPEST + VOLTAIC | 11/259 | 2.57 | 5 | 0.58 | 18/463 | 2.42 | 10 | 0.96 | good | unsure |
| TEMPEST + BUFFER | 8/259 | 1.47 | 4 | 0.35 | 15/463 | 1.24 | 8 | 0.57 | all | unsure |
| TEMPEST + MODDED | 11/259 | 1.25 | 6 | 0.48 | 16/463 | 1.14 | 11 | 0.40 | good | unsure |
| TEMPEST + SUPERCRITICAL | 7/259 | 2.82 | 3 | 0.22 | 11/463 | 1.35 | 6 | 0.58 | all | unsure |
| TESLA_COIL + VOLTAIC | 9/259 | 3.10 | 5 | 0.55 | 14/463 | 2.75 | 9 | 0.97 | good | unsure |
| TESLA_COIL + BUFFER | 5/259 | 1.14 | 4 | 0.23 | 9/463 | 1.05 | 6 | 0.28 | all | unsure |
| TESLA_COIL + MODDED | 9/259 | 1.06 | 4 | 0.15 | 12/463 | 1.12 | 7 | 0.36 | all | unsure |
| TESLA_COIL + SUPERCRITICAL | 3/259 | 1.48 | 2 | 0.02 | 9/463 | 1.75 | 7 | 0.80 | all | unsure |
| VOLTAIC + BUFFER | 9/259 | 1.89 | 7 | 0.87 | 16/463 | 1.55 | 12 | 0.92 | good | yes |
| VOLTAIC + MODDED | 6/259 | 0.84 | 5 | 0.17 | 8/463 | 0.68 | 7 | 0.03 | good | no |
| VOLTAIC + SUPERCRITICAL | 4/259 | 2.01 | 3 | 0.23 | 7/463 | 1.04 | 5 | 0.30 | all | unsure |
| BUFFER + MODDED | 13/259 | 0.87 | 6 | 0.17 | 20/463 | 0.94 | 13 | 0.07 | good | no |
| BUFFER + SUPERCRITICAL | 7/259 | 1.55 | 4 | 0.41 | 12/463 | 0.95 | 8 | 0.14 | all | no |
| MODDED + SUPERCRITICAL | 9/259 | 1.34 | 5 | 0.43 | 16/463 | 1.13 | 11 | 0.41 | good | unsure |

### defect.status

| 카드 | 우수 덱(둘 다/전체) | 우수 lift | 우수 플레이어 | 우수 부트스트랩 | 전체 덱 | 전체 lift | 전체 플레이어 | 전체 부트스트랩 | 판정 기준 | 지지 |
|---|---|---|---|---|---|---|---|---|---|---|
| COMPACT + ROCKET_PUNCH | 14/124 | 1.51 | 4 | 0.24 | 51/275 | 1.13 | 17 | 0.32 | all | unsure |
| COMPACT + BOOST_AWAY | 59/259 | 1.35 | 11 | 0.86 | 120/463 | 1.17 | 31 | 0.35 | good | yes |
| COMPACT + GUNK_UP | 24/259 | 1.25 | 8 | 0.60 | 57/463 | 1.06 | 20 | 0.12 | good | unsure |
| COMPACT + OVERCLOCK | 46/259 | 1.07 | 9 | 0.17 | 90/463 | 1.11 | 27 | 0.08 | good | no |
| COMPACT + TURBO | 80/259 | 1.04 | 11 | 0.07 | 143/463 | 1.06 | 29 | 0.01 | good | no |
| COMPACT + SMOKESTACK | 22/259 | 1.31 | 8 | 0.70 | 59/463 | 1.19 | 20 | 0.48 | good | unsure |
| COMPACT + TRASH_TO_TREASURE | 7/124 | 1.25 | 4 | 0.27 | 29/275 | 1.09 | 10 | 0.26 | all | unsure |
| COMPACT + FTL | 26/259 | 1.26 | 9 | 0.63 | 50/463 | 0.98 | 18 | 0.04 | good | unsure |
| ROCKET_PUNCH + BOOST_AWAY | 30/124 | 2.06 | 3 | 0.10 | 93/275 | 1.39 | 18 | 0.98 | all | yes |
| ROCKET_PUNCH + GUNK_UP | 9/124 | 2.21 | 4 | 0.38 | 54/275 | 1.60 | 16 | 0.98 | all | yes |
| ROCKET_PUNCH + OVERCLOCK | 18/124 | 1.72 | 4 | 0.40 | 59/275 | 1.32 | 17 | 0.90 | all | yes |
| ROCKET_PUNCH + TURBO | 31/124 | 0.84 | 3 | 0.06 | 89/275 | 1.15 | 17 | 0.28 | all | unsure |
| ROCKET_PUNCH + SMOKESTACK | 10/124 | 2.91 | 3 | 0.10 | 50/275 | 1.61 | 15 | 1.00 | all | yes |
| ROCKET_PUNCH + TRASH_TO_TREASURE | 10/124 | 2.33 | 3 | 0.10 | 38/275 | 1.65 | 10 | 0.98 | all | yes |
| ROCKET_PUNCH + FTL | 7/124 | 1.89 | 4 | 0.33 | 32/275 | 0.92 | 11 | 0.04 | all | no |
| BOOST_AWAY + GUNK_UP | 30/259 | 1.48 | 9 | 0.85 | 104/463 | 1.38 | 25 | 0.97 | good | yes |
| BOOST_AWAY + OVERCLOCK | 44/259 | 1.05 | 10 | 0.15 | 123/463 | 1.10 | 32 | 0.09 | good | no |
| BOOST_AWAY + TURBO | 96/259 | 1.10 | 11 | 0.15 | 199/463 | 1.04 | 34 | 0.00 | good | no |
| BOOST_AWAY + SMOKESTACK | 28/259 | 1.71 | 11 | 0.98 | 96/463 | 1.40 | 26 | 0.99 | good | yes |
| BOOST_AWAY + TRASH_TO_TREASURE | 14/124 | 1.72 | 4 | 0.35 | 54/275 | 1.37 | 14 | 0.96 | all | yes |
| BOOST_AWAY + FTL | 26/259 | 1.08 | 9 | 0.33 | 80/463 | 1.11 | 26 | 0.07 | good | unsure |
| GUNK_UP + OVERCLOCK | 32/259 | 1.63 | 9 | 0.89 | 81/463 | 1.36 | 22 | 0.96 | good | yes |
| GUNK_UP + TURBO | 40/259 | 1.09 | 10 | 0.16 | 103/463 | 1.05 | 26 | 0.01 | good | no |
| GUNK_UP + SMOKESTACK | 14/259 | 1.79 | 7 | 0.79 | 64/463 | 1.81 | 19 | 1.00 | good | unsure |
| GUNK_UP + TRASH_TO_TREASURE | 7/124 | 3.18 | 4 | 0.36 | 36/275 | 1.72 | 12 | 0.99 | all | yes |
| GUNK_UP + FTL | 14/259 | 1.17 | 7 | 0.39 | 48/463 | 1.21 | 17 | 0.52 | good | unsure |
| OVERCLOCK + TURBO | 73/259 | 1.09 | 11 | 0.14 | 150/463 | 1.05 | 31 | 0.01 | good | no |
| OVERCLOCK + SMOKESTACK | 24/259 | 1.61 | 9 | 0.98 | 68/463 | 1.28 | 20 | 0.76 | good | yes |
| OVERCLOCK + TRASH_TO_TREASURE | 7/124 | 1.81 | 4 | 0.33 | 32/275 | 1.09 | 11 | 0.27 | all | unsure |
| OVERCLOCK + FTL | 16/259 | 0.65 | 6 | 0.00 | 54/463 | 0.96 | 20 | 0.01 | good | no |
| TURBO + SMOKESTACK | 30/259 | 1.11 | 10 | 0.24 | 96/463 | 1.08 | 23 | 0.01 | good | unsure |
| TURBO + TRASH_TO_TREASURE | 11/124 | 0.77 | 3 | 0.04 | 50/275 | 1.12 | 13 | 0.28 | all | unsure |
| TURBO + FTL | 34/259 | 0.97 | 8 | 0.03 | 85/463 | 0.90 | 22 | 0.00 | good | no |
| SMOKESTACK + TRASH_TO_TREASURE | 7/124 | 1.17 | 2 | 0.02 | 34/275 | 1.69 | 10 | 0.98 | all | yes |
| SMOKESTACK + FTL | 6/259 | 0.79 | 4 | 0.04 | 37/463 | 1.05 | 11 | 0.13 | all | no |
| TRASH_TO_TREASURE + FTL | 4/124 | 2.61 | 4 | 0.36 | 20/275 | 1.15 | 11 | 0.37 | all | unsure |

### defect.zero_cost

| 카드 | 우수 덱(둘 다/전체) | 우수 lift | 우수 플레이어 | 우수 부트스트랩 | 전체 덱 | 전체 lift | 전체 플레이어 | 전체 부트스트랩 | 판정 기준 | 지지 |
|---|---|---|---|---|---|---|---|---|---|---|
| FERAL + BEAM_CELL | 28/259 | 1.64 | 8 | 0.88 | 52/463 | 1.58 | 19 | 0.99 | good | yes |
| FERAL + CLAW | 34/259 | 2.64 | 6 | 0.78 | 55/463 | 2.13 | 17 | 1.00 | good | unsure |
| FERAL + ALL_FOR_ONE | 15/259 | 2.21 | 7 | 0.88 | 36/463 | 1.87 | 14 | 1.00 | good | yes |
| FERAL + SCRAPE | 4/124 | 3.48 | 3 | 0.15 | 16/275 | 2.07 | 11 | 0.99 | all | yes |
| FERAL + BULK_UP | 16/259 | 1.36 | 6 | 0.53 | 40/463 | 1.34 | 16 | 0.78 | good | unsure |
| FERAL + REBOOT | 14/259 | 1.59 | 7 | 0.79 | 37/463 | 1.79 | 16 | 1.00 | good | unsure |
| BEAM_CELL + CLAW | 35/259 | 1.74 | 8 | 0.96 | 54/463 | 1.64 | 17 | 1.00 | good | yes |
| BEAM_CELL + ALL_FOR_ONE | 24/259 | 2.09 | 10 | 1.00 | 43/463 | 1.81 | 17 | 1.00 | good | yes |
| BEAM_CELL + SCRAPE | 7/124 | 1.66 | 3 | 0.14 | 15/275 | 1.34 | 8 | 0.72 | all | unsure |
| BEAM_CELL + BULK_UP | 20/259 | 0.88 | 5 | 0.10 | 42/463 | 1.08 | 14 | 0.23 | good | no |
| BEAM_CELL + REBOOT | 15/259 | 1.03 | 6 | 0.17 | 31/463 | 1.21 | 14 | 0.55 | good | no |
| CLAW + ALL_FOR_ONE | 26/259 | 2.70 | 6 | 0.78 | 44/463 | 2.36 | 15 | 1.00 | good | unsure |
| CLAW + SCRAPE | 10/124 | 1.93 | 3 | 0.22 | 18/275 | 1.95 | 9 | 0.94 | all | yes |
| CLAW + BULK_UP | 13/259 | 0.65 | 4 | 0.03 | 33/463 | 1.13 | 14 | 0.40 | all | unsure |
| CLAW + REBOOT | 21/259 | 1.64 | 6 | 0.73 | 33/463 | 1.57 | 12 | 0.96 | good | unsure |
| ALL_FOR_ONE + SCRAPE | 6/124 | 2.23 | 3 | 0.15 | 13/275 | 1.65 | 6 | 0.68 | all | unsure |
| ALL_FOR_ONE + BULK_UP | 10/259 | 1.00 | 4 | 0.18 | 28/463 | 1.21 | 11 | 0.52 | all | unsure |
| ALL_FOR_ONE + REBOOT | 6/259 | 0.59 | 3 | 0.00 | 19/463 | 1.18 | 9 | 0.42 | all | unsure |
| SCRAPE + BULK_UP | 5/124 | 3.14 | 4 | 0.36 | 13/275 | 1.79 | 9 | 0.92 | all | yes |
| SCRAPE + REBOOT | 2/124 | 0.11 | 1 | 0.00 | 8/275 | 1.52 | 5 | 0.55 | all | unsure |
| BULK_UP + REBOOT | 10/259 | 0.92 | 6 | 0.14 | 24/463 | 0.99 | 13 | 0.10 | good | no |

### defect.dark_orbs

| 카드 | 우수 덱(둘 다/전체) | 우수 lift | 우수 플레이어 | 우수 부트스트랩 | 전체 덱 | 전체 lift | 전체 플레이어 | 전체 부트스트랩 | 판정 기준 | 지지 |
|---|---|---|---|---|---|---|---|---|---|---|
| DARKNESS + SHADOW_SHIELD | 15/259 | 2.71 | 7 | 0.87 | 25/463 | 2.35 | 12 | 0.99 | good | yes |
| DARKNESS + CAPACITOR | 15/259 | 1.41 | 8 | 0.77 | 20/463 | 1.02 | 12 | 0.18 | good | unsure |
| DARKNESS + COMPACT | 20/259 | 1.65 | 6 | 0.73 | 33/463 | 1.44 | 13 | 0.92 | good | unsure |
| SHADOW_SHIELD + CAPACITOR | 21/259 | 1.04 | 9 | 0.20 | 27/463 | 0.88 | 15 | 0.01 | good | unsure |
| SHADOW_SHIELD + COMPACT | 19/259 | 0.92 | 8 | 0.21 | 32/463 | 0.87 | 14 | 0.01 | good | unsure |
| CAPACITOR + COMPACT | 43/259 | 0.97 | 11 | 0.01 | 62/463 | 0.93 | 19 | 0.00 | good | no |

### defect.orb_variety

| 카드 | 우수 덱(둘 다/전체) | 우수 lift | 우수 플레이어 | 우수 부트스트랩 | 전체 덱 | 전체 lift | 전체 플레이어 | 전체 부트스트랩 | 판정 기준 | 지지 |
|---|---|---|---|---|---|---|---|---|---|---|
| COOLHEADED + SYNCHRONIZE | 17/124 | 1.76 | 4 | 0.39 | 29/275 | 1.52 | 9 | 0.98 | all | yes |
| COOLHEADED + COMPACT | 72/259 | 1.10 | 12 | 0.15 | 126/463 | 1.07 | 28 | 0.01 | good | no |
| SYNCHRONIZE + COMPACT | 11/124 | 1.83 | 3 | 0.17 | 21/275 | 1.60 | 7 | 0.86 | all | yes |
