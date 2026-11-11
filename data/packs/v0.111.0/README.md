# v0.111.0 데이터 팩 (`claude-draft-v1`, v0.107.1 팩에서 갱신)

Slay the Spire 2 **v0.111.0** 기준 데이터 팩입니다. v0.107.1 팩을 복사한 뒤 v0.111 변경만 반영했습니다(§0).
**새 카드 티어는 임시**이고, 나머지 평가는 v0.107.1 팩과 같은 초안·보정 값입니다.
형식은 [`docs/DATA_SCHEMA.md`](../../../docs/DATA_SCHEMA.md), 설계 근거는 PLAN §6·§7·§9를 따릅니다.

| 파일 | 내용 |
|---|---|
| `tiers/ironclad.json` | 90장 (기본 3 + 일반 20 + 고급 38 + 희귀 27 + 고대 2) |
| `tiers/silent.json`, `defect.json`, `necrobinder.json`, `regent.json` | 각 91장 (기본 4 + 일반 20 + 고급 38 + 희귀 27 + 고대 2) |
| `tiers/colorless.json` | 65장 (고급 40 + 희귀 25) |
| `archetypes/*.json` | 아이언클래드 8, 사일런트 3, 디펙트 6, 네크로바인더 3, 리젠트 4 (랭커 실제 빌드 근거, [`docs/research/build-sources/`](../../../docs/research/build-sources/README.md)) |
| `combos/*.json` | 2장 콤보 58개 (캐릭터당 9~13개) |
| `weights.json` | 랭커 통계 v2로 바꾼 `idealDeckSize` 21/27/31, `path.targetElites` 2/2/2와, 카드 점수 보정(2026-11)으로 바꾼 `urgency` 다섯 칸. 나머지는 코드 기본값 (이유는 파일 주석) |
| `events.json` | 이벤트 66개의 선택지 효과(ID·수치만). v0.111.0 클래스로 다시 확인 |
| `events.stats.json` | (선택) 랭커가 이벤트 선택지 115개·고대 선택지 79개를 고른 비율. 이벤트 점수에 섞습니다 ([`docs/research/v2-stats/events.md`](../../../docs/research/v2-stats/events.md)) |
| `relics.json` | (선택) 유물 231개의 상점 구매율·고대 선택률. 안정한 82개(`use: true`)만 유물 값에 곱합니다 |
| `combat/` | 전투 시뮬레이터 데이터: 카드 596, 유물 130 + 무관 169, 포션 65 ([`combat/README.md`](combat/README.md)) |

- 캐릭터 풀과 무색 풀의 **모든 카드**(기본 타격·수비 포함)에 항목이 있습니다. 저주·상태이상·토큰·이벤트·퀘스트 카드는 보상에 나오지 않으므로 넣지 않았습니다.

## 0. v0.111.0 갱신 (2026-11-12)

바뀐 것 전체와 수량, 정하지 못한 것: [`docs/research/v0111/CHANGES.md`](../../../docs/research/v0111/CHANGES.md).
새 카드 티어와 바뀐 카드의 선택률 근거: [`docs/research/v0111/new-card-tiers.md`](../../../docs/research/v0111/new-card-tiers.md).

- **티어**: 사라진 `CARD.SCARE`(사일런트)를 지우고, 캐릭터·무색 풀에 새로 들어온 17장을 넣었습니다(16장이 협동 전용). 선택률 근거가 있는 카드는 `SIDESTEP`뿐이라 나머지는 판단 값이며 신뢰도 0.4~0.45("추정")입니다. v0.111에서 바뀐 카드 25장에 `changedIn: "v0.111.0"`을 달았습니다(규칙은 근거 문서 §3).
- **카드 사실**: 이 버전의 커밋 카탈로그는 `data/catalog/cards.v0.111.0.json`(596장)입니다. 아래 §1의 `cards.v0.107.1.json`은 처음 팩을 만들 때의 설명입니다.
- **전투·이벤트**: `combat/`와 `events.json`을 v0.111.0 디컴파일 클래스로 다시 확인했습니다(변경 목록은 CHANGES.md).

### 0.1 카탈로그 다시 만들기

v0.111.0 게임이 설치되어 있고, 게임 pck의 현지화 파일이 로컬 폴더(아래 `--loc`, 오버레이의 로컬 리소스 폴더)에 풀려 있는 상태에서 저장소 루트에서 실행합니다(PowerShell). 커밋하는 결과는 모두 ID·수치만 담습니다.

```powershell
dotnet build tools/CatalogBuilder -c Release -m:2
dotnet run --project tools/CatalogBuilder -c Release --no-build -- `
  --loc "$env:LOCALAPPDATA\Sts2Overlay\local\pck\localization" `
  --out data\local\catalog.v0.111.0.json `
  --starting-decks data\catalog\starting-decks.v0.111.0.json `
  --relics data\catalog\relics.v0.111.0.json `
  --card-vars data\catalog\card-vars.v0.111.0.json `
  --relic-vars data\catalog\relic-vars.v0.111.0.json
# data/local 의 카탈로그에는 게임 텍스트가 있으므로 이름을 뺀 사본만 커밋합니다
python -I tools/CatalogBuilder/strip_cards.py data/local/catalog.v0.111.0.json data/catalog/cards.v0.111.0.json
# 카드 풀 순서와 에포크: v0.111.0 sts2.dll 디컴파일 폴더(커밋하지 않음)에서 읽습니다
python -I tools/PoolDataGen/gen_card_pools.py <v0.111.0 디컴파일 폴더> data/catalog/cards.v0.111.0.json data/catalog/card-pools.v0.111.0.json
```

- CatalogBuilder 종료 코드 0, 템플릿 변수 커버리지 카드 1585/1585·유물 544/544(종료 코드 2면 처리하지 못한 변수가 있다는 뜻).
- 결과 수: 카드 596, 카드 변수 596, 유물 299, 유물 변수 299, 시작 덱 5, 카드 풀 5(디펙트 91, 아이언클래드 90, 네크로바인더 91, 리젠트 91, 사일런트 91; 에포크는 캐릭터당 3개 × 3장 그대로).
- 미해결: `CARD.MAD_SCIENCE`·`CARD.SOVEREIGN_BLADE`의 `gainsBlock`(v0.107.1과 같음). 로컬 카탈로그에만 있는 `starCostX`는 커밋본에 넣지 않습니다(v0.107.1 형식 유지).
- `aliases.json`: `CARD.SCARE` → `CARD.SIDESTEP` 하나뿐입니다. 게임이 v0.111에서 Scare를 Sidestep으로 고치고 이름을 바꾸며 옛 세이브·런 기록의 id도 옮깁니다(SerializableRunV19ToV20, RunHistoryV9ToV10). 효과가 완전히 달라(약화+소멸 → 다음 턴 에너지) SCARE의 학습 데이터는 쓰지 않고, 옛 id가 SIDESTEP 자신의 티어·학습 항목을 읽게만 합니다. `CARD.FOLLOW_THROUGH`는 여전히 별칭이 없습니다.
  (English: the only alias is SCARE → SIDESTEP, matching the game's own save/history migration; SCARE's learned entry is not reused because the card was reworked.)

> **카드 점수 보정 (2026-11)**: 상위 랭커 2,155판의 보상 화면 선택으로 티어 14장을 한 단계씩 옮기고, 카드 382장에 `baseAdjust`(−15~+15점, 배지 티어는 그대로)를 붙였습니다. 방법·근거·카드별 표는 [`docs/research/CARD_CALIBRATION.md`](../../../docs/research/CARD_CALIBRATION.md)에 있습니다.

## 1. 어떻게 만들었나

1. **카드 사실**: 커밋된 카탈로그 `data/catalog/cards.v0.107.1.json`(ID·풀·타입·희귀도·비용·키워드)과, 로컬 전용 `data/local/`의 카드 설명(영/한)을 읽고 카드가 하는 일을 파악했습니다.
   - 카탈로그에는 피해·방어 **수치가 없습니다**(정적 IL 분석 한계). 그래서 수치는 카드 설명과 1편의 비슷한 카드로 짐작했습니다. 이것이 신뢰도를 낮게 둔 가장 큰 이유입니다.
   - 카드 이름·설명은 **한 글자도 옮기지 않았습니다**. 팁·설명·콤보 이유는 우리 말로 쓴 짧은 요약이며, 다른 카드 이름도 넣지 않았습니다(예: 리젠트의 검 토큰은 "검 토큰"으로만 부름).
   - `type`·`rarity`·`cost`는 카탈로그가 없을 때(예: 리플레이 도구가 팩만 읽을 때) 쓰는 대체 값으로, 커밋된 카탈로그에서 복사했습니다. 카탈로그가 있으면 카탈로그 값이 우선합니다.
2. **티어**: Claude가 카드 텍스트와 일반적인 덱빌딩 원칙(초반 딜·방어·성장·드로우의 균형, PLAN §7.1 "Jobs")으로 매긴 뒤, 커뮤니티 자료를 **참고만** 했습니다. 참고 자료와 일치한 항목은 `source`에 `ref brokenbuilds(2026-04)`를 덧붙였습니다.
3. **아키타입**: 빌드 목록과 빌드별 카드(core·enabler·payoff·support)는 **상위 랭커가 실제로 이긴 덱**에서 정했습니다. 판정 규칙과 카드마다의 근거는 [`docs/research/build-sources/README.md`](../../../docs/research/build-sources/README.md)와 캐릭터별 문서에 있습니다.
   - 랭커가 거의 하지 않는 빌드(사일런트 드로우, 디펙트 파워, 네크로바인더 영혼)는 지웠고, 기준을 넘는 새 빌드(아이언클래드 공격 반복·타격·헬레이저, 디펙트 어둠 구체·구체 다양성, 리젠트 별 소모)를 더했습니다.
   - core 근거가 되는 카드가 없으면 core를 비워 두었습니다. 그 빌드는 "코어 먼저"가 꺼지고, 검증기는 이것을 경고로 알립니다.
   - 시작 덱 카드와 시작 유물은 모든 런에 있어 commit을 부풀리므로 아키타입에 넣지 않았습니다.
   - **콤보**는 각 캐릭터의 고유 메커니즘에서 도출했습니다(아키타입 교체 전과 같음).
4. **검증과 보정**: 테스트로 형식을 검증하고, 사용자의 실제 런 26개를 리플레이해 명백히 어긋난 10장을 조정했습니다(§4). 조정이 튜닝에 쓴 런에만 맞춘 것인지는 튜닝에 쓰지 않은 런으로 따로 검증했습니다(§4.1).

## 2. 필드 사용 규칙

### 2.1 티어와 역할

- 티어는 S/A/B/C/D만 씁니다(스키마에 F 없음). **S는 2장뿐입니다**: 아이언클래드 `OFFERING`, 리젠트 `BIG_BANG`. 랭커 선택률이 80% 이상이고 재표본에서도 안정한 카드만 S로 올렸습니다(v2, [`docs/research/v2-stats/tiers.md`](../../../docs/research/v2-stats/tiers.md)).
- A는 아이언클래드 10, 사일런트 8, 디펙트 6, 네크로바인더 3, 리젠트 3장입니다. v2는 각 파일의 기존 A/B/C/D 비율을 유지한 채 선택률 순위로 다시 잘랐기 때문에, 네크로바인더·리젠트처럼 A가 적은 캐릭터도 그대로 둡니다.
- 기본 타격·수비는 D입니다. 제거 가치(상점)는 엔진의 `shop.basicRemovalValue`가 따로 계산합니다.
- `roles` 기준점: 기본 타격 `frontload 0.3`, 기본 수비 `block 0.35`, 좋은 일반 공격 0.5, 큰 단일 피해 0.8, 핵심 성장 파워 `scaling 0.8~1.0`.
- `actTier`: 1막에 특히 좋은 카드(즉시 딜·광역)와, 일찍 얻을수록 좋은 카드(최대 체력·영구 성장)에만 적었습니다.
- `dupFactor`: 겹치면 효과가 없는 파워는 2~2.5, 여러 장 모을수록 강한 카드(클로·교수형 계열)는 0.2~0.3, 기본 파워는 1.3, 나머지 1.0.
- `upgradedDelta`: 기본 3, B 이하 4, A 6, 강화 효과가 큰 카드는 따로(예: 손패 전체 강화, 0코스트화) 6~8.
- `upgradeValue`(2026-11): 휴식처 강화 순위에 쓰는 카드별 강화 가치(1.4~7.9점). 랭커 강화 선택 8,140번으로 맞춘 값이며, 있으면 강화 조언이 `upgradedDelta` 대신 씁니다(카드 픽 점수는 읽지 않음). 빌드 역할 가중치는 `weights.json`의 `upgrade`. 근거와 다시 맞추는 법: [`docs/research/UPGRADE_CALIBRATION.md`](../../../docs/research/UPGRADE_CALIBRATION.md).

### 2.2 시너지 축 태그 (`provides` / `consumes` / `requires`)

`requires`는 "덱에 이 축이 하나도 없으면 −15(엔진 없음)"이므로, **생산자 없이는 거의 쓸모없는 페이오프**에만 붙였습니다.

| 캐릭터 | 축 |
|---|---|
| 아이언클래드 | `STRENGTH` `EXHAUST` `BLOCK`(방어 누적 카드) `SELF_DAMAGE` `VULNERABLE` `STRIKE` |
| 사일런트 | `POISON` `SHIV` `DISCARD`(버리기 엔진, 교활 카드는 이것을 `requires`) `DRAW` `WEAK` `VULNERABLE` |
| 디펙트 | `ORB` `ORB_LIGHTNING` `ORB_FROST` `ORB_DARK` `ORB_GLASS` `ORB_PLASMA` `FOCUS` `ORB_SLOT` `POWER`(파워 카드에 자동 부여) `ZERO_COST` `STATUS` `WEAK` `VULNERABLE` |
| 네크로바인더 | `SUMMON` `OSTY_ATTACK` `DOOM` `SOUL` `ETHEREAL` `WEAK` `VULNERABLE` |
| 리젠트 | `STAR`(별 생산; 별 비용 카드는 `consumes`) `FORGE` `CREATE` `WEAK` `VULNERABLE` |

- 포화 규칙(PLAN §7.1 규칙 2)이 `provides` 대 `consumes` 비율을 보므로, 예를 들어 리젠트 별 생산 카드만 쌓이고 별 소비 카드가 없으면 추가 생산자의 가치가 줄어듭니다.
- 시작 카드도 축을 가집니다(디펙트 시작 번개 카드 → `ORB_LIGHTNING`, 네크로바인더 시작 소환 카드 → `SUMMON`). 그래서 "구체가 있어야 하는 카드"의 `requires: ["ORB"]`는 디펙트에서는 사실상 항상 충족됩니다.

### 2.3 신뢰도 (`confidence`)

| 값 | 뜻 |
|---|---|
| 0.7 / 0.65 / 0.6 | v2 데이터로 정한 카드(`source`가 `opgg-top-a10-2026-11`): 보상 화면 제시 150회 이상 / 60회 이상 / 30회 이상 |
| 0.6~0.7 | (초안) 기본 카드, 또는 카드 효과가 단순하고 커뮤니티 평가와 우리 판단이 일치 |
| 0.5~0.55 | 보통의 초안 값 |
| 0.4~0.45 | 추정. 효과가 복잡하거나, 협동 전용이거나, 근거가 서로 엇갈림 |

v2에서 표본이 부족하거나 재표본에서 등급이 흔들린 카드("불확실")와 무색·기본·고대 카드는 초안 값(`claude-draft-v1`)을 그대로 둡니다. 모든 값은 0.3~0.7 안입니다.

**주의**: 엔진은 `confidence < skip.lowConfidence(0.5)`인 카드를 스킵보다 위로 추천하지 않고 "추정" 배지를 붙입니다. 그래서 0.45 이하는 "이 카드는 우리가 아직 잘 모른다"는 뜻으로만 썼습니다(복잡한 희귀 카드, 대부분의 협동 전용 카드). 검수하면서 확신이 생기면 0.5 이상으로 올리세요.

## 3. 출처

- 게임 데이터: 사용자 PC의 v0.107.1 설치본에서 생성한 카탈로그(커밋본은 이름·설명 없음)와 로컬 현지화 파일. 로컬 파일 내용은 커밋하지 않았습니다.
- 커뮤니티 참고(문구 미사용, 수치만 참고):
  - BrokenBuilds 캐릭터별 카드 티어 리스트 (Major Update 2, 2026-04-19): <https://brokenbuilds.gg/slay-the-spire-2/tier-lists>
    - 1편 카드 이름이 섞여 있는 등 품질이 고르지 않아 **약한 참고**로만 썼습니다.
  - STS2.GG 리젠트 가이드 (v0.111 베타 기준, 2026-09): <https://sts2.gg/guides/regent-character-guide> — 별 덱과 벼리기 덱을 섞지 말라는 조언
  - 네크로바인더 빌드 가이드(오스티·파멸·영혼 3축): KeenGamer <https://www.keengamer.com/articles/guides/best-necrobinder-build-in-slay-the-spire-2/>, PCGamesN <https://www.pcgamesn.com/slay-the-spire-2/necrobinder>, FinalBoss <https://finalboss.io/slay-the-spire-2-necrobinder-character-guide-strategy-builds-and-tier-list>
  - 리젠트 빌드 가이드: KeenGamer <https://www.keengamer.com/articles/guides/slay-the-spire-2-best-regent-builds>
  - 캐릭터 순위는 출처마다 크게 엇갈립니다(어떤 곳은 사일런트, 어떤 곳은 리젠트나 네크로바인더가 1위). 그래서 캐릭터 간 티어 분포를 맞추려 하지 않았습니다.
- 아키타입 근거 (2026-11, 자세한 내용은 [`docs/research/build-sources/README.md`](../../../docs/research/build-sources/README.md)):
  - **주 근거**: OP.GG Slay the Spire 2 랭킹의 실제 승리 런 (2026-11-02 ~ 11-08 주간 순위와 순위권 플레이어의 최근 승리; 승천 10·바닐라·싱글 플레이). 런은 `history/<runId>` 주소로만 인용합니다.
  - **보조 근거**: Baalorlord의 캐릭터별 승천 10 연승 덱과 본인이 쓴 공략.
  - **참고만**: Spire Codex A10 카드 통계. 카드를 넣고 빼는 유일한 이유로는 쓰지 않았습니다.
- 축 모델 아이디어: Deck Compass 분석([`docs/research/DECK_COMPASS.md`](../../../docs/research/DECK_COMPASS.md)). 데이터는 가져오지 않았습니다.

## 4. 런 기록 리플레이 결과 (2026-11-08)

```
dotnet run --project tools/RunHistoryAnalyzer -- replay "%APPDATA%\SlayTheSpire2\steam\<SteamID>\profile1\saves\history" --pack data/packs/v0.107.1 --out data/local/replay-v0.107.1.md
```

사용자 런 26개(디펙트 17, 아이언클래드 5, 네크로바인더 2, 리젠트 1, 사일런트 1; 협동 20; v0.98.3 11개 + v0.107.1 15개), 비교 가능한 선택 339회.

| 팩 | 추천과 일치 | 카드를 고른 280회 중 엔진 1위 = 사용자 선택 |
|---|---|---|
| `sample` (자리표시자) | 61회 (18%) — 대부분 "둘 다 스킵" | 의미 없음 (미평가 카드는 모두 동점) |
| 이 팩, 보정 전 | 112회 (33%) | 42% (무작위 33%) |
| 이 팩, 보정 후 | **132회 (38.9%)** | **51%**, 2위 안 81% |

보정한 10장(사용자 선택과 크게 어긋났고, 다시 봐도 초안이 틀렸다고 판단한 것만):
충전 계열 방어(디펙트 `CHARGE_BATTERY`)·에너지 두 배(`DOUBLE_ENERGY`)·`SUPERCRITICAL`·협동 전용 `ENERGY_SURGE`/`IGNITION` 상향,
`COMPILE_DRIVER`·`SWEEPING_BEAM`·`STORM`·`PERFECTED_STRIKE`·`TRUE_GRIT` 하향.

**해석할 때 주의**
- 같은 26판으로 보정하고 같은 26판으로 쟀으므로 **표본 내 수치**입니다. 실제 일반화 성능은 더 낮을 수 있습니다.
- 사용자의 선택이 정답은 아닙니다(디펙트 승률 12%). 일치율을 100%로 만드는 것이 목표가 아닙니다.
- 11판은 v0.98.3 시절 기록이라 카드 성능이 지금과 다를 수 있습니다.
- "스킵이 나았음" 87회는 주로 스킵 기준(`skip.t0`)의 문제라서 티어가 아니라 가중치 쪽에서 볼 일입니다.
- 위 표의 51%는 아래 §4.1의 공정한 동점 처리로 다시 재면 49.5%입니다.
- 보정한 10장은 `source`에 `replay-tuned(26 runs, LOGO-stable)`가 붙어 있습니다. 런 기록을 보고 바꾼 항목은 이렇게 표시해 두어야 다음 검증에서 어떤 런이 "이미 본 런"인지 알 수 있습니다.
- **v2(2026-11) 이후**: 그중 6장(디펙트 `CHARGE_BATTERY`·`DOUBLE_ENERGY`·`STORM`·`SUPERCRITICAL`·`SWEEPING_BEAM`, 아이언클래드 `PERFECTED_STRIKE`)은 이제 랭커 선택률로 값을 정해서 `replay-tuned` 표시가 없습니다. 나머지 4장(`ENERGY_SURGE`·`IGNITION`·`COMPILE_DRIVER`·`TRUE_GRIT`)은 v2에서 "불확실"이거나 표본에 없어 보정 값과 표시를 그대로 둡니다.

### 4.1 홀드아웃 검증 (2026-11-08)

```
dotnet run --project tools/RunHistoryAnalyzer -- validate <내 history> <상대 history> --pack data/packs/v0.107.1 --player <내 SteamID> --tuned-until 1794134406 --all-players --out data/local/holdout/validate-full.md
```

> v2 주의: 아래 표는 v2 이전 팩으로 잰 값입니다. 위 6장은 이제 사용자 런이 아니라 OP.GG 랭커 데이터로 정했으므로, `--tuned-until` 홀드아웃 구분이 이 6장에는 더 이상 해당하지 않습니다.

**일치율은 대리 지표입니다.** 사용자의 선택은 정답이 아니므로, 같은 결정 위에서 추천기끼리 비교할 때만 의미가 있습니다.
1위 일치 = 카드를 고른 결정 중 고른 카드가 엔진 1위인 비율. **동점은 동전 던지기로 셉니다**: `.run` 파일은 고른 카드를 항상 첫 번째로 적기 때문에, 제시 순서로 동점을 깨면 고른 카드가 공짜로 1위가 됩니다.

런 49개(스냅숏 기준) = 튜닝에 쓴 내 런 26 + 튜닝에 쓰지 않은 런: 내 새 런 2 (1794136097, 1794138857) + 협동 상대 계정의 결정 30런(협동 20 + 단독 10). 홀드아웃에는 디펙트 결정이 없습니다.

| 평가 집합 | 결정 | 엔진 1위 | 2위 안 | 평균 순위 | 스킵 일치 | 완전 일치 |
|---|---|---|---|---|---|---|
| 튜닝 집합 (표본 내) | 339 | 49.5% | 79.3% | 1.71 | 65.5% | 38.9% |
| 홀드아웃: 내 새 런 2개 | 45 | 57.3% | 73.5% | 1.69 | 48.9% | 33.3% |
| 홀드아웃: 협동 상대 30런 | 323 | 42.7% | 77.2% | 1.80 | 68.1% | 37.3% |
| **홀드아웃 전체** | 368 | **44.8%** | 76.7% | 1.79 | 65.8% | 36.8% |
| 기준선: 티어만 (홀드아웃 전체) | 368 | 45.7% | 77.4% | 1.77 | 70.7% | 40.8% |
| 기준선: 무작위 (홀드아웃 전체) | 368 | 33.3% | 66.7% | 2.00 | 67.0% | 25.1% |

교차 검증: 보정 전 값을 근사 재구성한 팩에서 출발해, 같은 종류의 자동 보정(카드 티어 한 단계 이동, `TierShiftCalibrator`)을 **각 fold의 학습 런만 보고** 맞췄습니다.

| 분할 (내 런 28개만) | 보정 전 시험 | 자동 보정 시험 | 자동 보정 학습(표본 내) |
|---|---|---|---|
| 게임 하나씩 빼기 (28 fold) | 40.5% | 51.7% | - |
| 5-fold | 40.5% | 50.4% | 55.8% |
| 시간순 (학습 20 / 시험 최신 8) | 43.4% | 47.7% | 54.9% |
| 튜닝 26판 → 튜닝 때 없던 게임 12판 (상대 포함) | 53.1% | 53.1% | 54.8% |

- 표본 내 이득 +15점 중 같은 사람의 다른 판에서 남는 것은 +10~11점(시간순 +4점), 다른 사람·다른 캐릭터 판에서는 0점입니다.
- 손 보정 10장은 자동 보정이 fold의 92~100%에서 같은 방향으로 다시 찾아냈고(한 판에만 기대지 않음), 홀드아웃 점수를 낮추지 않아 **모두 유지**했습니다.
  되돌리기 후보 4장을 되돌려 보면 튜닝 집합만 나빠지고(1위 −2회) 홀드아웃은 그대로였습니다.
- 튜닝 집합으로 더 맞춘 자동 이동 15개는 표본 내 49.5→54.0%였지만 튜닝 때 없던 게임에서 54.1→54.1%라 **적용하지 않았습니다**.
- 아키타입 데이터를 빼면 홀드아웃 1위 일치가 44.8→41.1%로 떨어집니다. 아키타입은 튜닝에 안 쓴 판에서도 도움이 됩니다.
- 엔진 전체가 티어만 기준선보다 홀드아웃에서 낫지 않습니다. 티어가 아니라 가중치(필요 역할·희석·스킵 기준)에서 볼 문제입니다.

## 5. 개선하는 방법

1. **검수**: 캐릭터별로 A·D 카드부터 읽고 동의하지 않는 티어를 고칩니다. 고친 항목은 `source`를 `curated`(또는 `claude-draft-v1; reviewed`)로 바꾸고 `confidence`를 올립니다.
2. **리플레이 루프**: 고친 뒤 위 명령으로 다시 리플레이해 일치율과 "엔진 1위 = 내 선택" 비율을 비교합니다. 특정 카드가 계속 "다른 카드 선택"에 나오면 그 카드부터 의심합니다. 단, 런 수가 적으므로 한두 번 어긋난 카드는 바꾸지 않습니다.
   - 런 기록을 보고 바꾼 항목은 `source`에 `replay-tuned`를 붙이고, `validate --tuned-until <마지막으로 본 런의 시작 시각>`으로 **그 뒤의 런에서** 점수를 확인합니다. 튜닝 집합 점수만 오르는 변경은 남기지 않습니다.
3. **패치 대응**: 새 버전 카탈로그가 나오면 이 폴더를 복사해 `data/packs/<새 버전>/`을 만들고, 카탈로그 diff로 바뀐 카드에 `changedIn`을 적습니다(→ "추정" 배지).
4. **편집 형식**: 카드 한 장이 한 줄이라 diff를 읽기 쉽습니다. 직접 수정해도 되고, `tests/Sts2Overlay.Core.Tests/Data/RealPackTests.cs`가 형식·커버리지·ID 존재를 검사합니다.
