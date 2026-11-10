#!/bin/sh
# Usage: ./run.sh <fights.jsonl> <out_prefix> [extra SimFuzz args...]
# Runs every simulatable v0.111 fight in the file as $PROCS processes x $THREADS threads (default 4 x 6),
# merges to <out_prefix>.csv. Use fewer while the game is running on this PC.
# Progress: every 15 s one "progress done/total (pct) elapsed .. eta HH:mm:ss (.. left)" line, also in
# <out_prefix>.progress, summed over the shards (each shard's .log has its own lines every 10 s).
cd "$(dirname "$0")"
in=$1; outp=$2; shift 2
PROCS=${PROCS:-4}; THREADS=${THREADS:-6}; BIN=${BIN:-bin/Release/net9.0/SimFuzz.dll}
start=$(date +%s)
i=0
while [ $i -lt $PROCS ]; do
  dotnet "$BIN" "$in" "$outp.s$i.csv" --act1cap 1000000 --totalcap 1000000 \
    --threads $THREADS --shard $i/$PROCS --report "$outp.s$i.report.txt" "$@" > "$outp.s$i.log" 2>&1 &
  pids="$pids $!"
  i=$((i+1))
done
progress() {
  # Same line format as one SimFuzz process, summed over the shards' "progress d/t" files.
  cat "$outp".s*.csv.progress 2>/dev/null | awk -v now="$(date +%s)" -v start="$start" '
    function fmt(s) { return s >= 3600 ? sprintf("%dh%02dm", s / 3600, (s % 3600) / 60) : sprintf("%dm%02ds", s / 60, s % 60) }
    { split($2, a, "/"); d += a[1]; t += a[2] }
    END {
      el = now - start; eta = "eta ?"
      if (d > 0 && d < t) { left = int(el * (t - d) / d); cmd = "date -d @" (now + left) " +%H:%M:%S"; cmd | getline clock; close(cmd); eta = "eta " clock " (" fmt(left) " left)" }
      else if (t > 0 && d >= t) eta = "finished"
      printf "progress %d/%d (%.1f%%) elapsed %s %s\n", d, t, (t > 0 ? 100 * d / t : 0), fmt(el), eta
    }' | tee "$outp.progress"
}
while :; do
  alive=0
  for p in $pids; do kill -0 "$p" 2>/dev/null && alive=1; done
  [ $alive -eq 0 ] && break
  sleep 15
  progress
done
wait
progress
rm -f "$outp".s*.csv.progress
end=$(date +%s)
head -1 "$outp.s0.csv" > "$outp.csv"
i=0
while [ $i -lt $PROCS ]; do tail -n +2 "$outp.s$i.csv" >> "$outp.csv"; rm -f "$outp.s$i.csv"; i=$((i+1)); done
grep -h "combats/s" "$outp".s*.log | sed 's/.*combats\/s=\([0-9.]*\).*/\1/' | awk '{s+=$1} END {print "combats/s (sum of shards) =", s}'
echo "wall_seconds=$((end-start))"
