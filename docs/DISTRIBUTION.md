# 배포와 자동 업데이트

플레이어는 GitHub Releases에서 설치 파일을 받고, 앱은 같은 곳에서 새 버전을 받아 스스로 업데이트합니다. 설치·업데이트는 [Velopack](https://velopack.io/)이 맡습니다.
플레이어용 설명은 [README](../README.md#설치), 개발 환경은 [DEVELOPMENT.md](DEVELOPMENT.md)에 있습니다.

![자동 업데이트 흐름](images/auto-update.svg)

## 한눈에 보기

| 항목 | 값 |
|---|---|
| 배포 위치 | https://github.com/namsy8471/SlayTheSpire2TierOverlay/releases (공개 저장소) |
| 패키지 ID (`--packId`) | `namsy8471.Sts2Overlay` |
| 설치 파일 | `namsy8471.Sts2Overlay-win-Setup.exe` (서명 없음 → SmartScreen "추가 정보 → 실행") |
| 설치 위치 | `%LocalAppData%\namsy8471.Sts2Overlay` (사용자별, 관리자 권한 불필요). 시작 메뉴·바탕 화면 바로 가기 |
| 앱 파일 | 설치 위치의 `current\` (업데이트마다 통째로 바뀜). 버전은 `current\sq.version` |
| 설정·기록 | `%LOCALAPPDATA%\Sts2Overlay` (`settings.json`, `app.log`, `captures\`, `local\`). 업데이트·제거 후에도 남음 |
| 버전 | `Directory.Build.props`의 `<Version>0.1.0</Version>`이 기본값, 릴리스 때 태그(`v0.2.0` → `0.2.0`)가 `-p:Version`으로 덮어씀 |
| 지원 OS | Windows 10 2004 이상, x64 (`win-x64` self-contained) |

## 릴리스 흐름

```
release.cmd 0.2.0
  └─ tools\release.ps1: 검사 → 확인 → 태그 v0.2.0 만들고 push
        └─ GitHub Actions .github/workflows/release.yml (태그 v* push로 시작)
              1. 버전 확인 (SemVer 2, 같은 Release가 이미 있으면 나머지 건너뜀)
              2. dotnet test (Core, GameState, Resources, Vision, App 테스트, Release 구성)
              3. dotnet publish src/Sts2Overlay.App -c Release -r win-x64 --self-contained -o publish -p:Version=0.2.0
              4. dotnet tool restore  (vpk는 .config/dotnet-tools.json의 로컬 도구)
              5. dotnet vpk download github  (이전 릴리스를 받아 둠 → 델타 패키지용, 첫 릴리스면 건너뜀)
              6. dotnet vpk pack --packId namsy8471.Sts2Overlay --packVersion 0.2.0 --packDir publish --mainExe Sts2Overlay.exe ...
              7. dotnet vpk upload github --publish --tag v0.2.0  (Release 공개)
```

- 버전 이름에 `-`가 있으면(`0.2.0-beta.1`) 사전 릴리스(`--pre`)로 올립니다.
- Actions 화면의 **Run workflow**(workflow_dispatch)로 버전을 입력해 올릴 수도 있습니다. 이때는 그 커밋에 태그가 새로 붙습니다.
- publish는 단일 파일이 아니라 폴더입니다. Velopack이 폴더를 패키지(`.nupkg`)로 묶고, 이전 버전과의 차이로 델타 패키지를 만듭니다.
- Release에는 설치 파일(`…-win-Setup.exe`), 전체 패키지(`…-full.nupkg`), 델타 패키지(`…-delta.nupkg`), 피드 파일(`releases.win.json` 등)이 올라갑니다. 앱은 피드를 보고 델타가 있으면 델타만 받습니다.

## 릴리스 만들기

1. 올릴 커밋을 `main`에 합치고 push합니다.
2. 레포 맨 위의 `release.cmd`를 더블클릭하고 버전을 입력합니다(또는 `release.cmd 0.2.0`). 버전 앞에 `v`는 붙이지 않습니다.
3. 스크립트가 검사합니다: 추적 중인 파일에 커밋 안 된 변경이 없는지, HEAD가 upstream과 같은지(`git fetch` 후), 태그 `v<버전>`이 로컬·원격에 없는지, 이전 최신 태그보다 큰 버전인지.
4. 확인하면 태그를 만들어 push하고, Actions 진행 페이지 주소를 알려 줍니다. 몇 분 뒤 Release가 공개되고, 켜져 있는 오버레이들은 다음 확인 때(최대 6시간) 받아 갑니다.

| 옵션 | 동작 |
|---|---|
| `-DryRun` | 검사만 하고 실행할 명령을 보여 줍니다. 태그·push·업로드는 하지 않습니다 |
| `-Local` | Actions 대신 이 PC에서 테스트·publish·`vpk pack`·`vpk upload`까지 합니다. 태그는 `vpk upload --tag`가 GitHub에 만듭니다. 토큰은 `$env:GITHUB_TOKEN`, 없으면 `gh auth token`(화면에 찍지 않음). 이 태그로 Actions가 다시 돌아도 Release가 이미 있으면 건너뜁니다 |
| `-Yes` | 확인 질문을 건너뜁니다 |
| `-NoPause` | 끝나고 키 입력을 기다리지 않습니다 (자동화용) |

## 앱의 업데이트 동작

코드: `src/Sts2Overlay.App/Services/UpdateService.cs`, `UpdatePolicy.cs` (규칙은 `tests/Sts2Overlay.App.Tests/UpdatePolicyTests.cs`에서 테스트).

- **설치판에서만** 동작합니다(`UpdateManager.IsInstalled`). checkout, `dist\Sts2Overlay.exe`, 그냥 publish한 폴더는 업데이트하지 않고, 트레이 버전 옆에 "(개발용)"이 붙습니다.
- 시작하고 **15초 뒤** 한 번 확인하고, 그 뒤로는 30분마다 "확인할 때가 됐는지"만 보고 실제 확인은 **최대 6시간마다** 합니다. 트레이 "업데이트 확인"은 바로 확인하되 1분에 한 번까지입니다.
- 새 버전이 있으면 백그라운드에서 받고, 패널 아래 초록 줄("새 버전 vX 준비됨 — 종료하면 적용")과 트레이 "업데이트 적용 (재시작) vX"를 보여 줍니다.
- 적용: 트레이에서 누르면 설정을 저장하며 정상 종료한 뒤 Velopack이 파일을 바꾸고 다시 켭니다. 안 누르면 오버레이가 종료될 때 조용히 적용하고 다시 켜지 않습니다. **스스로 재시작하는 일은 없습니다.**
- 오프라인, GitHub 한도 초과, 피드 오류는 `app.log`에만 남고 다음 확인 때 다시 시도합니다.
- `settings.json`:
  - `"autoUpdate": false` — 자동 확인을 끕니다(트레이 "업데이트 확인"은 그대로).
  - `"updateUrl"` — 업데이트를 받을 곳. 비우면 공식 저장소. 다른 `https://github.com/<owner>/<repo>`, vpk 출력이 있는 http(s) 폴더, 로컬·네트워크 폴더 절대 경로(테스트용)를 받습니다. 잘못된 값이면 기록을 남기고 공식 저장소를 씁니다.

### 왜 6시간인가

GitHub의 익명 API는 **IP당 시간당 60회**까지입니다. 같은 공유기 뒤의 PC들은 이 한도를 나눠 씁니다. 오버레이를 자주 껐다 켜거나 여러 대가 켜 두어도 한도를 넘지 않도록, 시작할 때 한 번과 그 뒤 6시간 간격으로만 묻습니다. 앱이 외부와 하는 통신은 이 확인과 내려받기뿐입니다.

## 데이터 위치

exe 위치를 보고 데이터를 어디서 읽고 쓸지 정합니다(`AppLayout.Resolve`, `src/Sts2Overlay.App/Infrastructure/AppPaths.cs`, 테스트 `AppLayoutTests.cs`). 처음 맞는 규칙을 씁니다.

| 실행 위치 | 읽기 전용 데이터 (`data/packs`, `data/catalog`) | 생성 데이터 (`LocalDataDir`: 카드 그림·이름·`manifest.json`) |
|---|---|---|
| **checkout**: exe 폴더나 그 위에 `data/packs`와 `.sln` 또는 `.git`이 있음 (`dotnet run`, `bin\...`, `dist\Sts2Overlay.exe`) | 레포 루트 | 레포의 `data/local/` (gitignore) |
| **설치판**: exe 옆에 `data/packs` | exe 폴더 (`current\`) | `%LOCALAPPDATA%\Sts2Overlay\local` |
| 위에 `data/packs`만 있고 표시 파일 없음 (복사·압축 해제한 레포) | 그 폴더 | 그 폴더의 `data/local/` |
| 어디에도 없음 | exe 폴더 (팩 없음으로 시작) | `%LOCALAPPDATA%\Sts2Overlay\local` |

설치판의 생성 데이터를 설치 폴더 밖에 두는 이유: Velopack은 업데이트 때 `current\`를 통째로 바꾸므로, 거기에 두면 업데이트마다 게임에서 다시 추출해야 합니다. 설정·로그·캡처는 어느 경우든 `%LOCALAPPDATA%\Sts2Overlay`입니다.

## 업데이트를 로컬에서 시험하기

GitHub에 올리지 않고 폴더 하나로 설치 → 업데이트 → 적용까지 확인하는 방법입니다. 레포 맨 위에서 PowerShell로:

```powershell
$feed = "$env:TEMP\sts2-update-feed"
dotnet tool restore

# 1) 0.0.1을 만들어 설치
dotnet publish src/Sts2Overlay.App -c Release -r win-x64 --self-contained -o publish -p:Version=0.0.1
dotnet vpk pack --packId namsy8471.Sts2Overlay --packVersion 0.0.1 --packDir publish --mainExe Sts2Overlay.exe --outputDir $feed
& "$feed\namsy8471.Sts2Overlay-win-Setup.exe" --silent

# 2) 같은 폴더에 0.0.2를 만듦 (0.0.1과의 델타 패키지도 생김)
Remove-Item publish -Recurse -Force
dotnet publish src/Sts2Overlay.App -c Release -r win-x64 --self-contained -o publish -p:Version=0.0.2
dotnet vpk pack --packId namsy8471.Sts2Overlay --packVersion 0.0.2 --packDir publish --mainExe Sts2Overlay.exe --outputDir $feed
```

3. 오버레이를 모두 끈 뒤 `%LOCALAPPDATA%\Sts2Overlay\settings.json`에 `"updateUrl": "C:\\Users\\<나>\\AppData\\Local\\Temp\\sts2-update-feed"`를 넣습니다(JSON이라 `\`를 두 번).
4. 시작 메뉴의 바로 가기로 설치판(0.0.1)을 켭니다. 트레이에 "버전 0.0.1"이 보입니다.
5. 15초쯤 기다리면 `app.log`에 `update: v0.0.2 available; downloading` → `update: v0.0.2 downloaded`가 찍히고, 패널에 "새 버전 v0.0.2 준비됨", 트레이에 "업데이트 적용 (재시작) v0.0.2"가 나옵니다.
6. 트레이 "업데이트 적용 (재시작)"을 누르거나 그냥 종료합니다. 확인:
   ```powershell
   Get-Content "$env:LOCALAPPDATA\namsy8471.Sts2Overlay\current\sq.version"   # 0.0.2
   ```
   다시 켜면 트레이에 "버전 0.0.2".
7. 정리: Windows **설정 → 앱 → 설치된 앱**에서 "STS2 티어 오버레이"를 제거하고, `settings.json`의 `updateUrl`을 지우고, `$feed`와 `publish` 폴더를 지웁니다.

주의: `settings.json`은 checkout 실행과 설치판이 같이 씁니다. 시험이 끝나면 `updateUrl`을 꼭 지우세요(남아 있으면 설치판이 공식 릴리스 대신 그 폴더를 봅니다).
