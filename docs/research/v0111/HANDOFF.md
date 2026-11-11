# v0.111.0 후속 작업: 인수인계 (브랜치 claude/v0111-followup)

아직 열려 있는 것만 적는다. 끝난 작업은 `CHANGES.md`, `LAYOUT_CHECK.md`, `REWARD_REPLAY.md`와 커밋 메시지에 있다.

## 이번 세션 항목 (2026-11-12 브리프, 1~5)

1. Vision 레이아웃 점검: **완료**. `LAYOUT_CHECK.md`: 상수 46개 중 같음 42, 바뀜 0, 실제 v0.111 화면 없이는 확인 불가 4
   (이벤트 버튼 텍스처 여백/색, 고대 텍스트 색, 지도 보이는 띠/상단 띠, 가격 글리프 모양과 상점 깔개 색). Vision·지도 코드는 바꾸지 않았다.
   남은 일: 실제 v0.111 캡처로 확인 불가 4개를 확인.
2. 두 팩 테스트: **완료**. PackTextTests, EventCatalogTests(버전별 공유 이벤트 표시 추가), TurnSimulatorTests의 실제 팩 부분(`CombatPackTests`),
   DummyHistoryTests를 버전마다 돌린다. v0.111.0에서 모두 통과, 팩 데이터 수정은 필요 없었다.
   팩별 Core 테스트: v0.107.1 37, v0.111.0 37, sample 3 (+ 버전 무관 TurnSimulatorTests 24, v0.107.1 데이터).
3. 협동 공유 이벤트: **Xx64 완료**. `GameDataVersion.IsSharedEvent`가 v0.111.0 `IsShared` 이벤트 8개를 가지고, `GameStateTracker.EventRngSeed`는
   이들에 슬롯을 더하지 않는다(지금 재현까지 가는 것은 Morphic Grove GROUP의 한 장 변환뿐). Legacy32는 그대로(항상 슬롯을 더함):
   v0.107.1도 공유 이벤트가 6개였지만 디컴파일이 없어 `BeginEvent` 시드 규칙을 모른다.
   남은 일: 공유 이벤트 변환이 있는 v0.107.1 협동 로그가 생기면 확인.
4. RewardCorpusTests: **완료**. 표는 `REWARD_REPLAY.md`. 일치/시도 v0.111.0 3891/3901 → 5369/5375(일치한 보상은 업그레이드 표시도 전부 맞음),
   v0.107.1 2882/2894 → 3978/3987. 남은 일: 설명 못 한 카드 불일치 6 + 9건, v0.107.1 포션 2건, v0.107.1 1막 보상 9건의 전부 업그레이드
   (v0.107.1 디컴파일 필요). 실제 오버레이의 `GameStateTracker.RewardUpgrade`는 LAVA_LAMP를 반영하지 않는다(전투 피해 정보가 필요).
5. tools/ 기본값: **완료**. 버전이 섞이던 기본값을 데이터 버전을 따르거나 인자로 받게 했다.
   - `DataPackLoader.DefaultCatalogRelativePath` 삭제 → `CatalogRelativePath(version)`, `PackRelativePath(version)`(버전을 반드시 지정).
   - `DataVersionSelector.ForLocalResources(root, local)`: 앱과 같은 규칙으로 `data/local/manifest.json`의 게임 버전에 맞는 데이터 버전.
   - MemProbe, TitleBench, VisionBench: `--data-version`, 기본값은 위 규칙(팩·커밋된 카탈로그·티어·유물 표가 카드 그림과 같은 버전).
   - MapBench: `--pack`이 없으면 세이브의 시드 방식(`GameDataVersion.For`)에 맞는 팩.
   - CatalogBuilder: release_info.json이 없으면 v0.107.1로 가정하지 않고 `--game-version`을 요구.
   - RunHistoryAnalyzer: 카탈로그 기본값이 `--pack`과 같은 버전(전에는 가장 새 카탈로그).
   - UpgradeCalib/fit.py: `--data-version`(기본 v0.107.1, 적합이 v0.107.1 기준). CardCalib/pipeline.py: 카탈로그를 `--base` 팩 버전에서(또는 `--data-version`).
   - 바꾸지 않음: DeckSim, PathStats(이미 `--pack` 필수, 카탈로그는 팩 옆), PoolDataGen(모든 경로가 인자), CardCalib의 나머지 스크립트(버전 기본값 없음).
   - 남은 일: 도구들은 실제 data/local로 실행해 보지 않았다(빌드만).

## 이전부터 열린 항목

- 팩 선택: v0.109/v0.110 게임은 v0.107.1 팩을 받는데 GameState는 v0.111 표를 쓴다. 팩 규칙을 세대 기준으로 할지 결정 필요. 선택된 팩을 출력하는 PrepareCommand 줄이 없다.
- 오버레이 실행 중 게임 버전을 다시 감지하지 않는다(다음 시작 때 반영).
- EventPrediction을 실제 v0.111 세이브로 확인하지 않았다.
- DeckEffects 빈칸(두 버전): REFLECTIONS SHATTER의 덱 복제, Reflections TOUCH_A_MIRROR, Wongo LEAVE 강등, BUGSLAYER, TINKER_TIME MAD_SCIENCE,
  CLAWS→MAUL, NUTRITIOUS_SOUP, DUSTY_TOME, BYRDPIP; Beautiful Bracelet의 무작위 인챈트(표에 필드 없음).
- 로그만 있는 싱글 플레이 덱: Neow 캡슐, Aroma 변환 결과, Wood Carvings, Amalgamator, Pael's Claw, Symbiote는 로그에 남지 않는다.
  다음 단계 후보: 세이브가 있으면 EventPrediction이 이벤트를 알려 주고, 변환 화면 힌트로 Aroma의 LET_GO를 재현(`TransformRngCall` 0).
- 패키징: v0.111.0 팩 + 카탈로그로 디스크 약 1.6 MB 증가, 패키징은 그대로.

## 빌드 / 테스트 상태

- 이전 세션 전체 실행(`2559692`): GameState 312 통과 / 3 건너뜀; Core 1412, App 530, Resources 88(그 전 커밋).
- 이번 세션 커밋마다 해당 프로젝트를 빌드하고 좁은 필터로 테스트했다(각 커밋 메시지 참조). 세션 끝 전체 실행은 아래.
