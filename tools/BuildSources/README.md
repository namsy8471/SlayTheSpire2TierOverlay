# BuildSources

랭커 승리 덱에서 빌드별 카드 통계를 내는 스크립트입니다. 결과는 `docs/research/build-sources/`의 근거 표가 되고, 사람이 읽고 `data/packs/<버전>/archetypes/*.json`을 고칩니다(자동으로 팩을 고치지 않습니다).

입력은 저장소에 없는 개인 수집 자료입니다: `runs_index.csv`(런 번호, 캐릭터, 승패, 게임 버전, 익명 플레이어 키, 우수 플레이어 여부)와 런 파일 폴더. **런 파일·런 번호·출력 JSON은 커밋하지 않습니다.** 문서에는 합계만 옮깁니다.

```bash
python tools/BuildSources/build_stats.py <runs_index.csv> <runs 폴더> data/packs/v0.111.0/archetypes/ironclad.json data/catalog/cards.v0.111.0.json tools/BuildSources/anchors-ironclad.json IRONCLAD v0.111 <출력.json>
```

```bash
python tools/BuildSources/calib.py <runs_index.csv> <runs 폴더> data/packs/v0.111.0/archetypes/ironclad.json tools/BuildSources/anchors-ironclad.json IRONCLAD v0.111
```

- `build_stats.py`: 빌드마다 멤버 덱 수, 카드별 플레이어 균형 점유율·lift·보유 플레이어 수·빌드 안 픽률, 부트스트랩(플레이어 재표본 200회)에서 지원·core 기준을 넘은 비율. 아이언클래드 192판에 약 2분 30초(스레드 1개).
- `calib.py`: 현재 역할 목록으로 우수 플레이어 멤버 덱의 commit 중앙값(fullWeight 후보별)과 enabler·payoff 보유 수 중앙값(essentialTarget).
- `anchors-<캐릭터>.json`: 빌드별 축 카드. 멤버 덱 = 축 카드 `min`장(기본 2) 이상.

규칙은 `docs/research/build-sources/ironclad.md` 2절과 `docs/research/build-sources/v0111/ironclad.md` 2절에 있습니다.
