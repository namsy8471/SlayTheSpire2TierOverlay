# Slay the Spire 2 티어 오버레이

슬레이 더 스파이어 2를 실시간으로 읽어 **카드 위에 티어 배지**를 띄우고, 보상·상점 화면에서 **지금 덱에 맞는 카드**와 **갈 길**을 추천하는 데스크톱 오버레이입니다.
**게임 모드를 쓰지 않습니다.** 게임 파일은 읽기만 하고, 세이브·로그 감시와 화면 캡처 인식으로 상태를 읽습니다.

- 설계: [docs/PLAN.md](docs/PLAN.md) · 기술 검증(M0) 결과: [docs/SPIKE.md](docs/SPIKE.md) · 경쟁 도구 분석: [docs/research/DECK_COMPASS.md](docs/research/DECK_COMPASS.md)
- 데이터 팩 형식: [docs/DATA_SCHEMA.md](docs/DATA_SCHEMA.md) · 현재 팩: [data/packs/v0.107.1](data/packs/v0.107.1/README.md) (초안)

## 구성

| 프로젝트 | 대상 | 역할 |
|---|---|---|
| `src/Sts2Overlay.Core` | net10.0 | 추천 엔진: 카드 점수, 스킵, 상점, 빌드 감지, **경로 추천(`Map/PathAdvisor`)**, 런 기록 리플레이 |
| `src/Sts2Overlay.GameState` | net10.0 | `current_run.save` 감시 + `godot.log` 꼬리 읽기 → 덱·HP·골드·맵·화면 힌트. 협동 손님(세이브 없음)은 로그로 재구성 |
| `src/Sts2Overlay.Vision` | net10.0 | 캡처 화면에서 보상·상점 판별, 카드 상자 탐색, 카드 그림 NCC 매칭, 강화 판별 |
| `src/Sts2Overlay.App` | net10.0-windows | WPF 오버레이: 게임 창 추적, GDI 캡처, 카드 위 배지, 패널(추천/덱/빌드/경로), 트레이, 단축키 |
| `tests/*` | | xUnit 테스트 (게임 파일이 필요한 테스트는 없으면 건너뜀) |
| `tools/CatalogBuilder` | | `sts2.dll` 정적 분석 → 카드 카탈로그, 시작 덱 |
| `tools/PckTool` | | `SlayTheSpire2.pck` 읽기 전용 추출 (카드 그림, 현지화) |
| `tools/VisionBench` | | 캡처 모음으로 인식 정확도 측정 (세이브의 `card_choices`로 자동 채점) |
| `tools/StateProbe` | | 세이브·로그 추적 결과를 실시간 출력 |
| `tools/RunHistoryAnalyzer` | | `.run` 기록을 엔진으로 리플레이해 "추천과 내 선택" 비교 |

## 처음 한 번: 로컬 리소스 준비

게임 이미지·텍스트는 레포에 없습니다. 사용자 PC에서 한 번 추출합니다 (`data/local/`, gitignore).

```bash
dotnet run --project tools/PckTool -- extract-images "images/packed/card_portraits/**/*.png" data/local/pck/card_art
```

```bash
dotnet run --project tools/PckTool -- extract "localization/kor/*.json" data/local/pck/localization/kor
```

```bash
dotnet run --project tools/CatalogBuilder -- --out data/local/catalog.v0.107.1.json
```

## 실행

```bash
dotnet run --project src/Sts2Overlay.App
```

- 게임을 켜 두면 창을 찾아 그 위에 패널과 배지를 띄웁니다. 보상·상점 화면일 때만 초당 3번 캡처합니다.
- 화면 없이 보기: `-- --demo reward` (또는 `shop`, `other`)
- UI 배율: 게임 해상도에 맞춰 자동 (FHD 100%, 4K 150%). 트레이 메뉴 "UI 배율"에서 바꿀 수 있습니다.
- 단축키: `Ctrl+F7` 패널 클릭 통과, `Ctrl+F8` 모두 숨기기, `Ctrl+F9` 캡처 저장, `Ctrl+F10` 지금 인식
- 기록: `%LOCALAPPDATA%\Sts2Overlay\app.log`, 설정: `settings.json`

## 빌드와 테스트

```bash
dotnet build SlayTheSpire2TierOverlay.sln
```

```bash
dotnet test SlayTheSpire2TierOverlay.sln
```

## 점검 도구

```bash
dotnet run --project tools/StateProbe -- --once
```

```bash
dotnet run --project tools/VisionBench -- bench
```

```bash
dotnet run --project tools/RunHistoryAnalyzer -- replay "%APPDATA%\SlayTheSpire2\steam\<id>\profile1\saves\history" --pack data/packs/v0.107.1 --out data/local/replay.md
```

튜닝에 쓰지 않은 런으로 데이터 팩을 검증 (홀드아웃·교차 검증, [팩 README §4.1](data/packs/v0.107.1/README.md)):

```bash
dotnet run --project tools/RunHistoryAnalyzer -- validate "%APPDATA%\SlayTheSpire2\steam\<id>\profile1\saves\history" --pack data/packs/v0.107.1 --player <id> --tuned-until 1794134406 --all-players --out data/local/holdout/validate.md
```

## 저작권

게임 텍스트·이미지는 레포에 넣지 않습니다. 데이터는 게임 내부 ID(`CARD.BALL_LIGHTNING` 등)와 우리 주석만 담고, 이름이 들어간 카탈로그와 카드 그림은 사용자 PC에서만 생성합니다.
