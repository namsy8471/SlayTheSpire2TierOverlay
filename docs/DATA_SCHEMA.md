# 데이터 팩 스키마

추천 엔진(`Sts2Overlay.Core`)이 읽는 큐레이션 데이터의 파일 형식입니다. PLAN §6(데이터 설계)과 §7(추천 로직)을 구현 기준으로 정리했습니다.

- 로더: `Sts2Overlay.Core.Data.DataPackLoader`
- 검증기: `Sts2Overlay.Core.Data.DataPackValidator`
- 형식 샘플: [`data/packs/sample/`](../data/packs/sample/) (값은 모두 자리표시자)

> 카드 이름과 설명은 Mega Crit 저작물이므로 **레포의 데이터에는 ID만** 적습니다. 커밋된 카탈로그(`data/catalog/cards.v0.107.1.json`)도 ID와 수치만 담고, 이름은 사용자 PC에서 따로 생성합니다(`data/local/`, 커밋 안 함).

> **[코드 확인]** 표시는 게임 코드(v0.107.1)로 확인한 수치입니다. 출처: M0 스파이크 결과 문서 `SPIKE.md` §5 (`m0-spike` 브랜치의 `docs/SPIKE.md`; 게임 코드 `Odds/CardRarityOdds.cs` 등). **[추정]** 표시는 아직 확인하지 못한 근사입니다.

## 1. 폴더 구조

```
data/packs/<팩 이름 또는 게임 버전>/
├─ weights.json          (선택) 가중치 부분 덮어쓰기
├─ tiers/*.json          캐릭터별 티어 팩, 무색은 colorless.json
├─ archetypes/*.json     아키타입 목록
├─ combos/*.json         콤보 목록
├─ aliases.json          (선택) 옛 ID → 현재 ID
├─ catalog.json          (선택) 카드 카탈로그. 보통 넘겨받음 (아래 참고)
├─ events.stats.json     (선택) 이벤트·고대 선택지를 상위 플레이어가 고른 비율 (§10)
└─ relics.json           (선택) 유물을 상위 플레이어가 사거나 고른 비율 (§11)

data/catalog/cards.v0.107.1.json   커밋된 이름 없는 카드 카탈로그 (§8)
```

- 모든 부분이 선택입니다. 없는 파일은 기본값(`weights.json`) 또는 빈 목록이 됩니다.
- 하위 폴더에서는 `*.json` 파일만 읽고, 파일 이름의 사전순으로 읽습니다. 파일 이름 자체에는 의미가 없습니다(캐릭터는 파일 내용으로 정합니다).
- 카탈로그 우선순위: `LoadDirectory(dir, catalog)`로 넘긴 객체 → `LoadDirectory(dir, catalogPath: "data/catalog/cards.v0.107.1.json")`로 넘긴 파일 → 폴더의 `catalog.json`. 파일 하나만 읽을 때는 `DataPackLoader.LoadCatalogFile(path)`를 씁니다(레포 기준 경로는 `DataPackLoader.DefaultCatalogRelativePath`).
- `events.stats.json`·`relics.json`이 없으면 `DataPack.EventStats`·`RelicStats`가 null이고, 이벤트·유물 점수는 파일이 없던 때와 똑같습니다.
- 계획만 있고 아직 로더가 읽지 않는 파일: `manifest.json`(PLAN §6.6), `user_overrides.json`, `data/schema/*.schema.json`.

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
| 중첩 객체 (`skip`, `shop`, `completion`, `cardOdds`, `cardOdds.eliteReward`, `cardOdds.pity`, `events` 등) | **병합**: 적은 속성만 바뀌고 나머지는 기본값 유지 | `"skip": { "margin": 1 }` → `t0`, `lambdaSize` 유지 |
| 사전 (`tierValue`, `targets`, `urgency`, `idealDeckSize`, `skip.t0`, `skip.basicPickBias`, `completion.poolSize`, `shop.cardPrice`, `shop.characterCardSlots`, `shop.colorlessCardSlots`, `events.maxHpFactor`, `events.relicTier`) | **키 단위 병합**: 적은 키의 값만 교체(값이 사전이면 그 값 전체를 교체), 나머지 키 유지 | `"tierValue": { "S": 90 }` → A~D 유지 |
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
| `corePriority.bonus` | 숫자 ≥ 0 | 15 (= 티어 한 칸) | 빠진 core 카드 가산 "코어 확보"의 최대값. `starter + (bonus − starter) × ramp` | §7.1 코어 먼저 |
| `corePriority.leanMin` | 0..1 | 0.15 | 이 commit 이상이고 1위 빌드이거나 1위와 `hybridMargin` 이내면 "기울어진" 빌드 (lean 부분을 받음) | §7.1 코어 먼저 |
| `corePriority.leanFull` | (0, 1] | 0.4 | lean 부분이 `bonus − starter` 전부가 되는 commit. 그 아래는 `commit / leanFull` 비례 (ramp) | §7.1 코어 먼저 |
| `corePriority.weakFactor` | 0..1 | 0.5 | 약한 역할이 있으면 "코어 확보"에 곱함 (약점 먼저, 규칙 3) | §7.1 코어 먼저 |
| `corePriority.starterBonus` | 숫자 ≥ 0 | 6 | `starterMaxAct`까지 어느 빌드든(기울지 않아도) 빠진 core 카드가 받는 기본 가산. core를 확보한 빌드가 하나라도 있으면 0 | §7.1 코어 먼저 |
| `corePriority.starterMaxAct` | 정수 | 2 | `starterBonus`를 주는 마지막 막 | §7.1 코어 먼저 |
| `corePriority.noCoreFactor` | `{역할: 0..1}` | payoff0 support0 enabler0 | core가 없을 때 그 역할 카드의 시너지 중 최소한 남는 비율(`unlock`과 큰 쪽). 키가 없는 역할은 잠그지 않음. **키 단위 병합** | §7.1 코어 먼저 |
| `corePriority.noCoreMaxUnlock` | 0..1 | 0.5 | core가 없을 때 시너지가 풀리는 최대 비율. `unlock = clamp(noCoreMaxUnlock − P, 0, noCoreMaxUnlock)`, P = 남은 런에서 빠진 core가 한 번이라도 나올 확률. P ≥ 0.5면 0 | §7.1 코어 먼저 |
| `corePriority.noCoreUnlockByAct` | `{막: 0..1}` | 1막 0 / 2막 0.2 / 3막 0.4 | P를 모를 때(감지기 없이 만든 상태) 쓰는 막별 `unlock`. `noCoreMaxUnlock`으로 제한. **키 단위 병합** | §7.1 코어 먼저 |
| `corePriority.floorsPerAct` | 정수 ≥ 1 | 15 | [추정] P 계산용 남은 보상 추정: 막당 층 수 | §7.1 코어 먼저 |
| `corePriority.rewardsPerFloor` | 숫자 ≥ 0 | 0.5 | [추정] 남은 층당 일반 카드 보상 수 | §7.1 코어 먼저 |
| `corePriority.runActs` | 정수 ≥ 1 | 3 | 런의 막 수. 끝나지 않은 막마다 보스 보상(희귀 3장) 1번 | §7.1 코어 먼저 |
| `corePriority.essentialTarget` | 정수 ≥ 0 | 2 | core를 얻은 뒤 support(서브 시너지)가 제값을 받기 전에 필요한 서로 다른 필수 시너지(payoff + enabler) 수. 아키타입 `essentialTarget`이 있으면 그것 | §7.1 필수 시너지 |
| `corePriority.subBeforeEssential` | 0..1 | 0.4 | core는 있지만 필수 시너지가 목표보다 적을 때 support 카드 시너지에 곱함 | §7.1 필수 시너지 |
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
| `skip.lambdaCommit` | 숫자 | 5 | 빌드 확정 시 스킵 가산. 코어를 반영한 commit(`commit × gate`, 아래 §5.1 코어 먼저 1)이 `commitThreshold` 이상일 때만 | §7.2 |
| `skip.margin` | 숫자 | 0.5 | 카드가 스킵을 이겨야 하는 최소 차이 | §7.2 |
| `skip.lowConfidence` | 0..1 | 0.5 | 이 미만 신뢰도의 카드는 스킵을 이기지 못함 (아래 `lowConfidenceStep` 예외) | §7.2 |
| `skip.lowConfidenceStep` | 숫자 ≥ 0 | 15 | 신뢰도 낮은 카드라도 `스킵 + margin + 이 값` 이상이고 신뢰도 충분한 카드 중 스킵을 이기는 카드가 없으면 스킵 대신 추천(확신도 "추정"). 한 티어 낮게 평가돼도 이기는 경우만. 최선 카드를 바꾸지는 않음 | §7.2 |
| `skip.basicPickBias` | `{막: 숫자 ≥ 0}` | 1:30 2:10 3:0 | "초반엔 어떤 카드든 타격·수비보다 낫다": 스킵에 `−basicPickBias[막] × min(1, basicShare / basicShareFull)` ("기본 카드 교체"). `basicShare` = 덱의 기본 카드(카탈로그·티어 희귀도 `basic`, 희귀도를 모르면 타격/수비 id) / 덱 크기. 1막 30이면 시작 덱의 스킵이 15로 D 티어(20)보다 낮아, 1막 초반엔 "엔진 없음"·중복 같은 실제 감점이 있는 카드만 스킵 | §7.2 |
| `skip.basicShareFull` | (0, 1] | 0.5 | 기본 카드 비율이 이 이상이면 `basicPickBias` 전부, 아래로는 비례 | §7.2 |
| `confidenceSureGap` | 숫자 | 10 | 1·2위 차이가 이 이상이면 "확신" | §7.4 |
| `confidenceCloseGap` | 숫자 | 5 | 1·2위 차이가 이 미만이면 "박빙" | §7.4 |
| `shop.removalBase` | 정수 | 75 | 카드 제거 첫 가격 [코드 확인] | §7.3 |
| `shop.removalStep` | 정수 | 25 | 제거할 때마다 오르는 가격 [코드 확인] | §7.3 |
| `shop.removalBaseHighAscension` | 정수 | 100 | 고승천 첫 제거 가격 [코드 확인] | §7.3 |
| `shop.removalStepHighAscension` | 정수 | 50 | 고승천 가격 증가분 [코드 확인] | §7.3 |
| `shop.highAscension` | 정수 | 6 | 제거 가격의 고승천 기준(승천 6 이상) [코드 확인] | §7.3 |
| `shop.curseRemovalValue` | 숫자 | 30 | 저주 제거의 점수 환산 가치 | §7.3 |
| `shop.basicRemovalValue` | 숫자 | 14 | 기본 타격/수비 제거의 점수 환산 가치 | §7.3 |
| `shop.cardPrice` | `{희귀도: 정수}` | common50 uncommon75 rare150 | 상점 카드 기본 가격 [코드 확인] | §7.3 |
| `shop.colorlessPriceMultiplier` | 숫자 | 1.15 | 무색 카드 가격 배수 [코드 확인] | §7.3 |
| `shop.priceJitterMin` / `priceJitterMax` | 숫자 | 0.95 / 1.05 | 가격 무작위 배수 범위 [코드 확인] | §7.3 |
| `shop.saleMultiplier` | 0..1 | 0.5 | 세일 카드(1장) 가격 배수 [코드 확인] | §7.3 |
| `shop.characterCardSlots` | `{타입: 정수}` | attack2 skill2 power1 | 상점 캐릭터 카드 구성 [코드 확인] | §7.3 |
| `shop.colorlessCardSlots` | `{희귀도: 정수}` | uncommon1 rare1 | 상점 무색 카드 구성 [코드 확인] | §7.3 |
| `cardOdds.highAscension` | 정수 | 7 | 희귀 확률 절반·보정 증가분·강화 확률이 바뀌는 승천 단계 [코드 확인] | §7.5 |
| `cardOdds.rareMultiplierHighAscension` | 0..1 | 0.5 | 출처에 `rareHighAscension`이 없을 때 고승천 희귀 확률 배수 | §7.5 |
| `cardOdds.cardReward` | `{rare, uncommon, rareHighAscension}` | 0.03 / 0.37 / 0.0149 | 일반 전투 보상 (이벤트 카드 제시에도 사용). 일반 = 나머지 [코드 확인] | §7.5 |
| `cardOdds.eliteReward` | 〃 | 0.10 / 0.40 / 0.05 | 엘리트 보상 [코드 확인] | §7.5 |
| `cardOdds.bossReward` | 〃 | 1.0 / 0 / 1.0 | 보스 보상 (항상 희귀) [코드 확인] | §7.5 |
| `cardOdds.shop` | 〃 | 0.09 / 0.37 / 0.045 | 상점 캐릭터 카드 [코드 확인] | §7.5 |
| `cardOdds.pity.start` | 숫자 | -0.05 | 희귀 보정치 시작값(희귀가 나오면 이 값으로 초기화) [코드 확인] | §7.5 |
| `cardOdds.pity.step` / `stepHighAscension` | 숫자 | 0.01 / 0.005 | 희귀가 아닌 카드 1장마다 오르는 보정치 [코드 확인] | §7.5 |
| `cardOdds.pity.cap` | 숫자 | 0.40 | 보정치 상한 [코드 확인] | §7.5 |
| `cardOdds.upgradeChancePerAct` / `upgradeChancePerActHighAscension` | 숫자 | 0.25 / 0.125 | 희귀가 아닌 보상 카드의 강화 확률 = 막 × 값 (상점 카드는 강화 없음) [코드 확인] | §7.5 |
| `completion.coreWeight` | 숫자 | 100 | 완성도 %에서 core·payoff 카드 가중치(참고 사이트 `must`) | §7.5 |
| `completion.supportWeight` | 숫자 | 56 | 완성도 %에서 enabler·support 카드 가중치(참고 사이트 `rec`) | §7.5 |
| `completion.poolSize` | `{희귀도: 정수}` | common20 uncommon35 rare25 | 카탈로그가 없을 때 캐릭터 풀 크기 (v0.107.1 카탈로그의 캐릭터별 싱글플레이 수) | §7.5 |
| `completion.cardsPerReward` | 정수 | 3 | 카드 보상 한 번의 카드 수 | §7.5 |
| `events.measuredBlend` | 0..1 | 0.5 | `events.stats.json`의 측정 선택률을 이벤트 점수에 섞는 비율. 0이면 효과 공식만 씀(파일이 없을 때와 같음). 식은 §10 | 이벤트 |
| `events.measuredScale` | 숫자 ≥ 0 | 20 | 선택률이 무작위보다 1.0 높을 때의 점수. 지금 공식 값의 폭(변화 16~28, 유물 13~24)과 비슷하게 둠 | 이벤트 |
| `events.*` (그 밖) | | `EventWeights` 기본값 | 이벤트 효과 점수(`goldValue`, `maxHpFactor`, `relicTier`, `cursePenalty` 등). 지금까지 코드 상수였고 이제 `weights.json`에서 덮어쓸 수 있음 [추정] | 이벤트 |

역할 이름(`Roles`): `frontload`, `aoe`, `block`, `scaling`, `draw`, `energy`, `exhaust_fodder`.

예전의 `completion.rarityOdds`(출처 구분 없는 [추정] 확률)는 `cardOdds`로 대체되었습니다. 남아 있어도 무시합니다.

### 3.2 카드 확률 계산 (`CardOdds`)

- **희귀도** (`CardOdds.Rarity(w, 출처, 승천, 보정치)`): 게임은 `r ∈ [0,1)`을 굴려 `r < 희귀 + 보정치`면 희귀로 정합니다(로그 `Card rarity: Rolled 0.455, need < -0.02 for rare` = 0.03 − 0.05). [코드 확인]
  - 희귀 = `clamp(희귀 + 보정치, 0, 1)`. 보정치는 `pity.cap`에서 자릅니다. 기본 희귀 확률이 1 이상인 출처(보스)는 보정치와 상관없이 항상 희귀.
  - **고급·일반 배분 [추정]**: 슬레이 더 스파이어 1의 `roll + cardBlizzRandomizer`처럼 보정치가 굴림 전체를 옮긴다고 가정합니다. 고급 = `clamp(희귀 + 보정치 + 고급) − 희귀`, 일반 = 나머지. 그래서 시작 보정치(−5%)에서 일반 전투 카드는 희귀 0%, 고급 35%, 일반 65%이고, 보정치가 오르면 일반 몫이 줄어듭니다.
  - 보정치를 0으로 주면 표의 확률 그대로입니다.
- **보정치 갱신** (`CardOdds.NextPityOffset`): 희귀가 나오면 `pity.start`, 아니면 `+ step`(고승천 `stepHighAscension`), 상한 `pity.cap`. 로그의 `offset = ...` 값으로 현재 보정치를 읽을 수 있습니다. [코드 확인]
- **강화 확률** (`CardOdds.UpgradeChance`): 희귀가 아닌 보상 카드에 `막 × 0.25` (고승천 `× 0.125`), 0..1로 제한. 희귀·상점 카드는 0. [코드 확인]
- **상점 가격** (`ShopPrices.Estimated`/`Range`, `ShopScorer.EstimatedPrice`): `기본가 × (무색이면 1.15) × (0.95~1.05)`, 세일 카드는 × 0.5. 화면에서 가격을 못 읽었을 때 쓰는 추정값이며, 게임의 반올림 방식은 확인하지 않았습니다(추정값은 배수 1.0 기준 반올림). 카드 제거 가격은 `ShopScorer.RemovalPrice`: `75 + 25 × 제거 횟수`, 승천 6 이상 `100 + 50 × 제거 횟수`. [코드 확인]

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
| `cards.core` | 카드 ID 배열 | `[]` | 핵심 카드 (참고 사이트 `must`). 비워 두면 그 빌드는 "코어 먼저"가 꺼짐(검증기 경고) |
| `cards.enabler` | 카드 ID 배열 | `[]` | 시동 카드 |
| `cards.payoff` | 카드 ID 배열 | `[]` | 마무리 카드 |
| `cards.support` | 카드 ID 배열 | `[]` | 보조 카드 (참고 사이트 `rec`) |
| `roleWeight` | `{역할: 숫자}` | core1.0 enabler0.7 payoff0.9 support0.5 | 역할별 commit 가중치. **키 단위 병합**(적은 키만 교체) |
| `fullWeight` | 숫자 > 0 | 4.0 | commit이 1.0이 되는 가중치 합 |
| `commitThreshold` | (0, 1] | 0.7 | 이 아키타입이 "확정"되는 commit |
| `coreTarget` | 정수 ≥ 1 | 1 | 빌드가 돌아가려면 필요한 서로 다른 core 카드 수. 이만큼 갖기 전까지 빠진 core를 "코어 확보"로 밀어 주고, 다른 카드 시너지는 `보유 core / coreTarget` 비율로만 반영. core 카드 수보다 크면 그 수로 줄임 |
| `essentialTarget` | 정수 ≥ 0 또는 생략 | (`corePriority.essentialTarget`) | 이 빌드의 support 카드가 제값을 받기 전에 필요한 필수 시너지(payoff + enabler) 수. 필수 카드 수보다 크면 그 수로 줄임 |
| `keyRelics` | `[{id, w}]` | `[]` | 핵심 유물과 commit 가중치(`w` 기본 1.0) |
| `needs` | `[{tag?, role?, min}]` | `[]` | 요구 조건. `role`은 역할 공급과, `tag`는 덱의 `provides` 축 개수와 비교. `min` 기본 1 |
| `desc` | 문자열 | null | 한 줄 설명 (한국어) |
| `guideHeroCard` | 카드 ID | null | 빌드 모음 탭의 대표 카드 |
| `onTheme` | 카드 ID 배열 | (없음) | 빌드에 전념한 덱에서 다른 덱보다 확실히 자주 보이지만 어느 역할 목록에도 없는 카드(lift ≥ 1.5, 멤버 덱 보유 5명 이상, 재표본 80% 이상). **데이터만 있음: 점수 반영은 빌드 밖 감점 면제 PR에서 들어옵니다.** 지금 로더는 이 필드를 무시합니다 |

한 카드를 여러 역할에 적으면 core → payoff → enabler → support 순으로 처음 나온 역할 하나로만 셉니다.

**`core`에 넣을 카드**: 그 카드가 없으면 빌드가 돌아가지 않는 엔진 카드만 넣습니다(보통 1~3장). 다른 카드로 바꿔 쓸 수 있는 일반 공급 카드는 `enabler`, 쌓인 자원을 터뜨리기만 하는 마무리 카드는 `payoff`에 둡니다. core는 "코어 먼저" 규칙(§3.1 `corePriority`)의 기준이라, 잘못 넣으면 그 카드 한 장으로 "코어 확보"가 끝나 진짜 엔진을 더는 밀어 주지 않습니다. 보상에 나오지 않는 카드(Ancient 등)도 core에 두지 않습니다. **역할 = 추천 단계**: core → 필수 시너지(`payoff`·`enabler`) → 서브 시너지(`support`) 순서로 점수가 풀립니다. 코드에는 카드 ID가 없고 이 목록만 봅니다.

### 5.1 빌드 감지 계산 (`ArchetypeDetector`, PLAN §7.5)

- **commit(a)** = `min(1, (Σ 보유한 서로 다른 아키타입 카드의 roleWeight + Σ 보유 핵심 유물 w) / fullWeight)`
  - **사본 처리**: 같은 카드의 첫 장은 roleWeight 전부, 두 번째 장은 0.5배, 세 번째 장부터는 0. 강화본도 같은 카드입니다.
  - 덱·유물·아키타입의 ID는 모두 별칭을 푼 뒤 비교합니다.
- **완성도 %** = 보유 가중치 / 전체 가중치 × 100. core·payoff는 `completion.coreWeight`(100), enabler·support는 `completion.supportWeight`(56). 서로 다른 카드만 셉니다(사본은 올리지 않음).
- **단계(불꽃)** = `ceil(완성도 / 20)`를 1..5로 제한. 아키타입 카드가 하나도 없을 때(완성도 0)만 0입니다.
- **필요한 카드**: 보유하지 않은 core, payoff 카드(core 먼저). 희귀도는 카탈로그 → 티어 항목 순.
- **코어 진행**: `OwnedCore`(보유한 서로 다른 core 수), `CoreTarget`(`coreTarget`을 1..core 수로 제한, core가 없으면 0), `MissingCore`(빠진 core만), `CoreSecured = OwnedCore ≥ CoreTarget`, `CoreChance`(빠진 core가 있을 때 남은 런에서 그중 하나라도 나올 확률: 남은 일반 보상 `round(남은 층 × rewardsPerFloor)` + 남은 보스 보상, 엘리트·상점·이벤트 제외 [추정]), `OwnedEssential`·`EssentialTarget`(필수 시너지 진행). commit 공식은 그대로입니다(예측·보정이 commit에 의존).
- **코어 먼저 (`CardScorer`)**, 빌드 A마다:
  1. **코어 전 시너지 잠금**: `coreFactor = min(1, OwnedCore / CoreTarget)`. A의 core가 아닌 카드가 A에서 받는 시너지 × `gate = floor + (1 − floor) × coreFactor`, `floor = max(noCoreFactor[역할], unlock)`, `unlock = clamp(noCoreMaxUnlock − CoreChance, 0, noCoreMaxUnlock)`. 덱에 A 시너지 카드가 많아도 `unlock`은 오르지 않습니다. `build`는 core가 있다고 쳤을 때의 값, 잃은 만큼은 `core_missing` ("코어 없음", 설명 "코어 전: 개별 성능으로 평가 · 먼저 노릴 코어 …" 또는 "코어 전: 시너지 n% 반영")으로 따로 빼서 구성 요소 합 = 총점. 보스 보상 빌드 가산과 스킵의 "빌드 완성"(`skip.lambdaCommit`)도 `commit × gate`를 씁니다 (`CardScorer.GatedCommit`, 스킵은 `floor = unlock`).
  2. **코어 전에는 확정 아님**: 점수기에서는 `CoreSecured`인 빌드만 commit ≥ `commitThreshold`일 때 "확정"으로 보고 오프빌드 감점을 줍니다(감지기 commit·예측은 그대로).
  3. **코어 확보**: `CoreSecured`가 아닌 A의 빠진 core 카드에 `core_priority = (starter + (bonus − starter) × ramp) × (1 − coreFactor) × weak` (`starter` = `starterMaxAct`까지, 그리고 core를 확보한 빌드가 아직 없을 때만 `starterBonus`, `ramp` = A가 기울어 있으면 `clamp(commit/leanFull)` 아니면 0, `weak` = 약한 덱이면 `weakFactor`, `requires` 축이 없으면 0). 여러 빌드 중 가장 큰 값 하나만. `coreTarget` 2면 두 번째 core는 절반.
  4. **필수 → 서브 시너지**: core를 하나라도 가진 뒤, `OwnedEssential < EssentialTarget`이면 A의 support 카드 시너지 × `subBeforeEssential` (빌드 설명에 "보조 시너지 40% (필수 시너지 n/2 전)"). payoff·enabler는 바로 제값.
- **빌드 역할 표시** (`CardEvaluation.BuildRoles`): 제시된 카드가 어느 빌드의 코어 / 필수 시너지 / 서브 시너지인지, 현재 commit 높은 빌드 순으로 최대 2개 ("냉기·밀집 구체 코어 · 파워 서브 시너지"). 보상·상점·이벤트 카드 줄에 표시.
- **충족 안 된 요구**: `role` 요구는 `역할 공급[role] < min`, `tag` 요구는 `provides 개수[tag] < min`이면 미충족. 해당 입력이 없으면(null) 그 검사는 건너뜁니다.
- **정렬**: commit 내림차순 → 완성도 내림차순 → id 사전순.
- **하이브리드**: 상위 두 아키타입이 모두 commit > 0이고 차이 ≤ `hybridMargin`.
- **core 등장 확률** (`CoreOfferProbability`): 남은 보상 N번 안에 빠진 core/payoff 카드가 1장 이상 나올 확률.
  - 기본 시그니처 `(status, state, pack, N)`: 일반 전투 보상(`cardOdds.cardReward`)과 런의 승천으로 표 확률을 쓰고, 보정치는 쓰지 않습니다. 오버로드 `(…, N, OfferContext, pityOffset)`로 출처와 현재 보정치를 줄 수 있습니다.
  - 카드 한 장마다 희귀도 확률은 §3.2. 그 희귀도의 특정 카드가 나올 확률은 `1 / 풀 크기(희귀도)`.
  - 풀 크기: 카탈로그가 있으면 `CardCatalog.PoolSizes(캐릭터, 멀티 여부)` — 풀 주인이 런 캐릭터이고, 싱글이면 `MultiplayerOnly` 제외, 멀티면 `SingleplayerOnly` 제외, `showInLibrary == false` 제외. 없으면 `completion.poolSize`. (`showInLibrary == false`인 카드는 v0.107.1에서 폐기 카드와 이벤트 카드 1장뿐이라 캐릭터 풀 크기에는 영향이 없지만, 게임이 숨긴 카드는 생성되지 않는다고 보고 뺍니다.)
  - 카탈로그가 캐릭터 풀 밖(무색, 이벤트 등)이거나 이번 모드에서 나오지 않는다고 하는 빠진 카드는 0으로 셉니다. 희귀도를 모르면 `uncommon`, 출처가 굴리지 않는 희귀도(basic, ancient 등)는 0.
  - 보정치 없음: `P = 1 − (1 − Σ p(c))^(cardsPerReward × N)` (카드는 독립으로 근사).
  - 보정치 있음: 카드 한 장마다 보정치 분포를 갱신합니다(희귀면 초기화, 아니면 +step, 상한). 그래서 시작 보정치(−5%)에서 짧은 기간은 표 확률보다 낮고, 긴 기간은 보정치가 쌓여 높아집니다.
  - 한 보상 안의 중복 방지, 리롤, 상점의 타입 고정 슬롯은 무시합니다. [추정: 근사]
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

## 8. 카드 카탈로그 (`data/catalog/cards.v0.107.1.json`)

`tools/CatalogBuilder`가 `sts2.dll`의 IL을 정적으로 분석해 만든 **이름 없는** 카탈로그입니다(ID와 수치만, 578장). 게임 텍스트(이름·설명)는 들어 있지 않으므로 레포에 포함합니다. 이름은 `data/local/`에 따로 생성하며 커밋하지 않습니다. 로더는 파일을 **있는 그대로** 읽습니다(`DataPackLoader.LoadCatalog` / `LoadCatalogFile`). 형식 예시: `data/packs/sample/catalog.example.json`(로더는 읽지 않음).

최상위:

| 필드 | 타입 | 의미 |
|---|---|---|
| `gameVersion` | 문자열 | 게임 버전 (`v0.107.1`) |
| `source` | 문자열 | 만든 도구 |
| `note` | 문자열 | 메모 |
| `cardCount` | 정수 | 카드 수 (참고용, `cards`가 기준) |
| `commit` | 문자열 | (예전 덤프) 게임 빌드 커밋 |
| `cards` | 배열 | 카드 목록 |

카드(`CatalogCard`):

| 필드 | 타입 | 기본값 | 의미 |
|---|---|---|---|
| `id` | 카드 ID | `""` | 카드 ID |
| `pool` | 문자열 | null | 게임의 카드 풀: `CARD_POOL.DEFECT_CARD_POOL`, `CARD_POOL.COLORLESS_CARD_POOL`, `CARD_POOL.EVENT_CARD_POOL`/`CURSE`/`TOKEN`/`STATUS`/`QUEST`/`DEPRECATED`. (예전 형식 `CHARACTER.X`/`COLORLESS`도 읽음) |
| `character` | 캐릭터 ID | null | 캐릭터 카드면 `CHARACTER.X`. `null`이나 문자열 `"None"`은 null로 읽음 |
| `type` | 문자열 | null | `Attack` / `Skill` / `Power` / `Curse` / `Status` / `Quest` |
| `rarity` | 문자열 | null | `Basic` / `Common` / `Uncommon` / `Rare` / `Ancient` / `Event` / `Curse` / `Status` / `Token` / `Quest` |
| `cost` | 정수 | null | 비용 |
| `costX` | bool | false | X 비용 여부 |
| `starCost` | 정수 | -1 | 별 비용(리젠트). -1이면 없음 |
| `target` | 문자열 | null | `Self` / `AnyEnemy` / `AllEnemies` / `None` / `RandomEnemy` / `AnyAlly` / `AllAllies` |
| `gainsBlock` | bool | false | 방어도를 얻는지. 추출기가 판단하지 못하면 `null`(v0.107.1에서 2장) → false로 읽음 |
| `maxUpgrade` | 정수 | null | 최대 강화 단계 |
| `multiplayer` | 문자열 | null | `None` / `MultiplayerOnly` / `SingleplayerOnly` |
| `keywords`, `tags` | 문자열 배열 | `[]` | 키워드, 게임 태그 |
| `showInLibrary` | bool | true | 카드 도감에 보이는지 (폐기 카드 등은 false) |
| `multiplayerOnly` / `singleplayerOnly` | bool | false | (예전 형식) `multiplayer` 대신 쓰던 bool. 둘 중 하나라도 해당하면 참 |
| `names` | `{언어: 이름}` | `{}` | 로컬 전용. 레포의 카탈로그에는 없음 |

정규화 규칙:

- **타입·희귀도**는 적힌 그대로 저장하고, 엔진은 항상 대소문자를 가리지 않고 비교합니다. `DataPack.CardType`/`CardRarity`는 소문자(`attack`, `rare`)를 돌려줍니다.
- **풀 주인** (`CatalogCard.PoolOwner`): `character`가 있으면 그 값, 없으면 `CARD_POOL.X_CARD_POOL` → `CHARACTER.X`, 무색 풀 → `COLORLESS`, 공용 풀(이벤트·저주·토큰·상태이상·퀘스트·폐기) → null. `DataPack.PoolOwner(id)`/`IsColorless(id)`는 카탈로그가 없으면 무색 티어 팩으로 대신 판단합니다.
- **모드 제한** (`CatalogCard.IsAvailable(멀티 여부)`): 싱글에서는 `MultiplayerOnly` 제외, 멀티에서는 `SingleplayerOnly` 제외, `showInLibrary == false`는 항상 제외.
- 알 수 없는 필드는 무시합니다.

v0.107.1 캐릭터 풀 크기 (싱글플레이): 모든 캐릭터가 일반 20 / 고급 35 / 희귀 25 / 고대 2, 기본은 아이언클래드 3·나머지 4. 멀티플레이는 고급 36 / 희귀 26 (사일런트는 싱글 전용 고급 1장이 빠지고 멀티 전용 1장이 들어와 고급 35). 무색: 싱글 고급 32 / 희귀 21, 멀티 39 / 25. 디펙트 카드는 모두 88장.

## 9. 검증 규칙 (`DataPackValidator`)

`Validate(pack)`는 `ValidationIssue(Severity, Where, Message)` 목록을 돌려줍니다. `Where`는 `tiers[CHARACTER.DEFECT].cards[CARD.X].roles.block`처럼 문제 위치를 가리킵니다.

**오류(Error)**: 데이터가 틀렸습니다. 팩을 배포하면 안 됩니다.

| 대상 | 규칙 |
|---|---|
| ID | 카드·유물·캐릭터 ID가 §2.2 형식이 아님 (티어 팩 키, 아키타입 카드·유물·캐릭터, `guideHeroCard`, 콤보 카드·캐릭터, 별칭 키·값) |
| 티어 항목 | `roles` 값이 0..1 밖, `confidence`가 0..1 밖, `dupFactor`가 0..3 밖, `cycle`이 0..1 밖, `upgradedDelta`가 유한수가 아님, `actTier` 막 < 1, `cost` < 0 |
| 아키타입 | id 없음, **id 중복**, 카드 0장, `fullWeight` ≤ 0, `commitThreshold`가 (0, 1] 밖, `coreTarget` < 1, `essentialTarget` < 0, `roleWeight` 음수, 유물 `w` 음수, `needs`에 `tag`와 `role`이 모두 없음, `min` 음수 |
| 콤보 | 서로 다른 카드가 2장 미만 |
| `events.stats.json` | 선택률·`byAct` 값이 0..1 밖, `k` < 1, `byAct` 막 < 1 |
| `relics.json` | 유물 ID가 `RELIC.X`가 아님, `use` 없음(참/거짓 필수), `pickRate`가 0..1 밖, `tier`가 S..D나 null이 아님, `signal`이 `shop`이나 `ancient:<ID>`가 아님, `shopMedianPickRate`·`ancientNeutralPickRate`가 (0, 1] 밖 |
| 가중치 | `unknownConfidence`·`commitThreshold`·`hybridMargin`·`weakThreshold`·`skip.lowConfidence`가 0..1 밖, `completion.cardsPerReward` < 1, `poolSize` 값 ≤ 0, `cardOdds.*`의 `rare`/`uncommon`/`rareHighAscension`이 0..1 밖, `rareMultiplierHighAscension`이 0..1 밖, `pity`의 `start`/`cap`이 -1..1 밖이거나 `start > cap`, `pity` step이나 강화 확률이 0..1 밖, `shop.cardPrice` 값 ≤ 0, `priceJitterMin`이 0 이하이거나 `priceJitterMax`보다 큼, `colorlessPriceMultiplier` ≤ 0, `saleMultiplier`가 0..1 밖, `corePriority.bonus`·`starterBonus` 음수, `skip.basicPickBias` 값·`skip.lowConfidenceStep` 음수, `skip.basicShareFull`이 (0, 1] 밖, `leanMin`·`weakFactor`·`noCoreFactor`·`noCoreMaxUnlock`·`noCoreUnlockByAct`·`subBeforeEssential` 값이 0..1 밖, `leanFull`이 (0, 1] 밖, `essentialTarget`·`rewardsPerFloor` 음수, `floorsPerAct`·`runActs` < 1, `events.measuredBlend`가 0..1 밖, `events.measuredScale` 음수 |

**경고(Warning)**: 데이터가 덜 채워졌을 가능성이 큽니다.

| 대상 | 규칙 |
|---|---|
| 티어 | 알 수 없는 역할 키, `Tiers` 사전 키와 팩의 `character`가 다름 |
| 아키타입 | 카드에 티어 항목이 없음, 같은 카드를 두 번 적음, 캐릭터의 티어 팩이 없음, 알 수 없는 `roleWeight` 키·`needs.role`, `guideHeroCard`가 아키타입 카드가 아님, core 카드 0장(코어 먼저 규칙이 적용되지 않음), `coreTarget`이 core 카드 수보다 큼, `essentialTarget`이 필수 시너지 카드 수보다 큼 |
| 콤보 | 카드에 티어 항목이 없음, 캐릭터의 티어 팩이 없음, `w` ≤ 0 (효과 없음) |
| 별칭 | 데이터가 현재 ID 대신 옛 ID(별칭 키)를 씀, 별칭 연쇄 |
| 카탈로그 | (카탈로그가 있을 때) 티어·아키타입·콤보·별칭 대상 카드 ID가 카탈로그에 없음 |
| 가중치 | `tierValue`에 빠진 티어, 출처별 `rare + uncommon`이 1 초과, `corePriority.leanFull < leanMin`, `starterBonus > bonus`, `noCoreFactor`에 core·알 수 없는 역할 키 |

## 10. `events.stats.json` (측정 선택률, 선택)

상위 플레이어가 이벤트·고대에서 각 선택지를 고른 비율입니다(v2: OP.GG 랭커 A10 바닐라 솔로, 근거 [`docs/research/v2-stats/events.md`](research/v2-stats/events.md)). `EventScorer`가 효과 공식 점수에 섞습니다. 파일이 없으면 점수는 공식 그대로입니다.

```jsonc
{ "version": 1, "source": "opgg-top-a10-2026-11",
  "events":   { "ABYSSAL_BATHS": { "INITIAL": { "k": 2, "options": { "ABSTAIN": { "choiceRate": 0.648, "byAct": { "3": 0.7 } } } } } },
  "ancients": { "NEOW": { "LEAD_PAPERWEIGHT": { "pickRate": 0.62 } } } }
```

| 필드 | 타입 | 의미 |
|---|---|---|
| `events.<이벤트>.<페이지>.k` | 정수 ≥ 1 | 표본에서 그 페이지에 보인 선택지 수. 무작위로 고르면 `1/k` |
| `events.<이벤트>.<페이지>.options.<선택지>.choiceRate` | 0..1 | 그 페이지에서 이 선택지를 고른 비율 |
| `…options.<선택지>.byAct` | `{막: 0..1}` | 그 막의 비율이 전체와 안정적으로 다를 때만 적음. 그 막에서만 `choiceRate` 대신 씀 |
| `ancients.<고대>.<선택지>.pickRate` | 0..1 | 이 선택지가 제시됐을 때 고른 비율 |
| 그 밖 (`n`, `players`, `dirBoot`, `bandBoot`, `basis` …) | | 근거 자료. 실행 중에는 읽지 않음 |

- 키는 `events.json`의 선택지 키 `<이벤트>.pages.<페이지>.options.<선택지>`를 세 단계로 나눈 것입니다. 고대 선택지는 `<고대>.pages.INITIAL.options.<선택지>`(`NeowOptions`와 같은 이름, 유물 ID에서 `RELIC.`을 뺀 것)로 찾습니다.
- 점수: 보이는 페이지의 선택지마다 `점수 = 공식 + events.measuredBlend × events.measuredScale × (비율 − 무작위 비율)`. 무작위 비율은 이벤트 `1/k`, 고대 `1/제시된 선택지 수`(모르면 3). 항목이 없는 선택지는 공식 값 그대로이고, 다음 페이지 미리보기에는 섞지 않습니다. 설명에 "상위 플레이어 72% 선택"이 붙습니다.

## 11. `relics.json` (측정 유물 선택률, 선택)

유물은 원래 희귀도 값(`events.relicTier`)으로만 평가합니다. 이 파일은 상점 구매율과 고대 선택률로 그 값을 조정합니다(엘리트·보물·보스 보상 유물은 99.9% 가져가서 선택 신호가 없어 항목이 없습니다).

```jsonc
{ "version": 1, "source": "opgg-top-a10-2026-11", "shopMedianPickRate": 0.327, "ancientNeutralPickRate": 0.333,
  "relics": { "RELIC.MINIATURE_TENT": { "signal": "shop", "use": true, "pickRate": 0.645, "tier": "A" } } }
```

| 필드 | 타입 | 의미 |
|---|---|---|
| `shopMedianPickRate` | (0, 1] | 상점 유물 구매율의 중앙값. 구매는 골드에 묶여 있어 상점 비율은 이 값에 대한 비로만 씀 |
| `ancientNeutralPickRate` | (0, 1] | 고대에서 무작위로 고를 때의 비율(1/3) |
| `relics.<RELIC.X>.signal` | `shop` 또는 `ancient:<고대>` | 비율을 어디서 쟀는지 |
| `….use` | bool (필수) | 실행 중에 쓸지. `false` = 불확실 → 희귀도 값 유지 |
| `….pickRate` | 0..1 | 구매율 또는 선택률 |
| `….tier` | S..D 또는 null | 등급 구간이 안정할 때만 적은 참고 표시. 점수에는 쓰지 않음 |
| 그 밖 (`n`, `eff_n`, `players`, `bandBoot`, `presenceLift` …) | | 근거 자료 |

- 유물 값 = 희귀도 값 × `clamp(pickRate / 기준, 0.5, 1.5)` (`use: true`일 때만). 기준은 `signal: "shop"`이면 `shopMedianPickRate`, 아니면 `ancientNeutralPickRate`.
- 쓰는 곳: `EventScorer`가 ID를 아는 유물을 줄 때(`relic` 효과, 고대 선택지), `ShopScorer.RelicValue`. 상점 화면 인식기가 아직 유물을 읽지 않아 상점 추천 목록에는 유물 항목이 없습니다. `RelicValue`는 그 항목이 생기면 쓸 값입니다.