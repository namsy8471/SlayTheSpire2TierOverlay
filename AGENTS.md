# AGENTS.md: STS2 티어 오버레이

이 저장소에서 일하는 코딩 에이전트(Codex 등)와 사람이 지키는 규칙입니다. 코드를 고치기 전에 끝까지 읽으세요.

이 저장소는 **공개** 저장소입니다. 이 PC에만 있는 전체 인수인계 문서(작업 현황, 다음 할 일, 로컬 폴더 구조)는 저장소 밖의 `.worktrees/HANDOVER/HANDOVER.ko.md`에 있습니다. 그 파일이 보이면 먼저 읽습니다.

## 1. 무엇을 만드는가

Slay the Spire 2 위에 떠서 조언을 보여 주는 Windows 프로그램(WPF, .NET 10)입니다.

- 게임을 고치지 않습니다(모드 없음). 상태는 세이브 파일, `godot.log`, 화면 캡처 인식으로만 읽습니다. 모드 방식을 다시 꺼내려면 먼저 저장소 주인에게 묻습니다.
- 추천만 합니다. 게임을 대신 조작하지 않습니다.
- 추천은 플레이어가 알 수 없는 정보(뽑을 카드 순서, 앞으로의 난수)를 쓰거나 보여 주지 않습니다.
- 화면에 나오는 글은 모두 한국어와 영어 두 가지로 넣습니다: `Loc.T("한국어", "English")`.
- 게임 기준 버전은 v0.111 베타입니다. 데이터 팩은 `data/packs/v0.107.1`과 `data/packs/v0.111.0`이고, 앱이 게임 버전에 맞는 것을 고릅니다.

구성, 실행 방법, 도구 목록은 [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md), 릴리스와 자동 업데이트는 [docs/DISTRIBUTION.md](docs/DISTRIBUTION.md), 전투 도우미와의 약속은 [docs/COMBAT_ADVISOR.md](docs/COMBAT_ADVISOR.md)에 있습니다.

## 2. 절대 커밋하지 않는 것

- Steam ID(17자리 숫자), 이메일 주소, 사용자 폴더 경로, Windows 계정 이름.
- 플레이 기록: 세이브, `godot.log`, `.run`, 판 번호가 든 jsonl/csv, 다른 플레이어의 이름이나 ID.
- 게임 파일과 게임에서 뽑은 그림·글(`*.pck`, `sts2.dll`, `data/local/`, 디컴파일한 소스). `.gitignore`가 막고 있으니 `git add -f`로 넣지 않습니다.
- 시뮬레이션 엔진 소스. 전투 도우미는 컴파일된 파일 3개만 릴리스에 들어가고, 그 파일도 git에는 넣지 않습니다(`advisor-dist/`는 무시됨).

공개해도 되는 것은 가중치와 합계 숫자뿐입니다. PR 본문과 댓글도 공개되니 같은 기준으로 씁니다. 로그나 경로를 그대로 붙이지 않습니다.

## 3. 커밋과 PR

- 커밋 메일은 `31647755+namsy8471@users.noreply.github.com` 하나만 씁니다. 새 클론에서는 먼저 `git config user.email`을 맞춥니다.
- 커밋 메시지에 AI 모델 이름을 쓰지 않습니다.
- 푸시 전에 확인합니다.

```bash
git log --format='%ae %ce' origin/main..HEAD | sort -u
```

```bash
git log --format=%B origin/main..HEAD | grep -i "opus\|sonnet\|fable\|haiku"
```

첫 명령은 위의 메일 하나만, 둘째 명령은 아무것도 출력하지 않아야 합니다.

- PR은 **초안(draft)** 으로 엽니다. "Ready for review" 누르기와 병합은 저장소 주인만 합니다. 에이전트는 병합하지 않습니다.
- 푸시하기 전에 그 PR이 이미 병합됐는지 봅니다(`gh pr view <번호> --json state`). 병합된 브랜치에는 다시 푸시하지 않고 새 브랜치와 새 PR을 만듭니다.
- 강제 푸시와 이력 고쳐 쓰기를 하지 않습니다.
- PR 설명은 한국어로, 배우는 사람 눈높이로 씁니다: 바꾸기 전과 후의 코드 조각, 그렇게 바꾼 이유, "꼭 이해하고 넘어갈 줄" 1~3개(`파일:줄`), 끝에 `### 배운 개념` 2~4개.
- 문서만 바꾼 PR은 코드 테스트를 다시 돌리지 않아도 됩니다.
- GitHub Actions는 2026-12-01까지 돌지 않습니다(무료 시간 소진). 그동안은 로컬에서 돌린 테스트 결과를 PR 본문에 적습니다.

## 4. 빌드와 테스트

Windows와 .NET 10 SDK가 필요합니다.

```powershell
dotnet build SlayTheSpire2TierOverlay.sln
```

```powershell
dotnet test SlayTheSpire2TierOverlay.sln
```

- 테스트 프로젝트는 Core, GameState, Resources, Vision, App 다섯 개입니다. Vision은 오래 걸립니다(2스레드로 약 15분). 고친 곳의 테스트를 `--filter`로 먼저 돌리고, PR 전에 관련 프로젝트를 한 번 돌립니다.
- CI는 Core·GameState·Resources를 Linux에서 돌립니다. Windows 경로에 기대는 테스트는 `WindowsFact`를 씁니다.
- 게임 파일이 필요한 테스트는 게임이 없으면 건너뜁니다.
- 알려진 문제: `%LOCALAPPDATA%\Sts2Overlay\captures` 폴더가 빈 채로 있으면 `RestSiteRealFrameTests`가 로컬에서 실패합니다.
- 이 PC에서는 CPU를 아껴 씁니다. 빌드와 테스트는 2~4스레드(`dotnet test -m:2`)로 돌립니다.
- 주인이 게임을 하는 동안에는 오버레이 창을 띄우지 않습니다.
- 인식, 강화 판별, 가격 읽기, 세이브·로그 추적처럼 정답이 있는 것은 측정값이 100 %가 될 때까지 고칩니다. "추천이 플레이어의 선택과 같은 비율"은 100 %를 목표로 하지 않습니다.
- 전문가 티어 목록과 상위 플레이어의 실제 선택이 다르면 실제 선택을 따릅니다.

## 5. 릴리스

- 릴리스는 저장소 주인이 직접 말할 때만 준비하고, 공개 명령은 주인이 자기 터미널에서 실행합니다. 에이전트는 미리 실행(`-DryRun`)까지만 합니다.
- 버전은 태그에서만 정해집니다. 태그는 `origin/main`과 같은 이력의 깨끗한 클론에서만 만듭니다.
- Velopack packId `namsy8471.Sts2Overlay`는 바꾸지 않습니다. 바꾸면 기존 사용자의 자동 업데이트가 끊깁니다.
- 릴리스 노트는 한국어로, 플레이어 입장에서 씁니다. PR 번호나 내부 용어를 쓰지 않고, 끝에 짧은 영어 요약을 붙입니다.
- 명령과 순서는 [docs/DISTRIBUTION.md](docs/DISTRIBUTION.md)와 `tools/release.ps1`에 있습니다.

## 6. 작업 폴더

- 작업용 클론과 임시 폴더는 저장소 안의 `.worktrees/` 아래에만 만듭니다(git이 무시함). 바탕화면이나 저장소 밖에 폴더를 만들지 않습니다.
- 끝난 작업의 클론과 빌드 결과물은 지웁니다. 수집한 데이터와 플레이 기록은 주인에게 묻기 전에는 지우지 않습니다.
