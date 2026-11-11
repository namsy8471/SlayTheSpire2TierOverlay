# v0.111.0 화면 배치 점검 (실제 화면 없이 코드로)

사용자 확인: 베타(v0.111.0)의 보상·상점 화면은 v0.107.1과 똑같아 보입니다. 그래서 이 점검은 **코드 확인**입니다.
인식기(Vision)와 화면 표시(배지·지도 표시)가 기대는 배치 상수를 모두 적고, 각 값의 출처(저장소 주석이 가리키는 게임 클래스·씬)를 v0.111.0에서 다시 읽었습니다.

- 게임 코드: v0.111.0 디컴파일(작업용 사본, 저장소 밖).
- 씬: 설치된 게임의 `SlayTheSpire2.pck`에서 `tools/PckTool extract`로 임시 폴더에 꺼낸 `.tscn`(텍스트 씬). 저장소에는 넣지 않았습니다.
- 비교 기준: 저장소 상수와 주석(모두 v0.107.1에서 읽은 값). v0.107.1 씬 사본은 이 PC에 없으므로 "같음"은 "저장소 값 = v0.111.0 값"이라는 뜻입니다.

## 결과

| 합계 | 같음 | 바뀜 | 확인 못 함 |
|---|---|---|---|
| 46 | 42 | 0 | 4 |

**바뀐 값이 없어서 Vision·지도 배치 코드는 고치지 않았습니다.**
확인 못 한 4개는 모두 실제 화면에서 잰 값(텍스처 안쪽 여백, 색, 글꼴 모양)이라 씬·코드로는 확인할 수 없습니다. 실제 v0.111 화면이 생기면 `VisionBench`로 다시 재야 합니다.

## 1. 카드 한 장 (`CardGeometry`, `CardRecognizer`)

| # | 상수 (저장소) | 출처 | v0.111.0 값 | 판정 |
|---|---|---|---|---|
| 1 | `CardGeometry.Card` (−150, −211, 300×422) | `card.tscn` Frame, `NCard.defaultSize` | Frame −150..150 / −211..211, `defaultSize` (300, 422) | 같음 |
| 2 | `CardGeometry.Art` (−125, −168, 250×190) | `card.tscn` Portrait | −125..125 / −168..22 | 같음 |
| 3 | `CardGeometry.Title` (−105, −204, 210×54) | `card.tscn` TitleLabel | −105..105 / −204..−150 | 같음 |
| 4 | 강화 카드 제목 = 초록 #7FFF00, 아니면 크림 #FFF6E2 | `NCard.UpdateTitleLabel`, `StsColors` | `CurrentUpgradeLevel == 0` → cream, 아니면 green. `green` 7FFF00, `cream` FFF6E2 | 같음 |

## 2. 카드 보상·카드 고르기 (`ScreenLayouts.Reward` / `Choice`, `BadgeLayout`)

| # | 상수 | 출처 | v0.111.0 값 | 판정 |
|---|---|---|---|---|
| 5 | `RewardRowX/Y` 960, 616 | `card_reward_selection_screen.tscn` CardRow: 화면 중앙 + `offset_top` 76 | 76 | 같음 |
| 6 | `RewardSpacing` 350, 여는 트윈 0.5초 | `NCardRewardSelectionScreen` (`_cardXOffset`, `RefreshOptions`) | 350, 0.5초 | 같음 |
| 7 | `RewardSmallScale` 0.8 / `RewardHoverScale` 1.0 | `NCardHolder.smallScale` / `HoverScale` (`NGridCardHolder`는 재정의 없음) | 0.8 / 1.0 | 같음 |
| 8 | `ChoiceRowY` 600 | `choose_a_card_selection_screen.tscn` CardRow `offset_top` 60 | 60 | 같음 |
| 9 | `ChoiceSpacing` 340 | `NChooseACardSelectionScreen._cardXSpacing` | 340 | 같음 |
| 10 | 보상 아래 버튼 줄 y 884..957 (`BadgeLayout`) | `card_reward_selection_screen.tscn` RewardAlternatives 344..417 | 344..417 | 같음 |

## 3. 카드 격자: 제거·변환·강화(대장간) 화면 (`ScreenLayouts.Grid`, `CardGridProbe`)

| # | 상수 | 출처 | v0.111.0 값 | 판정 |
|---|---|---|---|---|
| 11 | 격자 위쪽 80 | `simple_card_select_screen.tscn`·`deck_upgrade_select_screen.tscn` CardGrid `offset_top` 80, `NCardGrid.InsetForTopBar` | 80 | 같음 |
| 12 | 스크롤 영역 x 150..1720 | `card_grid.tscn` ScrollContainer `offset_left` 150 / `offset_right` −200 | 150 / −200 | 같음 |
| 13 | 칸 = 300×422 × 0.8, 간격 40, 열 수 공식 → 5열 | `NCardGrid` (`CardPadding`, `Columns`, `_cardSize`) | 40, 같은 공식 | 같음 |
| 14 | `GridX0` 375, 열 간격 280, 행 간격 377.6, 한 줄 y 460, 첫 줄 y 328.8 | `NCardGrid.ReflowColumns`(YOffset + 80 + 칸/2), 컨테이너 높이 = 내용 + 80 + 320, 화면보다 작을 때만 가운데 | 같은 식 | 같음 |
| 15 | "강화 상태 보기" 체크 상자 위치 (대장간 감지) | `deck_upgrade_select_screen.tscn` ViewUpgrades `offset` 16 / −76, scale 0.75 | 16 / −76, 0.75 | 같음 |

## 4. 상점 (`ScreenLayouts.Shop`, `RelicRecognizer`, `ShopPriceReader`, `CardRemovalProbe`)

| # | 상수 | 출처 | v0.111.0 값 | 판정 |
|---|---|---|---|---|
| 16 | 깔개 x 118 (= 960 − 842), y 80 | `merchant_inventory.tscn` SlotsContainer `offset_left` −842, `NMerchantInventory._openPosition` | −842, 80 | 같음 |
| 17 | 캐릭터 카드 (332, 303) + x −13/250/511/770/1036(y −3), 무색 카드 (397, 676) + x −13/261 | `merchant_inventory.tscn` CharacterCards·ColorlessCards | 같은 값 | 같음 |
| 18 | 상점 칸 크기 0.65, 마우스 올림 0.8 | `NMerchantSlot._smallScale` / `_hoverScale`, `merchant_card.tscn` scale | 0.65 / 0.8 | 같음 |
| 19 | 카드 가격 칸 x −148..149, y 220..278, 글자 39 | `merchant_card.tscn` Cost / CostLabel | 같은 값 | 같음 |
| 20 | 가격 색: 돈 부족 빨강 #FF5555, 할인 초록 #7FFF00, 그 밖 크림 (유물은 크림/빨강) | `NMerchantCard.UpdateVisual`, `NMerchantRelic.UpdateVisual` | 같은 규칙 | 같음 |
| 21 | 유물 칸 원점 (989 + 150 i, 675) | `merchant_inventory.tscn` Relics (871, 595), 칸 x 0/150/300 | 같은 값 | 같음 |
| 22 | 유물 아이콘 상자 (−56, −72, 120×120) | `merchant_relic.tscn` RelicHolder (−64, −80) × 2, `relic.tscn` Icon 안쪽 4, 60×60; `_iconSize`는 씬에서 정하지 않음(기본값) | 같은 값 | 같음 |
| 23 | 유물 가격 칸 x −148..148, y 52..106 | `merchant_relic.tscn` Cost | 같은 값 | 같음 |
| 24 | 카드 제거 동전 노드 (1488, 758), 0.65, 가격 칸 y 176.9..230.9 | `merchant_inventory.tscn` MerchantCardRemoval (1370, 678), `merchant_card_removal.tscn` Cost | 같은 값 | 같음 |
| 25 | 상점 가격 규칙 (`ShopPricing`, `ShopPriceRules`) | `MerchantCardEntry`·`MerchantRelicEntry`·`MembershipCard`·`TheCourier` | 이전 세션에서 확인, 변화 없음 | 같음 |

## 5. 이벤트 선택지 (`EventButtonDetector`, `TextInk`, `AncientButtonDetector`)

| # | 상수 | 출처 | v0.111.0 값 | 판정 |
|---|---|---|---|---|
| 26 | 선택지 열 x 922, 첫 버튼 y 596, 간격 104 | `default_event_layout.tscn`: 전체 −39, VBox `offset_left` −38 / `offset_top` 255, 제목 64 + 설명 최소 280(`fit_content`) + 협동 표시 48(숨김) + 여백 24, VBox 기본 간격 4 | 같은 값 | 같음 |
| 27 | 버튼 800×100, 중심 (400, 50), 마우스 올림 1.01 | `event_option_button.tscn`, `NEventOptionButton._hoverScale` | 같은 값 | 같음 |
| 28 | 버튼 글자 칸 x 42..764, y 13..87 | `event_option_button.tscn` Text | 같은 값 | 같음 |
| 29 | 버튼 그림이 보이는 칸 x 930..1714, y +8..+92, 채움 색(청록)·잠김 색 | 실제 화면에서 잼 (나인패치 텍스처 안쪽 여백) | 씬 설정은 같음, 텍스처 여백·색은 화면이 있어야 확인 | 확인 못 함 |
| 30 | 고대 선택지: x 460..1460, 1000×100, 간격 96, 맨 아래 버튼 바닥 y 1042 | `ancient_event_layout.tscn` ContentContainer 바닥 −40, Content 간격 12, OptionsContainer 간격 −4; `NAncientEventLayout.SetDialogueLineAndAnimate`의 +10 | 같은 값 (1080 − 40 − 10 + 12 = 1042) | 같음 |
| 31 | 고대 버튼 안: HBox x 22..964, 유물 아이콘 60, 간격 10, 글자 830×74 | `ancient_event_option_button.tscn` | 같은 값 | 같음 |
| 32 | 고대 제목 금색·설명 크림, 버튼 판 색(고대별 `ButtonColor`), 마우스 올림 청록 테두리 | 실제 화면에서 잰 문턱값 | 화면이 있어야 확인 | 확인 못 함 |

## 6. 휴식처 (`RestSiteLayout`, `RestIconSignatures`)

| # | 상수 | 출처 | v0.111.0 값 | 판정 |
|---|---|---|---|---|
| 33 | 버튼 줄: 중앙 기준 −399/400, −285/−122.121, 간격 100, 가운데 정렬 | `rest_site_room.tscn` ChoicesContainer | 같은 값 | 같음 |
| 34 | 버튼 최소 246.786×162.879, 중심 (125, 82), 아이콘 위 8 / 아래 3 안쪽, 비율 유지 가운데 | `rest_site_button.tscn` | 같은 값 | 같음 |
| 35 | 마우스 올림 1.1, 누름 0.9 | `NRestSiteButton` | 1.1 / 0.9 | 같음 |
| 36 | 아이콘 원본 256×169 | `images/ui/rest_site/option_*.png` | 10개 모두 256×169 (테두리 `option_outline`만 256×173) | 같음 |
| 37 | `RestIconSignatures` (아이콘별 4×3 평균 색) | `tools/RestBench signatures`로 게임 아이콘에서 생성 | v0.111 아이콘으로 다시 만든 결과가 저장소 파일과 바이트 단위로 같음 | 같음 |

## 7. 지도 (`MapScreenGeometry`, `MapScreenLayout`, `MapScrollEstimator`)

| # | 상수 | 출처 | v0.111.0 값 | 판정 |
|---|---|---|---|---|
| 38 | `distX` = 1050 / 열 수, `distY` = 2325 / (행 수 − 1), 두 번째 보스가 있으면 × 0.9 | `NMapScreen.SetMap` (`_totalWidth`, `_totalHeight`) | 같은 값 | 같음 |
| 39 | 격자 원점 (−500, 740), 흔들림 ±21 / ±25, `Rng(seed, "map_jitter_{막}")`에서 점마다 실수 2개, `GetAllMapPoints` 순서 | `NMapScreen.SetMap` | 같은 값. 시드 계산만 바뀜(이미 `RngScheme`으로 처리) | 같음 |
| 40 | 시작점 (−80, 720(고대) / 800(일반) − 행 × distY), 크기 208 / 56 | `NMapScreen.SetMap`, `ancient_map_point.tscn`, `normal_map_point.tscn` | 같은 값 | 같음 |
| 41 | 보스 (−200, −1980 k), 두 번째 보스 −2280 k, 374×306, 중심 (187, 153) | `NMapScreen.SetMap`, `boss_map_point.tscn` | 같은 값 | 같음 |
| 42 | 스크롤 −600..1800, 기본 −600 + 현재 행 × distY | `NMapScreen` (`_scrollLimitBottom`, `_scrollLimitTop`) | 같은 값 | 같음 |
| 43 | 아이콘 회전 가우스 σ 8° (템플릿 매칭을 쓰지 않는 이유) | `NMapScreen.SetMap` (`Rng.Chaotic.NextGaussianFloat(0, 8)`) | 같은 값 | 같음 |
| 44 | 지도에서 보이는 영역 y 95..1075 (`MapMarkLayout`), 위쪽 띠 170 (`HoverYield`) | 실제 화면에서 잼 | 화면이 있어야 확인 | 확인 못 함 |

## 8. 그 밖의 화면 판별 (`ScreenClassifier`, `PriceGlyphs`)

| # | 상수 | 출처 | v0.111.0 값 | 판정 |
|---|---|---|---|---|
| 45 | 카드 자세히 보기 화면: 검은 막 알파 0.9 | `NInspectCardScreen` 여는 트윈 | 0.9 | 같음 |
| 46 | 가격 숫자 모양(GyeonggiBatang Bold·Kreon Bold 틀), 상점 깔개 색 | 실제 화면·글꼴에서 만든 틀, 화면에서 잰 색 | 화면이 있어야 확인 | 확인 못 함 |

## 게임 파일에서 바로 만드는 것 (점검 대상 아님)

- 카드 그림 데이터베이스(`CardArtDatabase`)와 상점 유물 아이콘(`RelicIconDatabase`)은 시작할 때 **설치된 게임**의 pck에서 만듭니다. 카드 그림 캐시는 그림 파일 지문이 바뀌면 다시 만듭니다. 그래서 게임 버전을 저절로 따라갑니다.
- 유물 카탈로그(`relics.<버전>.json`)는 선택된 데이터 버전을 따릅니다(`VisionRecognizer.RelicCatalogFile`).

## 남은 것

- 확인 못 한 4개(#29, #32, #44, #46)는 v0.111.0 실제 화면을 모으면 `VisionBench`로 다시 잽니다. 사용자 말로는 화면이 같아 보이므로 바뀌었을 가능성은 낮습니다.
