# M0 기술 검증 스파이크 결과

> 기준: 게임 **v0.107.1** (commit 59260271) · 작성: 2026-11-08 · 브랜치 `m0-spike`
> **방식: 모드 없는 외부 오버레이** (2026-11-08 사용자 결정, PLAN §13 D1)
> 표기: **[코드 확인]** = 디컴파일한 `sts2.dll`에서 확인 · **[실행 확인]** = 실제 게임/파일로 확인 · **[대기]** = 사용자 테스트 필요

디컴파일: `ilspycmd -p -o decompiled/sts2 -r <data 폴더> sts2.dll` (3,424개 파일, `.gitignore` 처리).
아래 `파일:줄`은 `decompiled/sts2/` 기준입니다.

---

## 요약

| # | 체크 | 결과 | 상태 |
|---|---|---|---|
| 1 | 게임 구조 분석 | 세이브·로그·리소스·카드 데이터 위치와 형식 모두 확인 | ✅ |
| 2 | 카드 카탈로그 (모드 없이) | `sts2.dll` 정적 분석으로 **578장** (ID·희귀도·비용·캐릭터·키워드), 한/영 이름 전부 매칭 | ✅ 실행 확인 |
| 3 | 카드 그림 추출 | `SlayTheSpire2.pck`에서 카드 그림 **875장** (무손실 WebP 1000×760) | ✅ 실행 확인 |
| 4 | 세이브·로그로 상태 읽기 | 덱·맵·HP·골드는 세이브, 실시간 사건은 로그. **보상 카드는 고르기 전에 파일로 알 수 없음** | ✅ 실행 확인 |
| 5 | WPF Topmost 창 | 게임이 3842×2160 전체화면으로 도는 중에 테스트 창 실행 | ⏳ 사용자 눈으로 확인 대기 |
| 6 | 게임 화면 캡처 | `Ctrl+F9` 캡처 기능 추가 | ⏳ 보상·상점 캡처 대기 |
| 7 | 카드 그림 인식 정확도 | 캡처 확보 후 pHash 매칭 시험 | ⏳ |

**중요한 발견**
1. **로그가 생각보다 많은 걸 알려줍니다.** 전투 승리(= 보상 화면 열림), 방 종류, 고른 카드, 막 이동이 즉시 찍힙니다. 보상 카드마다 `Card rarity: Rolled ...` 줄도 찍혀 **몇 번째 카드가 희귀인지**까지 알 수 있습니다.
2. **보상·상점 카드 ID는 고르기 전에는 어떤 파일에도 없습니다.** → 화면 인식이 필요합니다 (PLAN §5.0).
3. **협동에서 방장이 아니면 세이브 파일이 아예 없습니다** (`RunSaveManager.cs:79`). 손님일 때는 로그 + 화면만으로 읽어야 합니다.
4. 사용자 화면은 **4K (3842×2160, Godot 전체화면 = 모니터보다 2px 큰 창)** 입니다. 설정 파일의 1920×1080과 다릅니다. 인식 좌표는 해상도 비율로 계산해야 합니다.

---

## 1. 카드 카탈로그 [실행 확인]

`tools/CatalogBuilder` (Mono.Cecil, net10.0). 게임을 실행하지 않고 `sts2.dll`의 IL을 읽습니다.

```
dotnet run --project tools/CatalogBuilder -- [--dll <path>] [--loc <dir>] [--out <file>]
```

- **ID 규칙**: 클래스 이름을 게임과 같은 정규식으로 슬러그화 (`StrikeDefect` → `CARD.STRIKE_DEFECT`, `ModelDb.cs:471-494`).
- **값 해석 순서**: 카드 클래스의 상수 getter → 생성자의 `: base(cost, type, rarity, target)` 인자 → `CardModel` 기본값. 작은 IL 해석기(`IlEvaluator.cs`)로 상수·배열·컬렉션 초기화를 계산합니다.
- **풀·캐릭터**: 각 풀의 `GenerateAllCards`, 캐릭터의 `CardPool`/`StartingDeck`에서 `ModelDb.Card<T>()` 호출을 찾습니다.
- **결과**: 578장. 아이언클래드 87, 나머지 캐릭터 88씩. 무색 64, 저주 18, 이벤트 27, 토큰 14, 상태이상 12, 퀘스트 3, 폐기 1.
  - 희귀도: 고급 219, 희귀 155, 일반 100, 기본 19, 이벤트 19, 고대 18 등
  - 해석 실패 2장(`gainsBlock`만), ILSpy 소스와 정규식 교차 검증 0건 불일치
- **레포에 올리는 것**: 이름·설명을 뺀 `data/catalog/cards.v0.107.1.json` (ID와 수치만).
  **로컬 전용**: 이름·설명 포함 `data/local/catalog.v0.107.1.json` (게임 텍스트라 커밋 안 함).

## 2. 게임 리소스 추출 [실행 확인]

`tools/PckTool` (net10.0). PCK를 **읽기 전용**으로 엽니다.

```
PckTool list [filter]
PckTool extract "localization/kor/*.json" data\local\pck\localization\kor
PckTool extract-images "images/packed/card_portraits/**/*.png" data\local\pck\card_art
```

- **PCK 형식**: Godot 4.5 GDPC v3, 플래그 0x02(상대 오프셋), **암호화 없음**, 파일 15,658개.
- **현지화**: `localization/<언어>/cards.json` 등 46개 파일. 평평한 사전 (`"STRIKE_DEFECT.title": "타격"`). 14개 언어.
- **카드 그림**: `images/packed/card_portraits/<풀>/<id 소문자>.png` (`CardModel.cs:148`). `.ctex`(GST2) 안에 무손실 WebP가 통째로 들어 있어 헤더만 잘라내면 됩니다.
  - 875장 = 본 그림 606 + 옛 베타 그림 267 + 기본 2. 본 그림이 없는 카드 37장 중 8장은 베타 그림만 있음.
  - 게임 화면에서는 카드 아틀라스(BC7)의 250×190 영역으로 그립니다 → 인식할 때 이 크기로 줄여 해시합니다.
- **화면 판별 템플릿 후보**: `images/ui/reward_screen/*.png` 12개 (보상 배너, 카드 아이콘, 스킵 버튼 등. 11개 WebP 추출 가능).
  카드 테두리·희귀도 배너는 셰이더 + BC7 아틀라스라 바로 꺼내기 어렵습니다.

## 3. 세이브·로그로 읽을 수 있는 것 [실행 확인]

사용자 데이터: `%APPDATA%\SlayTheSpire2\steam\<SteamID>\profile1\saves\`

### 3.1 `current_run.save` / `current_run_mp.save` (schema 16)

```json
{"ascension":1,"current_act_index":1,
 "acts":[{"id":"ACT.UNDERDOCKS","saved_map":{"width":7,"height":15,
   "start":{"coord":{"col":3,"row":0},"type":"ancient","children":[{"col":1,"row":1}]},
   "points":[{"coord":{"col":0,"row":8},"type":"treasure","children":[{"col":0,"row":9}]}],
   "boss":{"coord":{"col":3,"row":15},"type":"boss"}}}],
 "visited_map_coords":[{"col":3,"row":0},{"col":4,"row":1}],
 "players":[{"net_id":1,"character_id":"CHARACTER.IRONCLAD","current_hp":77,"max_hp":87,"gold":65,
   "deck":[{"id":"CARD.BASH","current_upgrade_level":1,"floor_added_to_deck":1}],
   "relics":[{"id":"RELIC.BURNING_BLOOD"}],"potions":[{"id":"POTION.DUPLICATOR","slot_index":0}],
   "rng":{"seed":2674631688,"counters":{"rewards":193,"shops":56}}}],
 "map_point_history":[[{"map_point_type":"rest_site","player_stats":[{"card_choices":[...]}]}]]}
```

- 현재 위치 = `visited_map_coords` 마지막 칸. 맵 간선 = 각 점의 `children`. 현재 막의 맵만 들어 있습니다.
- **협동에서 내 플레이어 찾기**: `net_id` = Steam ID (`<SteamID>`). 싱글은 `net_id: 1`.
- 쓰기 방식: `.tmp`에 쓴 뒤 이름 바꾸기 + `.backup` 보관 → 감시할 때 이름 변경 이벤트도 받아야 합니다.

**저장 시점** (모든 호출 지점)
1. 맵에서 다음 칸에 들어갈 때 (`RunManager.cs:768, 826`)
2. 전투 승리 직후, **보상 생성 전** (`CombatManager.cs:1009`)
3. 고대의 존재 이벤트 종료 (`EventRoom.cs:139`)
4. 런 시작·이어하기

→ 보상 화면이 열릴 때, 카드를 고른 뒤, 상점에서 살 때, 휴식처 강화 뒤에는 **저장하지 않습니다.** 그 사이 덱 변화는 로그로 보정합니다.
→ 제시됐던 카드·상점 품목은 **다음 칸에 들어간 뒤** `map_point_history`의 `card_choices`로 확인할 수 있습니다 (인식 결과 검증·학습용).

### 3.2 `logs/godot.log` 실시간 신호

| 신호 | 로그 예시 | 신뢰도 |
|---|---|---|
| 런 시작 | `Embarking on a singleplayer IRONCLAD run. Ascension: 1 Seed: ...` / `Embarking on a multiplayer run. Players: ...` | 높음 |
| 다음 칸 선택 | `Player vote changed for 1: ->MapVote (gen: 2 coord: (4, 9))` | 높음 |
| 방 진입 | `Preloading 'Combat Room' / 'Merchant Room' / 'RestSite Room' / 'Event Room' / 'Treasure Room' assets...` | 높음 |
| 일반/엘리트/보스 | `Creating NCombatRoom with mode=ActiveCombat encounter=KNIGHTS_ELITE.` (접미사 `_WEAK/_NORMAL/_ELITE/_BOSS`) | 높음 |
| **전투 승리 = 보상 화면** | `CHARACTER.DEFECT has won against encounter ENCOUNTER.NIBBITS_WEAK.` 뒤에 카드마다 `Card rarity: Rolled 0.455, need < -0.02 for rare` | 높음 |
| 카드 획득 | `Player 1 obtained CARD.CHAOS from card reward` | 높음 |
| 제거·강화 | `Player 1 chose cards [HELLRAISER]` (직전 방이 상점이면 제거, 휴식처면 강화) | 중간 |
| 막 이동 | `Preloading 'Act=HIVE' assets...` | 높음 |
| 런 종료 | `has lost to encounter` / `has won a run`, `Saved run history: <n>.run` | 높음 |

- 로그에 **없는 것**: 맵 화면 열기, 상점 구매, 카드 보상 스킵(Debug 레벨).
- 협동에서는 게임이 Debug 로그를 자동으로 켜서 `[RewardsSetSynchronizer]`, `[RestSiteSynchronizer] ... Option: SMITH` 같은 줄이 더 나옵니다.
- **읽는 법 주의**: 디렉터리 목록에서는 `godot.log`가 0바이트로 보일 수 있습니다. `FileShare.ReadWrite`로 열고 스트림 길이를 쓰세요. 약 1초 간격으로 기록되어 tail이 가능합니다.
- **로그 교체**: 게임을 켤 때 이전 로그를 `godotYYYY-MM-DDTHH.MM.SS.log`로 바꾸고 5개를 보관합니다.

### 3.3 고르기 전에 보상 카드를 알 수 있나? → **불가** [코드 확인]

- `CardReward.ToSerializable` (`CardReward.cs:334-357`)은 출처·확률·풀 ID·개수만 저장하고 카드 ID는 저장하지 않습니다.
- 전투 승리 저장이 보상 생성보다 **먼저** 일어납니다.
- 이론상 남은 길은 게임 RNG와 `CardFactory`를 그대로 재구현해 `seed` + `rewards` 카운터로 예측하는 것입니다. 정확하지만 패치마다 깨지기 쉬워 **보류**합니다. 화면 인식이 실패할 때 쓸 v2 후보로 둡니다.

## 4. 오버레이 창과 화면 캡처 [대기]

`tools/OverlaySpike` (WPF, net10.0-windows)
- `Topmost` + `WS_EX_NOACTIVATE`(클릭해도 포커스를 안 뺏음) + `WS_EX_TOOLWINDOW`. 1초마다 `HWND_TOPMOST`를 다시 지정합니다.
- `Ctrl+F7`: 클릭 통과(`WS_EX_TRANSPARENT`) 토글
- `Ctrl+F9`: 게임 창 영역을 GDI(`CopyFromScreen`)로 캡처해 `%LOCALAPPDATA%\Sts2Overlay\captures\`에 저장. 결과가 검게 나오면 Windows.Graphics.Capture로 바꿔야 합니다.
- 기록: `%LOCALAPPDATA%\Sts2Overlay\overlay-spike.log`

| 항목 | 결과 |
|---|---|
| 실행 시 전면 창 | `SlayTheSpire2 (3842x2160 @ 0,0)` — Godot 전체화면(모니터 +2px) 확인 |
| 전체화면 위에 보임 | ⏳ |
| 클릭해도 게임 포커스 유지 | ⏳ |
| GDI 캡처가 검지 않음 | ⏳ |

## 4.1 카드 그림 지각 해시 (pHash) [실행 확인, 오프라인]

`tools/VisionSpike` (net10.0, ImageSharp)

```
VisionSpike build-hashes                      # 카드 그림 → data/local/cardart-hashes.v0.107.1.json
VisionSpike selftest                          # 250×190 축소 + 흐림 + 어둡게 한 뒤 다시 찾기
VisionSpike match <capture.png> <x,y,w,h>     # 캡처의 카드 그림 영역 → 가장 가까운 카드 5개
```

- 해시 616개 (본 그림 + 본 그림 없는 카드의 베타 그림).
- 카드끼리 가장 가까운 해시 거리: 중앙값 **18비트**(64비트 중) → 서로 잘 구별됩니다.
- **자체 테스트 615/616 정답.** 틀린 1장은 `BLACKENED_BLADE`와 `BLADE_OF_INK`: 두 카드가 **같은 그림**을 써서 거리 0입니다 → 이런 쌍은 이름 OCR이나 캐릭터 풀로 구분해야 합니다.
- 실제 게임 화면은 카드 테두리가 그림 일부를 가리므로, 캡처로 다시 확인해야 합니다 ⏳

## 5. 추천 엔진용 수치 [코드 확인]

**카드 희귀도** (`Odds/CardRarityOdds.cs`, 괄호는 승천 7)

| 출처 | 희귀 | 고급 | 일반 |
|---|---|---|---|
| 일반 전투 | 3% (1.49%) | 37% | 나머지 |
| 엘리트 | 10% (5%) | 40% | 나머지 |
| 보스 | 100% | 0 | 0 |
| 상점 | 9% (4.5%) | 37% | 나머지 |

- **희귀 보정치**: −5%에서 시작, 희귀가 안 나올 때마다 카드 1장당 +1% (A7 +0.5%), 상한 40%, 희귀가 나오면 −5%로 초기화. 로그의 `offset = ...` 값으로 현재 보정치를 바로 읽을 수 있습니다.
- **보상 카드 강화 확률**: 희귀가 아닌 카드에 `막 번호 × 25%` (A7 12.5%). 상점 카드는 강화 안 됨.
- **상점**: 일반 50 / 고급 75 / 희귀 150, 무색 ×1.15, ×(0.95~1.05), 세일 1장 절반. 캐릭터 카드 5장(공격 2, 스킬 2, 파워 1) + 무색 2장(고급 1, 희귀 1).
- **카드 제거**: `75 + 25 × 제거 횟수` (승천 6 이상 `100 + 50 ×`).

---

## 부록 A. 모드 방식 조사 결과 (폐기, 참고용)

D1을 바꾸기 전에 확인한 내용입니다. 나중에 모드 방식을 다시 검토할 때 쓰도록 남깁니다. 코드는 커밋 `a5a827d`에 있습니다.

- **모드 로더** (`Modding/ModManager.cs`): `<게임>/mods/<id>/<id>.json` + `<id>.dll`, `[ModInitializer]` static 메서드. 모드는 ModelDb보다 먼저 로드됩니다. 첫 실행 때 허용 팝업이 뜨고 게임이 재시작됩니다.
- **modded 세이브**: `steam\<id>\modded\profile1\` 빈 프로필로 시작 (v0.107.1에는 복사 기능 없음).
- **업적**: 이 빌드는 모두에게 비활성 (`AchievementsUtil.cs:30-32`). 모드 시 통계 업로드·Sentry 차단.
- **협동**: `affects_gameplay:false`면 양방향 입장 가능 (`JoinFlow.cs:94-120`). `AbstractModel` 정의나 RNG 호출은 `StateDivergence` 위험.
- **후킹 지점**: `NCardRewardSelectionScreen.RefreshOptions`, `NGridCardHolder`(풀 재사용 주의), `NMerchantCard.UpdateVisual`, `NGame._Input`(F8 미사용).
- 사용자 세이브 백업: `Documents\<save-backup>` (모드 설치 전 복사본, 모드는 실제로 로드된 적 없음).
