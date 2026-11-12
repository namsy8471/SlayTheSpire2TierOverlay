# v0.111.0 데이터 변경 목록

게임 v0.111.0(2026-11-12부터 기준 버전)용 카탈로그와 데이터 팩 `data/packs/v0.111.0/`에서 v0.107.1 대비 바뀐 것을 영역별로 정리합니다.
모든 파일은 ID·수치만 담습니다. 데이터 팩은 v0.107.1 팩을 그대로 복사한 뒤 아래만 바꿨습니다.
**코드(`src/`)는 아직 v0.107.1 팩과 카탈로그를 읽습니다**(§9). 이 문서의 데이터는 코드가 새 경로로 바뀌어야 쓰입니다.

## 1. 카탈로그 (`data/catalog/*.v0.111.0.json`)

만드는 명령은 [팩 README §0.1](../../../data/packs/v0.111.0/README.md#01-카탈로그-다시-만들기)에 있습니다.

| 파일 | v0.107.1 | v0.111.0 | 바뀐 것 |
|---|---|---|---|
| `cards` | 578 | 596 | 추가 19, 삭제 1(`SCARE`), 필드가 바뀐 카드 25 + `EXPECT_A_FIGHT`의 `gainsBlock` false→true |
| `card-vars` | 578 | 596 | 설명 변수가 바뀐 카드 46 |
| `card-pools` | 5 풀 | 5 풀 | 디펙트 91, 아이언클래드 90, 네크로바인더 91, 리젠트 91, 사일런트 91. 에포크(캐릭터당 3개 × 3장)는 그대로 |
| `relics` | 297 | 299 | 추가 `DOWSING_ROD`, `NEOWS_SACRIFICE` |
| `relic-vars` | 297 | 299 | 수치가 바뀐 유물 10 (아래) |
| `starting-decks` | 5 | 5 | 변화 없음 |

- 새 카드 19장 = 캐릭터·무색 풀 17장 + `ABUNDANCE`(이벤트 풀, 고대 희귀도) + `DOWSING`(퀘스트 풀). 풀 카드 17장 중 16장이 협동 전용(`MultiplayerOnly`)이고, 솔로에도 나오는 카드는 `SIDESTEP`(사일런트 고급)뿐입니다.
- 솔로 보상 풀 크기는 그대로입니다(캐릭터당 일반 20 / 고급 35 / 희귀 25). 협동 풀은 고급 36→38(사일런트 35→38), 희귀 26→27, 무색 고급 39→40.
- 카탈로그 필드나 설명 변수가 바뀐 카드는 합쳐서 64장이고, 그중 57장이 티어 팩에 있습니다. 나머지 7장(`BRIGHTEST_FLAME`, `FUEL`, `GIANT_ROCK`, `MAUL`, `MINION_SACRIFICE`, `RELAX`, `WHISTLE`)은 풀 밖 카드입니다.

수치가 바뀐 유물 10개(`relic-vars`):

| 유물 | v0.107.1 | v0.111.0 |
|---|---|---|
| `BEAUTIFUL_BRACELET` | Cards 3, Swift 3 | Cards 4, Swift 2 |
| `DIAMOND_DIADEM` | CardThreshold 2 | Block 20 (효과가 바뀜) |
| `DISTINGUISHED_CAPE` | HpLoss 9 | Curses 2 (단점이 `SERE_TALON`과 맞바뀜) |
| `SERE_TALON` | Curses 2 | HpLoss 9 |
| `DIVINE_DESTINY` | 6 | 7 |
| `FUR_COAT` | 7 | 8 |
| `REGALITE` | 2 | 4 |
| `SEAL_OF_GOLD` | 5 | 3 |
| `SIGNET_RING` | 999 | 888 |
| `TOY_BOX` | 4 | 5 |

## 2. 티어 (`tiers/*.json`)

근거 표: [`new-card-tiers.md`](new-card-tiers.md) (임시).

| 항목 | 수 | 내용 |
|---|---|---|
| 삭제 | 1 | `SCARE` (사일런트) |
| 추가 | 17 | 캐릭터별 3장(사일런트 4장) + 무색 1장. `SIDESTEP` B(0.55, 선택률 근거), `SOULBOUND` D, 나머지 15장 C(0.4~0.45, 협동 전용, 판단) |
| `changedIn: "v0.111.0"` | 25 | 바뀐 카드 중 v0.111 제시 30회 미만이거나 선택률 티어가 1단계 다른 카드 |
| 티어 글자 변경 | 0 | 2단계 이상 어긋난 바뀐 카드가 없음 |
| 역할·축 태그 수정 | 5 | `EXPECT_A_FIGHT`, `HAZE`, `OUTBREAK`, `EXPERTISE`, `EIDOLON` |
| 대체값 `type`·`rarity`·`cost` | 19칸 | v0.111 카탈로그 값으로 |
| 파일 머리 주석 | 6 파일 | `dataVersion` v0.111.0 |

- `ABUNDANCE`·`DOWSING`은 보상에 나오지 않는 이벤트·퀘스트 카드라 티어를 넣지 않았습니다(전투 데이터에는 있음).
- 새 카드에는 `upgradeValue`가 없습니다(강화 표본 없음 → `upgradedDelta` 사용).

## 3. 아키타입·콤보·유물 궁합·가중치·영어 텍스트

| 파일 | 바뀐 것 | 내용 |
|---|---|---|
| `archetypes/*.json` | 0 | 바뀐 카드를 가리키는 항목은 있지만 역할이 그대로 맞음. 새 카드는 솔로 데이터가 없어 빌드에 넣지 않음 |
| `combos/silent.json` | 1 | `NOXIOUS_FUMES`+`OUTBREAK` 콤보 이름·이유: `OUTBREAK`가 파워에서 "독을 주고 바로 발동"하는 스킬로 바뀜 → "광역 독 → 즉시 발동" |
| `relic-fit.json` | 0 | `silent.shiv`의 `BEAUTIFUL_BRACELET`·`TOY_BOX`는 수치만 바뀌어 유지 |
| `weights.json` | 주석 1줄 | 첫 주석의 버전만. 값은 그대로 |
| `text.eng.json` | 18 | 새 카드 영어 팁 17 + 위 콤보 이름·이유의 영어판 1 |
| `relics.json` | 0 | 효과 수치는 없음. 새 유물 2개는 v0.111 베타 데이터로 이미 있음(`DOWSING_ROD` 미사용 n=187, `NEOWS_SACRIFICE` 사용 n=181) |
| `events.stats.json` | 0 | 모든 키가 v0.111 `events.json`의 선택지로 그대로 연결됨 |

## 4. 전투 시뮬레이터 데이터 (`combat/`)

형식: [`combat/README.md`](../../../data/packs/v0.111.0/combat/README.md). 전투 테스트 37/37 통과.

| 파일 | 추가 | 삭제 | 변경 | 내용 |
|---|---|---|---|---|
| `cards.json` | 19 | 1 (`SCARE`) | 57 | 새 카드 14장 `full`, 5장 `approx`(`CACOPHONY`, `IMITATION_LEARNING`, `MIDNIGHT`, `ONE_FOR_ALL`, `THE_BALL`) |
| `relics.json` | 2 (`_irrelevant`) | 0 | 2 | `DIVINE_DESTINY` 7, `SEAL_OF_GOLD` 메모. `DOWSING_ROD`·`NEOWS_SACRIFICE`는 전투와 무관 |
| `potions.json` | 1 (`AMBERGRIS`, `none`) | 0 | 0 | 기존 64개 그대로 |
| `characters.json` | 0 | 0 | 메모 1 | |

크게 바뀐 카드 모델: `EXPECT_A_FIGHT`(play 비움), `OUTBREAK`(→ `approx`), `EIDOLON`(`full` → `none`), `EXPERTISE`(→ `approx`), `WELL_LAID_PLANS`(`none` → `full`). 그 밖에 `SYNCHRONIZE`, `HYPERBEAM`, `HAZE`, `FUEL`, `GUIDING_STAR`, `REND`, `MIRAGE`, `TIMES_UP`, `TRASH_TO_TREASURE` 등 수치·키워드 갱신.

## 5. 이벤트 (`events.json`)

66개 전부를 v0.111.0 클래스로 다시 확인했습니다: **변경 14, 추가 0, 삭제 0**. `gameVersion`·`source` 갱신.

| 이벤트 | 바뀐 것 |
|---|---|
| `BATTLEWORN_DUMMY` | 전투가 V1/V2/V3로 나뉨(HP 75/150/300, 3턴 제한은 그대로) |
| `DENSE_VEGETATION` | `shared: true` |
| `JUNGLE_MAZE_ADVENTURE` | `shared: true`, 등장 조건 "협동: 모든 플레이어 HP > 18" |
| `SLIPPERY_BRIDGE` | 메모만 |
| `SPIRALING_WHIRLPOOL` | 회복 = floor(0.33 × 최대 체력) |
| `TEA_MASTER` | 등장 막에 `GLORY` 추가 |
| `WAR_HISTORIAN_REPY` | 메모만 |
| `NEOW` | 저주 칸 유물 10개. 새 선택지 `DOWSING_ROD`(유물 + `CARD.DOWSING`), `NEOWS_SACRIFICE`(유물 + `POTION.AMBERGRIS` + 저주 `CARD.GUILTY`). `NEOWS_SACRIFICE`가 나오면 긍정 풀에서 `PHIAL_HOLSTER`·`LOST_COFFER` 제외 |
| `DARV` | 유물 하나짜리 묶음 11개 |
| `NONUPEIPE` | `BEAUTIFUL_BRACELET` 무작위 4장·Swift 2, `DIAMOND_DIADEM` 새 효과, `FUR_COAT` 8, `SIGNET_RING` 888 |
| `OROBAS` | `SAND_CASTLE`이 두 번째 칸으로. `OROBAS.<캐릭터>` 통계 ID는 바다 유리 로그 ID임을 메모 |
| `TANX` | `WHISTLE` 비용 2 |
| `TEZCATARA` | `TOY_BOX` 5, `SEAL_OF_GOLD` 3, `TOASTY_MITTENS` 발동 시점 |
| `VAKUU` | `SERE_TALON`·`DISTINGUISHED_CAPE` 단점 맞바뀜 |

- 메모 2곳의 카드 이름을 ID(`CARD.MAUL`, `CARD.WHISTLE`)로 바꿨습니다.
- `PAEL`은 바뀌지 않았습니다.

## 6. 학습 모델 (`model/card-policy.json`)

| 항목 | 값 |
|---|---|
| 바꾼 것 | 없음 |
| 아는 카드 | 535 (보상 화면 38,847개, 플레이어 168명) |
| 새 카드 중 아는 카드 | `SIDESTEP`(n=143), `ABUNDANCE`, `DOWSING` |
| 모르는 새 카드 | 협동 전용 16장 → 오버레이는 학습값을 만들지 않고 `CoopOnlyNoSoloData`(추정)로 표시 |
| 바뀐 카드 25장 중 아는 카드 | 21 (`changedIn`이 있으므로 학습값이 있어도 "추정") |
| 사라진 카드 | `SCARE` 항목이 남아 있지만 제시되지 않으므로 쓰이지 않음 |

없는 값을 지어내지 않기 위해 모델은 다시 학습할 때까지 그대로 둡니다.

## 7. 검증

| 검사 | 결과 |
|---|---|
| `DataPackValidator` (v0.111.0 팩 + v0.111.0 카탈로그) | 오류 0, 경고 8(모두 의도한 "core 없음": `defect.dark_orbs`, `defect.orb_variety`, `ironclad.attack_replay`, `ironclad.strength`, `ironclad.strike_tempo`, `ironclad.vulnerable`, `necrobinder.ethereal`, `silent.sly_discard`) |
| 팩 테스트(`RealPackTests`, `PackTextTests`, `EventCatalogTests`, `TurnSimulatorTests`, `DummyHistoryTests`를 v0.111.0 경로로 바꾼 임시 사본) | 63/63 통과 |
| 전투 테스트 | 37/37 통과 |

임시 사본에서 바꾼 기대값: v0.107.1 팩에 없는 유물 목록 `[]`, v0.107.1에 없는 고대 선택지 `OROBAS.IRONCLAD`·`OROBAS.REGENT`·`OROBAS.SILENT`. 저장소의 테스트는 아직 v0.107.1을 가리키므로 바꾸지 않았습니다(§9).

## 8. 정하지 못한 것

| 영역 | 내용 |
|---|---|
| 티어 | 협동 전용 새 카드 16장은 솔로 표본 0 → 판단 값. 협동 런 데이터가 생기면 다시 매김 |
| 티어 | 1단계 차이 바뀐 카드 18장(`new-card-tiers.md` §3)은 옮기지 않음. 플레이어 균형·재표본을 한 v2 방식으로 다시 볼 것 |
| 티어 | 무색 기준 카드가 11장뿐이라 무색 배치는 약한 근거 |
| 아키타입 | 새 카드를 빌드에 넣을지(예: `BLADE_SYMPHONY`→시브, `HIBERNATE`→냉기)는 데이터가 없어 보류 |
| 유물 궁합 | `BEAUTIFUL_BRACELET`·`TOY_BOX`의 `silent.shiv` 값은 두 버전이 섞인 측정 |
| 학습 모델 | v0.111 데이터로 다시 학습할 때까지 새 카드 16장 미지원 |
| 전투 | `approx` 5장(새 카드)과 `OUTBREAK`·`EXPERTISE`, `none`이 된 `EIDOLON` |
| 전투(기존 팩 문제, 이번에 고치지 않음) | `GUIDING_STAR` 즉시 드로우, 방어 카드 78장 중 13장만 `gainBlock`, `CRIMSON_MANTLE` 발동 중첩, `IGNITION`의 `playableIf "0"`, 무작위 카드 풀에 협동 전용 카드가 섞임 |
| 이벤트 | `DARV` 티어 표기("event")와 코드의 고대 희귀도 차이(그대로 둠) |
| 이벤트 | `CLAWS` 3장 vs 코드 최대 6, `SEA_GLASS` 3 vs 최대 15(그대로 둠) |
| 이벤트 | 공유 이벤트에서 좁은 `acts` 표기가 일관되지 않음 |
| 이벤트 | `TINKER_TIME`의 `energyPrefix` 변수 |
| 이벤트 | `DENSE_VEGETATION` 내림 방식 |
| 이벤트 | `CRYSTAL_SPHERE` 미니게임은 모델 없음 |
| 이벤트 | v0.107.1 디컴파일이 없어서, 차이가 "게임 변경"인지 "기존 팩 오류"인지는 구분하지 못함 |

## 9. 코드 쪽 후속 작업 (이번에 하지 않음)

이 데이터는 데이터만 바꿨습니다. 오버레이가 v0.111.0 데이터를 쓰려면 아래를 바꿔야 합니다.

| 묶음 | 위치 |
|---|---|
| 팩·카탈로그 경로 상수 | `AppPaths.DefaultPackDir`, `DataPackLoader.DefaultCatalogRelativePath`, `VisionRecognizer.RelicCatalogFile`, `AncientReplayEvaluation.Version`, `AppSettings` 주석 |
| 내장 리소스 | `Sts2Overlay.GameState.csproj`의 starting-decks·cards·card-pools·relics |
| 배포 | App csproj가 모든 팩·카탈로그를 포함하므로 새 팩은 자동 포함(크기 증가) |
| v0.107.1에서 옮긴 게임 규칙 (동작 변경 필요) | `NeowOptions`(새 선택지 2개, `NEOWS_SACRIFICE` 제외 규칙), `AncientOptions`(DARV 묶음, OROBAS `SAND_CASTLE`), `DeckEffects`, `EventPrediction`(`JUNGLE_MAZE_ADVENTURE` 조건, 강화로 소멸이 빠지는 카드), `RelicTrading`, `CardRarities`, `StartingLoadouts`, `RewardRollReplay`, 상점 가격 규칙, `Weights`의 풀 크기 주석 |
| 테스트 | 약 35개 파일이 v0.107.1 팩·카탈로그에 고정(`RealPackTests`의 버전 상수와 기대값 포함) |

## 10. 후속: 설명 문구만 바뀐 카드 (`FORGOTTEN_RITUAL`)

2~4절의 비교는 카탈로그 필드와 설명 수치만 봤기 때문에, 숫자는 그대로이고 문구만 바뀐 카드를 놓쳤습니다. `FORGOTTEN_RITUAL`(잊힌 의식)이 그 경우입니다.

| | v0.107.1 | v0.111.0 |
|---|---|---|
| 설명 | 이번 턴에 카드를 소멸시켰다면 에너지 3 | 에너지 3 (조건 없음) |
| 게임 문구 키 | `FORGOTTEN_RITUAL.description` | 같은 키, 조건 문장 삭제 |

랭커 코퍼스(A10 아이언클래드, 전투 카드 보상 화면, 단순 비율·재표본 없음):

| | v0.107.x (229판) | v0.111.0 (339판) |
|---|---|---|
| 선택률, 전체 플레이어 | 19.4% (20/103), 57/79위 | 60.7% (108/178), 3/80위 |
| 선택률, 우수 플레이어 | 20.9% (9/43) | 54.2% (45/83), 7/77위 |
| 소멸 카드 2장 미만 덱에서 | 13% (14/109) | 53% (77/145) |
| 소멸 카드 2장 이상 덱에서 | 24% (9/37) | 60% (44/73) |

적용(`data/packs/v0.111.0`만, v0.107.1 팩은 그대로):

- `tiers/ironclad.json`: `consumes`·`requires: ["EXHAUST"]` 삭제, 티어 C → A(2단계 차이라 3절의 "명확한 경우" 규칙에 해당), `energy` 0.5 → 0.6, 신뢰도 0.65, `changedIn: "v0.111.0"`, 팁 추가.
- `archetypes/ironclad.json`: 소멸 빌드 payoff에서 제외(소멸 덱 여부와 선택률이 거의 무관).
- `combat/cards.json`: 에너지 `3*min(1,exhaustedThisTurn)` → `3`(강화 4).
- `model/card-policy.json`은 다시 학습하지 않았습니다(두 버전을 섞어 학습한 값 그대로).

같은 방식으로 본 문구 변경 카드는 15장이고, 그중 `HELIX_DRILL`("다른 카드에 쓴 에너지"), `ROCKET_PUNCH`(비용 0 → 1 감소), `SACRIFICE`(2배 → 3배), `TRACKING`(2배 → 50%)은 `combat/cards.json` 항목이 v0.107.1과 같습니다. 이번에는 고치지 않았습니다(확인 필요).
