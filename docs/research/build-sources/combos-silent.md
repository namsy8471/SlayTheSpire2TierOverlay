# 사일런트 콤보 동시 등장 검사 (v2)

대상: `silent.json`의 콤보 12개.

방법: 좋은 플레이어 승리 덱(482판·19명), 두 카드가 모두 판정 대상인 덱만(베타 변경 카드†는 v0.107.x). lift = 둘 다 든 덱 비율 ÷ (A 비율 × B 비율), 비율은 모두 플레이어 균형(한 사람 10% 상한). **지지 안 됨** = lift < 1.2 또는 둘 다 든 플레이어 < 5명. 부트스트랩 = 플레이어 200회 재표본에서 "지지" 판정이 유지된 비율. 전체 플레이어 값은 참고.

| A | B | 출처 | 둘 다(판) | 플레이어 | lift(균형) | 지지 부트스트랩 | 판정 | 전체 플레이어 lift·명 |
|---|---|---|---|---|---|---|---|---|
| ACCURACY | INFINITE_BLADES | 단도 강화 + 매 턴 단도 | 116/482 | 14 | 1.29 | 86% | 지지 | 1.26·44명 |
| ACCURACY | BLADE_DANCE | 단도 강화 + 단도 묶음 | 182/482 | 16 | 1.31 | 96% | 지지 | 1.22·56명 |
| FAN_OF_KNIVES | INFINITE_BLADES | 광역 단도 | 39/482 | 11 | 1.30 | 72% | 불확실 | 1.28·32명 |
| PHANTOM_BLADES | INFINITE_BLADES | 보존 단도 | 80/482 | 12 | 1.48 | 98% | 지지 | 1.41·39명 |
| KNIFE_TRAP | STORM_OF_STEEL | 단도 재사용 | 15/482 | 11 | 1.67 | 88% | 지지 | 1.14·21명 |
| ENVENOM | BLADE_DANCE | 다단 중독 | 38/482 | 10 | 0.96 | 2% | 지지 안 됨 | 1.07·29명 |
| NOXIOUS_FUMES | ACCELERANT | 광역 독 가속 | 11/202 | 5 | 1.19 | 36% | 불확실 | 1.98·12명 |
| BOUNCING_FLASK | ACCELERANT | 독 가속 | 12/202 | 6 | 3.14 | 74% | 불확실 | 3.84·10명 |
| NOXIOUS_FUMES | OUTBREAK | 독 부여 → 광역 피해 | 6/202 | 4 | 2.12 | 34% | 불확실 | 2.49·11명 |
| TOOLS_OF_THE_TRADE | TACTICIAN | 매 턴 버리기 + 교활 에너지 | 33/482 | 8 | 1.24 | 56% | 불확실 | 1.37·19명 |
| TOOLS_OF_THE_TRADE | REFLEX | 매 턴 버리기 + 교활 드로우 | 29/482 | 9 | 1.39 | 80% | 지지 | 1.56·21명 |
| SPEEDSTER | ACROBATICS | 드로우 → 광역 피해 | 16/482 | 9 | 1.48 | 80% | 지지 | 1.90·21명 |

지지 안 됨·불확실: FAN_OF_KNIVES+INFINITE_BLADES(불확실, lift 1.30, 11명), ENVENOM+BLADE_DANCE(지지 안 됨, lift 0.96, 10명), NOXIOUS_FUMES+ACCELERANT(불확실, lift 1.19, 5명), BOUNCING_FLASK+ACCELERANT(불확실, lift 3.14, 6명), NOXIOUS_FUMES+OUTBREAK(불확실, lift 2.12, 4명), TOOLS_OF_THE_TRADE+TACTICIAN(불확실, lift 1.24, 8명).

해석: lift가 1 근처이거나 1보다 낮은 쌍은 "같은 빌드 안의 대체 카드"(둘 중 하나만 드는 경향)일 수 있습니다. 콤보 가중치 조정 판단의 참고로만 쓰고, 이 작업에서는 콤보 파일을 고치지 않았습니다.
