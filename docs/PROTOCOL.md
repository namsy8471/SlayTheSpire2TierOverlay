# Bridge 프로토콜 (모드 ↔ 앱)

게임 안의 Bridge 모드(`Sts2Overlay.Bridge`)와 오버레이 앱(`Sts2Overlay.App`)이 주고받는 메시지 규약입니다.
C# 정의는 `src/Sts2Overlay.Contracts`에 있고, 모드와 앱이 같은 DLL을 참조합니다. 이 문서와 코드가 다르면 **코드가 기준**입니다.

- 현재 프로토콜 버전: **1** (`ProtocolVersion.Current`)
- 직렬화: `BridgeJson.Serialize` / `BridgeJson.Deserialize` (System.Text.Json 소스 생성기, 리플렉션 없음)

---

## 1. 전송 방식

| 항목 | 값 |
|---|---|
| 프로토콜 | WebSocket, 텍스트 프레임 1개 = JSON 메시지 1개 (UTF-8) |
| 주소 | `ws://127.0.0.1:<port>/bridge` (`BridgeTransport.BuildUri(port)`) |
| 서버 | 모드 (게임 프로세스 안). `127.0.0.1`에만 바인딩, 포트는 OS가 정하는 임의 포트 |
| 클라이언트 | 앱. 동시에 1개 연결만 받습니다. 새 연결이 오면 이전 연결을 닫습니다. |

### 1.1 연결 정보 파일

모드는 서버를 연 직후 `%LOCALAPPDATA%\Sts2Overlay\bridge.json`을 씁니다 (`BridgeConnectionFile.DefaultPath`).

```json
{
  "port": 51234,
  "token": "q9yQk3...43자 base64url...",
  "pid": 18244,
  "protocol": 1,
  "startedAt": "2026-11-08T21:30:00+09:00"
}
```

- `BridgeConnectionFile.Write(path, info)`: 임시 파일에 쓴 뒤 이름을 바꿔서 원자적으로 교체합니다. 읽는 쪽이 반쯤 쓰인 파일을 보지 않습니다.
- `BridgeConnectionFile.TryRead(path)`: 파일이 없거나 깨졌거나 `port`/`token`이 이상하면 `null`을 돌려줍니다 (예외 없음).
- 모드는 게임이 정상 종료될 때 `TryDelete`로 파일을 지웁니다. 게임이 비정상 종료되어 파일이 남으면, 앱은 `pid` 프로세스가 살아 있는지 확인해서 오래된 파일을 걸러냅니다.
- 앱은 파일 변경을 감시하다가 새 파일이 생기면 다시 연결합니다.

### 1.2 보안

- **루프백 전용**: 서버는 `127.0.0.1`에만 바인딩합니다. 외부 네트워크에서는 접근할 수 없습니다.
- **토큰**: 모드가 시작할 때마다 `BridgeConnectionFile.GenerateToken()`으로 256비트 무작위 토큰을 만듭니다. 앱은 WebSocket 업그레이드 요청에 `X-Sts2Overlay-Token: <token>` 헤더(`BridgeTransport.TokenHeader`)를 붙입니다. 토큰이 없거나 다르면 모드는 HTTP 401로 거절합니다. 비교는 고정 시간 비교(`CryptographicOperations.FixedTimeEquals`)를 씁니다.
- **Origin 거부**: 요청에 `Origin` 헤더가 있으면 무조건 거절합니다(HTTP 403). 브라우저는 항상 `Origin`을 붙이므로, 웹페이지가 localhost 포트를 찔러보는 공격을 막습니다. 앱의 `ClientWebSocket`은 `Origin`을 보내지 않습니다.
- **경로 확인**: `/bridge` 외의 경로는 404로 거절합니다.
- 토큰 파일은 사용자 프로필(`%LOCALAPPDATA%`) 안에 있으므로 같은 Windows 사용자만 읽을 수 있습니다.

### 1.3 연결 순서

1. 앱이 `bridge.json`을 읽고 토큰 헤더를 붙여 연결합니다.
2. 모드가 `hello`를 보냅니다.
3. 앱은 `hello.protocol`을 확인합니다 (`ProtocolVersion.IsSupported`). 지원하지 않는 버전이면 연결을 끊고 "모드 업데이트 필요"를 표시합니다.
4. 모드는 런이 진행 중이면 곧바로 `run.snapshot`을 보내고, 보상/상점 화면이 열려 있으면 `screen.opened`도 보냅니다.
5. 이후 상태가 바뀔 때마다 모드가 메시지를 보내고, 앱은 `badges.set`/`badges.clear`로 응답합니다.

---

## 2. 메시지 공통 형식 (봉투)

모든 메시지는 JSON 객체 하나이며, 다음 세 필드를 항상 가집니다.

| 필드 | 타입 | 설명 |
|---|---|---|
| `type` | string | 메시지 종류. `MessageTypes` 상수. 직렬화할 때 항상 첫 번째 필드로 씁니다. |
| `protocol` | int | 보낸 쪽의 프로토콜 버전 (현재 1) |
| `seq` | long | 보낸 쪽이 연결마다 1부터 1씩 늘리는 번호. 방향마다 따로 셉니다. |

C#에서는 `abstract record BridgeMessage`(`Protocol`, `Seq`)를 상속한 `sealed record`로 표현합니다.
`type` 값은 `[JsonPolymorphic]` / `[JsonDerivedType]`이 자동으로 붙이고 읽습니다.

### 2.1 JSON 규칙

- 필드 이름은 **camelCase**입니다.
- 값이 `null`인 선택 필드는 **쓰지 않습니다** (생략). 받는 쪽은 생략과 `null`을 똑같이 취급합니다.
- 받는 쪽은 **모르는 필드를 무시**합니다.
- `type`이 객체 중간이나 끝에 있어도 읽습니다.
- 열거형 같은 값(`screen`, `context`, `event`, 맵 노드 `type` 등)은 C# enum이 아니라 **문자열**입니다. 새 값이 추가되어도 파싱이 깨지지 않게 하기 위해서입니다. 모르는 값은 `other`처럼 취급합니다.
- 좌표(`rect`)는 `viewport` 크기 기준 픽셀이며, 왼쪽 위가 원점입니다.
- 게임 콘텐츠는 **모델 ID**로만 주고받습니다 (`CARD.BALL_LIGHTNING`, `RELIC.CRACKED_CORE`, `CHARACTER.DEFECT`). 카드 이름이나 설명 텍스트는 보내지 않습니다.

### 2.2 버전 규칙

- **같은 프로토콜 버전 안에서는 추가만 합니다**: 새 선택 필드, 새 메시지 `type`, 문자열 값(`screen` 종류 등)의 새 값.
- 필드 삭제, 이름 변경, 타입 변경, 의미 변경은 **호환이 깨지는 변경**이며 `ProtocolVersion.Current`를 올려야 합니다.
- **모르는 `type`은 무시합니다.** `BridgeJson.Deserialize`는 모르는 `type`, `type` 없음, 깨진 JSON에 대해 예외 대신 `null`을 돌려줍니다. 받는 쪽은 `null`이면 그 메시지를 버리고 연결은 유지합니다. 덕분에 오래된 앱이 새 모드와 붙어도 죽지 않습니다.
- 빠진 필드는 기본값이 됩니다 (숫자 0, 목록 `[]`, 문자열 `""`, `act`/`floor`는 1, `protocol`은 1).

---

## 3. 모드 → 앱

### 3.1 `hello`

연결 직후 한 번 보냅니다. (`HelloMessage`)

| 필드 | 타입 | 설명 |
|---|---|---|
| `gameVersion` | string | `release_info.json`의 version |
| `gameCommit` | string? | `release_info.json`의 commit |
| `modVersion` | string | Bridge 모드 버전 |
| `isMultiplayer` | bool | 협동(멀티) 런이면 true |
| `localPlayerId` | string? | 멀티에서 내 플레이어 ID. 싱글이면 생략 |

```json
{
  "type": "hello",
  "protocol": 1,
  "seq": 1,
  "gameVersion": "0.98.3",
  "gameCommit": "abc1234",
  "modVersion": "0.1.0",
  "isMultiplayer": false
}
```

### 3.2 `run.snapshot`

내 런의 전체 상태입니다. 연결 직후, 런 시작 시, 그리고 덱·유물·HP·골드·층이 바뀔 때마다 보냅니다. 멀티에서도 **내(로컬 플레이어) 상태만** 보냅니다. (`RunSnapshotMessage`)

| 필드 | 타입 | 설명 |
|---|---|---|
| `character` | string | 캐릭터 모델 ID |
| `ascension` | int | 승천 단계 |
| `act` | int | 막, **1부터** |
| `floor` | int | 전체 층, **1부터**, 막을 넘어가며 계속 셉니다 (`.run`의 `floor_added_to_deck`과 같은 기준) |
| `hp`, `maxHp`, `gold` | int | |
| `deck` | `SnapshotCard[]` | `{id, upgrade, instanceId, enchantment?, floorAdded?}` |
| `deck[].enchantment` | object? | `{id, amount}` |
| `relics` | string[] | 유물 모델 ID |
| `potions` | string[] | 포션 모델 ID (빈 슬롯 제외) |
| `cardRemovalsBought` | int? | 이번 런에 상점에서 산 카드 제거 횟수. 모르면 생략 |
| `map` | object? | `{nodes, edges, current?}` |
| `map.nodes[]` | | `{coord:{col,row}, type}`. `type`은 `MapNodeTypes` (monster, elite, rest, shop, treasure, event, boss, unknown) |
| `map.edges[]` | | `[from, to]` 두 칸짜리 배열. 각 칸은 `{col,row}` |
| `map.current` | object? | 현재 노드 좌표. 막 시작 전이면 생략 |

```json
{
  "type": "run.snapshot",
  "protocol": 1,
  "seq": 2,
  "character": "CHARACTER.DEFECT",
  "ascension": 3,
  "act": 2,
  "floor": 23,
  "hp": 41,
  "maxHp": 75,
  "gold": 187,
  "deck": [
    { "id": "CARD.STRIKE_DEFECT", "upgrade": 0, "instanceId": "c1" },
    { "id": "CARD.BALL_LIGHTNING", "upgrade": 1, "instanceId": "c2",
      "enchantment": { "id": "ENCHANTMENT.SHARP", "amount": 2 }, "floorAdded": 4 }
  ],
  "relics": ["RELIC.CRACKED_CORE"],
  "potions": ["POTION.FIRE_POTION"],
  "cardRemovalsBought": 1,
  "map": {
    "nodes": [
      { "coord": { "col": 0, "row": 0 }, "type": "monster" },
      { "coord": { "col": 1, "row": 1 }, "type": "elite" }
    ],
    "edges": [ [ { "col": 0, "row": 0 }, { "col": 1, "row": 1 } ] ],
    "current": { "col": 0, "row": 0 }
  }
}
```

앱에서는 `SnapshotMapper.ToRunState(snapshot, hello)`로 Core의 `RunState`로 바꿉니다 (`isMultiplayer`는 `hello`에서 가져옴).

### 3.3 `screen.opened`

카드를 고르는 화면(또는 맵)이 열렸을 때 보냅니다. (`ScreenOpenedMessage`)

| 필드 | 타입 | 설명 |
|---|---|---|
| `screen` | string | `ScreenKinds`: `card_reward`, `merchant`, `deck_select`, `map`, `other` |
| `context` | string? | 카드 보상의 출처. `RewardContexts`: `normal`, `elite`, `boss`, `event` (상점은 `merchant`) |
| `items` | `ScreenItem[]` | 화면 순서(왼→오, 위→아래)대로 고를 수 있는 카드 |
| `items[].instanceId` | string | 배지를 붙일 때 쓰는 ID. 이 화면 안에서 유일 |
| `items[].cardId` | string | 카드 모델 ID |
| `items[].upgrade` | int | 강화 수치 |
| `items[].price` | int? | 상점 가격 (상점에서만) |
| `items[].rect` | object? | `{x, y, w, h}` 화면 좌표 |
| `alternatives` | `ScreenAlternative[]` | 카드 외 선택지. `{kind, rect?}`, `kind`는 `skip` 등 |
| `removalPrice` | int? | 상점의 카드 제거 가격. 매진이거나 상점이 아니면 생략 |
| `viewport` | object? | `{w, h}`: `rect` 좌표계의 크기 |

카드 보상 예:

```json
{
  "type": "screen.opened",
  "protocol": 1,
  "seq": 3,
  "screen": "card_reward",
  "context": "elite",
  "items": [
    { "instanceId": "r1", "cardId": "CARD.COOLHEADED", "upgrade": 0,
      "rect": { "x": 560, "y": 300, "w": 180, "h": 250 } },
    { "instanceId": "r2", "cardId": "CARD.GLACIER", "upgrade": 1,
      "rect": { "x": 870, "y": 300, "w": 180, "h": 250 } }
  ],
  "alternatives": [ { "kind": "skip", "rect": { "x": 860, "y": 900, "w": 200, "h": 60 } } ],
  "viewport": { "w": 1920, "h": 1080 }
}
```

상점 예:

```json
{
  "type": "screen.opened",
  "protocol": 1,
  "seq": 4,
  "screen": "merchant",
  "items": [
    { "instanceId": "m1", "cardId": "CARD.DEFRAGMENT", "upgrade": 0, "price": 75 },
    { "instanceId": "m2", "cardId": "CARD.ECHO_FORM", "upgrade": 1, "price": 160 }
  ],
  "alternatives": [],
  "removalPrice": 100
}
```

앱 쪽 변환 (`SnapshotMapper`):

| 함수 | 결과 |
|---|---|
| `ToOfferContext(screen)` | `merchant` → `Shop`. `card_reward`는 `context`에 따라 `normal`→`CardReward`, `elite`→`EliteReward`, `boss`→`BossReward`, `event`→`Event` (없거나 모르는 값은 `CardReward`). 그 밖의 화면은 `null` |
| `ToCardRefs(screen)` | `items`와 같은 순서의 `CardRef` 목록 (i번째 결과 = i번째 item) |
| `ToShopOffer(screen)` | `ShopOffer` (가격이 없는 item은 제외) + `removalPrice` |

### 3.4 `screen.closed`

마지막으로 알린 화면이 닫혔습니다. 앱은 실시간 추천 표시를 정리합니다. 모드는 그 화면에 붙인 배지를 스스로 지웁니다. (`ScreenClosedMessage`)

```json
{ "type": "screen.closed", "protocol": 1, "seq": 5, "screen": "card_reward" }
```

### 3.5 `run.event`

런에서 일어난 일입니다. `event` 값에 따라 채워지는 필드가 다릅니다. 이벤트 뒤에는 보통 새 `run.snapshot`이 따라옵니다. (`RunEventMessage`)

| `event` (`RunEventKinds`) | 채워지는 필드 |
|---|---|
| `card_obtained` | `card` `{id, upgrade, instanceId?}`, `act?`, `floor?` |
| `card_removed` | `card`, `act?`, `floor?` |
| `card_upgraded` | `card` (강화 **후** 수치), `act?`, `floor?` |
| `room_entered` | `room` (`MapNodeTypes`), `coord?`, `act?`, `floor?` |
| `run_ended` | `victory` (승리 true, 패배·포기 false) |

```json
{ "type": "run.event", "protocol": 1, "seq": 6, "event": "card_obtained",
  "card": { "id": "CARD.GLACIER", "upgrade": 1, "instanceId": "c3" }, "act": 2, "floor": 23 }
```

```json
{ "type": "run.event", "protocol": 1, "seq": 7, "event": "room_entered",
  "room": "shop", "coord": { "col": 3, "row": 7 }, "act": 2, "floor": 24 }
```

```json
{ "type": "run.event", "protocol": 1, "seq": 8, "event": "run_ended", "victory": false }
```

---

## 4. 앱 → 모드

### 4.1 `badges.set`

현재 열린 화면의 카드에 그릴 배지입니다. 받을 때마다 **이전 배지를 모두 바꿉니다**. (`BadgesSetMessage`)

| 필드 | 타입 | 설명 |
|---|---|---|
| `badges[].instanceId` | string | `screen.opened`의 `items[].instanceId` |
| `badges[].label` | string | 티어 문자 (`S`, `A`, `B`, `C`, `D`) |
| `badges[].color` | string | `#RRGGBB` |
| `badges[].score` | double | 점수 (표시용) |
| `badges[].isBest` | bool | 추천 1위 (왕관 표시) |
| `badges[].chips` | string[] | 이유 칩 (한국어, 중요한 순) |
| `skipRecommended` | bool? | true면 스킵 버튼 옆에 추천 표시 |
| `screenSeq` | long? | 이 배지가 답하는 `screen.opened`의 `seq`. 모드는 이미 닫힌 화면에 대한 배지면 버립니다 |

티어 색 (`TierColors`, PLAN §8): S 금색 `#F1C40F`, A 보라 `#9B59B6`, B 파랑 `#3498DB`, C 회색 `#95A5A6`, D 어두운 빨강 `#8B1E1E`. 앱이 꺼져 있을 때 모드가 기본 티어 배지를 그릴 때도 같은 색을 씁니다.

```json
{
  "type": "badges.set",
  "protocol": 1,
  "seq": 1,
  "badges": [
    { "instanceId": "r1", "label": "S", "color": "#F1C40F", "score": 91.5, "isBest": true,
      "chips": ["광역 부족", "빌드 핵심"] },
    { "instanceId": "r2", "label": "C", "color": "#95A5A6", "score": 40, "isBest": false, "chips": [] }
  ],
  "skipRecommended": false,
  "screenSeq": 3
}
```

앱에서는 `SnapshotMapper.BuildBadges(items, skipRecommended, seq)`로 만듭니다. 색은 티어 문자로 자동 결정됩니다. `screenSeq`는 `with { ScreenSeq = opened.Seq }`로 붙입니다.

### 4.2 `badges.clear`

앱이 그리게 한 배지를 모두 지웁니다. (`BadgesClearMessage`, 추가 필드 없음)

```json
{ "type": "badges.clear", "protocol": 1, "seq": 2 }
```

---

## 5. 구현 메모

- **모드는 관찰만 합니다.** 메시지를 보내려고 게임 상태를 바꾸지 않습니다 (Harmony Postfix + 읽기). 직렬화와 전송은 게임 메인 스레드 밖에서 합니다.
- **DTO는 `set` 접근자를 씁니다.** net9 System.Text.Json 소스 생성기는 JSON에 없는 `init` 전용 속성에 `default`를 넣어서 `protocol = 1`, `deck = []` 같은 초기값을 지워 버립니다. 그래서 `init` 대신 `set`을 쓰고, 만든 뒤에는 바꾸지 않는 것을 규칙으로 합니다 (`with`로 복사).
- 받는 쪽은 `BridgeJson.Deserialize` 결과가 `null`이면 조용히 버리고(디버그 로그만), 연결은 끊지 않습니다.
- 한 메시지의 크기는 보통 수 KB입니다. 맵이 들어간 `run.snapshot`도 64 KB를 넘지 않게 유지합니다.
