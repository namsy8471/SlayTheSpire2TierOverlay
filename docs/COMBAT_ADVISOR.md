# 전투 추천 순서 (combat advisor helper protocol)

전투 중(이벤트 방 안에서 시작하는 전투 포함) 패널 탭 위에 "이번 턴 추천 순서"(이번 턴에 낼 카드 순서)를 보여 줍니다. 순서 계산은 오버레이가 하지 않습니다.
**별도로 빌드되는 도우미 프로그램(helper)** 이 계산하고, 오버레이는 도우미를 실행해 표준 출력을 읽고 그리기만 합니다.
도우미의 소스는 이 레포에 없습니다(전투 시뮬레이터, 게임 로직 포함 금지). 오버레이도 추천만 하며 입력을 자동화하지 않습니다.

아이언클래드 표준 솔로 승천 0·10에서 비교용 전체 런의 전투 모델 R01을 사용합니다. 평가에는 v0.111 시뮬레이터를 썼으며 v0.113은 세이브 호환을 지원합니다. 도우미가 전투 시작 시 옆에 있는 `combat-value-R01.json`을 읽습니다. 승천 0의 포션 보존 가중치는 12, 승천 10은 18입니다. 실제 게임에서는 플레이어가 선택한 행동에 맞춰 다음 수를 다시 계산합니다.

오버레이의 비전투 추천은 `data/packs/v0.111.0/model/run-baseline/`의 카드 모델과 휴식·상점 보정값을 사용합니다. 평가 당시 함께 쓴 티어·빌드 표는 배포본의 v0.107.1 표와 같습니다. 지도는 이 표의 경로 규칙을 따릅니다.

이 문서는 도우미 쪽 구현을 대조할 수 있도록 프로토콜을 정리합니다. 오버레이 쪽 코드:

| 파일 | 역할 |
|---|---|
| `src/Sts2Overlay.App/Integration/CombatAdvice/AdvisorCommand.cs` | 도우미 찾기(설정 → exe 옆), 명령줄 나누기, `live` 인자 |
| `src/Sts2Overlay.App/Integration/CombatAdvice/AdvisorProcessHost.cs` | 자식 프로세스 실행·읽기·재시작·종료 |
| `src/Sts2Overlay.App/Integration/CombatAdvice/CombatAdviceMessage.cs` | 출력 한 줄 → 메시지 (`CombatAdviceParser`) |
| `src/Sts2Overlay.App/Controllers/CombatAdvicePresenter.cs` | 메시지 → 패널 행 (이름, 대상, 마지막 행) |
| `src/Sts2Overlay.App/Controllers/AppController.CombatAdvice.cs` | 언제 실행/중지하고 언제 보이는지 |
| `src/Sts2Overlay.App/Interop/KillOnCloseJob.cs` | 오버레이가 죽으면 도우미도 같이 끝나게 하는 job object |
| `tests/FakeCombatAdvisor` | 테스트용 가짜 도우미 (정해진 줄을 출력할 뿐, 게임 로직 없음) |

## 도우미 찾기

순서대로 찾고, 둘 다 없으면 기능이 조용히 꺼집니다(섹션 없음, 오류 없음, 트레이 항목 없음).

1. `settings.json`의 `"combatAdvisorCommand"`: exe의 전체 경로 + (선택) 앞에 붙일 인자. 공백이 있는 경로는 큰따옴표로 묶습니다.
   역슬래시는 그대로 쓰고, 따옴표 안의 `""`는 따옴표 문자 하나입니다.
   예: `"\"C:\\Program Files\\dotnet\\dotnet.exe\" C:\\tools\\Helper.dll"` (JSON 안이라 `\`와 `"`가 이스케이프됨).
   경로가 전체 경로가 아니거나 파일이 없으면 app.log에 경고를 한 번 남기고 2번으로 넘어갑니다.
2. 오버레이 exe 옆의 `advisor\Sts2CombatAdvisor.exe`. 빌드·릴리스가 이 폴더를 채우는 방법은
   [DISTRIBUTION.md "Combat advisor helper"](DISTRIBUTION.md#combat-advisor-helper)에 있습니다.

`"combatAdvisorEnabled"`(기본 `true`)와 트레이 메뉴 "전투 추천 순서 표시"로 켜고 끕니다. 설정은 시작할 때 읽습니다.

## 실행

```
<exe> [앞 인자…] live --save "<current_run.save 전체 경로>" --log "<godot.log 전체 경로>" --exit-on-stdin-eof
```

- 경로는 오버레이의 세이브 추적기가 지금 보고 있는 것입니다: `%APPDATA%\SlayTheSpire2\steam\<id>\profile<N>\saves\current_run.save`
  (싱글 플레이 세이브. 파일이 아직 없을 수 있음)와 `%APPDATA%\SlayTheSpire2\logs\godot.log`.
- **싱글 플레이 런이 진행 중일 때만** 실행합니다. 협동 런, 데모 모드, 런이 없을 때, 기능이 꺼졌을 때는 실행하지 않고, 실행 중이면 끝냅니다.
  세이브 폴더가 바뀌면(다른 프로필) 새 인자로 다시 실행합니다.
- 창 없음(CreateNoWindow), 작업 폴더는 exe의 폴더, 우선순위 BelowNormal.
- stdout: UTF-8(BOM 없음), 줄 단위로 읽습니다. 줄마다 flush 해 주세요.
- stderr: UTF-8, 오버레이의 app.log에 `combat advisor stderr: …`로 남깁니다(1분에 20줄까지, 넘치면 버린 줄 수만 기록).
- stdin: 열린 파이프이며 아무것도 쓰지 않습니다. 오버레이가 어떻게 끝나든 파이프가 닫히므로, `--exit-on-stdin-eof`를 받은
  도우미는 stdin EOF에서 스스로 끝나야 합니다. job object(아래)와 함께 쓰는 두 번째 안전장치입니다.
- 도우미가 스스로 끝나면 2초, 5초, 15초 뒤, 그다음부터는 60초마다 다시 실행합니다. 60초 넘게 돌다가 끝나면 다시 2초부터 셉니다.
  다시 실행할 때 마지막으로 받은 줄은 버립니다(섹션이 숨겨짐).
- 오버레이가 끝날 때(정상 종료, `AppDomain.ProcessExit`) 도우미를 프로세스 트리째 끝냅니다. 또 도우미는
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` job object에 들어가므로, 오버레이가 비정상 종료해도 Windows가 같이 끝냅니다.
  도우미가 띄운 자식 프로세스도 같은 job에 들어갑니다.

## 출력 (stdout)

한 줄에 JSON 객체 하나. 새 줄이 이전 줄을 대체합니다. 모르는 필드는 무시합니다.
`v`가 1이 아니거나 JSON 객체가 아니거나 `state`를 모르는 줄은 무시합니다(같은 종류의 문제는 app.log에 한 번만 기록). 빈 줄도 무시합니다.

### `idle`

```json
{"v":1,"state":"idle"}
```

따라가는 전투가 없음 → 섹션을 숨깁니다.

### `advice`

```json
{"v":1,"state":"advice","encounter":"JAW_WORM_WEAK","turn":2,"energy":3,"hp":61,"block":0,"played":1,
 "steps":[{"kind":"card","id":"BASH","upgraded":false,"hand":2,"target":"JAW_WORM","targetSlot":0},
          {"kind":"potion","id":"FIRE_POTION","target":"JAW_WORM","targetSlot":0},
          {"kind":"card","id":"DEFEND_IRONCLAD","upgraded":true,"hand":0}],
 "ending":"end_turn","confidence":"exact","note":"","ms":2.4}
```

(실제로는 한 줄)

| 필드 | 뜻 | 오버레이 표시 |
|---|---|---|
| `encounter` | 인카운터 모델 id | 표시 안 함 |
| `turn` | 턴 번호 | 제목 "이번 턴 추천 순서 · 2턴" |
| `energy`, `hp`, `block` | 지금 에너지·체력·방어도 | 제목 툴팁 |
| `played` | 이번 턴에 도우미가 이미 본 행동 수 (`steps`에 없음) | 제목 툴팁 |
| `steps` | **지금부터** 할 행동, 순서대로 | 번호 붙은 행 |
| `steps[].kind` | `card` / `potion`. 그 밖의 값은 `id`를 그대로 보여 줌 | 포션은 "포션" 태그 |
| `steps[].id` | godot.log에 찍히는 모델 id (`STRIKE_IRONCLAD`, `BASH`, `FIRE_POTION`). `id`가 없거나 빈 행동은 버림 | 카드: 카탈로그 이름(`CARD.` + id), 포션: 게임 현지화 이름 |
| `steps[].upgraded` | 강화된 카드 | 이름 뒤 `+` |
| `steps[].hand` | 손패에서 왼쪽부터 0부터 센 위치 (선택) | 행 툴팁 "손패 왼쪽에서 3번째 카드" |
| `steps[].target` | 대상 몬스터 모델 id (선택) | `→ 턱벌레` (게임 현지화 이름) |
| `steps[].targetSlot` | 살아 있는 적 중 왼쪽부터 0부터 센 위치 (선택) | 이름을 모르면 "2번째 적" / "enemy 2" |
| `steps[].targetAmbiguous` | 살아 있는 적 둘 이상이 같은 이름일 수 있음 (선택, 기본 false) | true이고 `targetSlot`이 있으면 "턱벌레 (2번째)" / "Jaw Worm (#2)" |
| `ending` | `end_turn`(행동 뒤 턴 종료), `win`(이 순서로 적을 모두 처치), `draw`(마지막 행동이 카드를 뽑거나 더미를 섞거나 무작위 결과를 내므로, 그 뒤는 그 행동이 끝난 다음 줄에서 알려 줌), `""`. 그 밖의 값은 `""`로 봄 | 마지막 행 "턴 종료" / "마무리!", `draw`면 흐린 행 "… 이 카드를 낸 뒤 다시 추천" / "… more after this play", `""`이면 없음 |
| `confidence` | `exact` / `uncertain` (계산한 손패가 실제와 다를 수 있음). `exact`가 아니면(없어도) 불확실로 봄 | 제목 옆 작은 "불확실" 태그 |
| `note` | 불확실한 이유 | "불확실" 태그 툴팁 (`draw`의 세 문장과 `event fight from …`은 해당 언어 문장으로 바꿔 보여 줌) |
| `ms` | 탐색에 걸린 시간 | 제목 툴팁 |

행은 최대 10개까지 보여 주고, 나머지는 "… N개 더" 한 줄로 줄입니다(패널이 손패를 가리지 않게).
`draw`로 끝나는 줄이 잘리면 멈추는 카드(마지막 행동)도 가려지므로, 두 행을 "… N개 더 낸 뒤 다시 추천" / "… N more, then new advice"
한 행으로 합칩니다("이 카드"가 10번째 행을 가리키지 않게).

### 공정성 규칙: 아직 모르는 정보는 보내지 않음

도우미는 **아직 뽑지 않은 카드나 아직 굴리지 않은 무작위 결과에 따라 달라지는 행동을 절대 보내면 안 됩니다.**
카드를 뽑거나, 뽑을 카드 더미를 섞거나, 무작위로 결과가 정해지는 행동이 나오면 줄을 거기서 끊고 `"ending":"draw"`를 보냅니다.
그 행동이 끝나 새 손패·결과가 로그에 나오면 다음 줄에서 이어서 추천합니다. 시뮬레이터는 시드로 뒤를 계산할 수 있지만,
그것을 보여 주면 플레이어가 아직 알 수 없는 카드를 미리 보는 셈이기 때문입니다(`draw`의 존재 이유).

`draw`일 때 `note`는 다음 셋 중 하나입니다. 오버레이는 이 영어 문장을 그대로 보여 주지 않고, 마지막 행의 툴팁을 해당 언어로 바꿔 보여 줍니다.
모르는 문장이면 "뽑거나 무작위로 정해질 결과에 따라 달라짐"이라는 일반 툴팁을 씁니다.

| `note` | 툴팁 (한국어) |
|---|---|
| `next steps depend on the cards drawn` | 다음 행동은 이 카드로 뽑을 카드에 따라 달라집니다. … |
| `next steps depend on the draw pile's new order` | 다음 행동은 섞인 뽑을 카드 더미의 새 순서에 따라 달라집니다. … |
| `next steps depend on a random result` | 다음 행동은 무작위 결과에 따라 달라집니다. … |

### `lost`, `unsupported`

```json
{"v":1,"state":"lost","encounter":"JAW_WORM_WEAK","reason":"..."}
{"v":1,"state":"unsupported","reason":"..."}
```

다음 `idle`/`advice`까지 흐린 한 줄을 보여 줍니다: "이 전투는 추적이 끊겼습니다" / "이 전투는 아직 지원하지 않습니다". `reason`은 툴팁.

## 언제 보이나

먼저 플레이어가 어디 있는지를 정하고(`CombatAdvicePresenter.PlaceOf`), 그다음 도우미의 마지막 줄을 봅니다.

| 어디 | 조건 | 섹션 |
|---|---|---|
| 전투 | 세이브·로그 추적기의 힌트가 `ScreenHint.Combat` | 마지막 줄이 `advice` / `lost` / `unsupported`면 표시, `idle`이거나 아직 줄이 없으면 숨김 |
| 이벤트 방 | 추적기가 이벤트 방이라고 봄 (`GameStateSnapshot.InEventRoom`) | 위와 같음. 단, 첫 턴 미리 보기는 "전투를 고르면" 표시(아래) |
| 그 밖 | 지도, 상점, 휴식처, 보상 화면, 메인 메뉴 등 | 마지막 줄과 관계없이 숨김 |

마지막 줄과 관계없이, 다음 중 하나면 항상 숨깁니다.

- 게임 프로세스(창)가 없음
- 도우미가 실행 중이 아님 (기능 꺼짐, 협동 런, 런 없음) 또는 도우미가 끝나 다시 실행되는 중

섹션은 패널의 다른 부분과 같은 규칙(게임이 앞에 있을 때만 표시, 게임 툴팁 위에서 비켜 줌, 방송·캡처 표시 설정)을 따릅니다.

### 이벤트에서 시작하는 전투

난타전(Punch Off)의 전투 선택지나 The Lantern Key 이벤트의 기사(`MYSTERIOUS_KNIGHT_EVENT_ENCOUNTER`)처럼, **이벤트 방 안에서 그대로 시작하는 전투**가 있습니다.
이런 전투는 godot.log에 `Preloading 'Combat Room'`도 `mode=ActiveCombat` 줄도 남지 않습니다. 남는 것은 이벤트 화면이 열릴 때 찍히는
배경용 전투 방 줄과, 그 뒤 전투 자체의 줄(`Player 1 playing card …`)뿐입니다.

```
Preloading 'Event Room' assets...
Creating NCombatRoom with mode=VisualOnly encounter=PUNCH_OFF_EVENT_ENCOUNTER.   ← 이벤트 화면이 열릴 때 (선택 전)
Player 1 playing card BASH (targeting …)                                          ← 전투 선택지를 고른 뒤 첫 카드
```

그래서 추적기의 힌트는 첫 카드를 낼 때까지 이벤트 방으로 남고(`GameStateTracker`는 첫 `playing card` 줄에서 `Combat`으로 바꿉니다),
그 사이에는 도우미의 상태를 믿습니다. 도우미는 따라가는 전투가 없으면 `idle`을 보내고 전투가 끝나면 다시 `idle`로 돌아가므로,
이벤트 방에서 `advice` / `lost` / `unsupported`가 왔다면 그 이벤트의 전투 이야기입니다.

**첫 턴 미리 보기.** 배경용 전투 방 줄은 플레이어가 선택지를 고르기 **전에** 찍힙니다. 도우미는 이때 이미 1턴 추천을 보내는데,
전투를 고를지 알 수 없으므로 `"confidence":"uncertain"`, `"played":0`이고 `note`는 다음과 같은 모양입니다.

```
event fight from PUNCH_OFF: assumes the options I_CAN_TAKE_THEM, then FIGHT
```

즉 이벤트 방에서는 플레이어가 아직 이벤트 글을 읽는 동안에도 섹션이 나타날 수 있습니다. 오버레이는 이 상태를 이렇게 다룹니다.

| 상태 | 조건 | 표시 |
|---|---|---|
| 미리 보기 | 이벤트 방 + `advice` + `uncertain` + `turn` 1 + `played` 0(또는 없음) | 제목 옆에 "불확실" 대신 흐린 "전투를 고르면" / "if you fight" 태그. 행(낼 순서)은 그대로 보여 줌 |
| 전투 중 | 위에서 하나라도 달라짐: `played` > 0, `turn` 2 이상, `confidence` `exact`. 또는 추적기 힌트가 이미 `Combat` | 보통 섹션 (불확실하면 "불확실" 태그) |

- "전투를 고르면" 태그의 툴팁: "이벤트에서 전투를 고른다고 가정한 첫 턴 추천입니다. 아직 고르기 전이라면 미리 보기로만 참고하세요. …"
- `event fight from`으로 시작하는 `note`는 영어 문장과 선택지 id를 그대로 보여 주지 않습니다. "불확실" 태그의 툴팁은
  "이벤트에서 시작한 전투라서, 이벤트에서 고른 선택지를 가정해 계산했습니다. …"로 바꿔 보여 줍니다.
- 전투를 고르지 않으면(다른 선택지, 이벤트를 떠남) 도우미가 `idle`을 보내거나 방이 바뀌면서 섹션이 사라집니다.
- 섹션은 탭 줄 **위에** 높이만 더합니다. 이벤트 조언(추천 탭의 선택지 순위)을 가리거나 탭을 바꾸지 않고,
  추천 탭 내용은 섹션 높이만큼 아래로 밀립니다. 패널이 화면 높이를 넘으면 탭 내용 쪽에 스크롤이 생깁니다(`PanelPlacement.ScrollMaxHeight`).
- 실제 전투의 1턴에서도 첫 행동을 하기 전까지는 도우미가 같은 줄을 보내므로 "전투를 고르면" 태그가 남아 있을 수 있습니다. 행은 그대로 유효합니다.

## 직접 해 보기 (가짜 도우미)

`tests/FakeCombatAdvisor`는 스크립트에 적힌 줄을 출력하기만 하는 가짜 도우미입니다(게임 로직 없음). 스크립트 문법은 `Program.cs` 위쪽 주석에 있습니다.

```powershell
dotnet build tests/FakeCombatAdvisor -m:4
```

`%LOCALAPPDATA%\Sts2Overlay\settings.json`에 (경로는 내 레포 위치로):

```json
"combatAdvisorCommand": "\"<레포>\\tests\\FakeCombatAdvisor\\bin\\Debug\\net10.0\\FakeCombatAdvisor.exe\" --script \"<레포>\\tests\\FakeCombatAdvisor\\sample-fight.txt\""
```

오버레이를 켜고 싱글 플레이 런에서 전투에 들어가면 `sample-fight.txt`의 줄(대기 → 1턴 추천 → 포션·불확실·마무리 → 뽑기에서 멈춤(`draw`) → 추적 끊김 → 지원 안 함
→ 대기 → 이벤트 전투 1턴 미리 보기 → 행동 뒤(불확실) → 2턴 마무리)이 몇 초 간격으로 반복됩니다.
이벤트 방에 들어가도 같은 줄이 보이며, 이때 "이벤트 전투 1턴 미리 보기" 줄에는 "불확실" 대신 "전투를 고르면" 태그가 붙습니다(전투 방에서는 "불확실").
가짜 도우미는 실제 전투와 상관없는 고정된 줄을 내므로 화면 확인용입니다. 다 보면 설정 줄을 지우세요.
