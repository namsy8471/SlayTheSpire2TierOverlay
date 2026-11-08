# Slay the Spire 2 티어 오버레이 설계 계획

> 작성일: 2026-11-08 · 기준 게임 버전: **v0.107.1** (사용자 PC 설치본) · 상태: **확정, 구현 시작** (2026-11-08)
> 결정 사항은 [§13](#13-결정-필요-사항)에 모아 두었습니다. D1·D2는 확정됐고, 나머지는 권장안을 기본값으로 진행합니다.
>
> **2026-11-08 개정 (D1 변경): 게임 모드를 쓰지 않고 외부 오버레이만으로 구현합니다.** 사용자 결정.
> 게임 파일은 건드리지 않고, **세이브·로그 감시 + 화면 캡처 인식**으로 상태를 읽습니다. 배지도 오버레이 창이 그립니다.
> 아래 본문에서 "Bridge 모드"를 다루는 부분(§5.1, §5.2)은 참고용으로 남기고, 바뀐 구조는 [§5](#5-전체-아키텍처)와 [§12](#12-마일스톤과-구현-스레드-분할)에 반영했습니다.
> 모드 방식 조사 결과(협동 호환, modded 세이브 분리 등)는 [SPIKE.md](SPIKE.md)에 기록해 두었습니다.
> 표기: **[확인]** = 사용자 PC 설치본이나 출처로 직접 확인함 · **[추정]** = 확인이 더 필요함

---

## 0. 한눈에 보기

1. **게임은 건드리지 않습니다.** (2026-11-08 개정) 모드를 설치하지 않으므로 modded 세이브 분리, 모드 허용 팝업, 게임 패치 때 후킹이 깨지는 문제가 없습니다.
2. **덱·맵·유물·HP·골드는 세이브 파일과 로그를 감시**해서 읽습니다. 세이브에는 맵 전체 구조와 덱이 평문 JSON으로 들어 있습니다.
3. **세이브 파일은 카드를 고른 뒤에야 갱신됩니다.** [확인] 그래서 **보상·상점에 뜬 카드는 화면 캡처로 인식**합니다.
   - 카드 그림 해시(pHash)를 게임 리소스에서 **사용자 PC에서만** 만들어 두고, 캡처 화면의 카드 그림과 맞춥니다. 강화 여부는 카드 이름 색으로 판별합니다.
   - 한국어 OCR은 보조 수단입니다.
4. 카드 위 **티어 배지는 게임 크기에 맞춘 투명·클릭 통과 오버레이 창**이 그립니다. 덱 목록, 추천 이유, 빌드 모음, 경로 추천 같은 큰 화면은 **별도 패널 창**에 띄웁니다.
5. 추천 계산은 게임과 무관한 순수 C# **Core 라이브러리**가 맡습니다. 클라우드에서도 단위 테스트할 수 있습니다.
6. 티어·아키타입·콤보 데이터는 **우리 소유의 JSON 데이터 팩**(게임 내부 카드 ID 기준)으로 관리합니다. 카드 카탈로그(ID, 한/영 이름, 희귀도, 비용)는 게임 파일(`sts2.dll`, `SlayTheSpire2.pck`)을 **읽기만 해서** 사용자 PC에서 생성합니다.
7. 첫 구현 단계는 **기술 검증 스파이크**입니다. 전체화면 위 오버레이 창, 게임 창 캡처, 카드 그림 인식 정확도, 세이브·로그 감시를 확인합니다.

---

## 1. 목표와 범위

### 1.1 사용자 요구 (2026-11-08)

| # | 요구 | 이 문서에서 다루는 곳 |
|---|---|---|
| R1 | 카드 위에 해당 카드의 **단일 티어** 표시 | §5.1, §8 |
| R2 | 보상 카드·상점 화면에서 티어 + **지금 덱에 맞는 카드**를 골라줌 | §7.1~7.4 |
| R3 | 카드를 먹으면 오버레이의 **덱 목록 자동 갱신** | §5.2, §8 |
| R4 | **빌드(아키타입) 모음**을 오버레이에서 열람 | §6.3, §7.5, §8 |
| R5 | 어떤 덱이 나올 **확률이 높은지** 표시 | §7.5 |
| R6 | 지금 **어떤 길로 가면 좋은지** 자동 추천 | §7.6 |

### 1.2 버전별 범위

| 기능 | v1 (MVP) | v2 이후 |
|---|---|---|
| 카드 배지 | 보상 3장, 상점 카드, 덱 보기 화면 | 이벤트 카드 선택, 변환/강화 화면, 유물·포션 배지 |
| 추천 | 카드 점수 + 스킵 + 이유 칩 | 다음 경로(엘리트 등)를 반영한 긴급도, 유물 시너지 |
| 상점 | 카드 구매 + 카드 제거 가치 | 유물·포션 구매, 남는 골드 계산 |
| 덱 추적 | 실시간 덱 목록, 강화 표시, 타입·비용 분포 | 덱 통계 그래프 |
| 빌드 모음 | 캐릭터별 아키타입, 완성도 %, 필요한 카드 | 완성 확률 시뮬레이션 (몬테카를로) |
| 경로 | 막(act) 시작·맵 화면에서 상위 3개 경로 | 게임 맵 위에 경로 하이라이트, 몬테카를로 위험도 |
| 데이터 | 수작업 티어 팩 v0 | 내 런 기록으로 보정, 데이터 팩 자동 업데이트 |
| 모드 없이 | 바닐라 모드 (덱/경로/빌드만) | 화면 인식으로 보상 카드 읽기 |

**멀티플레이:** 사용자 런 기록 24판 중 20판이 2인 협동입니다. 그래서 v1부터 **협동에서도 "내 덱" 기준으로 동작**해야 합니다 (읽기 전용, 나만 모드 설치). 팀원 덱을 고려한 추천은 v2입니다.

**v1 범위 밖:** 전투 중 플레이 조언, 팀원 덱을 고려한 추천, 시드 예측, macOS/Linux.

---

## 2. 참고 사이트 분석: "카드 뭐 뽑지?"

- 위치: <https://store.hailot.co/apps/5> → <https://elithelucky.github.io/sts2-guide/ko> (작성자 핸들 "무적득구", 댓글 221개) [확인]
- 형태: 바닐라 JS 웹앱. 게임과 연결되지 않으며, **덱과 보상 카드를 사용자가 직접 입력**합니다. 단축키 Q(보상 모드)·W(상점 모드), 숫자키로 카드 선택. [확인, 브라우저에서 JS 분석]

### 2.1 데이터 모델 [확인]

| 데이터 | 구조 | 비고 |
|---|---|---|
| `CHARS` | 5캐릭터: 아이언클래드, 사일런트, 디펙트, 네크로바인더, 리젠트 | |
| `XX_CARDS` (캐릭터별 89장 + 무색 73장) | `{n: 한글 이름, c: 비용, t: 타입, r: 희귀도, tier: S~D, tip: 한 줄 설명}` | **카드 키가 한글 이름**이라 띄어쓰기·패치에 취약 |
| `XX_ARCHS` (캐릭터당 7~10개) | `{id, name, color, must: [필수 카드], rec: [추천 카드], desc, source}` | 예: 아이언클래드 "취약 덱" must=[제압, 포악함] |
| `XX_COMBOS` (캐릭터당 15~26개) | `{cards: [2장], name, why}` | 카드 쌍 시너지 |
| `XX_UNI` | 범용 카드 목록 | 어느 덱에나 좋은 카드 |
| `BUILD_RELICS` | 아키타입별 핵심 유물 설명 | |
| `TS` | `S:50, A:35, B:20, C:8, D:0` | 티어 기본 점수 |

### 2.2 점수 로직 요약 [확인]

- **기본점** = 티어 점수(TS)
- **활성 빌드** (직접 선택했거나 자동 감지한 빌드)
  - 필수 카드면 +64~74, 추천 카드면 +30~40에 티어 보너스
  - 빌드 몰입도가 높거나 보스 보상이면 추가 가산
  - 빌드 밖 카드는 중반 이후 −12 ("오프빌드")
- **콤보**: 짝이 덱에 이미 있으면 +20~58("지금 강함"), 일부만 있으면 +10~24("나중에 좋아짐")
- **진행 단계**: 덱 크기·상점 방문·픽 수로 초/중/후반을 추정
  - 초반: 싼 공격 카드 가산, 당장 강해지지 않는 파워 감산
- **페널티**
  - 덱 18장 이상이고 빌드 무관: −18 ("덱 오염")
  - 중복: 1장 보유 −26, 2장 이상 −55
  - D티어 −25, 기본 카드 −20
- **빌드 감지**: must/rec 보유 수, 티어·희귀도, 핵심 카드 가중치로 아키타입별 점수를 매기고 1위를 활성 빌드로 삼음
- **빌드 완성도 %**: 필수 100 / 추천 56 가중치로 보유 비율을 계산해 1~5단계로 표시
- **이유 칩**: 필수, 시너지, 지금 강함, 빌드 시동, 범용, 덱 오염, 중복 등

### 2.3 가져올 점과 개선할 점

- **가져올 점**
  - 아키타입의 must/rec 구조
  - 콤보 쌍
  - 이유 칩 UX
  - 빌드 완성도 %
  - 한글 초성 검색
- **개선할 점**
  - **입력 자동화**: 모드가 게임 상태를 직접 읽음
  - **카드 키를 게임 내부 ID로**: 예 `CARD.STRIKE_DEFECT`
  - **진행 단계를 실제 막/층 정보로 판단**: 추정하지 않음
  - **점수 구성요소가 합계와 일치하는 설명**
  - **스킵을 정식 후보로**
  - **경로 추천 추가**
- **데이터 재사용 주의**: 이 사이트의 티어·아키타입 데이터는 작성자의 저작물입니다. 그대로 가져오려면 허락이 필요합니다 (§9, §13).

---

## 3. 게임 내부 조사 결과 (사용자 PC에서 직접 확인)

설치 경로: `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2`
사용자 데이터: `%APPDATA%\SlayTheSpire2`

### 3.1 엔진과 패키징 [확인]

- **엔진**: Mega Crit 커스텀 Godot, 로그 표기 "MegaDot v4.5.1.m.12.mono"
- **런타임**: .NET 9.0.7 (`sts2.runtimeconfig.json`의 tfm `net9.0`)
- **게임 코드**: `data_sts2_windows_x86_64/sts2.dll` (9.3MB)
- **API 문서**: 바로 옆에 **`sts2.xml` (XML 문서 주석, 타입 2,785개)** 이 함께 배포됩니다. 모드 개발 때 가장 좋은 API 레퍼런스입니다.
- **리소스**: `SlayTheSpire2.pck` (1.9GB). 이미지와 현지화(`res://localization/kor` 등)가 들어 있습니다.
- **함께 들어 있는 라이브러리**: `0Harmony.dll` 2.4.2, MonoMod, Sentry, Steamworks.NET, SmartFormat
- **사용자 설정**: 전체화면, 1920×1080, 144fps, D3D12, 게임 언어 `kor`

### 3.2 공식 모드 로더 [확인, `sts2.xml`]

- **클래스**: `MegaCrit.Sts2.Core.Modding.ModManager`
- **읽는 위치**: **실행 파일 옆 `mods` 폴더와 Steam 창작마당**. 폴더 안을 재귀적으로 훑어 PCK/DLL과 JSON 매니페스트를 읽습니다.
- **진입점**: `[ModInitializer]` 특성이 있으면 그 메서드를 호출합니다. 없으면 모드 어셈블리에 Harmony `PatchAll`을 실행합니다.
- **활성화 조건**: 사용자가 설정에서 **모드 로딩을 켜야** 동작합니다.
- **멀티플레이 구분**: "게임플레이에 영향 있는 모드"와 "없는 모드"를 나눕니다. 멀티플레이에서는 영향 있는 모드만 서로 비교합니다. 매니페스트 필드는 `affects_gameplay` [확인, 패치노트 출처].
- **통계 업로드**: 모드가 로드되면 **런 통계 업로드를 하지 않습니다** (`CallMetricsHooks` 주석).
- **세이브·업적**
  - 모드 사용 시 **세이브는 별도 폴더(`modded`)** 를 씁니다. v0.108부터는 첫 실행 때 기존 세이브를 복사합니다.
  - Steam 업적이 비활성화된다는 보고가 있습니다 [추정, sts2.gg·spirescope 기준].
  - 스파이크에서 v0.107.1 기준으로 직접 확인합니다.

### 3.3 세이브와 로그 [확인]

세이브 위치: `%APPDATA%\SlayTheSpire2\steam\<SteamID>\profile1\saves\`. 전부 **평문 JSON**입니다.

**`current_run.save`**
- 진행 중인 런에만 존재합니다.
- **맵 전체 구조(`SerializableActMap`)**, 덱, 유물, HP, 골드 등이 들어 있습니다.
- **기록 시점: 방에 들어갈 때, 그리고 보상을 받은 직후.** 로그로 확인했습니다. 보상 화면이 열릴 때는 기록되지 않습니다.
- 그래서 **제시된 카드 3장은 파일로 미리 알 수 없습니다.**

**`history\<유닉스시간>.run`** (완료된 런, 현재 계정에 23개)
- `players[].deck`: 최종 덱. 카드 ID, `floor_added_to_deck`, `current_upgrade_level`
- `players[].relics`, `character`, `ascension`, `seed`, `win`, `acts`
- `map_point_history[막][층]`
  - `map_point_type`: ancient, monster, elite, rest_site, shop, treasure, unknown, boss
  - `player_stats`
    - `card_choices`: 제시 카드와 `was_picked`
    - 기타 선택: `relic_choices`, `potion_choices`, `rest_site_choices`, `upgraded_cards`, `cards_removed`, `cards_gained`
    - HP와 골드 변화
- **→ 내 픽 기록으로 추천을 보정할 수 있는 학습 데이터가 됩니다** (§7.7).

**`logs\godot.log`** (실시간으로 기록)
- `Player 1 obtained CARD.CHAOS from card reward`
- `Preloading 'Merchant Room' assets...`
- `Creating NCombatRoom with mode=ActiveCombat encounter=KNIGHTS_ELITE.`
- `Wrote 47758 bytes to ... current_run.save`

**ID 형식**: `CARD.STRIKE_DEFECT`, `RELIC.CRACKED_CORE`, `CHARACTER.DEFECT`, `ENCOUNTER.KAISER_CRAB_BOSS`, `ACT.OVERGROWTH`. 로그에 따르면 ModelId 1,622개가 등록되어 있습니다.

### 3.4 후킹 후보 클래스 [확인, `sts2.xml`]

| 용도 | 클래스 |
|---|---|
| 보상 카드 3장 화면 | `Nodes.Screens.CardSelection.NCardRewardSelectionScreen` |
| 스킵 등 대체 선택지 | `NCardRewardAlternativeButton`, `Entities.CardRewardAlternatives.CardRewardAlternative` |
| 카드 노드 (배지를 붙일 곳) | `Nodes.Cards.Holders.NCardHolder` |
| 상점 | `Entities.Merchant.MerchantCardEntry`, `MerchantCardRemovalEntry`, `MerchantRelicEntry`, `MerchantPotionEntry` |
| 덱 선택 화면 (제거/변환/강화) | `NDeckCardSelectScreen`, `NDeckUpgradeSelectScreen`, `NDeckTransformSelectScreen` |
| 맵 | `Map.ActMap`, `Map.MapCoord`, `Map.MapPointType`, `Nodes.Screens.Map.*` |
| 런 상태 | `Runs.RunManager` (`IsInProgress`), `Runs.RunState` |
| 카드 정보 (자동 태깅용) | `Models.CardModel`: `Tags`, `Keywords`, `TargetType`(AllEnemies면 광역), `GainsBlock`, `CanonicalEnergyCost`, `HasEnergyCostX`, `IsBasicStrikeOrDefend` |
| 화면별 처리 참고 | `AutoSlay.Handlers.Screens.*`: 개발사의 자동 플레이 봇이 각 화면을 어떻게 읽고 누르는지 보여줌 |

(접두사 `MegaCrit.Sts2.Core.` 생략. 정확한 메서드 시그니처는 스파이크에서 ILSpy로 확인합니다.)

### 3.5 게임 콘텐츠 개요 [확인, 위키·나무위키·op.gg 기준]

- **캐릭터 5명**: 아이언클래드, 사일런트, 디펙트, 리젠트, 네크로바인더
- **카드**: 약 595장. 캐릭터당 약 90장, 나머지는 무색·저주·상태이상 등
- **희귀도**: 기본, 일반, 고급, 희귀, 고대의 존재(Ancient)
- **신규 메커니즘**: 인챈트, 퀘스트 카드, 리젠트의 별 자원, 네크로바인더의 소환·오스티
- **막 구성**: 1막 Overgrowth 또는 Underdocks, 2막 Hive, 3막 Glory
- **맵 노드**: 몬스터, 엘리트, 휴식, 상점, 미지(?), 보물, 보스, 고대의 존재(Ancient)
- **승천**: 10단계
- **패치 주기**: 정식 대형 업데이트 약 2개월, 베타 1~2주
  - **다음 대형 업데이트: 2026-11-13 예정 (베타 먼저)**
  - → 후킹이 깨질 가능성을 전제로 설계합니다

---

## 4. 게임 상태 획득 방식 비교

| 방식 | 정확도·범위 | 지연 | 개발량 | 패치 취약성 | 부작용 |
|---|---|---|---|---|---|
| **A. 읽기 전용 브리지 모드** | 최상. 보상·상점 카드 ID, 강화 여부, 화면 위치, 맵, 덱 전부 | 50ms 미만, 이벤트 기반 | 중 | 중~상 (패치마다 재빌드 가능성) | 모드 로딩 켜야 함, modded 세이브 분리, 통계 업로드 꺼짐, 업적 비활성 가능성 |
| B. 세이브·로그 감시 | 덱·유물·HP·골드·맵. **보상/상점 제시 카드는 불가** | 방 단위 | 하 | 하 | 없음 |
| C. 화면 캡처 + 인식 | 중. 한글 OCR·해상도·강화 표시가 어려움 | 100~500ms | 상 | 중 | 없음 |
| D. 메모리 읽기 | 중 | 낮음 | 최상 | 최상 | 치트 툴처럼 보임 → **제외** |

~~권장: A를 기본으로, B를 바닐라 모드 백업으로.~~
**확정 (2026-11-08 개정): B + C.** 세이브·로그 감시(B)로 덱·맵·런 상태를, 화면 캡처 인식(C)으로 보상·상점 카드를 읽습니다. A(모드)는 쓰지 않습니다 ([§13](#13-결정-필요-사항) D1).

---

## 5. 전체 아키텍처

### 5.0 개정 구조 (2026-11-08, 모드 없음)

```
┌──────────── Slay the Spire 2 (수정 안 함) ────────────┐
│  세이브(JSON) · godot.log · 게임 창 화면                │
└───────┬──────────────────┬───────────────────┬─────────┘
        │ FileSystemWatcher │ tail               │ Windows.Graphics.Capture
┌───────┴──────────────────┴───────────────────┴─────────────────────┐
│  Sts2Overlay.App (WPF, net10.0-windows)                             │
│   ├ SaveWatcher   : current_run(_mp).save → RunSnapshot (덱·맵·HP…) │
│   ├ LogTailer     : 방 진입·카드 획득·상점 등 실시간 이벤트          │
│   ├ Vision        : 게임 창 캡처 → 화면 판별 → 카드 그림 pHash 매칭 │
│   │                 → 강화 여부(이름 색) → [cardId, upgrade, rect]   │
│   ├ BadgeWindow   : 게임 창 크기, 투명·항상 클릭 통과, 카드 위 배지  │
│   ├ PanelWindow   : 추천 / 덱 / 빌드 모음 / 경로 / 설정              │
│   └ Core 호출                                                       │
└───────────────────────────┬────────────────────────────────────────┘
                            │ 참조
┌───────────────────────────┴────────────────────────────────────────┐
│  Sts2Overlay.Core (net9.0) — 아래 §5.3 그대로                       │
└────────────────────────────────────────────────────────────────────┘
  로컬 생성 데이터 (%LOCALAPPDATA%\Sts2Overlay, 커밋 안 함):
   catalog.<버전>.json (sts2.dll IL + pck 현지화), cardart-hashes.<버전>.json (pck 카드 그림)
  레포 데이터: data/packs/<버전>/ 티어·아키타입·콤보 (우리 저작물, ID만 참조)
```

- **카탈로그 생성** (`tools/CatalogBuilder`, `tools/PckTool`): `sts2.dll`을 Mono.Cecil로 정적 분석해 카드 ID·희귀도·비용·캐릭터를 뽑고, `SlayTheSpire2.pck`에서 한/영 이름과 카드 그림을 읽습니다. 게임이 업데이트되면 다시 실행합니다.
- **화면 인식 흐름**: 로그로 "전투 승리 / 상점 진입"을 감지하면 캡처 빈도를 올립니다. 화면 판별(보상 화면 배너 등 템플릿) → 카드 영역 → 그림 해시 매칭. 같은 화면이 유지되는 동안은 결과를 캐시합니다.
- **카드를 고른 뒤**: 세이브 파일 갱신과 로그 `obtained CARD.X`로 덱을 확정합니다. 화면 인식이 틀렸더라도 덱은 항상 정확합니다.
- 아래 §5.1·§5.2(모드·프로토콜)는 이전 설계로, 참고용으로만 남깁니다.

### 5.0.1 (이전 설계) 모드 방식 구조

```
┌──────────────── Slay the Spire 2 프로세스 ────────────────┐
│  Sts2Overlay.Bridge (모드, C# net9.0, Harmony, 읽기 전용)   │
│   ├ ScreenWatcher : 보상/상점/덱/맵 화면 진입·이탈 감지      │
│   ├ StateReader   : RunState → 스냅샷(덱·유물·HP·골드·맵)   │
│   ├ CatalogDumper : 카드·유물 전체 목록(ID, 한/영 이름 등)  │
│   ├ BadgeRenderer : 카드 노드에 티어 배지 자식 노드 추가    │
│   └ WsServer      : 127.0.0.1 WebSocket (토큰 인증)          │
└───────────────────────────┬────────────────────────────────┘
          상태/이벤트 JSON ↓   ↑ 배지 주석(티어·점수·추천 표시)
┌───────────────────────────┴────────────────────────────────┐
│  Sts2Overlay.App (WPF, net10.0-windows)                     │
│   ├ SourceArbiter : 모드 > 세이브/로그 감시 > (v2) 화면 인식 │
│   ├ Core 호출     : 점수·빌드·경로 계산                     │
│   ├ OverlayWindow : 게임 위 반투명 패널 (클릭 통과 토글)     │
│   ├ 탭: 실시간 추천 / 덱 / 빌드 모음 / 경로 / 설정          │
│   └ DataPackManager, ModInstaller, 단축키, 트레이           │
└───────────────────────────┬────────────────────────────────┘
                            │ 참조
┌───────────────────────────┴────────────────────────────────┐
│  Sts2Overlay.Core (net9.0, Godot 의존성 없음)               │
│   CardScorer · SkipScorer · ShopScorer · ArchetypeDetector  │
│   PathPlanner · DataPack 로더 · RunHistory 파서             │
│  Sts2Overlay.Contracts (net9.0): 프로토콜 DTO (모드와 공유) │
└─────────────────────────────────────────────────────────────┘
        데이터 팩 (data/packs/<게임버전>/*.json, 서명·버전 관리)
```

**이렇게 나누는 이유**
- **모드는 얇게 유지**합니다 (읽기 + 배지 그리기 + 전송). 게임 패치 때 고칠 곳이 작아지고, 추천 로직을 바꿔도 모드를 다시 빌드할 필요가 없습니다.
- **Core는 게임 없이 테스트**할 수 있습니다. 실제 `.run` 파일을 픽스처로 써서 클라우드 스레드에서도 개발할 수 있습니다.
- **앱은 모드 없이도 일부 동작**합니다 (바닐라 모드).
- **앱이 꺼져 있을 때**: 모드가 데이터 팩의 **기본 티어만** 배지로 표시합니다. 덱 맞춤 점수는 앱이 있을 때만 나옵니다.

### 5.1 Bridge 모드

- **매니페스트**: `id`, `name`, `version`, `has_dll: true`, `has_pck: false`, **`affects_gameplay: false`**
- **원칙: 게임 상태를 절대 바꾸지 않습니다.** 관찰만 하는 Harmony **Postfix**와 읽기만 사용합니다.
  - 근거: 상태를 건드린 다른 모드(STS2-Agent)가 멀티플레이에서 `StateDivergence`로 강퇴를 일으킨 사례가 있습니다.
- **화면 감지**: `NCardRewardSelectionScreen` 등이 열리거나 닫힐 때 Postfix를 겁니다. 카드 홀더 목록에서 `CardModel`(ID, 강화 수치)과 화면 좌표를 수집합니다.
- **배지**: 각 `NCardHolder`에 작은 `PanelContainer`와 `Label` 자식 노드를 붙입니다. 카드가 움직이면 배지도 같이 움직입니다.
  - 표시 내용: 티어 문자, 색, 추천 1위 왕관 표시
  - 단축키(F8)로 숨기기
- **카탈로그 덤프**: 게임 버전(`release_info.json`의 version/commit)이 바뀌면 한 번 실행합니다. 모든 `CardModel`을 `catalog.<버전>.json`으로 내보냅니다 (§6.1).
- **안전장치**: 모든 패치를 try/catch로 감쌉니다. 시그니처가 맞지 않으면 그 기능만 끄고 앱에 "모드 호환 안 됨, 바닐라 모드로 전환"을 알립니다.
- **게임 안 상태 표시**: 화면 구석에 작은 연결 상태 점을 표시합니다 (앱 연결 여부).

### 5.2 통신 프로토콜 (`docs/PROTOCOL.md`로 분리 예정)

- **전송 방식**: WebSocket, `127.0.0.1`, 임의 포트
- **연결 정보**: 포트와 1회용 토큰을 `%LOCALAPPDATA%\Sts2Overlay\bridge.json`에 기록합니다.
- **보안**: 브라우저가 localhost에 접근하는 것을 막기 위해 Origin 헤더가 있는 연결은 거부합니다.
- 모든 메시지에 `protocol`(정수)과 `seq`를 붙입니다.

| 방향 | 메시지 | 내용 |
|---|---|---|
| 모드→앱 | `hello` | 게임 버전, 모드 버전, 프로토콜 버전, 싱글/멀티 |
| 모드→앱 | `run.snapshot` | 캐릭터, 승천, 막, 층, HP/최대HP, 골드, 덱 `[ {id, upgrade, instanceId} ]`, 유물, 포션, 맵 `{nodes:[{coord,type}], edges, current}` |
| 모드→앱 | `screen.opened` | `screen`: card_reward \| merchant \| deck_select \| map \| other, `items`: `[ {instanceId, cardId, upgrade, price?, rect} ]`, 대체 선택지(스킵 등) |
| 모드→앱 | `screen.closed` | |
| 모드→앱 | `run.event` | card_obtained, card_removed, card_upgraded, room_entered, run_ended |
| 앱→모드 | `badges.set` | `[ {instanceId, label:"S", color, score, isBest, chips[]} ]` |
| 앱→모드 | `badges.clear` | |

### 5.3 Core 라이브러리

- **도메인 모델**: `CardRef(Id, Upgrade)`, `RunSnapshot`, `ScreenOffer`, `MapGraph`
- **`DataPack`**: JSON 로드와 스키마 검증, 카탈로그 ID와 대조
- **`CardScorer` / `SkipScorer` / `ShopScorer`**: §7.1~7.3
- **`ArchetypeDetector`**: commit 비중, 완성도 %, 필요한 카드, 완성 확률 (§7.5)
- **`PathPlanner`**: 경로 나열과 점수, 안전/균형/공격 3안 (§7.6)
- **`RunHistoryReader`**: `.run` 파싱, 개인 통계 (§7.7)
- 모든 점수 함수는 **구성요소 목록**(`[{key:"aoe_need", label:"광역 부족", value:+24}]`)을 반환합니다. 합계와 정확히 일치해야 합니다.

### 5.4 App (오버레이)

- **WPF, .NET 10 LTS.** 사용자가 .NET 개발 환경을 가지고 있고, 모드·Core와 같은 C#이라 DTO를 공유할 수 있습니다.
- **창 두 개**
  - **패널 창**: 게임 오른쪽 또는 왼쪽 가장자리에 반투명으로 표시. `Topmost`와 `WS_EX_NOACTIVATE`로 클릭해도 게임 포커스를 뺏지 않습니다. 단축키로 클릭 통과를 토글합니다.
  - **(바닐라/화면 인식 모드 전용) 배지 창**: 게임 크기에 맞춘, 항상 클릭이 통과되는 창
- **게임 창 추적**: `SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE)`. 모니터를 넘는 2px은 잘라냅니다.
- **전체화면 호환성**
  - Godot 4.5의 "독점 전체화면"도 실제로는 테두리 없는 창을 모니터 크기로 키운 것이고, 일반 전체화면은 2px 더 크게 만든 창입니다 [확인, Godot 소스].
  - 따라서 Topmost 창이 위에 보일 가능성이 높습니다. 다만 GPU·드라이버별로 스파이크에서 확인합니다.
  - 안 되면 "테두리 없는 창" 설정을 안내하고 듀얼 모니터 모드를 씁니다.
- **듀얼 모니터 모드**: 패널을 일반 창으로 다른 모니터에 띄웁니다.
- **기타**
  - 트레이 아이콘
  - 모드 설치/업데이트 버튼 (Steam `libraryfolders.vdf`로 게임 폴더 탐색)
  - 데이터 팩 업데이트 (§6.6)

### 5.5 바닐라 모드 (모드 없이)

- `current_run.save`(FileSystemWatcher, 쓰기 완료 후 300ms 디바운스)와 `godot.log`(tail)를 감시합니다.
- **가능**: 덱 목록 자동 갱신, 빌드 모음·완성도, 맵 경로 추천 (세이브에 맵 구조가 있음)
- **불가**: 보상·상점 카드 배지. 대신 패널에서 **"수동 입력"**(참고 사이트처럼 초성 검색으로 3장 선택)을 제공합니다.

### 5.6 (v2, 선택) 화면 인식 백업

- **캡처**: Windows.Graphics.Capture로 게임 창만 캡처합니다 (우리 오버레이는 찍히지 않음).
- **화면 판별**: 보상 화면 배너 등을 OpenCvSharp 템플릿 매칭으로 판별합니다.
- **카드 식별**
  - **카드 아트 지각 해시(pHash)** 가 1순위입니다. 언어와 무관하고, 카드당 1ms 미만입니다.
  - 강화 여부는 제목 색(녹색)으로 판정합니다.
  - 보조로 PaddleOCR 한국어 모델(ONNX)로 제목을 읽어 카드 이름 목록과 퍼지 매칭합니다.
- **저작권**: 게임 이미지는 배포하지 않고 사용자 PC에서 해시만 생성합니다.

---

## 6. 데이터 설계 (`docs/DATA_SCHEMA.md` + `data/schema/*.schema.json`로 분리 예정)

### 6.1 카드 카탈로그 (자동 생성, 직접 편집 안 함)

모드의 `CatalogDumper`가 사용자 설치본에서 생성합니다. **게임 텍스트를 레포에 커밋하지 않고** 로컬에서 만들어 씁니다.

```json
{
  "gameVersion": "v0.107.1", "commit": "59260271",
  "cards": [{
    "id": "CARD.BALL_LIGHTNING",
    "pool": "CHARACTER.DEFECT",
    "names": { "kor": "구체 번개", "eng": "Ball Lightning" },
    "type": "attack", "rarity": "common", "cost": 1, "costX": false,
    "keywords": ["..."], "tags": ["..."],
    "target": "AnyEnemy", "gainsBlock": false,
    "multiplayerOnly": false,
    "upgrade": { "maxLevel": 1 }
  }]
}
```
(카드 이름은 예시이며 실제 값은 덤프로 채웁니다.)

**자동 역할 태깅** (수작업 태그의 초기값)

| 조건 | 태그 |
|---|---|
| `target: AllEnemies` | `aoe` |
| `gainsBlock` | `block` |
| 설명에 드로우 | `draw` |
| 0코스트 | `cycle↑` |
| 파워 타입 | `scaling` 후보 |

### 6.2 티어 팩 (`data/packs/<버전>/tiers/<캐릭터>.json`, 수작업)

```json
{
  "character": "CHARACTER.DEFECT", "dataVersion": "v0.107.1",
  "cards": {
    "CARD.BALL_LIGHTNING": {
      "tier": "B",
      "actTier": { "1": "A" },
      "upgradedDelta": 5,
      "roles": { "frontload": 0.6, "aoe": 0.0, "scaling": 0.2 },
      "provides": ["ORB_LIGHTNING"], "consumes": [], "requires": [],
      "dupFactor": 0.8, "cycle": 0.0,
      "tip": "1막 전투력 보강, 번개 구체 시너지",
      "confidence": 0.7, "changedIn": null, "source": "curated"
    }
  }
}
```

- **티어 등급**: S/A/B/C/D 5단계. 숫자값은 `weights.json`에서 정의합니다 (S=80, A=65, B=50, C=35, D=20).
- **`changedIn`**: 패치로 수치가 바뀐 카드는 자동으로 표시되고, 신뢰도가 낮아져 "추정" 배지가 붙습니다.
- **`provides` / `consumes` / `requires`**: 자원 축 태그입니다. Deck Compass의 생산자·소비자·증폭기·페이오프 모델을 참고했습니다 ([research/DECK_COMPASS.md](research/DECK_COMPASS.md)).
  - 예: 교활 카드는 `requires: ["DISCARD_ENGINE"]`
  - 별 생성 카드는 `provides: ["STAR"]`

### 6.3 아키타입 (`archetypes/<캐릭터>.json`)

```json
{
  "id": "defect.orb_focus", "character": "CHARACTER.DEFECT",
  "names": { "kor": "구체 덱" }, "color": "#3498db",
  "cards": {
    "core":    ["CARD.DEFRAGMENT"],
    "enabler": ["CARD.COOLHEADED"],
    "payoff":  [],
    "support": ["CARD.HOTFIX", "CARD.CAPACITOR"]
  },
  "roleWeight": { "core": 1.0, "enabler": 0.7, "payoff": 0.9, "support": 0.5 },
  "fullWeight": 4.0, "commitThreshold": 0.7,
  "keyRelics": [{ "id": "RELIC.XXX", "w": 1.0 }],
  "needs": [{ "tag": "block", "min": 2 }, { "role": "draw", "min": 2 }],
  "desc": "밀집(Focus) 쌓고 구체로 딜·방어",
  "guideHeroCard": "CARD.DEFRAGMENT"
}
```
- 참고 사이트의 `must`는 `core`, `rec`는 `support`/`enabler`에 대응합니다.
- 위 카드 ID는 형식을 보여주는 예시입니다. 실제 ID는 카탈로그 덤프로 확정합니다.

### 6.4 콤보·유물 시너지

- **`combos/<캐릭터>.json`**: `{ "cards": [id, id], "name": "...", "why": "...", "w": 6 }`
- **`relics.json`** (v2): 유물 티어와 아키타입 가중치

### 6.5 가중치 (`weights.json`)

`tierValue`, 막별 역할 목표치 `targets[캐릭터][막]`, 막별 긴급도, `W_arch`·`W_need`·`W_combo`, 희석 λ, 중복 ρ, 스킵 기준 `T0[막]`, 경로 노드 가치, 프로필(싱글/멀티, 승천 구간)을 담습니다.
→ **로직을 다시 빌드하지 않고 조정할 수 있게** 합니다. 사용자 덮어쓰기 파일(`user_overrides.json`)도 지원합니다.

### 6.6 버전 관리와 업데이트

- **`data/packs/<게임버전>/manifest.json`**: 스키마 버전, 팩 버전, 호환 게임 버전, 파일별 sha256
- **패치 후 변경 감지**: 새 카탈로그 덤프와 이전 덤프를 비교(diff)해 수치·희귀도가 바뀐 카드를 자동으로 `changedIn`에 표시합니다.
- **CI 검증**: 팩의 모든 ID가 카탈로그에 존재하는지 확인합니다.
- **(v2) 자동 업데이트**: 앱이 GitHub Releases의 데이터 팩을 ETag로 확인하고, 서명을 검증한 뒤 원자적으로 교체합니다. 마지막 정상 팩은 보관합니다.

---

## 7. 추천 로직

### 7.1 카드 점수

```
점수(c) = 기본(c) + 빌드(c) + 콤보(c) + 필요(c) + 상황(c) − 희석(c) − 중복(c)
```

| 항목 | 계산 | 초기 가중치 (조정 가능) |
|---|---|---|
| **기본** | `tierValue[actTier(c, 막) ?? tier(c)]` + 강화본 보정 | S80 A65 B50 C35 D20 |
| **빌드** | `W_arch × Σ_a commit(a) × roleWeight(c,a)`. 상한 **+25** (시너지 과대평가 방지). commit ≥ 0.7인데 빌드 밖 카드면 −10 "오프빌드" | `W_arch = 20` |
| **콤보** | 짝이 이미 덱에 있으면 +w, 일부만 있으면 +w/2. 상한 +15 | |
| **필요** | `W_need × Σ_r gap(r) × urgency(r, 막) × strength(c, r)`. `gap(r) = max(0, 목표 − 공급) / 목표`. 공급 = 덱 카드 역할 강도의 합 (강화본 ×1.2) | `W_need = 25` |
| **상황** | 보스 보상: 빌드 카드 가산 · HP 30% 미만: 방어 가중 ↑ · (v2) 3층 안에 다수 적 엘리트가 있으면 광역 긴급도 ↑ | |
| **희석** | `λ × max(0, 덱 − 이상덱크기[막]) × (1 − cycle(c))` | λ = 1.5 |
| **중복** | `ρ × 사본 수 × dupFactor(c)` | ρ = 15 |

- **역할(role)**: `frontload`(초반 딜), `aoe`(광역), `block`(방어), `scaling`(성장), `draw`(드로우/순환), `energy`, `exhaust_fodder`
- **Deck Compass 사용자 불만에서 얻은 규칙 3가지** ([research/DECK_COMPASS.md](research/DECK_COMPASS.md))
  1. **의존 조건 게이트**: `requires`가 덱에 없으면 시너지 점수를 0으로 하고 "엔진 없음 −x" 칩을 붙입니다. 예: 버리기 엔진 없이 교활 카드
  2. **포화 체감**: 같은 자원의 생산이 소비 대비 과다하면(예: 리젠트 별) 추가 생산자의 빌드·필요 점수를 체감시킵니다. 파워 과다도 감점합니다.
  3. **약점 우선**: 방어나 드로우 공급이 목표치의 50% 미만이면 빌드 점수 상한을 +25에서 +12로 낮춥니다. 빌드 쏠림 때문에 방어 부족으로 죽는 문제를 막기 위해서입니다.
- **막별 우선순위** (커뮤니티 "Jobs" 프레임워크): 1막은 딜, 2막 초반은 방어, 2막 보스는 성장, 후반은 드로우

### 7.2 스킵도 후보로

```
스킵(덱) = T0[막] + λ_size × max(0, 덱 − 이상덱크기) + λ_commit × [max commit ≥ 0.7]
```
- **스킵 추천 조건**: 모든 카드가 스킵보다 0.5점 이상 높지 않으면 "스킵 추천"으로 표시합니다.
- **신뢰도 "낮음"**: 신뢰도가 낮은 카드는 스킵을 이기지 못하게 합니다.

### 7.3 상점

- **카드 순위**: `(점수(c) − 스킵) / 가격`
- **카드 제거**도 후보로 넣습니다
  - 가치: `제거가치 × (남은 타격/수비/저주 수)`
  - 비용: 75골드에서 시작해 제거할 때마다 +25 (승천 6 이상은 100, +50) [확인, sts2.gg; 스파이크에서 게임 코드로 재확인]
- **표시**: 추천 구매 조합과 남는 골드
- **v2**: 유물·포션

### 7.4 설명과 신뢰도 UX

- **카드마다 표시**: 총점, 순위, 그리고 **부호가 붙은 상위 3개 구성요소**. 칩 예시: `광역 부족 +24`, `구체 덱 시너지 +10 (구체 40%)`, `덱 비대 −3`, `중복 −15`
- **참고 사이트 칩 재사용**: 필수 · 시너지 · 지금 강함 · 빌드 시동 · 범용 · 덱 오염 · 중복 · 오프빌드
- **신뢰도 표시**
  - 1·2위 차이 ≥ 10: **확신**
  - 차이 < 5: **박빙**. 무엇이 바뀌면 순위가 뒤집히는지 한 줄로 표시합니다.
  - 패치로 바뀐 카드나 신규 카드: **추정**

### 7.5 빌드 감지 · 완성도 · "나올 확률"

- **commit(a)** = `min(1, (Σ 보유 카드 roleWeight + 핵심 유물 w) / fullWeight)`
  - 가장 높은 아키타입이 "현재 방향"입니다. 상위 2개가 비슷하면 하이브리드로 표시합니다.
- **완성도 %**: 참고 사이트 방식(core 100 / support 56 가중)을 그대로 씁니다. 1~5단계 불꽃 아이콘으로 표시합니다.
- **필요한 카드**: 미보유 core/payoff, 충족 안 된 `needs`. 희귀도와 함께 표시합니다.
- **완성 확률 (R5)**
  - **v1**: 남은 카드 보상 기회 수(층 수와 경로 기준) × 해당 희귀도 등장 확률로 "다음 N번 보상 안에 core 카드가 1장 이상 나올 확률"을 근사합니다.
  - **v2**: 실제 카드 풀과 희귀도 가중치로 남은 보상을 몬테카를로 시뮬레이션해 아키타입별 완성 확률을 계산합니다. 희귀도 확률은 스파이크에서 게임 코드로 확인합니다.
    - **구현 (2026-11-08)**: `Core/Forecast/ArchetypeForecaster` — 남은 보상(지도 무작위 경로 또는 보통 막 구성) × SPIKE §5 희귀도(희귀 보정치 포함, 보스 = 희귀) × 엔진 근사 선택. 1500회 약 5~15 ms. 빌드 탭에 "3막 끝까지 / 다음 보스 전" 완성(몰입 ≥ 기준) 확률. 보정 결과는 `RunHistoryAnalyzer calibrate` (실제 남은 일정 기준 Brier skill +0.12, 낮은 확률을 과소평가하는 경향).
- **빌드 모음 탭**: 캐릭터별 아키타입 카드 목록, 보유 여부 체크, 핵심 유물, 설명, 현재 덱 기준 완성도·확률

### 7.6 경로 추천

**알고리즘**: 현재 노드에서 보스까지 **모든 경로를 나열**합니다 (DAG라서 수천 개 이하로 계산이 가볍습니다). 각 경로를 따라 HP·골드·덱 강도를 시뮬레이션하며 점수를 매깁니다.

```
score(path) = Σ 노드가치(node, 상태) − 위험패턴(path) − 사망비용 × P(보스 패배 | 도착 HP, 덱 강도)
```

**노드 가치**

| 노드 | 가치 |
|---|---|
| 몬스터 | 카드 가치 × 덱 필요도 + 골드 − HP 손실 × HP가치(HP%) |
| 엘리트 | 유물 + 좋은 카드 + 골드 − 큰 HP 손실. HP가 60% 미만이고 2칸 안에 휴식이 없으면 감점 |
| 휴식 | max(회복 30%, 강화 가치). 앞 경로가 위험할수록 회복 가치 ↑ |
| 상점 | 골드 < 제거 비용이면 약 0, 아니면 제거 + 최고 구매 가치 |
| 미지(?) | 이벤트·전투·상점 등의 기대값. 분산이 낮아 HP가 낮을 때 가산 |
| 보물 | 유물 가치 |

- **위험 패턴**: 엘리트 연속, 첫 휴식 전 엘리트 (Silver Compass 모드 참고)
- **추천 결과 3개**
  - 🛡 **안전**: 엘리트 최소, 휴식 우선
  - ⚖ **균형**
  - ⚔ **공격**: 엘리트 최대
  - 각 경로 요약 예: `엘리트 2 · 휴식 2 · 상점 1 · 보스 도착 HP 예상 62% · 위험: 엘리트 연속`
- **설정 가능 값**: 엘리트 진입 HP 기준(기본 60~70%), 1막 목표 엘리트 수. 가이드마다 1~2와 2~3으로 의견이 갈립니다.
- **덱 강도(0~1)**: 초기에는 `(1 − 평균 역할 부족도) × 0.6 + max commit × 0.4`로 계산합니다. 이후 런 기록의 HP 손실로 보정합니다.
- **표시**: 패널의 미니맵. v2에서는 모드가 게임 맵 위에 추천 경로를 하이라이트합니다.

### 7.7 런 기록으로 보정 (v2)

- **데이터**: `.run`의 `card_choices`(제시 3장 + 고른 카드) + 승패 → 의사결정 로그
- **보정 방식**
  - (1) **모방 모델**: 고른 카드 vs 안 고른 카드로 약 8개 가중치만 조건부 로짓으로 학습합니다. 카드별 파라미터는 두지 않습니다 (과적합 방지).
  - (2) **결과 기반**: 같은 제시 상황에서 "고름 vs 안 고름"의 승률 차이(pick lift)를 큐레이션 티어 쪽으로 수축(shrinkage)시켜 반영합니다.
- **주의**: "덱에 있었던 카드의 승률"은 편향되므로 쓰지 않습니다 (희귀 카드는 긴 런에 몰림).
- **현실적 한계**: 개인 기록 23~44판은 통계적으로 부족합니다. v2에서는 **개인 통계 화면**(캐릭터별 승률, 자주 고르는 카드)과 가중치 미세조정 정도로만 씁니다.

---

## 8. UI/UX

**게임 안 (모드가 그림)**
- 카드 오른쪽 위에 **티어 배지**: S=금색, A=보라, B=파랑, C=회색, D=어두운 빨강
- 추천 1위 카드에는 **왕관 아이콘**, 스킵 추천이면 스킵 버튼 옆에 표시
- 단축키 `F8`: 배지 표시/숨김

**오버레이 패널 (앱)**
- 단축키 `F7` 열기/닫기, `Ctrl+F7` 클릭 통과 토글
- 탭 구성

| 탭 | 내용 |
|---|---|
| **실시간 추천** | 보상/상점 화면이 열리면 자동으로 이 탭으로 전환. 카드별 점수 막대, 이유 칩 3개, 신뢰도, 스킵 카드 |
| **덱** | 현재 덱 (비용·타입별 그룹, 강화 표시, 획득 층), 역할 공급 vs 목표 막대 (광역·방어·드로우…) |
| **빌드 모음** | 캐릭터별 아키타입 카드, 현재 방향, 완성도 %, 완성 확률, 필요한 카드 |
| **경로** | 미니맵 + 추천 경로 3개와 요약 |
| **기록 / 설정** | 개인 통계, 데이터 팩 버전, 모드 상태, 가중치 프리셋 |

**공통**
- 한국어 우선이며, 영어 이름 병기 옵션
- 초성 검색: 수동 입력과 카드 찾기에 사용

---

## 9. 티어 데이터 출처 전략

| 출처 | 장점 | 단점 |
|---|---|---|
| **자체 큐레이션** (Claude 초안 + 사용자 검수) | 우리 소유, 한국어, 구조(역할·아키타입)를 우리 스키마에 맞춤 | 초기 작성량 큼 (약 450장 + 아키타입 40여 개) |
| 참고 사이트 데이터 | 이미 한국어·아키타입·콤보까지 있음 | **작성자 허락 필요**. 한글 이름을 ID로 매핑하는 작업 필요 |
| 통계 사이트 (Untapped, MetaBot, Spire Codex) | 수백만 런 기반 픽률·승률 | 공개 API 없음, 약관상 스크래핑 불가 가능성, 의존 리스크 (HDT Arena Helper는 출처가 문을 닫자 멈춤) |
| 내 런 기록 | 내 플레이에 맞춤 | 표본 부족 (§7.7) |

**권장**
1. 자체 큐레이션으로 **티어 팩 v0**을 만듭니다. 공개 가이드와 티어 리스트는 *참고만* 하고, 수치는 우리가 매깁니다.
2. 참고 사이트 작성자에게 데이터 사용 허락을 받으면 초기값으로 가져옵니다 (선택).
3. v2에서 내 런 기록으로 보정합니다.

확정되었습니다 ([§13](#13-결정-필요-사항) D2).

---

## 10. 비슷한 기존 도구와 차별점

이미 비슷한 도구가 여럿 있습니다 [확인]. 구현 전에 참고하고, 겹치는 부분은 차별화합니다.

| 도구 | 형태 | 특징 |
|---|---|---|
| Deck Compass (Nexus 627) | 게임 내 모드 | 0~100 적합도, 22개 아키타입, 스킵 점수, 티어 JSON 덮어쓰기. 상세 분석: [research/DECK_COMPASS.md](research/DECK_COMPASS.md) |
| TierLister (Nexus 740) | 게임 내 모드 | SS~D 배지, 카드·유물 150개 |
| sts2-advisor / QuestceSpire | 게임 내 모드 | Godot CanvasLayer 배지, F8 토글, 커뮤니티 승률 |
| StsCompanion / STS Tracker | 모드 + 웹 | 5요소 점수 분해, 세이브 감시 |
| Spire Codex (Overwolf + 모드) | 외부 앱 + 모드 | 세이브 파싱 덱 추적, 경로 추천, 위험도 |
| Untapped.gg Companion | 외부 오버레이 | 픽률·승률, 티어 리스트 가져오기 |
| RouteSuggest / Silver Compass | 게임 내 모드 | 경로 점수, 안전/공격 경로, 위험 패턴 |
| BoberInSpire, STS2MCP | 모드 + 외부 | 상태를 JSON·WebSocket·REST로 내보내는 브리지 구조 (우리 구조와 같음) |

**우리 차별점**
- **한국어 우선 UX**: 초성 검색, 한국어 이유 칩
- **참고 사이트식 빌드 모음과 완성도·확률**
- **카드 추천과 경로 추천의 연동**
- **외부 패널**: 듀얼 모니터와 덱 분석
- **내 기록 기반 개인화**

라이선스가 허용하면 BoberInSpire·STS2MCP의 후킹 지점을 참고합니다. 코드는 직접 작성합니다.

---

## 11. 리스크와 대응

| 리스크 | 영향 | 대응 |
|---|---|---|
| **화면 인식 오류** (개정 후 최대 리스크) | 엉뚱한 카드에 배지 | 카드 그림 해시 + 이름 OCR 교차 확인, 신뢰도 낮으면 배지 대신 "?" 표시, 패널에서 클릭으로 수동 보정, 카드 고른 뒤 세이브로 덱은 항상 정확 |
| **게임 패치로 카드 그림·UI 변경** (11/13 대형 업데이트 예정) | 인식률 하락 | `release_info.json` 버전이 바뀌면 카탈로그·그림 해시 자동 재생성, 화면 템플릿은 해상도 비율 기준 |
| 해상도·UI 배율 다양성 | 카드 영역 찾기 실패 | 1920×1080 기준 비율 좌표 + 템플릿 매칭으로 보정, 사용자 PC 기준 먼저 |
| **모드 사용 시 modded 세이브 분리, 업적 비활성 가능성, 통계 업로드 꺼짐** | 사용자 불편 | 스파이크에서 v0.107.1 기준 실제 동작 확인 후 문서화. 바닐라 모드 유지 |
| 멀티플레이 (사용자 주력 모드) | `affects_gameplay:false`로 호환된다는 보고와 아니라는 보고가 엇갈림. 나만 모드를 켰을 때 친구 방 입장·동기화가 되는지 불확실 | **M0 스파이크에서 최우선으로 테스트**. 막히면 협동 중에는 바닐라 모드(세이브 감시)로 전환 |
| 전체화면 위 오버레이 창 표시 | 패널이 안 보일 수 있음 | 스파이크에서 D3D12/Vulkan 확인, 실패 시 테두리 없는 창 안내와 듀얼 모니터 모드 |
| 티어 데이터 품질과 노후화 | 추천 신뢰 하락 | `changedIn` 자동 표시, "추정" 배지, 가중치 외부화, 사용자 덮어쓰기 |
| 저작권 | 게임 텍스트·이미지 재배포 금지 | 카탈로그는 로컬 생성, 레포에는 ID와 우리 주석만 커밋, 이미지 미포함 |
| 로컬 WebSocket 보안 | 웹페이지가 접근할 수 있음 | 토큰 + Origin 거부 + 127.0.0.1 바인딩 |
| 시너지 과대평가 | 엉뚱한 추천 | 빌드·콤보 점수 상한, 구성요소 공개 |

---

## 12. 마일스톤과 구현 스레드 분할

구현 스레드는 **effort high**로 돌리고, 독립 작업은 **서브에이전트로 병렬 처리**합니다.
게임 실행과 빌드가 필요한 작업(★)은 사용자 PC(Remote Control)에서 진행합니다. 작업 폴더는 `C:\Users\<user>\Desktop\SlayTheSpire2TierOverlay`입니다.

| 단계 | 내용 | 완료 기준 | 병렬 가능 |
|---|---|---|---|
**개정 (2026-11-08, 모드 없음)**

| 단계 | 내용 | 완료 기준 | 병렬 가능 |
|---|---|---|---|
| **M0 스파이크** ★ | ILSpy로 게임 구조 확인(완료) → PCK에서 한/영 카드 이름·카드 그림 추출 → `sts2.dll` 정적 분석으로 카탈로그 → 세이브·로그 신호 조사 → WPF Topmost·클릭 통과 창이 전체화면 위에 보이는지 → 게임 창 캡처 → 보상 화면 스크린샷으로 카드 그림 매칭 정확도 | 결과를 `docs/SPIKE.md`에 기록 | 조사는 서브에이전트 병렬 |
| **M1 기반** | 솔루션 구조(Core, App, Vision, tools), CI(Core 테스트) | 빈 프로젝트가 빌드되고 테스트가 돎 | Core는 클라우드에서 가능 |
| **M2 데이터** | 카탈로그 생성 도구(★), 스키마, 티어 팩 v0 (캐릭터별 병렬), 아키타입·콤보 | 5캐릭터 티어·아키타입 JSON, ID 검증 통과 | ✔ 캐릭터별 병렬 |
| **M3 상태 읽기** ★ | SaveWatcher, LogTailer, 협동 세이브(`current_run_mp.save`)에서 내 플레이어 찾기 | 실제 런에서 덱·맵·HP 자동 갱신 | M4와 병렬 |
| **M4 Core 엔진** | CardScorer·Skip·Shop·Archetype, 실제 `.run` 픽스처로 테스트 | 단위 테스트 통과, 예제 상황 점수 리포트 | ✔ 클라우드 |
| **M5 화면 인식** ★ | 게임 창 캡처, 보상·상점 화면 판별, 카드 그림 pHash 매칭, 강화 판별, 배지 창 | 보상 3장·상점 카드 인식 정확도 95% 이상, 배지 위치 일치 | M4와 병렬 |
| **M6 App 패널** | 패널 창, 탭 4개, 단축키, 수동 보정(인식이 틀리면 클릭으로 고치기) | 실제 런에서 추천 탭 동작 | |
| **M7 경로 추천** | PathPlanner, 미니맵 | 막 시작 시 경로 3안 표시 | ✔ 클라우드 (Core) |
| **M8 마감** | 앱 패키징(Velopack), 첫 실행 때 카탈로그·그림 해시 생성, README | 새 PC에서 설치부터 사용까지 | |
| v2 | 런 기록 보정, 완성 확률 몬테카를로, 유물 티어, 이벤트·강화 화면 인식 | | |

<details><summary>이전 마일스톤 (모드 방식, 폐기)</summary>

| 단계 | 내용 |
|---|---|
| M0 | 최소 모드 로딩, 보상 화면 Postfix, 배지, modded 세이브·업적, 협동 |
| M3 Bridge 모드 | 화면 감지, 스냅샷, WebSocket, 배지, 카탈로그 덤프 |
| M7 바닐라 모드 | 세이브·로그 감시, 수동 입력 |

</details>

---

## 13. 결정 필요 사항

| # | 질문 | 권장안 | 상태 |
|---|---|---|---|
| **D1** | 게임 정보를 어떻게 읽을까? (모드 / 화면 인식 / 둘 다) | ~~공식 모드 방식~~ → **모드 없이 오버레이만: 세이브·로그 감시 + 화면 인식** | ✅ 변경 확정 (2026-11-08, 사용자 결정) |
| **D2** | 티어 데이터 출처 | **자체 큐레이션** (참고 사이트 작성자 허락은 선택) | ✅ 확정 (2026-11-08, 사용자 승인) |
| D3 | 큰 패널 위치: 외부 WPF 창 vs 게임 안 패널 | **외부 WPF 창** (배지는 게임 안) | 기본값으로 진행 |
| D4 | 멀티플레이 지원 | **v1부터 협동에서 "내 덱" 기준 읽기 전용 지원** (런 기록 24판 중 20판이 협동) | 기본값으로 진행 |
| D5 | 배포 범위: 개인용 vs 공개(창작마당) | **개인용 먼저**, 공개는 데이터 라이선스 정리 후 | 기본값으로 진행 |
| D6 | GitHub 레포 운영 | 작업 폴더를 `git init` 후 `namsy8471/SlayTheSpire2TierOverlay`에 푸시 (M1에서) | 기본값으로 진행 |

---

## 14. 레포 구조 (제안)

```
SlayTheSpire2TierOverlay/
├─ docs/
│  ├─ PLAN.md            ← 이 문서
│  ├─ SPIKE.md           (M0 결과)
│  ├─ PROTOCOL.md        (모드↔앱 메시지)
│  └─ DATA_SCHEMA.md
├─ src/
│  ├─ Sts2Overlay.Contracts/   net9.0   프로토콜 DTO
│  ├─ Sts2Overlay.Core/        net9.0   점수·빌드·경로·데이터 팩
│  ├─ Sts2Overlay.Vision/      net10.0-windows  게임 창 캡처, 화면 판별, 카드 그림 매칭
│  └─ Sts2Overlay.App/         net10.0-windows  WPF 오버레이 (패널 + 배지 창, 세이브·로그 감시)
├─ data/
│  ├─ schema/                  JSON 스키마
│  └─ packs/v0.107.1/          tiers/, archetypes/, combos/, weights.json, manifest.json
├─ tests/
│  └─ Sts2Overlay.Core.Tests/  xUnit, 픽스처: 익명화한 .run / current_run.save
├─ tools/
│  ├─ PckTool/                 SlayTheSpire2.pck에서 현지화·카드 그림 추출 (로컬 전용)
│  ├─ CatalogBuilder/          sts2.dll 정적 분석으로 카드 카탈로그 생성 (로컬 전용)
│  └─ RunHistoryAnalyzer/      .run 통계 CLI
├─ Directory.Build.props       Sts2GameDir (게임 파일을 읽기 전용으로 찾는 경로)
└─ SlayTheSpire2TierOverlay.sln
```

- **게임 DLL은 커밋하지 않습니다.** `Directory.Build.props`의 `Sts2GameDir`로 참조합니다.
- **클라우드 CI**는 Core·Contracts만 빌드하고 테스트합니다.

---

## 부록 A. 출처

**사용자 PC에서 직접 확인** (2026-11-08)
- `release_info.json`, `sts2.runtimeconfig.json`, `sts2.deps.json`, `sts2.xml`
- `settings.save`, `prefs.save`
- `history/*.run`
- `logs/godot*.log`

**참고 사이트**
- 카드 뭐 뽑지?: <https://store.hailot.co/apps/5>, <https://elithelucky.github.io/sts2-guide/ko>

**모딩·구조**
- fresh-milkshake Modding-Tutorial: <https://github.com/fresh-milkshake/Modding-Tutorial>
- v0.99 매니페스트 개편: <https://steamdb.info/patchnotes/22321696/>
- v0.107.1 창작마당: <https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2:V0.107.1_-_Major_Update_2>
- 모드 설치 가이드: <https://sts2.gg/guides/how-to-install-mods>

**브리지·오버레이 선례**
- BoberInSpire: <https://github.com/S0ul3r/BoberInSpire>
- STS2MCP: <https://github.com/Gennadiyev/STS2MCP>
- STS2-Agent: <https://github.com/Meddrin/STS2-Agent>
- sts2-advisor: <https://github.com/ebadon16/sts2-advisor>
- spirescope: <https://github.com/thequantumfalcon/spirescope>
- Spire Codex: <https://github.com/ptrlrd/spire-codex>
- Spire Codex Overwolf 앱: <https://overwolf.com/app/ptrlrd-spire_codex>

**게임 내 모드**
- Deck Compass: <https://sts2.gg/mods/mod-627>
- TierLister: <https://sts2.gg/mods/mod-740>
- StsCompanion: <https://sts2.gg/mods/mod-340>
- Silver Compass: <https://sts2.gg/mods/mod-944>
- RouteSuggest: <https://sts2.gg/mods/mod-54>

**통계·추천**
- metabot 카드 어드바이저: <https://metabot.gg/en/slay-the-spire-2/card-advisor>
- Untapped: <https://sts2.untapped.gg/articles/everything-new-for-slay-the-spire-2-on-untappedgg>
- "Jobs" 덱빌딩 프레임워크: <https://sts2.untapped.gg/articles/slay-the-spire-deckbuilding-strategy-solving-the-spire-with-jobs>
- 경로 가이드: <https://sts2.gg/guides/map-routing-guide>

**Godot 전체화면 구현**
- <https://raw.githubusercontent.com/godotengine/godot/4.5/platform/windows/display_server_windows.cpp>

**게임 콘텐츠**
- 카드 목록: <https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2:Cards_List>
- op.gg 한국어 카드: <https://op.gg/ko/slay-the-spire2/cards>
- 패치노트: <https://sts2.gg/patch-notes>

**추천 알고리즘 선행 연구**
- HearthArena: <https://www.heartharena.com/blog/the-big-heartharena-tierlist-overhaul>
- MTGA_Draft_17Lands: <https://github.com/bstaple1/MTGA_Draft_17Lands>
- 드래프트 지표 인과 비판: <https://arxiv.org/pdf/2604.18314>
- StS1 학습 픽 모델 연구: <https://dev.to/doronsinger/part-3-good-models-finish-last-2n6a>

