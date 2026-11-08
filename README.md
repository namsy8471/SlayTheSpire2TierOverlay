# Slay the Spire 2 티어 오버레이

슬레이 더 스파이어 2를 실시간으로 읽어 **카드 위에 티어 배지**를 띄우고, 보상·상점 화면에서 **지금 덱에 맞는 카드**를 추천하는 데스크톱 오버레이입니다.
- 설계: [docs/PLAN.md](docs/PLAN.md) · 경쟁 도구 분석: [docs/research/DECK_COMPASS.md](docs/research/DECK_COMPASS.md)
- 기술 검증(M0) 결과: [docs/SPIKE.md](docs/SPIKE.md) (작성 중)

## 구성

| 프로젝트 | 대상 | 역할 |
|---|---|---|
| `src/Sts2Overlay.Core` | net9.0 | 추천 엔진: 카드 점수, 스킵, 상점, 빌드 감지, 런 기록 파서, 리플레이 |
| `tests/*` | net9.0 | xUnit 테스트. 실제 런 기록 24개(익명화)를 픽스처로 사용 |
| `tools/RunHistoryAnalyzer` | net9.0 | `.run` 파일을 엔진으로 리플레이해 "추천과 내 선택" 리포트를 만드는 CLI |
| `data/catalog/` | | 게임 코드에서 뽑은 카드 카탈로그 (ID와 수치만, 이름·설명 없음) |
| `data/packs/` | | 데이터 팩 형식 샘플 ([docs/DATA_SCHEMA.md](docs/DATA_SCHEMA.md)) |

게임 모드는 쓰지 않습니다. 세이브·로그 감시와 화면 인식으로 게임 상태를 읽는 WPF 오버레이 앱(`Sts2Overlay.App`)은 이후 마일스톤에서 추가합니다.

## 빌드와 테스트

.NET 9 SDK(또는 .NET 9 런타임이 있는 .NET 10 SDK)가 필요합니다.

```bash
dotnet build SlayTheSpire2TierOverlay.sln
dotnet test SlayTheSpire2TierOverlay.sln
```

## 런 기록 분석

```bash
# 추천 엔진으로 내 런 기록을 리플레이 (데이터 팩 지정)
dotnet run --project tools/RunHistoryAnalyzer -- replay "%APPDATA%\SlayTheSpire2\steam\<id>\profile1\saves\history" --pack data/packs/sample --catalog data/catalog/cards.v0.107.1.json --out replay.md

# 캐릭터별 승률, 카드 선택률
dotnet run --project tools/RunHistoryAnalyzer -- stats "%APPDATA%\SlayTheSpire2\steam\<id>\profile1\saves\history"
```

## 저작권

게임 텍스트·이미지는 레포에 넣지 않습니다. 데이터는 게임 내부 ID(`CARD.BALL_LIGHTNING` 등)와 우리 주석만 담고, 카드 카탈로그는 사용자 PC에서 생성합니다.
