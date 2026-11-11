# 보상 스트림 재현 측정 (RewardCorpusTests, 2026-11-12)

`RewardCorpusTests.Card_rewards_replayed_from_history`는 다른 플레이어의 런 기록(`STS2_RUN_CORPUS`)으로 Rewards 스트림을 시드부터 다시 돌려 보고,
전투 카드 보상이 기록과 같은지 센다. 규칙을 하나씩 더한 다섯 묶음을 모두 측정하므로 각 규칙의 효과가 따로 보인다.
`STS2_REWARD_DETAIL=1`이면 마지막 묶음의 불일치를 카드·유물 id만으로 출력한다(런 id, 시드, 파일 이름은 출력하지 않는다).

## 결과 (일치 / 시도, 업그레이드까지 맞은 수)

| 규칙 묶음 | v0.111.0 (1574런) | v0.107.1 (1447런) |
|---|---|---|
| 이전 | 3891 / 3901, 업 3814 | 2882 / 2894, 업 2838 |
| + 정지 방 3개 계산 | 5260 / 5274, 업 5128 | 3884 / 3900, 업 3793 |
| + Darv의 DUSTY_TOME 제안 | 5306 / 5312, 업 5167 | 3930 / 3939, 업 3839 |
| + 전투 중 포션 | 5369 / 5375, 업 5224 | 3978 / 3987, 업 3883 |
| + 훅 업그레이드 (이후) | **5369 / 5375, 업 5369** | **3978 / 3987, 업 3969** |

포션 판정 불일치: v0.111.0 10 → 0, v0.107.1 9 → 2.

## 원인과 규칙 (디컴파일 v0.111.0)

| 원인 | 규칙 | 효과 |
|---|---|---|
| BRAIN_LEECH가 재현을 멈춤 | RIP: 무색 카드 3장 보상, 장마다 희귀도+선택 = 6값. SHARE_KNOWLEDGE: 캐릭터 카드 5장 격자 = 10값 (`ForNonCombatWithDefaultOdds`, 업그레이드 굴림 없음, 기본 확률) | 격자 5장 검증 v0.111.0 56/56 |
| THIS_OR_THAT가 멈춤 | ORNATE: 유물 희귀도 1값, PLAIN: 0값 | |
| ARCANE_SCROLL이 멈춤 | 고대 선택 시 희귀 카드 1장 `NextItem` 1값 | |
| Darv 직후 첫 보상 불일치 | Darv가 DUSTY_TOME을 **제안만 해도** `DustyTome.SetupForPlayer`가 Rewards에서 1값을 뽑는다. 고르면 더 뽑지 않음 (DUSTY_TOME은 정지 유물에서 뺌) | 불일치 v0.111.0 14→6, v0.107.1 16→9 |
| 포션 판정 불일치 (기록엔 포션, 예측엔 없음) | `PotionCmd.TryToProcure`는 전투 중 얻은 포션(PETRIFIED_TOAD, DELICATE_FROND, 카드 효과)도 `potion_choices`에 남긴다. 예측 없음 + 기록 있음은 불일치가 아니다 | v0.111.0 10건 전부 |
| 업그레이드 표시 틀림 | MOLTEN_EGG·TOXIC_EGG·FROZEN_EGG(공격/스킬/파워), LAVA_LAMP(막지 못한 피해 0이면 전부, 피해가 있으면 두 경우 모두 허용), 같은 보상 화면에서 얻은 유물(`CardReward.OnRelicObtained`가 훅을 다시 적용) | v0.111.0 잔여 0 |

## 남은 불확실

- v0.111.0 카드 불일치 6건: 1막 4건(첫 보상 3건: 앞에 고대 선택 기록 없음 1, NEOWS_TORMENT 1, ANCIENT_AFFECTION_BOND+ARCANE_SCROLL 1; 3번째 보상 1건), 2막 2건(PANDORAS_BOX, PAELS_HORN 직후). 원인 미상.
- v0.107.1 카드 불일치 9건(그중 1건은 모드 유물이 있는 런), 포션 2건(SOZU 1, 모드 1), 업그레이드 9건: 1막 첫 1~3번째 보상의 세 장이 모두 업그레이드된 런(니오우 선택 PRECISE_SCISSORS / PRECARIOUS_SHEARS / ARCANE_SCROLL). v0.107.1 디컴파일이 없어 원인 미확인.
- v0.107.1 Brain Leech 격자 검증 0/41: 기록 형식이 다른 것으로 보인다. 그 뒤 보상은 일치하므로 뽑는 수(10)는 맞다.
- 실제 오버레이 영향: 오버레이는 로그의 희귀도 굴림을 스트림에서 찾아가므로 Darv의 DUSTY_TOME 같은 빈칸은 스스로 메운다. 다만 LAVA_LAMP 업그레이드는 반영하지 않는다(`GameStateTracker.RewardUpgrade`; 전투 피해 정보가 필요해 작은 수정이 아니다).
