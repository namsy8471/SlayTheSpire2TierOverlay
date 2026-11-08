# M0 기술 검증 스파이크 결과

> 기준: 게임 **v0.107.1** (commit 59260271) · 작성: 2026-11-08 · 브랜치 `m0-spike`
> 표기: **[코드 확인]** = 디컴파일한 `sts2.dll`에서 직접 확인 · **[실행 확인]** = 게임을 실제로 돌려 확인 · **[대기]** = 실행 테스트 필요

디컴파일: `ilspycmd -p -o decompiled/sts2 -r <data 폴더> sts2.dll` (3,424개 파일). `decompiled/`는 저작권 때문에 `.gitignore` 처리했습니다.
아래 `파일:줄`은 모두 `decompiled/sts2/` 기준입니다.

---

## 요약 (PLAN §12 M0의 7개 체크)

| # | 체크 | 결과 | 상태 |
|---|---|---|---|
| 1 | `sts2.dll` 분석 | 모드 로더, 보상·상점·덱 화면, 세이브, 협동 동기화, 카탈로그 API 모두 확인 | ✅ 코드 확인 |
| 2 | 최소 모드 로딩 | 빌드 성공, `mods/Sts2Overlay.Bridge/`에 배포. 게임 첫 실행 시 **모드 허용 팝업 → 게임 재시작** 필요 | ⏳ 실행 대기 |
| 3 | 보상 화면 3장 ID 로그 | `NCardRewardSelectionScreen.RefreshOptions` Postfix 작성 | ⏳ 실행 대기 |
| 4 | 카드에 배지 | `NGridCardHolder`에 `PanelContainer+Label` 자식 추가, 풀 반환 시 제거, F8 토글 | ⏳ 실행 대기 |
| 5 | WPF Topmost 창 | `tools/OverlaySpike` 빌드 완료 | ⏳ 실행 대기 |
| 6 | modded 세이브·업적 | **modded는 빈 프로필로 시작** (복사 없음). 업적은 이 버전에서 원래 꺼져 있음 | ✅ 코드 확인 |
| 7 | 협동 (나만 모드) | `affects_gameplay:false`면 **양방향 입장 가능**. 동기화 해시는 모델 상태만 봄 | ✅ 코드 확인 / ⏳ 친구와 실행 대기 |

**가장 중요한 발견 두 가지**
1. **협동은 코드상 문제없습니다.** 입장 시 비교하는 건 `affects_gameplay: true` 모드 목록뿐이고, 상대에게 "모드 사용 중"이 표시되지도 않습니다.
2. **모드를 켜면 진행도가 없는 새 프로필로 시작합니다.** 해금·런 기록·진행 중인 런이 `modded/` 폴더에 따로 쌓이고, v0.107.1에는 기존 세이브를 복사하는 코드가 없습니다. (PLAN §3.2의 "v0.108부터 복사"는 아직 이 버전에 없음)

---

## 1. 공식 모드 로더 [코드 확인]

`MegaCrit.Sts2.Core.Modding/ModManager.cs`

- **읽는 위치** (`:88-99`): 실행 파일 옆 `mods/`, `mods_STEAMTEST/`, Steam 창작마당. 폴더를 재귀로 훑어 **모든 `.json`을 매니페스트로** 읽습니다 (`:332-358`).
  → 모드 폴더에 다른 JSON(설정 파일 등)을 두면 안 됩니다. 매니페스트로 오인됩니다.
- **DLL 이름 규칙** (`:709`): `<매니페스트 id>.dll`. 그래서 `AssemblyName`과 `id`를 `Sts2Overlay.Bridge`로 맞췄습니다.
- **진입점** (`:752-777`): `[ModInitializer("메서드명")]`이 붙은 클래스의 **static** 메서드를 호출합니다. 없으면 `Harmony.PatchAll`.
- **로드 시점**: `OneTimeInitialization.ExecuteVeryEarly` (`Helpers/OneTimeInitialization.cs:59`). **ModelDb보다 먼저**라서 초기화 메서드에서 카드 목록을 읽으면 안 됩니다 (아래 §5).
- **허용 조건** (`:588-591`): `PlayerAgreedToModLoading`이 false면 모든 모드가 Disabled.
  - 처음엔 메인 메뉴에서 `NConfirmModLoadingPopup`이 뜨고 (`Nodes.Screens.MainMenu/NMainMenu.cs:483-486`), "모드 불러오기"를 누르면 **설정 저장 후 게임이 종료**됩니다 (`NConfirmModLoadingPopup.cs:100-111`). 다시 켜야 로드됩니다.
  - 현재 사용자 `settings.save`의 `mod_settings`는 `null` (아직 동의 전).
  - `-nomods` 실행 인자로 모드 없이 켤 수 있습니다 (`:75-79`).
- **버전 검사**: `min_game_version`과 게임 버전을 SemanticVersion으로 비교. 앞의 `v`는 무시됩니다 (`Debug/SemanticVersion.cs:66`).
- **어셈블리 버전 관용** (`:892-905`): 모드가 빌드된 `sts2`/`0Harmony` 버전이 달라도 현재 로드된 것으로 연결해 줍니다. 패치 후에도 **API가 안 바뀌었으면 재빌드 없이** 동작할 가능성이 큽니다.

참조 DLL 버전: `sts2 0.1.0.0`, `GodotSharp 4.5.1.0`, `0Harmony 2.4.2.0`.

## 2. 보상 화면 후킹과 배지 [코드 확인]

| 대상 | 메서드 | 용도 |
|---|---|---|
| `NCardRewardSelectionScreen` | `public void RefreshOptions(IReadOnlyList<CardCreationResult>, IReadOnlyList<CardRewardAlternative>)` (`:265`) | `_Ready`와 리롤 때 호출. 끝나면 `_cardRow`에 새 `NGridCardHolder`들이 이미 붙어 있음 → Postfix에서 배지 |
| `NGridCardHolder` | `OnFreedToPool()` / `OnReturnedFromPool()` (`:286`, `:266`) | **홀더가 풀(pool)에서 재사용**되므로 배지를 지워 덱 보기 등에 새지 않게 함 |
| `NMerchantCard` | `protected UpdateVisual()` (`Nodes.Screens.Shops/NMerchantCard.cs:135`) | 상점 카드 ID·가격·세일 여부 (스파이크는 로그만) |
| `NGame` | `_Input(InputEvent)` (`Nodes/NGame.cs:1159`) | F8 배지 토글. F8은 게임이 안 씀 (`NInputManager.cs:177-210`) |

- **카드 정보**: `NGridCardHolder.CardModel` (강화 미리보기 중에도 원본 카드), `card.Id.ToString()` → `"CARD.STRIKE_DEFECT"`, `CurrentUpgradeLevel`.
- **좌표**: 홀더 원점은 카드 **중심**, 카드 크기 300×422. 배지는 `Hitbox` 오른쪽 위에 둡니다. 홀더의 위치·크기 트윈을 자식 노드가 자동으로 따라갑니다.
- **주의**
  - 배지는 `NCard`가 아니라 홀더에 붙입니다. `NCard`는 고른 뒤 덱으로 날아가고, `Model` setter에는 부작용(`MarkCardAsSeen`)이 있습니다.
  - 모드 DLL은 Godot 스크립트 등록 없이 로드되므로 **커스텀 Node 서브클래스 대신 내장 `PanelContainer`/`Label`만** 씁니다. 입력도 같은 이유로 `NGame._Input`에 Postfix.
  - `MouseFilter = Ignore`로 카드 클릭을 막지 않습니다.
- **협동**: 보상 화면은 로컬 플레이어에게만 뜹니다 (`CardReward.cs:190-193`).
- 개발사 자동 플레이 봇의 화면 처리: `AutoSlay.Handlers.Screens/CardRewardScreenHandler.cs:22-38`.

덱 보기(`NDeckViewScreen` → `NCardGrid`)는 가상화되어 `NGridCardHolder.ReassignToCard`로 홀더를 재사용합니다. M3에서 `OnCardReassigned` Postfix로 배지를 갱신합니다.

## 3. modded 세이브 · 업적 · 통계 [코드 확인]

- **세이브 경로** (`UserDataPathProvider.cs:43`): `IsRunningModded`면 프로필 경로 앞에 `modded/`가 붙습니다.
  → `%APPDATA%\SlayTheSpire2\steam\<SteamID>\modded\profile1\saves\` (user:// 매핑은 추정)
  - 이동하는 것: `progress.save`(해금), `prefs.save`, `current_run.save`, `current_run_mp.save`, 런 기록, 리플레이
  - 이동 안 하는 것: `settings.save` (계정 공용 → 모드 허용 여부도 공용)
  - **복사·이전 코드 없음.** 처음 모드로 켜면 빈 프로필. 바닐라 세이브는 그대로 남고, 모드 없이 켜면 돌아옵니다.
- **"모드 실행 중" 판정** (`ModManager.cs:915-922`): Loaded 또는 **Failed** 상태 모드가 하나라도 있으면 true. `affects_gameplay`와 무관합니다.
- **업적**: 이 빌드는 **모두에게 꺼져 있습니다** (`Platform/AchievementsUtil.cs:30-32` 빈 메서드, `NullAchievementStrategy`). 모드 때문에 잃는 업적은 현재 없습니다.
- **통계 업로드**: 모드 시 건너뜀 (`MetricUtilities.cs:99-102`). Sentry 크래시 리포트도 차단 (`SentryService.cs:619, 635`).
- 모드와 무관하게 동작: Steam 스탯 `architect_damage`, 일일 도전 리더보드.
- 게임 화면에 "MODDED (n)" 라벨이 **로컬에만** 표시됩니다 (`Nodes.Debug/NDebugInfoLabelManager.cs:148-203`).

**사용자 백업**: 모드 설치 전에 `%APPDATA%\SlayTheSpire2\steam`을 `Documents\<save-backup>`에 복사해 두었습니다 (66개 파일).

## 4. 협동 (나만 모드) [코드 확인, 실행은 친구 필요]

- **입장 검사** (`Multiplayer.Game/JoinFlow.cs`): 들어가는 쪽(클라이언트)만 비교합니다. 호스트는 클라이언트 모드 목록을 받지 않습니다.
  - `:94-107` gameplay 모드 목록이 다르면 `ModMismatch`로 실패
  - `:108-111` `idDatabaseHash`(ModelDb ID 해시)가 다르면 실패
  - `:113-120` non-gameplay 목록 차이는 **경고 로그만**
- `affects_gameplay:false` 모드만 있으면 gameplay 목록이 빈 목록 = 바닐라와 같음 → **내가 호스트든 손님이든 입장 가능**.
- **반드시 지킬 것**
  - 모드 DLL에 `AbstractModel` 서브클래스(카드·유물·파워 등)를 **절대 정의하지 않기**. ID 해시에 들어가 입장이 막힙니다 (`ModelIdSerializationCache.cs:61-71`).
  - **진짜 읽기 전용**: 동기화 체크섬(`ChecksumTracker.cs:166`, `NetFullCombatState.cs:369-440`)은 HP·블록·파워·에너지·별·골드·카드 더미·포션·유물·오브·**RNG 카운터** 등을 해시합니다. RNG를 굴리는 getter나 미리보기 헬퍼를 호출하면 `StateDivergence`로 강퇴됩니다. UI 노드는 해시에 포함되지 않으므로 배지는 안전합니다.

## 5. 카드 카탈로그 덤프 [코드 확인]

- **시점**: `OneTimeInitialization.ExecuteDeferred()` Postfix (메인 메뉴가 뜬 뒤, ModelDb·로컬라이즈·카드 풀 준비 완료).
- **목록**: `ModelDb.AllCards` (캐릭터 풀 + 무색·저주·상태이상·토큰·이벤트·퀘스트·폐기 풀 + 시작 덱). 캐릭터는 `ModelDb.AllCharacters` 5명.
- **이름**: `res://localization/{kor,eng}/cards.json`을 `Godot.FileAccess`로 직접 읽습니다. 키는 `STRIKE_DEFECT.title`, `STRIKE_DEFECT.description`.
  `LocManager.SetLanguage`는 UI 전체를 새로 그려서 쓰지 않았습니다.
- **비용**: `CanonicalEnergyCost`는 protected라 `EnergyCost.Canonical`/`.CostsX` 사용.
- **출력**: `%LOCALAPPDATA%\Sts2Overlay\catalog.v0.107.1.json` (게임 텍스트 포함 → 레포에 커밋 안 함). 버전별로 한 번만 생성.

## 6. 추천 엔진용 수치 [코드 확인]

**카드 희귀도** (`Odds/CardRarityOdds.cs`, 괄호는 승천 7 "희소")

| 출처 | 희귀 | 고급 | 일반 |
|---|---|---|---|
| 일반 전투 | 3% (1.49%) | 37% | 나머지 |
| 엘리트 | 10% (5%) | 40% | 나머지 |
| 보스 | 100% | 0 | 0 |
| 상점 | 9% (4.5%) | 37% | 나머지 |

- **희귀 보정치(pity)**: −5%에서 시작, 희귀가 안 나올 때마다 카드 1장당 +1% (A7 +0.5%), 상한 40%, 희귀가 나오면 −5%로 초기화. 즉 **1막 첫 전투의 희귀 확률은 0%**.
  - 전투 보상(일반·엘리트·보스)만 보정치를 쓰고 바꿉니다. 상점은 현재 보정치로 굴리되 바꾸지 않습니다 (`CardFactory.cs:50, 244-260`).
- **보상 카드 강화 확률**: 희귀가 아닌 카드에 `막 번호 × 25%` (A7 12.5%). 상점 카드는 강화 안 됨 (`CardFactory.cs:23, 288-305`).
- **상점** (`Entities.Merchant/`)
  - 가격: 일반 50, 고급 75, 희귀 150. 무색 ×1.15. 그 뒤 ×(0.95~1.05) 랜덤. 세일 카드 1장은 절반 (`MerchantCardEntry.cs:38-50, 119-129`).
  - 구성: 캐릭터 카드 5장 (공격 2, 스킬 2, 파워 1) + 무색 2장 (고급 1, 희귀 1) (`MerchantInventory.cs:17-27`).
  - 카드 제거: `75 + 25 × 제거 횟수` (승천 6 이상 `100 + 50 ×`) (`MerchantCardRemovalEntry.cs:20-32`). PLAN §7.3과 일치.
- **게임 버전 읽기**: `ReleaseInfoManager.Instance.ReleaseInfo?.Version`.

---

## 7. 실행 테스트 방법 (사용자 PC)

모드는 이미 `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\Sts2Overlay.Bridge\`에 배포되어 있습니다.
다시 빌드·배포하려면:

```powershell
dotnet build src\Sts2Overlay.Bridge -c Release -p:DeployMod=true
```

1. 게임 실행 → 메인 메뉴의 **모드 허용 팝업에서 "모드 불러오기"** → 게임이 꺼지면 다시 실행
2. 메인 메뉴 구석에 "MODDED (1)" 표시 확인 → `%LOCALAPPDATA%\Sts2Overlay\bridge.log`에 `Initialize`, `Patched ...`, `Catalog dumped: N cards` 확인
3. 새 런 시작 (modded 프로필이라 해금 상태가 초기화돼 있음) → 첫 전투 승리 → 카드 보상 화면에서 **카드 오른쪽 위 배지** 확인 (스파이크는 희귀도 기준 임시 등급: 희귀 S, 고급 A, 일반 B)
4. **F8**로 배지 숨기기/보이기
5. 카드 선택 뒤 덱 보기에서 배지가 **남아 있지 않은지** 확인 (풀 재사용 누수 체크)
6. 오버레이 창: `tools\OverlaySpike\bin\Release\net10.0-windows\OverlaySpike.exe` 실행 → 게임 전체화면에서 오른쪽 위 패널이 보이는지, 패널을 클릭해도 게임이 최소화되지 않는지, **Ctrl+F7** 클릭 통과 토글. 기록은 `%LOCALAPPDATA%\Sts2Overlay\overlay-spike.log`. 종료는 Ctrl+Shift+Q.
7. 협동: 친구 방에 입장 (친구는 바닐라) → 한 층 이상 진행하며 끊김(StateDivergence) 없는지. 반대로 내가 방을 열고 친구 입장도 확인.
8. 모드를 끄려면 `mods\Sts2Overlay.Bridge` 폴더를 지우거나, 게임 설정 → 모딩에서 끄거나, 실행 옵션에 `-nomods`.

## 8. 실행 결과 기록 [대기]

| 항목 | 결과 | 메모 |
|---|---|---|
| 모드 로드 | | |
| 카탈로그 덤프 카드 수 | | |
| 보상 화면 ID 로그 | | |
| 배지 위치·애니메이션 | | |
| F8 토글 | | |
| 덱 보기 배지 누수 없음 | | |
| 상점 로그 | | |
| WPF 패널 (전체화면) | | |
| WPF 클릭 통과 / 포커스 | | |
| 협동: 친구 방 입장 | | |
| 협동: 내 방에 친구 입장 | | |

## 9. PLAN 반영 사항

- §3.2 "v0.108부터 세이브 복사"는 v0.107.1에 없음 → modded 프로필이 빈 상태로 시작한다는 점을 설치 안내(M8)에 넣습니다. 원하면 바닐라 `profile1`을 `modded\profile1`로 복사하는 옵션을 앱에 둘 수 있습니다.
- §11 "업적 비활성 가능성" → 현재 버전은 업적 자체가 비활성이라 해당 없음.
- §11 협동 리스크 → 코드상 해소. 남은 위험은 "실수로 상태를 바꾸는 호출"이라 Bridge 코드 리뷰 규칙으로 관리 (AbstractModel 정의 금지, RNG getter 금지).
- §7.5 완성 확률 → 희귀도 확률과 pity 수치 확보, v1 근사식에 바로 사용 가능.
