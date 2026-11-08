# 이벤트 예측 픽스처 (실제 싱글 세이브)

- 출처: 사용자 PC `current_run.save` 스냅샷 (2026-11-08~09, v0.107.1, 싱글 8판). 플레이 중 저장될 때마다 복사해 둔 파일들.
- 개인정보: 싱글 세이브는 `net_id`가 1이고 Steam ID·경로가 없어 내용은 그대로 둔다. 공백만 없앤 JSON을 gzip으로 저장.
- `room_entry/`: "?" 방에 들어설 때 쓰인 세이브(이벤트를 뽑기 직전) 25개. `truth.txt`는 그 방에서 실제로 나온 이벤트
  (같은 판의 가장 늦은 세이브 `map_point_history` 기록). `EventPrediction.Predict`가 전부 맞혀야 한다.
- `runs/`: 판마다 가장 늦은 세이브. 기록을 처음부터 다시 돌려 모든 이벤트 방의 뽑기를 재현한다.

## 다시 만들기

1. 플레이 중 세이브 스냅샷을 모은다 (`current_run.save`가 바뀔 때마다 복사).
2. `SNAP_DIRS=<스냅샷 폴더> EXPORT_DIR=<출력 폴더> dotnet test tests/Sts2Overlay.GameState.Tests --filter EventSnapshotScanTests`
3. 출력 폴더의 `*.save`, `truth.txt`, `latest/*.save`를 gzip(공백 없는 JSON)으로 이 폴더에 넣는다. 넣기 전에
   `players[].net_id`가 `[1]`인지, `7656…` Steam ID나 경로가 없는지 확인한다.
