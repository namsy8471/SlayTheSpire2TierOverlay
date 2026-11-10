#!/bin/sh
# Usage: ./run.sh <fights.jsonl> <out_prefix> [extra SimFuzz args...]
# Runs every simulatable v0.111 fight in the file as $PROCS processes x $THREADS threads (default 4 x 6),
# merges to <out_prefix>.csv. Use fewer while the game is running on this PC.
# Progress: every 15 s one "progress done/total (pct) elapsed .. eta HH:mm:ss (.. left)" line for all
# processes together, also in <out_prefix>.progress (each process's .log has the same every 10 s).
cd "$(dirname "$0")"
in=$1; outp=$2; shift 2
PROCS=${PROCS:-4}; THREADS=${THREADS:-6}; BIN=${BIN:-bin/Release/net9.0/SimFuzz.dll}
start=$(date +%s)
# The processes share the whole sample through a claim directory: each takes the next fight no
# other process has taken (heaviest first), so they all finish together instead of the slowest
# fixed shard setting the wall time. Rows land in $claims/<index>.row and are merged in index order.
claims="$outp.claims"; rm -rf "$claims"; mkdir -p "$claims"
pids=""
i=0
while [ $i -lt $PROCS ]; do
  dotnet "$BIN" "$in" "$outp.s$i.csv" --act1cap 1000000 --totalcap 1000000 \
    --threads $THREADS --claim "$claims" --report "$outp.s$i.report.txt" "$@" > "$outp.s$i.log" 2>&1 &
  pids="$pids $!"
  i=$((i+1))
done
# Every process reports the same shared count; show the most advanced line.
progress() {
  cat "$outp".s*.csv.progress 2>/dev/null | awk '{split($2, a, "/"); if (a[1] >= best) { best = a[1]; line = $0 }} END { if (line != "") print line }' | tee "$outp.progress"
}
n=0
while :; do
  alive=0
  for p in $pids; do kill -0 "$p" 2>/dev/null && alive=1; done
  [ $alive -eq 0 ] && break
  sleep 1
  n=$((n+1))
  [ $((n % 15)) -eq 0 ] && progress
done
wait
end=$(date +%s)
head -1 "$outp.s0.csv" > "$outp.csv"
total=$(ls "$claims" | wc -l)
i=0
while [ $i -lt $total ]; do cat "$claims/$i.row" >> "$outp.csv"; i=$((i+1)); done
empty=$(find "$claims" -name '*.row' -size 0 | wc -l)
[ "$empty" -gt 0 ] && echo "WARNING: $empty claimed fights have no row (a process failed); see $outp.s*.log"
rm -rf "$claims" "$outp".s*.csv "$outp".s*.csv.progress
grep -h "combats/s" "$outp".s*.log | sed 's/.*combats\/s=\([0-9.]*\).*/\1/' | awk '{s+=$1} END {print "combats/s (sum of shards) =", s}'
echo "wall_seconds=$((end-start))"
