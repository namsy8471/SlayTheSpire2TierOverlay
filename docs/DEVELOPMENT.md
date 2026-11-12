# 개발 안내

플레이어용 안내는 [README](../README.md), 릴리스와 자동 업데이트는 [DISTRIBUTION.md](DISTRIBUTION.md)에 있습니다.
설계: [PLAN.md](PLAN.md) · 기술 검증(M0) 결과: [SPIKE.md](SPIKE.md) · 경쟁 도구 분석: [research/DECK_COMPASS.md](research/DECK_COMPASS.md) · 데이터 팩 형식: [DATA_SCHEMA.md](DATA_SCHEMA.md) · 현재 팩: [data/packs/v0.107.1](../data/packs/v0.107.1/README.md) (초안)

필요한 것: Windows, .NET 10 SDK, (게임 파일이 필요한 테스트·도구를 돌리려면) Slay the Spire 2 설치.

## 구성

| 프로젝트 | 대상 | 역할 |
|---|---|---|
| `src/Sts2Overlay.Core` | net10.0 | 추천 엔진: 카드 점수, 스킵, 상점, 빌드 감지, **경로 추천(`Map/PathAdvisor`)**, 휴식처·이벤트 조언, 런 기록 리플레이 |
| `src/Sts2Overlay.GameState` | net10.0 | `current_run.save` 감시 + `godot.log` 꼬리 읽기 → 덱·HP·골드·맵·화면 힌트. 협동 손님(세이브 없음)은 로그로 재구성 |
| `src/Sts2Overlay.Vision` | net10.0 | 캡처 화면에서 보상·상점·맵·휴식처·이벤트 판별, 카드 상자 탐색, 카드 그림 NCC 매칭, 강화 판별 |
| `src/Sts2Overlay.Resources` | net10.0 | 설치된 게임에서 로컬 리소스 자동 생성 (pck 추출, `sts2.dll` 카탈로그), 게임 버전·지문, 버전 경고 |
| `src/Sts2Overlay.App` | net10.0-windows | WPF 오버레이: 게임 창 추적, 화면 캡처(Windows.Graphics.Capture, 안 되면 GDI), 카드 위 배지, 맵·휴식처·이벤트 표시, 패널(추천/덱/빌드/경로/리뷰), 트레이, 단축키, 자동 업데이트(Velopack) |
| `tests/*` | | xUnit 테스트 (게임 파일이 필요한 테스트는 없으면 건너뜀) |
| `tests/FakeCombatAdvisor` | | 전투 추천 도우미의 가짜(정해진 줄만 출력). 프로세스 테스트와 손으로 확인할 때 씀 ([COMBAT_ADVISOR.md](COMBAT_ADVISOR.md)) |
| `tools/CatalogBuilder` | | `sts2.dll` 정적 분석 → 카드 카탈로그, 시작 덱 (수동 실행용) |
| `tools/PckTool` | | `SlayTheSpire2.pck` 읽기 전용 추출 (카드 그림, 현지화, 수동 실행용) |
| `tools/refresh-local.ps1` | | 로컬 리소스 다시 만들기 (`--prepare` 실행) |
| `tools/VisionBench` | | 캡처 모음으로 인식 정확도 측정 (세이브의 `card_choices`로 자동 채점) |
| `tools/MapBench`, `tools/RestBench` | | 맵 화면·휴식처 인식 측정 |
| `tools/StateProbe` | | 세이브·로그 추적 결과를 실시간 출력 |
| `tools/RunHistoryAnalyzer` | | `.run` 기록을 엔진으로 리플레이해 "추천과 내 선택" 비교 |
| `tools/PathStats` | | `.run` 기록에서 방 종류·막별 HP 손실, 골드, 휴식, 사망률을 재고 경로 가중치를 맞춘 뒤 이전 값과 백테스트 |
| `build-exe.cmd`, `tools/build-exe.ps1` | | 내 PC용 단일 exe(`dist\Sts2Overlay.exe`) 다시 만들기 |
| `release.cmd`, `tools/release.ps1` | | 새 버전 릴리스 ([DISTRIBUTION.md](DISTRIBUTION.md)) |

## 빌드와 테스트

```powershell
dotnet build SlayTheSpire2TierOverlay.sln
```

```powershell
dotnet test SlayTheSpire2TierOverlay.sln
```

CI(`.github/workflows/ci.yml`)는 Core·GameState·Resources 테스트를 Ubuntu에서, Vision·App 테스트와 App 빌드를 Windows에서 돌립니다.

## 실행

개발 중 바로 실행:

```powershell
dotnet run --project src/Sts2Overlay.App
```

- 게임을 켜 두면 창을 찾아 그 위에 패널과 배지를 띄웁니다. 보상·상점 화면일 가능성이 있을 때 초당 3번(`recognizeHz`), 평소에는 초당 1.5번(`idleRecognizeHz`) 캡처합니다. 로그가 보상·상점 화면을 예고한 뒤 아직 배지가 뜨지 않은 동안(최대 8초)은 초당 20번(`huntRecognizeHz`, 0 = 끔) 캡처해 화면이 뜨자마자 잡습니다.
- 화면 없이 보기: `-- --demo reward` (또는 `shop`, `other`). 트레이 "데모 모드"에서도 바꿀 수 있습니다.
- 오버레이를 스크린샷에 보이게: `-- --show-in-capture` (기본은 캡처에서 숨김, 레이아웃 확인용)
- 캡처 경로 점검: `-- --selftest` (2초 뒤 캡처 1장 저장 + 인식 1번, 결과는 app.log)
- 다른 데이터 팩·카탈로그: `-- --pack <폴더> --catalog <파일>`
- 리소스만 준비하고 끝내기: `-- --prepare [--force] [--game-dir <폴더>]`
- UI 배율: 게임 해상도에 맞춰 자동 (FHD 100%, 1440p 125%, 4K 150%). 트레이 메뉴 "UI 배율"에서 바꿀 수 있습니다.
- 단축키: `Ctrl+F5` 맵·휴식처 위 추천, `Ctrl+F6` 항상 맨 앞, `Ctrl+F7` 패널 클릭 통과, `Ctrl+F8` 모두 숨기기, `Ctrl+F9` 캡처 저장(`debugCaptures` 켰을 때만), `Ctrl+F10` 지금 인식, `Ctrl+F11` 인식 영역 표시(카드·가격 상자, 디버그)
- 기록: `%LOCALAPPDATA%\Sts2Overlay\app.log`, 설정: 같은 폴더의 `settings.json`, 캡처: `captures\` (`settings.json`의 `"debugCaptures": true`일 때만 남음. 꺼져 있으면 오버레이를 켤 때 폴더를 비우므로, Vision 테스트·VisionBench용 캡처는 이 값을 켜 두거나 다른 폴더에 보관하세요)
- 캡처 방식: 기본은 Windows.Graphics.Capture(4K 한 장 약 4 ms). 지원되지 않거나 실패하면 GDI(약 50 ms)로 자동 전환합니다. `settings.json`의 `"captureBackend": "gdi"`로 GDI를 강제할 수 있고, 실제로 쓰인 방식은 app.log의 `capture backend` 줄에 남습니다.
- 전투 추천 순서: 별도 도우미 프로그램(`advisor\Sts2CombatAdvisor.exe` 또는 `settings.json`의 `"combatAdvisorCommand"`)이 있으면 전투 중 탭 위에 "이번 턴 추천 순서"를 보여 줍니다. 없으면 조용히 꺼집니다. 프로토콜과 가짜 도우미로 확인하는 법은 [COMBAT_ADVISOR.md](COMBAT_ADVISOR.md).
- 한 번에 하나만 실행됩니다(뮤텍스 `Local\Sts2Overlay.App`). `--prepare`, `--bench-capture`, `--verify-capture`는 오버레이가 켜져 있어도 돌릴 수 있습니다.

### 내 PC용 exe (`build-exe.cmd`)

레포 맨 위의 `build-exe.cmd`를 더블클릭하면 원격의 최신 코드를 받아(`git pull --ff-only`) `dist\Sts2Overlay.exe`를 다시 만듭니다. 받기에 실패하면(오프라인, 로컬 변경 등) 경고만 띄우고 지금 폴더의 코드로 빌드합니다. 빌드가 실패하면 `dist`는 그대로 둡니다.

| 옵션 | 동작 |
|---|---|
| (없음) | 받기 → 빌드 → exe 교체. 오버레이가 켜져 있으면 다시 켤지 묻습니다 |
| `-NoPull` | 원격에서 받지 않고 지금 코드로 빌드 |
| `-Restart` | 묻지 않고 켜져 있는 오버레이를 새 exe로 다시 켬 |
| `-NoRestart` | 켜져 있는 오버레이를 건드리지 않음 (다음에 켤 때 새 exe) |
| `-NoPause` | 끝나고 키 입력을 기다리지 않음 (자동화용, 재시작도 안 함) |

켜져 있는 exe도 이름은 바꿀 수 있어서, 이전 exe를 `Sts2Overlay.prev.exe`로 옮기고 새 exe를 넣습니다. 안에서 하는 일은 아래 명령과 같습니다(임시 폴더로 publish한 뒤 교체):

```powershell
dotnet publish src/Sts2Overlay.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o dist
```

`dist\Sts2Overlay.exe`는 위 폴더로 올라가며 `data/packs`를 찾으므로 `dist`는 레포 안에 둡니다. 이 exe는 설치판이 아니라서 자동 업데이트를 하지 않습니다(트레이 버전 옆에 "(개발용)").

## 로컬 리소스 (자동)

게임 이미지·텍스트는 레포에 없습니다. **앱을 켜면 설치된 게임에서 자동으로 추출**합니다 (약 2초).

- 저장 위치(`AppPaths.LocalDataDir`): 레포 checkout에서 실행하면(`dotnet run`, `bin\...`, `dist\Sts2Overlay.exe`) 레포의 `data/local/`(gitignore), 설치판이면 `%LOCALAPPDATA%\Sts2Overlay\local`. 자세한 규칙은 [DISTRIBUTION.md](DISTRIBUTION.md#데이터-위치).
- 게임 버전은 설치 폴더의 `release_info.json`(게임이 직접 읽는 파일)에서 읽고, `sts2.dll` 해시와 `SlayTheSpire2.pck` 크기·수정 시각을 지문으로 `manifest.json`에 저장합니다. 게임이 업데이트되어 지문이 바뀌면 다음 실행 때 다시 만듭니다. 그대로면 아무것도 하지 않습니다.
- 진행 상황과 경고는 패널 아래 주황색 줄에 나옵니다. 게임 버전이 데이터 팩(`data/packs/v0.107.1`)과 다르면 "게임 버전 …이 데이터 팩 …보다 새롭습니다"와 티어 없는 새 카드 목록(기본 티어로 계산)을 보여 줍니다.
- 게임 폴더를 못 찾으면 `settings.json`에 `"gameDir"`을 적거나 `--game-dir <폴더>`를 쓰세요 (Steam 라이브러리와 환경 변수 `STS2_GAME_DIR`은 자동 검색).

미리 만들거나 강제로 다시 만들기:

```powershell
pwsh tools/refresh-local.ps1          # 바뀐 경우에만
pwsh tools/refresh-local.ps1 -Force   # 항상 다시
```

추출 코드는 `src/Sts2Overlay.Resources`에 있고, `tools/PckTool`·`tools/CatalogBuilder`는 같은 코드를 쓰는 수동 도구로 남아 있습니다.

## 점검 도구

```powershell
dotnet run --project tools/StateProbe -- --once
```

캡처 속도 비교(GDI 대 WGC, 창을 띄우지 않음)와 정확도 검증(WGC 결과가 GDI·저장된 캡처와 픽셀 단위로 같은지, 인식 결과가 같은지, 크기 변경·창 닫힘 처리):

```powershell
dotnet run --project src/Sts2Overlay.App -- --bench-capture 60
```

```powershell
dotnet run --project src/Sts2Overlay.App -- --verify-capture 16
```

```powershell
dotnet run --project tools/VisionBench -- bench
```

```powershell
dotnet run --project tools/RunHistoryAnalyzer -- replay "%APPDATA%\SlayTheSpire2\steam\<id>\profile1\saves\history" --pack data/packs/v0.107.1 --out data/local/replay.md
```

튜닝에 쓰지 않은 런으로 데이터 팩을 검증 (홀드아웃·교차 검증, [팩 README §4.1](../data/packs/v0.107.1/README.md)):

```powershell
dotnet run --project tools/RunHistoryAnalyzer -- validate "%APPDATA%\SlayTheSpire2\steam\<id>\profile1\saves\history" --pack data/packs/v0.107.1 --player <id> --tuned-until 1794134406 --all-players --out data/local/holdout/validate.md
```

경로 가중치 측정·백테스트 (협동 런은 두 계정 폴더에 같은 런이 있어도 한 번만 셉니다):

```powershell
dotnet run --project tools/PathStats -- "%APPDATA%\SlayTheSpire2\steam\<id>\profile1\saves\history" --pack data/packs/v0.107.1 --map tests/Sts2Overlay.Core.Tests/Map/Fixtures/map_act2.json
```

결과는 `data/local/path/`(stats.md, stats.json, backtest.md, backtest.json)에 생깁니다. 측정값은 `PathWeights`의 `[측정]` 주석에 표본 수와 함께 옮겨 둡니다.

빌드 완성 확률(몬테카를로) 보정 확인: 기록의 체크포인트마다 예측한 확률과 실제 마지막 덱을 비교합니다.
이벤트 카드 비율·막별 사망률·보정 곡선은 해당 런을 뺀 나머지 런으로 맞춥니다(leave-one-run-out). 협동 기록이 있으면 Steam 폴더를 모두 넘기세요.
`--emit`은 모든 런으로 맞춘 기본값(`ForecastFit`, `ForecastCalibration.Default`에 붙여 넣을 값)을 출력합니다.

```powershell
dotnet run --project tools/RunHistoryAnalyzer -c Release -- calibrate "%APPDATA%\SlayTheSpire2\steam\<id>\profile1\saves\history" --pack data/packs/v0.107.1 --sims 1000 [--emit]
```

점수 가중치를 같은 방식으로 탐색 (fold마다 다시 맞추고, 현재 값 쪽으로 규제):

```powershell
dotnet run --project tools/RunHistoryAnalyzer -- tune-weights "%APPDATA%\SlayTheSpire2\steam\<id>\profile1\saves\history" --pack data/packs/v0.107.1 --player <id> --tuned-until 1794134406 --all-players --lambda 0.005
```

휴식처 조언(휴식 vs 강화, 강화할 카드)을 기록의 선택과 비교 (일치율은 대리 지표, 남은 길은 실제로 걸은 방):

```powershell
dotnet run --project tools/RunHistoryAnalyzer -- rest-advice "%APPDATA%\SlayTheSpire2\steam\<id>\profile1\saves\history" --pack data/packs/v0.107.1 [--all-players] [--out rest.md]
```

## 저작권

게임 텍스트·이미지는 레포에 넣지 않습니다. 데이터는 게임 내부 ID(`CARD.BALL_LIGHTNING` 등)와 우리 주석만 담고, 이름이 들어간 카탈로그와 카드 그림은 사용자 PC에서만 생성합니다(`.gitignore`가 `*.pck`, `sts2.dll`, `catalog.*.json`, `data/local/`을 막습니다).
README와 `docs/images/`의 그림도 같은 원칙입니다. 스크린샷은 카드 이름을 가린 예시 카탈로그로 데모 모드를 렌더링했고, 카드 그림은 넣지 않았습니다.
