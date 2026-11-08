# v0.107.1 데이터 팩 (M2 초안, `claude-draft-v1`)

Slay the Spire 2 **v0.107.1** 기준 첫 실제 데이터 팩입니다. **초안**이므로 모든 평가는 검수 전 값입니다.
형식은 [`docs/DATA_SCHEMA.md`](../../../docs/DATA_SCHEMA.md), 설계 근거는 PLAN §6·§7·§9를 따릅니다.

| 파일 | 내용 |
|---|---|
| `tiers/ironclad.json` | 87장 (기본 3 + 일반 20 + 고급 36 + 희귀 26 + 고대 2) |
| `tiers/silent.json`, `defect.json`, `necrobinder.json`, `regent.json` | 각 88장 (기본 4 + 82 + 고대 2) |
| `tiers/colorless.json` | 64장 (고급 39 + 희귀 25) |
| `archetypes/*.json` | 아이언클래드 5, 사일런트 4, 디펙트 5, 네크로바인더 4, 리젠트 3 |
| `combos/*.json` | 2장 콤보 58개 (캐릭터당 9~13개) |
| `weights.json` | 비어 있음 → 코드 기본값 사용 (이유는 파일 주석) |

- 캐릭터 풀과 무색 풀의 **모든 카드**(기본 타격·수비 포함)에 항목이 있습니다. 저주·상태이상·토큰·이벤트 카드는 보상에 나오지 않으므로 넣지 않았습니다.
- `aliases.json`은 아직 없습니다. 사용자 런 기록 중 `CARD.FOLLOW_THROUGH`(v0.98.3 시절 사일런트 카드)는 v0.107.1 카탈로그에 없지만, 어느 카드로 바뀌었는지 확인하지 못해 별칭을 만들지 않았습니다(틀린 별칭이 더 해롭기 때문).

## 1. 어떻게 만들었나

1. **카드 사실**: 커밋된 카탈로그 `data/catalog/cards.v0.107.1.json`(ID·풀·타입·희귀도·비용·키워드)과, 로컬 전용 `data/local/`의 카드 설명(영/한)을 읽고 카드가 하는 일을 파악했습니다.
   - 카탈로그에는 피해·방어 **수치가 없습니다**(정적 IL 분석 한계). 그래서 수치는 카드 설명과 1편의 비슷한 카드로 짐작했습니다. 이것이 신뢰도를 낮게 둔 가장 큰 이유입니다.
   - 카드 이름·설명은 **한 글자도 옮기지 않았습니다**. 팁·설명·콤보 이유는 우리 말로 쓴 짧은 요약이며, 다른 카드 이름도 넣지 않았습니다(예: 리젠트의 검 토큰은 "검 토큰"으로만 부름).
   - `type`·`rarity`·`cost`는 카탈로그가 없을 때(예: 리플레이 도구가 팩만 읽을 때) 쓰는 대체 값으로, 커밋된 카탈로그에서 복사했습니다. 카탈로그가 있으면 카탈로그 값이 우선합니다.
2. **티어**: Claude가 카드 텍스트와 일반적인 덱빌딩 원칙(초반 딜·방어·성장·드로우의 균형, PLAN §7.1 "Jobs")으로 매긴 뒤, 커뮤니티 자료를 **참고만** 했습니다. 참고 자료와 일치한 항목은 `source`에 `ref brokenbuilds(2026-04)`를 덧붙였습니다.
3. **아키타입·콤보**: 각 캐릭터의 고유 메커니즘(힘·소멸, 독·단도·교활, 구체·밀집·파워·0코스트·상태이상, 소환·파멸·영혼·영체, 별·벼리기·카드 생성)에서 도출했습니다. 시작 덱 카드와 시작 유물은 모든 런에 있어 commit을 부풀리므로 아키타입에 넣지 않았습니다.
4. **검증과 보정**: 테스트로 형식을 검증하고, 사용자의 실제 런 26개를 리플레이해 명백히 어긋난 10장을 조정했습니다(§4).

## 2. 필드 사용 규칙

### 2.1 티어와 역할

- 티어는 S/A/B/C/D만 씁니다(스키마에 F 없음). **S는 일부러 비웠습니다.** 초안 단계에서 "무조건 집는 카드"를 단정할 근거가 부족해서입니다. A는 캐릭터당 5~12장입니다.
- 기본 타격·수비는 D입니다. 제거 가치(상점)는 엔진의 `shop.basicRemovalValue`가 따로 계산합니다.
- `roles` 기준점: 기본 타격 `frontload 0.3`, 기본 수비 `block 0.35`, 좋은 일반 공격 0.5, 큰 단일 피해 0.8, 핵심 성장 파워 `scaling 0.8~1.0`.
- `actTier`: 1막에 특히 좋은 카드(즉시 딜·광역)와, 일찍 얻을수록 좋은 카드(최대 체력·영구 성장)에만 적었습니다.
- `dupFactor`: 겹치면 효과가 없는 파워는 2~2.5, 여러 장 모을수록 강한 카드(클로·교수형 계열)는 0.2~0.3, 기본 파워는 1.3, 나머지 1.0.
- `upgradedDelta`: 기본 3, B 이하 4, A 6, 강화 효과가 큰 카드는 따로(예: 손패 전체 강화, 0코스트화) 6~8.

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
| 0.6~0.7 | 기본 카드, 또는 카드 효과가 단순하고 커뮤니티 평가와 우리 판단이 일치 |
| 0.5~0.55 | 보통의 초안 값 (대부분) |
| 0.4~0.45 | 추정. 효과가 복잡하거나, 협동 전용이거나, 근거가 서로 엇갈림 |

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

## 5. 개선하는 방법

1. **검수**: 캐릭터별로 A·D 카드부터 읽고 동의하지 않는 티어를 고칩니다. 고친 항목은 `source`를 `curated`(또는 `claude-draft-v1; reviewed`)로 바꾸고 `confidence`를 올립니다.
2. **리플레이 루프**: 고친 뒤 위 명령으로 다시 리플레이해 일치율과 "엔진 1위 = 내 선택" 비율을 비교합니다. 특정 카드가 계속 "다른 카드 선택"에 나오면 그 카드부터 의심합니다. 단, 런 수가 적으므로 한두 번 어긋난 카드는 바꾸지 않습니다.
3. **패치 대응**: 새 버전 카탈로그가 나오면 이 폴더를 복사해 `data/packs/<새 버전>/`을 만들고, 카탈로그 diff로 바뀐 카드에 `changedIn`을 적습니다(→ "추정" 배지).
4. **편집 형식**: 카드 한 장이 한 줄이라 diff를 읽기 쉽습니다. 직접 수정해도 되고, `tests/Sts2Overlay.Core.Tests/Data/RealPackTests.cs`가 형식·커버리지·ID 존재를 검사합니다.
