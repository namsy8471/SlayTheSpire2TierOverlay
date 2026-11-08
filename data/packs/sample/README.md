# 샘플 데이터 팩 (형식 예시)

이 폴더는 **데이터 팩 파일 형식을 보여 주는 샘플**입니다.

- 티어, 역할 강도, 아키타입 구성, 콤보, 가중치 값은 모두 **자리표시자(placeholder)** 이며 실제 평가가 아닙니다. 모든 티어 항목은 `"source": "placeholder"`입니다.
- 카드 ID 일부(`CARD.BALL_LIGHTNING` 등)는 테스트 픽스처에 나오는 실제 ID를 빌렸고, `CARD.SAMPLE_*`는 가짜 ID입니다.
- 실제 티어 팩 v0는 카드 카탈로그 덤프 이후 **마일스톤 M2**에서 만듭니다.
- 파일과 필드 설명: [`docs/DATA_SCHEMA.md`](../../../docs/DATA_SCHEMA.md)

| 파일 | 내용 |
|---|---|
| `weights.json` | 가중치 부분 덮어쓰기 |
| `tiers/defect.json`, `tiers/colorless.json` | 캐릭터별 티어 팩, 무색 티어 팩 |
| `archetypes/defect.json` | 아키타입 (최상위 객체 형태) |
| `combos/defect.json` | 콤보 (배열 형태) |
| `aliases.json` | 옛 ID → 현재 ID |
| `catalog.example.json` | `catalog.json` 형식 예시. 로더가 읽지 않으며, 실제 카탈로그는 커밋하지 않습니다. |
