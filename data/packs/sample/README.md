# 샘플 데이터 팩 (형식 예시)

이 폴더는 **데이터 팩 파일 형식을 보여 주는 샘플**입니다.

- 티어, 역할 강도, 아키타입 구성, 콤보, 가중치 값은 모두 **자리표시자(placeholder)** 이며 실제 평가가 아닙니다. 모든 티어 항목은 `"source": "placeholder"`입니다.
- 카드 ID 일부(`CARD.BALL_LIGHTNING` 등)는 테스트 픽스처에 나오는 실제 ID를 빌렸고, `CARD.SAMPLE_*`는 가짜 ID입니다.
- 실제 티어 팩 v0는 **마일스톤 M2**에서 만듭니다. 카드 카탈로그는 이미 있습니다: [`data/catalog/cards.v0.107.1.json`](../../catalog/cards.v0.107.1.json) (이름 없는 ID·수치, 578장).
- 샘플 팩은 실제 카탈로그와 함께 검증할 수 있습니다: `DataPackLoader.LoadDirectory("data/packs/sample", catalogPath: "data/catalog/cards.v0.107.1.json")` → `DataPackValidator.Validate`. 이때 "not in the catalog" 경고는 가짜 `CARD.SAMPLE_*` ID에만 나와야 합니다.
- 가중치 기본값 중 카드 희귀도 확률(출처별), 희귀 보정치, 강화 확률, 상점 가격, 카드 제거 가격은 게임 코드로 확인한 값입니다([코드 확인], M0 스파이크 `SPIKE.md` §5). 이 샘플의 `weights.json`은 형식만 보여 주며 그 값들을 바꾸지 않습니다.
- 파일과 필드 설명: [`docs/DATA_SCHEMA.md`](../../../docs/DATA_SCHEMA.md)

| 파일 | 내용 |
|---|---|
| `weights.json` | 가중치 부분 덮어쓰기 |
| `tiers/defect.json`, `tiers/colorless.json` | 캐릭터별 티어 팩, 무색 티어 팩 |
| `archetypes/defect.json` | 아키타입 (최상위 객체 형태) |
| `combos/defect.json` | 콤보 (배열 형태) |
| `aliases.json` | 옛 ID → 현재 ID |
| `catalog.example.json` | 카탈로그 형식 예시(실제 카탈로그와 같은 필드). 로더가 읽지 않습니다. 실제 카탈로그는 `data/catalog/`에 있습니다. |
