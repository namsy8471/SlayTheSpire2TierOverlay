# 데이터 팩 스키마

추천 엔진(`Sts2Overlay.Core`)이 읽는 큐레이션 데이터의 파일 형식입니다. PLAN §6(데이터 설계)과 §7(추천 로직)을 구현 기준으로 정리했습니다.

- 로더: `Sts2Overlay.Core.Data.DataPackLoader`
- 검증기: `Sts2Overlay.Core.Data.DataPackValidator`
- 형식 샘플: [`data/packs/sample/`](../data/packs/sample/) (값은 모두 자리표시자)

> 카드 이름과 설명은 Mega Crit 저작물이므로 **레포의 데이터에는 ID만** 적습니다. 이름은 사용자 PC에서 생성하는 카탈로그(`catalog.json`)에서만 가져옵니다.

## 1. 폴더 구조

```
data/packs/<팩 이름 또는 게임 버전>/
├─ weights.json          (선택) 가중치 부분 덮어쓰기
├─ tiers/*.json          캐릭터별 티어 팩, 무색은 colorless.json
├─ archetypes/*.json     아키타입 목록
├─ combos/*.json         콤보 목록
├─ aliases.json          (선택) 옛 ID → 현재 ID
└─ catalog.json          (선택) 카드 카탈로그. 보통 로컬에서 생성하며 커밋하지 않음
```

- 모든 부분이 선택입니다. 없는 파일은 기본값(`weights.json`) 또는 빈 목록이 됩니다.
- 하위 폴더에서는 `*.json` 파일만 읽고, 파일 이름의 사전순으로 읽습니다. 파일 이름 자체에는 의미가 없습니다(캐릭터는 파일 내용으로 정합니다).
- `LoadDirectory(dir, catalog)`에 카탈로그를 직접 넘기면 폴더의 `catalog.json`보다 우선합니다.
- 계획만 있고 아직 로더가 읽지 않는 파일: `manifest.json`(PLAN §6.6), `relics.json`(v2), `user_overrides.json`, `data/schema/*.schema.json`.

## 2. 공통 규칙

### 2.1 JSON 문법

| 규칙 | 내용 |
|---|---|
| 속성 이름 | camelCase (`dupFactor`, `fullWeight`). 읽을 때는 대소문자를 가리지 않음 |
| 사전 키 | 적은 그대로 사용 (카드 ID, 역할 이름 등은 대소문자 구분) |
| 주석 | `//`, `/* */` 허용 |
| 끝 쉼표 | 허용 |
| 티어 | 문자열 `"S"`, `"A"`, `"B"`, `"C"`, `"D"` (소문자도 허용, 숫자는 거부) |
| 막(act) 키 | JSON 객체 키 `"1"`, `"2"`, `"3"` → 정수 막 번호 |

### 2.2 ID 형식

| 종류 | 정규식 | 예 |
|---|---|---|
| 카드 | `^CARD\.[A-Z0-9_]+$` | `CARD.BALL_LIGHTNING` |
| 유물 | `^RELIC\.[A-Z0-9_]+$` | `RELIC.CRACKED_CORE` |
| 캐릭터 | `^CHARACTER\.[A-Z0-9_]+$` 또는 `COLORLESS` | `CHARACTER.DEFECT` |

ID는 게임 모델 ID 그대로입니다. 게임이 ID를 바꾼 경우(예: `RunHistoryV8ToV9` 마이그레이션)는 `aliases.json`으로 옛 ID를 현재 ID에 연결합니다. 엔진은 카드·유물 ID를 비교하기 전에 항상 `DataPack.Canonical`로 별칭을 풉니다.

### 2.3 오류 처리

`LoadDirectory`는 파일이 깨졌으면 `InvalidDataException`을 던지고, 메시지는 **파일 경로로 시작**합니다(System.Text.Json의 줄 번호와 JSON 경로 포함). 다음 경우도 같은 예외입니다.

- 티어 팩에 `character`가 없음
- 두 티어 파일이 같은 `character`를 선언함
- `archetypes/*.json`이나 `combos/*.json`이 배열도 아니고 목록을 담은 객체도 아님
- 파일 내용이 `null`

## 3. `weights.json`

점수 모델의 모든 조정 가능한 숫자입니다(PLAN §6.5). **기본값과 다른 값만 적으면 됩니다.**

병합 규칙:

| JSON 값 종류 | 동작 | 예 |
|---|---|---|
| 숫자·문자열 | 적은 값으로 교체 | `"wArch": 30` |
| 중첩 객체 (`skip`, `shop`, `completion` 등) | **병합**: 적은 속성만 바뀌고 나머지는 기본값 유지 | `"skip": { "margin": 1 }` → `t0`, `lambdaSize` 유지 |
| 사전 (`tierValue`, `targets`, `urgency`, `idealDeckSize`, `skip.t0`, `completion.rarityOdds`, `completion.poolSize`) | **키 단위 병합**: 적은 키의 값만 교체(값이 사전이면 그 값 전체를 교체), 나머지 키 유지 | `"tierValue": { "S": 90 }` → A~D 유지 |
| 배열 (`weakRoles`) | 통째로 교체 | `"weakRoles": ["block"]` |

예: `"targets": { "CHARACTER.DEFECT": { "1": { ... } } }`는 `"default"`를 그대로 두고 디펙트 목표치를 추가합니다. 반대로 `"urgency": { "2": { "block": 2 } }`는 2막의 역할별 사전 전체를 `{ "block": 2 }`로 바꿉니다(1·3막은 유지).

막별 사전에 없는 막은 **그 아래에서 가장 가까운 막** 값을 쓰고, 그것도 없으면 가장 낮은 막 값을 씁니다(`Weights.ForAct`).

### 3.1 가중치 레퍼런스

| 속성 | 타입 | 기본값 | 의미 | PLAN |
|---|---|---|---|---|
| `tierValue` | `{티어: 숫자}` | S80 A65 B50 C35 D20 | 티어별 기본 점수 | §7.1 기본 |
| `unknownTier` | 티어 | `"C"` | 티어 팩에 없는 카드의 가정 티어 | §7.1, §7.4 |
| `unknownConfidence` | 0..1 | 0.3 | 티어 팩에 없는 카드의 신뢰도 | §7.4 |
| `upgradedSupplyMultiplier` | 숫자 | 1.2 | 강화된 덱 카드의 역할 공급 배수 | §7.1 필요 |
| `wArch` | 숫자 | 20 | 빌드 점수 계수 `W_arch` | §7.1 빌드 |
| `buildCap` | 숫자 | 25 | 빌드 점수 상한 | §7.1 빌드 |
| `buildCapWeak` | 숫자 | 12 | 약한 역할이 목표 미달일 때의 빌드 상한 | §7.1 규칙 3 |
| `weakThreshold` | 0..1 | 0.5 | 약한 역할 공급/목표 비율이 이 값 미만이면 `buildCapWeak` 적용 | §7.1 규칙 3 |
| `weakRoles` | 문자열 배열 | `["block","draw"]` | 약점 우선 규칙을 보는 역할 | §7.1 규칙 3 |
| `offBuildPenalty` | 숫자 | 10 | commit ≥ 기준인데 빌드 밖 카드일 때 감점 | §7.1 빌드 |
| `commitThreshold` | 0..1 | 0.7 | 엔진 전역의 "빌드 확정" 기준(스킵 등) | §7.1, §7.2 |
| `hybridMargin` | 0..1 | 0.15 | 상위 두 아키타입 commit 차이가 이 이하면 하이브리드 | §7.5 |
| `comboCap` | 숫자 | 15 | 콤보 점수 상한 | §7.1 콤보 |
| `wNeed` | 숫자 | 25 | 필요 점수 계수 `W_need` | §7.1 필요 |
| `targets` | `{캐릭터 또는 "default": {막: {역할: 숫자}}}` | `default`: 1막 frontload3 aoe1 block2.5 scaling0.5 draw1 / 2막 3.5·1.5·3.5·1.5·2 / 3막 3.5·2·4·2.5·3 | 막별 역할 공급 목표치 | §7.1 필요 |
| `urgency` | `{막: {역할: 숫자}}` | 1막 frontload1.4 aoe1 block0.8 scaling0.5 draw0.6 / 2막 1·1.1·1.3·1.1·0.9 / 3막 0.8·1·1.2·1.2·1.2 | 막별 역할 긴급도 ("Jobs" 프레임워크) | §7.1 필요 |
| `lambdaDilution` | 숫자 | 1.5 | 희석 λ | §7.1 희석 |
| `idealDeckSize` | `{막: 정수}` | 1:15 2:20 3:25 | 막별 이상 덱 크기 | §7.1 희석, §7.2 |
| `rhoDuplicate` | 숫자 | 15 | 중복 ρ | §7.1 중복 |
| `requiresPenalty` | 숫자 | 15 | `requires` 축이 덱에 없을 때 감점("엔진 없음") | §7.1 규칙 1 |
| `saturation.ratio` | 숫자 | 2.0 | 생산자 > `ratio × max(1, 소비자)`면 초과분 | §7.1 규칙 2 |
| `saturation.perExcess` | 숫자 | 0.15 | 초과 생산자 1장당 빌드·필요·콤보 가치 감소율 | §7.1 규칙 2 |
| `saturation.minFactor` | 숫자 | 0.3 | 포화 감쇠의 하한 배수 | §7.1 규칙 2 |
| `powerSaturation.threshold` | 정수 | 6 | 덱의 파워 카드가 이 수 이상이면 추가 파워 감점 | §7.1 규칙 2 |
| `powerSaturation.penaltyPerExtra` | 숫자 | 4 | 기준 초과 파워 1장당 감점 | §7.1 규칙 2 |
| `situation.bossRewardBuildBonus` | 숫자 | 8 | 보스 보상에서 빌드 카드 가산 | §7.1 상황 |
| `situation.lowHpThreshold` | 0..1 | 0.3 | 저체력 기준 HP 비율 | §7.1 상황 |
| `situation.lowHpBlockBoost` | 숫자 | 0.5 | 저체력일 때 방어 필요에 더하는 비율 | §7.1 상황 |
| `skip.t0` | `{막: 숫자}` | 1:45 2:52 3:58 | 스킵 기준 `T0[막]` | §7.2 |
| `skip.lambdaSize` | 숫자 | 1.5 | 덱 크기 초과 1장당 스킵 가산 | §7.2 |
| `skip.lambdaCommit` | 숫자 | 5 | 빌드 확정 시 스킵 가산 | §7.2 |
| `skip.margin` | 숫자 | 0.5 | 카드가 스킵을 이겨야 하는 최소 차이 | §7.2 |
| `skip.lowConfidence` | 0..1 | 0.5 | 이 미만 신뢰도의 카드는 스킵을 이기지 못함 | §7.2 |
| `confidenceSureGap` | 숫자 | 10 | 1·2위 차이가 이 이상이면 "확신" | §7.4 |
| `confidenceCloseGap` | 숫자 | 5 | 1·2위 차이가 이 미만이면 "박빙" | §7.4 |
| `shop.removalBase` | 정수 | 75 | 카드 제거 첫 가격 | §7.3 |
| `shop.removalStep` | 정수 | 25 | 제거할 때마다 오르는 가격 | §7.3 |
| `shop.removalBaseHighAscension` | 정수 | 100 | 고승천 첫 제거 가격 | §7.3 |
| `shop.removalStepHighAscension` | 정수 | 50 | 고승천 가격 증가분 | §7.3 |
| `shop.highAscension` | 정수 | 6 | 고승천 기준 승천 단계 | §7.3 |
| `shop.curseRemovalValue` | 숫자 | 30 | 저주 제거의 점수 환산 가치 | §7.3 |
| `shop.basicRemovalValue` | 숫자 | 14 | 기본 타격/수비 제거의 점수 환산 가치 | §7.3 |
| `completion.coreWeight` | 숫자 | 100 | 완성도 %에서 core·payoff 카드 가중치(참고 사이트 `must`) | §7.5 |
| `completion.supportWeight` | 숫자 | 56 | 완성도 %에서 enabler·support 카드 가중치(참고 사이트 `rec`) | §7.5 |
| `completion.rarityOdds` | `{희귀도: 0..1}` | common0.60 uncommon0.37 rare0.03 | 보상 슬롯 하나의 희귀도 확률 [추정] | §7.5 |
| `completion.poolSize` | `{희귀도: 정수}` | common20 uncommon36 rare16 | 카탈로그가 없을 때 캐릭터 풀 크기 | §7.5 |
| `completion.cardsPerReward` | 정수 | 3 | 카드 보상 한 번의 카드 수 | §7.5 |

역할 이름(`Roles`): `frontload`, `aoe`, `block`, `scaling`, `draw`, `energy`, `exhaust_fodder`.

## 4. `tiers/*.json` (티어 팩)

캐릭터 하나에 파일 하나입니다(PLAN §6.2). 무색 카드는 `"character": "COLORLESS"` 팩에 넣습니다.

```json
{
  "character": "CHARACTER.DEFECT",
  "dataVersion": "v0.107.1",
  "cards": {
    "CARD.BALL_LIGHTNING": { "tier": "B", "actTier": { "1": "A" }, "roles": { "frontload": 0.6 } }
  }
}
```

| 필드 | 타입 | 기본값 | 의미 |
|---|---|---|---|
| `character` | 캐릭터 ID | (필수) | `CHARACTER.X` 또는 `COLORLESS` |
| `dataVersion` | 문자열 | null | 이 팩이 기준으로 삼은 게임 버전 |
| `cards` | `{카드 ID: 항목}` | `{}` | 카드별 평가 |

카드 항목(`CardTierEntry`):

| 필드 | 타입 | 기본값 | 의미 |
|---|---|---|---|
| `tier` | 티어 | `"C"` | 배지에 표시하는 기본 티어 |
| `actTier` | `{막: 티어}` | null | 막별 티어 덮어쓰기 |
| `upgradedDelta` | 숫자 | 0 | 강화본일 때 기본 점수에 더할 값 |
| `roles` | `{역할: 0..1}` | `{}` | 역할 강도 (필요 점수와 역할 공급 계산에 사용) |
| `provides` | 문자열 배열 | `[]` | 생산하는 자원 축 (예: `STAR`, `ORB_LIGHTNING`, `DISCARD_ENGINE`) |
| `consumes` | 문자열 배열 | `[]` | 소비하는 자원 축 |
| `requires` | 문자열 배열 | `[]` | 덱에 있어야 시너지가 생기는 축(하나라도 있으면 충족) |
| `dupFactor` | 0..3 | 1.0 | 중복 감점 배수. 0이면 여러 장이어도 괜찮음 |
| `cycle` | 0..1 | 0 | 스스로를 대체하는 정도(드로우·순환). 희석 감점을 줄임 |
| `tip` | 문자열 | null | 한 줄 팁 (한국어, 카드 설명 문구 금지) |
| `confidence` | 0..1 | 1.0 | 평가 신뢰도. 0.5 미만이면 "추정" 표시, 스킵을 이기지 못함 |
| `changedIn` | 문자열 | null | 평가 이후 카드가 바뀐 게임 버전. 값이 있으면 "추정" |
| `source` | 문자열 | `"curated"` | 평가 출처 (`curated`, `placeholder` 등) |
| `type` | 문자열 | null | 카탈로그가 없을 때 쓰는 카드 타입 (`attack`/`skill`/`power`/...) |
| `rarity` | 문자열 | null | 카탈로그가 없을 때 쓰는 희귀도 |
| `cost` | 정수 | null | 카탈로그가 없을 때 쓰는 비용 |

카드 티어 조회 순서(`DataPack.FindTier`): 캐릭터 팩 → `COLORLESS` 팩 → 그 밖의 팩. `type`/`rarity`는 카탈로그 값이 항상 우선입니다.

## 5. `archetypes/*.json` (아키타입)

배열 하나, 또는 최상위 객체 `{ "character": ..., "archetypes": [...] }` 형태입니다. 객체 형태에서는 `character`가 없는 항목에 최상위 값을 넣습니다.

```json
{
  "character": "CHARACTER.DEFECT",
  "archetypes": [{
    "id": "defect.orb_focus",
    "names": { "kor": "구체 덱" },
    "cards": { "core": ["CARD.DEFRAGMENT"], "enabler": ["CARD.COOLHEADED"], "payoff": [], "support": ["CARD.HOLOGRAM"] },
    "fullWeight": 4.0,
    "keyRelics": [{ "id": "RELIC.CRACKED_CORE", "w": 1.0 }],
    "needs": [{ "tag": "ORB_FROST", "min": 2 }, { "role": "draw", "min": 2 }]
  }]
}
```

| 필드 | 타입 | 기본값 | 의미 |
|---|---|---|---|
| `id` | 문자열 | (필수) | 전체 팩에서 유일한 ID. 관례: `<캐릭터>.<이름>` |
| `character` | 캐릭터 ID | (필수, 객체 형태면 상속) | 아키타입의 캐릭터 |
| `names` | `{언어: 이름}` | `{}` | 표시 이름. `kor`가 있으면 그것을 씀 |
| `color` | 문자열 | null | UI 색 (`#rrggbb`) |
| `cards.core` | 카드 ID 배열 | `[]` | 핵심 카드 (참고 사이트 `must`) |
| `cards.enabler` | 카드 ID 배열 | `[]` | 시동 카드 |
| `cards.payoff` | 카드 ID 배열 | `[]` | 마무리 카드 |
| `cards.support` | 카드 ID 배열 | `[]` | 보조 카드 (참고 사이트 `rec`) |
| `roleWeight` | `{역할: 숫자}` | core1.0 enabler0.7 payoff0.9 support0.5 | 역할별 commit 가중치. **키 단위 병합**(적은 키만 교체) |
| `fullWeight` | 숫자 > 0 | 4.0 | commit이 1.0이 되는 가중치 합 |
| `commitThreshold` | (0, 1] | 0.7 | 이 아키타입이 "확정"되는 commit |
| `keyRelics` | `[{id, w}]` | `[]` | 핵심 유물과 commit 가중치(`w` 기본 1.0) |
| `needs` | `[{tag?, role?, min}]` | `[]` | 요구 조건. `role`은 역할 공급과, `tag`는 덱의 `provides` 축 개수와 비교. `min` 기본 1 |
| `desc` | 문자열 | null | 한 줄 설명 (한국어) |
| `guideHeroCard` | 카드 ID | null | 빌드 모음 탭의 대표 카드 |

한 카드를 여러 역할에 적으면 core → payoff → enabler → support 순으로 처음 나온 역할 하나로만 셉니다.

### 5.1 빌드 감지 계산 (`ArchetypeDetector`, PLAN §7.5)

- **commit(a)** = `min(1, (Σ 보유한 서로 다른 아키타입 카드의 roleWeight + Σ 보유 핵심 유물 w) / fullWeight)`
  - **사본 처리**: 같은 카드의 첫 장은 roleWeight 전부, 두 번째 장은 0.5배, 세 번째 장부터는 0. 강화본도 같은 카드입니다.
  - 덱·유물·아키타입의 ID는 모두 별칭을 푼 뒤 비교합니다.
- **완성도 %** = 보유 가중치 / 전체 가중치 × 100. core·payoff는 `completion.coreWeight`(100), enabler·support는 `completion.supportWeight`(56). 서로 다른 카드만 셉니다(사본은 올리지 않음).
- **단계(불꽃)** = `ceil(완성도 / 20)`를 1..5로 제한. 아키타입 카드가 하나도 없을 때(완성도 0)만 0입니다.
- **필요한 카드**: 보유하지 않은 core, payoff 카드(core 먼저). 희귀도는 카탈로그 → 티어 항목 순.
- **충족 안 된 요구**: `role` 요구는 `역할 공급[role] < min`, `tag` 요구는 `provides 개수[tag] < min`이면 미충족. 해당 입력이 없으면(null) 그 검사는 건너뜁니다.
- **정렬**: commit 내림차순 → 완성도 내림차순 → id 사전순.
- **하이브리드**: 상위 두 아키타입이 모두 commit > 0이고 차이 ≤ `hybridMargin`.
- **core 등장 확률 [추정]** (v1 근사): 보상 슬롯 하나에 특정 카드 c가 나올 확률 `p(c) = rarityOdds[희귀도(c)] / 풀 크기(희귀도)`.
  - 풀 크기: 카탈로그가 있으면 `pool == 런 캐릭터`이고 그 희귀도인 카드 수(싱글플레이면 `multiplayerOnly` 제외). 없으면 `completion.poolSize`.
  - 희귀도를 모르면 `uncommon`으로 보고, `rarityOdds`에 없는 희귀도(basic, ancient 등)는 보상에 나오지 않는 것으로 봅니다.
  - `P(N번 안에 1장 이상) = 1 − (1 − Σ p(c))^(cardsPerReward × N)` (슬롯은 독립으로 근사).
  - 빠진 core/payoff가 없으면(이미 완성) **1.0**, 남은 보상이 0이면 0.

## 6. `combos/*.json` (콤보)

배열 하나, 또는 `{ "character": ..., "combos": [...] }` 형태입니다(PLAN §6.4). 항목의 `character`가 최상위 값보다 우선합니다. 어느 쪽에도 없으면 모든 캐릭터에 적용됩니다.

| 필드 | 타입 | 기본값 | 의미 |
|---|---|---|---|
| `character` | 캐릭터 ID | null | 적용 캐릭터. null이면 전체 |
| `cards` | 카드 ID 배열 | `[]` | 콤보를 이루는 카드 (서로 다른 카드 2장 이상) |
| `name` | 문자열 | `""` | 칩에 표시할 이름 (한국어) |
| `why` | 문자열 | null | 이유 한 줄 (한국어, 카드 설명 문구 금지) |
| `w` | 숫자 | 6 | 짝이 모두 있으면 +w, 일부만 있으면 +w/2 (상한 `comboCap`) |

## 7. `aliases.json`

```json
{ "CARD.OLD_ID": "CARD.NEW_ID" }
```

- 키와 값은 카드 또는 유물 ID입니다.
- 별칭은 한 단계만 풉니다. 값이 다시 별칭이면(연쇄) 검증 경고가 납니다.

## 8. `catalog.json` (카드 카탈로그)

브리지 모드의 `CatalogDumper`가 사용자 설치본에서 생성합니다(PLAN §6.1). 게임 텍스트가 들어가므로 **커밋하지 않습니다**(`.gitignore`의 `data/catalog/`). 형식 예시는 `data/packs/sample/catalog.example.json`에 있습니다(로더는 이 예시 파일을 읽지 않음).

| 필드 | 타입 | 기본값 | 의미 |
|---|---|---|---|
| `gameVersion` | 문자열 | null | 덤프한 게임 버전 |
| `commit` | 문자열 | null | 게임 빌드 커밋 |
| `cards[].id` | 카드 ID | `""` | 카드 ID |
| `cards[].pool` | 캐릭터 ID | null | 카드 풀 (`CHARACTER.X` 또는 `COLORLESS`) |
| `cards[].names` | `{언어: 이름}` | `{}` | 표시 이름 (로컬 전용) |
| `cards[].type` | 문자열 | null | `attack` / `skill` / `power` / `status` / `curse` / `quest` |
| `cards[].rarity` | 문자열 | null | `basic` / `common` / `uncommon` / `rare` / `ancient` / `special` / `curse` |
| `cards[].cost` | 정수 | null | 비용 |
| `cards[].costX` | bool | false | X 비용 여부 |
| `cards[].keywords` | 문자열 배열 | `[]` | 키워드 |
| `cards[].tags` | 문자열 배열 | `[]` | 게임 태그 |
| `cards[].target` | 문자열 | null | 대상 (`AnyEnemy`, `AllEnemies` 등) |
| `cards[].gainsBlock` | bool | false | 방어도를 얻는지 |
| `cards[].multiplayerOnly` | bool | false | 멀티플레이 전용 카드인지 |

알 수 없는 필드(예: `upgrade`)는 무시합니다.

## 9. 검증 규칙 (`DataPackValidator`)

`Validate(pack)`는 `ValidationIssue(Severity, Where, Message)` 목록을 돌려줍니다. `Where`는 `tiers[CHARACTER.DEFECT].cards[CARD.X].roles.block`처럼 문제 위치를 가리킵니다.

**오류(Error)**: 데이터가 틀렸습니다. 팩을 배포하면 안 됩니다.

| 대상 | 규칙 |
|---|---|
| ID | 카드·유물·캐릭터 ID가 §2.2 형식이 아님 (티어 팩 키, 아키타입 카드·유물·캐릭터, `guideHeroCard`, 콤보 카드·캐릭터, 별칭 키·값) |
| 티어 항목 | `roles` 값이 0..1 밖, `confidence`가 0..1 밖, `dupFactor`가 0..3 밖, `cycle`이 0..1 밖, `upgradedDelta`가 유한수가 아님, `actTier` 막 < 1, `cost` < 0 |
| 아키타입 | id 없음, **id 중복**, 카드 0장, `fullWeight` ≤ 0, `commitThreshold`가 (0, 1] 밖, `roleWeight` 음수, 유물 `w` 음수, `needs`에 `tag`와 `role`이 모두 없음, `min` 음수 |
| 콤보 | 서로 다른 카드가 2장 미만 |
| 가중치 | `unknownConfidence`·`commitThreshold`·`hybridMargin`·`weakThreshold`·`skip.lowConfidence`가 0..1 밖, `completion.cardsPerReward` < 1, `rarityOdds` 값이 0..1 밖, `poolSize` 값 ≤ 0 |

**경고(Warning)**: 데이터가 덜 채워졌을 가능성이 큽니다.

| 대상 | 규칙 |
|---|---|
| 티어 | 알 수 없는 역할 키, `Tiers` 사전 키와 팩의 `character`가 다름 |
| 아키타입 | 카드에 티어 항목이 없음, 같은 카드를 두 번 적음, 캐릭터의 티어 팩이 없음, 알 수 없는 `roleWeight` 키·`needs.role`, `guideHeroCard`가 아키타입 카드가 아님 |
| 콤보 | 카드에 티어 항목이 없음, 캐릭터의 티어 팩이 없음, `w` ≤ 0 (효과 없음) |
| 별칭 | 데이터가 현재 ID 대신 옛 ID(별칭 키)를 씀, 별칭 연쇄 |
| 카탈로그 | (카탈로그가 있을 때) 티어·아키타입·콤보·별칭 대상 카드 ID가 카탈로그에 없음 |
| 가중치 | `tierValue`에 빠진 티어, `rarityOdds` 합이 1 초과 |
